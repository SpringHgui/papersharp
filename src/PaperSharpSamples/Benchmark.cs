using System.Diagnostics;
using System.Globalization;
using System.Text;
using SkiaSharp;
using PaperSharp;
using PaperSharp.Css;
using PaperSharp.Layout;
using PaperSharp.Imaging;
using PaperSharp.Paint;

namespace PaperSharpSamples;

/// <summary>
/// 可复现的渲染基准测试。两种模式：
/// - bench：进程内预热后，对 9 个样例做 N 次计时（min/中位/均值/p95/max）、逐阶段耗时拆解、内存、规模扩展曲线。
/// - cold：单进程只渲染一个样例（不预热），测"冷启动首帧"（含 JIT + 字体加载 + 图片解码首次触碰）。
/// 所有数字都是本机 Stopwatch 实测，输出为机器可读行，供报告直接引用。
/// </summary>
public static class Benchmark
{
    static readonly string[] Names = { "invoice", "long", "flex", "img", "report", "pct", "polish", "inline", "table" };
    const float PngW = 794f;

    public static void Bench(string assets, string outDir, int iters)
    {
        CultureInfo Ci = CultureInfo.InvariantCulture;
        var docs = Names.Select(n => (n,
            html: File.ReadAllText(Path.Combine(assets, n + ".html")),
            css: File.ReadAllText(Path.Combine(assets, n + ".css")))).ToList();

        // 预热：每个样例各渲一次，触发 JIT + 字体子集加载 + 图片解码缓存（模拟常驻服务的热态）
        foreach (var d in docs) { Renderer.RenderPdf(d.html, d.css, baseDir: assets); Renderer.RenderPng(d.html, d.css, PngW, baseDir: assets); }

        Console.WriteLine("BENCH_BEGIN warm iters=" + iters + " gc=" + (System.Runtime.GCSettings.IsServerGC ? "server" : "workstation"));

        // ── 1) 每样例热态延迟（PDF / PNG）+ 产物体积 ──
        Console.WriteLine("LAT,sample,mode,iters,min_ms,med_ms,mean_ms,p95_ms,max_ms,out_kb");
        foreach (var d in docs)
        {
            byte[] pdf = Renderer.RenderPdf(d.html, d.css, baseDir: assets);
            byte[] png = Renderer.RenderPng(d.html, d.css, PngW, baseDir: assets);
            var sp = Time(() => Renderer.RenderPdf(d.html, d.css, baseDir: assets), iters);
            var sn = Time(() => Renderer.RenderPng(d.html, d.css, PngW, baseDir: assets), iters);
            Row("LAT", d.n, "PDF", sp, pdf.Length / 1024.0);
            Row("LAT", d.n, "PNG", sn, png.Length / 1024.0);
        }

        // ── 2) 逐阶段耗时拆解（PDF 管线，每阶段取 iters 次均值）──
        Console.WriteLine("STAGE,sample,prepare_ms,parse_ms,buildDoc_ms,toBox_ms,measure_ms,paginate_ms,paintEncode_ms,total_ms");
        foreach (var d in docs)
        {
            var acc = new double[7];
            for (int i = 0; i < iters; i++)
            {
                var sw = Stopwatch.StartNew(); double t0 = 0, t1 = 0, t2 = 0, t3 = 0, t4 = 0, t5 = 0;
                using var scope = Prepare(d.css, assets); t0 = sw.Elapsed.TotalMilliseconds;
                var spec = AtPageParser.Parse(d.css, 595.28f, 841.89f); t1 = sw.Elapsed.TotalMilliseconds;
                var computed = StyleResolver.BuildDocument(d.html, d.css); t2 = sw.Elapsed.TotalMilliseconds;
                var body = LayoutBuilder.ToBoxTree(computed); t3 = sw.Elapsed.TotalMilliseconds;
                LayoutBuilder.Measure(body, spec.ContentW); t4 = sw.Elapsed.TotalMilliseconds;
                var pages = Paginator.Paginate(body, spec); t5 = sw.Elapsed.TotalMilliseconds;
                EncodePdf(pages, spec); double t6 = sw.Elapsed.TotalMilliseconds;
                acc[0] += t0; acc[1] += t1 - t0; acc[2] += t2 - t1; acc[3] += t3 - t2;
                acc[4] += t4 - t3; acc[5] += t5 - t4; acc[6] += t6 - t5;
            }
            for (int k = 0; k < 7; k++) acc[k] /= iters;
            double total = acc.Sum();
            Console.WriteLine(string.Join(",", new[] { "STAGE", d.n }.Concat(acc.Select(v => v.ToString("0.000", Ci))).Append(total.ToString("0.000", Ci))));
        }

        // ── 3) 内存：托管堆增量 + 进程工作集 ──
        Console.WriteLine("MEM,sample,managed_delta_kb,working_set_mb,peak_output_kb");
        foreach (var d in docs)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalMemory(true);
            long ws0 = Process.GetCurrentProcess().WorkingSet64;
            byte[] pdf = Renderer.RenderPdf(d.html, d.css, baseDir: assets);
            long after = GC.GetTotalMemory(false);
            long ws1 = Process.GetCurrentProcess().WorkingSet64;
            Console.WriteLine($"MEM,{d.n},{(after - before) / 1024.0:0},{ws1 / 1048576.0:0.0},{pdf.Length / 1024.0:0}");
        }

