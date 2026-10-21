using System.Globalization;
using System.Text.RegularExpressions;
using PaperSharp;
using PaperSharp.Layout;
using SkiaSharp;

namespace PaperSharp.Tests;

/// <summary>
/// 字距这块账，**凡落笔的地方都得付** —— 绘制点哨兵。
///
/// 度量端（<see cref="PaperSharp.Layout.TextMeasurer"/> / <see cref="PaperSharp.Layout.TextShaper.WidthOf"/>）
/// 在 hmtx 之外还要加字体自带的字距对，而 Skia 一次 <c>DrawText</c> 只按 hmtx 走位。
/// <see cref="KernPaintParityTests"/> 钉的是「按配对切开、逐段落笔」这套补法本身自洽（ΣTail == Adjust）；
/// 这里钉的是**有没有哪条绘制通路根本没走它** —— 那种地方盒级对拍量不出来（每个盒的矩形都对），
/// 只在 Playground 叠加视图里露成一段重影，而且越靠近句尾糊得越开。
///
/// 血泪三处已修（都由本哨兵管）：
/// ① <c>@page</c> 边距盒（页眉页脚）—— 居中/贴右的宽度用裸 <c>MeasureText</c> 算、整串一次落笔，
///    于是同一串字在页脚里的排布与正文里不一样；
/// ② 混合方向行的主面段（<see cref="PaperSharp.Layout.TextShaper"/> 的 <c>SegWidths</c>/<c>DrawShaped</c>）——
///    两边都不加字距，整条 RTL 行里的拉丁段比浏览器宽（m7 那种 bidi 样例，实测量宽多 3.6px）；
/// ③ SVG 导出的 <c>text-align</c> —— 量宽函数是同一个，但 SVG 那张 paint 没开 AA+subpixel，
///    Skia 于是把字形推进折成整像素，贴右整行左跑（9 个字偏 3px，P2-9）。
///
/// ①② 的判据读 PDF 内容流，且**参照串单独渲染成另一份 PDF**：若把正文与被检通路放进同一份文件，
/// 被检那条就算一分不付，断言也会在同页那条正确的正文上撞个满分（假通过）。
/// ③ 只能跨表面对账（SVG 的 <c>&lt;text x&gt;</c> vs 光栅墨迹），因为它的错不在公式而在 paint 的旗。
/// </summary>
public class KernDrawSiteTests
{
    /// <summary>串里必须真有非零配对：雅黑子集的 T+o 那对量级由 <see cref="KernPaintParityTests"/> 钉着（&lt; −1px）。</summary>
    const string Kerned = "Total Toluene";

    const string BodyCss = """
        <style>html,body{margin:0}body{font:16px/1.6 "Microsoft YaHei",Tahoma,sans-serif}p{margin:0}</style>
        """;

    static byte[] Render(string html) => Renderer.RenderPdf(html, "", baseDir: Fixture.AssetsDir);

    /// <summary>正文通路（TextPainter 的 KernChunks）对这把串的实际切开推进量，作为全引擎的基准口径。</summary>
    static List<double> BodyDeltas()
    {
        var lines = PdfTextOps.KernedLines(Render($"{BodyCss}<p>{Kerned}</p>"));
        var d = PdfTextOps.Deltas(Assert.Single(lines));
        Assert.True(d.Count >= 2, $"正文通路只切出 {d.Count + 1} 段：字距表没装上（子集 PRKT 登记出问题），本哨兵会全部空跑");
        return d;
    }

    /// <summary>
    /// ① 页脚边距盒：单独渲染一份只有页脚文字的 PDF，它的逐段推进必须与正文基准逐条相等。
    /// 修前这里是整串一次落笔（且居中宽度按不付字距的裸宽算），一条都切不开。
    /// </summary>
    [Fact]
    public void Margin_box_pays_the_kern_bill_like_body_text()
    {
        var pdf = Render($$"""
            <style>
              @page { size: 420px 560px; margin: 60px;
                @bottom-center { content: "{{Kerned}}"; font-family: "Microsoft YaHei"; font-size: 16px; } }
              html, body { margin: 0 }
            </style>
            <p>·</p>
            """);

        var lines = PdfTextOps.KernedLines(pdf);
        var got = Assert.Single(lines);
        Assert.Equal(BodyDeltas(), PdfTextOps.Deltas(got));
    }

