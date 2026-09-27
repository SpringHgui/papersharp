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

    /// <summary>
    /// 八条边一次扫完时，「本规则在某条边上更优」只能让它赢下那一条边，不能把已经由更高特异度
    /// 规则赢下的边一起覆盖。这个形状是 invoice 实测逼出来的：div[2] 的 margin-top 被低特异度的
    /// <c>.b</c> 从 10px 顶成 20px，兄弟间坍缩随之少 8px，页面后半整段下移（PDF 栅格 diff 报出）。
    /// </summary>
    [Fact]
    public void EachEdgeIsAdjudicatedSeparately_higherSpecEdgeSurvives()
    {
        var (top, bottom, _) = H2Margins(
            "<h2 class='a b'>x</h2>",
            $"{Reset} h2.a{{margin-top:10px}} .b{{margin-top:20px;margin-bottom:5px}}");
        Assert.Equal(10, top);      // h2.a 特异度 (0,1,1) 压过 .b (0,1,0)
        Assert.Equal(5, bottom);    // margin-bottom 只有 .b 声明，归 .b
    }

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
        // 默认值现在是 **em**（Chrome UA：h2 { margin: .83em }），按 h2 自己的字号折算而不是 16px 根字号，
        // 所以 14px 的 h2 是 .83×14=11.62 而不是 20（_probe-ua2 实测 headless Chrome 同值）。
        Assert.Equal(11.62f, H2Margins("<h2>x</h2>", "h2{font-size:14px}").Top, 2);
        Assert.Equal(0, H2Margins("<h2>x</h2>", $"{Reset} h2{{font-size:14px}}").Top);
    }

    /// <summary>量 body 里第一个 ul 的上下 margin（本类只关心 UA 的 <c>ul,ol{margin:1em 0}</c>）。</summary>
    static (float Top, float Bottom) UlMargins(string html, string css)
    {
        var body = LayoutBuilder.ToBoxTree(StyleResolver.BuildDocument(html, css));
        LayoutBuilder.Measure(body, 600);
        BlockBox? Find(BlockBox b)
        {
            if (b.Style.Tag == "ul") return b;
            foreach (var k in b.Children)
                if (Find(k) is BlockBox hit) return hit;
            return null;
        }
        var ul = Find(body) ?? throw new InvalidOperationException("样例里没有 ul");
        return (ul.Style.MarginTop, ul.Style.MarginBottom);
    }

    /// <summary>
    /// margin 的 em 必须按**元素自己的字号**折，不是按渲染设备的 16px：AngleSharp 的 computed
    /// 拿 <c>margin:1em</c> 折出来的就是设备值（实测 16px），声明值才是原文，所以 ReadStyle 的
    /// <c>MarginVal</c> 对 margin 一律优先声明值。把这条「简化」成只读 computed 的代价是 m1 实测：
    /// <c>.note{font-size:10pt}</c> 里那条 ul 少 2.667px，八条列表项累计把整页后半推歪。
    /// 同理也堵死「按 computed 是否为空来跳过第二轮层叠」——computed 对未声明属性本就返回空串。
    /// </summary>
    [Fact]
    public void UaMarginInEm_usesElementFontSize_notDeviceBase()
    {
        var (top, bottom) = UlMargins("<div style='font-size:20px'><ul><li>x</li></ul></div>", "");
        Assert.Equal(20, top);        // 1em of 20px；按设备基线折会得到 16
        Assert.Equal(20, bottom);
        // em 基准跟着继承字号走，不是常量：ul 继承 10px → 1em = 10px
        Assert.Equal(10, UlMargins("<div style='font-size:20px'><div style='font-size:10px'><ul><li>x</li></ul></div></div>", "").Top);
    }
}
