using System.Diagnostics;
using SkiaSharp;
using PaperSharp;

namespace PaperSharpSamples;

/// <summary>
/// PDF 字体嵌入行为探针：同一小段文本，分别用不同来源的 typeface 出 PDF，
/// 量「PDF 体积 / 编码耗时 / 字体流占比」，判断 Skia 2.88 在哪些字体上做了子集化、哪些整嵌。
/// dotnet run -- pdfprobe
/// </summary>
public static class PdfProbe
{
    public static void Run(string assets, string outDir)
    {
        var fontDir = Path.Combine(assets, "fonts");
        var cases = new List<(string name, SKTypeface? tf)>
        {
            ("system SegoeUI", SKFontManager.Default.MatchFamily("Segoe UI")),
            ("system Arial", SKFontManager.Default.MatchFamily("Arial")),
            ("system msyh(ttc)", SKTypeface.FromFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"))),
            ("bundled NotoSub-Regular", SafeFile(Path.Combine(fontDir, "NotoSansSCSub-Regular.ttf"))),
            ("bundled NSimSunSub", SafeFile(Path.Combine(fontDir, "NSimSunSub-Regular.ttf"))),
            ("bundled YaheiSub-Regular", SafeFile(Path.Combine(fontDir, "YaheiSub-Regular.ttf"))),
        };

        Console.WriteLine("PROBE,label,glyphs_written,pdf_kb,enc_ms,min_ms,fontstreams");
        foreach (var (label, tf) in cases)
        {
            if (tf == null) { Console.WriteLine($"PROBE,{label},skip"); continue; }
            const string text = "Invoice 发票金额合计 №12345 Hello World 测试文本 ABCD";

            byte[] Enc()
            {
                using var ms = new MemoryStream();
                using (var doc = SKDocument.CreatePdf(ms))
                {
                    using var c = doc.BeginPage(300, 150);
                    using var p0 = new SKPaint { Typeface = tf, TextSize = 18 };
                    c.DrawText(text, 40, 100, p0);
                    doc.EndPage();
                    doc.Close();
                }
                return ms.ToArray();
            }
            var warm = Enc();
            byte[] last = warm;
            double t = 0, mn = double.MaxValue;
            for (int i = 0; i < 5; i++) { var sw = Stopwatch.StartNew(); last = Enc(); var e = sw.Elapsed.TotalMilliseconds; t += e; mn = Math.Min(mn, e); }
            var streams = FontStreams(last);
            Console.WriteLine($"PROBE,{label},{text.Length},{last.Length / 1024.0:0},{t / 5:0.0},{mn:0.0},{streams}");
        }

        // 彩色字体（emoji）实例化方式探针：同一份字节，FromFile / FromData / 裁剪后 FromData 各自光栅，
        // 数彩色像素——定位「子集化丢彩色」到底出在字节还是出在 SKTypeface 的创建路径。
        ColorProbe();

        // 表级解剖：SKTypeface 能否拿到全部表 + 各表大小（决定自研子集化怎么接）
        Console.WriteLine("TABLES,label,numTables,glyfKB,locaKB,cmapKB,GPOS_KB,GSUB_KB,hmtxKB,nameKB,postKB,totalKB");
        foreach (var (label, tf) in cases)
        {
            if (tf == null) continue;
            try
            {
                var tags = tf.GetTableTags();
                var sizes = new Dictionary<string, int>();
                foreach (var tag in tags)
                {
                    var name = TagName(tag);
                    sizes[name] = tf.GetTableData(tag)?.Length ?? 0;
                }
                int G(string k) => sizes.GetValueOrDefault(k);
                Console.WriteLine($"TABLES,{label},{tags.Length},{G("glyf") / 1024},{G("loca") / 1024},{G("cmap") / 1024},{G("GPOS") / 1024},{G("GSUB") / 1024},{G("hmtx") / 1024},{G("name") / 1024},{G("post") / 1024},{sizes.Values.Sum() / 1024}");
            }
            catch (Exception e) { Console.WriteLine($"TABLES,{label},ERR {e.GetType().Name}:{e.Message}"); }
        }
    }

    static string TagName(uint tag) => string.Create(4, tag, (span, t) =>
    {
        span[0] = (char)((t >> 24) & 0xFF); span[1] = (char)((t >> 16) & 0xFF);
        span[2] = (char)((t >> 8) & 0xFF); span[3] = (char)(t & 0xFF);
    });

    static SKTypeface? SafeFile(string p) => File.Exists(p) ? SKTypeface.FromFile(p) : null;

    /// <summary>列出所有 &gt;5KB 的 FlateDecode 流大小，拼成 desc。</summary>
    static string FontStreams(byte[] pdf)
    {
        var s = System.Text.Encoding.Latin1.GetString(pdf);
        var parts = new List<string>();
        foreach (System.Text.RegularExpressions.Match mm in
                 System.Text.RegularExpressions.Regex.Matches(s, @"/Filter\s*/FlateDecode[^>]*?/Length\s+(\d+)|/Length\s+(\d+)[^>]*?/Filter\s*/FlateDecode"))
        {
            int len = int.TryParse(mm.Groups[1].Value, out var a) ? a : int.Parse(mm.Groups[2].Value);
            if (len > 5000) parts.Add((len / 1024) + "KB");
        }
        return string.Join("+", parts);
    }

    /// <summary>
    /// 彩色字体（emoji）三条实例化路径对照：<c>FromFile</c> / 原字节 <c>FromData</c> / 裁剪后 <c>FromData</c>，
    /// 数彩色像素定位「子集化丢彩色」出在字节还是创建方式。
    /// </summary>
    static void ColorProbe()
    {
        string emj = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");
        if (!File.Exists(emj)) return;
        byte[] whole = File.ReadAllBytes(emj);
        using var byFile = SKTypeface.FromFile(emj);
        using var byData = SKTypeface.FromData(SKData.CreateCopy(whole));
        var used = new List<int>();
        const string s = "😀👍✔☑";
        for (int i = 0; i < s.Length; i++) { int g = byFile.GetGlyph(s[i]); if (g != 0) used.Add(g); }
        var subBytes = PaperSharp.Pdf.TtfSubsetter.Subset(byFile, used);

        Console.WriteLine("COLOR,label,subsetKB,colorPixels,drawnPixels");
        Console.WriteLine($"COLOR,FromFile({s}),-,{ColorPx(byFile)},{AnyPx(byFile)}");
        Console.WriteLine($"COLOR,FromData(whole),{whole.Length / 1024},{ColorPx(byData)},{AnyPx(byData)}");
        if (subBytes != null)
        {
            using var bySub = SKTypeface.FromData(SKData.CreateCopy(subBytes));
            Console.WriteLine($"COLOR,FromData(subset),{subBytes.Length / 1024},{ColorPx(bySub)},{AnyPx(bySub)}");
        }
        else Console.WriteLine("COLOR,FromData(subset),null");
    }

    static int ColorPx(SKTypeface tf) => Rasterize(tf, (r, g, b) => Math.Abs(r - g) > 12 || Math.Abs(g - b) > 12);

    static int AnyPx(SKTypeface tf) => Rasterize(tf, (r, g, b) => r < 240 || g < 240 || b < 240);

    static int Rasterize(SKTypeface tf, Func<byte, byte, byte, bool> hit)
    {
        const string s = "😀👍✔☑";
        using var surface = SKSurface.Create(new SKImageInfo(240, 60, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Typeface = tf, TextSize = 40, IsAntialias = true, Color = SKColors.Black };
        surface.Canvas.DrawText(s, 4, 46, paint);
        using var img = surface.Snapshot();
        using var bmp = new SKBitmap(240, 60, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (!img.ReadPixels(bmp.Info, bmp.GetPixels())) return -1;
        var buf = new byte[240 * 60 * 4];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), buf, 0, buf.Length);
        int n = 0;
        for (int i = 0; i < buf.Length; i += 4)
            if (hit(buf[i + 2], buf[i + 1], buf[i])) n++;
        return n;
    }
}
