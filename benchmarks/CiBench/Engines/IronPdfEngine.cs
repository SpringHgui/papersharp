#if IRONPDF
namespace CiBench.Engines;

/// <summary>
/// IronPDF 捆绑自己的 Chromium。授权码只从环境变量读，绝不写进源码；
/// 缺 key 时父进程预检直接标 skipped，避免把试用版水印输出当成正式性能数据。
/// 每个并发 worker 持有一个独立 ChromePdfRenderer（与官方「每请求 new 一个 renderer」的用法一致）。
/// </summary>
public sealed class IronPdfEngine : IPdfEngine
{
    public string Name => "ironpdf";
    public string Display => "IronPdf";
    public string RenderingEngine => "Chromium / Blink（随包捆绑）";
    public string Version => typeof(IronPdf.ChromePdfRenderer).Assembly.GetName().Version?.ToString() ?? "unknown";

    public string? Preflight()
    {
        var key = Environment.GetEnvironmentVariable("IRONPDF_LICENSE_KEY");
        if (!string.IsNullOrWhiteSpace(key)) return null;
        bool allowTrial = Environment.GetEnvironmentVariable("CIBENCH_ALLOW_UNLICENSED_IRONPDF") == "1";
        return allowTrial
            ? null
            : "缺少 IRONPDF_LICENSE_KEY：IronPdf 需授权才能出正式产物，跳过以免把试用/水印结果计入基准" +
              "（可用 --allow-unlicensed-ironpdf 以试用模式参赛）";
    }

    public Task InitializeAsync(string html, string baseDir, CancellationToken ct)
    {
        var key = Environment.GetEnvironmentVariable("IRONPDF_LICENSE_KEY");
        if (!string.IsNullOrWhiteSpace(key)) IronPdf.License.LicenseKey = key;
        return Task.CompletedTask;
    }

    public Task<IPdfWorker> CreateWorkerAsync(string html, string baseDir, CancellationToken ct)
    {
        var renderer = new IronPdf.ChromePdfRenderer();
        renderer.RenderingOptions.PaperSize = IronPdf.Rendering.PdfPaperSize.A4;
        renderer.RenderingOptions.MarginTop = 0;
        renderer.RenderingOptions.MarginBottom = 0;
        renderer.RenderingOptions.MarginLeft = 0;
        renderer.RenderingOptions.MarginRight = 0;
        return Task.FromResult<IPdfWorker>(new Worker(renderer, html));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    sealed class Worker : IPdfWorker
    {
        readonly IronPdf.ChromePdfRenderer _renderer;
        readonly string _html;
        public Worker(IronPdf.ChromePdfRenderer renderer, string html) { _renderer = renderer; _html = html; }

        public async Task<byte[]> RenderAsync(CancellationToken ct)
        {
            var doc = await _renderer.RenderHtmlAsPdfAsync(_html);
            return doc.BinaryData;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
#endif
