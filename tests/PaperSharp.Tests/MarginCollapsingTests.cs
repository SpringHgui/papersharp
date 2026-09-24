namespace PaperSharp.Tests;

/// <summary>
/// 相邻兄弟 margin 坍缩（CSS 2.1 §8.3.1）。
///
/// 引擎此前完全不坍：两个 <p>（各 margin 16px）之间留 32px，浏览器只留 16px。
/// 单看一块不多，一页十几个块就累积成几十 px 的系统性下偏——Playground 盒级 Δ 表在
/// invoice / table 样例上量到的逐级漂移全部来自这里。
///
/// 断言里的期望值全部来自 headless Edge 的 getBoundingClientRect 实测（见每条注释的「实测」），
/// 不是照抄规范文字：overflow / flow-root / position:relative / clear 这几条凭记忆写出来的
/// 「不坍」条件，实测全都照样坍，写成不坍反而会在原本正确的地方重新制造偏差。
/// </summary>
public class MarginCollapsingTests
{
    /// <summary>量指定元素的 border-box 顶边（相对 body 边框盒原点，与 Playground Δ 表同一坐标系）。</summary>
    static BoxRect Box(string html, string css, string domPath, float width = 600)
        => Renderer.RenderRasterWithBoxes(html, css, width).Boxes.Single(b => b.DomPath == domPath);

    const string Two = ".a{height:40px}.b{height:40px}";

    [Fact]
    public void Positive_siblings_collapse_to_the_larger()
    {
        // 实测：a.top=0 b.top=70（40 高 + max(30,12)，不是 40+30+12=82）
        var html = "<div class=\"a\"></div><div class=\"b\"></div>";
        var css = Two + ".a{margin-bottom:30px}.b{margin-top:12px}";
        Assert.Equal(0, Box(html, css, "body[1]/div[1]").Y, 1);
        Assert.Equal(70, Box(html, css, "body[1]/div[2]").Y, 1);
    }

    [Fact]
    public void Equal_margins_collapse_into_one()
    {
        // 实测：两个默认 <p>（UA margin 16px）之间间隙 16，不是 32
        var boxes = Renderer.RenderRasterWithBoxes("<p>甲</p><p>乙</p>", "", 600).Boxes;
        var p1 = boxes.Single(b => b.DomPath == "body[1]/p[1]");
        var p2 = boxes.Single(b => b.DomPath == "body[1]/p[2]");
        Assert.Equal(16, p2.Y - (p1.Y + p1.H), 1);
    }

    [Fact]
    public void A_chain_uses_each_adjacent_pair()
    {
        // a▾30 / b▴12 ▾8 / c▴20，中段有 40px 实高：a-b 坍成 30，b-c 坍成 20
        var html = "<div class=\"a\"></div><div class=\"b\"></div><div class=\"c\"></div>";
        var css = ".a{height:40px;margin-bottom:30px}.b{height:40px;margin-top:12px;margin-bottom:8px}"
                + ".c{height:40px;margin-top:20px}";
        var boxes = Renderer.RenderRasterWithBoxes(html, css, 600).Boxes;
        Assert.Equal(70, boxes.Single(b => b.DomPath.EndsWith("div[2]")).Y, 1);
        Assert.Equal(70 + 40 + 20, boxes.Single(b => b.DomPath.EndsWith("div[3]")).Y, 1);
    }

    [Fact]
    public void Two_negatives_take_the_more_negative()
    {
        // 实测：a▾-10 / b▴-20 → b.top=20（间隙 -20，不是 -30 也不是 -10）
        var css = Two + ".a{margin-bottom:-10px}.b{margin-top:-20px}";
        Assert.Equal(20, Box("<div class=\"a\"></div><div class=\"b\"></div>", css, "body[1]/div[2]").Y, 1);
    }

    [Theory]
    [InlineData(-10, 25, 55)]   // 实测：异号相加 15 → 40+15
    [InlineData(30, -12, 58)]   // 实测：异号相加 18 → 40+18
    public void Opposite_signs_are_summed(float mb, float mt, float expectedY)
    {
        var css = Two + $".a{{margin-bottom:{mb}px}}.b{{margin-top:{mt}px}}";
        Assert.Equal(expectedY, Box("<div class=\"a\"></div><div class=\"b\"></div>", css, "body[1]/div[2]").Y, 1);
    }