        // ── 4) 规模扩展：只测 CPU 布局（BuildDocument+ToBoxTree+Measure+Position），不含光栅/PNG 编码，
        //        以隔离布局复杂度（避免超大位图编码干扰），并暴露表格定位的复杂度。──
        Console.WriteLine("SCALE,kind,n_rows,buildDoc_ms,toBox_ms,measure_ms,position_ms,layout_total_ms,us_per_row");
        foreach (int n in new[] { 50, 200, 800, 2000 })
        {
            var (th, tc) = SynthTable(n);
            LayoutScale("table", th, tc, assets, n);
            var (fh, fc) = SynthFlex(n);
            LayoutScale("flexlist", fh, fc, assets, n);
        }

        // ── 5) 吞吐：小样例连续渲染每秒次数 ──
        {
            var d = docs[0];
            int done = 0; var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 3) { Renderer.RenderPdf(d.html, d.css, baseDir: assets); done++; }
            Console.WriteLine($"THROUGHPUT,sample=invoice,rps={done / sw.Elapsed.TotalSeconds:0.0}");
        }
        Console.WriteLine("BENCH_END");

        void Row(string tag, string sample, string mode, (double min, double med, double mean, double p95, double max) s, double kb)
            => Console.WriteLine($"{tag},{sample},{mode},{iters},{s.min.ToString("0.000", Ci)},{s.med.ToString("0.000", Ci)},{s.mean.ToString("0.000", Ci)},{s.p95.ToString("0.000", Ci)},{s.max.ToString("0.000", Ci)},{kb.ToString("0", Ci)}");
    }

    /// <summary>只跑布局复杂度扫描（纯 CPU，秒级完成，抗光栅/GC 噪声），用于展示 table 的定位复杂度增长。</summary>
    public static void ScaleOnly(string assets, int[]? rows = null)
    {
        Console.WriteLine("SCALE,kind,n_rows,buildDoc_ms,toBox_ms,measure_ms,position_ms,layout_total_ms,us_per_row");
        foreach (int n in rows ?? new[] { 100, 200, 400, 800 })
        {
            var (th, tc) = SynthTable(n); LayoutScale("table", th, tc, assets, n);
            var (fh, fc) = SynthFlex(n); LayoutScale("flexlist", fh, fc, assets, n);
        }
    }

    public static void Cold(string assets, string name)
    {
        var html = File.ReadAllText(Path.Combine(assets, name + ".html"));
        var css = File.ReadAllText(Path.Combine(assets, name + ".css"));
        // 不预热：这两次计时就是进程内"首帧"，含 JIT + 字体子集从文件加载 + 图片首次解码
        var sw = Stopwatch.StartNew(); Renderer.RenderPdf(html, css, baseDir: assets); double pdf = sw.Elapsed.TotalMilliseconds;
        sw.Restart(); Renderer.RenderPng(html, css, PngW, baseDir: assets); double png = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"COLD,{name},{pdf.ToString("0.000", CultureInfo.InvariantCulture)},{png.ToString("0.000", CultureInfo.InvariantCulture)}");
    }

    static RenderScope Prepare(string css, string? baseDir)
    {
        var scope = RenderScope.Begin(baseDir);
        FontFaceParser.ParseAndRegister(css);
        return scope;
    }

    static byte[] EncodePdf(List<Page> pages, PageSpec spec)
    {
        using var ms = new MemoryStream();
        using (var doc = SKDocument.CreatePdf(ms))
        {
            for (int i = 0; i < pages.Count; i++)
            {
                using var canvas = doc.BeginPage(spec.PageW, spec.PageH);
                PagePainter.PaintPage(canvas, pages[i], spec.PageW, spec.PageH);
                MarginBoxRenderer.Render(canvas, spec, i + 1, pages.Count);
                doc.EndPage();
            }
            doc.Close();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Playground 路径逐阶段拆解（`RenderRasterWithBoxes`，与页面默认档一致：宽 1284、scale 2、domPath 开）。
    /// 与 `bench`（PDF 管线）分开：Playground 的瓶颈是长图位图 + 编码 + 盒采集，PDF 那边是字体子集写入，
    /// 两条路的优化点完全不同，混在一张表里只会指错方向。
    ///
    /// 逐阶段取 N 次的**最小值**：这条路径每轮都要新建几十 MB 画布，均值会被随机落点的 GC 污染
    /// （同一份输入实测 buildDoc 均值能在 86ms 与 161ms 之间飘），最小值才接近「这段代码自己要多少 CPU」。
    /// e2e 另取中位数（用户实际感到的延迟）与最小值（无 GC 干扰的下限），三个口径一起看才不会被单一数字骗。
    /// </summary>
    public static void Playground(string assets, int iters, string[]? only = null)
    {
        const float W = 1284f, S = 2f;
        var names = Directory.EnumerateFiles(assets, "*.html").Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is { Length: > 0 } && !n!.StartsWith('_') && (only == null || only.Contains(n!)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        CultureInfo Ci = CultureInfo.InvariantCulture;
        Console.WriteLine("PG,sample,px_Mpx,prepare,buildDoc,toBox,measure,position,paint,encode,stage_min_sum,e2e_med,e2e_min,collect,boxes");
        foreach (var n in names!)
        {
            var html = File.ReadAllText(Path.Combine(assets, n + ".html"));
            var css = File.Exists(Path.Combine(assets, n + ".css")) ? File.ReadAllText(Path.Combine(assets, n + ".css")) : "";

            var first = Renderer.RenderRasterWithBoxes(html, css, W, baseDir: assets, scale: S);   // 预热 + 拿尺寸
            double mpx = (double)first.PixelWidth * first.PixelHeight / 1_000_000;
            var best = Enumerable.Repeat(double.MaxValue, 7).ToArray();

            void Stage(int k, ref double since, Stopwatch sw)
            {
                double now = sw.Elapsed.TotalMilliseconds;
                best[k] = Math.Min(best[k], now - since);
                since = now;
            }

            for (int i = 0; i < iters; i++)
            {
                // 逐阶段复刻 RasterLayout（含 @page 边距口径，编码器与生产同一派发规则）
                var sw = Stopwatch.StartNew(); double since = 0;
                using var scope = Prepare(css, assets);
                var spec = AtPageParser.Parse(css, 595.28f, 841.89f);
                float mL = spec.MarginLeft, mR = spec.MarginRight, mT = spec.MarginTop, mB = spec.MarginBottom;
                Stage(0, ref since, sw);
                var computed = StyleResolver.BuildDocument(html, css, null, false, W, domPath: true); Stage(1, ref since, sw);
                var body = LayoutBuilder.ToBoxTree(computed); Stage(2, ref since, sw);
                LayoutBuilder.Measure(body, Math.Max(1, W - mL - mR)); Stage(3, ref since, sw);
                LayoutBuilder.Position(body, mL, mT); Stage(4, ref since, sw);
                int pw = (int)Math.Round(W * S), ph = Math.Max(100, (int)Math.Ceiling((body.TotalHeight + mT + mB) * S));
                using var surface = SKSurface.Create(new SKImageInfo(pw, ph));
                surface.Canvas.Clear(SKColors.White);
                surface.Canvas.Scale(S);
                Painter.Paint(surface.Canvas, body, surface); Stage(5, ref since, sw);
                using var img = surface.Snapshot();
                EncodeLike(img);       // 与 Renderer.Encode 同一派发：开着快路就走 PngEncoder
                Stage(6, ref since, sw);
            }
            var sp = Time(() => Renderer.RenderRasterWithBoxes(html, css, W, baseDir: assets, scale: S), iters);
            double pngOnly = Time(() => Renderer.RenderPng(html, css, W, baseDir: assets, scale: S), iters).min;
            Console.WriteLine("PG," + n + "," + mpx.ToString("0.0", Ci) + "," +
                string.Join(",", best.Select(v => v.ToString("0.00", Ci))) + "," +
                best.Sum().ToString("0.00", Ci) + "," + sp.med.ToString("0.0", Ci) + "," + sp.min.ToString("0.0", Ci) + "," +
                (sp.min - pngOnly).ToString("0.00", Ci) + "," + first.Boxes.Count);
        }
    }

    /// <summary>复刻 <c>Renderer</c> 内部的编码派发，让逐阶段拆解量到的是生产真正走的那条路。</summary>
    static void EncodeLike(SKImage img)
    {
        if (Renderer.UseFastPng && PaperSharp.Imaging.PngEncoder.Supports(img))
        {
            PaperSharp.Imaging.PngEncoder.Encode(img);
            return;
        }
        using var d = img.Encode(SKEncodedImageFormat.Png, 100);
        d.ToArray();
    }

    /// <summary>
    /// 画布生命周期专项（`dotnet run -c Release -- pgalloc [iters] [样例名…]`）。
    /// Playground 每次渲染都新建一张 1284×scale 的巨型 SKSurface：分配本身、第一次写入的缺页、
    /// 以及 Dispose 的开销都记在「编码」段里，看不出是谁在花钱。这里把三者拆开：
    ///   create   = SKSurface.Create + Dispose（不画任何东西）
    ///   clear    = 复用同一张画布只做 Clear
    ///   fresh    = 新建 + Clear（每轮都换新的）
    ///   reuse    = 复用 + Clear + 画 + 编码，对照 fresh + 同样动作
    /// 只有 reuse 明显便宜，才值得为画布加缓存池。
    ///
    /// 实测结论（本机，2026-09-24）：m1（85MB 画布）create≈0.0ms、fresh-clear 20.9ms == reuse-clear 20.9ms
    /// ——Clear 就是按内存带宽刷一遍，与画布新旧无关；整条链 reuse 77.4ms vs fresh 106.4ms，
    /// 省下的 29ms 只占该样例端到端（1.06s）的 2.7%，却要常驻一张 85MB 原生缓冲。
    /// **据此决定不做画布池**，优化方向回到 buildDoc 与编码本身。
    /// </summary>
    public static void CanvasLifecycle(string assets, int iters, string[]? only = null)
    {
        const float W = 1284f, S = 2f;
        CultureInfo Ci = CultureInfo.InvariantCulture;
        var names = Directory.EnumerateFiles(assets, "*.html").Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is { Length: > 0 } && !n!.StartsWith('_') && (only == null || only.Contains(n!)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine("ALC,sample,MB,create,fresh-clear,reuse-clear,reuse-e2e,fresh-e2e");
        foreach (var n in names!)
        {
            var html = File.ReadAllText(Path.Combine(assets, n + ".html"));
            var css = File.Exists(Path.Combine(assets, n + ".css")) ? File.ReadAllText(Path.Combine(assets, n + ".css")) : "";
            using var scope = Prepare(css, assets);
            var spec = AtPageParser.Parse(css, 595.28f, 841.89f);
            var computed = StyleResolver.BuildDocument(html, css, null, false, W, domPath: true);
            var body = LayoutBuilder.ToBoxTree(computed);
            LayoutBuilder.Measure(body, Math.Max(1, W - spec.MarginLeft - spec.MarginRight));
            LayoutBuilder.Position(body, spec.MarginLeft, spec.MarginTop);
            int pw = (int)Math.Round(W * S), ph = Math.Max(100, (int)Math.Ceiling((body.TotalHeight + spec.MarginTop + spec.MarginBottom) * S));
            double mb = (double)pw * ph * 4 / 1e6;

            double create = Time(() => { SKSurface.Create(new SKImageInfo(pw, ph)).Dispose(); }, iters).med;
            double freshClear = Time(() => { using var s = SKSurface.Create(new SKImageInfo(pw, ph)); s.Canvas.Clear(SKColors.White); }, iters).med;
            double reuseE2e = 0;
            {
                using var keep = SKSurface.Create(new SKImageInfo(pw, ph));
                keep.Canvas.Clear(SKColors.White);
                reuseE2e = Time(() =>
                {
                    keep.Canvas.Clear(SKColors.White);
                    keep.Canvas.Save();
                    keep.Canvas.Scale(S);
                    Painter.Paint(keep.Canvas, body, keep);
                    keep.Canvas.Restore();
                    using var img = keep.Snapshot();
                    PaperSharp.Imaging.PngEncoder.Encode(img);
                }, iters).med;
            }
            double freshE2e = Time(() =>
            {
                using var s = SKSurface.Create(new SKImageInfo(pw, ph));
                s.Canvas.Clear(SKColors.White);
                s.Canvas.Scale(S);
                Painter.Paint(s.Canvas, body, s);
                using var img = s.Snapshot();
                PaperSharp.Imaging.PngEncoder.Encode(img);
            }, iters).med;
            Console.WriteLine("ALC," + n + "," + mb.ToString("0", Ci) + "," + create.ToString("0.0", Ci) + "," +
                freshClear.ToString("0.0", Ci) + "," + (freshClear - create).ToString("0.0", Ci) + "," +
                reuseE2e.ToString("0.0", Ci) + "," + freshE2e.ToString("0.0", Ci));
        }
    }

    static (double min, double med, double mean, double p95, double max) Time(Action a, int iters)
    {
        var t = new double[iters];
        for (int i = 0; i < iters; i++) { var sw = Stopwatch.StartNew(); a(); t[i] = sw.Elapsed.TotalMilliseconds; }
        Array.Sort(t);
        double mean = t.Average();
        int p95i = Math.Min(iters - 1, (int)Math.Ceiling(0.95 * (iters - 1)));
        return (t[0], t[iters / 2], mean, t[p95i], t[iters - 1]);
    }

    /// <summary>只测布局四阶段（不光栅/不编码），取 5 次中位，隔离 CPU 布局复杂度。</summary>
    static void LayoutScale(string kind, string html, string css, string assets, int rows)
    {
        using var scope = Prepare(css, assets);
        LayoutBuilder.Measure(LayoutBuilder.ToBoxTree(StyleResolver.BuildDocument(html, css)), PngW); // 预热
        int K = 5;
        var bd = new double[K]; var tb = new double[K]; var me = new double[K]; var po = new double[K]; var tot = new double[K];
        for (int i = 0; i < K; i++)
        {
            var sw = Stopwatch.StartNew();
            var computed = StyleResolver.BuildDocument(html, css); double c0 = sw.Elapsed.TotalMilliseconds;
            var body = LayoutBuilder.ToBoxTree(computed); double c1 = sw.Elapsed.TotalMilliseconds;
            LayoutBuilder.Measure(body, PngW); double c2 = sw.Elapsed.TotalMilliseconds;
            LayoutBuilder.Position(body, 0, 0); double c3 = sw.Elapsed.TotalMilliseconds;
            bd[i] = c0; tb[i] = c1 - c0; me[i] = c2 - c1; po[i] = c3 - c2; tot[i] = c3;
        }
        double M(double[] a) { Array.Sort(a); return a[a.Length / 2]; }
        double b = M(bd), t = M(tb), m = M(me), p = M(po), total = M(tot);
        Console.WriteLine($"SCALE,{kind},{rows},{b:0.000},{t:0.000},{m:0.000},{p:0.000},{total:0.000},{total * 1000.0 / rows:0.0}");
    }

    internal static (string html, string css) SynthTable(int rows)
    {
        var sb = new StringBuilder("<table><thead><tr><th>项目</th><th>数量</th><th>单价</th><th>金额</th></tr></thead><tbody>");
        for (int i = 0; i < rows; i++)
            sb.Append($"<tr><td>零件 {i}</td><td>{i * 7 % 999}</td><td>¥{(i % 90) + 1}.00</td><td>¥{i * 13}.00</td></tr>");
        sb.Append("</tbody></table>");
        return (sb.ToString(), "table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:4px 8px}body{font-family:sans-serif;font-size:13px}");
    }

    internal static (string html, string css) SynthFlex(int items)
    {
        var sb = new StringBuilder("<div class='list'>");
        for (int i = 0; i < items; i++)
            sb.Append($"<div class='row'><span class='l'>标签 {i}</span><span class='r'>¥{i * 3}</span></div>");
        sb.Append("</div>");
        return (sb.ToString(), ".list{display:flex;flex-direction:column}.row{display:flex;justify-content:space-between;padding:3px 6px;border-bottom:1px solid #eee}.l{flex:1}.r{color:#333}body{font-family:sans-serif;font-size:13px}");
    }
}
