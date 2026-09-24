using SkiaSharp;
using PDFtoImage;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// overflow 裁剪：开裁剪的祖先把自己的 padding box 罩到整棵子树上。
/// 断言一律用「裁剪框内 / 裁剪框外各取一个像素」的对照式探针，而不是逐字节 golden——
/// 同一份 CSS 在 visible 对照组里必须泄漏，才能证明差异真的来自裁剪而不是画布尺寸或定位巧合。
/// 静态文档不画滚动条：hidden | clip | auto | scroll | overlay 一律等价裁剪（边界声明 §1.1 拍板）。
///
/// 为什么需要 StyleResolver 的原始 CSS 兜底表（实测，AngleSharp 1.3.1 / AngleSharp.Css 1.0.0-beta.176）：
///   hidden / auto / scroll / visible → computed 与 declared 均正常；
///   clip、overlay、两值简写 "visible hidden" → computed 与 declared <b>都是空串</b>（解析阶段就被判非法丢弃）。
/// 所以这三种写法只能靠 ScanUnsupported 从 CSS 原文捞；扫描正则因此加了 (?<![\w-]) 左边界，
/// 否则 "overflow" 会命中同一规则里的 "text-overflow:clip"，把文本截断值当成盒级裁剪值。
/// </summary>
public class OverflowClipTests
{
    // 画布 400 宽；盒 100×100 落在 (0,0)，超宽子盒 300×50 → (200,20) 在盒外、(50,20) 在盒内
    const string Doc = "<div id=box><div id=kid></div></div>";

    static string Css(string boxDecls) =>
        "html,body{margin:0}#box{width:100px;height:100px;background:#ffffff;" + boxDecls + "}" +
        "#kid{width:300px;height:50px;background:#ff0000}";

    static byte[] Png(string css) => Renderer.RenderPng(Doc, css, 400f, scale: 1f);

    static SKColor Pixel(byte[] png, int x, int y)
    {
        using var b = SKBitmap.Decode(png);
        return b.GetPixel(x, y);
    }

