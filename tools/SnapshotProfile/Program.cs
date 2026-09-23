using System.Diagnostics;
using System.Reflection;
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
if (args.Length > 4 && args[4] == "diff") { CascadeDiff(html, css, width, Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(htmlPath)) ?? ".")); return; }
if (args.Length > 4 && args[4] == "measure") { MeasureProbe(args.Length > 5 ? args[5] : "SAVED REPORTS", args.Length > 6 ? float.Parse(args[6]) : 12f); return; }

for (int round = 1; round <= rounds; round++)
{
    var row = new List<(string, double)>();
    using var scope = RenderScope.Begin(null);

    // precomputed:true = 扩展端同款输入（offscreen.js 恒传）：快照的值已是浏览器 used value，第二轮层叠无意义。
    var (tStyle, style) = Time(() => StyleResolver.BuildDocument(html, css, precomputed: true));
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

// 双跑对账（ROADMAP M6 步骤⑥）：同一份快照分别走「AngleSharp 逐元素层叠」与「FlatCascade 类名命中」，
// 逐元素逐字段比对。绿了才允许换路径——这里的一致性是整段加速的唯一凭据。
static void CascadeDiff(string html, string css, float width, string outDir)
{
    Directory.CreateDirectory(outDir);
    // 强制老路：追加一条无声明的 @media，FlatCascade.TryBuild 见到 at-rule 即整份弃权，
    // 而 AngleSharp 解析它不产生任何声明，层叠结果不变。
    var a = StyleResolver.BuildDocument(html, css + "\n@media all{}", precomputed: true);
    var b = StyleResolver.BuildDocument(html, css, precomputed: true);

    var flat = new List<(string Path, string Prop, string Va, string Vb, bool Live)>();
    int na = 0, nb = 0;
    var fields = typeof(PaperSharp.Css.ComputedStyle).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(f => !f.FieldType.IsGenericType && f.Name is not ("Parent" or "Document" or "Before" or "After"))
        .OrderBy(f => f.Name).ToList();

    // liveA/liveB：该节点在各自路径下是否真的会被画出来（祖先链没有 display:none）。
    // 展平快照里大量常驻但隐藏的面板（移动端 header、抽屉侧栏）两边都不 live，
    // 逐属性差异再多也不影响成品——没有这一列，「7183 处差异 + 0 像素」根本没法解释。
    void Cmp(string path, PaperSharp.Css.ComputedStyle? x, PaperSharp.Css.ComputedStyle? y, bool liveA, bool liveB)
    {
        if (x == null || y == null) { if (x != y) flat.Add((path, "<盒缺失>", x == null ? "-" : "+", y == null ? "-" : "+", liveA || liveB)); return; }
        na++; nb++;
        bool live = (liveA && x.Display != "none") || (liveB && y.Display != "none");
        if (x.Tag != y.Tag) flat.Add((path, "<tag>", x.Tag, y.Tag, live));
        foreach (var f in fields)
        {
            var va = Str(f.GetValue(x));
            var vb = Str(f.GetValue(y));
            if (va != vb) flat.Add((path, f.Name, va, vb, live));
        }
        for (int i = 0; i < Math.Max(x.Children.Count, y.Children.Count); i++)
        {
            var cx = i < x.Children.Count ? x.Children[i] : null;
            var cy = i < y.Children.Count ? y.Children[i] : null;
            Cmp(path + "/" + (cy?.Tag ?? cx?.Tag ?? "?") + "[" + i + "]", cx, cy,
                liveA && x.Display != "none", liveB && y.Display != "none");
        }
        Cmp(path + "::before", x.Before?.Style, y.Before?.Style, liveA && x.Display != "none", liveB && y.Display != "none");
        Cmp(path + "::after", x.After?.Style, y.After?.Style, liveA && x.Display != "none", liveB && y.Display != "none");
    }
    Cmp("body", a, b, true, true);
    int liveDiffs = flat.Count(d => d.Live);

    // BoxShadow/CssFilter/GradientSpec 这类裸引用对象的 ToString 只有类型名，直接比会永远「相等」——
    // 反射展开一层，才看得见里面的渐变层/模糊半径差在哪。
    static string Str(object? v, int depth = 0)
    {
        if (v == null) return "";
        var t = v.GetType();
        if (t == typeof(string)) return (string)v;
        if (v is System.Collections.IEnumerable en && depth < 3)
            return "[" + string.Join(",", en.Cast<object?>().Select(o => Str(o, depth + 1))) + "]";
        if (depth < 3 && v.ToString() == t.FullName)
        {
            var parts = new List<string>();
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) parts.Add(f.Name + "=" + Str(f.GetValue(v), depth + 1));
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetMethod == null || p.GetIndexParameters().Length > 0) continue;
                try { parts.Add(p.Name + "=" + Str(p.GetValue(v), depth + 1)); } catch { }
            }
            return t.Name + "{" + string.Join(",", parts) + "}";
        }
        return v.ToString() ?? "";
    }

    Console.WriteLine($"\n双跑对账：老路 {na} 盒 / 新路 {nb} 盒 · 差异 {flat.Count} 处（会画出来的 {liveDiffs} 处）");
    foreach (var g in flat.GroupBy(d => d.Prop).OrderByDescending(g => g.Count()).Take(25))
        Console.WriteLine($"  {g.Key,-24} {g.Count(),5} 处  会画出来 {g.Count(d => d.Live),4}   例：{g.First().Path}  「{Trim(g.First().Va)}」 → 「{Trim(g.First().Vb)}」");
    if (liveDiffs > 0)
    {
        Console.WriteLine("\n会画出来的差异（前 40 处）：");
        foreach (var d in flat.Where(d => d.Live).Take(40))
            Console.WriteLine($"  {d.Path}  {d.Prop}: 「{Trim(d.Va)}」 → 「{Trim(d.Vb)}」");
    }
    static string Trim(string s) => s.Length <= 60 ? s : s[..60] + "…";

    // 像素级：逐属性差异看着吓人未必看得见，反过来（差异小但画歪）也可能。两边都渲染再比。
    static byte[] RenderTree(PaperSharp.Css.ComputedStyle st, float w)
    {
        var box = LayoutBuilder.ToBoxTree(st);
        LayoutBuilder.Measure(box, w);
        LayoutBuilder.Position(box, 0, 0);
        using var surface = SKSurface.Create(new SKImageInfo((int)w, Math.Max(1, (int)Math.Ceiling(box.TotalHeight))));
        Painter.Paint(surface.Canvas, box, surface);
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
    static (byte[] Png, double MsStyle) Render(string html, string css, float w)
    {
        var (tStyle, st) = Time(() => StyleResolver.BuildDocument(html, css, precomputed: true));
        return (RenderTree(st, w), tStyle);
    }
    using var rscope = RenderScope.Begin(null);
    var (pa, taStyle) = Render(html, css + "\n@media all{}", width);
    var (pb, tbStyle) = Render(html, css, width);
    // 层叠段各自计时：老路若 ≈2700ms 说明确实退回 AngleSharp 逐元素层叠；
    // 若两行都 ≈0.2s 则是 `@media` 弃权没生效、两边同一条路——那 0 像素差就是假绿。
    Console.WriteLine($"\n渲染分路核对：层叠段 老 {taStyle:F0}ms / 新 {tbStyle:F0}ms"
        + $" · 老 {pa.Length:N0}B · 新 {pb.Length:N0}B · PNG 字节完全相同 {pa.AsSpan().SequenceEqual(pb)}");
    File.WriteAllBytes(Path.Combine(outDir, "cascade-old.png"), pa);
    File.WriteAllBytes(Path.Combine(outDir, "cascade-new.png"), pb);
    using var ba = SKBitmap.Decode(pa);
    using var bb = SKBitmap.Decode(pb);
    static (long N, int X0, int Y0, int X1, int Y1) Compare(SKBitmap x, SKBitmap y)
    {
        long n = 0; int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        int wmin = Math.Min(x.Width, y.Width), hmin = Math.Min(x.Height, y.Height);
        for (int r = 0; r < hmin; r++)
            for (int c = 0; c < wmin; c++)
            {
                if (x.GetPixel(c, r) == y.GetPixel(c, r)) continue;
                n++;
                x0 = Math.Min(x0, c); y0 = Math.Min(y0, r); x1 = Math.Max(x1, c); y1 = Math.Max(y1, r);
            }
        return (n, x0, y0, x1, y1);
    }
    void Report(string label, (long N, int X0, int Y0, int X1, int Y1) d, long totalPx)
        => Console.WriteLine($"  {label,-26} 差异像素 {d.N,12:N0} / {totalPx:N0}（{d.N * 100.0 / Math.Max(1, totalPx):F4}%）"
            + (d.X1 < 0 ? "  ·  完全一致" : $"  包围盒 x {d.X0}–{d.X1} · y {d.Y0}–{d.Y1}"));

    Console.WriteLine($"\n像素对账：老 {ba.Width}×{ba.Height} / 新 {bb.Width}×{bb.Height}"
        + (ba.Height == bb.Height ? "" : $"  ← 文档高差 {bb.Height - ba.Height}px"));
    // 「完全一致」必须排除「两张都是空白页」这种假绿：先量各自着墨像素数。
    static long Ink(SKBitmap b)
    {
        var bg = b.GetPixel(2, 2);
        long n = 0;
        for (int r = 0; r < b.Height; r += 3)
            for (int c = 0; c < b.Width; c += 3)
                if (b.GetPixel(c, r) != bg) n++;
        return n;
    }
    Console.WriteLine($"  着墨采样点：老 {Ink(ba):N0} · 新 {Ink(bb):N0}（每 3px 抽一点，全图 {(long)ba.Width * ba.Height / 9:N0} 点）");
    long totalPx = Math.Max(1L, (long)ba.Width * ba.Height);
    Report("老 vs 新", Compare(ba, bb), totalPx);

    // 灵敏度对照①（全局）：往 body 追加高对比底色，比对器必须看见大片差异。
    var (pbg, _) = Render(html, css + "\nbody{background-color:#ff00fe}", width);
    using var bbg = SKBitmap.Decode(pbg);
    Report("对照①（新 vs 新+改底色）", Compare(bb, bbg), totalPx);

    // 灵敏度对照②（局部几何）：底色对照只证明比较器看得见颜色，不证明它看得见「盒子挪了 40px」——
    // 而老/新差异绝大多数正是 margin/padding 这类几何值。直接改一个真正会画的静态块的 padding-left 再渲染。
    static bool IsBlockLevel(string d) => d is "block" or "flex" or "grid" or "flow-root" or "list-item" or "table";
    PaperSharp.Css.ComputedStyle? probeTarget = null; string probePath = ""; int probeSize = -1;
    void Scan(PaperSharp.Css.ComputedStyle n, bool live, string path)
    {
        if (!live) return;
        int size = 0;
        void Count(PaperSharp.Css.ComputedStyle m) { size++; foreach (var c in m.Children) Count(c); }
        Count(n);
        if (IsBlockLevel(n.Display) && size > probeSize) { probeTarget = n; probePath = path; probeSize = size; }
        for (int i = 0; i < n.Children.Count; i++)
            Scan(n.Children[i], n.Children[i].Display != "none", path + "/" + n.Children[i].Tag + "[" + i + "]");
    }
    Scan(b, b.Display != "none", "body");
    var target = probeTarget;
    if (target == null) Console.WriteLine("  对照②跳过：找不到可改的块级节点");
    else
    {
        // 逐项试探：哪一类样式改动真能反映到像素上。同时改 Css 声明值与已解析 float，
        // 排除「Measure 期 float 缓存没失效」这一种假阴性。
        void Probe(string name, Action apply, Action undo)
        {
            apply();
            using var bmp = SKBitmap.Decode(RenderTree(b, width));
            undo();
            Report("对照②" + name, Compare(bb, bmp), totalPx);
        }
        var t = target;
        Console.WriteLine($"  对照②探针节点：{probePath} display={t.Display} position={t.Position} 后代 {probeSize}");
        var bg0 = t.BackgroundColor;
        Probe("底色", () => t.BackgroundColor = new SKColor(255, 0, 254), () => t.BackgroundColor = bg0);
        var (pc0, pf0) = (t.PaddingLeftCss, t.PaddingLeft);
        Probe("padding-left+40", () => { t.PaddingLeftCss = CssSize.Abs(pf0 + 40); t.PaddingLeft = pf0 + 40; },
              () => { t.PaddingLeftCss = pc0; t.PaddingLeft = pf0; });
        var (mc0, mf0) = (t.MarginLeftCss, t.MarginLeft);
        Probe("margin-left+40", () => { t.MarginLeftCss = CssSize.Abs(mf0 + 40); t.MarginLeft = mf0 + 40; },
              () => { t.MarginLeftCss = mc0; t.MarginLeft = mf0; });
        var (oc0, of0, op0) = (t.OffsetLeft, t.OffsetTop, t.Position);
        Probe("relative 右移200下移200", () => { t.Position = "relative"; t.OffsetLeft = CssSize.Abs(200); t.OffsetTop = CssSize.Abs(200); },
              () => { t.Position = op0; t.OffsetLeft = oc0; t.OffsetTop = of0; });
    }
    // 对照③（把真实差异整类搬到新树）：探针只证明「比较器看得见几何」，还要证明「老/新之间真实存在的那批
    // 差异本身就是惰性的」——否则 0 像素差异可能只是巧合。按属性逐类搬：把老路该属性的值覆盖到新路同位置节点，
    // 整类一起渲染比对。哪一类搬完像素动了，哪一类就是真问题。
    static PaperSharp.Css.ComputedStyle? NodeAt(PaperSharp.Css.ComputedStyle root, string path)
    {
        var parts = path.Split('/');
        var cur = root;
        for (int i = 1; i < parts.Length && cur != null; i++)
        {
            int br = parts[i].LastIndexOf('[');
            if (br < 0) return null;
            int idx = int.Parse(parts[i].Substring(br + 1).TrimEnd(']'));
            cur = idx < cur.Children.Count ? cur.Children[idx] : null;
        }
        return cur;
    }
    var fld = typeof(PaperSharp.Css.ComputedStyle).GetFields(BindingFlags.Public | BindingFlags.Instance);
    foreach (var grp in flat.Where(d => d.Live && !d.Path.Contains("::") && d.Prop != "<盒缺失>")
                         .GroupBy(d => d.Prop).OrderByDescending(g => g.Count()))
    {
        var f = fld.FirstOrDefault(x => x.Name == grp.Key);
        if (f == null) continue;
        // 只改 *Css 声明值有可能「改了等于没改」：某些布局路径不走 ResolveBoxSizes，float 才是它用的数。
        // 所以成对的 float 一起写，避免对照③给出一眼假绿。
        var pair = grp.Key.EndsWith("Css") ? fld.FirstOrDefault(x => x.Name == grp.Key[..^3]) : null;
        var undo = new List<(PaperSharp.Css.ComputedStyle N, object? V, object? Pv)>();
        foreach (var d in grp)
        {
            var nOld = NodeAt(a, d.Path); var nNew = NodeAt(b, d.Path);
            if (nOld == null || nNew == null) continue;
            undo.Add((nNew, f.GetValue(nNew), pair?.GetValue(nNew)));
            f.SetValue(nNew, f.GetValue(nOld));
            if (pair != null)
            {
                var old = f.GetValue(nOld);
                var px = old is CssSize csz ? (csz.IsAuto ? 0f : csz.Value) : 0f;
                pair.SetValue(nNew, px);
            }
        }
        if (undo.Count == 0) continue;
        using var bmp = SKBitmap.Decode(RenderTree(b, width));
        foreach (var (n, v, pv) in undo) { f.SetValue(n, v); if (pair != null && pv != null) pair.SetValue(n, pv); }
        Report($"对照③套用老值 {grp.Key}({undo.Count}/{grp.Count()} 节点, {grp.First(d => NodeAt(a, d.Path) != null && NodeAt(b, d.Path) != null).Path}", Compare(bb, bmp), totalPx);
    }
    Console.WriteLine($"  成品图：{Path.Combine(outDir, "cascade-old.png")} · {Path.Combine(outDir, "cascade-new.png")}");
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

    // ReadStyle 对每元素的 GetComputedStyle 都有 try/catch（AngleSharp 遇 linear-gradient 必抛 NRE），
    // 这里照搬，否则整个基准跑不完。
    IWindow window2 = window;
    List<ICssStyleDeclaration?> SafeComputed(IEnumerable<IElement> es)
    {
        var r = new List<ICssStyleDeclaration?>();
        foreach (var e in es) { try { r.Add(window2.GetComputedStyle(e, null)); } catch { r.Add(null); } }
        return r;
    }
    var (tParse, _) = Time(() => ctx.OpenAsync(r => r.Content(full)).GetAwaiter().GetResult());
    var (tComputed, list) = Time(() => SafeComputed(els));
    var (tComputedProps, _) = Time(() => list.Sum(d => props.Sum(p => (d?.GetPropertyValue(p) ?? "").Length)));
    var (tDecls, decls) = Time(() => { var sc = window.GetStyleCollection(); return els.Select(e => sc.GetDeclarations(e, null)).ToList(); });
    var (tDeclProps, _) = Time(() => decls.Sum(d => props.Sum(p => (d?.GetPropertyValue(p) ?? "").Length)));
    var (tBoth, _) = Time(() => els.Select(e =>
    {
        ICssStyleDeclaration? c; try { c = window.GetComputedStyle(e, null); } catch { c = null; }
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
        using var aa = new SKPaint { Typeface = tf, TextSize = size, IsAntialias = true };
        using var both = new SKPaint { Typeface = tf, TextSize = size, IsAntialias = true, SubpixelText = true };
        Console.WriteLine($"  {family,-16} → {tf.FamilyName,-18} 默认 {plain.MeasureText(text),7:F2}  Subpixel {sub.MeasureText(text),7:F2}  Antialias {aa.MeasureText(text),7:F2}  AA+Subpixel {both.MeasureText(text),7:F2}");
    }
    Console.WriteLine($"  文本「{text}」@ {size}px · 浏览器 canvas 参照：system-ui/YaHei 94.89，Segoe UI 89.84");
}