    [Fact]
    public void Inline_level_sibling_breaks_the_collapse()
    {
        // 实测：b{display:inline-block} → b.top=82，两侧 margin 都算（40+30+12）
        var css = Two + ".a{margin-bottom:30px}.b{margin-top:12px;display:inline-block;width:50px}";
        Assert.Equal(82, Box("<div class=\"a\"></div><div class=\"b\"></div>", css, "body[1]/div[2]").Y, 1);
    }

    [Theory]
    [InlineData(".b{overflow:hidden}")]            // 实测 70：建立 BFC 只挡住「与子代」坍缩，不挡兄弟
    [InlineData(".b{display:flow-root}")]          // 实测 70：同上
    [InlineData(".b{position:relative}")]          // 实测 70：relative 仍在流内
    [InlineData(".a{overflow:hidden}")]            // 实测 70
    [InlineData(".b{clear:both}")]                 // 实测 70：没有浮动可清时 clear 空转，不影响坍缩
    [InlineData(".b{display:table;width:50px}")]   // 实测 70
    public void These_do_not_prevent_sibling_collapse(string extraCss)
    {
        var css = Two + ".a{margin-bottom:30px}.b{margin-top:12px}" + extraCss;
        Assert.Equal(70, Box("<div class=\"a\"></div><div class=\"b\"></div>", css, "body[1]/div[2]").Y, 1);
    }

    [Fact]
    public void An_in_flow_box_between_them_does_prevent_it()
    {
        // 实测：中间插一个 10px 高的普通块 → 52 = 30 + 10 + 12，两侧各自算
        var html = "<div class=\"a\"></div><div class=\"m\"></div><div class=\"b\"></div>";
        var css = Two + ".a{margin-bottom:30px}.m{height:10px}.b{margin-top:12px}";
        Assert.Equal(92, Box(html, css, "body[1]/div[3]").Y, 1);
    }

    [Fact]
    public void A_display_none_box_between_them_does_not()
    {
        // 实测：中间块 display:none → 仍然相邻，70
        var html = "<div class=\"a\"></div><div class=\"n\"></div><div class=\"b\"></div>";
        var css = Two + ".a{margin-bottom:30px}.n{display:none}.b{margin-top:12px}";
        Assert.Equal(70, Box(html, css, "body[1]/div[3]").Y, 1);
    }

    /// <summary>
    /// 父盒高度与子盒落位出自两轮遍历（MeasureBlock 累加 h，PositionRec 累加 cy）。
    /// 坍缩量 absorb 只要两边算法有一点不一致，就会表现成「子盒排到父盒外面」，
    /// 而 PNG 只画子盒、分页只看父盒高 —— 差值会静默变成底部留白或内容被裁。
    /// </summary>
    [Fact]
    public void Parent_height_agrees_with_collapsed_child_positions()
    {
        var html = "<div class=\"a\"></div><div class=\"b\"></div><div class=\"c\"></div>";
        var css = ".a{height:40px;margin-bottom:30px}.b{height:40px;margin-top:12px;margin-bottom:8px}"
                + ".c{height:40px;margin-top:20px}";
        var boxes = Renderer.RenderRasterWithBoxes(html, css, 600).Boxes;
        var body = boxes.Single(b => b.DomPath == "body[1]");
        var last = boxes.Single(b => b.DomPath.EndsWith("div[3]"));
        Assert.Equal(last.Y + last.H, body.H, 1);
        Assert.All(boxes, b => Assert.True(b.Y + b.H <= body.H + 1, $"{b.DomPath} 越出父盒"));
    }

    /// <summary>百分比 margin 也按解析后的 px 参与坍缩（margin 的 % 一律相对包含块宽）。</summary>
    [Fact]
    public void Percentage_margins_collapse_after_resolution()
    {
        // 宽 600：a▾10%=60 与 b▴2%=12 → 坍成 60 → b.top = 40+60 = 100
        var css = Two + ".a{margin-bottom:10%}.b{margin-top:2%}";
        Assert.Equal(100, Box("<div class=\"a\"></div><div class=\"b\"></div>", css, "body[1]/div[2]").Y, 1);
    }
}
