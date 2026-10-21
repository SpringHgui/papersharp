using PaperSharp;

namespace PaperSharpBlazorApp.Services;

/// <summary>
/// 引擎盒矩形 vs 浏览器 getBoundingClientRect() 的对位求差。
/// 纯函数（不碰 IO / Skia），便于单测钉住对位口径。
/// </summary>
public static class PlaygroundDiff
{
    /// <summary>
    /// 表格里最多返回的行数。叠加层画的是全部漂移盒，表格只给最坏的这些——
    /// 真实站点动辄上千元素，全量回传给 Blazor 电路纯属自找的卡顿。
    /// </summary>
    public const int MaxReturnedRows = 400;

    public static DiffResult Compare(IReadOnlyList<BoxRect> engine, IReadOnlyList<DomRect> browser, float tolerancePx)
    {
        var engineMap = new Dictionary<string, DomRect>(StringComparer.Ordinal);
        foreach (var b in engine)
            engineMap[b.DomPath] = new DomRect { DomPath = b.DomPath, Tag = b.Tag, X = b.X, Y = b.Y, W = b.W, H = b.H };
        var browserMap = IndexByPath(browser, b => b.DomPath);

        var rows = new List<DiffRow>(engineMap.Count + browserMap.Count);
        int ok = 0, drift = 0, engineOnly = 0, browserOnly = 0;
        float worst = 0;
        double sumDrift = 0;

        foreach (var (path, e) in engineMap)
        {
            if (!browserMap.TryGetValue(path, out var b))
            {
                engineOnly++;
                rows.Add(new DiffRow(e, null, 0, 0, 0, 0, DiffKind.EngineOnly));
                continue;
            }
            var (dx, dy, dw, dh) = (b.X - e.X, b.Y - e.Y, b.W - e.W, b.H - e.H);
            var max = Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy)), Math.Max(Math.Abs(dw), Math.Abs(dh)));
            var kind = max <= tolerancePx ? DiffKind.Ok : DiffKind.Drift;
            if (kind == DiffKind.Ok) ok++;
            else { drift++; sumDrift += max; }
            worst = Math.Max(worst, max);
            rows.Add(new DiffRow(e, b, dx, dy, dw, dh, kind) { MaxDelta = max });
        }
        foreach (var (path, b) in browserMap)
            if (!engineMap.ContainsKey(path))
            {
                browserOnly++;
                rows.Add(new DiffRow(null, b, 0, 0, 0, 0, DiffKind.BrowserOnly));
            }

        // 最坏的排前面；单边缺盒没有 Δ 数值，但比「偏 1px」更值得先看一眼，故置顶。
        var top = rows.Where(r => r.Kind != DiffKind.Ok)
            .OrderByDescending(r => r.MaxDelta)
            .Take(MaxReturnedRows)
            .ToList();

        return new DiffResult(top, new DiffSummary(
            Total: rows.Count, Ok: ok, Drift: drift, EngineOnly: engineOnly, BrowserOnly: browserOnly,
            WorstDelta: worst, MeanDriftPx: drift > 0 ? sumDrift / drift : 0,
            Returned: top.Count, TolerancePx: tolerancePx));
    }

    static Dictionary<string, T> IndexByPath<T>(IEnumerable<T> items, Func<T, string> path)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var it in items) map[path(it)] = it;   // 同 key 后来者覆盖：DomPath 唯一性由核心库测试钉住
        return map;
    }
}
