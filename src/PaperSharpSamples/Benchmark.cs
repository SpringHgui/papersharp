using System.Diagnostics;
using System.Globalization;
using System.Text;
using SkiaSharp;
using PaperSharp;
using PaperSharp.Css;
using PaperSharp.Layout;
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
                Prepare(d.css, assets); t0 = sw.Elapsed.TotalMilliseconds;
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
    public static void ScaleOnly(string assets)
    {
        Console.WriteLine("SCALE,kind,n_rows,buildDoc_ms,toBox_ms,measure_ms,position_ms,layout_total_ms,us_per_row");
        foreach (int n in new[] { 100, 200, 400, 800 })
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

    static void Prepare(string css, string? baseDir)
    {
        ImageLoader.BaseDir = baseDir; FontManager.BaseDir = baseDir;
        FontFaceParser.ParseAndRegister(css);
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
        Prepare(css, assets);
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

    static (string html, string css) SynthTable(int rows)
    {
        var sb = new StringBuilder("<table><thead><tr><th>项目</th><th>数量</th><th>单价</th><th>金额</th></tr></thead><tbody>");
        for (int i = 0; i < rows; i++)
            sb.Append($"<tr><td>零件 {i}</td><td>{i * 7 % 999}</td><td>¥{(i % 90) + 1}.00</td><td>¥{i * 13}.00</td></tr>");
        sb.Append("</tbody></table>");
        return (sb.ToString(), "table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:4px 8px}body{font-family:sans-serif;font-size:13px}");
    }

    static (string html, string css) SynthFlex(int items)
    {
        var sb = new StringBuilder("<div class='list'>");
        for (int i = 0; i < items; i++)
            sb.Append($"<div class='row'><span class='l'>标签 {i}</span><span class='r'>¥{i * 3}</span></div>");
        sb.Append("</div>");
        return (sb.ToString(), ".list{display:flex;flex-direction:column}.row{display:flex;justify-content:space-between;padding:3px 6px;border-bottom:1px solid #eee}.l{flex:1}.r{color:#333}body{font-family:sans-serif;font-size:13px}");
    }
}
