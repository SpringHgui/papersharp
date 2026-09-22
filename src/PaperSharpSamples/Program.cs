using SkiaSharp;
using PaperSharp;

Console.WriteLine("PaperSharp · minimal HTML/CSS → PDF + PNG via SkiaSharp");
Console.WriteLine("=====================================================");
Console.WriteLine();

var baseDir = AppContext.BaseDirectory;
var assets = Path.Combine(baseDir, "assets");
var outDir = Path.Combine(baseDir, "output");
Directory.CreateDirectory(outDir);

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
if (args.Length > 0 && args[0] == "scale")
{
    Benchmark.ScaleOnly(assets);
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
    var css = File.ReadAllText(Path.Combine(assets, args[1] + ".css"));
    var doc = PaperSharp.Css.StyleResolver.BuildDocument(html, css);
    var root = PaperSharp.Layout.LayoutBuilder.ToBoxTree(doc);
    PaperSharp.Layout.LayoutBuilder.Measure(root, 794);
    void Dump(PaperSharp.Layout.BlockBox b, int d)
    {
        Console.WriteLine(new string(' ', d * 2) + $"<{b.Style.Tag}> display={b.Style.Display} w={b.ContentWidth} h={b.ContentHeight} kids={b.Children.Count} gridInfo={b.GridInfo != null} lines={b.Lines?.Count ?? -1} wl={b.WrappedLines?.Length ?? -1}"
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
