using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

    /// <summary>PDF 一份几十到几百 KB，且悬浮窗只看最新那份，上限比 PNG 更小。</summary>
    const int MaxPdfs = 8;

    /// <summary>同宽档的页面高仍取 A4：分页切在哪一行是预览要看的东西，高度不该跟着窗格宽一起变。</summary>
    const float A4HeightPt = 841.89f;

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
    readonly Dictionary<string, byte[]> _pdfs = new();
    readonly Queue<string> _pdfOrder = new();
    readonly object _gate = new();

    /// <summary>
    /// PDF 与长图共用同一份输入，但刻意不同一趟跑：PDF 管线要写字体子集，秒级且吃满 CPU，
    /// 排进主渲染链路会让「打字→出图」慢一倍。所以它挂在链路外自己跑，只保证同一时刻
    /// 最多一份在生成——否则连打十个字符就排十趟管线，全在抢同一批核，最后那张反而最晚出来。
    /// </summary>
    readonly SemaphoreSlim _pdfGate = new(1, 1);

    /// <summary>打印对拍会拉起一个无头 Edge（秒级、吃一份进程），串到 1 份，别连打时堆一排浏览器。</summary>
    readonly SemaphoreSlim _ppGate = new(1, 1);

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
            r.MarginLeft, r.MarginTop, r.MarginRight, r.MarginBottom,
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

    /// <summary>
    /// 「打印对拍」的浏览器参照：把预览文档丢给**无头** Edge（<c>tools/pgshot.mjs</c>）出 2× 截图 + 量矩形。
    /// 为什么是无头而不是 Playground 默认的有头 iframe（2026-10-02 取证，见台账 P0-10）：
    /// 有头屏显吃 **DirectWrite** 字体度量，无头屏显与 <c>printToPDF</c> 吃 **Skia** 度量——本引擎是打印
    /// 引擎，出图走 Skia，且实测**引擎 PDF 与 Chrome PDF 逐位相同**。所以无头参照才是与引擎同口径的参照物，
    /// 拿它对 <c>challenge</c> 逐盒对拍 **257/257、最坏 0.00px**；有头 iframe 才会露出 ±0.5px/行的 CJK 幻影重影。
    /// </summary>
    /// <param name="viewportWidth">整页宽（= 长图 <see cref="PlaygroundRender.CssWidth"/>），与 iframe 同值。</param>
    /// <param name="pageMargin">引擎那趟的 @page 边距，落成预览文档的 html padding，两侧零点/相位才一致。</param>
    public async Task<PrintParityResult> PrintParityAsync(string html, string extraCss, float viewportWidth,
        float scale, (float top, float right, float bottom, float left)? pageMargin, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var script = FindRepoTool("tools/pgshot.mjs")
            ?? throw new InvalidOperationException("找不到 tools/pgshot.mjs（打印对拍参照脚本）。");
        var assets = _samples.AssetsDir;
        var fileBase = string.IsNullOrEmpty(assets)
            ? null
            : "file:///" + assets.Replace('\\', '/').TrimEnd('/') + "/";
        var srcdoc = BuildPreviewDoc(html, extraCss, fileBase, pageMargin);

        var dir = Path.Combine(Path.GetTempPath(), "papersharp-pp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var docPath = Path.Combine(dir, "preview.html");
            var pngPath = Path.Combine(dir, "shot.png");
            var rectsPath = Path.Combine(dir, "rects.json");
            await File.WriteAllTextAsync(docPath, srcdoc, ct);

            await _ppGate.WaitAsync(ct);
            try
            {
                var psi = new ProcessStartInfo("node",
                    $"\"{script}\" \"{docPath}\" {Math.Max(1, (int)viewportWidth)} {Math.Max(1, (int)scale)} \"{pngPath}\" \"{rectsPath}\"")
                {
                    RedirectStandardError = true, RedirectStandardOutput = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(script)!,
                };
                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("无法启动 node。");
                // 无头 Edge 冷启 + 排版 settle 约 1~2s；给足但别无限等。
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    await proc.WaitForExitAsync(timeout.Token);
                }
                if (proc.ExitCode != 0 || !File.Exists(pngPath))
                    throw new InvalidOperationException(
                        "无头参照出图失败：" + (await proc.StandardError.ReadToEndAsync()).Trim());
            }
            finally { _ppGate.Release(); }

            var png = await File.ReadAllBytesAsync(pngPath, ct);
            var rects = JsonDocument.Parse(await File.ReadAllTextAsync(rectsPath, ct))
                .RootElement.EnumerateArray().Select(e => new DomRect
                {
                    DomPath = e.TryGetProperty("domPath", out var dp) ? dp.GetString() ?? "" : "",
                    Tag = e.TryGetProperty("tag", out var tg) ? tg.GetString() ?? "" : "",
                    X = GetF(e, "x"), Y = GetF(e, "y"), W = GetF(e, "w"), H = GetF(e, "h"),
                }).ToList();

            var token = Guid.NewGuid().ToString("N");
            Remember(token, png, Array.Empty<BoxRect>());   // 只借 _shots 存 PNG 供 <img> 取；对拍用 render.Token 那份盒
            sw.Stop();
            _log.LogDebug("print-parity {Token}：{Rects} 矩形 · {Kb} KB · {Ms} ms",
                token[..8], rects.Count, png.Length / 1024.0, sw.Elapsed.TotalMilliseconds);
            return new PrintParityResult(token, rects, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录清不掉不影响本次结果 */ }
        }
    }

    static float GetF(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.TryGetSingle(out var f) ? f : 0f;

    /// <summary>从运行目录逐级上溯找仓库内的工具脚本（Playground 是本地开发工具，node 已在 PATH 上）。</summary>
    static string? FindRepoTool(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; dir is not null && up < 8; up++, dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(cand)) return cand;
        }
        return null;
    }


    public byte[]? PeekPdf(string token)
    {
        lock (_gate) return _pdfs.TryGetValue(token, out var b) ? b : null;
    }

    /// <summary>
    /// 生成一份 PDF 供悬浮窗实时预览。CPU 密集，故丢线程池并串行走 <see cref="_pdfGate"/>。
    /// 页面尺寸按 <see cref="AtPageParser"/> 现算一遍再返回——显示的是真跑出来的那个尺寸，
    /// 不是下拉框的字面值（用户自己的 @page 可能把它改掉）。
    /// </summary>
    public async Task<PlaygroundPdf> RenderPdfAsync(string html, string extraCss, float width, PdfPaper paper, CancellationToken ct)
    {
        var css = BuildPdfCss(extraCss, width, paper);
        var (defW, defH) = paper == PdfPaper.Letter ? (612f, 792f) : (595.28f, A4HeightPt);
        var spec = AtPageParser.Parse(css, defW, defH);

        await _pdfGate.WaitAsync(ct);
        Stopwatch sw;
        byte[] bytes;
        try
        {
            sw = Stopwatch.StartNew();
            bytes = await Task.Run(
                () => Renderer.RenderPdf(html, css, paper == PdfPaper.Letter ? PageSize.Letter : PageSize.A4,
                    baseDir: _samples.AssetsDir), ct);
            sw.Stop();
        }
        finally { _pdfGate.Release(); }

        var token = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _pdfs[token] = bytes;
            _pdfOrder.Enqueue(token);
            while (_pdfOrder.Count > 0 && _pdfs.Count > MaxPdfs)
                if (_pdfOrder.TryDequeue(out var old)) _pdfs.Remove(old);
        }
        _log.LogDebug("pdf {Token}：{W}×{H}pt · {Kb} KB · {Ms} ms",
            token[..8], spec.PageW, spec.PageH, bytes.Length / 1024.0, sw.Elapsed.TotalMilliseconds);
        return new PlaygroundPdf(token, spec.PageW, spec.PageH, spec.ContentW, sw.Elapsed.TotalMilliseconds, bytes.Length);
    }

    /// <summary>
    /// 同宽档：把页面宽钉成长图那一次的画布宽，两边内容宽才严格相等（1 CSS px = 1 布局点 = 1 PDF 点），
    /// 悬浮窗里的折行才能逐行对上左侧两窗。追加在末尾——AtPageParser 按出现顺序逐条应用，
    /// 后来者只覆盖 size，用户自己 @page 里的 margin 与页眉页脚原样保留。
    /// </summary>
    public static string BuildPdfCss(string extraCss, float width, PdfPaper paper)
        => paper == PdfPaper.SameWidth
            ? extraCss + "\n@page{size:" + width.ToString("0.##", CultureInfo.InvariantCulture)
                        + "px " + A4HeightPt.ToString("0.##", CultureInfo.InvariantCulture) + "px}"
            : extraCss;

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
    /// <param name="pageMargin">
    /// 引擎那趟用到的 @page 边距（CSS px，上右下左）。@page 在屏幕渲染里不生效，所以预览文档要把它
    /// 落成 <c>html{padding}</c>：浏览器与引擎的**绘制相位**都是拿「相对文档零点的位置」折整 CSS px
    /// （2026-09-30 探针 <c>tools/snapprobe.mjs</c> 实测：左边距 60.5 → 落在 61，60.46875 → 落在 60），
    /// 引擎的内容从页边距内侧开画、浏览器从 0 开画，两边折的网格就不是同一个 —— 毫米边距的样例
    /// （16mm = 60.46875）于是整页文字错开一格，polish/pct/report 实测 6~15% 的「重影」全是这么来的。
    /// 注了 padding 之后两侧文档零点相同，视口宽也要跟着给**整页宽**（见 Playground.razor 的 loadPreview）。
    /// </param>
    public static string BuildPreviewDoc(string html, string extraCss, string? assetBaseHref = null,
        (float top, float right, float bottom, float left)? pageMargin = null)
    {
        var (mt, mr, mb, ml) = pageMargin ?? (0, 0, 0, 0);
        // 边距已经折在 1/64 网格上（AtPageParser.ToMarginPx），默认 float  ToString 就是往返精确的十进制，
        // 别加格式串——"0.##" 会把 60.46875 打成 60.47，两侧又错开半格。
        string N(float v) => v.ToString(CultureInfo.InvariantCulture);
        var pagePad = mt == 0 && mr == 0 && mb == 0 && ml == 0
            ? ""
            : $"<style>html{{padding:{N(mt)}px {N(mr)}px {N(mb)}px {N(ml)}px}}</style>";
        var head = "<meta charset=\"utf-8\">" +
                   (assetBaseHref is { Length: > 0 } b ? $"<base href=\"{b}\">" : "") +
                   $"<style>{StyleResolver.UaStylesheetForPreview}</style>" +
                   (string.IsNullOrWhiteSpace(extraCss) ? "" : $"<style>{extraCss}</style>") +
                   $"<style>{PreviewOnlyStyles}</style>" +
                   pagePad;

        var mHead = Regex.Match(html, @"<head[^>]*>", RegexOptions.IgnoreCase);
        if (mHead.Success) return html.Insert(mHead.Index + mHead.Length, head);
        var mBody = Regex.Match(html, @"<body[^>]*>", RegexOptions.IgnoreCase);
        if (mBody.Success) return html.Insert(mBody.Index, "<head>" + head + "</head>");
        return $"<!doctype html><html><head>{head}</head><body>{html}</body></html>";
    }
}
