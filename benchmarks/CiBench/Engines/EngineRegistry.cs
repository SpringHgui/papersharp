namespace CiBench.Engines;

public static class EngineRegistry
{
    static readonly IReadOnlyList<EngineDescriptor> AllDescriptors = new List<EngineDescriptor>
    {
        // 默认四家（见 BenchOptions.Engines）：纯托管自绘 / CDP 直连 / Playwright driver / 解析型布局。
        new("papersharp", "PaperSharp", "纯托管 CSS 排版 + Skia（无浏览器）", () => new PaperSharpEngine()),
        new("puppeteer", "PuppeteerSharp", "Chromium / Blink（CDP 外部进程）", () => new PuppeteerSharpEngine()),
        new("playwright", "Microsoft.Playwright", "Chromium / Blink（Playwright driver + 外部 Chrome）",
            () => new PlaywrightEngine()),
#if ITEXT
        // iText pdfHTML 为 AGPLv3 / 商业双授权，默认参赛但可用 -p:IncludeIText=false 移除依赖。
        new("itext", "iText 7 pdfHTML", "解析型 HTML/CSS 布局（纯托管，无浏览器）", () => new ITextEngine()),
#else
        new("itext", "iText 7 pdfHTML", "解析型 HTML/CSS 布局（纯托管，无浏览器）",
            () => new UnavailableEngine("itext", "iText 7 pdfHTML", "解析型 HTML/CSS 布局（纯托管，无浏览器）",
                "编译时已排除 iText（-p:IncludeIText=false）；去掉该开关重编译即可参赛")),
#endif
        // IronPdf 是商业库，已从默认组合移除；只有显式 --engines ironpdf 且编译时打开开关才参赛。
#if IRONPDF
        new("ironpdf", "IronPdf", "Chromium / Blink（随包捆绑）", () => new IronPdfEngine()),
#else
        new("ironpdf", "IronPdf", "Chromium / Blink（随包捆绑）",
            () => new UnavailableEngine("ironpdf", "IronPdf", "Chromium / Blink（随包捆绑）",
                "IronPdf 已从默认组合移除；如需参赛请 -p:IncludeIronPdf=true 重编译并配置 IRONPDF_LICENSE_KEY")),
#endif
    };

    public static IReadOnlyList<EngineDescriptor> All => AllDescriptors;

    public static EngineDescriptor? Find(string name) =>
        AllDescriptors.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>按用户指定顺序解析，未知名字直接报错，避免 CI 里静默少跑一家。</summary>
    public static IReadOnlyList<EngineDescriptor> Resolve(IEnumerable<string> names)
    {
        var list = new List<EngineDescriptor>();
        foreach (var n in names)
        {
            var d = Find(n) ?? throw new ArgumentException(
                $"未知引擎 '{n}'。可用: {string.Join(", ", AllDescriptors.Select(x => x.Name))}");
            if (!list.Contains(d)) list.Add(d);
        }
        return list;
    }
}

/// <summary>编译期缺库 / 环境不满足时的占位引擎：如实给出 skip 原因，不伪造数据。</summary>
public sealed class UnavailableEngine : IPdfEngine
{
    readonly string _reason;
    public UnavailableEngine(string name, string display, string renderingEngine, string reason)
    {
        Name = name; Display = display; RenderingEngine = renderingEngine; _reason = reason;
    }
    public string Name { get; }
    public string Display { get; }
    public string RenderingEngine { get; }
    public string Version => "n/a";
    public string? Preflight() => _reason;
    public Task InitializeAsync(string html, string baseDir, CancellationToken ct) =>
        throw new InvalidOperationException(_reason);
    public Task<IPdfWorker> CreateWorkerAsync(string html, string baseDir, CancellationToken ct) =>
        throw new InvalidOperationException(_reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
