namespace PaperSharp.Tests;

/// <summary>
/// 像素层（便宜的一半）：PNG 长图路径与 golden 做**解码后像素**比对。
/// 布局/绘制任何一处回归都会立刻红。基准在 goldens/png/，重生成：
///   UPDATE_GOLDENS=1 dotnet test --filter PngGoldenTests
/// 为什么比像素而不比文件字节：字节只能当「画面没变」的代理，一旦换 PNG 编码器
/// （见 <see cref="PngEncoderTests"/>）所有基准就得整体翻新，等于把真正的回归淹没在
/// 合法的编码差异里。解码后比 RGBA，代理就换成了本体——编码器随便换，画面动一下就红。
/// 注意：光栅结果依赖本机字体栈，跨平台 CI 需按 OS 分别存基准（当前仅 Windows）。
/// </summary>
public class PngGoldenTests
{
    [Theory]
    [MemberData(nameof(FixtureSamples))]
    public void Png_matches_golden(string name)
    {
        var png = Fixture.RenderPng(name);
        Assert.True(Fixture.MatchGolden($"png/{name}.png", png, Fixture.SamePixels),
            $"{name}.png 解码后与基准像素不一致（或基准缺失）→ 生成 diff 确认是有意变更后 UPDATE_GOLDENS=1 重生成");
    }

    public static TheoryData<string> FixtureSamples => new(Fixture.Samples);
}
