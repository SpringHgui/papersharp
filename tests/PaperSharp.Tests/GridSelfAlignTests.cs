namespace PaperSharp.Tests;

/// <summary>
/// grid 子项自对齐（align-self / justify-self / place-self）与显式空轨道。
///
/// 两处引擎缺陷（盒级 Δ 表在 pricing-mid / pricing-page 上量到的大面积下坠全部来自这里）：
/// ① MeasureGrid 无条件把子项纵向拉到所跨行总高，从没读 align-self；浏览器里
///    <c>.cat{align-self:start}</c> 只有 59.8 高，引擎给 525（整行高），同一行右侧的
///    flex 列被顶到错误基线，往下每一格都偏。
/// ② 非 stretch 那一轴要按 fit-content 收，横轴完全没做——且 AngleSharp 在解析期就把
///    align-self:start/end、justify-self、place-self 整条声明丢掉（computed 与
///    GetDeclarations 都是空串），引擎根本读不到，所以 200 宽拉满 vs 浏览器 12.1。
///
/// 期望值取自 headless Edge 的 getBoundingClientRect 实测（对拍样例 assets/_gsel.html，
/// 该样例现为 18 盒全零差），不是照抄规范文字。行几何：4 条显式行 120px，gap 10px，
/// 第 5/6 行为隐式 auto；两列各 200px。
/// </summary>
public class GridSelfAlignTests
{
    const string Css = """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font: 14px/1.4 Arial, sans-serif; }
        .g { display: grid; grid-template-columns: 200px 200px; grid-template-rows: 120px 120px 120px 120px; gap: 10px; width: 420px; }
        .g > div { background: #eee; border: 1px solid #333; }
        .s1 { align-self: start; }
        .s2 { align-self: center; }
        .s3 { align-self: end; }
        .j1 { justify-self: start; }
        .j2 { justify-self: center; }
        .j3 { justify-self: end; }
        .p1 { place-self: center; }
        .f1 { align-self: start; height: 40px; }
        .f2 { width: 80px; }
        .b1 { align-self: center; justify-self: center; }
        .m1 { align-self: start; justify-self: start; margin: 10px 20px; }
        .g2 { align-items: center; justify-items: center; }
        .a2 { align-self: stretch; justify-self: stretch; }
        """;

    const string Markup = """
        <div class="g">
          <div class="s0">stretch</div><div class="s1">start</div>
          <div class="s2">center</div><div class="s3">end</div>
          <div class="j1">js</div><div class="j2">jc</div>
          <div class="j3">je</div><div class="p1">pc</div>
          <div class="f1">fixed-h</div><div class="f2">fixed-w</div>
          <div class="b1">both-c</div><div class="m1">m-start</div>
        </div>
        <div class="g g2">
          <div class="a0">items-center</div><div class="a1">items-center</div><div class="a2">self-stretch</div>
        </div>
        """;

    static readonly IReadOnlyDictionary<string, BoxRect> Box =
        Renderer.RenderRasterWithBoxes(Markup, Css, 1284).Boxes.ToDictionary(b => b.DomPath);

    static BoxRect Item(int n) => Box[$"body[1]/div[1]/div[{n}]"];

    [Fact]
    public void Default_stretches_both_axes_but_self_start_keeps_natural_height()
    {
        // 实测：s0 200×120（默认两轴 stretch）；s1 y=0 h=21.6 w=200（只收交叉轴）
        Assert.Equal(new { x = 0f, y = 0f, w = 200f, h = 120f }.x, Item(1).X, 1);
        Assert.Equal(120, Item(1).H, 1);
        var s1 = Item(2);
        Assert.Equal(0, s1.Y, 1);
        Assert.True(s1.H is > 15 and < 40, $"align-self:start 没收缩：h={s1.H}");
        Assert.Equal(200, s1.W, 1);
    }

    [Fact]
    public void AlignSelf_center_and_end_use_the_track_free_space()
    {
        var s2 = Item(3);
        var s3 = Item(4);
        Assert.Equal(130 + (120 - s2.H) / 2, s2.Y, 1);   // 实测 y=179.2
        Assert.Equal(250, s3.Y + s3.H, 1);                // 实测 y=228.4 + h=21.6
    }

    [Fact]
    public void JustifySelf_shrinks_the_item_to_fit_content()
    {
        var j1 = Item(5);
        var j2 = Item(6);
        var j3 = Item(7);
        Assert.True(j1.W < 40, $"justify-self:start 主轴没收缩：w={j1.W}");   // 实测 12.1 / 12.9
        Assert.Equal(0, j1.X, 1);
        Assert.Equal(210 + (200 - j2.W) / 2, j2.X, 1);                        // 实测 x=303.9
        Assert.Equal(200, j3.X + j3.W, 1);                                     // 实测 x=187.1
        Assert.Equal(120, j1.H, 1);                                            // 交叉轴照旧 stretch
    }

