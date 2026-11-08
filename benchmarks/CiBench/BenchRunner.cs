using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CiBench.Engines;

namespace CiBench;

/// <summary>父进程编排：每个引擎先做轻量预检，再分别 spawn 冷启动子进程与热态子进程，合并结果。</summary>
public static class BenchRunner
{
    const int ColdChildTimeoutMs = 180_000;
    const int WorkerChildTimeoutMs = 1_800_000;

    public static async Task<BenchReport> RunAsync(BenchOptions o)
    {
        Directory.CreateDirectory(o.OutDir);
        var htmlBytes = await File.ReadAllBytesAsync(o.HtmlPath);

        var report = new BenchReport
        {
            Env = JsonIo.CollectEnv(o.HtmlPath, !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("IRONPDF_LICENSE_KEY"))),
            Config = new ConfigSnapshot
            {
                Repeats = o.Repeats,
                Batches = o.Batches,
                Concurrency = o.Concurrency,
                Quick = o.Quick,
                HtmlPath = o.HtmlPath,
                HtmlSha256 = Convert.ToHexString(SHA256.HashData(htmlBytes))[..16],
                HtmlBytes = htmlBytes.Length,
            },
            Notes =
            [
                "四家吃同一份 HTML 字符串，产出 byte[] 后由测量内核统一落盘；计时含 PDF 写盘，不含临时文件删除。",
                "冷启动 = 每次全新子进程：含 CLR 启动、JIT、字体/浏览器初始化、渲染、落盘；父进程用墙钟计时，N 次取平均。",
                "热启动 = 常驻进程预热后连续导出单张，逐次墙钟取平均；预热（每个 worker 2 次）不计入。",
                "并发批次 = 热态下固定 N 个 worker 消费 documents 张队列任务；平均耗时 = 批次总墙钟 / 文档数，吞吐 = 成功数 / 总秒数。",
                "RSS = 本进程 + 全部后代进程工作集之和（含 Chrome 多进程树），20~25ms 采样一轮；峰值取最近 3 次采样的中位数，抑制 Windows Toolhelp32 父子快照因 PID 复用产生的单点假尖峰（持续上升的真实峰值不受影响）。",
                "每个引擎在独立子进程测量，避免上一家的原生内存/字体缓存污染下一家的 RSS。",
                "PuppeteerSharp / Microsoft.Playwright 每次重新 SetContent + 导出，PaperSharp / iText pdfHTML（及可选的 IronPdf）每次从 HTML 重新排版，不吃跨文档缓存红利。",
                "iText 7 pdfHTML 是解析型 HTML/CSS 布局引擎（纯托管、不启动浏览器），CSS 支持面比 Blink 窄（不支持 flex/grid 等）；本报告只比较同一语料的排版成本，不比较视觉还原度，产物体积也不代表还原度。",
                "iText pdfHTML 为 AGPLv3 / 商业双授权：仅用作本基准工具的被测依赖；若引入闭源产品或网络服务需评估 AGPL 义务或购买商业授权，可用 -p:IncludeIText=false 从编译中排除。",
                "iText 中文依赖系统 CJK 字体：优先加载单个中文字体文件，找不到才回退 AddSystemFonts() 全盘扫描；缺字只会让产物偏小，耗时仍如实测量。",
                "默认组合中 PaperSharp / PuppeteerSharp / Microsoft.Playwright 为宽松开源、无需 license；Playwright 复用系统 Chrome，RSS 进程树里会包含它的 driver 与 Chrome 子进程。",
                "IronPdf 是商业库，已移出默认组合；只有显式 --engines ironpdf 且编译时 -p:IncludeIronPdf=true 才参赛，缺 IRONPDF_LICENSE_KEY 时标记 skipped，不把试用/水印产物计入正式基准。",
                "skipped / failed 一律如实入账，不补零、不插值；图表里显示为 N/A。",
            ],
        };

        var descriptors = EngineRegistry.Resolve(o.Engines);
        foreach (var d in descriptors)
        {
            await using var engine = d.Factory();
            var result = new EngineResult
            {
                Name = d.Name,
                Display = d.Display,
                RenderingEngine = d.RenderingEngine,
                Version = engine.Version,
            };

            var skip = engine.Preflight();
            if (skip is not null)
            {
                result.Status = "skipped";
                result.SkipReason = skip;
                Console.WriteLine($"=== {d.Display} === SKIPPED: {skip}");
                report.Engines.Add(result);
                SaveIncremental(o, report);
                continue;
            }

            if (d.Name == "ironpdf" && string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("IRONPDF_LICENSE_KEY")))
            {
                result.Warning = "IronPdf 试用模式（无 license）：产物可能带水印或受限，数字仅供内部参考";
                if (!report.Notes.Contains("IronPdf 以试用模式参赛（--allow-unlicensed-ironpdf），输出可能含水印，不应与正式授权版横比。"))
                    report.Notes.Add("IronPdf 以试用模式参赛（--allow-unlicensed-ironpdf），输出可能含水印，不应与正式授权版横比。");
            }

