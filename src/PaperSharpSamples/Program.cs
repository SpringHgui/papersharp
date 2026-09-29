using SkiaSharp;
using PaperSharp;
using PaperSharpSamples;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);   // 重定向到文件/管道时默认按 OEM 码页(GBK)写，bash 侧 grep 中文模式永远匹配不上
Console.WriteLine("PaperSharp · minimal HTML/CSS → PDF + PNG via SkiaSharp");
Console.WriteLine("=====================================================");
Console.WriteLine();

var baseDir = AppContext.BaseDirectory;
var assets = Path.Combine(baseDir, "assets");
var outDir = Path.Combine(baseDir, "output");
Directory.CreateDirectory(outDir);

// 自检：某字族下字符串的「hmtx 裸宽 + 字体字距对」分解，用于核对 KernTable 是否生效。
// dotnet run -- kern <text> [family]
if (args.Length > 1 && args[0] == "kern")
{
    var family = args.Length > 2 ? args[2] : "Microsoft YaHei";
    var tf = PaperSharp.Layout.FontManager.Get(family, 400);
    using var p = new SKPaint { Typeface = tf, TextSize = 14 };
    float bare = p.MeasureText(args[1]);
    float kern = PaperSharp.Layout.KernTable.Of(args[1], tf, 14f);
    Console.WriteLine($"kern [{family}] \"{args[1]}\" @14: bare={bare:0.###} kern={kern:0.####} total={bare + kern:0.###} hasTable={PaperSharp.Layout.KernTable.HasTable(tf)}");
    return;
}

