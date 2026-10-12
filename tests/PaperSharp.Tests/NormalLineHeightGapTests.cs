using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// <c>line-height: normal</c> 的行盒高必须含字体建议行距 <c>hhea.lineGap</c>，并且是
/// **三个量各自取整后相加**，不是「(asc+desc+gap) × 字号」也不是「asc+desc 再加一次取整」。
///
/// 原先按「Blink 的 normal 行盒 = 取整后的 ascent+descent」把 lineGap 剔掉了。这个口径对
/// <c>lineGap = 0</c> 的面完全看不出来——雅黑、Noto Sans SC 子集、Segoe UI、Georgia 全是 0——
/// 所以一直没暴露；而带行距的面就整档偏：
///   NSimSun / KaiTi   hhea 220/−36 gap 36（upm 256）  → 20px 行盒 20，浏览器 23（差 3px）
///   Arial             hhea 1854/−434 gap 67（upm 2048）→ 20px 行盒 22，浏览器 23
///   Times New Roman   hhea 1825/−443 gap 87（upm 2048）→ 20px 行盒 22，浏览器 23
/// Arial / Times 是正文里最常见的两族，每行恒差 1~3px 会沿页面一路累积成整页漂移。
///
/// 口径怎么定的：8 张面（monospace / cursive / math / serif / sans-serif / NSimSun / KaiTi /
/// Times New Roman）× 10 个字号（10.5…32）逐个量浏览器行盒，再拿五组候选公式去拟合
/// （usWin / hhea × 含不含 gap × sTypo+typoGap）。只有「usWin(hhea) asc + desc + hhea lineGap、
/// 各自 floor(x+0.5)」6/6 张面全部命中；「不含 gap」在 Arial/Times/NSimSun/KaiTi 上 10 个点全偏；
/// 「sTypo + sTypoLineGap」只在 bit7(USE_TYPO_METRICS)=0 的这批面上与 hhea 分支巧合，
/// Arial/Times/Segoe/Georgia 一律不中——所以取 hhea 那支。
/// 复量脚本：<c>node tools/boxdiff.mjs _probe-lh --probe "$(cat .scratch/.../probe-lh3.js)"</c>，
/// 常驻样例 <c>assets/_probe-lh.html</c>（80 个纯拉丁盒修完后逐位吻合，0 漂移）。
///
/// 断言用打包的 NSimSun 子集（gap 36/256）当夹具——它随仓库分发，不依赖本机装了哪些字体。
/// </summary>
public class NormalLineHeightGapTests
{
    // 浏览器实测：font-family:monospace（= NSimSun）单字符 "H"、line-height:normal 的行盒高
    static readonly (float Size, int Px)[] Curve =
    {
        (10.5f, 11), (11f, 13), (12f, 14), (13f, 15), (14f, 16),
        (16f, 18), (18f, 21), (20f, 23), (24f, 27), (32f, 38),
    };

    public static IEnumerable<object[]> CurveCases() => Curve.Select(c => new object[] { c.Size, c.Px });

    /// <summary>闸门不许空跑：剔掉 lineGap 时这条曲线必须对不上，否则本测试什么都没钉住。</summary>
    [Theory]
    [MemberData(nameof(CurveCases))]
    public void Monospace_normal_line_box_follows_the_browser_curve(float size, int expected)
    {
        float got = FontManager.NormalLineHeightPx("monospace", 400, size);
        Assert.True(Math.Abs(got - expected) < 0.01f,
            $"monospace @{size}px 行盒 {got}，浏览器真值 {expected}——lineGap 又没算进去，或取整口径漂了");
    }

    /// <summary>
    /// 反面对照：lineGap = 0 的面（打包雅黑）加了这一项也不该动，
    /// 证明改动只影响真正申报了行距的字面，不是一刀切抬高。
    /// </summary>
    [Theory]
    [InlineData(10.5f)]
    [InlineData(14f)]
    [InlineData(20f)]
    [InlineData(32f)]
    public void Zero_gap_faces_are_untouched(float size)
    {
        var (a, d) = FontManager.TextExtents("Microsoft YaHei", 400, size);
        Assert.Equal(a + d, FontManager.NormalLineHeightPx("Microsoft YaHei", 400, size), 3);
    }

    /// <summary>
    /// 取整口径：三个量各自 floor(x+.5) 再相加。10.5px 的宋体是唯一的判据点——
    /// 先加后取整会得 12（9.02+1.48+1.48 = 11.98 → 12），浏览器是 11（9 + 1 + 1）。
    /// </summary>
    [Fact]
    public void Each_metric_rounds_independently()
    {
        Assert.Equal(11f, FontManager.NormalLineHeightPx("monospace", 400, 10.5f));
    }
}
