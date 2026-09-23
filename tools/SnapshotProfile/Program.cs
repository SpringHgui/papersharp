using System.Diagnostics;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using SkiaSharp;
using PaperSharp;
using PaperSharp.Css;
using PaperSharp.Layout;
using PaperSharp.Paint;

// 分段计时：把「快照 → 成品」拆成 层叠(BuildDocument) / 建盒树 / 度量 / 定位 / 绘制 / 编码 六段，
// 用来回答「导出慢到底慢在哪一段」。跑两遍，第二遍排除 JIT 预热干扰。
//
//   dotnet run -c Release --project tools/SnapshotProfile -- <snapshot.html> <snapshot.css> [宽度] [遍数]

var htmlPath = args.Length > 0 ? args[0] : "extension/test/out/snapshot.html";
var cssPath = args.Length > 1 ? args[1] : "extension/test/out/snapshot.css";
float width = args.Length > 2 ? float.Parse(args[2]) : 1406f;
int rounds = args.Length > 3 ? int.Parse(args[3]) : 2;

var html = File.ReadAllText(htmlPath);
var css = File.ReadAllText(cssPath);
Console.WriteLine($"输入：{htmlPath}（{(html.Length / 1024)} KB） + {cssPath}（{(css.Length / 1024)} KB） · 宽 {width}px");

if (args.Length > 4 && args[4] == "micro") { MicroBench(html, css); return; }
if (args.Length > 4 && args[4] == "measure") { MeasureProbe(args.Length > 5 ? args[5] : "SAVED REPORTS", args.Length > 6 ? float.Parse(args[6]) : 12f); return; }

for (int round = 1; round <= rounds; round++)
{
    var row = new List<(string, double)>();
    using var scope = RenderScope.Begin(null);

    var (tStyle, style) = Time(() => StyleResolver.BuildDocument(html, css));
    var (tTree, box) = Time(() => LayoutBuilder.ToBoxTree(style));
    var (tMeasure, _) = Time(() => { LayoutBuilder.Measure(box, width); return 0; });
    var (tPos, _) = Time(() => { LayoutBuilder.Position(box, 0, 0); return 0; });
    float layoutH = box.TotalHeight;

    using var surface = SKSurface.Create(new SKImageInfo(Math.Max(1, (int)width), Math.Max(100, (int)Math.Ceiling(layoutH))));
    var (tPaint, _) = Time(() => { Painter.Paint(surface.Canvas, box, surface); return 0; });
    var (tEncode, bytes) = Time(() =>
    {
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    });

    row.Add(("层叠 BuildDocument", tStyle));
    row.Add(("建盒树 ToBoxTree", tTree));
    row.Add(("度量 Measure", tMeasure));
    row.Add(("定位 Position", tPos));
    row.Add(("绘制 Paint", tPaint));
    row.Add(("编码 PNG", tEncode));
    double total = row.Sum(r => r.Item2);

    Console.WriteLine($"\n第 {round} 遍（盒树高 {layoutH:F0}px · 输出 {bytes.Length / 1024} KB · 合计 {total:F0}ms）");
    foreach (var (name, ms) in row.OrderByDescending(r => r.Item2))
        Console.WriteLine($"  {name,-20} {ms,8:F0} ms  {ms / Math.Max(1, total) * 100,5:F1}%");
}

static (double Ms, T Value) Time<T>(Func<T> f)
{
    var sw = Stopwatch.StartNew();
    var v = f();
    sw.Stop();
    return (sw.Elapsed.TotalMilliseconds, v);
}