    [Fact]
    public void PlaceSelf_shorthand_drives_both_axes()
    {
        var p1 = Item(8);
        // 实测 x=301.6 y=439.2（第 4 行顶 390，第二列 210）
        Assert.Equal(210 + (200 - p1.W) / 2, p1.X, 1);
        Assert.Equal(390 + (120 - p1.H) / 2, p1.Y, 1);
    }

    [Fact]
    public void Fixed_height_item_sizes_its_auto_row()
    {
        var f1 = Item(9);
        var f2 = Item(10);
        // 实测 f1 y=520 h=40（align-self:start + 定高），f2 同轨被 stretch 到 40
        Assert.Equal(520, f1.Y, 1);
        Assert.Equal(40, f1.H, 1);
        Assert.Equal(40, f2.H, 1);
        Assert.Equal(80, f2.W, 1);
    }

    [Fact]
    public void Item_margin_counts_into_the_auto_track_and_is_not_double_counted()
    {
        var b1 = Item(11);
        var m1 = Item(12);
        // 实测：m1 的 10px 上边距撑出第 6 行 41.6 高，b1 在该轨里居中 → 两者同 y=580
        Assert.Equal(580, m1.Y, 1);
        Assert.Equal(230, m1.X, 1);   // 第 2 列 210 + margin-left 20
        Assert.Equal(580, b1.Y, 1);
    }

    [Fact]
    public void Container_items_center_can_be_overridden_by_self_stretch()
    {
        var a0 = Box["body[1]/div[2]/div[1]"];
        var a1 = Box["body[1]/div[2]/div[3]"];
        Assert.True(a0.H < 40 && a0.W < 200, $"align-items/justify-items:center 没生效：{a0.W}×{a0.H}");
        Assert.Equal(120, a1.H, 1);   // 实测子项 align-self:stretch 压回容器默认
        Assert.Equal(200, a1.W, 1);
    }

    [Fact]
    public void Explicit_tracks_without_items_still_occupy_space()
    {
        // 模板写了 4 行 120px，第二个 grid 只放了 3 个子项（占 2 行）——浏览器仍按 4 行算高 510。
        Assert.Equal(4 * 120 + 3 * 10, Box["body[1]/div[2]"].H, 1);
    }

    [Fact]
    public void Container_min_height_grows_auto_tracks_before_items_center()
    {
        // pricing-mid 的 .r{min-height:105px;align-items:center}：轨道得先吃到 min-height 的富余，
        // 格子里的 22 高单元才有地方居中。此前轨道停在内容高 → 整行格子贴顶，一列下来偏 41.5px。
        var css = ".g{display:grid;grid-template-columns:100px 100px;min-height:120px;align-items:center}"
                + ".i{height:20px}";
        var b = Renderer.RenderRasterWithBoxes("<div class='g'><div class='i'>a</div><div class='i'>b</div></div>", css, 600)
            .Boxes.Single(x => x.DomPath == "body[1]/div[1]/div[1]");
        Assert.Equal(50, b.Y, 1);   // (120 - 20) / 2
    }

    [Fact]
    public void AlignContent_center_and_end_offset_the_row_block()
    {
        // 轨道不涨，整块行往下挪：center 留一半，end 全留（_gsel 的 .g3/.g5 实测同值）。
        var css = ".c{display:grid;height:200px;grid-template-rows:60px;grid-template-columns:100px;align-content:center}"
                + ".e{display:grid;height:200px;grid-template-rows:60px;grid-template-columns:100px;align-content:end}"
                + ".i{height:20px}";
        var boxes = Renderer.RenderRasterWithBoxes(
            "<div class='c'><div class='i'>a</div></div><div class='e'><div class='i'>b</div></div>", css, 600).Boxes;
        Assert.Equal(70, boxes.Single(b => b.DomPath == "body[1]/div[1]/div[1]").Y, 1);     // (200-60)/2
        Assert.Equal(340, boxes.Single(b => b.DomPath == "body[1]/div[2]/div[1]").Y, 1);    // 第二个容器顶 200 + 留白 140
    }

    [Fact]
    public void AngleSharp_dropped_alignment_keywords_are_recovered_from_raw_css()
    {
        // 这三条声明 AngleSharp 会在解析期整条丢掉，靠 StyleResolver 的原始 CSS 兜底表捞回；
        // 丢了就静默退回 stretch（横轴拉满 100、纵轴贴顶）。
        var css = ".c{display:grid;grid-template-columns:100px;grid-template-rows:200px}"
                + ".i{align-self:end;justify-self:start;width:30px;height:40px}";
        var box = Renderer.RenderRasterWithBoxes("<div class='c'><div class='i'>x</div></div>", css, 600)
            .Boxes.Single(b => b.DomPath == "body[1]/div[1]/div[1]");
        Assert.Equal(30, box.W, 1);
        Assert.Equal(40, box.H, 1);
        Assert.Equal(160, box.Y, 1);
        Assert.Equal(0, box.X, 1);
    }
}
