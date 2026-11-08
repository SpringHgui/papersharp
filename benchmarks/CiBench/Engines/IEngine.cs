namespace CiBench.Engines;

/// <summary>
/// 一个参赛 PDF 引擎。生命周期分两层：
/// InitializeAsync 是「进程级」的（浏览器只拉起一次 / 字体只加载一次），
/// CreateWorkerAsync 是「并发 worker 级」的（每个 worker 独占不可共享的渲染对象）。
/// 冷启动测的是「新进程 → Initialize → 出一张 → 退出」的整段墙钟；
/// 热启动与并发批次测的是「Initialize 之后」的常驻状态。
/// </summary>
public interface IPdfEngine : IAsyncDisposable
{
    string Name { get; }
    string Display { get; }
    string Version { get; }
    string RenderingEngine { get; }

    /// <summary>轻量预检，只允许读环境变量/文件，不得加载渲染库。null = 可用。</summary>
    string? Preflight();

    Task InitializeAsync(string html, string baseDir, CancellationToken ct);
    Task<IPdfWorker> CreateWorkerAsync(string html, string baseDir, CancellationToken ct);
}

/// <summary>单个并发渲染通道。RenderAsync 返回 PDF 字节；落盘由测量内核统一完成，保证各家口径一致。</summary>
public interface IPdfWorker : IAsyncDisposable
{
    Task<byte[]> RenderAsync(CancellationToken ct);
}

public sealed record EngineDescriptor(string Name, string Display, string RenderingEngine, Func<IPdfEngine> Factory);
