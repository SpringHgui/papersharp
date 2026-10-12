using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// 「度量端口径 == 绘制端推进」这条恒等式的切分半边。
///
/// 度量端（<see cref="TextMeasurer"/> / <see cref="TextShaper.WidthOf"/>）在 hmtx 之外还要加字体自带的
/// 字距对（<see cref="KernTable.Adjust"/>），而 Skia 的一次 DrawText **只按 hmtx 走位**。绘制端要补这块，
/// 补法是把串在「非零字距对」处切开、逐段落笔并按 <c>段宽 + 该对调整量</c> 推进
/// （<see cref="KernTable.KernChunks"/>，TextPainter.DrawSpaced 就是照它走的）。
/// 于是恒等式是：Σ段宽 = 整串 hmtx（Skia 的 MeasureText 本就是 hmtx 求和，天然可加）
/// 且 **ΣTail 必须 == Adjust(整串)** —— 后者才是会漏的那一半：两边各查各的表，
/// 一旦查表规则（Limit 闸门、ASCII 快路、码位 vs 字素）漂开，就又回到「量得窄、画得宽」。
///
/// 漂开的代价实测过：invoice 的 <c>To: 三花股份有限公司 · 采购部</c>，"To" 那一对 −1.56px，
/// 绘制端不补，配对之后的每个字形都比布局以为的位置更往右 —— 整段中文在 Playground 叠加视图里
/// 重影 1.5px（盒级对拍量不出来，每个盒的矩形都对）。同页 From/Date 无配对、Widget A 的配对为 0，
/// 所以只有那一行糊。修好后该行逐像素差 60.1% → 0.0%（tools 侧 pdiff，见 PixelDiff）。
///
/// 这里刻意不碰 Skia 类型：测试工程经 Svg.Skia 把 SkiaSharp 抬到 4.x，而类库按 2.88 编译，
/// 在测试里直接使 SKPaint 的成员会编不过（Typeface/TextSize 已迁到 SKFont）。
/// </summary>
public class KernPaintParityTests
{
    const float Size = 14f;
    const string Reported = "To: 三花股份有限公司 · 采购部";

    static readonly string[] Corpus =
    {
        Reported,
        "From: Acme GmbH · München",
        "Widget A · 精密零件 × 120 @ ¥12.50 = ¥1,500.00",
        "Total 合计: ¥2,760.00",
        "AVoWyA! To: 三花股份有限公司 · 采购部 — Powered by PaperSharp · AngleSharp.Css",
        "备注 Notes：本单据由 PaperSharp demo 生成，同一份 HTML+CSS 通过 SkiaSharp 输出为 PDF 与 PNG。",
        "Wave offline — TO: 三花",
    };

    static KernTable Table() => KernTable.For(FontManager.Get("Microsoft YaHei", 400))
        ?? throw new InvalidOperationException("雅黑子集没带字距表：ResolveFallback 的子集装载或 PRKT 登记出问题了，断言会空跑");

    /// <summary>闸门不能空跑：报告里那一对必须真的在表里、且是当初造出重影的量级。</summary>
    [Fact]
    public void Yahei_carries_the_To_pair_this_bug_is_about()
    {
        var kt = Table();
        float to = kt.Pair('T', 'o', Size);
        Assert.True(to < -1f, $"\"To\" 那一对字距实测 {to:0.###}px，量级已变——请连同绘制端一起复核");
        Assert.True(kt.KernChunks(Reported, Size).Count > 1, "报告串没被切开，绘制端仍会整串一次落笔");
    }

    /// <summary>核心恒等式：逐段推进的字距合计 == 度量端整串 Adjust。</summary>
    public static IEnumerable<object[]> CorpusCases() => Corpus.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(CorpusCases))]
    public void Chunk_tails_sum_to_the_measured_adjustment(string s)
    {
        var kt = Table();
        Assert.Equal(kt.Adjust(s, Size), kt.KernChunks(s, Size).Sum(c => c.Tail), 4);
    }

    /// <summary>切分不许丢字、重字，也不许在码位中间下刀（代理对劈开就是乱码）。</summary>
    [Theory]
    [MemberData(nameof(CorpusCases))]
    public void Chunks_tile_the_string_on_codepoint_boundaries(string s)
    {
        var segs = Table().KernChunks(s, Size);
        Assert.Equal(s, string.Concat(segs.Select(c => c.Seg)));
        Assert.All(segs, c => Assert.False(c.Seg.Length > 0 && char.IsHighSurrogate(c.Seg[^1]), $"段尾留下半个代理对：{c.Seg}"));
    }

    /// <summary>全宽汉字/中文标点不参与配对（Limit 之上表里本就没有条目），切刀只能落在拉丁段里。</summary>
    [Fact]
    public void Cjk_never_gets_a_pair()
    {
        var kt = Table();
        Assert.Equal(0f, kt.Pair('花', '股', Size));
        Assert.Equal(0f, kt.Pair('：', '本', Size));
        Assert.Equal(0f, kt.Pair('o', '花', Size));   // 拉丁→汉字跨界那一对两边都不许算
        Assert.All(Reported.Zip(Reported.Skip(1), (a, b) => (a, b)).Where(p => p.b >= 0x2E80),
            p => Assert.Equal(0f, kt.Pair(p.a, p.b, Size)));
    }

    /// <summary>没有配对的串必须原样一段返回（快路不因此多一次落笔），且尾量为 0。</summary>
    [Fact]
    public void Unkerned_text_stays_a_single_chunk()
    {
        var kt = Table();
        const string s = "一二三四五六七八九十";
        var segs = kt.KernChunks(s, Size);
        Assert.Single(segs);
        Assert.Same(s, segs[0].Seg);
        Assert.Equal(0f, segs[0].Tail);
    }
}
