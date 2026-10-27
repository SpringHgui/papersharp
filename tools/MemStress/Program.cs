using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using PaperSharp;
using PaperSharp.Layout;

namespace MemStress;

/// <summary>
/// 批量生成 PDF 时的内存行为测量：托管堆 / 私有字节（含 Skia 原生内存）/ 静态缓存条目数分开设账，
/// 跑完再强制回收一次，用来区分「真滞留」和「等 GC」。
/// </summary>
public static class Program
{
    const long Mb = 1024 * 1024;

    sealed record Options
    {
        public string Mode = "serial";          // serial | par
        public int Docs = 300;
        public int Degree = 4;
        public string Content = "vary";         // vary | same
        public string Html = "";
        public int SampleEvery = 0;             // 0 → 自动（约 24 个采样点）
        public int IntervalMs = 200;
        public string Csv = "";
        public string WriteDir = "";            // 空 = 产物丢弃在内存
        public int Pool = 0;                    // >0：每份文字从 N 个不同汉字里取，用来量「不同码位数 → 内存」
        public bool Probe;                      // 单独量「一张加载后的字面值多少字节 + 反复载放的残迹」
        public string Evict = "";               // "" | every（每份后清空子集字面缓存）| end/probe（跑完当场清空，量它占多少字节）| N（第 N 份时清空）
        public bool SubsetBytes;                // 只量子集流的体积与分表占比
        public bool SubsetHold;                 // 48 张真子面只加载不画，量「每张活字面」的 priv 成本
        public string Bundled = "";             // 打包子集字体目录（空 = 不启用 → 中文走系统整份字面）
    }

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        var o = Parse(args);
        if (o.Html == "") o.Html = FindSpec();
        if (o.Html == "" || !File.Exists(o.Html))
        {
            Console.Error.WriteLine("找不到 HTML 模板（--html 指定，或仓库内需有 benchmarks/PdfBench/spec/challenge.html）");
            return 2;
        }
        var proc0 = Process.GetCurrentProcess();
        if (o.Probe) { ProbeFaceCost(proc0); return 0; }
        if (o.SubsetBytes) { ProbeSubsetBytes(); return 0; }
        if (o.SubsetHold) { ProbeSubsetHold(Process.GetCurrentProcess()); return 0; }
        var tpl = File.ReadAllText(o.Html);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(o.Html))!;
        // 默认不启用打包子集：中文会落到系统整份雅黑（每张 16~19MB 常驻），那是「宿主没配字体」
        // 的最坏口径。--bundled 指到样例工程的 assets/fonts 才能量到产品实际形态。
        if (o.Bundled != "") FontManager.BundledFontDirectory = Path.GetFullPath(o.Bundled);
        if (o.SampleEvery == 0) o.SampleEvery = Math.Max(1, o.Docs / 24);
        if (o.Csv == "")
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "out");
            Directory.CreateDirectory(dir);
            o.Csv = Path.Combine(dir, $"mem_{o.Mode}_{o.Docs}x{o.Degree}_{o.Content}{(o.Pool > 0 ? "_p" + o.Pool : "")}{(o.Evict == "" ? "" : "_ev" + o.Evict)}.csv");
        }
        if (o.WriteDir != "") Directory.CreateDirectory(o.WriteDir);

        Console.WriteLine($"# PaperSharp 批量内存压测");
        Console.WriteLine($"# 模板={Path.GetFileName(o.Html)} 文档数={o.Docs} 并发={o.Degree} 内容={o.Content} 驱逐={(o.Evict == "" ? "否" : o.Evict)} GC={(GCSettings.IsServerGC ? "Server" : "Workstation")} CPU={Environment.ProcessorCount} runtime={Environment.Version}");
        if (o.Evict != "" && o.Mode == "par") { Console.Error.WriteLine("--evict 只支持 serial（并发中驱逐字面会踩到正在渲染的线程）"); return 2; }
        Console.WriteLine($"# 说明: docs=已完成 ws=工作集MB priv=私有字节MB heap=托管堆MB alloc=累计分配MB g0/g1/g2=GC次数 thr=线程数 ms/doc=平均耗时");

        for (int i = 0; i < 5; i++) Gen(tpl, baseDir, o, 10000 + i);   // warmup：JIT + 字体加载不计入斜率
        var proc = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew();
        long alloc0 = GC.GetTotalAllocatedBytes(true);
        long basePriv = proc.PrivateMemorySize64, baseWs = proc.WorkingSet64;
        long gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        var t0 = Take(proc, clock, 0);
        var rows = new List<Snap> { t0 };
        Console.WriteLine("#   docs      ws    priv    heap  allocMB  fb/face  sub  gc0/g1/g2  thr  ms/doc");
        Console.WriteLine(Format(t0, 0, alloc0));

        long outBytes = o.Mode == "par"
            ? RunParallel(o, tpl, baseDir, proc, clock, rows, alloc0)
            : RunSerial(o, tpl, baseDir, proc, clock, rows, alloc0);

        clock.Stop();
        var tEnd = Take(proc, clock, o.Docs);
        rows.Add(tEnd);
        Console.WriteLine(Format(tEnd, o.Docs, alloc0));

        // 强制回收后再看一次：priv 不降 = 原生侧滞留；heap 不降 = 托管侧被根引用
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Thread.Sleep(300);
        GC.Collect();
        var tGc = Take(proc, clock, o.Docs);
        long heapRetained = GC.GetTotalMemory(true);
        Console.WriteLine($"#{new string('-', 78)}");
        Console.WriteLine($"# 跑完（{o.Docs} 份, 墙钟 {tEnd.Ms / 1000:F1}s, 吞吐 {o.Docs / Math.Max(0.001, tEnd.Ms / 1000):F1} docs/s, 产物合计 {outBytes / Mb:F1} MB）");
        Console.WriteLine(Format(tGc, o.Docs, alloc0) + "   <- 强制 Full GC + 等终结器后");
        Report(o, rows, alloc0, basePriv, baseWs, gc0, gc1, gc2, tGc, heapRetained);

        if (Environment.GetEnvironmentVariable("PS_FACEPROBE") == "1") FaceProbe(proc);
        DumpCaches(o.Docs);
        InspectSubsets();
        if (o.Evict == "end") EvictAndReport(proc, "跑完当场");
        WriteCsv(o.Csv, rows);
        Console.WriteLine($"# 采样明细: {o.Csv}");
        return 0;
    }

    static long RunSerial(Options o, string tpl, string baseDir, Process proc, Stopwatch clock, List<Snap> rows, long alloc0)
    {
        long total = 0;
        int evictAt = int.TryParse(o.Evict, out var n) ? n : -1;
        int evictions = 0;
        int keep = o.Evict.StartsWith("keep:") ? int.Parse(o.Evict[5..]) : -1;   // 模拟「CacheCap = keep」
        for (int i = 0; i < o.Docs; i++)
        {
            total += Gen(tpl, baseDir, o, i);
            if (o.Evict == "every") evictions += EvictSubsetCache();
            else if (o.Evict == "admit2") evictions += AdmitOnSecondHit();
            else if (keep >= 0) evictions += TrimSubsetCache(keep);
            if (evictAt > 0 && i + 1 == evictAt)
            {
                EvictAndReport(proc, $"第 {i + 1} 份");
                evictAt = -1;
            }
            if ((i + 1) % o.SampleEvery == 0)
            {
                var s = Take(proc, clock, i + 1);
                rows.Add(s);
                Console.WriteLine(Format(s, i + 1, alloc0) + (o.Evict == "every" ? "   (每份后清空字面缓存)" : ""));
            }
        }
        if (evictions > 0) Console.WriteLine($"# 共驱逐常驻子集字面 {evictions} 张（每份平均 {(double)evictions / Math.Max(1, o.Docs):F1}；口径={o.Evict}）");
        return total;
    }

    /// <summary>把缓存裁到 keep 张（先进先出，等价于把 <c>CacheCap</c> 改小）；返回本次放掉的张数。</summary>
    static int TrimSubsetCache(int keep)
    {
        var list = (System.Collections.IList)SubsetCacheField.GetValue(null)!;
        int over = list.Count - keep, freed = 0;
        for (int i = 0; i < over; i++)
        {
            if (list[0]!.GetType().GetProperty("Subset")?.GetValue(list[0]) is SkiaSharp.SKTypeface tf) { tf.Dispose(); freed++; }
            list.RemoveAt(0);
        }
        return freed;
    }

    /// <summary>模拟「第二次命中才准入」的缓存策略（TinyLFU 那一路）：某张字面只在本轮**第一次**出现就先请出去，
    /// 下份文档再要它就重新裁一次并计数 +1，从此常驻。异质批次里每张独有字面只值一次，正好全部被挡住；
    /// 重复导出的字面第二轮起就稳定命中 → 缓存收益一点不丢。</summary>
    static int AdmitOnSecondHit()
    {
        var list = (System.Collections.IList)SubsetCacheField.GetValue(null)!;
        if (SigHits.Count > 200_000) SigHits.Clear();
        var sigs = new List<string>();
        foreach (var item in list.Cast<object>())
        {
            var t = item!.GetType();
            int[]? kept = t.GetProperty("Kept")?.GetValue(item) as int[] ?? Array.Empty<int>();
            var face = t.GetProperty("Face")?.GetValue(item);
            string fam = face?.GetType().GetProperty("Family")?.GetValue(face) as string ?? "?";
            ulong h = 14695981039346656037;
            foreach (int g in kept) h = (h ^ (uint)g) * 1099511628211;
            sigs.Add($"{fam}|{kept.Length}|{h:X16}");
        }
        foreach (var sig in sigs)
            SigHits[sig] = SigHits.TryGetValue(sig, out var c) ? c + 1 : 1;
        int dropped = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (SigHits[sigs[i]] > 1) continue;
            if (list[i]!.GetType().GetProperty("Subset")?.GetValue(list[i]) is SkiaSharp.SKTypeface tf) { tf.Dispose(); dropped++; }
            list.RemoveAt(i);
            sigs.RemoveAt(i);
        }
        return dropped;
    }

    static readonly Dictionary<string, int> SigHits = new();

    /// <summary>先压平托管堆，驱逐全部常驻子集字面，再压平 —— priv 的差值只可能来自这些原生字面。</summary>
    static void EvictAndReport(Process proc, string tag)
    {
        double p0 = Squeeze(proc);
        var (mb, med, top) = CacheStreamBytes();
        Console.WriteLine($"# >>> {tag}：缓存里的子集**字体流**合计 {mb:F1} MB（每张中位 {med:F2} MB）  大头：{top}");
        int n = EvictSubsetCache();
        double p1 = Squeeze(proc);
        Console.WriteLine($"# >>> {tag}：驱逐 {n} 张常驻子集字面 → priv {p0:F1} → {p1:F1} MB（降 {p0 - p1:F1}，每张 ≈ {(p0 - p1) / Math.Max(1, n):F2} MB）");
    }

    /// <summary>逐张读出缓存里那些 face 的**表字节合计**（= Skia 持有的字体流大小），
    /// 用来判定 priv 降幅是「流本身」还是「流 × 若干倍的加载开销」。</summary>
    static (double Total, double Median, string Top) CacheStreamBytes()
    {
        var list = (System.Collections.IEnumerable)SubsetCacheField.GetValue(null)!;
        var per = new List<double>();
        var byTag = new Dictionary<string, long>();
        foreach (var item in list.Cast<object?>().Where(x => x != null))
        {
            if (item!.GetType().GetProperty("Subset")?.GetValue(item) is not SkiaSharp.SKTypeface tf) continue;
            long sum = 0;
            try
            {
                foreach (var tag in tf.GetTableTags())
                {
                    int len = tf.GetTableData(tag)?.Length ?? 0;
                    sum += len;
                    string name = $"{(char)(tag >> 24 & 0xFF)}{(char)(tag >> 16 & 0xFF)}{(char)(tag >> 8 & 0xFF)}{(char)(tag & 0xFF)}";
                    byTag[name] = byTag.TryGetValue(name, out var had) ? had + len : len;
                }
            }
            catch { }
            per.Add(sum / 1048576.0);
        }
        string top = string.Join(" / ", byTag.OrderByDescending(x => x.Value).Take(5)
            .Select(x => $"{x.Key} {x.Value / 1048576.0:F1}MB"));
        return (per.Sum(), per.Count > 0 ? Median(per) : 0, top);
    }

    /// <summary>Full GC + 等终结器 + 稍候，返回稳定后的 priv（MB）。前后各来一次，差值才归得清。</summary>
    static double Squeeze(Process p)
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Thread.Sleep(250);
        GC.Collect();
        p.Refresh();
        return p.PrivateMemorySize64 / (double)Mb;
    }

    /// <summary>清空 <c>PdfGlyphUsage.Cache</c> 并 Dispose 里面每张常驻子集字面（仅在两份文档之间调用：
    /// 上一趟的 <c>PdfGlyphUsage</c> 已 Dispose，字面除缓存外没有别的根）。返回清掉的张数。</summary>
    static int EvictSubsetCache()
    {
        var list = (System.Collections.IList)SubsetCacheField.GetValue(null)!;
        int n = 0;
        foreach (var item in list.Cast<object>().ToList())
        {
            if (item!.GetType().GetProperty("Subset")?.GetValue(item) is SkiaSharp.SKTypeface tf) { tf.Dispose(); n++; }
        }
        list.Clear();
        return n;
    }

    static long RunParallel(Options o, string tpl, string baseDir, Process proc, Stopwatch clock, List<Snap> rows, long alloc0)
    {
        int next = -1;
        long done = 0, bytes = 0;
        var lats = new System.Collections.Concurrent.ConcurrentQueue<double>();
        var stop = false;
        var mon = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                Thread.Sleep(o.IntervalMs);
                var s = Take(proc, clock, Interlocked.Read(ref done));
                lock (rows) rows.Add(s);
                Console.WriteLine(Format(s, s.Docs, alloc0));
            }
        }) { IsBackground = true };
        mon.Start();

        var workers = Enumerable.Range(0, o.Degree).Select(_ => new Thread(() =>
        {
            while (true)
            {
                int i = Interlocked.Increment(ref next);
                if (i >= o.Docs) return;
                var sw = Stopwatch.StartNew();
                int n = Gen(tpl, baseDir, o, i);
                sw.Stop();
                lats.Enqueue(sw.Elapsed.TotalMilliseconds);
                Interlocked.Add(ref bytes, n);
                Interlocked.Increment(ref done);
            }
        })).ToArray();
        foreach (var t in workers) { t.IsBackground = false; t.Start(); }
        foreach (var t in workers) t.Join();
        Volatile.Write(ref stop, true);
        mon.Join(2000);

        var l = lats.ToList();
        if (l.Count > 0)
        {
            l.Sort();
            Console.WriteLine($"# 并发单份耗时: p50 {l[(int)(l.Count * 0.5)]:F0}ms  p95 {l[Math.Min(l.Count - 1, (int)(l.Count * 0.95))]:F0}ms  max {l[^1]:F0}ms（串行口径见 serial 模式）");
        }
        return bytes;
    }

    static int Gen(string tpl, string baseDir, Options o, int docId)
    {
        string html = o.Content == "vary" ? Vary(tpl, docId, o.Pool) : tpl;
        byte[] pdf = Renderer.RenderPdf(html, "", PageSize.A4, baseDir: baseDir);
        if (o.WriteDir != "") File.WriteAllBytes(Path.Combine(o.WriteDir, $"doc{docId}.pdf"), pdf);
        return pdf.Length;
    }

    // ── 每份文档不同：页脚注入一段按 docId 确定的文字，逼出各自的字形子集 ──
    static readonly string[] Words =
    {
        "invoice", "ledger", "quarterly", "shipment", "thermal", "gradient", "kerning", "viewport",
        "摘要", "季度", "发货", "冷链", "仓储", "税率", "合计", "盖章", "校验", "批次", "编号",
        "报表", "签收", "退款", "库存", "流水", "质检", "备注", "承运", "磅单", "验收",
        "Zhejiang", "Rotterdam", "Montreal", "Kaohsiung", "Σ", "Ω", "µ", "№", "±", "≈",
    };

    static string Vary(string tpl, int docId, int pool)
    {
        var rnd = new Random(docId * 7919 + 17);
        var sb = new System.Text.StringBuilder("v2.");
        if (pool > 0)
        {
            // 常用汉字区取 pool 个不同码位，按 docId 轮转铺开：整批 touch 到的码位数 = min(pool, 8×份数)
            for (int i = 0; i < 8; i++) sb.Append((char)(0x4E00 + rnd.Next(pool) * Math.Max(1, 20000 / pool))).Append(' ');
        }
        else for (int i = 0; i < 6; i++) sb.Append(Words[rnd.Next(Words.Length)]).Append('-');
        sb.Append(docId);
        return tpl.Replace("generated by spec v0.1", "generated by spec " + sb);
    }

    // ── 采样 ────────────────────────────────────────────────

    sealed record Snap(int Docs, double Ms, double Ws, double Priv, double Heap, double Alloc,
                       int G0, int G1, int G2, int Threads, int Faces, int ListFaces, int Subsets);

    // TextShaper 两张按码位键控的字面缓存：批量场景里条目数 = 整批 touch 过的不同码位数
    static readonly FieldInfo FallbackCacheField = FaceType().GetField("FallbackCache", BindingFlags.Static | BindingFlags.NonPublic)!;
    static readonly FieldInfo ListFaceCacheField = FaceType().GetField("ListFaceCache", BindingFlags.Static | BindingFlags.NonPublic)!;
    // 字形子集缓存（上限 48 张）：每张是一个常驻的原生字面
    static readonly FieldInfo SubsetCacheField = typeof(Renderer).Assembly.GetType("PaperSharp.Pdf.PdfGlyphUsage")!
        .GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic)!;
    static Type FaceType() => typeof(Renderer).Assembly.GetType("PaperSharp.Layout.TextShaper")!;
    static int CountOfField(FieldInfo f) => ((System.Collections.IEnumerable)f.GetValue(null)!).Cast<object?>().Count();

    static Snap Take(Process p, Stopwatch clock, long docs)
    {
        p.Refresh();
        return new Snap((int)docs, clock.Elapsed.TotalMilliseconds,
            p.WorkingSet64 / (double)Mb, p.PrivateMemorySize64 / (double)Mb,
            GC.GetTotalMemory(false) / (double)Mb, GC.GetTotalAllocatedBytes(true) / (double)Mb,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), p.Threads.Count,
            CountOfField(FallbackCacheField), CountOfField(ListFaceCacheField), CountOfField(SubsetCacheField));
    }

    static string Format(Snap s, long docs, long alloc0, bool header = false)
    {
        string alloc = $"{s.Alloc - alloc0 / (double)Mb,7:F0}";
        double per = docs > 0 ? s.Ms / docs : 0;
        return $"{(header ? "# " : "  ")}{docs,6} {s.Ws,7:F1} {s.Priv,7:F1} {s.Heap,7:F1} {alloc,7} {s.Faces,5}/{s.ListFaces,5} {s.Subsets,3} {s.G0,4}/{s.G1,3}/{s.G2,3} {s.Threads,3} {per,7:F1}";
    }

    static void Report(Options o, List<Snap> rows, long alloc0, long basePriv, long baseWs,
                       long gc0, long gc1, long gc2, Snap tGc, long heapRetained)
    {
        // 用后半段采样点做最小二乘，跳过启动期 JIT/字体加载的爬升
        var tail = rows.Where(r => r.Docs >= o.Docs / 2).ToList();
        double privSlopePer1k = tail.Count >= 3 ? Slope(tail.Select(r => (double)r.Docs).ToList(), tail.Select(r => r.Priv).ToList()) * 1000 : double.NaN;
        double privPerFace = tail.Count >= 3 ? Slope(tail.Select(r => (double)r.Faces).ToList(), tail.Select(r => r.Priv).ToList()) : double.NaN;
        double heapSlopePer1k = tail.Count >= 3 ? Slope(tail.Select(r => (double)r.Docs).ToList(), tail.Select(r => r.Heap).ToList()) * 1000 : double.NaN;
        double privPeak = rows.Max(r => r.Priv), wsPeak = rows.Max(r => r.Ws), heapPeak = rows.Max(r => r.Heap);
        double allocPerDocKb = (tGc.Alloc * Mb - alloc0) / Math.Max(1, o.Docs) / 1024.0;

        Console.WriteLine($"# 峰值: priv {privPeak:F1} MB   ws {wsPeak:F1} MB   heap {heapPeak:F1} MB   （起点 priv {basePriv / (double)Mb:F1} / ws {baseWs / (double)Mb:F1} MB）");
        Console.WriteLine($"# 增长斜率（后半段线性拟合）: priv {privSlopePer1k:+0.0;-0.0;+0.0} MB/1k 份   heap {heapSlopePer1k:+0.0;-0.0;+0.0} MB/1k 份   priv/回退码位条目 {privPerFace * 1024:+0;-0;+0} KB");
        Console.WriteLine($"# 托管累计分配 {allocPerDocKb:F0} KB/份   GC gen0 {GC.CollectionCount(0) - gc0} / gen1 {GC.CollectionCount(1) - gc1} / gen2 {GC.CollectionCount(2) - gc2}");
        double drop = privPeak - tGc.Priv, nativeRetained = Math.Max(0, tGc.Priv - heapRetained / (double)Mb);
        Console.WriteLine($"# Full GC 后: priv {tGc.Priv:F1} MB（比峰值降 {drop:F1}） 托管堆滞留 {heapRetained / (double)Mb:F1} MB → 原生侧 ≈ {nativeRetained:F1} MB");
    }

    static double Slope(List<double> x, List<double> y)
    {
        int n = x.Count;
        double mx = x.Average(), my = y.Average();
        double sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); }
        return sxx == 0 ? 0 : sxy / sxx;
    }

    // ── 静态缓存清点：批量场景里条目数随文档数线性涨的，就是根 ──
    /// <summary>临时：清各类字面缓存前后各量一次 priv，并按 face 打印原生字面清单。</summary>
    static void FaceProbe(Process p)
    {
        var asm = typeof(Renderer).Assembly;
        var faces = new Dictionary<long, (SkiaSharp.SKTypeface tf, string src)>();
        void Harvest(string name, object? dict)
        {
            if (dict is System.Collections.IEnumerable en)
                foreach (var kv in en.Cast<object?>())
                {
                    var v = kv?.GetType().GetProperty("Value")?.GetValue(kv) ?? kv;
                    if (v is SkiaSharp.SKTypeface f) faces[f.Handle.ToInt64()] = (f, name);
                }
        }
        Harvest("FontManager.Cache", typeof(PaperSharp.Layout.FontManager).GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        Harvest("FallbackCache", FaceType().GetField("FallbackCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        Harvest("CpFaceCache", FaceType().GetField("CpFaceCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        Harvest("ListFaceCache", FaceType().GetField("ListFaceCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        foreach (var fld in typeof(PaperSharp.Layout.FontManager).GetFields(BindingFlags.Static | BindingFlags.NonPublic))
            if (fld.GetValue(null) is SkiaSharp.SKTypeface sf) faces[sf.Handle.ToInt64()] = (sf, fld.Name);
        double before = Squeeze(p);
        Console.WriteLine($"# 字面探针：活字面 {faces.Count} 张，压平后 priv {before:F1} MB");
        foreach (var (h, (tf, src)) in faces.OrderByDescending(x => TblBytes(x.Value.tf)))
            Console.WriteLine($"#   {TblBytes(tf) / 1048576.0,7:F2} MB  {tf.FamilyName,-22} w{tf.FontWeight,-4} wd{tf.FontWidth} s{(int)tf.FontSlant} g{tf.GlyphCount,-6} h{tf.Handle.ToInt64():X} [{src}]");
        long sum = faces.Sum(x => (long)TblBytes(x.Value.tf));
        Console.WriteLine($"#   表字节合计 {sum / 1048576.0:F1} MB");
        // 清空全部字面缓存再压平
        foreach (var name in new[] { "FontManager.Cache" })
            if (asm.GetType("PaperSharp.Layout.FontManager")!.GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is System.Collections.IDictionary d) d.Clear();
        foreach (var nm in new[] { "FallbackCache", "CpFaceCache", "ListFaceCache" })
            if (FaceType().GetField(nm, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is System.Collections.IDictionary cd) cd.Clear();
        int ev = EvictSubsetCache();
        double after = Squeeze(p);
        Console.WriteLine($"#   清掉 {faces.Count} 张(其中子集 {ev})后 priv {after:F1} MB（降 {before - after:F1}）");
    }

    static int TblBytes(SkiaSharp.SKTypeface tf)
    {
        int s = 0;
        try { foreach (var t in tf.GetTableTags()) s += tf.GetTableSize(t); } catch { }
        return s;
    }

    static void DumpCaches(int docs)
    {
        var asm = typeof(Renderer).Assembly;
        var suspects = new List<(string Name, int Count)>();
        foreach (var t in asm.GetTypes())
        {
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType == typeof(string) || f.IsLiteral) continue;
                object? v;
                try { v = f.GetValue(null); } catch { continue; }
                if (v is null || v is string) continue;
                int? count = CountOf(v);
                if (count is >= 4)
                {
                    string extra = "";
                    if (v is System.Collections.IEnumerable en && en.Cast<object?>().Any(x => x is SkiaSharp.SKTypeface))
                    {
                        var hs = new HashSet<long>();
                        foreach (var o in en.Cast<object?>())
                            if (o is SkiaSharp.SKTypeface tf) hs.Add(tf.Handle.ToInt64());
                        extra = $"  → 不同原生字面 {hs.Count} 个";
                    }
                    suspects.Add(($"{t.Namespace}.{t.Name}.{f.Name}{extra}", count.Value));
                }
            }
        }
        Console.WriteLine($"# 静态缓存条目数（{docs} 份之后；只列 ≥4 条的）:");
        foreach (var (name, c) in suspects.OrderByDescending(x => x.Count))
            Console.WriteLine($"#   {c,8}  {name}");
    }

    /// <summary>子集缓存里到底存了什么：每张 face 的字形数、族名、以及它们各自是不是独立的原生字面。
    /// 字形数很小而 priv 很大 → 常驻的是「加载后的整张字体」，不是那点轮廓数据。</summary>
    static void InspectSubsets()
    {
        var list = (System.Collections.IEnumerable)SubsetCacheField.GetValue(null)!;
        var rows = new List<(string Family, int Glyphs, long Handle)>();
        foreach (var item in list.Cast<object?>().Where(x => x != null))
        {
            var t = item!.GetType();
            var key = t.GetProperty("Face")?.GetValue(item);
            int[]? kept = t.GetProperty("Kept")?.GetValue(item) as int[];
            var face = t.GetProperty("Subset")?.GetValue(item) as SkiaSharp.SKTypeface;
            string fam = key?.GetType().GetProperty("Family")?.GetValue(key) as string ?? "?";
            rows.Add((fam, kept?.Length ?? 0, face?.Handle.ToInt64() ?? 0));
        }
        if (rows.Count == 0) { Console.WriteLine("# 子集缓存：空（本轮被驱逐过）"); return; }
        Console.WriteLine($"# 子集缓存 {rows.Count} 张：保留字形合计 {rows.Sum(r => r.Glyphs):N0}（中位 {Median(rows.Select(r => (double)r.Glyphs).ToList()):F0}）不同原生字面 {rows.Select(r => r.Handle).Distinct().Count()} 个");
        foreach (var g in rows.GroupBy(r => r.Family).OrderByDescending(g => g.Sum(x => x.Glyphs)).Take(6))
            Console.WriteLine($"#   {g.Count(),3} 张 × 中位 {Median(g.Select(x => (double)x.Glyphs).ToList()):F0} 字形   {g.Key}");
    }

    static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    /// <summary>一张「加载后的字面」到底值多少字节：拿 2.33 MB 的打包字体反复加载，
    /// 分开量「同时活 N 张」的边际成本与「反复载放 N 次」留下的 allocator 高水位残迹。
    /// 前者决定缓存该按什么计价，后者决定「用完就 Dispose」到底省不省。</summary>
    static void ProbeFaceCost(Process proc)
    {
        string font = Path.Combine(FindFontDir(), "NotoSansSCSub-Regular.ttf");
        if (!File.Exists(font)) { Console.WriteLine($"# 探针：找不到 {font}"); return; }
        byte[] bytes = File.ReadAllBytes(font);
        double baseP = Squeeze(proc);
        var live = new List<SkiaSharp.SKTypeface>();
        for (int i = 1; i <= 12; i++)
        {
            live.Add(SkiaSharp.SKTypeface.FromData(SkiaSharp.SKData.CreateCopy(bytes)));
            if (i % 4 != 0) continue;
            proc.Refresh();
            Console.WriteLine($"# 探针 · 同时 {i,2} 张 {bytes.Length / Mb:F2} MB 字面：priv {proc.PrivateMemorySize64 / (double)Mb:F1} MB（每张边际 ≈ {(proc.PrivateMemorySize64 / (double)Mb - baseP) / i:F2} MB）");
        }
        foreach (var f in live) f.Dispose();
        live.Clear();
        double afterFree = Squeeze(proc);
        Console.WriteLine($"# 探针 · 12 张全放掉并压平：priv {afterFree:F1} MB（起点 {baseP:F1}，回落 {baseP - 0:F0} → 残留 {afterFree - baseP:+0.0;-0.0}）");
        for (int i = 0; i < 12; i++)
            using (var f = SkiaSharp.SKTypeface.FromData(SkiaSharp.SKData.CreateCopy(bytes)))
                GC.KeepAlive(f);
        proc.Refresh();
        double afterChurn = Squeeze(proc);
        Console.WriteLine($"# 探针 · 同样 12 次改成「载一份放一份」交替：priv {afterChurn:F1} MB（比一次性持有 {afterChurn - afterFree:+0.0;-0.0}，即 churn 的代价）");
    }

    /// <summary>一张**裁出来的子集流**到底多少字节，以及被哪些表吃掉：
    /// 若裁出的流仍是 MB 级，那 3.59 MB/张就是「加载后的字体流本身」，缓存该按字节计价；
    /// 若只有几十 KB，那常驻的是 Skia 内部的字形/度量缓存，裁缓存省不下多少。</summary>
    static void ProbeSubsetBytes()
    {
        foreach (var tf in Candidates())
        {
            var orig = TableSizesOf(tf);
            Console.WriteLine($"# 子集探针 · 原始字面 {tf.FamilyName}：表合计 {orig.Sum(x => (long)x.Len) / 1048576.0:F2} MB / {tf.GlyphCount} 字形（{orig.Count} 张表）");
            if (orig.Count > 0)
                Console.WriteLine($"#   原表大头：{string.Join(" / ", orig.OrderByDescending(x => x.Len).Take(6).Select(x => $"{x.Name} {x.Len / 1048576.0:F2}MB"))}");
            foreach (int want in new[] { 20, 50, 200, 800, 2000 })
            {
                var set = new HashSet<int>();
                for (int cp = 0x4E00; cp < 0x9FFF && set.Count < want; cp++)
                {
                    int g = tf.GetGlyph(cp);
                    if (g != 0) set.Add(g);
                }
                if (set.Count == 0) { Console.WriteLine($"#   要 {want} 字形：cmap 查不到码位"); continue; }
                var bytes = PaperSharp.Pdf.TtfSubsetter.Subset(tf, set);
                if (bytes == null) { Console.WriteLine($"#   {set.Count} 字形：未裁（CFF/异常）"); continue; }
                var top = TableSizes(bytes).OrderByDescending(x => x.Len).Take(5)
                    .Select(x => $"{x.Name} {x.Len / 1024.0:F0}KB").Aggregate((a, b) => a + " / " + b);
                Console.WriteLine($"#   {set.Count,4} 字形 → 子集流 {bytes.Length / 1024.0,8:F0} KB（{bytes.Length / (double)set.Count,6:F0} B/字形）  大头：{top}");
            }
        }
    }

    /// <summary>把 48 张**真裁出来的子集**一次加载进内存但不画任何东西：
    /// 每张的边际 priv ≈ 流本身 → 常驻就是字体流；≫ 流 → 常驻大头是 Skia 的按字面 glyph 缓存，
    /// 那就只能靠「少留活字面」降内存，缩子集流没用。</summary>
    static void ProbeSubsetHold(Process proc)
    {
        var tf = Candidates().LastOrDefault(x => x.GlyphCount > 20000)
              ?? SkiaSharp.SKTypeface.FromFamilyName("Microsoft YaHei");
        if (tf == null) { Console.WriteLine("# 探针：没有可用的大字体"); return; }
        var streams = new List<int>();
        var live = new List<SkiaSharp.SKTypeface>();
        double baseP = Squeeze(proc);
        for (int i = 0; i < 48; i++)
        {
            var set = new HashSet<int>();
            for (int cp = 0x4E00 + i * 200; cp < 0x9FFF && set.Count < 250; cp++)
            {
                int g = tf.GetGlyph(cp);
                if (g != 0) set.Add(g);
            }
            var bytes = PaperSharp.Pdf.TtfSubsetter.Subset(tf, set);
            if (bytes == null) return;
            streams.Add(bytes.Length);
            live.Add(SkiaSharp.SKTypeface.FromData(SkiaSharp.SKData.CreateCopy(bytes)));
            if ((i + 1) % 12 != 0) continue;
            proc.Refresh();
            double held = streams.Sum() / 1048576.0;
            Console.WriteLine($"# 持有 {i + 1,3} 张子面：priv {proc.PrivateMemorySize64 / (double)Mb:F1} MB（起点 {baseP:F1}）"
                + $"  每张边际 {(proc.PrivateMemorySize64 / (double)Mb - baseP) / (i + 1):F2} MB vs 流均 {held / (i + 1):F2} MB");
        }
        foreach (var f in live) f.Dispose();
        Console.WriteLine($"# 48 张全放掉并压平：priv {Squeeze(proc):F1} MB（起点 {baseP:F1}）；48 条流合计 {streams.Sum() / 1048576.0:F1} MB");
    }

    static List<SkiaSharp.SKTypeface> Candidates()
    {
        var list = new List<SkiaSharp.SKTypeface>();
        string noto = Path.Combine(FindFontDir(), "NotoSansSCSub-Regular.ttf");
        if (File.Exists(noto))
        {
            var f = SkiaSharp.SKTypeface.FromData(SkiaSharp.SKData.CreateCopy(File.ReadAllBytes(noto)));
            if (f != null) list.Add(f);
        }
        var yh = SkiaSharp.SKTypeface.FromFamilyName("Microsoft YaHei")
              ?? SkiaSharp.SKTypeface.FromFamilyName("SimSun");
        if (yh != null) list.Add(yh);
        return list;
    }

    static List<(string Name, int Len)> TableSizesOf(SkiaSharp.SKTypeface tf)
    {
        var r = new List<(string, int)>();
        try
        {
            foreach (var t in tf.GetTableTags())
                r.Add((TagName(t), tf.GetTableData(t)?.Length ?? 0));
        }
        catch { }
        return r;
    }

    static string TagName(uint t) => new string(new[]
    {
        (char)((t >> 24) & 0xFF), (char)((t >> 16) & 0xFF), (char)((t >> 8) & 0xFF), (char)(t & 0xFF),
    });

    static List<(string Name, int Len)> TableSizes(byte[] sfnt)
    {
        var r = new List<(string, int)>();
        ushort n = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4));
        for (int i = 0; i < n && 12 + i * 16 + 8 < sfnt.Length; i++)
        {
            int p = 12 + i * 16;
            string tag = System.Text.Encoding.ASCII.GetString(sfnt, p, 4).Trim();
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(sfnt.AsSpan(p + 12));
            r.Add((tag, len));
        }
        return r;
    }

    static string FindFontDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string cand = Path.Combine(dir.FullName, "src", "PaperSharp", "Fonts");
            if (Directory.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "src", "PaperSharp", "Fonts");
    }

    static int? CountOf(object v) => v switch
    {
        System.Collections.IDictionary d => d.Count,
        System.Collections.ICollection c => c.Count,
        System.Collections.IEnumerable e => e.Cast<object?>().Count(),
        _ => null,
    };

    static void WriteCsv(string path, List<Snap> rows)
    {
        var sb = new System.Text.StringBuilder("docs,elapsed_ms,ws_mb,priv_mb,heap_mb,alloc_cum_mb,gen0,gen1,gen2,threads\n");
        foreach (var r in rows)
            sb.Append($"{r.Docs},{r.Ms:F0},{r.Ws:F2},{r.Priv:F2},{r.Heap:F2},{r.Alloc:F2},{r.G0},{r.G1},{r.G2},{r.Threads}\n");
        File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
    }

    static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i], v = i + 1 < args.Length ? args[i + 1] : "";
            switch (a)
            {
                case "serial" or "par": o.Mode = a; break;
                case "--docs": o.Docs = int.Parse(v); i++; break;
                case "--degree": o.Degree = int.Parse(v); i++; break;
                case "--content": o.Content = v; i++; break;
                case "--html": o.Html = v; i++; break;
                case "--every": o.SampleEvery = int.Parse(v); i++; break;
                case "--interval": o.IntervalMs = int.Parse(v); i++; break;
                case "--csv": o.Csv = v; i++; break;
                case "--write": o.WriteDir = v; i++; break;
                case "--pool": o.Pool = int.Parse(v); i++; break;
                case "--evict": o.Evict = v; i++; break;
                case "--probe": o.Probe = true; break;
                case "--subset": o.SubsetBytes = true; break;
                case "--hold": o.SubsetHold = true; break;
                case "--bundled": o.Bundled = v; i++; break;
                default: Console.Error.WriteLine($"未知参数: {a}"); Console.Error.WriteLine("用法: MemStress [serial|par] --docs N [--degree D] [--content vary|same] [--pool P] [--html path] [--every K] [--csv f] [--write dir] [--evict every|end|keep:N|N] [--bundled dir] [--probe] [--subset]"); return o;
            }
        }
        return o;
    }

    static string FindSpec()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string cand = Path.Combine(dir.FullName, "benchmarks", "PdfBench", "spec", "challenge.html");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        return "";
    }
}
