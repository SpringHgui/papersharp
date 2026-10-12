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

if (args.Length >= 3 && args[0] == "fam")
{
    foreach (var fam in args[1..^1])
    {
        float size = float.Parse(args[^1], System.Globalization.CultureInfo.InvariantCulture);
        var tfx2 = fam.EndsWith(".ttc")||fam.EndsWith(".ttf")||fam.EndsWith(".otf") ? SKTypeface.FromFile(fam) : SKTypeface.FromFamilyName(fam);
        if (tfx2 == null) { Console.WriteLine($"{fam}: null"); continue; }
        using var fnt2 = new SKFont(tfx2, size);
        var m2 = fnt2.Metrics;
        Console.WriteLine($"{fam} -> {tfx2.FamilyName}/{tfx2.FontWeight} asc={-m2.Top:0.###} desc={m2.Descent:0.###} roundSum={(float)Math.Floor(-m2.Top+0.5f)+(float)Math.Floor(m2.Descent+0.5f):0.###} @size={size}");
        tfx2.Dispose();
    }
    return;
}

if (args.Length >= 4 && args[0] == "caps")
{
    // caps <size> <text> <variant> —— 走引擎度量通路，看 smcp 增量是否吃进去
    float size4 = float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    using var f4 = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), size4);
    Console.WriteLine($"plain={TextMeasurer.LineWidth(args[2], f4, 0f):0.###} caps={TextMeasurer.LineWidth(args[2], f4, 0f, args[3]):0.###}");
    return;
}

if (args.Length >= 3 && args[0] == "sct")
{
    // sct <family> <size> <text> —— 直接问 SmallCaps 表要 delta
    var tf3 = SKTypeface.FromFamilyName(args[1]);
    float size3 = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    Console.WriteLine($"face={tf3?.FamilyName}/{tf3?.FontWeight} hasTable={PaperSharp.Layout.SmallCaps.For(tf3) != null}");
    Console.WriteLine($"delta-caps={PaperSharp.Layout.SmallCaps.Delta(args[3], tf3, size3, "small-caps"):0.####}");
    return;
}

if (args.Length >= 4 && args[0] == "wt")
{
    // wt <family> <size> <text> —— 各 CSS 字重下 FromFamilyName 实际选中的面 + 文本宽度
    string fam = args[1];
    float size = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    string text = args[3];
    foreach (int w in new[] { 400, 500, 550, 600, 650, 700 })
    {
        int skw = w >= 600 ? 600 : w >= 500 ? 500 : 400;
        using var tfx = SKTypeface.FromFamilyName(fam, new SKFontStyle((SKFontStyleWeight)skw, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
        if (tfx == null) { Console.WriteLine($"{w}: null"); continue; }
        using var paint = new SKPaint { Typeface = tfx, TextSize = size };
        Console.WriteLine($"cssWeight={w} → {tfx.FamilyName}/{tfx.FontWeight} w={paint.MeasureText(text):0.####}");
    }
    return;
}

if (args.Length >= 1 && args[0] == "minw")
{
    // minw —— 直接走引擎 min-content 通路量 "Pro/Pro+/Ultra"
    var st = new PaperSharp.Css.ComputedStyle { FontFamily = "PingFang SC", FontWeight = 600, FontSize = 20, WhiteSpace = "normal" };
    var tfl = FontManager.Get("PingFang SC", 600);
    Console.WriteLine($"face = {tfl.FamilyName}/{tfl.FontWeight}");
    using var paint = new SKPaint { Typeface = tfl, TextSize = 20 };
    Console.WriteLine($"measureText        = {paint.MeasureText("Pro/Pro+/Ultra"):0.###}");
    Console.WriteLine($"MinContentOfText   = {PaperSharp.Layout.InlineLayout.MinContentOfText("Pro/Pro+/Ultra", st):0.###}");
    var st4 = new PaperSharp.Css.ComputedStyle { FontFamily = "PingFang SC", FontWeight = 400, FontSize = 20, WhiteSpace = "normal" };
    Console.WriteLine($"MinContentOfText400= {PaperSharp.Layout.InlineLayout.MinContentOfText("Pro/Pro+/Ultra", st4):0.###}");
    return;
}

if (args.Length >= 3 && args[0] == "smcp")
{
    // smcp <ttf-file> <size> <text> —— 裸 HarfBuzz 成形：有/无 smcp feature 的宽度与字形对比
    using var blob = HarfBuzzSharp.Blob.FromFile(args[1]);
    using var face = new HarfBuzzSharp.Face(blob, 0);
    using var hbFont = new HarfBuzzSharp.Font(face);
    float size = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    hbFont.SetScale((int)(size * 64), (int)(size * 64));
    foreach (var on in new[] { false, true })
    {
        var feats = on ? new[] { new HarfBuzzSharp.Feature(HarfBuzzSharp.Tag.Parse("smcp"), 1) } : Array.Empty<HarfBuzzSharp.Feature>();
        using var buf = new HarfBuzzSharp.Buffer();
        buf.AddUtf16(args[3]);
        buf.Direction = HarfBuzzSharp.Direction.LeftToRight;
        buf.GuessSegmentProperties();
        hbFont.Shape(buf, feats);
        var gi = buf.GlyphInfos;
        var gp = buf.GlyphPositions;
        float sum = 0; foreach (var p in gp) sum += p.XAdvance;
        Console.WriteLine($"smcp={(on ? 1 : 0)} px64={sum / 64f:0.###} glyphs={string.Join(",", gi.Select(g => g.Codepoint))}");
    }
    return;
}

// 度量口径探针：measure <font-file> <sizePx> <text>
// 打印 SKPaint.MeasureText（默认/LinearMetrics）hmtx 浮点累加、逐字取整累加、PRKT 字距。
if (args.Length >= 4 && args[0] == "measure")
{
    string file = Path.GetFullPath(args[1]);
    float size = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    string text = args[3];
    using var tfx = SKTypeface.FromFile(file);
    PaperSharp.Layout.KernTable.Attach(tfx, file);
    Console.WriteLine($"face={tfx.FamilyName}/{tfx.FontWeight} upm={tfx.UnitsPerEm} size={size:0.####} text=\"{text}\"");
    using (var paint = new SKPaint { Typeface = tfx, TextSize = size })
        Console.WriteLine($"measureText        = {paint.MeasureText(text):0.####}");
    try
    {
        using var fnt = new SKFont(tfx, size);
        var lm = typeof(SKFont).GetProperty("LinearMetrics");
        Console.WriteLine($"SKFont.LinearMetrics prop: {lm != null}");
        if (lm != null) lm.SetValue(fnt, true);
        using var p3 = new SKPaint { Typeface = tfx, TextSize = size };
        ushort[] utf16 = new ushort[text.Length];
        for (int i = 0; i < text.Length; i++) utf16[i] = text[i];
        Console.WriteLine($"skfont measure(lin)  = {fnt.MeasureText(utf16, p3):0.####}");
        if (lm != null) lm.SetValue(fnt, false);
        Console.WriteLine($"skfont measure(hint) = {fnt.MeasureText(utf16, p3):0.####}");
    }
    catch (Exception e) { Console.WriteLine($"skfont measure  ERR {e.GetType().Name}: {e.Message}"); }

    // ── hmtx 直读 ──
    var head = tfx.GetTableData((uint)((0x68 << 24) | 0x65 << 16 | 0x61 << 8 | 0x64)); // 'head'
    var cmap = tfx.GetTableData((uint)((0x63 << 24) | 0x6D << 16 | 0x61 << 8 | 0x70)); // 'cmap'
    var hhea = tfx.GetTableData((uint)((0x68 << 24) | 0x68 << 16 | 0x65 << 8 | 0x61)); // 'hhea'
    var hmtx = tfx.GetTableData((uint)((0x68 << 24) | 0x6D << 16 | 0x74 << 8 | 0x78)); // 'hmtx'
    if (head == null || cmap == null || hhea == null || hmtx == null) { Console.WriteLine("table missing"); return; }
    int upm = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(18));
    int numH = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
    var g2cp = Glyph2Cp(cmap);
    float Sum(bool roundEach)
    {
        float w = 0;
        foreach (var ch in text.EnumerateRunes())
        {
            int cpv = ch.Value;
            if (!g2cp.TryGetValue(cpv, out int g)) { Console.WriteLine($"  no glyph for U+{cpv:X4}"); w += 1000; continue; }
            int adv = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(g * 4));
            float px = adv * size / upm;
            w += roundEach ? (float)Math.Floor(px + 0.5f) : px;
        }
        return w;
    }
    Console.WriteLine($"hmtx float sum     = {Sum(false):0.####}");
    Console.WriteLine($"hmtx round-each    = {Sum(true):0.####}");
    Console.WriteLine($"kern(PRKT/GPOS)    = {PaperSharp.Layout.KernTable.Of(text, tfx, size):0.####}");
    return;
}

