using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using iText.Kernel.Pdf;

namespace PdfBench.Runner;

/// <summary>
/// 性能测量内核：对每个 adapter 采集 CONTRACT.md 约定的全指标，写 out/results.csv 长表。
/// 单 adapter / 单指标失败一律记 NaN，不中断整体。
/// </summary>
public static class MeasureRunner
{
    const int Warmup = 5;
    const int Iters = 30;
    const int ThroughputSeconds = 3;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // 指标顺序与单位（CONTRACT.md）
    static readonly (string Key, string Unit)[] MetricOrder =
    {
        ("latency_min", "ms"), ("latency_med", "ms"), ("latency_mean", "ms"),
        ("latency_p95", "ms"), ("latency_max", "ms"),
        ("alloc_med", "KB"),
        ("gen0", "count"), ("gen1", "count"), ("gen2", "count"),
        ("rss_peak", "MB"),
        ("size", "KB"), ("pages", "count"),
        ("throughput_serial", "docs/s"), ("throughput_par4", "docs/s"),
        ("coldstart", "ms"),
        ("threadsafe", "bool"),
    };

    public static int Run(IReadOnlyList<IPdfAdapter> adapters, AdapterContext ctx, string outDir)
    {
        // 每个 adapter 在独立子进程中测量（measure-one），避免前一 adapter 遗留的
        // GC 堆/Skia 原生内存污染后一 adapter 的 rss_peak 与延迟数据。
        var csvPath = Path.Combine(outDir, "results.csv");
        using (var w = new StreamWriter(csvPath, false, new UTF8Encoding(false)))
        {
            w.WriteLine("adapter,metric,value,unit");
            foreach (var a in adapters)
            {
                Console.WriteLine($"=== {a.Name} ===");
                var rowPath = Path.Combine(outDir, $"_rows_{a.Name}.csv");
                try { File.Delete(rowPath); } catch { }
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"measure-one {a.Name} \"{rowPath}\"")
                    {
                        UseShellExecute = false,
                    })!;
                    p.WaitForExit();
                }
                catch (Exception ex) { Log(a.Name, "child", ex); }