// 基准测试模式：dotnet run -- bench [iters]  /  dotnet run -- cold <sample>
if (args.Length > 0 && args[0] == "bench")
{
    int iters = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 30;
    Benchmark.Bench(assets, outDir, iters);
    return;
}
if (args.Length > 1 && args[0] == "cold")
{
    Benchmark.Cold(assets, args[1]);
    return;
}
// 单文件模式：dotnet run -- html <name>
// 读 assets/<name>.html（样式内嵌于 <style>），导出 PDF + PNG，用于快速看整页效果。
if (args.Length > 1 && args[0] == "html")
{
    var name = args[1];
    var html = File.ReadAllText(Path.Combine(assets, name + ".html"));
    float pageW = args.Length > 2 && float.TryParse(args[2], out var w) ? w : 1284;

    var pdf = Renderer.RenderPdf(html, "", baseDir: assets);
    var pdfPath = Path.Combine(outDir, name + ".pdf");
    File.WriteAllBytes(pdfPath, pdf);

    var png = Renderer.RenderPng(html, "", width: pageW, baseDir: assets, scale: 2f);
    var pngPath = Path.Combine(outDir, name + ".png");
    File.WriteAllBytes(pngPath, png);

    Console.WriteLine($"[{name}]  PDF {pdf.Length / 1024:N0} KB  ·  PNG {png.Length / 1024:N0} KB (2x, w={pageW:0})");
    Console.WriteLine($"  → {pdfPath}\n  → {pngPath}");
    return;
}
// 像素级重影对拍：dotnet run -- pixdiff <name> <browser2x.png> [width]
// 浏览器侧截图由 tools/boxdiff.mjs --dsf 2 --shot 产出（含 .meta.json 的 body 原点）。
if (args.Length > 2 && args[0] == "pixdiff")
{
    float pdW = args.Length > 3 && float.TryParse(args[3], out var pdw) ? pdw : 1284;
    PixelDiff.Run(assets, outDir, args[1], Path.GetFullPath(args[2]), pdW);
    return;
}
// 基线落位探针：dotnet run -- snap <name> <browser2x.png> [width]（逐行墨迹纵向质心，量两层各落在哪条设备行）
if (args.Length > 2 && args[0] == "snap")
{
    float snW = args.Length > 3 && float.TryParse(args[3], out var snw) ? snw : 1284;
    SnapProbe.Run(assets, outDir, args[1], Path.GetFullPath(args[2]), snW);
    return;
}
// 像素级重影对拍：dotnet run -- pixdiff <name> <browser2x.png> [width]
// 浏览器侧截图由 tools/boxdiff.mjs --dsf 2 --shot 产出（含 .meta.json 的 body 原点）。
if (args.Length > 2 && args[0] == "pixdiff")
{
    float pdW = args.Length > 3 && float.TryParse(args[3], out var pdw) ? pdw : 1284;
    PixelDiff.Run(assets, outDir, args[1], Path.GetFullPath(args[2]), pdW);
    return;
}
// 基线落位探针：dotnet run -- snap <name> <browser2x.png> [width]（逐行墨迹纵向质心，量两层各落在哪条设备行）
if (args.Length > 2 && args[0] == "snap")
{
    float snW = args.Length > 3 && float.TryParse(args[3], out var snw) ? snw : 1284;
    SnapProbe.Run(assets, outDir, args[1], Path.GetFullPath(args[2]), snW);
    return;
}
// 换 PNG 编码器前的一次性迁移取证：与已提交基准逐像素对账（跑完即可删除本模式）
if (args.Length > 0 && args[0] == "goldcheck")
{
    // 从 bin 输出向上找到放了 goldens 的测试工程目录（层数随 TFM/配置变化，不写死）
    var dir = new DirectoryInfo(baseDir);
    string? goldens = null;
    while (dir != null)
    {
        var cand = Path.Combine(dir.FullName, "tests", "PaperSharp.Tests", "goldens");
        if (Directory.Exists(cand)) { goldens = cand; break; }
        dir = dir.Parent;
    }
    if (goldens == null) { Console.WriteLine("找不到 tests/PaperSharp.Tests/goldens"); return; }
    GoldenPixelCheck.Run(assets, goldens);
    return;
}
// 布局/层叠规模扫描：dotnet run -- scale [行数…]（默认 100 200 400 800）
// 给大行数（如 2000）才能看 buildDoc 的复杂度；改前那一档单跑就要十几分钟。
if (args.Length > 0 && args[0] == "scale")
{
    int[]? scaleRows = args.Length > 1 && args[1..].All(a => int.TryParse(a, out _))
        ? args[1..].Select(int.Parse).ToArray() : null;
    Benchmark.ScaleOnly(assets, scaleRows);
    return;
}
// Playground 路径逐阶段拆解：dotnet run -- pgstage [iters] [样例名…]
// 与页面同口径（宽 1284 / scale 2 / domPath 开）。不给样例名就跑 assets 下全部。
if (args.Length > 0 && args[0] == "pgstage")
{
    int pgIters = args.Length > 1 && int.TryParse(args[1], out var pn) ? pn : 7;
    string[]? only = args.Length > 2 ? args[2..] : null;
    Benchmark.Playground(assets, pgIters, only);
    return;
}
// 编码阶段专项对比：dotnet run -- pgenc [iters] [样例名…]
if (args.Length > 0 && args[0] == "pgenc")
{
    int enIters = args.Length > 1 && int.TryParse(args[1], out var en) ? en : 3;
    string[]? enOnly = args.Length > 2 ? args[2..] : null;
    EncodeProbe.Run(assets, enIters, enOnly);
    return;
}
// 画布生命周期专项：dotnet run -- pgalloc [iters] [样例名…]
if (args.Length > 0 && args[0] == "pgalloc")
{
    int acIters = args.Length > 1 && int.TryParse(args[1], out var ac) ? ac : 5;
    string[]? acOnly = args.Length > 2 ? args[2..] : null;
    Benchmark.CanvasLifecycle(assets, acIters, acOnly);
    return;
}
// buildDoc 内部拆解：dotnet run -- pgbuild [iters] [样例名…]
if (args.Length > 0 && args[0] == "pgbuild")
{
    int blIters = args.Length > 1 && int.TryParse(args[1], out var bl) ? bl : 3;
    string[]? blOnly = args.Length > 2 ? args[2..] : null;
    CascadeProbe.Run(assets, blIters, blOnly);
    return;
}
// 层叠超线性来源 + 按元素缓存版的对账门：dotnet run -- pgstyle [N…]
if (args.Length > 0 && args[0] == "pgstyle")
{
    int[] sizes = args.Length > 1
        ? args[1..].Select(int.Parse).ToArray()
        : new[] { 250, 500, 1000, 2000 };
    CascadeProbe.Style(assets, sizes);
    return;
}
// PDF 字体嵌入行为探针：dotnet run -- pdfprobe
if (args.Length > 0 && args[0] == "pdfprobe")
{
    PdfProbe.Run(assets, outDir);
    return;
}
// 验证模式：把 SVG 文件栅格化为 PNG（用 Svg.Skia），供肉眼核对 SVG 输出
if (args.Length > 2 && args[0] == "svgpng")
{
    var svg = new Svg.Skia.SKSvg();
    var pic = svg.FromSvg(File.ReadAllText(args[1]));
    if (pic == null) { Console.WriteLine("parse fail"); return; }
    int w = (int)pic.CullRect.Width, h = (int)pic.CullRect.Height;
    using var surf = SKSurface.Create(new SKImageInfo(w, h));
    surf.Canvas.Clear(SKColors.White);
    surf.Canvas.DrawPicture(pic);
    using var im = surf.Snapshot();
    using var d = im.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(args[2], d.ToArray());
    Console.WriteLine($"svgpng {w}x{h} → {args[2]}");
    return;
}
if (args.Length > 1 && args[0] == "tree")
{
    var html = File.ReadAllText(Path.Combine(assets, args[1] + ".html"));
    var cssPath = Path.Combine(assets, args[1] + ".css");
    var css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";   // 样式内嵌于 <style> 的样例没有同名 .css
    float treeW = args.Length > 2 && float.TryParse(args[2], out var tw2) ? tw2 : 794;
    var doc = PaperSharp.Css.StyleResolver.BuildDocument(html, css, viewportWidth: treeW);
    var root = PaperSharp.Layout.LayoutBuilder.ToBoxTree(doc);
    PaperSharp.Layout.LayoutBuilder.Measure(root, treeW);
    PaperSharp.Layout.LayoutBuilder.Position(root, 0, 0);
    void Dump(PaperSharp.Layout.BlockBox b, int d)
    {
        Console.WriteLine(new string(' ', d * 2) + $"<{b.Style.Tag}> display={b.Style.Display} x={b.BorderX} y={b.BorderY} w={b.ContentWidth} h={b.ContentHeight} kids={b.Children.Count} gridInfo={b.GridInfo != null} lines={b.Lines?.Count ?? -1} wl={b.WrappedLines?.Length ?? -1}"
            + (b.Style.MixBlendMode != "normal" || b.Style.ClipPath != null || b.Style.MaskImage != null || b.Style.Filter != null || b.Style.CornerShape != "round"
               ? $" FX[blend={b.Style.MixBlendMode} clip={b.Style.ClipPath} mask={b.Style.MaskImage != null} filt={b.Style.Filter != null} shape={b.Style.CornerShape}]" : "")
            + (b.WrappedLines is { Length: > 0 } ? $" tw={b.Style.TextWrap} va={b.Style.VerticalAlign} tf={b.Style.TextFit} fs={b.Style.FontSize} L0=[{b.WrappedLines[0]}]" : ""));
        if (b.GridInfo != null)
            Console.WriteLine(new string(' ', (d + 1) * 2) + $"grid cols=[{string.Join(",", b.GridInfo.ColWidths)}] rows=[{string.Join(",", b.GridInfo.RowHeights)}] gap={b.GridInfo.ColGap}/{b.GridInfo.RowGap} placements={b.GridInfo.Placements.Count}");
        if (b.TColW != null)
            Console.WriteLine(new string(' ', (d + 1) * 2) + $"table cols=[{string.Join(",", b.TColW)}] rows=[{string.Join(",", b.TRowH)}] spacing={b.TSpacing}");
        foreach (var c in b.Children) Dump(c, d + 1);
    }
    Dump(root, 0);
    return;
}

