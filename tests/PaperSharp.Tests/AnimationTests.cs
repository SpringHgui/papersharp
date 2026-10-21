using SkiaSharp;

namespace PaperSharp.Tests;

/// <summary>
/// 动画确定性取帧（P4）：@keyframes + animation 在给定 animationTime 求值插值。
/// 只测引擎已渲染的属性（opacity / 颜色）——用盒中心像素的通道值做连续量断言
/// （对定位稳健、跨渲染栈稳定），不钉逐字节 golden。
/// transform 明确不支持：输出须逐字节等于「无动画」基线，以此钉死「不造假」。
/// </summary>
public class AnimationTests
{
    // #b = 200×200、margin:0 起，中心 (100,100) 恒在界内
    const string BoxHtml = "<div id=b></div>";
    static string Doc(string bDecls, string keyframes = "")
        => "html,body{margin:0}#b{width:200px;height:200px;" + bDecls + "}" + keyframes;

    static byte[] Png(string css, double? t)
        => Renderer.RenderPng(BoxHtml, css, 400f, baseDir: null, scale: 1f, animationTime: t);

    static SKColor Center(byte[] png) { using var b = SKBitmap.Decode(png); return b.GetPixel(100, 100); }
    static bool SameBytes(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

    const string FadeKf = "@keyframes fade{from{opacity:0}to{opacity:1}}";
    static string Fade(string fillDir) => Doc("background:#ff0000;opacity:0;animation:fade 2s linear " + fillDir, FadeKf);

    [Fact]
    public void Opacity_fade_interpolates_monotonically()
    {
        var css = Fade("forwards");
        int g0 = Center(Png(css, 0.0)).Green;     // 透明 → 白底 → 绿≈255
        int gHalf = Center(Png(css, 1.0)).Green;   // 半透 → 绿≈128
        int gEnd = Center(Png(css, 2.0)).Green;    // 满不透明红 → 绿≈0
        Assert.True(g0 > 240, $"t=0 应近乎全透明（绿≈255），实测 {g0}");
        Assert.True(gEnd < 15, $"t=2s 应满不透明（绿≈0），实测 {gEnd}");
        Assert.True(g0 > gHalf && gHalf > gEnd, $"绿通道应随时长单调下降：{g0} > {gHalf} > {gEnd}");
    }

    [Fact]
    public void Fill_forwards_holds_end_state()
    {
        var css = Fade("forwards");
        int gEnd = Center(Png(css, 2.0)).Green;
        int gBeyond = Center(Png(css, 9.0)).Green;  // 远超时长 → 保持末帧
        Assert.True(gBeyond < 15, $"fill:forwards 末态应保持满不透明，实测绿 {gBeyond}");
        Assert.Equal(gEnd, gBeyond);
    }

    [Fact]
    public void Fill_none_reverts_after_end()
    {
        // 无 fill：动画结束后回到元素自身样式（opacity:0 → 白底）
        var css = Fade("none");
        int gDuring = Center(Png(css, 1.0)).Green;   // 作用中 → 半透
        int gAfter = Center(Png(css, 3.0)).Green;     // 结束后 → 透明
        Assert.True(gDuring < gAfter, $"无 fill 结束后应回落到不透明基线：{gDuring} < {gAfter}");
        Assert.True(gAfter > 240, $"结束后应透明（绿≈255），实测 {gAfter}");
    }

    [Fact]
    public void Same_time_is_deterministic()
    {
        var css = Fade("forwards");
        var a = Png(css, 1.234);
        var b = Png(css, 1.234);
        Assert.True(SameBytes(a, b), "同一时刻两次渲染须逐字节一致");
    }

    [Fact]
    public void Color_animation_takes_effect()
    {
        // 蓝 → 红，中心像素红通道随时长升
        var css = Doc("background:#0000ff;animation:c 1s linear forwards",
            "@keyframes c{from{background-color:#0000ff}to{background-color:#ff0000}}");
        int r0 = Center(Png(css, 0.0)).Red;
        int rHalf = Center(Png(css, 0.5)).Red;
        int r1 = Center(Png(css, 1.0)).Red;
        Assert.True(r0 < 15, $"t=0 应仍是蓝（红≈0），实测 {r0}");
        Assert.True(r1 > 240, $"t=1s 应插值到红（红≈255），实测 {r1}");
        Assert.True(r0 < rHalf && rHalf < r1, $"红通道应单调上升：{r0} < {rHalf} < {r1}");
    }

    [Fact]
    public void Alternate_direction_reverses_late_iterations()
    {
        // 2 迭代 infinite alternate：第 1 迭代中点与第 2 迭代（反向）中点应对称（都≈半透）
        var css = Fade("infinite alternate");
        int gFirst = Center(Png(css, 1.0)).Green;   // 第 1 迭代中点 0.5
        int gSecond = Center(Png(css, 3.0)).Green;   // 第 2 迭代反向中点
        Assert.True(System.Math.Abs(gFirst - gSecond) < 25, $"alternate 两迭代中点应对称：{gFirst} vs {gSecond}");
    }

    [Fact]
    public void Transform_is_not_faked()
    {
        // transform-only 关键帧：引擎不支持 transform → 必须与「完全不传 animationTime」逐字节一致
        var baseCss = Doc("background:#ff0000");
        var anim = Doc("background:#ff0000;animation:spin 2s linear infinite",
            "@keyframes spin{from{transform:rotate(0)}to{transform:rotate(360deg)}}");
        var noAnim = Renderer.RenderPng("<div id=b>HELLO</div>", baseCss, 400f, null, 1f, null);
        var atMid = Renderer.RenderPng("<div id=b>HELLO</div>", anim, 400f, null, 1f, 1.0);
        Assert.True(SameBytes(noAnim, atMid), "transform 关键帧须被忽略：输出与无动画逐字节一致");
    }

    [Fact]
    public void M9_sample_captures_distinct_deterministic_frames()
    {
        // 验收样例 m9：证明真实资产在不同 animationTime 下取到不同且确定的帧
        var (html, css) = Fixture.ReadSample("m9");
        byte[] At(double? t) => Renderer.RenderPng(html, css, Fixture.PngWidth, Fixture.AssetsDir, Fixture.PngScale, t);
        var rest = At(null);
        var early = At(0.4);
        var mid = At(1.0);
        Assert.False(SameBytes(rest, mid), "m9 在 animationTime=1s 应与静止基线不同（动画取帧生效）");
        Assert.False(SameBytes(early, mid), "m9 不同时刻帧应互不相同");
        Assert.True(SameBytes(mid, At(1.0)), "同一时刻须逐字节确定");
    }
}