                if (File.Exists(rowPath))
                {
                    foreach (var line in File.ReadLines(rowPath)) w.WriteLine(line);
                    File.Delete(rowPath);
                }
                else
                {
                    foreach (var (key, unit) in MetricOrder)
                        w.WriteLine($"{a.Name},{key},NaN,{unit}");
                }
                w.Flush(); // 增量落盘，崩了也保留已完成行
            }
        }
        Console.WriteLine($"results written: {csvPath}");
        return 0;
    }

    /// <summary>子命令入口：测量单个 adapter，把行写入 rowPath 后退出。</summary>
    public static int RunOne(IPdfAdapter a, AdapterContext ctx, string outDir, string rowPath)
    {
        var values = MeasureOne(a, ctx, outDir);
        var lines = new List<string>();
        foreach (var (key, unit) in MetricOrder)
        {
            var v = values.TryGetValue(key, out var x) ? x : double.NaN;
            lines.Add($"{a.Name},{key},{Fmt(v, key)},{unit}");
        }
        File.WriteAllLines(rowPath, lines, new UTF8Encoding(false));
        return 0;
    }

    static string Fmt(double v, string key)
    {
        if (double.IsNaN(v)) return "NaN";
        if (key is "gen0" or "gen1" or "gen2" or "pages" or "threadsafe")
            return ((long)Math.Round(v)).ToString(Inv);
        return v.ToString("0.##", Inv);
    }

    static Dictionary<string, double> MeasureOne(IPdfAdapter a, AdapterContext ctx, string outDir)
    {
        var r = new Dictionary<string, double>();
        string R(string name) => Path.Combine(outDir, $"_perf_{name}.pdf");

        // ---------- 1+2+3+4. latency / alloc / gen / rss：共享一个 warmup+30 迭代循环 ----------
        var latencies = new List<double>();
        var allocs = new List<double>();
        int g0 = 0, g1 = 0, g2 = 0;
        double rssPeakMb = double.NaN;
        string? lastGood = null;

        try
        {
            for (int i = 0; i < Warmup; i++)
            {
                try { a.Generate(ctx, R($"warm{i}")); }
                catch (Exception ex) { Log(a.Name, $"warmup {i}", ex); }
            }
        }
        catch (Exception ex) { Log(a.Name, "warmup", ex); }

        var monitor = new RssMonitor();
        int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1), c2 = GC.CollectionCount(2);
        try
        {
            monitor.Start();
            var sw = new Stopwatch();
            for (int i = 0; i < Iters; i++)
            {
                try
                {
                    long allocBefore = GC.GetTotalAllocatedBytes(true);
                    sw.Restart();
                    a.Generate(ctx, R($"i{i}"));
                    sw.Stop();
                    long allocAfter = GC.GetTotalAllocatedBytes(true);
                    latencies.Add(sw.Elapsed.TotalMilliseconds);
                    allocs.Add((allocAfter - allocBefore) / 1024.0);
                    lastGood = R($"i{i}");
                }
                catch (Exception ex) { Log(a.Name, $"iter {i}", ex); }
            }
        }
        finally
        {
            g0 = GC.CollectionCount(0) - c0;
            g1 = GC.CollectionCount(1) - c1;
            g2 = GC.CollectionCount(2) - c2;
            rssPeakMb = monitor.Stop();
        }

        if (latencies.Count > 0)
        {
            var sorted = latencies.OrderBy(x => x).ToList();
            r["latency_min"] = sorted[0];
            r["latency_med"] = Percentile(sorted, 0.50);
            r["latency_mean"] = latencies.Average();
            r["latency_p95"] = Percentile(sorted, 0.95);
            r["latency_max"] = sorted[^1];
            var asorted = allocs.OrderBy(x => x).ToList();
            r["alloc_med"] = asorted.Count > 0 ? Percentile(asorted, 0.50) : double.NaN;
        }
        r["gen0"] = g0; r["gen1"] = g1; r["gen2"] = g2;
        r["rss_peak"] = rssPeakMb;
        if (latencies.Count > 0)
            Console.WriteLine($"  [{a.Name}] {latencies.Count}/{Iters} iters, med {Percentile(latencies.OrderBy(x => x).ToList(), 0.5):F0}ms, rss {rssPeakMb:F0}MB");

        // ---------- size / pages：取最后一次成功产物 ----------
        try
        {
            string artifact = lastGood is not null && File.Exists(lastGood) ? lastGood : R(a.Name);
            if (lastGood is not null && File.Exists(lastGood))
                File.Copy(artifact, Path.Combine(outDir, a.Name + ".pdf"), true);
            r["size"] = new FileInfo(artifact).Length / 1024.0;
            try
            {
                using var pdf = new PdfDocument(new PdfReader(artifact));
                r["pages"] = pdf.GetNumberOfPages();
            }
            catch (Exception ex) { Log(a.Name, "pages", ex); r["pages"] = double.NaN; }
        }
        catch (Exception ex) { Log(a.Name, "size", ex); }

        // ---------- 5. throughput_serial / throughput_par4 ----------
        try
        {
            int count = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < ThroughputSeconds)
            {
                // 每份文档独立路径：个别库（如 PDFsharp）可能短暂持有上一份文件的句柄，复用同名路径会 IOException
                try { a.Generate(ctx, R($"tp_s_{count}")); count++; }
                catch (Exception ex) { Log(a.Name, "throughput_serial iter", ex); break; }
            }
            sw.Stop();
            if (count > 0) r["throughput_serial"] = count / sw.Elapsed.TotalSeconds;
        }
        catch (Exception ex) { Log(a.Name, "throughput_serial", ex); }

        try
        {
            var wall = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            {
                int count = 0;
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < ThroughputSeconds)
                {
                    try { a.Generate(ctx, R($"tp_p{i}_{count}")); count++; }
                    catch { break; } // 线程不安全也算吞吐测到崩为止的完成数
                }
                sw.Stop();
                return (count, elapsed: sw.Elapsed.TotalSeconds);
            })).ToArray();
            Task.WaitAll(tasks, TimeSpan.FromMinutes(10));
            wall.Stop();
            int total = tasks.Sum(t => t.Result.count);
            if (total > 0) r["throughput_par4"] = total / wall.Elapsed.TotalSeconds;
        }
        catch (Exception ex) { Log(a.Name, "throughput_par4", ex); }

        // ---------- 6. threadsafe ----------
        try
        {
            a.Generate(ctx, R("ts_serial"));
            var serial = Sha256Normalized(R("ts_serial"));
            var par = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            {
                a.Generate(ctx, R($"ts_par{i}"));
                return Sha256Normalized(R($"ts_par{i}"));
            })).ToArray();
            Task.WaitAll(par, TimeSpan.FromMinutes(5));
            r["threadsafe"] = par.All(t => t.Result == serial) ? 1 : 0;
        }
        catch (Exception ex) { Log(a.Name, "threadsafe", ex); r["threadsafe"] = double.NaN; }

        // ---------- 7. coldstart：spawn 自身 single 子命令 5 次取中位 ----------
        try
        {
            var exe = Environment.ProcessPath;
            var times = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    using var p = Process.Start(new ProcessStartInfo(exe!, "single " + a.Name)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    })!;
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(180000);
                    sw.Stop();
                    if (p.ExitCode == 0) times.Add(sw.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex) { Log(a.Name, $"coldstart {i}", ex); }
            }
            if (times.Count > 0)
            {
                times.Sort();
                r["coldstart"] = Percentile(times, 0.50);
            }
        }
        catch (Exception ex) { Log(a.Name, "coldstart", ex); }

        // 清理临时文件
        foreach (var f in Directory.EnumerateFiles(outDir, "_perf_*.pdf"))
            try { File.Delete(f); } catch { }

        return r;
    }

    static double Percentile(List<double> sorted, double p)
    {
        // 最近秩法
        int idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    static void Log(string adapter, string where, Exception ex) =>
        Console.WriteLine($"  [{adapter}/{where}] {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");

    // ---------- 时变字节剥离后 SHA256 ----------
    static readonly Regex[] TimeVariantPatterns =
    {
        new(@"/CreationDate\s*\(D:[^)]*\)", RegexOptions.Compiled),
        new(@"/ModDate\s*\(D:[^)]*\)", RegexOptions.Compiled),
        new(@"/ID\s*\[[^\]]*\]", RegexOptions.Compiled),
        // 字体子集随机前缀（如 /ABCDEF+SegoeUI）：每次运行都重新随机，属"每次变化字节"而非线程不安全
        new(@"/[A-Z]{5,7}\+", RegexOptions.Compiled),
        new(@"<xmp:(CreateDate|ModifyDate|MetadataDate)>.*?</xmp:\1>", RegexOptions.Compiled | RegexOptions.Singleline),
        new(@"<xmpMM:DocumentID>.*?</xmpMM:DocumentID>", RegexOptions.Compiled | RegexOptions.Singleline),
        new(@"<xmpMM:InstanceID>.*?</xmpMM:InstanceID>", RegexOptions.Compiled | RegexOptions.Singleline),
        new(@"<tr:Created[^>]*>.*?</tr:Created>", RegexOptions.Compiled | RegexOptions.Singleline),
        new(@"<tr:Modified[^>]*>.*?</tr:Modified>", RegexOptions.Compiled | RegexOptions.Singleline),
        new(@"<pdf:docinfo:CreationDate>.*?</pdf:docinfo:CreationDate>", RegexOptions.Compiled | RegexOptions.Singleline),
        new(@"<pdf:docinfo:ModDate>.*?</pdf:docinfo:ModDate>", RegexOptions.Compiled | RegexOptions.Singleline),
    };

    static string Sha256Normalized(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var s = Encoding.Latin1.GetString(bytes);
        foreach (var rx in TimeVariantPatterns)
            s = rx.Replace(s, m => new string('X', m.Length)); // 等长替换，保持交叉引用偏移可比
        return Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(s)));
    }

    static void TryMove(string src, string dst)
    {
        try { File.Move(src, dst, true); } catch { }
    }
}
/// <summary>后台线程每 50ms 轮询"本进程 + 全部后代进程"工作集之和的最大值（MB）。</summary>
sealed class RssMonitor
{
    double _peakMb;
    volatile bool _stop;
    Thread? _thread;

