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
