using System.Globalization;

namespace CiBench;

/// <summary>
/// CI 退出码：0=通过，3=--strict 下有引擎 skipped/failed，4=相对基线劣化超阈值。
/// 门禁默认只约束 papersharp（自家回归），第三方库的版本/机器波动不应把 CI 弄红。
/// </summary>
public static class Gate
{
    public static int Evaluate(BenchOptions o, BenchReport report)
    {
        Console.WriteLine();
        Console.WriteLine("引擎状态:");
        foreach (var e in report.Engines)
        {
            string extra = e.Status switch
            {
                "skipped" => e.SkipReason ?? "",
                "failed" => e.Error ?? "",
                _ => $"冷 {Fmt(e.Cold?.MeanMs)}ms / 热 {Fmt(e.Warm?.MeanMs)}ms",
            };
            extra = extra.Split('\n')[0].Trim();
            Console.WriteLine($"  {e.Display,-18} {e.Status,-8} {extra}");
        }

        int exit = 0;
        if (o.Strict && report.Engines.Any(e => !e.IsOk))
        {
            Console.Error.WriteLine("[strict] 存在 skipped/failed 引擎，判为失败");
            exit = 3;
        }

        if (o.BaselinePath is null) return exit;
        if (!File.Exists(o.BaselinePath))
        {
            Console.Error.WriteLine($"[baseline] 文件不存在，跳过回归门禁: {o.BaselinePath}");
            return exit;
        }

        var baseline = JsonIo.Deserialize<BenchReport>(File.ReadAllText(o.BaselinePath));
        if (baseline is null)
        {
            Console.Error.WriteLine("[baseline] 解析失败，跳过回归门禁");
            return exit;
        }

        if (baseline.Config.Repeats != o.Repeats || !baseline.Config.Batches.SequenceEqual(o.Batches))
        {
            Console.Error.WriteLine("[baseline] 基线与本次口径不同（repeats/batches），跳过回归门禁");
            return exit;
        }

        var regressions = new List<string>();
        foreach (var cur in report.Engines.Where(e => e.IsOk && o.GateEngines.Contains(e.Name)))
        {
            var old = baseline.Engines.FirstOrDefault(e => e.Name == cur.Name && e.IsOk);
            if (old is null) continue;

            Check(regressions, cur.Name, "cold.meanMs", old.Cold?.MeanMs, cur.Cold?.MeanMs, o.MaxRegressionPct);
            Check(regressions, cur.Name, "warm.meanMs", old.Warm?.MeanMs, cur.Warm?.MeanMs, o.MaxRegressionPct);
            foreach (var cb in cur.Batches)
            {
                var ob = old.Batches.FirstOrDefault(b => b.Documents == cb.Documents);
                Check(regressions, cur.Name, $"batch{cb.Documents}.avgMsPerDoc",
                    ob?.AvgMsPerDoc, cb.AvgMsPerDoc, o.MaxRegressionPct);
            }
        }

        if (regressions.Count == 0)
        {
            Console.WriteLine($"[baseline] 门禁通过（阈值 +{o.MaxRegressionPct.ToString("0.#", CultureInfo.InvariantCulture)}%）");
        }
        else
        {
            Console.Error.WriteLine($"[baseline] 检测到 {regressions.Count} 项劣化超过 +{o.MaxRegressionPct:F0}%:");
            foreach (var r in regressions) Console.Error.WriteLine("  " + r);
            exit = 4;
        }
        return exit;
    }

    static void Check(List<string> list, string engine, string metric, double? before, double? after, double maxPct)
    {
        if (before is null or <= 0 || after is null || double.IsNaN(before.Value) || double.IsNaN(after.Value)) return;
        double pct = (after.Value - before.Value) / before.Value * 100.0;
        if (pct > maxPct)
            list.Add($"{engine} {metric}: {before.Value:F1} → {after.Value:F1} ms (+{pct:F1}%)");
    }

    static string Fmt(double? v) => v is null || double.IsNaN(v.Value) ? "n/a" : v.Value.ToString("F0", CultureInfo.InvariantCulture);
}