    static bool Red(SKColor c) => c.Red > 180 && c.Green < 80 && c.Blue < 80;
    static bool Blue(SKColor c) => c.Blue > 180 && c.Red < 80 && c.Green < 80;
    static bool SameBytes(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

    /// <summary>红像素总数（PDF 栅格回读后用于跨后端比对，避开 PDFium 的 DPI/取整差异）。</summary>
    static int RedCount(SKBitmap b)
    {
        int n = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (Red(b.GetPixel(x, y))) n++;
        return n;
    }

    static SKBitmap PdfPage1(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return Conversion.ToImage(ms, 0);
    }

    [Fact]
    public void Hidden_clips_child_outside_padding_box()
    {
        var png = Png(Css("overflow:hidden"));
        Assert.True(Red(Pixel(png, 50, 20)), "裁剪框内的正常内容不能被动掉");
        Assert.False(Red(Pixel(png, 200, 20)), "overflow:hidden 后 (200,20) 不该再有子盒泄漏");
    }

    [Fact]
    public void Visible_leaks_overflow_control()
    {
        // 对照组：除 overflow 值外与上面逐字相同。这条不成立，上面那条就是假绿。
        var png = Png(Css("overflow:visible"));
        Assert.True(Red(Pixel(png, 200, 20)), "visible 必须照常溢出，否则测不出裁剪");
    }

    [Theory]
    [InlineData("clip")]
    [InlineData("auto")]
    [InlineData("scroll")]
    [InlineData("overlay")]
    public void Clipping_keywords_are_equivalent_to_hidden(string kw)
    {
        Assert.True(SameBytes(Png(Css("overflow:hidden")), Png(Css("overflow:" + kw))),
            $"定格输出里 {kw} 与 hidden 须逐字节相同（都不画滚动条）");
    }

    [Theory]
    [InlineData("overflow-x:hidden")]
    [InlineData("overflow-y:hidden")]
    [InlineData("overflow:visible hidden")]      // 一轴 visible 一轴裁剪 → 规范把 visible 折算成 auto
    [InlineData("overflow-x:visible;overflow-y:scroll")]
    public void Any_axis_trims_both_axes(string decls)
    {
        var png = Png(Css(decls));
        Assert.False(Red(Pixel(png, 200, 20)), $"「{decls}」应等价于整盒裁剪（分轴裁剪明确不做）");
        Assert.True(Red(Pixel(png, 50, 20)));
    }

    [Fact]
    public void Rounded_ancestor_clips_corner()
    {
        var css = "html,body{margin:0}#box{width:100px;height:100px;border-radius:50px;overflow:hidden;background:#fff}" +
                  "#kid{width:100px;height:100px;background:#ff0000}";
        var png = Renderer.RenderPng(Doc, css, 400f, scale: 1f);
        Assert.False(Red(Pixel(png, 1, 1)), "圆角裁剪后左上角在圆外，不该有红");
        Assert.True(Red(Pixel(png, 50, 50)), "圆心的红必须保住");

        // 不裁剪时子盒是方的，角上就是红——证明上面的差异来自圆角裁剪而非圆角背景本身
        var leak = Renderer.RenderPng(Doc, css.Replace("overflow:hidden;", ""), 400f, scale: 1f);
        Assert.True(Red(Pixel(leak, 1, 1)), "对照组：无裁剪时子盒直角应盖到盒角");
    }

    [Fact]
    public void Absolute_child_does_not_escape_ancestor_clip()
    {
        // 引擎按「树祖先」裁脱流盒（不是 CSS 的包含块链），快照钉死模式正依赖这条。
        var doc = "<div id=box><div id=kid></div></div>";
        string Css(string boxDecls) =>
            "html,body{margin:0}#box{position:relative;width:100px;height:100px;background:#fff;" + boxDecls + "}" +
            "#kid{position:absolute;left:0;top:0;width:300px;height:50px;background:#ff0000}";
        var png = Renderer.RenderPng(doc, Css("overflow:hidden"), 400f, scale: 1f);
        Assert.False(Red(Pixel(png, 200, 20)), "absolute 子盒也得被祖先的 overflow 裁掉");
        Assert.True(Red(Pixel(png, 50, 20)));
    }

    [Fact]
    public void Pdf_path_clips_same_as_raster()
    {
        var hidden = PdfPage1(Renderer.RenderPdf(Doc, Css("overflow:hidden")));
        var visible = PdfPage1(Renderer.RenderPdf(Doc, Css("overflow:visible")));
        using (hidden) using (visible)
        {
            int h = RedCount(hidden), v = RedCount(visible);
            Assert.True(h > 0, "PDF 里裁剪框内的红没了？");
            Assert.True(h * 2 < v, $"PDF 未裁到 padding box：hidden={h} 应远小于 visible={v}（300/100 面积比 3×）");
        }
    }

    [Fact]
    public void Clipping_ancestor_shares_page_with_its_items()
    {
        // 钉前提本身而不是页数：裁剪祖先必须和它的绘制条目落在同一页坐标系里。
        // 分页是「每页重新 Position 一棵子树」，祖先一旦被碎片化到别页，
        // BlockBox.OverflowClip 沿父链求出来的矩形就是错的（条目会凭空消失或整块跑偏）。
        // 页数是间接信号（受 @page 边距、孤行控制影响），实测钉不住，故改判坐标。
        var html = "<div id=box>" + string.Concat(Enumerable.Repeat("<p>溢出裁剪分页探针</p>", 60)) + "</div>";
        var css = "html,body{margin:0}p{margin:0;font-size:20px;line-height:30px}" +
                  "#box{width:300px;height:900px;overflow:hidden;background:#ffffff}";
        var spec = AtPageParser.Parse(css, 595.28f, 841.89f);
        var body = LayoutBuilder.ToBoxTree(StyleResolver.BuildDocument(html, css));
        LayoutBuilder.Measure(body, spec.ContentW);

        var pages = Paginator.Paginate(body, spec);
        Assert.NotEmpty(pages);
        int probed = 0;
        foreach (var page in pages)
            foreach (var it in page.Items.Concat(page.AbsItems))
            {
                var clip = it.Box.OverflowClip();
                if (clip == null) continue;
                probed++;
                Assert.True(clip.Value.Rect.Bottom > 0 && clip.Value.Rect.Top < spec.PageH,
                    $"{it.Box.Style.Tag} 的裁剪祖先矩形整块落在页外（Top={clip.Value.Rect.Top} " +
                    $"Bottom={clip.Value.Rect.Bottom}，页高 {spec.PageH}）→ 祖先被碎片化了");
            }
        Assert.True(probed > 0, "一个带裁剪祖先的条目都没扫到 = 探针失效");
    }

    [Fact]
    public void Sibling_margin_collapsing_unaffected_by_overflow()
    {
        // overflow:hidden 建立 BFC，阻断的是「父子」坍缩；兄弟之间照常坍缩。
        // 这条曾按直觉写反过（见 LayoutBuilder.Collapsible 注释里的实测记录），钉死防回归。
        // a 高 50 + margin-bottom 30，b margin-top 20 → 坍缩后 b 顶在 80；不坍缩则在 100。
        var html = "<div id=a></div><div id=b></div>";
        var css = "html,body{margin:0}#a{width:100px;height:50px;margin-bottom:30px;background:#00ff00}" +
                  "#b{width:100px;height:50px;margin-top:20px;overflow:hidden;background:#0000ff}";
        var png = Renderer.RenderPng(html, css, 400f, scale: 1f);
        Assert.True(Blue(Pixel(png, 50, 90)), "b 应紧接坍缩后的 30px 间距落位（顶边 80）");
        Assert.False(Blue(Pixel(png, 50, 79)), "79 还在 a 的下 margin 带里");
    }
}
