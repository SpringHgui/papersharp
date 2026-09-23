using UglyToad.PdfPig;

namespace PaperSharp.Tests;

/// <summary>结构层：PDF 可解析、页数、页面尺寸、体积上限。页数钉死 = 分页器回归哨兵。</summary>
public class PdfStructureTests
{
    // 探针实测于 2026-09-22（master 551a72e 之后）。布局/分页有意变更后 UPDATE 基准并重跑探针。
    public static TheoryData<string, int> ExpectedPages => new()
    {
        { "invoice", 1 }, { "long", 2 }, { "flex", 1 }, { "img", 1 }, { "report", 2 }, { "pct", 2 },
        { "polish", 2 }, { "inline", 2 }, { "table", 1 }, { "m1", 11 }, { "m2", 5 }, { "m3", 3 },
        { "m4", 2 }, { "m5", 2 },
    };

    [Theory]
    [MemberData(nameof(ExpectedPages))]
    public void Pdf_structure_matches_golden_facts(string name, int pages)
    {
        var pdf = Fixture.RenderPdf(name);

        Assert.Equal((byte)'%', pdf[0]); // %PDF 头
        Assert.True(pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8));

        using var doc = PdfDocument.Open(pdf); // 解析失败即抛：兜住 PdfEnhancer 手写 xref/trailer 写坏
        Assert.Equal(pages, doc.NumberOfPages);

        foreach (var page in doc.GetPages())
        {
            // 全部样例默认 A4（595.28×841.89pt，PdfPig 给 595.0/842.0）
            Assert.Equal(595.0, page.Width, 1);
            Assert.Equal(842.0, page.Height, 1);
        }
    }

    [Theory]
    [MemberData(nameof(ExpectedPages))]
    public void Pdf_size_within_budget(string name, int pages)
    {
        var pdf = Fixture.RenderPdf(name);
        // 字体子集化失效（整份雅黑 ~20MB 嵌入）会被这条第一时间抓住；上限留了 m4 多字体的余量
        Assert.True(pdf.Length < 6 * 1024 * 1024, $"{name}.pdf = {pdf.Length / 1024}KB，超过 6MB 预算，疑似子集字体失效");
    }
}
