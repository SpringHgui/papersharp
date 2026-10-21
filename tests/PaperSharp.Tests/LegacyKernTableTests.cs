using PaperSharp.Layout;
using SkiaSharp;

namespace PaperSharp.Tests;

/// <summary>
/// 系统字面的字距来源：**GPOS 与 legacy <c>kern</c> 表按字面整体二选一**，以及读这两张表所依赖的
/// cmap 反向表。
///
/// 为什么要有这条通路：Arial / Times New Roman 这批 Windows 系统字面的字距**全**躲在 legacy
/// <c>kern</c> 表里（它们的 GPOS 里没有可用的 pair-pos）。<see cref="KernTable"/> 原先只解析 GPOS，
/// 于是这些字面整张表为 null、度量退回纯 hmtx —— 每一行拉丁从第一个配对起就一路往右偏，
/// 表现就是 Playground 叠加视图里那条越往右越宽的重影。1080 用例的真值矩阵（9 字面 × 3 字号 ×
/// 40 串，含混排 run）实测：接上之前 Arial 66/120 坏、Times New Roman 75/120 坏；接上之后双双 0/120。
///
/// 两个坑，都留了断言：
/// ① <b>子表头字节序</b>按 fontTools 的 <c>table__k_e_r_n.decompile</c> 为准（PRKT 就是它烘的）：
///    <c>u16 version / u16 length / u8 format / u8 coverage</c> —— format 在前，与 OpenType 文档里
///    coverage 在前的写法相反。照文档读会把 arial 那 909 对当成 format 1 状态表整张丢掉。
/// ② <b>cmap format 12 的表头是 16 字节</b>（format/reserved/length/<b>language</b>/numGroups）。
///    少读那个 u32 language 会把 length 当组数、组表起点少挪 4 字节，凭空造出几千条假的
///    字形→码位 映射；<see cref="KernTable"/> 里 format 12 先跑且先到先得，format 4 那份正确表
///    就被盖掉——Arial 的 gid 89 本该是 'v'，假表里成了 '9'，于是 (A,v) 这一对登记到查不到的键上。
///    当时 Arial 只有 A+V / A+W 这类侥幸命中的对活下来，其余 900 对静默丢失。
///
/// 为什么是「整体二选一」而不是像 <c>bake_kern</c> 那样逐对合并：浏览器（HarfBuzz / DirectWrite）在
/// 字面自带 GPOS 'kern' 时就不再吃 legacy kern 表。逐对补会把只在 legacy 里出现的对塞进来 ——
/// 实测 Segoe UI 的 r+y 因此从对得上变成「ry」@20 偏 0.79px，真值矩阵里 Segoe UI 从 0/120 坏成 8/120。
/// 打包子集不受这套影响：它们的 legacy kern 早在构建期被并进 PRKT，PRKT 命中就轮不到这里。
///
/// 断言一律以**字体单位**记账（把 size 传成 unitsPerEm，<c>Pair</c> 就正好回吐整数 units），
/// 免得字号与 upm 的换算把误差混进基准里。
/// </summary>
public class LegacyKernTableTests
{
    const float SegoeUpm = 2048f;

    /// <summary>取字面并登记字距表。测试里只碰 <see cref="SKTypeface.FamilyName"/>，
    /// 不碰 SKPaint/SKFont 那些在 2.88 与 4.x 之间搬过家的成员（见 <c>KernPaintParityTests</c> 的说明）。</summary>
    static SKTypeface Face(string family)
    {
        var tf = FontManager.Get(family, 400);
        KernTable.AttachFromTables(tf);      // 幂等：已登记（含负缓存）时直接返回
        return tf;
    }

    /// <summary>闸门不许空跑：字面没解析到预期的物理字体时直接判失败，别悄悄跳过一条真回归。</summary>
    static KernTable TableOf(string family, string physical)
    {
        var tf = Face(family);
        Assert.True(tf.FamilyName.Equals(physical, StringComparison.OrdinalIgnoreCase),
            $"本机把 {family} 解析成了 {tf.FamilyName}，字距基准不适用（CI 是 windows-latest，装的是真货）");
        return KernTable.For(tf)
            ?? throw new InvalidOperationException($"{physical} 没登记上字距表：legacy kern 通路断了");
    }

