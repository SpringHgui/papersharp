using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// 表格 auto layout 里「写了 width 的列」（CSS 2.1 §17.5.2.2 的 special column）。
///
/// 引擎此前把声明宽当**下限**与 max-content 取大，等于让内容反过来吃掉比例列，
/// 再把所有列一起按比例缩放到目标宽 → 定宽列被撑歪、auto 列被挤没。
/// pricing/m3 对拍实测：40% 列里放 46 个汉字，浏览器仍是 319.6（=40%），其余 60% 全归 auto 列；
/// 引擎给 768.6/31.4，整表错位。m3 由此产生的单格偏差 337px。
/// </summary>
public class TableSpecifiedColumnTests
{
    const string Css = """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font: 15px/1.4 Arial, sans-serif; }
        table { border-collapse: collapse; }
        td { border: 1px solid #888; padding: 6px; }
        .t { width: 100%; }
        .p40 { width: 40%; }
        .w200 { width: 200px; }
        """;

    static readonly string Long = new string('中', 46);

    static (float A, float B) TwoCols(string html, float width = 800)
    {
        var boxes = Renderer.RenderRasterWithBoxes(html, Css, width).Boxes;
        float W(string path) => boxes.Single(b => b.DomPath == path).W;
        return (W("body[1]/table[1]/tbody[1]/tr[1]/td[1]"), W("body[1]/table[1]/tbody[1]/tr[1]/td[2]"));
    }

    [Fact]
    public void Percentage_column_keeps_its_share_even_when_content_is_wider()
    {
        // 无头 Edge 实测（同一份标记放进 800px 容器）：td1 = 319.59375、td2 = 479.40625。
        // 净宽不是 800 而是 799：collapse 表的外层网格线被单元格边框骑跨，格区四边各内缩半框（见 :306 注释）。
        var (a, b) = TwoCols($"<table class=t><tr><td class=p40>{Long}</td><td>短</td></tr></table>");
        Assert.Equal(319.6f, a, 1);
        Assert.Equal(479.4f, b, 1);
    }

    [Fact]
    public void Px_column_is_not_rescaled_by_the_fit_to_available_width()
    {
        // 实测：td1 = 200 原样，td2 = 599 拿走余量（799 净宽扣掉定宽列）。缩放只作用于 auto 列。
        var (a, b) = TwoCols("<table class=t><tr><td class=w200>px 列内容</td><td>auto 列内容 auto 列内容</td></tr></table>");
        Assert.Equal(200, a, 1);
        Assert.Equal(599, b, 1);
    }

    /// <summary>
    /// PDF 路径会把表格量两遍（Renderer 一遍、Paginator 起手一遍）。测量期给单元格设的
    /// <c>width:auto</c> 若不给回声明值，第二遍就读不到定宽列，PDF 退回按内容分宽——
    /// 而 raster 只量一遍，金标与单测全绿也发现不了（用户报的「td width 不生效」就是这条）。
    /// </summary>
    [Fact]
    public void Specified_cell_width_survives_the_paginator_second_measure()
    {
        var body = LayoutBuilder.ToBoxTree(StyleResolver.BuildDocument(
            "<table class=t><tr><td class=w200>px 列内容</td><td>auto 列内容 auto 列内容</td></tr></table>", Css));
        LayoutBuilder.Measure(body, 800);
        var table = Find(body, b => b.Style.Display == "table")!;
        float first = table.TColW![0];

        LayoutBuilder.Measure(table, 800);   // Paginator.Paginate 的第二次测量
        Assert.Equal(first, table.TColW![0], 1);
        Assert.InRange(table.TColW![0], 199f, 201f);
        Assert.True(table.TColW![1] > 500f, $"auto 列没拿到余量：{table.TColW[1]}");
    }

    [Fact]
    public void Colgroup_column_widths_are_honored()
    {
        var (a, b, c) = ThreeCols(
            "<table class=t><colgroup><col style='width:7mm'><col style='width:20mm'><col></colgroup>" +
            "<tr><td>A</td><td>B</td><td>C</td></tr></table>");

        // 7mm = 26.46px、20mm = 75.59px：col 的 width 就是列宽本身，不再叠单元格 border/padding。
        Assert.Equal(26.46f, a, 0.5f);
        Assert.Equal(75.59f, b, 0.5f);
        Assert.True(c > 600f, $"第三列没拿到余量：{c}");
    }

    [Fact]
    public void Col_span_puts_one_width_on_several_columns()
    {
        var (a, b, c) = ThreeCols(
            "<table class=t><colgroup><col span=2 style='width:100px'><col></colgroup>" +
            "<tr><td>A</td><td>B</td><td>C</td></tr></table>");

        Assert.Equal(100f, a, 1);
        Assert.Equal(100f, b, 1);
        Assert.True(c > 500f, $"第三列没拿到余量：{c}");
    }

