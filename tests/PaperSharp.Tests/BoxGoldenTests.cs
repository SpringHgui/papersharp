using PaperSharpBlazorApp.Services;

namespace PaperSharp.Tests;

/// <summary>
/// 几何层（另一半）：把 Playground 里能选到的**每一个**样例都渲染一遍，用生产同款
/// <see cref="PlaygroundDiff.Compare"/> 与金标盒矩形对位求差，容差钉死 1px。
///
/// 为什么单独立一道、而不是靠 PngGoldenTests：像素基准会因字体光栅化整片翻（跨机/换字体栈就红），
/// 但它对「某个盒往左挪了 3px」不敏感——那 3px 被字形纹理淹没。盒级基准反过来，直接盯几何：
/// 布局/层叠任何一处改动让某个 border-box 偏离 1px 以上就红，且与像素编码、字体平滑无关。
///
/// 为什么金标而不是现量浏览器：Playground 的 Δ 表本是「引擎 vs 无头浏览器 getBoundingClientRect」，
/// 而单测跑不了浏览器（见 <see cref="PlaygroundTests"/> 的注释——它只能测对位算法本身，测不了真实样例）。
/// 把浏览器那一侧的真值固化成盒基准，就等于把某次 boxdiff 对拍过的几何钉进回归门，之后每次跑 CI 复核。
///
/// 基准在 goldens/boxes/，重生成：UPDATE_GOLDENS=1 dotnet test --filter BoxGoldenTests
/// 依赖本机字体栈定文字宽高，跨平台 CI 需按 OS 分别存基准（与 PngGoldenTests 同一局限，当前仅 Windows）。
/// </summary>
public class BoxGoldenTests
{
    /// <summary>盒级差异容差：任一 border-box 的 x/y/w/h 偏离基准超过这么多 px 即判回归。</summary>
    const float TolerancePx = 1f;

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void Boxes_stay_within_1px_of_golden(string name)
    {
        var actual = Fixture.RenderBoxes(name);

        if (Fixture.UpdateGoldens)
        {
            Fixture.SaveBoxGolden(name, actual);
            return;
        }

        var golden = Fixture.LoadBoxGolden(name);
        Assert.True(golden is { Count: > 0 },
            $"{name} 缺盒基准 goldens/boxes/{name}.json（或为空）→ 跑 UPDATE_GOLDENS=1 dotnet test --filter BoxGoldenTests 生成");

        // 与 Playground 完全同一口径求差：engine=本轮渲染，browser=金标真值。
        var d = PlaygroundDiff.Compare(actual, golden!, TolerancePx);

        Assert.True(d.Summary.EngineOnly == 0 && d.Summary.BrowserOnly == 0,
            $"{name}: 盒集合与基准不符（引擎独 {d.Summary.EngineOnly} 个 / 基准独 {d.Summary.BrowserOnly} 个）" +
            "——某元素成盒/散盒情况变了，比 px 漂移更严重\n" +
            Offenders(d, r => r.Kind is DiffKind.EngineOnly or DiffKind.BrowserOnly, r => $"  {r.Kind,-11} {r.DomPath}"));

        Assert.True(d.Summary.Drift == 0,
            $"{name}: {d.Summary.Drift} 个盒偏离基准超过 {TolerancePx}px，最坏 {d.Summary.WorstDelta:0.##}px\n" +
            Offenders(d, r => r.Kind == DiffKind.Drift,
                r => $"  max {r.MaxDelta,6:0.##}  {r.DomPath}   dx{Signed(r.Dx)} dy{Signed(r.Dy)} dw{Signed(r.Dw)} dh{Signed(r.Dh)}"));
    }

    static string Offenders(DiffResult d, Func<DiffRow, bool> pick, Func<DiffRow, string> line)
    {
        var rows = d.Rows.Where(pick).Take(15);
        var s = string.Join("\n", rows.Select(line));
        int more = d.Rows.Count(pick) - 15;
        return more > 0 ? s + $"\n  …另有 {more} 行" : s;
    }

    static string Signed(float v) => (v >= 0 ? "+" : "") + v.ToString("0.##");

    public static TheoryData<string> AllSamples => new(Fixture.PlaygroundSampleNames());
}