// AngleSharp 微观基准：分别测「每元素一次 GetComputedStyle」「每元素一次 GetDeclarations」
// 与「在拿到的声明上逐属性 GetPropertyValue」的单价，用来定位 ReadStyle 里哪一步在吃时间。
static void MicroBench(string html, string css)
{
    var device = new DefaultRenderDevice { FontSize = 16, ViewPortWidth = 1024, ViewPortHeight = 1024 };
    using var ctx = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device));
    var full = "<!doctype html><html><head><meta charset='utf-8'><style>" + css + "</style></head><body>" + html + "</body></html>";
    var doc = ctx.OpenAsync(r => r.Content(full)).GetAwaiter().GetResult();
    var window = doc.DefaultView ?? throw new InvalidOperationException("no window");
    var els = doc.Body!.QuerySelectorAll("*").ToList();
    string[] props =
    [
        "display", "position", "width", "height", "margin-top", "margin-left", "padding-left", "border-top-width",
        "border-color", "color", "background-color", "background-image", "font-size", "font-family", "line-height",
        "text-align", "white-space", "flex-direction", "gap", "opacity", "box-shadow", "clip-path", "z-index", "top",
    ];
    Console.WriteLine($"元素 {els.Count} · CSS 规则块 {(css.Split('{').Length - 1)} · 探测属性 {props.Length} 个");

    var (tParse, _) = Time(() => ctx.OpenAsync(r => r.Content(full)).GetAwaiter().GetResult());
    var (tComputed, list) = Time(() => els.Select(e => window.GetComputedStyle(e, null)).ToList());
    var (tComputedProps, _) = Time(() => list.Sum(d => props.Sum(p => (d?.GetPropertyValue(p) ?? "").Length)));
    var (tDecls, decls) = Time(() => { var sc = window.GetStyleCollection(); return els.Select(e => sc.GetDeclarations(e, null)).ToList(); });
    var (tDeclProps, _) = Time(() => decls.Sum(d => props.Sum(p => (d?.GetPropertyValue(p) ?? "").Length)));
    var (tBoth, _) = Time(() => els.Select(e =>
    {
        var c = window.GetComputedStyle(e, null);
        var s = window.GetStyleCollection().GetDeclarations(e, null);
        return props.Sum(p => (c?.GetPropertyValue(p) ?? "").Length + (s?.GetPropertyValue(p) ?? "").Length);
    }).Sum());

    void Row(string name, double ms, int ops) =>
        Console.WriteLine($"  {name,-30} {ms,8:F0} ms   {(ops > 0 ? ms / ops * 1000 : 0),8:F1} µs/次  ({ops} 次)");
    Console.WriteLine("\n单价（µs/次）就是 ReadStyle 里每元素每步的真实开销：");
    Row("解析整份 HTML+CSS", tParse, 1);
    Row("GetComputedStyle / 元素", tComputed, els.Count);
    Row("computed.GetPropertyValue", tComputedProps, els.Count * props.Length);
    Row("GetDeclarations / 元素", tDecls, els.Count);
    Row("decls.GetPropertyValue", tDeclProps, els.Count * props.Length);
    Row("computed+decls 合做 / 元素", tBoth, els.Count);
}

/// <summary>量宽探针：同一串文本在「默认 paint / SubpixelText paint / SKFont」三种通路下的宽度，
/// 用来判断钉死盒子里「差一点就换行」是不是量宽的整数进位造成的（浏览器 canvas 同字体的宽度可对照）。</summary>
static void MeasureProbe(string text, float size)
{
    foreach (var family in new[] { "Microsoft YaHei", "Segoe UI", "system-ui" })
    {
        var tf = FontManager.Get(family, 500);
        using var plain = new SKPaint { Typeface = tf, TextSize = size };
        using var sub = new SKPaint { Typeface = tf, TextSize = size, SubpixelText = true };
        using var lin = new SKPaint { Typeface = tf, TextSize = size, LinearText = true };
        using var both = new SKPaint { Typeface = tf, TextSize = size, LinearText = true, SubpixelText = true, IsAntialias = true };
        Console.WriteLine($"  {family,-16} → {tf.FamilyName,-18} 默认 {plain.MeasureText(text),7:F2}  Subpixel {sub.MeasureText(text),7:F2}  Linear {lin.MeasureText(text),7:F2}  Linear+Sub {both.MeasureText(text),7:F2}");
    }
    Console.WriteLine($"  文本「{text}」@ {size}px · 浏览器 canvas 参照：system-ui/YaHei 94.89，Segoe UI 89.84");
}

