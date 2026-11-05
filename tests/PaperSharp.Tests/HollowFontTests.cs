using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PaperSharp.Pdf;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace PaperSharp.Tests;

/// <summary>
/// PDF 字体「空壳 + 换流」方案（<see cref="PdfGlyphUsage.UseHollowFaces"/>）的保真门。
/// 方案本身：Skia 画字时用无轮廓空壳 face（省掉它对每个新字形的光栅度量与整张字体的 deflate），写完后由
/// <see cref="PdfFontSwap"/> 把空壳流换成带轮廓的子集字体。判据只有一条——**和关掉空壳方案画出来的东西一样**：
/// 页面内容流的落笔序列相同、PDFium 栅格逐像素相同；嵌进去的字体是带轮廓的。
/// </summary>
public class HollowFontTests
{
    static readonly string FontDir = Path.Combine(Fixture.AssetsDir, "fonts");

    static byte[] Render(string html, string css, bool hollow)
    {
        using var _ = PdfGlyphUsage.HollowScope(hollow);
        return Renderer.RenderPdf(html, css, baseDir: Fixture.AssetsDir);
    }

    static byte[] RasterPage1(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var bmp = Conversion.ToImage(ms, 0);
        return bmp.Bytes;
    }

    /// <summary>每个 FontFile2 流解压后的字体字节（Skia 写的版式：&lt;&lt;/Length1 N /Filter /FlateDecode /Length M&gt;&gt; stream）。</summary>
    static List<byte[]> EmbeddedFonts(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        var list = new List<byte[]>();
        foreach (Match m in Regex.Matches(s, @"<</Length1 (\d+)\n/Filter /FlateDecode\n/Length (\d+)>> stream\n"))
        {
            int start = m.Index + m.Length, len = int.Parse(m.Groups[2].Value);
            using var ms = new MemoryStream(pdf, start + 2, len - 2);
            using var z = new DeflateStream(ms, CompressionMode.Decompress);
            using var outp = new MemoryStream();
            z.CopyTo(outp);
            list.Add(outp.ToArray());
        }
        return list;
    }

    /// <summary>这张字体里有没有任何一个字形带轮廓（空壳的字形全是空的）。</summary>
    static bool HasOutlines(byte[] font)
    {
        using var tf = SKTypeface.FromData(SKData.CreateCopy(font));
        if (tf == null) return false;
        using var f = new SKFont(tf, 40);
        var cps = Enumerable.Range(0x21, 0x5E).Concat(Enumerable.Range(0, 0x9FA6 - 0x4E00).Where(i => i % 7 == 0).Select(i => 0x4E00 + i));
        foreach (int cp in cps)
        {
            int g = tf.GetGlyph(cp);
            if (g == 0) continue;
            using var p = f.GetGlyphPath((ushort)g);
            if (p is { PointCount: > 0 }) return true;
        }
        return false;
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("report")]
    [InlineData("m1")]
    [InlineData("m5")]
    [InlineData("m8")]
    [InlineData("m9")]
    public void Hollow_and_exact_subsets_draw_the_same_page_content(string name)
    {
        var (html, css) = Fixture.ReadSample(name);
        var exact = Render(html, css, hollow: false);
        var hollow = Render(html, css, hollow: true);

        Assert.Equal(PdfTextOps.Pages(exact), PdfTextOps.Pages(hollow));
        using (var a = PdfDocument.Open(exact))
        using (var b = PdfDocument.Open(hollow))
        {
            Assert.Equal(a.NumberOfPages, b.NumberOfPages);
            for (int i = 1; i <= a.NumberOfPages; i++)
                Assert.Equal(a.GetPage(i).Text, b.GetPage(i).Text);
        }
        Assert.Equal(RasterPage1(exact), RasterPage1(hollow));
    }

    [Fact]
    public void Hollow_documents_are_smaller_for_plain_text()
    {
        var (html, css) = Fixture.ReadSample("invoice");
        // 嵌入字体去掉了 PDF 阅读器不读的排版表（GSUB/GPOS/kern…）与 post 的字形名表
        Assert.True(Render(html, css, hollow: true).Length < Render(html, css, hollow: false).Length);
    }

