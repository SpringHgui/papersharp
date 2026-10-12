using PaperSharp;
using PaperSharp.Layout;
using Xunit;

namespace PaperSharp.Tests;

/// <summary>
/// 打包子集字体的来源门（PERF_REPORT §12.6 那条宿主依赖 bug 的回归钉）。
///
/// 修前的行为：字体按 <c>AppContext.BaseDirectory/assets/fonts</c> 找——那是**入口程序集**旁边，
/// 库说了不算，而且字体从没进过 NuGet 包。于是消费方静默退回系统同名字体：
/// 同一份 HTML 体积差 2.9~12.7 倍（m1 4,002,249 B → 315,539 B），
/// 而「PDF 体积 + 跨平台一致」正是这批子集存在的唯一理由；更糟的是金标只在
/// 「文件在场」的 testhost 里录过，消费方实际走的那条分支一条测试都没有。
///
/// 修后：字体嵌进 dll，解析顺序 显式目录 → 内嵌 → 老路径。
///
/// ⚠️ 本组按**只嵌 Noto** 的许可口径钉（1.0.0 决定）：SIL OFL 允许随包再分发，
/// 而 YaheiSub/NSimSunSub 是微软雅黑与中易宋体的派生二进制，不随 MIT 包分发。
/// 所以「各字面来源一致」在这里**不再**是判据——仓内 testhost 里 Noto 走内嵌、
/// 雅黑走老路径，混用是预期状态；真正要钉死的是「dll 里只有 OFL 那两份」。
/// </summary>
public class BundledFontTests
{
    const string NotoRegular = "PaperSharp.Fonts.NotoSansSCSub-Regular.ttf";
    const string NotoBold = "PaperSharp.Fonts.NotoSansSCSub-Bold.ttf";

    [Fact]
    public void Only_ofl_licensed_faces_are_embedded()
    {
        // 这条是许可决定的钉子：谁哪天手滑把雅黑/宋体塞进 EmbeddedResource，这里先红。
        var names = typeof(Renderer).Assembly.GetManifestResourceNames();
        Assert.Contains(NotoRegular, names);
        Assert.Contains(NotoBold, names);
        Assert.DoesNotContain(names, n => n.Contains("Yahei", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("NSimSun", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Embedded_bytes_are_loadable_faces()
    {
        // 只证明「包里有、且 Skia 认」——渲染路径另有金标门管。
        foreach (var name in new[] { NotoRegular, NotoBold })
        {
            using var s = typeof(Renderer).Assembly.GetManifestResourceStream(name);
            Assert.NotNull(s);
            using var ms = new MemoryStream();
            s!.CopyTo(ms);
            using var data = SkiaSharp.SKData.CreateCopy(ms.ToArray());
            using var tf = SkiaSharp.SKTypeface.FromData(data);
            Assert.NotNull(tf);
            Assert.False(string.IsNullOrWhiteSpace(tf.FamilyName));
        }
    }

    [Fact]
    public void Prkt_kern_table_survives_subset_loading()
    {
        // 嵌资源改造最容易踩的坑：PRKT 是项目自定义表，原先按**文件路径**读；
        // 若内嵌加载偷懒退回 KernTable.AttachFromTables（只认标准 GPOS），字距会**静默丢失**，
        // 度量退回 hmtx 口径——正撞在 P0-6 / P0-7 / P1-16 那批字距与重影工作的地基上。
        var tf = FontManager.SubsetIfCovers('合', 400);
        Assert.NotNull(tf);
        Assert.True(KernTable.HasTable(tf!),
            "打包子集的 PRKT 字距表没挂上：内嵌加载必须走 KernTable.AttachFromBytes，不能用 AttachFromTables 顶替");
    }

    [Fact]
    public void Subset_faces_resolve_without_silent_degradation()
    {
        // FontSource 在首次用到子集字体时定格，取不到就是 "none"（正文静默退回系统字体）。
        Assert.NotNull(FontManager.SubsetIfCovers('合', 400));
        Assert.NotNull(FontManager.SubsetIfCovers('合', 700));
        Assert.NotEqual("none", FontManager.FontSource);
    }

    [Fact]
    public void Pdf_does_not_depend_on_fonts_sitting_next_to_entry_assembly()
    {
        // 消费方视角的验收：嵌的必须是打包子集。§13.3 实测 invoice 单页
        // 子集 47,519 B / 退回系统字体 120,258 B，取 100 KB 作分界。
        var (html, css) = Fixture.ReadSample("invoice");
        var pdf = Renderer.RenderPdf(html, css, baseDir: Fixture.AssetsDir);
        Assert.True(pdf.Length < 100 * 1024,
            $"invoice 产物 {pdf.Length:N0} B，超出打包子集的量级——疑似退回系统字体");
    }

    /// <summary>
    /// 漂移门：<c>subset-font.py</c> 的产出与库工程里被嵌进 dll 的那份必须逐字节相同。
    /// 嵌资源等于开了第二个副本，于是有了「被验证的字体 ≠ 出厂字体」的静默漂移风险——
    /// 现实已在眼前：在途改动把 NotoSansSCSub-Regular 从 2,467,864 B 重烤成 2,331,520 B，
    /// 而库里的副本还是旧的。脚本末尾的 <c>sync_to_library()</c> 是正解，
    /// 这条测试保证忘了同步时是红的、而不是安静的。
    /// 从包内跑测试时仓库目录不存在 → 跳过（不算失败）。
    /// </summary>
    [Theory]
    [InlineData("NotoSansSCSub-Regular.ttf")]
    [InlineData("NotoSansSCSub-Bold.ttf")]
    [InlineData("OFL.txt")]
    public void Library_copy_matches_font_baking_output(string file)
    {
        var repo = FindRepoRoot();
        if (repo is null) return;                       // 包内环境，无源码可比
        var baked = Path.Combine(repo, "src", "PaperSharpSamples", "assets", "fonts", file);
        var shipped = Path.Combine(repo, "src", "PaperSharp", "Fonts", file);
        if (!File.Exists(baked) || !File.Exists(shipped)) return;

        var a = File.ReadAllBytes(baked);
        var b = File.ReadAllBytes(shipped);
        Assert.True(a.Length == b.Length && System.Security.Cryptography.CryptographicOperations
                .FixedTimeEquals(a, b),
            $"{file}：subset-font.py 产出 {a.Length:N0} B 与库内副本 {b.Length:N0} B 不一致 —— " +
            "重跑 `python src/PaperSharpSamples/tools/subset-font.py`（末尾会自动同步）后提交");
    }

    /// <summary>不可再分发的字体不该躺在库工程目录里等着被哪天误嵌。</summary>
    [Theory]
    [InlineData("YaheiSub-Regular.ttf")]
    [InlineData("YaheiSub-Bold.ttf")]
    [InlineData("NSimSunSub-Regular.ttf")]
    public void Non_redistributable_fonts_stay_out_of_the_library_project(string file)
    {
        var repo = FindRepoRoot();
        if (repo is null) return;
        Assert.False(File.Exists(Path.Combine(repo, "src", "PaperSharp", "Fonts", file)),
            $"{file} 在 src/PaperSharp/Fonts/ 里：它嵌在 csproj 旁边，随时可能被再分发。" +
            "仓内需要这份字体的话放 src/PaperSharpSamples/assets/fonts/（老路径，样张与金标仍能用）。");
    }

    static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaperSharp.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
