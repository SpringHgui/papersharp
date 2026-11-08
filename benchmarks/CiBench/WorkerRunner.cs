using System.Collections.Concurrent;
using System.Diagnostics;
using CiBench.Engines;

namespace CiBench;

/// <summary>
/// 子进程侧测量。父进程只负责 spawn 与计时，所有渲染都发生在隔离的子进程里，
/// 避免上一家的原生内存/Skia 字体缓存把下一家的 RSS 抬上去（PdfBench 既有口径）。
/// </summary>
public static class WorkerRunner
{
    public const string OneMarker = "CIBENCH_ONE:";

    /// <summary>cold-one：全新进程 → 初始化 → 出一张 → 落盘 → 退出。父进程用墙钟计时。</summary>
    public static async Task<int> ColdOneAsync(string engineName, string htmlPath, string outPdfPath)
    {
        var d = EngineRegistry.Find(engineName);
        if (d is null) { Console.Error.WriteLine($"未知引擎 {engineName}"); return 2; }

        await using var engine = d.Factory();
        var skip = engine.Preflight();
        if (skip is not null) { Console.Error.WriteLine(skip); return 3; }

        var html = await File.ReadAllTextAsync(htmlPath);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(htmlPath))!;
        var sampler = new RssSampler(20);
        sampler.Start();
        try
        {
            await engine.InitializeAsync(html, baseDir, CancellationToken.None);
            await using var worker = await engine.CreateWorkerAsync(html, baseDir, CancellationToken.None);
            byte[] bytes = await worker.RenderAsync(CancellationToken.None);
            File.WriteAllBytes(outPdfPath, bytes);
            var (peak, _) = sampler.Stop();
            // 必须是紧凑单行 JSON：父进程按行读取 stdout 并解析 marker 之后的内容，
            // 缩进版会把 '}' 拆到后续行，导致解析失败、冷启动内存与体积指标变 N/A。
            Console.WriteLine(OneMarker + System.Text.Json.JsonSerializer.Serialize(new
            {
                rssPeakMb = peak,
                sizeKb = bytes.Length / 1024.0,
            }));
            return 0;
        }
        catch (Exception ex)
        {
            sampler.Stop();
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// worker-one：常驻进程，预热后依次测「单张热启动」与「并发批次」，把该引擎的
    /// Warm + Batches 写进 jsonOut 供父进程合并。
    /// </summary>
    public static async Task<int> WorkerOneAsync(string engineName, string jsonOut, BenchOptions o)
    {
        var d = EngineRegistry.Find(engineName);
        if (d is null) { Console.Error.WriteLine($"未知引擎 {engineName}"); return 2; }

        var result = new EngineResult
        {
            Name = d.Name,
            Display = d.Display,
            RenderingEngine = d.RenderingEngine,
        };

        await using var engine = d.Factory();
        result.Version = engine.Version;
        var skip = engine.Preflight();
        if (skip is not null)
        {
            result.Status = "skipped";
            result.SkipReason = skip;
            await File.WriteAllTextAsync(jsonOut, JsonIo.Serialize(result));
            Console.Error.WriteLine(skip);
            return 3;
        }

        string tempDir = Path.Combine(o.OutDir, "_tmp", d.Name);
        Directory.CreateDirectory(tempDir);
        var ct = CancellationToken.None;

        try
        {
            var html = await File.ReadAllTextAsync(o.HtmlPath);
            var baseDir = Path.GetDirectoryName(Path.GetFullPath(o.HtmlPath))!;
            await engine.InitializeAsync(html, baseDir, ct);
            result.Version = engine.Version;

            int poolSize = Math.Max(1, o.Concurrency);
            var pool = new List<IPdfWorker>(poolSize);
            for (int i = 0; i < poolSize; i++)
                pool.Add(await engine.CreateWorkerAsync(html, baseDir, ct));

            // 预热：JIT / 字体 / 浏览器只付一次，且不计入任何计时。
            // 并发预热，避免顺序跑 concurrency × 2 次把 Chromium 那家的启动拖到几十秒。
            await Task.WhenAll(pool.Select(async w =>
            {
                for (int j = 0; j < 2; j++) await w.RenderAsync(ct);
            }));
            Console.Error.WriteLine($"[{d.Name}] 预热完成，workers={poolSize}");

            result.Warm = await MeasureWarmAsync(pool[0], o.Repeats, tempDir, ct);
            Console.Error.WriteLine($"[{d.Name}] 热启动单张 mean={result.Warm.MeanMs:F0}ms");

            foreach (int n in o.Batches)
            {
                var b = await MeasureBatchAsync(pool, n, o.Concurrency, tempDir, ct);
                result.Batches.Add(b);
                Console.Error.WriteLine($"[{d.Name}] 并发 {n} 张：wall={b.WallMs:F0}ms, " +
                    $"avg={b.AvgMsPerDoc:F1}ms/doc, rss={b.RssPeakMb:F0}MB, ok={b.Success}");
            }

            foreach (var w in pool) await w.DisposeAsync();
            result.Status = "ok";
            await File.WriteAllTextAsync(jsonOut, JsonIo.Serialize(result));
            TryClean(tempDir);
            return 0;
        }
        catch (Exception ex)
        {
            result.Status = "failed";
            // IronPdf 的 LicensingException 消息是一大段营销文案，只留首行，别把 CI 日志刷爆
            result.Error = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}";
            await File.WriteAllTextAsync(jsonOut, JsonIo.Serialize(result));
            Console.Error.WriteLine(result.Error);
            TryClean(tempDir);
            return 1;
        }
    }

