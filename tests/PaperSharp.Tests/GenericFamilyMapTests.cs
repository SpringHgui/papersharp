using PaperSharp.Layout;
using SkiaSharp;

namespace PaperSharp.Tests;

/// <summary>
/// 通用族（<c>serif</c> / <c>sans-serif</c> / <c>monospace</c> / <c>cursive</c> / <c>fantasy</c> /
/// <c>math</c>）落到哪张**物理字面**的标定闸门。
///
/// 这些关键字在 CSS 里不是"某个字体名"，而是"由 UA 决定的一张面"。引擎原先只有 sans-serif /
/// monospace 两条是按浏览器标定的，serif 排在 Times New Roman、cursive/fantasy/math 干脆掉到
/// 表底的 Noto Sans SC 兜底 —— 于是 <c>font-family:serif</c> 的 <c>line-height:normal</c> 行盒
/// 22px vs 浏览器 29px（每行恒差 7px 并沿页面累积），fantasy 整串 @20 窄 16.24px。
///
/// 真值怎么来的：<c>node tools/_genfonts.mjs</c> 走 CDP 的 <c>CSS.getPlatformFontsForNode</c>，
/// 回的是 Blink 真正拿去画字形的那张面（<c>getComputedStyle().fontFamily</c> 只会原样吐回关键字，
/// 拿它当答案等于什么都没量到）。本机（Windows，装了 Noto Sans/Serif SC VF）实测：
/// serif→Noto Serif SC、sans-serif→Noto Sans SC、monospace→NSimSun、cursive→KaiTi、
/// fantasy→Impact、math→Cambria Math、ui-*/emoji→Noto Sans SC、system-ui→Microsoft YaHei UI。
///
/// 探针务必写成**不带引号**的 <c>font-family:serif</c>：加引号就成了一个不存在的自定义族名，
/// Blink 直接落到文档默认字体，量出来的"映射差"全是假信号（本轮就栽过一次，白追一整轮）。
/// </summary>
public class GenericFamilyMapTests
{
    /// <summary>注意**不要 Dispose**：<see cref="FontManager.Get"/> 回的是进程级缓存里的共享字面，
    /// 测试里把它释放掉会把后续所有渲染的字体打空。</summary>
    static string Physical(string family) => FontManager.Get(family, 400).FamilyName;

    static bool Installed(string want) =>
        Physical(want).Equals(want, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 核心断言：四个通用族必须各自落到**不同**的物理字面，而不是全塌进 sans-serif 的兜底面。
    /// 塌兜底是这条通路最安静的失效模式——宽度看着"也就差一点"，行盒却整档跳。
    /// </summary>
    [Fact]
    public void Generics_do_not_collapse_into_the_sans_fallback()
    {
        var sans = Physical("sans-serif");
        foreach (var fam in new[] { "serif", "cursive", "fantasy", "math" })
        {
            var got = Physical(fam);
            Assert.True(got != sans || !Installed(Expected(fam)),
                $"{fam} 塌进了 sans-serif 的兜底面 {sans}；本机该面是可解析的，说明 Fallbacks 里这一条丢了");
        }
    }

    /// <summary>装了就把面钉死；没装（别的机器/CI 换镜像）就退化成上一条的"不许塌兜底"。</summary>
    [Theory]
    [InlineData("serif", "Noto Serif SC")]
    [InlineData("cursive", "KaiTi")]
    [InlineData("fantasy", "Impact")]
    [InlineData("math", "Cambria Math")]
    public void Generic_resolves_to_the_face_Blink_uses(string fam, string want)
    {
        if (!Installed(want)) return;                       // 本机没有这张面：断言不适用，别假红
        Assert.Equal(want, Physical(fam));
    }

    /// <summary>
    /// serif 的行盒是这轮真正咬人的量：Times New Roman 的 normal 行盒 1.1em，Noto Serif SC 是 1.45em。
    /// 走错面不是"字宽差一点"，而是每一行恒差 7px 并沿页面向下累积。
    /// </summary>
    [Fact]
    public void Serif_normal_line_box_is_the_noto_serif_one_not_times()
    {
        if (!Installed("Noto Serif SC")) return;
        float serif = FontManager.NormalLineHeightPx("serif", 400, 20f);
        float times = FontManager.NormalLineHeightPx("Times New Roman", 400, 20f);
        Assert.True(Math.Abs(serif - times) > 2f,
            $"serif 行盒 {serif} 与 Times {times} 已经贴在一起——多半又落回 Times 了（浏览器真值 29）");
    }

    static string Expected(string fam) => fam switch
    {
        "serif" => "Noto Serif SC",
        "cursive" => "KaiTi",
        "fantasy" => "Impact",
        "math" => "Cambria Math",
        _ => fam,
    };
}
