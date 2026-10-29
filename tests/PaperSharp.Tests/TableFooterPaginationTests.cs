using PaperSharp;
using UglyToad.PdfPig;

namespace PaperSharp.Tests;

/// <summary>
/// tfoot 跨页续表：脚注每页重放，且与当页最后一行之间不留空档。
///
/// 引擎会把 body 量两遍（Renderer 先量整棵树，Paginator 起手又重量一遍），而 MeasureTable 里
/// THeaderRows/TFooterRows 是累加的：不清空就变成 [0,0] / [41,41]，FlowTable 每页把表头表尾各排两遍。
/// 多出的两行高度把每页挤掉一行正文，表尾与末行之间多出一个空行槽（用户报的空白间隔），
/// 表尾本身还被顶进下页边距。浏览器真值（本文件断言的数值来自 headless Edge printToPDF 对拍）：
/// 续页表尾就跟在当页最后一行之后一个行距处，不钉页底。
/// </summary>
public class TableFooterPaginationTests
{
    static string Html
    {
        get
        {
            var sb = new System.Text.StringBuilder(
                "<table><thead><tr><th>序</th><th>项目</th><th>金额</th></tr></thead><tbody>");
            for (int i = 1; i <= 40; i++)
                sb.Append($"<tr><td>B{i}</td><td>明细行 {i}</td><td>1,000.00</td></tr>");
            return sb.Append("</tbody><tfoot><tr><td colspan=\"2\">TOTAL</td><td>40,000.00</td></tr></tfoot></table>")
                     .ToString();
        }
    }

    const string Css = """
        @page { size: 595pt 842pt; margin: 40pt }
        body { font-family: "Microsoft YaHei", sans-serif; font-size: 10pt; margin: 0; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border: 0.5pt solid #666; padding: 4pt 6pt; }
        thead th { background: #e8f0f8; }
        tfoot td { background: #fdecec; }
        """;

    // 浏览器（headless Edge printToPDF）每页行集：首页表头 + B1..B33 + TOTAL，续页表头 + B34..B40 + TOTAL
    const float PageMarginPt = 40f;
    const float RowPitchPt = 21.4f;   // 本样例行距（Chrome 实测 21.8/21.0 交替，引擎 21.8/20.9）

    [Fact]
    public void Tfoot_repeats_and_sits_one_row_below_the_last_body_row()
    {
        using var doc = PdfDocument.Open(Renderer.RenderPdf(Html, Css));
        Assert.Equal(2, doc.NumberOfPages);

        var expectedFirst = new List<string> { "序项目金额" };
        for (int i = 1; i <= 33; i++) expectedFirst.Add(BodyRow(i));
        expectedFirst.Add(FooterRow());
        var expectedSecond = new List<string> { "序项目金额" };
        for (int i = 34; i <= 40; i++) expectedSecond.Add(BodyRow(i));
        expectedSecond.Add(FooterRow());

        AssertLines(doc.GetPage(1), expectedFirst);
        AssertLines(doc.GetPage(2), expectedSecond);
    }

    /// <summary>
    /// 表尾与当页最后一行之间只隔一个行距：修前多排一遍表头表尾，这里会差出两行（≈43pt），
    /// 视觉上就是用户看到的那道空白；同时表尾被顶进下页边距。
    /// </summary>
    [Fact]
    public void Tfoot_gap_is_one_row_pitch_and_stays_inside_the_printable_area()
    {
        using var doc = PdfDocument.Open(Renderer.RenderPdf(Html, Css));
        for (int p = 1; p <= doc.NumberOfPages; p++)
        {
            var lines = Baselines(doc.GetPage(p));
            int footer = lines.FindIndex(l => l.Text.StartsWith("TOTAL"));
            Assert.True(footer > 0, $"第 {p} 页没有表尾");
            float delta = lines[footer - 1].Y - lines[footer].Y;
            Assert.InRange(delta, RowPitchPt - 3, RowPitchPt + 3);
            Assert.True(lines[footer].Y > PageMarginPt + 4,
                $"第 {p} 页表尾基线 {lines[footer].Y:0.0}pt 落进了下页边距（内容底 {PageMarginPt}pt）");
        }
    }

    /// <summary>
    /// 根因守卫：量两遍后表头/表尾行号列表不能翻倍，否则 FlowTable 每页会各排两遍。
    /// </summary>
    [Fact]
    public void Measuring_twice_keeps_header_and_footer_row_lists_single()
    {
        var spec = PaperSharp.Css.AtPageParser.Parse(Css, 595.28f, 841.89f);
        var body = PaperSharp.Layout.LayoutBuilder.ToBoxTree(PaperSharp.Css.StyleResolver.BuildDocument(Html, Css));
        PaperSharp.Layout.LayoutBuilder.Measure(body, spec.ContentW);
        var table = Find(body, b => b.Style.Display == "table")!;
        PaperSharp.Layout.LayoutBuilder.Measure(table, spec.ContentW);

        Assert.Single(table.THeaderRows);
        Assert.Single(table.TFooterRows);
        Assert.Equal(table.TRowH![table.THeaderRows[0]], table.THeaderHeight);
        Assert.Equal(table.TRowH![table.TFooterRows[0]], table.TFooterHeight);
    }

    static string BodyRow(int i) => $"B{i}明细行 {i}1,000.00";
    static string FooterRow() => "TOTAL40,000.00";

    static void AssertLines(UglyToad.PdfPig.Content.Page page, IReadOnlyList<string> expected)
    {
        var actual = Baselines(page).Select(l => l.Text).ToList();
        Assert.Equal(expected, actual);
    }

    static List<(float Y, string Text)> Baselines(UglyToad.PdfPig.Content.Page page)
        => page.Letters
            .GroupBy(l => Math.Round(l.StartBaseLine.Y, 1))
            .OrderByDescending(g => g.Key)
            .Select(g => ((float)g.Key, string.Concat(g.Select(l => l.Value))))
            .ToList();

    static PaperSharp.Layout.BlockBox? Find(PaperSharp.Layout.BlockBox box, Func<PaperSharp.Layout.BlockBox, bool> pred)
    {
        if (pred(box)) return box;
        foreach (var ch in box.Children)
            if (Find(ch, pred) is { } hit) return hit;
        return null;
    }
}