// 盒矩形导出模式：dotnet run -- boxes <name> [width]
// 与 Playground 同一口径（RenderRasterWithBoxes），落两份产物：
//   output/boxes-<name>.html    —— 与 PlaygroundService.BuildPreviewDoc 同优先级拼出的浏览器预览文档（UA < 附加 CSS < 文档内 style，藏滚动条，base 指向 assets）
//   output/boxes-<name>.json    —— 引擎盒矩形（body 边框盒原点，CSS px）
// 供 tools/boxdiff.mjs 在无头浏览器里量同一份文档并逐元素求差，不靠开网页肉眼对。
if (args.Length > 1 && args[0] == "boxes")
{
    var name = args[1];
    float boxW = args.Length > 2 && float.TryParse(args[2], out var bw) ? bw : 1284;
    // 与 PlaygroundSamples 同口径：进程级字体目录必须在这条路先注册，否则引擎解析出的字形与浏览器
    // 渲染的不是同一张 face，量出来的盒差全是字体度量的假差（m9 的 1.4875 行高比就是这么来的）。
    var fontsDir = Path.Combine(assets, "fonts");
    if (Directory.Exists(fontsDir)) PaperSharp.Layout.FontManager.RegisterFontDirectory(fontsDir);
    var html = File.ReadAllText(Path.Combine(assets, name + ".html"));
    var css = File.Exists(Path.Combine(assets, name + ".css")) ? File.ReadAllText(Path.Combine(assets, name + ".css")) : "";

    var r = Renderer.RenderRasterWithBoxes(html, css, boxW, baseDir: assets, scale: 2f);
    File.WriteAllBytes(Path.Combine(outDir, $"boxes-{name}.png"), r.Bytes);   // 目视核对用（与金标同 2x 参数）
    File.WriteAllText(Path.Combine(outDir, $"boxes-{name}.json"),
        System.Text.Json.JsonSerializer.Serialize(new { width = boxW, contentWidth = r.ContentWidth, bodyX = r.BodyX, bodyY = r.BodyY, boxes = r.Boxes },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));

    var baseHref = "file:///" + assets.Replace('\\', '/').TrimEnd('/') + "/";
    var head = "<meta charset=\"utf-8\">" +
               $"<base href=\"{baseHref}\">" +
               $"<style>{PaperSharp.Css.StyleResolver.UaStylesheetForPreview}</style>" +
               (string.IsNullOrWhiteSpace(css) ? "" : $"<style>{css}</style>") +
               "<style>::-webkit-scrollbar{width:0!important;height:0!important;display:none!important}" +
               "html{scrollbar-width:none!important;-ms-overflow-style:none!important}</style>";
    File.WriteAllText(Path.Combine(outDir, $"boxes-{name}.html"),
        $"<!doctype html><html><head>{head}</head><body>{html}</body></html>");

    Console.WriteLine($"[boxes:{name}] w={boxW:0} · 引擎 {r.Boxes.Count} 盒 → output/boxes-{name}.json + boxes-{name}.html");
    return;
}