    static void AssertPair(KernTable kt, char a, char b, int units, string note = "")
        => Assert.True(Math.Abs(kt.Pair(a, b, SegoeUpm) - units) < 0.5f,
            $"{a}{b} 字距 = {kt.Pair(a, b, SegoeUpm):0.##} units，应为 {units} {note}");

    /// <summary>① Arial：字距只来自 legacy kern 表，逐对等于表里的字体单位值（同时反查过 Chrome 实测）。</summary>
    [Fact]
    public void Arial_kerns_from_the_legacy_kern_table()
    {
        var kt = TableOf("Arial", "Arial");
        AssertPair(kt, 'T', 'e', -227, "（矩阵里 Arial@20「Te」曾偏宽 2.21px）");
        AssertPair(kt, 'T', 'a', -227);
        AssertPair(kt, 'T', 'r', -76);
        AssertPair(kt, 'A', 'V', -152);
        AssertPair(kt, 'Y', 'e', -188);
        AssertPair(kt, 'W', 'o', -37);
        AssertPair(kt, 'L', 'y', -76);
        // Arial 没配 r+y：0 是正确值，不是查不到
        AssertPair(kt, 'r', 'y', 0);
    }

    /// <summary>② Times New Roman 同理，且量级更大（矩阵实测「TAVoWyA!」@20 曾偏宽 7.96px）。</summary>
    [Fact]
    public void Times_New_Roman_kerns_from_the_legacy_kern_table()
    {
        var kt = TableOf("Times New Roman", "Times New Roman");
        AssertPair(kt, 'T', 'e', -143);
        AssertPair(kt, 'A', 'V', -264);
        AssertPair(kt, 'V', 'o', -264);
        AssertPair(kt, 'Y', 'e', -205);
    }

    /// <summary>③ 坑 ② 的定点回归：cmap format 12 的 language 字段少读 4 字节时，
    /// Arial 的 gid 89 会被假表 claim 成 '9'，(A,v) 就登记到查不到的键上——表现为「整张表只剩几对」。</summary>
    [Fact]
    public void Format12_cmap_header_does_not_shadow_the_BMP()
    {
        var kt = TableOf("Arial", "Arial");
        AssertPair(kt, 'A', 'v', -37, "（'v' 的字形必须落回 0x76，被 format 12 假映射抢走时这一对查不到）");
        AssertPair(kt, 'A', 'w', -37);
        AssertPair(kt, 'A', 'y', -37);
        // 表若被假映射大面积抢走，非零对的数量会塌到个位数；Arial 实际 908 对非零。
        int hits = 0;
        foreach (var a in "ATVWLY")
            foreach (var b in "aeowyv")
                if (kt.Pair(a, b, SegoeUpm) != 0f) hits++;
        Assert.True(hits >= 20, $"36 个常用配对里只命中 {hits} 对，字距表又被 cmap 反向表抢过位了");
    }

    /// <summary>④ 整体二选一：Segoe UI 有自己的 GPOS 配对，就绝不能再吃它那份 legacy kern。
    /// 逐对合并时 Segoe UI 的 r+y 会被补进来，「ry」@20 偏 0.79px、矩阵从 0/120 坏成 8/120。</summary>
    [Fact]
    public void Gpos_wins_wholesale_and_a_face_is_never_kerned_twice()
    {
        var kt = TableOf("Segoe UI", "Segoe UI");
        AssertPair(kt, 'T', 'e', -200, "（这条来自 GPOS，接不上就说明字面整体退回了 hmtx）");
        AssertPair(kt, 'A', 'V', -140);
        AssertPair(kt, 'r', 'y', 0, "（Segoe UI 的 legacy kern 里有这一对，GPOS 没有：出现即两表被合并）");
        AssertPair(kt, 'v', 'a', 0);
    }
}
