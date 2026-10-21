using PaperSharp.Css;
using PaperSharp.Layout;
using SkiaSharp;

namespace PaperSharp.Tests;

/// <summary>
/// &lt;input&gt; 占位符的两条浏览器语义：颜色走 ::placeholder（不是控件自身的 color），
/// 定高时整行文字在内容盒里垂直居中（不是贴顶）。
/// 踩过的坑：引擎把占位符当普通块文本画，颜色写死一个灰、文字贴内容盒顶——
/// login-page 的 90px 输入框里占位符比基准图高出一整行，叠加视图里直接看成两份。
/// ::placeholder 在 AngleSharp 里压根不是可解析伪元素（实测 ComputeDeclarations(el,"::placeholder") 为空），
/// 颜色只能从原始 CSS 兜底扫，所以这里把「扫得到」和「扫不到时用什么」都钉住。
/// </summary>
public class PlaceholderTests
{
    static BlockBox InputBox(string css)
    {
        var body = LayoutBuilder.ToBoxTree(StyleResolver.BuildDocument(
            "<div class=wrap><input class=ph placeholder=请输入手机号></div>", css));
        LayoutBuilder.Measure(body, 600);
        BlockBox? Find(BlockBox b)
        {
            if (b.Style.Tag == "input") return b;
            foreach (var k in b.Children)
                if (Find(k) is BlockBox hit) return hit;
            return null;
        }
        return Find(body) ?? throw new InvalidOperationException("样例里没有 input");
    }

    // flex 容器：input 被块化才成盒（inline 上下文里它只是行内原子），login-page 的 .field 正是这个形状。
    const string Box90 = ".wrap{display:flex;height:90px} .ph{height:90px;font-size:22px;color:#10151c}";

    /// <summary>无作者 ::placeholder 时，Chrome 实测占位符是 placeholderText #757575，且不跟随控件自身 color。</summary>
    [Fact]
    public void Default_placeholder_color_is_chrome_placeholder_text()
    {
        var cs = InputBox(Box90).Style;
        Assert.Equal(new SKColor(0x75, 0x75, 0x75), cs.Color);
        Assert.NotEqual(new SKColor(0x10, 0x15, 0x1c), cs.Color);
    }

    [Theory]
    [InlineData(".ph::placeholder{color:#a8adb5}")]        // class 末段
    [InlineData(".wrap input::placeholder{color:#a8adb5}")] // 组合选择器，末段是 tag
    [InlineData("input::placeholder{color:#a8adb5}")]
    public void Author_placeholder_color_wins(string css)
        => Assert.Equal(new SKColor(0xa8, 0xad, 0xb5), InputBox(Box90 + css).Style.Color);

    /// <summary>作者写了非 ::placeholder 的伪元素规则时，占位符颜色扫描不能把 ::before 的 display 索引带偏。</summary>
    [Fact]
    public void Placeholder_color_coexists_with_before_after_rules()
    {
        var css = Box90 + ".ph::placeholder{color:#00ff00} .wrap::after{content:'x';display:block}";        Assert.Equal(new SKColor(0, 0xff, 0), InputBox(css).Style.Color);
    }

    /// <summary>定高输入框里的整行文字居中；普通块（div）定高仍然贴顶，浏览器就是这么分的。</summary>
    [Fact]
    public void Single_line_control_text_is_vertically_centered()
    {
        var input = InputBox(Box90);
        float lines = input.WrappedLines!.Length;
        Assert.True(lines > 0);
        Assert.Equal((input.ContentHeight - lines * input.Style.LineHeightPx()) / 2, input.ValignDY, 2);
        Assert.True(input.ValignDY > 0, $"90px 盒里的 22px 行应居中，实得 {input.ValignDY}");
    }
}
