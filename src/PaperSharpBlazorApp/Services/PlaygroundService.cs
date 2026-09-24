using System.Diagnostics;
using System.Text.RegularExpressions;
using PaperSharp;
using PaperSharp.Css;

namespace PaperSharpBlazorApp.Services;

/// <summary>
/// Playground 的后端：驱动 PaperSharp 出图并留下盒矩形，再把图与矩形按 token 暂存，
/// 等浏览器侧回传矩形后按 DomPath 对位求差。
/// </summary>
public sealed class PlaygroundService
{
    /// <summary>
    /// 只保留最近这么多张图。Blazor 电路一断、或用户连打十几个字符，都会留下没人取的 PNG；
    /// 不设上限等于拿服务器内存换一堆没人看的截图。
    /// </summary>
    const int MaxShots = 32;

    /// <summary>
    /// 预览 iframe 专用：把滚动条宽度压成 0。
    /// 引擎排版宽 = 传入的 width，浏览器视口宽 = iframe 宽 − 滚动条宽，不藏滚动条就凭空差 15px，
    /// 每个元素的宽度都会被判成漂移。（e2e 脚本走的是同一个路子：Chromium 加 --hide-scrollbars。）
    /// </summary>
    const string PreviewOnlyStyles = """
        ::-webkit-scrollbar{width:0!important;height:0!important;display:none!important}
        html{scrollbar-width:none!important;-ms-overflow-style:none!important}
        """;

    sealed record Shot(byte[] Png, IReadOnlyList<BoxRect> Boxes);

    readonly PlaygroundSamples _samples;
    readonly ILogger<PlaygroundService> _log;
    readonly Dictionary<string, Shot> _shots = new();
    readonly Queue<string> _order = new();   // 与 _shots 同锁访问，FIFO 淘汰
    readonly object _gate = new();

    public PlaygroundService(PlaygroundSamples samples, ILogger<PlaygroundService> log)
    {
        _samples = samples;
        _log = log;
    }

    /// <summary>
    /// 渲染一次。CPU 密集（实测真实站点快照 3s 量级），故丢到线程池并允许取消——
    /// 用户边打字边出图时，上一次还没跑完就该被丢弃，否则电路会被排队的渲染堵死。
    /// </summary>
    public async Task<PlaygroundRender> RenderAsync(string html, string extraCss, float width, float scale, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var r = await Task.Run(
            () => Renderer.RenderRasterWithBoxes(html, extraCss, width, baseDir: _samples.AssetsDir, scale: scale), ct);
        sw.Stop();

        var token = Guid.NewGuid().ToString("N");
        Remember(token, r.Bytes, r.Boxes);
        return new PlaygroundRender(token, r.PixelWidth, r.PixelHeight,
            r.PixelWidth / r.Scale, r.PixelHeight / r.Scale,
            r.ContentWidth, r.BodyX, r.BodyY,
            r.Boxes.Count, sw.Elapsed.TotalMilliseconds, r.Bytes.Length);
    }

    /// <summary>
    /// 浏览器矩形对位求差。token 已被淘汰（用户连打太快）时返回 null，调用方跳过这一轮即可。
    /// </summary>
    public DiffResult? Diff(string token, IReadOnlyList<DomRect> browserRects, float tolerancePx)
    {
        Shot? shot;
        lock (_gate) _shots.TryGetValue(token, out shot);
        if (shot == null) return null;
        var result = PlaygroundDiff.Compare(shot.Boxes, browserRects, tolerancePx);
        _log.LogDebug("diff {Token}：引擎 {Engine} 盒 / 浏览器 {Browser} 矩形 → 漂移 {Drift}、单边 {Orphan}",
            token[..8], shot.Boxes.Count, browserRects.Count, result.Summary.Drift,
            result.Summary.EngineOnly + result.Summary.BrowserOnly);
        return result;
    }

    void Remember(string token, byte[] png, IReadOnlyList<BoxRect> boxes)
    {
        lock (_gate)
        {
            _shots[token] = new Shot(png, boxes);
            _order.Enqueue(token);
            while (_order.Count > 0 && _shots.Count > MaxShots)
                if (_order.TryDequeue(out var old)) _shots.Remove(old);
        }
    }

    public byte[]? PeekImage(string token)
    {
        lock (_gate) return _shots.TryGetValue(token, out var shot) ? shot.Png : null;
    }

    /// <summary>预览 iframe 解析相对资源用的基址（与引擎的 baseDir 指向同一目录）。</summary>
    public const string AssetUrlBase = "/api/playground/asset/";

    /// <summary>
    /// 读样例资产。目录固定在 AssetsDir 内，越界路径直接拒——这是本地图，但也不该变成任意文件读取。
    /// </summary>
    public (byte[] Bytes, string ContentType)? ReadAsset(string relative)
    {
        if (_samples.AssetsDir is not { Length: > 0 } root) return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        if (!File.Exists(full)) return null;
        return (File.ReadAllBytes(full), ContentType(full));
    }

    static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".woff" or ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// 把用户输入拼成一份能在 iframe 里独立打开的文档，优先级顺序刻意与引擎一致：
    /// UA 表 &lt; 附加 CSS &lt; 文档内 &lt;style&gt;（对应 StyleResolver 里
    /// <c>&lt;style&gt;UA&lt;/style&gt;&lt;style&gt;附加+内嵌&lt;/style&gt;</c> 那一串）。
    /// 顺序错一位，比对就不是在比同一份层叠结果。
    /// </summary>
    /// <param name="assetBaseHref">
    /// 相对资源的服务端基址。引擎按 baseDir 从磁盘读 <c>imgs/qr.png</c>，浏览器没有它就取不到，
    /// 于是凭空报出一条「浏览器独有」——注 <c>&lt;base&gt;</c> 让两边解析到同一个文件，差异才是真差异。
    /// </param>
    public static string BuildPreviewDoc(string html, string extraCss, string? assetBaseHref = null)
    {
        var head = "<meta charset=\"utf-8\">" +
                   (assetBaseHref is { Length: > 0 } b ? $"<base href=\"{b}\">" : "") +
                   $"<style>{StyleResolver.UaStylesheetForPreview}</style>" +
                   (string.IsNullOrWhiteSpace(extraCss) ? "" : $"<style>{extraCss}</style>") +
                   $"<style>{PreviewOnlyStyles}</style>";

        var mHead = Regex.Match(html, @"<head[^>]*>", RegexOptions.IgnoreCase);
        if (mHead.Success) return html.Insert(mHead.Index + mHead.Length, head);
        var mBody = Regex.Match(html, @"<body[^>]*>", RegexOptions.IgnoreCase);
        if (mBody.Success) return html.Insert(mBody.Index, "<head>" + head + "</head>");
        return $"<!doctype html><html><head>{head}</head><body>{html}</body></html>";
    }
}
