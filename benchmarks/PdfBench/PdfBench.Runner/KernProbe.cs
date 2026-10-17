using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using PaperSharp.Layout;

namespace PdfBench.Runner;

/// <summary>临时探针：系统字体能不能拿到字距（Skia 的 kerning API / 原始表 / HarfBuzz 默认特性）。</summary>
public static class KernProbe
{
    static uint Tag(string t) => (uint)((t[0] << 24) | (t[1] << 16) | (t[2] << 8) | t[3]);

    /// <summary>引擎侧「family+weight → 实际字面 + 度量的三段拆解」：hmtx / 字距 / 合计。</summary>
    /// <summary>反射调 KernTable 的私有解析步骤，看哪一环空了。</summary>
    static void Dump()
    {
        var kt = typeof(KernTable);
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var table = kt.GetMethod("Table", bf)!;
        var cmap = kt.GetMethod("CmapReps", bf)!;
        var gpos = kt.GetMethod("GposRows", bf)!;
        var tf = SKTypeface.FromFamilyName("Segoe UI",
            new SKFontStyle(SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
        var g = (byte[]?)table.Invoke(null, new object[] { tf, "GPOS" });
        Console.WriteLine($"GPOS bytes = {g?.Length ?? -1}");
        var reps = (Dictionary<int, int>?)cmap.Invoke(null, new object[] { tf });
        Console.WriteLine($"cmap reps = {reps?.Count ?? -1}  'A'->? cp keys sample: " +
            string.Join(",", (reps ?? new()).Values.Take(8)));
        var rows = (System.Collections.IList?)gpos.Invoke(null, new object?[] { tf, reps });
        Console.WriteLine($"gpos rows = {rows?.Count.ToString() ?? "null"}");
        if (rows != null)
        {
            int ascii = 0, shown = 0;
            foreach (var r in rows)
            {
                var t0 = r.GetType();
                int a = (int)t0.GetField("Item1")!.GetValue(r)!;
                int b = (int)t0.GetField("Item2")!.GetValue(r)!;
                float e = (float)t0.GetField("Item3")!.GetValue(r)!;
                if (a < 128 && b < 128) ascii++;
                if (shown++ < 12) Console.WriteLine($"   row {a},{b} = {e:F5}em  ('{(char)a}' '{(char)b}')");
            }
            Console.WriteLine($"   ascii pairs = {ascii}");
        }
    }

    static int Faces()
    {
        Dump();
        var cases = new (string, float)[]
        {
            ("bold", 10.5f), ("Page 1 - Typography & Text", 10f), ("Page 1 — Typography & Text", 10f),
            ("TAVoWyA!", 40f), ("PDFBENCH · VISUAL CHALLENGE", 13f), ("Small Caps", 10.5f),
        };
        foreach (var (fam, w) in new (string, int)[]
        {
            ("Segoe UI", 400), ("Segoe UI", 700), ("Arial", 400), ("Times New Roman", 400),
            ("Microsoft YaHei", 400), ("Microsoft YaHei", 700), ("sans-serif", 400), ("serif", 400),
        })
        {
            var tf = FontManager.Get(fam, w);
            Console.WriteLine($"== {fam}/{w} → '{tf.FamilyName}' w={tf.FontWeight} kernTable={KernTable.HasTable(tf)}");
            foreach (var (s, size) in cases)
            {
                using var p = new SKPaint { Typeface = tf, TextSize = size };
                float h = p.MeasureText(s);
                float k = KernTable.Of(s, tf, size);
                float wot = TextShaper.WidthOf(s, tf, size, 0);
                Console.WriteLine($"   {s,-30} hmtx={h,8:F3} kern={k,7:F3} sum={h + k,8:F3} WidthOf={wot,8:F3}");
            }
        }
        return 0;
    }

    /// <summary>样式树里每个文本节点：CSS family/weight → 实际字面 + 整串宽（定位「度量用了哪张 face」）。</summary>
    static void Faces2(string htmlPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(htmlPath))!;
        var cssPath = Path.ChangeExtension(htmlPath, ".css");
        var css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";
        var root = global::PaperSharp.Css.StyleResolver.BuildDocument(
            File.ReadAllText(htmlPath), css, viewportWidth: 794f, domPath: true);
        void Walk(global::PaperSharp.Css.ComputedStyle s)
        {
            var txt = s.TextContent;
            if (!string.IsNullOrEmpty(txt))
            {
                var tf = FontManager.Get(s.FontFamily, s.FontWeight);
                using var p = new SKPaint { Typeface = tf, TextSize = s.FontSize };
                Console.WriteLine($"{s.DomPath} fam='{s.FontFamily}' w={s.FontWeight} → '{tf.FamilyName}'/{tf.FontWeight}" +
                    $" fs={s.FontSize:F2} txt=[{txt}] hmtx={p.MeasureText(txt):F3} WidthOf={TextShaper.WidthOf(txt, tf, s.FontSize, s.LetterSpacing):F3}");
            }
            foreach (var c in s.Children) Walk(c);
        }
        Walk(root);
    }

    /// <summary>small-caps 合成口径对表：逐字符大写/小写 advance + x-height/cap-height + 候选公式。</summary>
    static void SmallCaps()
    {
        var tf = FontManager.Get("Segoe UI", 400);
        const float size = 10.5f;
        using var p = new SKPaint { Typeface = tf, TextSize = size };
        using var fnt = new SKFont(tf, size);
        var m = fnt.Metrics;
        Console.WriteLine($"face='{tf.FamilyName}' xHeight={m.XHeight:F3} top={m.Top:F3} asc={m.Ascent:F3} desc={m.Descent:F3}");
        // OS/2 的 sCapHeight（version≥2 才有），偏移 88
        var os2 = tf.GetTableData((uint)((('O' << 24) | ('S' << 16) | ('/' << 8) | '2')));
        if (os2 != null && os2.Length >= 90)
        {
            int ver = (os2[2] << 8) | os2[3];
            int capH = (short)((os2[88] << 8) | os2[89]);
            Console.WriteLine($"OS/2 version={ver} sCapHeight={capH} upm={tf.UnitsPerEm} → capPx={capH * size / tf.UnitsPerEm:F3}");
            Console.WriteLine($"k(=xHeight/capHeight)={m.XHeight / (capH * size / tf.UnitsPerEm):F4}");
        }
        string[] probes = ["Small Caps", "SMALL CAPS", "SmallCAPS", "small caps", "SmallCaps", "SMALLCAPS", "效果"];
        foreach (var s in probes) Console.WriteLine($"  {s,-12} w={p.MeasureText(s),8:F3}");
        foreach (var ch in "SMALLCAPsmalilcapseff")
            Console.WriteLine($"   '{ch}' {p.MeasureText(ch.ToString()),7:F3}" +
                $"  upper {p.MeasureText(char.ToUpper(ch).ToString()),7:F3}" +
                $"  ratio {(p.MeasureText(ch.ToString()) / p.MeasureText(char.ToUpper(ch).ToString())):F4}");
        // 候选公式：小写字母换成大写后按 k 缩放
        float k = (float)(m.XHeight / Math.Max(0.001, os2 != null && os2.Length >= 90
            ? (((short)((os2[88] << 8) | os2[89])) * size / tf.UnitsPerEm) : m.XHeight));
        float SumAdv(SKPaint pp, string s) => pp.MeasureText(s);
        float FitA(string s)
        {
            float w = 0;
            foreach (var ch in s)
                w += char.IsLower(ch) ? SumAdv(p, char.ToUpper(ch).ToString()) * k : SumAdv(p, ch.ToString());
            return w;
        }
        Console.WriteLine($"  fitA('Small Caps')={FitA("Small Caps"):F3}  fitA('SmallCaps')={FitA("SmallCaps"):F3}  (Chrome 75.50 / 48.75 含 效果)");
        float notoY = FontManager.Get("Microsoft YaHei", 400).UnitsPerEm;
        using var pn = new SKPaint { Typeface = FontManager.Get("Microsoft YaHei", 400), TextSize = size };
        Console.WriteLine($"  效果@yaheiSub={pn.MeasureText("效果"):F3} plain total={(FitA("Small Caps") + pn.MeasureText("效果")):F3}");
        _ = notoY;
    }

    /// <summary>某个字族在 100..900 各请求字重下实际落到哪个面、行盒基线（ascent+descent 取整）。</summary>
    static void FaceWeights(string fam, float size)
    {
        foreach (var w in new[] { 100, 200, 300, 350, 400, 500, 600, 700, 800, 900 })
        {
            var st = new SKFontStyle((SKFontStyleWeight)w, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            using var tf = SKTypeface.FromFamilyName(fam, st);
            if (tf == null) { Console.WriteLine($"  {fam} req{w}: null"); continue; }
            using var f = new SKFont(tf, size);
            var m = f.Metrics;
            float a = -m.Ascent, d = m.Descent;
            Console.WriteLine($"  {fam} req{w} → '{tf.FamilyName}'/{tf.FontWeight}" +
                $" asc={a,6:F3} desc={d,6:F3} raw={a + d,6:F3} round={(float)Math.Floor(a + 0.5) + Math.Floor(d + 0.5),5:F1}" +
                $" leading={m.Leading,6:F3} xh={m.XHeight,6:F3} cap={m.CapHeight,6:F3}");
        }
    }

    /// <summary>一个字面在 OS/2 / hhea / Skia 三套纵向度量下的 px 值：用来认 Chrome 行盒 15px 出自哪套口径。</summary>
    static void VerticalTables(string fam)
    {
        foreach (var w in new[] { 100, 200, 300, 400, 700 })
        {
            var st = new SKFontStyle((SKFontStyleWeight)w, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            using var tf = SKTypeface.FromFamilyName(fam, st);
            if (tf == null) continue;
            float upm = tf.UnitsPerEm, s = 10.5f / upm;
            using var f = new SKFont(tf, 10.5f);
            var m = f.Metrics;
            Console.WriteLine($"-- {fam} req{w} → '{tf.FamilyName}'/{tf.FontWeight} upm={(int)upm}  " +
                $"skia a={-m.Ascent:F3} d={m.Descent:F3} sum={m.Descent - m.Ascent:F3} leading={m.Leading:F3}");
            var os2 = tf.GetTableData(Tag("OS/2"));
            if (os2 != null && os2.Length >= 88)
            {
                int Ver() => (short)((os2[0] << 8) | os2[1]);
                int i16(int o) => (short)((os2[o] << 8) | os2[o + 1]);
                int u16(int o) => (os2[o] << 8) | os2[o + 1];
                Console.WriteLine($"   OS/2 v{Ver()} typo a={i16(68) * s,6:F3} d={-i16(70) * s,6:F3} gap={i16(72) * s,6:F3}" +
                    $" | win a={u16(74) * s,6:F3} d={u16(76) * s,6:F3}" +
                    $" | xh={i16(86) * s,6:F3} cap={i16(88) * s,6:F3}");
                Console.WriteLine($"      和：typo+gap={(i16(68) - i16(70) + i16(72)) * s,6:F3}  win={(u16(74) + u16(76)) * s,6:F3}");
            }
            var hh = tf.GetTableData(Tag("hhea"));
            if (hh != null && hh.Length >= 12)
            {
                int i16(int o) => (short)((hh[o] << 8) | hh[o + 1]);
                Console.WriteLine($"   hhea a={i16(4) * s,6:F3} d={-i16(6) * s,6:F3} gap={i16(8) * s,6:F3}" +
                    $" → a+d={(i16(4) - i16(6)) * s,6:F3}  a+d+gap={(i16(4) - i16(6) + i16(8)) * s,6:F3}");
            }
        }
    }

    /// <summary>系统里所有中文族字体在 10.5px 下的「取整后 ascent+descent」，用来认 Chrome 行盒 15px 是谁。</summary>
    static void CjkFaces()
    {
        var mgr = SKFontManager.Default;
        var names = mgr.FontFamilies
            .Where(n => n.Any(ch => ch > 0x2E80 && ch < 0xA000)
                     || n.Contains("YaHei", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("SimSun", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("SimHei", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("NSimSun", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Kai", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Fang", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("DengXian", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("JhengHei", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Noto", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Gothic", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Mincho", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Song", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Hei", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Ming", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("PingFang", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        Console.WriteLine($"候选族 {names.Count} 个");
        foreach (var n in names)
            foreach (var w in new[] { 100, 200, 300, 400, 700 })
            {
                var st = new SKFontStyle(w switch
                {
                    <= 100 => SKFontStyleWeight.Thin,
                    200 => SKFontStyleWeight.ExtraLight,
                    300 => SKFontStyleWeight.Light,
                    400 => SKFontStyleWeight.Normal,
                    _ => SKFontStyleWeight.Bold,
                }, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
                using var tf = SKTypeface.FromFamilyName(n, st);
                if (tf == null) continue;
                using var f = new SKFont(tf, 10.5f);
                var m = f.Metrics;
                float a = -m.Ascent, d = m.Descent;
                Console.WriteLine($"  {n,-28} req{w} → '{tf.FamilyName}'/{tf.FontWeight} " +
                    $"asc={a,6:F3} desc={d,6:F3} round={(float)Math.Floor(a + 0.5) + Math.Floor(d + 0.5),5:F1} raw={a + d,6:F3}");
            }
    }

    public static int Run(string[] args)
    {
        if (args.Length >= 2 && args[1] == "faces") return Faces();
        if (args.Length >= 2 && args[1] == "sc") { SmallCaps(); return 0; }
        if (args.Length >= 2 && args[1] == "cjk") { CjkFaces(); return 0; }
        if (args.Length >= 3 && args[1] == "lh")
        {
            FaceWeights(args[2], args.Length >= 4 ? float.Parse(args[3]) : 10.5f);
            return 0;
        }
        if (args.Length >= 3 && args[1] == "os2") { VerticalTables(args[2]); return 0; }
        if (args.Length >= 3 && args[1] == "style") { Faces2(args[2]); return 0; }
        foreach (var fam in new[] { "Segoe UI", "Arial", "Times New Roman", "Microsoft YaHei" })
        {
            foreach (var w in new[] { 400, 700 })
            {
                var tf = SKTypeface.FromFamilyName(fam,
                    w >= 700 ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                    SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
                if (tf == null) { Console.WriteLine($"{fam}/{w}: null"); continue; }
                Console.WriteLine($"--- {fam} {w} actual='{tf.FamilyName}' upm={tf.UnitsPerEm} tables={tf.TableCount}");
                foreach (var t in new[] { "kern", "GPOS", "GSUB", "cmap", "head" })
                {
                    byte[]? d = null;
                    try { d = tf.GetTableData(Tag(t)); } catch (Exception ex) { Console.WriteLine($"  {t}: throw {ex.GetType().Name}"); continue; }
                    Console.WriteLine($"  {t}: {(d == null ? "null" : d.Length + "B")}");
                }
                ProbeKernApi(tf);
            }
        }
        HarfBuzzWidth();
        return 0;
    }

    static void ProbeKernApi(SKTypeface tf)
    {
        const string s = "TAVoWyA!ToTa";
        ushort[]? glyphs = null;
        try { tf.GetGlyphs(s, out glyphs); } catch (Exception ex) { Console.WriteLine($"  GetGlyphs throw {ex.GetType().Name}"); return; }
        if (glyphs == null) { Console.WriteLine("  GetGlyphs null"); return; }
        try
        {
            int[]? adj = tf.GetKerningPairAdjustments(glyphs);
            Console.WriteLine("  KernAdj(hmtx-order): " + (adj == null ? "null" : string.Join(",", adj)));
        }
        catch (Exception ex) { Console.WriteLine($"  KernAdj throw {ex.GetType().Name} {ex.Message}"); }
        using var paint = new SKPaint { Typeface = tf, TextSize = 40 };
        Console.WriteLine($"  MeasureText(\"{s}\")@40 = {paint.MeasureText(s):F3}");
    }

    static void HarfBuzzWidth()
    {
        const string s = "TAVoWyA!";
        foreach (var fam in new[] { "Segoe UI", "Arial" })
        {
            var tf = SKTypeface.FromFamilyName(fam, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            using var paint = new SKPaint { Typeface = tf, TextSize = 40 };
            using var shaper = new SKShaper(tf);
            var res = shaper.Shape(s, paint);
            float sum = 0;
            if (res?.Points is { } pos) foreach (var p in pos) sum += p.X;
            Console.WriteLine($"HB {fam} Bold shaped@40 res.Width={res?.Width ?? -1:F3} sumPos.X={sum:F3} hmtx={paint.MeasureText(s):F3} npts={res?.Points?.Length ?? -1}");
        }
    }
}
