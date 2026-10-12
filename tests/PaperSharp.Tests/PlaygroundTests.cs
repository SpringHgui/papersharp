using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PaperSharp;
using PaperSharp.Css;
using PaperSharpBlazorApp.Services;

namespace PaperSharp.Tests;

/// <summary>
/// Playground 的对位与预览文档拼装。这两件事都错得很安静：
/// 对位错 → Δ 表把 A 元素的偏差记到 B 头上，看着挺像回事但全是假的；
/// 层叠顺序错 → 浏览器窗格和引擎跑的不是同一份样式，差异表整片飘红。
/// 所以钉在单测里，不靠开浏览器肉眼确认。
/// </summary>
public class PlaygroundTests
{
    static DomRect R(string path, float x = 0, float y = 0, float w = 100, float h = 20)
        => new() { DomPath = path, Tag = "div", X = x, Y = y, W = w, H = h };

    static BoxRect E(string path, float x = 0, float y = 0, float w = 100, float h = 20)
        => new(path, "div", x, y, w, h);

    [Fact]
    public void Pairs_by_dom_path_not_by_position_in_the_list()
    {
        // 引擎按盒树深度优先，浏览器按 DOM 顺序；两者对同一棵树的展平次序不保证一致。
        var engine = new[] { E("a[1]", x: 0), E("a[1]/b[1]", x: 10) };
        var browser = new[] { R("a[1]/b[1]", x: 13), R("a[1]", x: 1) };

        var rows = PlaygroundDiff.Compare(engine, browser, 0.5f).Rows;

        Assert.Equal(2, rows.Count);
        var nested = rows.Single(r => r.DomPath == "a[1]/b[1]");
        Assert.Equal(3f, nested.Dx);                       // 浏览器 − 引擎
        Assert.Equal(1f, rows.Single(r => r.DomPath == "a[1]").Dx);
    }

    [Theory]
    [InlineData(0.5f, 0.5f, DiffKind.Ok)]                 // 恰好等于容差 → 不算漂移（取整噪声就该被吃掉）
    [InlineData(0.5f, 0.51f, DiffKind.Drift)]
    [InlineData(2f, 1.4f, DiffKind.Ok)]
    [InlineData(2f, 2.4f, DiffKind.Drift)]
    public void Tolerance_decides_ok_vs_drift(float tol, float delta, DiffKind want)
    {
        var got = PlaygroundDiff.Compare(new[] { E("p[1]", y: 0) }, new[] { R("p[1]", y: delta) }, tol);
        Assert.Equal(want, got.Rows.Count == 0 ? DiffKind.Ok : got.Rows[0].Kind);
        Assert.Equal(want == DiffKind.Ok ? 0 : 1, got.Summary.Drift);
    }

    [Fact]
    public void One_sided_boxes_are_reported_as_two_distinct_kinds()
    {
        var got = PlaygroundDiff.Compare(
            new[] { E("only-engine[1]") },
            new[] { R("only-browser[1]") },
            0.5f);

        Assert.Equal(DiffKind.EngineOnly, got.Rows.Single(r => r.DomPath == "only-engine[1]").Kind);
        Assert.Equal(DiffKind.BrowserOnly, got.Rows.Single(r => r.DomPath == "only-browser[1]").Kind);
        Assert.Equal(1, got.Summary.EngineOnly);
        Assert.Equal(1, got.Summary.BrowserOnly);
        // 单边缺盒没有 Δ 数值，但必须进表——「引擎压根没成盒」比「偏 3px」严重得多。
        Assert.Null(got.Rows.Single(r => r.Kind == DiffKind.EngineOnly).Browser);
    }

    [Fact]
    public void Worst_rows_come_first_and_the_summary_counts_every_row()
    {
        var engine = new[] { E("p[1]"), E("p[2]"), E("p[3]") };
        var browser = new[] { R("p[1]", y: 1), R("p[2]", y: 40), R("p[3]", y: 9) };

        var got = PlaygroundDiff.Compare(engine, browser, 0.5f);

        Assert.Equal(new[] { "p[2]", "p[3]", "p[1]" }, got.Rows.Select(r => r.DomPath));
        Assert.Equal(3, got.Summary.Drift);
        Assert.Equal(40f, got.Summary.WorstDelta);
        Assert.Equal((1 + 40 + 9) / 3.0, got.Summary.MeanDriftPx, 3);
    }

    [Fact]
    public void Returned_rows_are_capped_but_the_summary_is_not()
    {
        var engine = Enumerable.Range(0, PlaygroundDiff.MaxReturnedRows + 50).Select(i => E($"p[{i}]")).ToArray();
        var browser = engine.Select(b => R(b.DomPath, y: 1)).ToArray();

        var got = PlaygroundDiff.Compare(engine, browser, 0.5f);

        Assert.Equal(PlaygroundDiff.MaxReturnedRows, got.Rows.Count);
        Assert.Equal(engine.Length, got.Summary.Total);
        Assert.Equal(engine.Length, got.Summary.Drift);
    }

