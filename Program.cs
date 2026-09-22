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

Console.WriteLine();
Console.WriteLine("Done. 产物在 output/。polish.pdf 演示第二梯队，inline.pdf 演示 inline 格式化上下文，table.pdf 演示表格布局，m1.pdf 演示 M1 里程碑全部特性，m2.pdf 演示 M2 里程碑布局能力扩展。");

void RenderOne(string name)
{
    var html = File.ReadAllText(Path.Combine(assets, name + ".html"));
    var css = File.ReadAllText(Path.Combine(assets, name + ".css"));

    var pdf = Renderer.RenderPdf(html, css, baseDir: assets);
    var pdfPath = Path.Combine(outDir, name + ".pdf");
    File.WriteAllBytes(pdfPath, pdf);

    var png = Renderer.RenderPng(html, css, width: 794, baseDir: assets, scale: 2f);
    var pngPath = Path.Combine(outDir, name + ".png");
    File.WriteAllBytes(pngPath, png);

    Console.WriteLine($"[{name}]  PDF {pdf.Length / 1024:N0} KB  ·  PNG {png.Length / 1024:N0} KB (2x)");
}