    [Fact]
    public void Hollow_documents_embed_fonts_with_real_outlines()
    {
        int before = PdfGlyphUsage.SwapsOnThisThread;
        var pdf = Render("<p style='font:18px sans-serif'>中文字符 ABC abc 0123 汉字渲染</p><p style='font:700 18px sans-serif'>粗体 Bold</p>", "", hollow: true);
        Assert.True(PdfGlyphUsage.SwapsOnThisThread > before, "空壳路径没有走到换流（每份都退回了带轮廓子集重画？）");
        var fonts = EmbeddedFonts(pdf);
        Assert.NotEmpty(fonts);
        Assert.All(fonts, f => Assert.True(HasOutlines(f), "嵌进 PDF 的字体没有任何轮廓：空壳没被换掉"));
        Assert.DoesNotContain("PSK", Encoding.Latin1.GetString(pdf));   // 占位名不外泄
    }

    [Theory]
    [InlineData("<p style='font:24px sans-serif;text-shadow:2px 2px 3px #888'>阴影文字 Shadow</p>")]                       // 模糊阴影：Skia 先光栅化再合成
    [InlineData("<p style='font:700 28px sans-serif;background:linear-gradient(90deg,red,blue);-webkit-background-clip:text;background-clip:text;color:transparent'>渐变文字 Gradient</p>")]
    [InlineData("<p style='font:20px sans-serif;opacity:.5'>半透明 opacity</p><p style='font:20px sans-serif;transform:rotate(10deg);transform-origin:0 0;margin:40px'>旋转 rotate</p>")]
    public void Text_that_needs_outlines_is_not_drawn_on_hollow_faces(string html)
    {
        var exact = Render(html, "", hollow: false);
        var hollow = Render(html, "", hollow: true);
        Assert.Equal(PdfTextOps.Pages(exact), PdfTextOps.Pages(hollow));
        Assert.Equal(RasterPage1(exact), RasterPage1(hollow));
    }

    [Fact]
    public void Text_whose_origin_is_outside_the_page_but_body_is_inside_is_kept()
    {
        // 基线落在页底之外、字形上半身仍在页内：带轮廓的 face 会保留，空壳（包围盒为空）会被 Skia 剔掉——
        // 所以贴边文字必须改用带轮廓的同一子集。
        const string html = "<body style='margin:0'><div style='position:absolute;left:20px;bottom:-14px;font:20px/24px sans-serif'>贴着页底 Bottom edge 汉字</div>"
            + "<div style='position:absolute;right:-30px;top:200px;font:20px sans-serif;white-space:nowrap'>伸出右边 sticks out</div></body>";
        var exact = Render(html, "", hollow: false);
        var hollow = Render(html, "", hollow: true);
        Assert.Equal(RasterPage1(exact), RasterPage1(hollow));
    }

    [Fact]
    public void SubsetForPdf_hollow_keeps_cmap_and_metrics_but_no_outlines()
    {
        using var tf = SKTypeface.FromFile(Path.Combine(FontDir, "NotoSansSCSub-Regular.ttf"))!;
        var used = new SortedSet<int>();
        foreach (char c in "中文字符ABCabc0123，。") used.Add(tf.GetGlyph(c));
        var pair = TtfSubsetter.SubsetForPdf(tf, used, "PSK7");
        Assert.NotNull(pair);

        using var hollow = SKTypeface.FromData(SKData.CreateCopy(pair!.Hollow))!;
        using var embed = SKTypeface.FromData(SKData.CreateCopy(pair.Embed))!;
        using var fh = new SKFont(hollow, 28);
        using var fe = new SKFont(embed, 28);
        using var fo = new SKFont(tf, 28);
        foreach (char c in "中文字符ABCabc0123，。")
        {
            Assert.Equal(tf.GetGlyph(c), hollow.GetGlyph(c));
            Assert.Equal(tf.GetGlyph(c), embed.GetGlyph(c));
            Assert.Equal(fo.MeasureText(c.ToString()), fh.MeasureText(c.ToString()));   // 度量逐字相同
            using var ph = fh.GetGlyphPath((ushort)hollow.GetGlyph(c));
            Assert.Equal(0, ph?.PointCount ?? 0);   // 空壳没有任何轮廓
        }
        using var po = fe.GetGlyphPath((ushort)embed.GetGlyph('中'));
        Assert.True(po is { PointCount: > 0 });      // 嵌入字体有轮廓
        Assert.Equal("PSK7", hollow.FamilyName);     // 占位名写进 name 表，Skia 据此写 /FontName
        // 嵌入版去掉 PDF 阅读器不读的排版表
        var tags = embed.GetTableTags().Select(t => Encoding.ASCII.GetString(new[] { (byte)(t >> 24), (byte)(t >> 16), (byte)(t >> 8), (byte)t })).ToHashSet();
        Assert.DoesNotContain("GSUB", tags);
        Assert.DoesNotContain("GPOS", tags);
    }

