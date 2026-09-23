using SkiaSharp;
using PaperSharp.Layout;

if (args.Length >= 2 && args[0] == "pages")
{
    GlyphProbe.PageProbe.Run(args[1]);
    return;
}

if (args.Length >= 1 && args[0] == "match")
{
    foreach (var a in args[1..])
    {
        int cp = int.Parse(a.StartsWith("0x") ? a[2..] : a, System.Globalization.NumberStyles.HexNumber);
        var m = SKFontManager.Default.MatchCharacter(cp);
        Console.WriteLine($"U+{cp:X4} → {m?.FamilyName ?? "null"} covers={m?.GetGlyph(cp) ?? 0}");
    }
    return;
}

if (args.Length >= 1 && args[0] == "parallel")
{
    // 并行吞吐探针：T 线程并发渲染 invoice PDF，报告页/秒（线程安全化后的扩容实测）
    int threads = args.Length > 1 ? int.Parse(args[1]) : 8;
    int perThread = args.Length > 2 ? int.Parse(args[2]) : 5;
    string assets = Path.GetFullPath("../../src/PaperSharpSamples/assets");
    var html = File.ReadAllText(Path.Combine(assets, "invoice.html"));
    var css = File.ReadAllText(Path.Combine(assets, "invoice.css"));
    PaperSharp.Renderer.RenderPdf(html, css, baseDir: assets); // 预热
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
    {
        for (int i = 0; i < perThread; i++) PaperSharp.Renderer.RenderPdf(html, css, baseDir: assets);
    })).ToArray();
    Task.WaitAll(tasks);
    sw.Stop();
    double pages = threads * (double)perThread;
    Console.WriteLine($"parallel pdf: threads={threads} pages={pages:(0)} wall={sw.Elapsed.TotalSeconds:(0.00)}s → {pages / sw.Elapsed.TotalSeconds:(0.0)} pages/s");
    return;
}

// 诊断：对指定样例 HTML，列出子集字体未覆盖的码位（文本层，不过 CSS 解析）。
var html = File.ReadAllText(args[0]);
var tf = FontManager.Get("Microsoft YaHei", 400);
Console.WriteLine($"typeface = {tf.FamilyName} weight={tf.FontWeight}");
var missing = new SortedDictionary<int, int>();
foreach (var c in html)
{
    int cp = c;
    if (cp < 0x80) continue;
    if (tf.GetGlyph(cp) == 0) missing[cp] = missing.GetValueOrDefault(cp) + 1;
}
foreach (var (cp, n) in missing)
    Console.WriteLine($"U+{cp:X4} '{char.ConvertFromUtf32(cp)}' x{n} shaping={TextShaper.NeedsShaping(char.ConvertFromUtf32(cp))}");
Console.WriteLine($"total distinct missing = {missing.Count}");