    // ── 预览文档 ────────────────────────────────────────────────────────

    /// <summary>
    /// UA 表 &lt; 附加 CSS &lt; 文档内 &lt;style&gt;。与 StyleResolver 拼
    /// <c>&lt;style&gt;UA&lt;/style&gt;&lt;style&gt;附加+内嵌&lt;/style&gt;</c> 的次序一致，
    /// 错一位就不是在比同一份层叠结果了。
    /// </summary>
    [Fact]
    public void Preview_doc_keeps_the_engine_cascade_order()
    {
        var html = "<head><style>p{color:blue}</style></head><body><p>x</p></body>";
        var doc = PlaygroundService.BuildPreviewDoc(html, "p{color:red}");

        var ua = doc.IndexOf(StyleResolver.UaStylesheet[..24], StringComparison.Ordinal);
        var extra = doc.IndexOf("p{color:red}", StringComparison.Ordinal);
        var own = doc.IndexOf("p{color:blue}", StringComparison.Ordinal);
        Assert.InRange(ua, 0, doc.Length);
        Assert.True(ua < extra, $"UA 表必须在附加 CSS 之前：{ua} vs {extra}");
        Assert.True(extra < own, $"附加 CSS 必须在文档内 <style> 之前：{extra} vs {own}");
    }

    [Fact]
    public void Preview_doc_wraps_a_bare_fragment_like_the_engine_does()
    {
        var doc = PlaygroundService.BuildPreviewDoc("<p>hi</p>", "");
        Assert.StartsWith("<!doctype html>", doc);
        Assert.Contains("<body><p>hi</p></body>", doc);
    }

    [Fact]
    public void Preview_doc_injects_a_head_when_the_document_only_has_a_body()
    {
        var doc = PlaygroundService.BuildPreviewDoc("<body><p>hi</p></body>", "");
        Assert.True(doc.IndexOf("<head>", StringComparison.Ordinal) < doc.IndexOf("<body", StringComparison.Ordinal));
    }

    /// <summary>滚动条宽度必须归零：否则浏览器视口比引擎排版宽凭空少一截，每个元素都报宽度漂移。</summary>
    [Fact]
    public void Preview_doc_hides_scrollbars_so_the_viewport_matches_the_layout_width()
    {
        var doc = PlaygroundService.BuildPreviewDoc("<p>hi</p>", "");
        Assert.Contains("::-webkit-scrollbar", doc);
        Assert.Contains("scrollbar-width:none", doc);
    }

    // ── PDF 悬浮窗 ──────────────────────────────────────────────────────

    /// <summary>
    /// 同宽档的全部意义在于「PDF 里的折行 = 两窗里的折行」：页面宽按长图那一次的画布宽钉死，
    /// 但只能覆盖 size——用户自己 @page 里的 margin 与页面高还得留着，否则悬浮窗看着对，
    /// 拿出去导真 PDF 又不是一回事。
    /// </summary>
    [Fact]
    public void Same_width_paper_overrides_only_the_page_size()
    {
        var css = PlaygroundService.BuildPdfCss("@page{size:a2;margin:20mm 15mm}", 1284f, PdfPaper.SameWidth);
        var spec = AtPageParser.Parse(css, 595.28f, 841.89f);
        var mV = AtPageParser.ToCssPx("20mm");
        var mH = AtPageParser.ToCssPx("15mm");

        Assert.Equal(1284f, spec.PageW);                    // a2 被同宽档换掉
        Assert.Equal(841.89f, spec.PageH);                  // 高仍按 A4：分页位置才可比
        Assert.Equal(mV, spec.MarginTop);                   // margin 与页面高都得留着
        Assert.Equal(mV, spec.MarginBottom);
        Assert.Equal(mH, spec.MarginLeft);
        Assert.Equal(1284f - mH - mH, spec.ContentW);        // 与长图口径同一个排版宽
    }

    [Theory]
    [InlineData(PdfPaper.A4)]
    [InlineData(PdfPaper.Letter)]
    public void Fixed_papers_leave_the_css_alone(PdfPaper paper)
        => Assert.Equal("@page{margin:1in}", PlaygroundService.BuildPdfCss("@page{margin:1in}", 1284f, paper));

    /// <summary>注入的 @page 必须真的落到页面几何上，而不只是解析器认——MediaBox 是成品 PDF 的口径。</summary>
    [Fact]
    public void Same_width_pdf_actually_lands_on_the_requested_page_width()
    {
        var css = PlaygroundService.BuildPdfCss("", 1284f, PdfPaper.SameWidth);
        var pdf = Renderer.RenderPdf("<div style=\"width:100%;height:10px;background:red\"></div>", css);

        var m = Regex.Match(Encoding.ASCII.GetString(pdf), @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)");
        Assert.True(m.Success, "PDF 里没找到 MediaBox");
        Assert.Equal(1284, (int)float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
    }
}