RenderOne("invoice");
RenderOne("long");
RenderOne("flex");
RenderOne("img");
RenderOne("report");
RenderOne("pct");
RenderOne("polish");
RenderOne("inline");
RenderOne("table");
RenderOne("m1");
RenderOne("m2");
RenderOne("m3");
RenderFormats("m3");

// M4：字体 helper——目录整批注册 + Google Fonts（联网一次，之后离线复用）
PaperSharp.Layout.FontManager.RegisterFontDirectory(Path.Combine(assets, "fonts"));
var gfontCss = PaperSharp.Css.GoogleFonts.Fetch("Noto Serif SC:wght@400;700");
Console.WriteLine($"[gfonts] {(gfontCss.Length > 0 ? gfontCss.Count(c => c == '{') + " 条 @font-face（本地缓存）" : "无网络/拉取失败 → 样例回退系统字体")}");
RenderOne("m4", gfontCss);
RenderOne("m5");
RenderOne("m7");
RenderOne("m8");
RenderOne("m9");
RenderAnimFrame("m9", 1.0);   // 动画样例：额外输出一张 animationTime=1s 的取帧，直观演示确定性取帧

// 确定性取帧：同一资产在不同 animationTime 下渲染成不同帧
void RenderAnimFrame(string name, double t)
{
    var html = File.ReadAllText(Path.Combine(assets, name + ".html"));
    var css = File.ReadAllText(Path.Combine(assets, name + ".css"));
    var png = Renderer.RenderPng(html, css, width: 794, baseDir: assets, scale: 2f, animationTime: t);
    var path = Path.Combine(outDir, $"{name}@{t:0.#}s.png");
    File.WriteAllBytes(path, png);
    Console.WriteLine($"[{name}@{t:0.#}s] PNG {png.Length / 1024:N0} KB (2x, 取帧)");
}

