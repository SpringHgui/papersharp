using PaperSharp;
using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// 长度换算与 Chrome 的对账：em 该按谁的字号折、以及 used 值的 1/64 网格。
///
/// 每条期望值都不是推出来的，是无头浏览器对同一份文档的 <c>getBoundingClientRect</c>
/// （<c>assets/_probe-em2.html</c> / <c>_probe-lu.html</c>，<c>node tools/boxdiff.mjs … --tol 0.01</c>）。
/// </summary>
public class LengthParityTests
{
    const float W = 400f;

    static IReadOnlyList<BoxRect> Boxes(string html, string css = "html,body{margin:0}")
        => Renderer.RenderRasterWithBoxes(html, css, W, scale: 1f).Boxes;

    // ── em 的基准：元素自己的 font-size，不是父的 ────────────────────────────────

    /// <summary>
    /// 父 16px、本元素 20px 时，几何属性上的 em 必须按 20 折。
    /// AngleSharp 的 computed 会把 top/left/width/height/border-width 上的 em 按**父**字号折
    /// （Chrome 30 / 引擎旧值 24，最差一条差 20px），所以这些属性一律改用声明值自己算。
    /// </summary>
    [Theory]
    [InlineData("top", 1.5f, 30f)]
    [InlineData("left", 1.5f, 30f)]
    [InlineData("width", 2f, 40f)]
    [InlineData("height", 2f, 40f)]
    [InlineData("border-top", 1f, 20f)]
    [InlineData("margin-top", 1.5f, 30f)]
    [InlineData("padding-top", 1.5f, 30f)]
    public void Em_resolves_against_the_elements_own_font_size(string prop, float mult, float want)
    {
        // 用**简写**：`border-top-width:1em solid` 是非法声明（长写法只收宽度），会被整条丢掉
        string style = prop.StartsWith("border") ? $"{prop}:{mult}em solid #000" : $"{prop}:{mult}em";
        var bs = Boxes($"<div style='font-size:16px'><div class=t style='font-size:20px;position:absolute;{style}'></div></div>");
        var t = bs.Last(x => x.Tag == "div");
        float got = prop switch
        {
            "top" or "margin-top" => t.Y,
            // 边框宽看**盒高**：没设 top，静态位不动，但 1em 的边框会把盒撑起来
            "padding-top" or "border-top" => t.H,
            "left" => t.X,
            "width" => t.W,
            "height" => t.H,
            _ => throw new ArgumentOutOfRangeException(nameof(prop)),
        };
        Assert.Equal(want, got, 3);
    }

    /// <summary>反方向：<c>font-size</c> 自己的 em 必须按**父**字号折（CSS 口径），
    /// 且折完之后本元素的其它 em 再按这个新字号走。三层嵌套一次钉住两头。</summary>
    [Fact]
    public void Font_size_em_uses_parent_but_later_em_uses_own()
    {
        // 16 → font-size:1.5em = 24 → top:1em = 24（若错按 16 折会得 16；错按 16×1.5 再 1.5 会得 36）
        var bs = Boxes("<div style='font-size:16px'><div class=t style='font-size:1.5em;position:absolute;top:1em'></div></div>");
        var t = bs.Last(x => x.Tag == "div");
        Assert.Equal(24f, t.Y, 3);
    }

    // ── used 值折到 Blink 的 1/64 网格（向零截断）──────────────────────────────

    [Theory]
    [InlineData("9.3828125px", 9.375f)]      // ×64 = 600.5 → 600/64（平局走截断，不是四舍五入）
    [InlineData("9.38671875px", 9.375f)]     // ×64 = 600.75 → 600/64
    [InlineData("2.5mm", 9.4375f)]           // 9.448819 → 604/64
    [InlineData("1mm", 3.765625f)]           // 3.779528 → 241/64
    [InlineData("0.5pt", 0.65625f)]          // 0.666667 → 42/64
    [InlineData("1pc", 16f)]
    public void Absolute_lengths_snap_to_the_64th_grid(string authored, float want)
    {
        var bs = Boxes($"<div class=t style='position:absolute;top:{authored};left:0;width:6px;height:6px'></div>");
        Assert.Equal(want, bs.Last(x => x.Tag == "div").Y, 5);
    }

    /// <summary><c>q</c> = ¼ **毫米**。引擎的换算表原来写成 PxPerCm/4（大 10 倍），
    /// 走自己的换算表那条路（padding 的边）实测 Chrome 0.9375 / 引擎 9.4375。
    /// <c>top:1q</c> 另有一层丢：AngleSharp 不认这个单位，computed 退成 auto —— 见台账 P2-11。</summary>
    [Fact]
    public void Quarter_millimeter_unit_snaps_to_60_64ths()
    {
        var bs = Boxes("<div class=t style='width:60px;height:0;padding-top:1q'></div>");
        Assert.Equal(0.9375f, bs.Last(x => x.Tag == "div").H, 5);
    }

    /// <summary>负长度向**零**截断（−600.75 → −600，不是 −601）。Chrome 实测六档负值全是这个方向。</summary>
    [Theory]
    [InlineData("-9.38671875px", -9.375f)]   // ×64 = −600.75
    [InlineData("-9.3984375px", -9.390625f)] // ×64 = −601.5 → −601
    public void Negative_lengths_truncate_toward_zero(string authored, float want)
    {
        var bs = Boxes($"<div style='position:relative;top:20px'><div class=t style='position:absolute;top:{authored};left:0;width:6px;height:6px'></div></div>");
        Assert.Equal(want + 20f, bs.Last(x => x.Tag == "div").Y, 5);
    }

    /// <summary>百分比的**乘积**也落在网格上：Chrome 实测 33.333% of 300.5 → 100.15625（=6410/64）。</summary>
    [Fact]
    public void Percent_results_snap_to_the_grid_too()
    {
        var bs = Boxes("<div style='position:relative;width:300.5px'><div class=t style='width:33.333%;height:4px'></div></div>");
        Assert.Equal(100.15625f, bs.Last(x => x.Tag == "div").W, 5);
    }
}
