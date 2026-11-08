using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace CiBench.Engines;

/// <summary>
/// 一个进程只拉起一个 Chromium（热态的真实形态），每个并发 worker 独占一个 IPage。
/// 每次 RenderAsync 都重新 SetContent + PdfDataAsync，与另外两家「每次重新排版」对齐，
/// 不复用已排好版的页面。
/// </summary>
public sealed class PuppeteerSharpEngine : IPdfEngine
{
    IBrowser? _browser;
    string? _html;

    static readonly PdfOptions Pdf = new()
    {
        Format = PaperFormat.A4,
        PrintBackground = true,
        PreferCSSPageSize = true,
        MarginOptions = new MarginOptions { Top = "0", Bottom = "0", Left = "0", Right = "0" },
    };

    public string Name => "puppeteer";
    public string Display => "PuppeteerSharp";
    public string RenderingEngine => "Chromium / Blink（CDP 外部进程）";
    public string Version => typeof(Puppeteer).Assembly.GetName().Version?.ToString() ?? "unknown";

    // 找不到系统 Chrome 时交给 BrowserFetcher 下载，所以预检不判失败。
    public string? Preflight() => null;

    public async Task InitializeAsync(string html, string baseDir, CancellationToken ct)
    {
        _html = html;
        var chrome = ChromeLocator.TryFindPath();
        var opts = new LaunchOptions
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
            Console.Error.WriteLine("[puppeteer] 未找到系统 Chrome，下载 PuppeteerSharp 捆绑 Chromium…");
            await new BrowserFetcher().DownloadAsync();
        }
        _browser = await Puppeteer.LaunchAsync(opts);
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
            _browser.Dispose();
            _browser = null;
        }
    }

    sealed class Worker : IPdfWorker
    {
        readonly IPage _page;
        readonly string _html;
        public Worker(IPage page, string html) { _page = page; _html = html; }

        public async Task<byte[]> RenderAsync(CancellationToken ct)
        {
            // 无外部资源的自包含语料，用默认 load 等待即可；25.8 起带 NavigationOptions 的重载已弃用。
            await _page.SetContentAsync(_html);
            return await _page.PdfDataAsync(Pdf);
        }

        public async ValueTask DisposeAsync()
        {
            try { await _page.CloseAsync(); } catch { }
            _page.Dispose();
        }
    }
}