// M4：同一模板一键出 PNG/JPEG/WebP/ICO/SVG
void RenderFormats(string name)
{
    var html = File.ReadAllText(Path.Combine(assets, name + ".html"));
    var css = File.ReadAllText(Path.Combine(assets, name + ".css"));

    var jobs = new (string ext, byte[] bytes)[]
    {
        ("jpg", Renderer.RenderImage(html, css, 794, SKEncodedImageFormat.Jpeg, 90, assets, 2f)),
        ("webp", Renderer.RenderImage(html, css, 794, SKEncodedImageFormat.Webp, 90, assets, 2f)),
        ("svg", Renderer.RenderSvg(html, css, 794, assets)),
        ("ico", Renderer.RenderIco(html, css, 64, 794, assets)),
    };
    var notes = new List<string>();
    foreach (var (ext, bytes) in jobs)
    {
        var path = Path.Combine(outDir, name + "." + ext);
        File.WriteAllBytes(path, bytes);
        string verify = ext == "svg"
            ? (bytes.Length > 0 && System.Text.Encoding.UTF8.GetString(bytes, 0, 5) == "<svg " ? "svg✓" : "svg✗")
            : SKImage.FromEncodedData(path) is { } img ? $"{img.Width}x{img.Height}✓" : "✗";
        notes.Add($"{ext} {bytes.Length / 1024:N0}KB {verify}");
    }
    Console.WriteLine($"[{name}:fmt] {string.Join(" · ", notes)}");
}

Console.WriteLine();
Console.WriteLine("Done. 产物在 output/。polish.pdf 演示第二梯队，inline.pdf 演示 inline 格式化上下文，table.pdf 演示表格布局，m1.pdf 演示 M1 里程碑全部特性，m2.pdf 演示 M2 里程碑布局能力扩展。");

void RenderOne(string name, string extraCss = "")
{
    var html = File.ReadAllText(Path.Combine(assets, name + ".html"));
    var css = File.ReadAllText(Path.Combine(assets, name + ".css")) + "\n" + extraCss;

    var pdf = Renderer.RenderPdf(html, css, baseDir: assets);
    var pdfPath = Path.Combine(outDir, name + ".pdf");
    File.WriteAllBytes(pdfPath, pdf);

    var png = Renderer.RenderPng(html, css, width: 794, baseDir: assets, scale: 2f);
    var pngPath = Path.Combine(outDir, name + ".png");
    File.WriteAllBytes(pngPath, png);

    Console.WriteLine($"[{name}]  PDF {pdf.Length / 1024:N0} KB  ·  PNG {png.Length / 1024:N0} KB (2x)");
}