    static (float A, float B, float C) ThreeCols(string html)
    {
        var boxes = Renderer.RenderRasterWithBoxes(html, Css, 800).Boxes;
        float W(string path) => boxes.Single(b => b.DomPath == path).W;
        return (W("body[1]/table[1]/tbody[1]/tr[1]/td[1]"),
                W("body[1]/table[1]/tbody[1]/tr[1]/td[2]"),
                W("body[1]/table[1]/tbody[1]/tr[1]/td[3]"));
    }

    static float[] ColWidths(string html, int n, float width = 800)
    {
        var boxes = Renderer.RenderRasterWithBoxes(html, Css, width).Boxes;
        return Enumerable.Range(1, n)
            .Select(i => boxes.Single(b => b.DomPath == $"body[1]/table[1]/tbody[1]/tr[1]/td[{i}]").W)
            .ToArray();
    }

    /// <summary>
    /// 全部列都是定宽、表又比声明宽总和更宽时，余量按**各自声明宽**等比放大
    /// （Blink ColumnWidthWeight）。此前余量在各定宽列间均分，60/120/180/240 会变成 200/200/200/200，
    /// 列比例整体塌掉——用户看到的「宽度不太准确」有一半来自这里。
    /// </summary>
    [Fact]
    public void All_fixed_columns_scale_in_proportion_to_their_declared_width()
    {
        var w = ColWidths(
            "<table class=t><tr><td style='width:60px'>a</td><td style='width:120px'>b</td>" +
            "<td style='width:180px'>c</td><td style='width:240px'>d</td></tr></table>", 4);

        // 无头 Edge 实测 @800：80 / 160 / 240 / 320（= 净宽 799 按 1:2:3:4 放大）。
        Assert.Equal(2f, w[1] / w[0], 1);
        Assert.Equal(3f, w[2] / w[0], 1);
        Assert.Equal(4f, w[3] / w[0], 1);
        Assert.Equal(799, w.Sum(), 2);
    }

    /// <summary>
    /// 表比声明宽总和更窄时，Chrome 先把定宽列压向内容 min-content（表格照常铺满容器），
    /// 而不是抱着声明宽让整表溢出。旧逻辑用声明宽当 min-content 下限，于是永远压不动。
    /// </summary>
    [Fact]
    public void Fixed_columns_shrink_below_declared_width_when_the_table_is_narrow()
    {
        var w = ColWidths(
            "<table><tr><td style='width:200px'>甲</td><td style='width:200px'>乙</td></tr></table>", 2, width: 300);

        Assert.True(w[0] < 200f && w[1] < 200f, $"定宽列没被压缩：{w[0]} / {w[1]}");
        Assert.Equal(w[0], w[1], 0.5f);
        Assert.Equal(299, w.Sum(), 2);
    }

    /// <summary>
    /// 定宽列的 preferred 宽只看声明值：单元格里塞长文本、自动换行也不该把它顶宽。
    /// 用户提的「会不会是自动换行撑坏原本的分配」即这条——此前用 max-content 与声明宽取大，
    /// 长内容列会把 auto 列挤没。
    /// </summary>
    [Fact]
    public void Long_wrapping_content_does_not_widen_a_fixed_column()
    {
        var w = ColWidths(
            $"<table class=t><tr><td style='width:100px'>{Long}</td><td style='width:100px'>b</td>" +
            "<td style='width:300px'>c</td></tr></table>", 3);

        Assert.True(w[0] < 200f, $"定宽列被长内容顶宽：{w[0]}");
        Assert.Equal(1f, w[0] / w[1], 1);
        Assert.Equal(3f, w[2] / w[0], 1);
    }

    static BlockBox? Find(BlockBox box, Func<BlockBox, bool> pred)
    {
        if (pred(box)) return box;
        foreach (var ch in box.Children)
            if (Find(ch, pred) is { } hit) return hit;
        return null;
    }

    [Fact]
    public void All_auto_columns_still_share_by_max_content()
    {
        // 没有任何声明宽时保持旧行为：按内容宽比例分。实测 740.4 / 58.6（引擎 739.5 / 59.5，两列各差 0.93px），
        // 重点是别把定宽列的逻辑漏进来——所以先验比例，再验总宽恰好铺满 799。
        var (a, b) = TwoCols($"<table class=t><tr><td>{Long}</td><td>中中中</td></tr></table>");
        Assert.True(a > b * 8, $"两列分配比失衡：{a} vs {b}");
        Assert.Equal(799, a + b, 2);
    }
}