    public void Start()
    {
        _peakMb = 0;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "rss-monitor" };
        _thread.Start();
    }

    public double Stop()
    {
        _stop = true;
        _thread?.Join(2000);
        return _peakMb > 0 ? _peakMb : double.NaN;
    }

    void Loop()
    {
        var self = Process.GetCurrentProcess().Id;
        while (!_stop)
        {
            try
            {
                long total = 0;
                foreach (var pid in Native.GetProcessTree(self))
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        total += p.WorkingSet64;
                    }
                    catch { /* 进程已退出 */ }
                }
                var mb = total / (1024.0 * 1024.0);
                if (mb > _peakMb) _peakMb = mb;
            }
            catch { /* 快照失败，忽略本次轮询 */ }
            Thread.Sleep(50);
        }
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct PROCESSENTRY32
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    const uint TH32CS_SNAPPROCESS = 0x2;
    const uint InvalidHandle32 = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>返回 rootPid 及其全部后代进程 pid。</summary>
    public static IEnumerable<int> GetProcessTree(int rootPid)
    {
        var children = new Dictionary<int, List<int>>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == (IntPtr)InvalidHandle32 || snap == (IntPtr)(-1)) yield break;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref e)) yield break;
            do
            {
                if (e.th32ProcessID == e.th32ParentProcessID) continue;
                if (!children.TryGetValue(e.th32ParentProcessID, out var list))
                    children[e.th32ParentProcessID] = list = new List<int>();
                list.Add(e.th32ProcessID);
            } while (Process32Next(snap, ref e));
        }
        finally
        {
            CloseHandle(snap);
        }

        var stack = new Stack<int>();
        stack.Push(rootPid);
        var seen = new HashSet<int>();
        while (stack.Count > 0)
        {
            var pid = stack.Pop();
            if (!seen.Add(pid)) continue;
            yield return pid;
            if (children.TryGetValue(pid, out var kids))
                foreach (var k in kids) stack.Push(k);
        }
    }
}