static Dictionary<int, int> Glyph2Cp(byte[] cmap)
{
    // cmap format 4 → cp→glyph（够 BMP 用）
    var map = new Dictionary<int, int>();
    int n = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(2));
    int off4 = -1;
    for (int i = 0; i < n; i++)
    {
        int rec = 4 + i * 8;
        ushort plat = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(rec));
        ushort enc = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(rec + 2));
        int sub = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(rec + 4));
        if ((plat == 3 && (enc == 1 || enc == 10)) || (plat == 0 && enc == 3))
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(sub)) == 4) { off4 = sub; break; }
    }
    if (off4 < 0) return map;
    int segX2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(off4 + 6));
    int segs = segX2 / 2;
    int ends = off4 + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
    ushort U16(byte[] t, int p) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(p));
    for (int i = 0; i < segs; i++)
    {
        int end = U16(cmap, ends + i * 2), start = U16(cmap, starts + i * 2);
        int delta = U16(cmap, deltas + i * 2), ro = U16(cmap, ranges + i * 2);
        for (int cp = Math.Max(start, 0x20); cp <= end; cp++)
        {
            int g;
            if (ro == 0) g = (cp + delta) & 0xFFFF;
            else
            {
                int p = ranges + i * 2 + ro + (cp - start) * 2;
                if (p + 1 >= cmap.Length) continue;
                g = U16(cmap, p);
                if (g != 0) g = (g + delta) & 0xFFFF;
            }
            if (g != 0) map.TryAdd(cp, g);
        }
    }
    return map;
}

if (args.Length >= 1 && args[0] == "parallel")
{
    // 并行吞吐探针：T 线程并发渲染 invoice PDF，报告页/秒（线程安全化后的扩容实测）
    int threads = args.Length > 1 ? int.Parse(args[1]) : 8;
    int perThread = args.Length > 2 ? int.Parse(args[2]) : 5;
    string assets = Path.GetFullPath("../../src/PaperSharpSamples/assets");
    var phtml = File.ReadAllText(Path.Combine(assets, "invoice.html"));
    var css = File.ReadAllText(Path.Combine(assets, "invoice.css"));
    PaperSharp.Renderer.RenderPdf(phtml, css, baseDir: assets); // 预热
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
    {
        for (int i = 0; i < perThread; i++) PaperSharp.Renderer.RenderPdf(phtml, css, baseDir: assets);
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

