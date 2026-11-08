namespace CiBench.Engines;

public sealed class PaperSharpEngine : IPdfEngine
{
    public string Name => "papersharp";
    public string Display => "PaperSharp";
    public string RenderingEngine => "纯托管 CSS 排版 + Skia（无浏览器）";
    public string Version => typeof(global::PaperSharp.Renderer).Assembly.GetName().Version?.ToString() ?? "local build";

    public string? Preflight() => null;

    // PaperSharp 是纯类库，没有浏览器/引擎要拉起；首次渲染的字体/JIT 预热由 WorkerRunner 统一做。
    public Task InitializeAsync(string html, string baseDir, CancellationToken ct) => Task.CompletedTask;

    public Task<IPdfWorker> CreateWorkerAsync(string html, string baseDir, CancellationToken ct) =>
        Task.FromResult<IPdfWorker>(new Worker(html, baseDir));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    sealed class Worker : IPdfWorker
    {
        readonly string _html;
        readonly string _baseDir;
        public Worker(string html, string baseDir) { _html = html; _baseDir = baseDir; }

        public Task<byte[]> RenderAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // 与其他两家对齐：每次都从 HTML 重新排版，不吃跨文档缓存红利。
            return Task.FromResult(global::PaperSharp.Renderer.RenderPdf(
                _html, "", global::PaperSharp.PageSize.A4, baseDir: _baseDir));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