    /// <summary>
    /// ② 混合方向行的**量宽**：拉丁段必须与纯拉丁行同口径（都付字距）。
    /// 绘制端 <c>DrawSpaced</c> 早就是逐字素补 <c>Pair</c> 的，而 <c>SegWidths</c> 原先不加，
    /// 于是整条 RTL 行量得比画出来的宽 —— 实测比浏览器宽 3.6px / 2.8px（m7 那种 bidi 样例）。
    /// </summary>
    [Fact]
    public void Mixed_direction_line_measures_its_latin_run_like_a_plain_line()
    {
        var tf = FontManager.Get("Microsoft YaHei", 400);
        const string Arabic = "مرحبا بالعالم ", Latin = "Total Toluene";

        float mixed = TextShaper.WidthOf(Arabic + Latin, tf, 16f, 0f);
        float parts = TextShaper.WidthOf(Arabic, tf, 16f, 0f) + TextShaper.WidthOf(Latin, tf, 16f, 0f);
        Assert.True(Math.Abs(mixed - parts) < 0.02f,
            $"混合行量宽 {mixed:0.###} 与「阿拉伯段 + 纯拉丁段」之和 {parts:0.###} 差 {(mixed - parts):0.###}px"
            + "——拉丁段在混合行里没按正文那条口径付字距");
    }

    /// <summary>
    /// ③ SVG 的 <c>text-align</c> 落点：与光栅跨表面对账。同一串写两遍（一遍贴右、一遍贴左），
    /// SVG 里两个 <c>&lt;text x&gt;</c> 之差 == 光栅上两行最左墨迹列之差（首字形左侧 bearing 两边同量、
    /// 天然抵消）。几把串/样式各钉一头：<c>nnnn nnnn</c> 一个配对都没有，只可能栽在**度量旗**上
    /// （Skia 在 AA+subpixel 都没开时把字形推进折成整像素，9 个字就偏 3px）；
    /// <c>AV To Ty Ta Wa Ye</c> 20px 下字距合计 −9.21px，量宽一旦漏付字距立刻越界；
    /// <c>italic</c> 那两条钉**合成错切**——斜体只有光栅那张 paint 带 <c>TextSkewX</c>，
    /// 若量宽不吃同一个 skew，斜体的贴右落点又会分叉。
    /// 光栅读数按整设备 px 取，±0.5 CSS px 是量法本身的分辨率，故阈值 0.6。
    /// </summary>
    [Theory]
    [InlineData("nnnn nnnn", "normal")]
    [InlineData("AV To Ty Ta Wa Ye", "normal")]
    [InlineData("nnnn nnnn", "italic")]
    [InlineData("AV To Ty Ta Wa Ye", "italic")]
    public void Svg_text_align_lands_where_the_raster_puts_the_ink(string text, string style)
    {
        string css = $$"""
            html,body{margin:0}
            p{margin:0;padding-left:24px;width:400px;font:20px/1.6 "Segoe UI","Microsoft YaHei",sans-serif;font-style:{{style}}}
            """;
        // padding-left 是给斜体留的：合成错切会让首字形的墨迹探到 x 左边一点，贴着画布左沿就被裁掉，
        // 两行墨迹之差就不再等于两行落点之差（量法自身的假差，不是引擎的）。
        string html = $"<p style=\"text-align:right\">{text}</p><p>{text}</p>";

        using var bmp = SKBitmap.Decode(Renderer.RenderPng(html, css, 400f, scale: 2f));
        var ink = InkLeftByLine(bmp);
        Assert.Equal(2, ink.Count);
        float raster = (ink[0] - ink[1]) / 2f;      // 贴右行在前；2x 光栅 → CSS px
        Assert.True(raster > 100, $"[{text}/{style}] 贴右那行只比贴左那行靠左 {raster:0.##}px——样例没生效");

        var svg = System.Text.Encoding.UTF8.GetString(Renderer.RenderSvg(html, css, 400f));
        var xs = Regex.Matches(svg, @"<text x=""([-\d.]+)""").Select(m => double.Parse(m.Groups[1].Value, Inv)).ToList();
        Assert.Equal(2, xs.Count);
        Assert.True(Math.Abs((float)(xs[0] - xs[1]) - raster) < 0.6f,
            $"[{text}/{style}] SVG 贴右落点 {xs[0]:0.###} 与光栅墨迹 {raster:0.###} 差 {(xs[0] - raster):0.###}px"
            + "——SVG 的量宽与光栅又不同口径了（度量旗 / 字距 / 合成错切）");
    }

    static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;

    /// <summary>按墨迹行带归堆，取每行最左的墨迹列（只看页首几行，省掉满宽空行的逐像素扫描）。</summary>
    static List<int> InkLeftByLine(SKBitmap bmp)
    {
        var res = new List<int>();
        int cur = -1;
        for (int y = 0, rows = Math.Min(bmp.Height, 200); y < rows; y++)
        {
            int first = -1;
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (255 - (c.Red + c.Green + c.Blue) / 3 > 60) { first = x; break; }
            }
            if (first < 0)
            {
                if (cur >= 0) { res.Add(cur); cur = -1; }
                continue;
            }
            cur = cur < 0 ? first : Math.Min(cur, first);
        }
        if (cur >= 0) res.Add(cur);
        return res;
    }
}