            Console.WriteLine($"=== {d.Display} ({d.RenderingEngine}) ===");
            try
            {
                result.Cold = await MeasureColdAsync(d.Name, o);
                if (result.Cold.Success > 0)
                    Console.WriteLine($"  冷启动 {result.Cold.Repeats} 次：mean {result.Cold.MeanMs:F0}ms " +
                        $"(p50 {result.Cold.P50Ms:F0} / p95 {result.Cold.P95Ms:F0}), 成功 {result.Cold.Success}");
                else
                    Console.WriteLine($"  冷启动 {result.Cold.Repeats} 次：无成功样本（{result.Cold.FirstError ?? "未知原因"}）");
            }
            catch (Exception ex)
            {
                result.Error = $"cold: {ex.GetType().Name}: {ex.Message}";
                Console.Error.WriteLine($"  {result.Error}");
            }

            string jsonOut = Path.Combine(o.OutDir, $"_worker_{d.Name}.json");
            try { File.Delete(jsonOut); } catch { }
            string childArgs = $"worker-one {d.Name} {SelfLaunch.Quote(jsonOut)} " + string.Join(' ', o.ToChildArgs());
            var child = await SelfLaunch.RunAsync(childArgs, WorkerChildTimeoutMs);
            if (child.StdErr.Length > 0)
                foreach (var line in child.StdErr.Split('\n').Where(x => x.Trim().Length > 0).Take(12))
                    Console.WriteLine($"  {line.TrimEnd()}");

            var merged = TryReadWorkerResult(jsonOut);
            if (merged is not null)
            {
                result.Warm = merged.Warm;
                result.Batches = merged.Batches;
                if (!string.IsNullOrEmpty(merged.Version)) result.Version = merged.Version;
                if (merged.Status == "failed") result.Error = AppendError(result.Error, merged.Error ?? "worker-one failed");
            }
            else if (child.Failed)
            {
                result.Error = AppendError(result.Error, $"worker-one: {child.BriefError}");
            }

            bool hasData = (result.Cold?.Success ?? 0) > 0 || (result.Warm?.Success ?? 0) > 0;
            result.Status = hasData ? "ok" : "failed";
            if (result.Status == "failed" && result.Error is null)
                result.Error = "没有任何成功的测量样本";
            report.Engines.Add(result);
            SaveIncremental(o, report);
        }

        return report;
    }

    static async Task<ColdResult> MeasureColdAsync(string name, BenchOptions o)
    {
        string coldDir = Path.Combine(o.OutDir, "_cold");
        Directory.CreateDirectory(coldDir);
        var times = new List<double>(o.Repeats);
        var rss = new List<double>(o.Repeats);
        var sizes = new List<double>(o.Repeats);
        int failed = 0;
        string? firstError = null;

        for (int i = 0; i < o.Repeats; i++)
        {
            string pdf = Path.Combine(coldDir, $"{name}_{i}.pdf");
            string args = $"cold-one {name} {SelfLaunch.Quote(o.HtmlPath)} {SelfLaunch.Quote(pdf)}";
            var child = await SelfLaunch.RunAsync(args, ColdChildTimeoutMs);
            if (!child.Failed)
            {
                times.Add(child.WallMs);
                var one = ParseOnePayload(child.StdOut);
                if (one is not null) { rss.Add(one.Value.RssPeakMb); sizes.Add(one.Value.SizeKb); }
            }
            else
            {
                failed++;
                firstError ??= child.BriefError;
            }
            try { File.Delete(pdf); } catch { }
            if ((i + 1) % 5 == 0 || i + 1 == o.Repeats)
                Console.WriteLine($"  冷启动 {i + 1}/{o.Repeats}，最近一次 {child.WallMs:F0}ms");
        }
        try { Directory.Delete(coldDir, true); } catch { }

        var sorted = Stats.Sorted(times);
        double mean = Stats.Mean(times);
        return new ColdResult
        {
            Repeats = o.Repeats,
            Success = times.Count,
            Failed = failed,
            MeanMs = mean,
            MinMs = sorted.Count > 0 ? sorted[0] : double.NaN,
            P50Ms = Stats.Percentile(sorted, 0.50),
            P95Ms = Stats.Percentile(sorted, 0.95),
            MaxMs = sorted.Count > 0 ? sorted[^1] : double.NaN,
            StdMs = Stats.Std(times, mean),
            RssPeakMbMean = Stats.Mean(rss),
            SizeKbMean = Stats.Mean(sizes),
            FirstError = firstError,
        };
    }

    static (double RssPeakMb, double SizeKb)? ParseOnePayload(string stdout)
    {
        try
        {
            var line = stdout.Split('\n').LastOrDefault(x => x.Contains(WorkerRunner.OneMarker));
            if (line is null) return null;
            string json = line[(line.IndexOf(WorkerRunner.OneMarker, StringComparison.Ordinal) + WorkerRunner.OneMarker.Length)..].Trim();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            double rss = root.TryGetProperty("rssPeakMb", out var r) ? r.GetDouble() : double.NaN;
            double size = root.TryGetProperty("sizeKb", out var s) ? s.GetDouble() : double.NaN;
            return (rss, size);
        }
        catch { return null; }
    }

    static EngineResult? TryReadWorkerResult(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonIo.Deserialize<EngineResult>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  worker 结果解析失败: {ex.Message}");
            return null;
        }
    }

    static string AppendError(string? existing, string next) =>
        string.IsNullOrEmpty(existing) ? next : existing + " | " + next;

    static void SaveIncremental(BenchOptions o, BenchReport report)
    {
        try
        {
            File.WriteAllText(Path.Combine(o.OutDir, "results.json"),
                JsonIo.Serialize(report), new UTF8Encoding(false));
        }
        catch (Exception ex) { Console.Error.WriteLine($"增量落盘失败: {ex.Message}"); }
    }
}
