using UglyToad.PdfPig;
using PaperSharp;

namespace PaperSharp.Tests;

/// <summary>
/// @page 边距盒的**钉位**口径（居中/贴右用的那个宽度）。
///
/// 边距盒不走布局，x 由 <c>MarginBoxRenderer</c> 自己按「贴左 / 居中 / 贴右」算：
///   贴左 = spanLeft，居中 = spanLeft + (spanW − w)/2，贴右 = spanRight − w。
/// 所以 w 漏了字体字距对，页码条就贴不到版心右边（差整个 |K|）、居中行偏半个 |K|。
/// 落笔那半边由 <see cref="KernDrawSiteTests"/> 哨兵管（逐段推进必须与正文同一条口径），
/// 这里只钉**宽度算式**——它俩错法不同，得分开守。
///
/// 探针 src/PaperSharpSamples/assets/_probe-mb.html 把同一串 "Wave offline AVoWyA!" 同时放进
/// @top-left / @top-center / @bottom-right。三个盒子吃的是同一个 w，于是**不带边距衬字**地：
///   贴右起点 − 贴左起点 = spanW − w，居中起点 − 贴左起点 = (spanW − w)/2
/// 前者必须严格是后者的 2 倍（两钉同宽），再由 spanW − 贴右差反出 w 的绝对值。
/// 探针串在雅黑子集里有 6 个非零配对（裸宽 152.291px / 带字距 149.557px，差 2.734px = 2.05pt），
/// 判据取 112.17pt 而非 114.22pt，容差 0.6pt 足以分开两者。
/// 绝对值只依赖 PdfPig 读到的页面框与 16mm 页边距，不依赖同页正文 —— 不存在「拿正确那条撞满分」的假通过。
/// </summary>
public class MarginBoxKernTests
{
    const string Probe = "_probe-mb";
    const double MarginPt = 16 * 72.0 / 25.4;       // 探针 @page margin: 24mm 16mm
    const double KernedPt = 149.557 * 0.75;         // hmtx + 字距对（PaperSharpSamples kern 探针实测）
    const double BarePt = 152.291 * 0.75;           // 纯 hmtx：度量端漏付字距时会退回这个

    /// <summary>按基线把字形分成文本行（PDF 坐标 y 向上：页眉在顶、页脚在底），
    /// 再按 x 的大跳变（&gt;20pt，远大于词距）切成盒子。</summary>
    static List<List<(double X, double R)>> Boxes(PdfDocument doc)
    {
        var ls = doc.GetPage(1).Letters
            .Select(l => (B: (double)l.GlyphRectangle.Bottom, L: (double)l.GlyphRectangle.Left, R: (double)l.GlyphRectangle.Right))
            .OrderBy(t => t.B).ToList();
        var bands = new List<List<(double B, double L, double R)>>();
        foreach (var t in ls)
        {
            // 同一行里各字形的墨迹下缘差 1~3pt（基线 / 降部），行与行之间差上百 pt
            if (bands.Count > 0 && t.B - bands[^1][^1].B <= 5) bands[^1].Add(t);
            else bands.Add(new List<(double, double, double)> { t });
        }
        var boxes = new List<List<(double, double)>>();
        foreach (var band in bands.OrderByDescending(b => b[0].B))
        {
            var ordered = band.OrderBy(t => t.L).ToList();
            var run = new List<(double, double)> { (ordered[0].L, ordered[0].R) };
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].L - ordered[i - 1].R > 20) run.Add((ordered[i].L, ordered[i].R));
                else run[^1] = (run[^1].Item1, Math.Max(run[^1].Item2, ordered[i].R));
            }
            boxes.Add(run);
        }
        return boxes;
    }

    [Fact]
    public void Center_and_right_pins_use_the_kerned_width()
    {
        var html = File.ReadAllText(Path.Combine(Fixture.AssetsDir, Probe + ".html"));
        var cssPath = Path.Combine(Fixture.AssetsDir, Probe + ".css");
        using var doc = PdfDocument.Open(Renderer.RenderPdf(html,
            File.Exists(cssPath) ? File.ReadAllText(cssPath) : "", baseDir: Fixture.AssetsDir));

        var top = Boxes(doc)[0];                       // @top-left, @top-center
        var bottom = Boxes(doc)[^1];                   // @bottom-right
        Assert.Equal(2, top.Count);
        Assert.Single(bottom);

        double dCenter = top[1].X - top[0].X;
        double dRight = bottom[0].X - top[0].X;
        // 居中钉 = (spanW − w)/2，贴右钉 = spanW − w ⇒ 严格 2 倍（两钉必须吃同一个 w）
        Assert.True(Math.Abs(dRight - 2 * dCenter) < 0.35,
            $"贴右偏移 {dRight:0.###}pt 不是居中偏移 {dCenter:0.###}pt 的 2 倍——两个盒子用的宽度不是同一个");

        double w = doc.GetPage(1).Width - 2 * MarginPt - dRight;
        Assert.True(Math.Abs(w - BarePt) > 0.9,
            $"边距盒退回按裸宽 {w:0.###}pt 定位了：页码贴不到版心右边（差 {BarePt - KernedPt:0.##}pt），居中行偏半个配对量");
        Assert.True(Math.Abs(w - KernedPt) < 0.6,
            $"边距盒按 w={w:0.###}pt 定位，既不是字距宽 {KernedPt:0.###}pt 也不是裸宽 {BarePt:0.###}pt");
    }
}
