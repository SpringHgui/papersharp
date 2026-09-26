using PaperSharp.Css;

namespace PaperSharp.Tests;

/// <summary>
/// 带单位的行高（<c>line-height: 46px</c> / <c>1.5em</c>）。
///
/// 引擎原先把这类值当 normal 兜底（ParseLineHeight 里明写「demo 不做绝对行高单位换算」），
/// 定高盒里的文字于是整条基线偏 (L − normal)/2：m9 的 <c>height:46px; line-height:46px</c> 偏 11px、
/// m3 的 <c>line-height:90px</c> 偏 36px。盒矩形两边全对（Δ 表 0 漂移），只有叠图看得见重影——
/// 所以这条要按 used line-height 断言，光看盒级对拍会漏。
///
/// em 另外要绕开 AngleSharp：它的 computed 把 <c>1.5em</c> 按初值字号 16 折算（<c>font-size:30px</c>
/// 下给 24px 而不是 45px），基准根本不是本元素字号，故 em 走声明值自己折算。
/// 期望值取自 headless Edge 的 getBoundingClientRect / 截图像素探针。
/// </summary>
public class AbsoluteLineHeightTests
{
    static ComputedStyle At(string html, string css, string domPathSuffix)
    {
        var root = StyleResolver.BuildDocument(html, css, domPath: true);
        ComputedStyle? hit = null;
        void Walk(ComputedStyle n)
        {
            if (hit == null && n.DomPath?.EndsWith(domPathSuffix, StringComparison.Ordinal) == true) hit = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(root);
        return hit ?? throw new InvalidOperationException($"树里找不到 {domPathSuffix}");
    }

    [Theory]
    [InlineData("div{font-size:15px;line-height:46px}", 46f)]        // m9/m3 的定高盒
    [InlineData("div{font-size:15px;line-height:16.5px}", 16.5f)]    // 非整像素
    [InlineData("div{font-size:15px;line-height:1.5em}", 22.5f)]     // em：按本元素字号折算
    [InlineData("div{font-size:30px;line-height:1.5em}", 45f)]       // 同上，AngleSharp computed 会给错的 24
    [InlineData("div{font-size:15px;line-height:1.5}", 22.5f)]       // 纯数字仍走倍数
    [InlineData("div{font-size:15px;line-height:150%}", 22.5f)]      // 百分比同倍数
    public void Used_line_height_is_the_declared_length(string rule, float expected)
        => Assert.Equal(expected, At("<div>x</div>", rule, "div[1]").LineHeightPx(), 3);

    [Fact]
    public void Absolute_line_height_inherits_as_a_length()
    {
        // 父写 40px、子字号不同：CSS 里行高在父级就折成绝对长度再继承，子元素不该跟着 20px 重新缩放。
        var s = At("<div class=\"p\"><span class=\"k\">x</span></div>",
            ".p{font-size:12px;line-height:40px}.k{font-size:20px}", "span[1]");
        Assert.Equal(40f, s.LineHeightPx(), 3);
    }

    [Fact]
    public void Normal_line_height_still_comes_from_font_metrics()
    {
        // Chrome 的 normal 行盒 = round(ascent·fs) + round(descent·fs)（逐度量取整、不含 lineGap）。
        // 15px sans-serif 本机 headless Chrome 量得 21；老的「比值×字号」给 21.72，每行 +0.7px 就是整页漂移。
        var s = At("<div>x</div>", "div{font-size:15px;line-height:normal}", "div[1]");
        Assert.True(float.IsNaN(s.LineHeightAbsPx));
        Assert.False(s.LineHeightNumeric);
        Assert.Equal(21f, s.LineHeightPx(), 3);
        Assert.NotEqual(15f * s.LineHeightFactor, s.LineHeightPx(), 3);
    }

    [Theory]
    [InlineData(16, 21)]     // 雅黑：Chrome 实测 21（老的 1.4 兜底给 22.39）
    [InlineData(13, 17)]
    [InlineData(10, 14)]
    [InlineData(20, 26)]
    [InlineData(8, 10)]
    [InlineData(10.5f, 14)]
    public void Normal_line_height_matches_chrome_per_font_size(float size, float expected)
    {
        // UA 表里绝不能再写 line-height：它以数字继承给全部元素，normal 这条路就永远走不到。
        var s = At("<div>x</div>", $"div{{font-size:{size}px;font-family:\"Microsoft YaHei\"}}", "div[1]");
        Assert.Equal(expected, s.LineHeightPx(), 3);
    }
}
