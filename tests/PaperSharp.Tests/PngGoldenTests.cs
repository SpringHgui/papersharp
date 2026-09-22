namespace PaperSharp.Tests;

/// <summary>
/// 像素层（便宜的一半）：PNG 长图路径是字节级确定的，直接比 golden SHA。
/// 布局/绘制任何一处回归都会立刻红。基准在 goldens/png/，重生成：
///   UPDATE_GOLDENS=1 dotnet test --filter PngGoldenTests
/// 注意：光栅结果依赖本机字体栈，跨平台 CI 需按 OS 分别存基准（当前仅 Windows）。
/// </summary>
public class PngGoldenTests
{
    [Theory]
    [MemberData(nameof(FixtureSamples))]
    public void Png_matches_golden(string name)
    {
        var png = Fixture.RenderPng(name);
        Assert.True(Fixture.MatchGolden($"png/{name}.png", png),
            $"{name}.png 与基准不一致（或基准缺失）→ 看 diff 确认是有意变更后 UPDATE_GOLDENS=1 重生成");
    }

    public static TheoryData<string> FixtureSamples => new(Fixture.Samples);
}