    /// <summary>手工搭一份「Skia 用空壳 face 写出」的 PDF，再换流。</summary>
    static (byte[] Pdf, PdfFontPair Pair, string Text) HollowPdf()
    {
        using var tf = SKTypeface.FromFile(Path.Combine(FontDir, "NotoSansSCSub-Regular.ttf"))!;
        const string text = "中文ABC";
        var used = new SortedSet<int>(text.Select(c => (int)tf.GetGlyph(c)));
        var pair = TtfSubsetter.SubsetForPdf(tf, used, "PSK0")!;
        using var hollow = SKTypeface.FromData(SKData.CreateCopy(pair.Hollow))!;
        using var ms = new MemoryStream();
        using (var doc = SKDocument.CreatePdf(ms))
        {
            using var canvas = doc.BeginPage(300, 200);
            using var font = new SKFont(hollow, 24);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText(text, 20, 60, font, paint);
            doc.EndPage();
            doc.Close();
        }
        return (ms.ToArray(), pair, text);
    }

    [Fact]
    public void FontSwap_replaces_the_hollow_stream_and_restores_the_font_name()
    {
        var (pdf, pair, text) = HollowPdf();
        var target = new PdfFontSwap.Target("PSK0", "NotoSansSCSub-Regular", pair.Hollow.Length, pair.Embed);

        var swapped = PdfFontSwap.Apply(pdf, pdf.Length, new[] { target });
        Assert.NotNull(swapped);
        var bytes = swapped!.Value.Buf.AsSpan(0, swapped.Value.Len).ToArray();
        var s = Encoding.Latin1.GetString(bytes);
        Assert.Matches(@"/FontName /([A-Z]{6}\+)?NotoSansSCSub-Regular\b", s);   // 较新的 Skia 会给子集名加 ABCDEF+ 前缀
        Assert.DoesNotContain("PSK0", s);
        Assert.EndsWith("%%EOF\n", s);

        using var doc = PdfDocument.Open(bytes);   // xref/trailer 重排后仍可解析
        Assert.Equal(text, doc.GetPage(1).Text);
        var fonts = EmbeddedFonts(bytes);
        Assert.Single(fonts);
        Assert.Equal(pair.Embed, fonts[0]);
        Assert.True(HasOutlines(fonts[0]));
    }

    [Fact]
    public void FontSwap_refuses_what_it_cannot_verify()
    {
        var (pdf, pair, _) = HollowPdf();

        // 占位名对不上：空壳没被 Skia 嵌成 TrueType（Type3 / 没出现）
        Assert.Null(PdfFontSwap.Apply(pdf, pdf.Length, new[] { new PdfFontSwap.Target("PSK9", "X", pair.Hollow.Length, pair.Embed) }));
        // Length1 对不上：流里不是我们建的那张空壳
        Assert.Null(PdfFontSwap.Apply(pdf, pdf.Length, new[] { new PdfFontSwap.Target("PSK0", "X", pair.Hollow.Length + 4, pair.Embed) }));
        // 不是 Skia 的平整输出
        Assert.Null(PdfFontSwap.Apply(Encoding.ASCII.GetBytes("not a pdf"), 9, new[] { new PdfFontSwap.Target("PSK0", "X", 1, pair.Embed) }));
    }
}
