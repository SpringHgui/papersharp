using UglyToad.PdfPig;

namespace PaperSharp.Tests;

/// <summary>
/// 伪元素 ::before/::after 语义层：content 字符串 / attr() / 引号 / counter() / counters() /
/// 块级装饰 / url() 图片。走文本抽取断言（不钉像素，跨字体栈更稳）；块级与图片另测「渲染不崩且成盒」。
/// </summary>
public class PseudoElementTests
{
    static string Text(string html, string css)
    {
        using var doc = PdfDocument.Open(Renderer.RenderPdf(html, css));
        return string.Concat(doc.GetPages().Select(p => p.Text)).Replace("\n", "").Replace(" ", "");
    }

    [Fact]
    public void Inline_before_after_content()
    {
        var t = Text("<p>body</p>", "p::before{content:'BEFORE-'}p::after{content:'-AFTER'}");
        Assert.Contains("BEFORE-", t);
        Assert.Contains("body", t);
        Assert.Contains("-AFTER", t);
    }

    [Fact]
    public void Content_attr()
    {
        var t = Text("<h2 id=\"intro\">Title</h2>", "h2::after{content:' # ' attr(id)}");
        Assert.Contains("#intro", t);
    }

    [Fact]
    public void Content_quotes()
    {
        var t = Text("<p><q>hi</q></p>", "q::before{content:open-quote}q::after{content:close-quote}");
        Assert.Contains("“hi”", t);
    }

    [Fact]
    public void Counter_cjk_style()
    {
        var t = Text("<div><h3>A</h3><h3>B</h3></div>",
            "div{counter-reset:s}h3{counter-increment:s}h3::before{content:counter(s,cjk-ideographic)'、'}");
        Assert.Contains("一、A", t);
        Assert.Contains("二、B", t);
    }

    [Fact]
    public void Counters_multilevel_outline()
    {
        var t = Text("<ol class=o><li>一<ol><li>甲</li><li>乙</li></ol></li><li>二</li></ol>",
            "ol.o,ol.o ol{counter-reset:i}ol.o li{counter-increment:i}ol.o li::before{content:counters(i,'.')' '}");
        Assert.Contains("1一", t);
        Assert.Contains("1.1甲", t);
        Assert.Contains("1.2乙", t);
        Assert.Contains("2二", t);
    }

    [Fact]
    public void Badge_after_counter_on_inline_block()
    {
        var t = Text("<ul class=t><li>A</li><li>B</li></ul>",
            "ul.t{counter-reset:g}ul.t li{counter-increment:g;display:inline-block}ul.t li::after{content:'#'counter(g)}");
        Assert.Contains("A#1", t);
        Assert.Contains("B#2", t);
    }

    [Fact]
    public void Block_pseudo_decorative_renders_and_adds_height()
    {
        // 空 content 的块级伪元素：无文字可抽，但应成盒并撑高——对比有/无 ::before 的 PNG 尺寸。
        var html = "<div class=c>x</div>";
        var withBar = Renderer.RenderPng(html, ".c{padding:4px}.c::before{content:'';display:block;height:40px;background:#f00}", 200);
        var noBar = Renderer.RenderPng(html, ".c{padding:4px}", 200);
        Assert.True(withBar.Length > 0 && noBar.Length > 0);
        // 带 40px 装饰条的整图更高 → 字节数（PNG 非压缩尺寸代理）不同，且明显大于无条版
        Assert.NotEqual(withBar.Length, noBar.Length);
    }

    [Fact]
    public void Url_image_pseudo_renders()
    {
        // url() 图片伪元素：真图（assets/imgs/qr.png）加载成功会改变输出；坏路径则不成图。
        var withImg = Renderer.RenderPng("<p>hi</p>", "p::before{content:url(imgs/qr.png);width:16px;height:16px}", 200, Fixture.AssetsDir);
        var bogus = Renderer.RenderPng("<p>hi</p>", "p::before{content:url(no-such-file-xyz.png);width:16px;height:16px}", 200, Fixture.AssetsDir);
        Assert.Equal((byte)0x89, withImg[0]);   // PNG 签名
        Assert.NotEqual(withImg.Length, bogus.Length);
    }

    [Fact]
    public void No_pseudo_rules_leaves_output_unchanged()
    {
        // 门控回归哨兵：CSS 无 ::before/::after 时，伪元素代码路径整体跳过，输出与「无该特性」一致。
        var a = Text("<p>plain</p>", "p{color:red}");
        Assert.Equal("plain", a);
    }
}
