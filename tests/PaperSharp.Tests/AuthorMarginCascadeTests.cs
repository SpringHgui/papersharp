using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// 作者 margin/padding 微级联（<see cref="AuthorBoxSpacing"/>）的形状守卫。
/// 踩过的坑：末段是复合选择器（h2.sec）时整串被当成标签名建索引桶，查询按 el.TagName 取不到，
/// 于是 `.band h2.sec{margin-bottom:44px}` 永不被扫到，反而让全局重置 `*{margin:0}` 独赢，
/// 定价页整个 subtree 上坠 44~68px（pricing-mid 均值 32.7 → 100.5）。
/// </summary>
public class AuthorMarginCascadeTests
{
    /// <summary>量 body 里第一个 h2 的 margin（作者源级联结果）。</summary>
    static (float Top, float Bottom, float FontSize) H2Margins(string html, string css)
    {
        var body = LayoutBuilder.ToBoxTree(StyleResolver.BuildDocument(html, css));
        LayoutBuilder.Measure(body, 600);
        BlockBox? Find(BlockBox b)
        {
            if (b.Style.Tag == "h2") return b;
            foreach (var k in b.Children)
                if (Find(k) is BlockBox hit) return hit;
            return null;
        }
        var h2 = Find(body) ?? throw new InvalidOperationException("样例里没有 h2");
        LayoutBuilder.Position(body, 0, 0);
        return (h2.Style.MarginTop, h2.Style.MarginBottom, h2.Style.FontSize);
    }

    const string Reset = "*{box-sizing:border-box;margin:0;padding:0}";

    [Fact]
    public void CompoundTagClass_beatsUniversalReset()
    {
        var (top, bottom, fs) = H2Margins(
            "<section class='band'><div class='wrap'><h2 class='sec'>x</h2></div></section>",
            $"{Reset} .band h2.sec{{font-size:30px;margin-bottom:44px}}");
        Assert.Equal(30, fs);
        Assert.Equal(0, top);       // 重置赢：作者没写 margin-top，UA 默认不得穿透
        Assert.Equal(44, bottom);   // 复合选择器 h2.sec 的声明必须被扫到
    }

    [Fact]
    public void DescendantCompound_resolvesEachEdgeIndependently()
    {
        var (top, bottom, _) = H2Margins(
            "<div class='wrap'><h2 class='sec'>x</h2></div>",
            $"{Reset} .wrap h2.sec{{font-size:30px;margin-top:10px;margin-bottom:44px}}");
        Assert.Equal(10, top);
        Assert.Equal(44, bottom);
    }

    [Fact]
    public void BareClass_and_BareTag_stillResolve()
    {
        Assert.Equal(44, H2Margins("<h2 class='sec'>x</h2>", $"{Reset} .sec{{font-size:30px;margin-bottom:44px}}").Bottom);
        Assert.Equal(44, H2Margins("<div class='w'><h2>x</h2></div>", $"{Reset} .w h2{{font-size:30px;margin-bottom:44px}}").Bottom);
    }

    [Fact]
    public void TypeSelectorCounts_higherThanPureClassTie()
    {
        // .a h2.b(0,2,1) 与 .a .b(0,2,0) 同时声明：真浏览器按特异度取前者，与源顺序无关。
        var (top, _, _) = H2Margins(
            "<div class='a'><h2 class='b'>x</h2></div>",
            $"{Reset} .a .b{{margin-top:1px}} .a h2.b{{margin-top:2px}}");
        Assert.Equal(2, top);
    }

    [Fact]
    public void UniversalReset_winsOverUaDefault()
    {
        // 没写重置时 h2 的内建默认须在；写了 `*{margin:0}` 就必须被压掉（AngleSharp 会反，故有本类）。
        Assert.Equal(20, H2Margins("<h2>x</h2>", "h2{font-size:14px}").Top);
        Assert.Equal(0, H2Margins("<h2>x</h2>", $"{Reset} h2{{font-size:14px}}").Top);
    }
}
