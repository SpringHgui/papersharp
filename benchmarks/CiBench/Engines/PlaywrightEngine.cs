using Microsoft.Playwright;

namespace CiBench.Engines;

/// <summary>
/// Microsoft.Playwright（Apache-2.0，免费）：Playwright driver + headless Chromium。
/// 替代商业库 IronPdf 成为默认组合之一——同样是一条 API「HTML 字符串 → PDF byte[]」，
/// 但没有 license 门槛，CI 里不会因为缺 key 只出两家数据。
///
/// 浏览器复用系统 Chrome（与 PuppeteerSharp 同一个二进制），避免再下载 ~400MB 的 Playwright Chromium；
/// 真找不到时才回退到 Playwright 自带浏览器（需事先 playwright install chromium）。
/// 一个进程只拉起一个 browser，每个并发 worker 独占一个 page；每次渲染重新 SetContent + PdfAsync，
/// 与另外两家「每次从 HTML 重新排版」口径一致。
/// </summary>
public sealed class PlaywrightEngine : IPdfEngine
{
    IPlaywright? _playwright;
    IBrowser? _browser;

    static readonly PagePdfOptions Pdf = new()
    {
        Format = "A4",
        PrintBackground = true,
        PreferCSSPageSize = true,
        Margin = new Margin { Top = "0", Bottom = "0", Left = "0", Right = "0" },
    };

    public string Name => "playwright";
    public string Display => "Microsoft.Playwright";
    public string RenderingEngine => "Chromium / Blink（Playwright driver + 外部 Chrome）";
    public string Version => typeof(Playwright).Assembly.GetName().Version?.ToString() ?? "unknown";

    // 免费开源，无需 license 预检；浏览器可用性留给 InitializeAsync 给出真实错误。
    public string? Preflight() => null;

    public async Task InitializeAsync(string html, string baseDir, CancellationToken ct)
    {
        _playwright = await Playwright.CreateAsync();
        var chrome = ChromeLocator.TryFindPath();
        var opts = new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage", "--disable-gpu"],
        };
        if (chrome is not null)
        {
            opts.ExecutablePath = chrome;
        }
        else
        {
            Console.Error.WriteLine("[playwright] 未找到系统 Chrome，改用 Playwright 自带 Chromium" +
                                    "（需先执行 playwright install chromium）");
        }
        _browser = await _playwright.Chromium.LaunchAsync(opts);
    }

    public async Task<IPdfWorker> CreateWorkerAsync(string html, string baseDir, CancellationToken ct)
    {
        if (_browser is null) throw new InvalidOperationException("InitializeAsync 未调用");
        var page = await _browser.NewPageAsync();
        return new Worker(page, html);
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); } catch { }
            _browser = null;
        }
        try { _playwright?.Dispose(); } catch { }
        _playwright = null;
    }

    sealed class Worker : IPdfWorker
    {
        readonly IPage _page;
        readonly string _html;
        public Worker(IPage page, string html) { _page = page; _html = html; }

        public async Task<byte[]> RenderAsync(CancellationToken ct)
        {
            // 语料自包含、无外部资源；不设 Path 时 PdfAsync 直接返回 byte[]，由测量内核统一落盘。
            await _page.SetContentAsync(_html);
            return await _page.PdfAsync(Pdf);
        }

        public async ValueTask DisposeAsync()
        {
            try { await _page.CloseAsync(); } catch { }
        }
    }
}