    static async Task<WarmResult> MeasureWarmAsync(IPdfWorker w, int repeats, string tempDir, CancellationToken ct)
    {
        var lat = new List<double>(repeats);
        long lastSize = 0;
        string? firstError = null;
        var sampler = new RssSampler(20);

        long allocBefore = GC.GetTotalAllocatedBytes(true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        sampler.Start();

        for (int i = 0; i < repeats; i++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                byte[] bytes = await w.RenderAsync(ct);
                string path = Path.Combine(tempDir, $"warm_{i}.pdf");
                File.WriteAllBytes(path, bytes);
                sw.Stop();
                TryDelete(path);
                lat.Add(sw.Elapsed.TotalMilliseconds);
                lastSize = bytes.Length;
            }
            catch (Exception ex)
            {
                sw.Stop();
                firstError ??= $"{ex.GetType().Name}: {ex.Message}".Split('\n')[0];
            }
        }

        var (peak, _) = sampler.Stop();
        long allocKb = (GC.GetTotalAllocatedBytes(true) - allocBefore) / 1024;
        var sorted = Stats.Sorted(lat);
        double mean = Stats.Mean(lat);
        return new WarmResult
        {
            Repeats = repeats,
            Success = lat.Count,
            MeanMs = mean,
            P50Ms = Stats.Percentile(sorted, 0.50),
            P95Ms = Stats.Percentile(sorted, 0.95),
            StdMs = Stats.Std(lat, mean),
            AllocKbPerDoc = lat.Count > 0 ? allocKb / (double)lat.Count : double.NaN,
            RssPeakMb = peak,
            SizeKb = lastSize / 1024.0,
            Gen0 = GC.CollectionCount(0) - g0,
            Gen1 = GC.CollectionCount(1) - g1,
            Gen2 = GC.CollectionCount(2) - g2,
            FirstError = firstError,
        };
    }

    static async Task<BatchResult> MeasureBatchAsync(
        IReadOnlyList<IPdfWorker> pool, int documents, int concurrency, string tempDir, CancellationToken ct)
    {
        var queue = new ConcurrentQueue<int>(Enumerable.Range(0, documents));
        var latencies = new ConcurrentBag<double>();
        var sizes = new ConcurrentBag<long>();
        int success = 0, failed = 0;
        string? firstError = null;
        var gate = new object();
        var sampler = new RssSampler(20);

        long allocBefore = GC.GetTotalAllocatedBytes(true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

        sampler.Start();
        var wall = Stopwatch.StartNew();
        var tasks = pool.Select(w => Task.Run(async () =>
        {
            while (queue.TryDequeue(out int i))
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    byte[] bytes = await w.RenderAsync(ct);
                    string path = Path.Combine(tempDir, $"b_{documents}_{i}_{Guid.NewGuid():N}.pdf");
                    File.WriteAllBytes(path, bytes);
                    sw.Stop();
                    TryDelete(path);
                    latencies.Add(sw.Elapsed.TotalMilliseconds);
                    sizes.Add(bytes.Length);
                    Interlocked.Increment(ref success);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Interlocked.Increment(ref failed);
                    string msg = $"{ex.GetType().Name}: {ex.Message}".Split('\n')[0];
                    if (firstError is null) lock (gate) firstError ??= msg;
                }
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        wall.Stop();
        var (peak, last) = sampler.Stop();

        var sorted = Stats.Sorted(latencies);
        double seconds = wall.Elapsed.TotalSeconds;
        return new BatchResult
        {
            Documents = documents,
            Concurrency = concurrency,
            Success = success,
            Failed = failed,
            WallMs = wall.Elapsed.TotalMilliseconds,
            AvgMsPerDoc = wall.Elapsed.TotalMilliseconds / documents,
            ThroughputDps = seconds > 0 ? success / seconds : double.NaN,
            LatencyP50Ms = Stats.Percentile(sorted, 0.50),
            LatencyP95Ms = Stats.Percentile(sorted, 0.95),
            LatencyMaxMs = sorted.Count > 0 ? sorted[^1] : double.NaN,
            RssPeakMb = peak,
            RssEndMb = last,
            AllocMbTotal = (GC.GetTotalAllocatedBytes(true) - allocBefore) / (1024.0 * 1024.0),
            Gen0 = GC.CollectionCount(0) - g0,
            Gen1 = GC.CollectionCount(1) - g1,
            Gen2 = GC.CollectionCount(2) - g2,
            SizeKbAvg = sizes.Count > 0 ? sizes.Average() / 1024.0 : double.NaN,
            FirstError = firstError,
        };
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    static void TryClean(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir)) File.Delete(f);
            Directory.Delete(dir, true);
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && Directory.Exists(parent) &&
                !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch { }
    }
}
