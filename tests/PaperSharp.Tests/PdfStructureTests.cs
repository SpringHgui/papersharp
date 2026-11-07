using UglyToad.PdfPig;

namespace PaperSharp.Tests;

/// <summary>结构层：PDF 可解析、页数、页面尺寸、体积上限。页数钉死 = 分页器回归哨兵。</summary>
public class PdfStructureTests
{
    // 探针实测于 2026-09-22（master 551a72e 之后）。布局/分页有意变更后 UPDATE 基准并重跑探针。
    // 2026-09-24 相邻兄弟 margin 坍缩修好后重探：pct 2→1、m2 5→4 已用 Chrome Page.printToPDF
    // 在同一纸张（595×842 CSS px，引擎的 1px=1pt 口径）上取真值对上；m1 11→10 只是内容变矮少一页，
    // 该页有固定宽度容器，Chrome 打印不会回流到 595，比不了，按引擎侧事实记。
    //
    // 2026-10-12 全列改按 **Chrome 真值**重探（headless Chrome + CDP printToPDF，
    // paperWidth/Height = A4、displayHeaderFooter:false，无 @page 者注入 @page{size:A4;margin:0}
    // 对齐引擎的全 0 默认边距；页数一律 pypdf 读 PDF 得到，脚本与产物在 .scratch/retention/）。
    // 上一批 px 单位制 + 1/64 折算落地后，long/inline/m1/m2/m3/m5/polish 七个样例各少一页，
    // 先怀疑塌页：逐样例量字符留存得 96.9~101.4%（内容没丢，是排版变矮），再取 Chrome 页数——
    // **七个样例三列（引擎边距 / Chrome 默认 1cm 边距 / UA body 8px 对照）全部等于引擎当前页数**，
    // 也就是说旧的那列数字是过期的引擎侧事实，m1 的 10 按其自注释从未被 Chrome 验过。
    // 已知最弱的一格：m3 页数与 Chrome 相同但首页有固定宽元素撑出版面（两边都在页外裁字），
    // 成因未必同源；字体也不同（Chrome 吃本机雅黑，引擎吃子集），不足以翻盘。
    public static TheoryData<string, int> ExpectedPages => new()
    {
        { "invoice", 1 }, { "long", 1 }, { "flex", 1 }, { "img", 1 }, { "report", 2 }, { "pct", 1 },
        { "polish", 1 }, { "inline", 1 }, { "table", 1 }, { "m1", 5 }, { "m2", 3 }, { "m3", 2 },
        { "m4", 2 }, { "m5", 1 },
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

    /// <summary>
    /// 连续渲染复用线程私有缓冲（<see cref="PaperSharp.Renderer"/> 的 EncodePdfPages / PdfFontSwap）：
    /// 交出去的数组必须归调用方独占，后续渲染不能把上一份覆写掉。
    /// 钉的是「PdfEnhancer 在没有书签/链接可加时会原样返回入参」那条路径——漏了补拷贝，
    /// 调用方手里的 PDF 会在下一份渲染后悄悄变样（且只在字节数恰好等于缓冲容量时才发作）。
    /// </summary>
    [Fact]
    public void RenderPdf_hands_out_caller_owned_buffer()
    {
        var first = PaperSharp.Renderer.RenderPdf("<p>alpha 甲</p>", "");
        var snapshot = first.ToArray();
        for (int i = 0; i < 6; i++) PaperSharp.Renderer.RenderPdf($"<p>beta 乙 {i}</p>", "");
        Assert.Equal(snapshot, first);   // 第一份不能被后来的渲染改写

        var a = PaperSharp.Renderer.RenderPdf("<p>gamma 丙</p>", "");
        var b = PaperSharp.Renderer.RenderPdf("<p>gamma 丙</p>", "");
        Assert.Equal(a, b);              // 同输入恒定产物
        Assert.NotSame(a, b);            // 但两份产物不能共用同一块缓冲
    }

    /// <summary>复用缓冲的交出口径：Enhance 原样返回入参时必须补拷贝，返回新数组时原样透传（零拷贝）。</summary>
    [Fact]
    public void EnsureCallerOwned_copies_only_when_result_aliases_the_pooled_input()
    {
        var pooled = new byte[32];
        pooled[0] = 0x25; pooled[3] = 0x37;   // "%PDF" 的头几个字节，示意

        var owned = PaperSharp.Renderer.EnsureCallerOwned(pooled, pooled, 4);
        Assert.NotSame(pooled, owned);       // 同一块缓冲 → 必须换一块
        Assert.Equal(4, owned.Length);       // 且只留有效长度
        Assert.Equal(new byte[] { 0x25, 0, 0, 0x37 }, owned);

        var fresh = new byte[4];
        Assert.Same(fresh, PaperSharp.Renderer.EnsureCallerOwned(fresh, pooled, 4));   // 新数组：零拷贝透传
    }
}
