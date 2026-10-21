using SkiaSharp;
using PaperSharp.Pdf;

namespace PaperSharp.Tests;

/// <summary>
/// PDF 字体子集化的保真门：<see cref="TtfSubsetter"/> 裁出的“只用字”字体必须与原字体
/// **逐位同形同宽**（GID 保持、其余表原样），否则 PDF 里就是静默的字形缺失/位移。
/// 判据取最硬的一条：同一字面在两个 face 下栅格化，像素逐字节相等。
/// 注：测试项目经 PDFtoImage 解析到 SkiaSharp 4.x，这里只能用 4.x 还存在的 API 面。
/// </summary>
public class TtfSubsetterTests
{
    static readonly string FontDir = Path.Combine(Fixture.AssetsDir, "fonts");

    static SKTypeface Load(string file) => SKTypeface.FromFile(Path.Combine(FontDir, file))!;

    /// <summary>探测用码位：ASCII + 常用标点 + 步长采样的 CJK + 全角区，覆盖 simple/复合字形与 BMP 外。</summary>
    static IEnumerable<int> ProbeCodepoints()
    {
        for (int cp = 0x21; cp <= 0x7E; cp++) yield return cp;
        foreach (int cp in new[] { 0x3001, 0x3002, 0x4E00, 0x7B2C, 0x8D39, 0x9762, 0xFF0C, 0xFF1A, 0x2116, 0x2460 })
            yield return cp;
        for (int cp = 0x4E00; cp <= 0x9FA5; cp += 37) yield return cp;
    }

    void AssertFaithful(string fontFile)
    {
        using var tf = Load(fontFile);
        var glyphToCp = new Dictionary<int, int>();
        foreach (var cp in ProbeCodepoints())
        {
            int g = tf.GetGlyph(cp);
            if (g != 0) glyphToCp.TryAdd(g, cp);
        }
        Assert.True(glyphToCp.Count > 60, $"探测集太小（{glyphToCp.Count}），测不出东西");

        var bytes = TtfSubsetter.Subset(tf, glyphToCp.Keys)!;
        Assert.NotNull(bytes);
        using var sub = SKTypeface.FromData(SKData.CreateCopy(bytes))!;

        // 家族/码位映射/GID 语义必须原样
        Assert.Equal(tf.FamilyName, sub.FamilyName);
        foreach (var (g, cp) in glyphToCp)
            Assert.Equal(g, sub.GetGlyph(cp));
        // 保 GID 设计里 cmap/hmtx 原封不动，这里反向确认没被“顺手裁掉”
        foreach (var cp in ProbeCodepoints())
            Assert.Equal(tf.GetGlyph(cp), sub.GetGlyph(cp));

        // 逐字面：宽度一致 + 栅格像素一致
        using var fontA = new SKFont(tf, 28) { Edging = SKFontEdging.Antialias };
        using var fontB = new SKFont(sub, 28) { Edging = SKFontEdging.Antialias };
        foreach (var cp in glyphToCp.Values)
        {
            string s = char.ConvertFromUtf32(cp);
            Assert.Equal(fontA.MeasureText(s), fontB.MeasureText(s));
            Assert.Equal(Raster(s, fontA), Raster(s, fontB));
        }
    }

    static byte[] Raster(string s, SKFont font)
    {
        const int W = 48, H = 48;
        var info = new SKImageInfo(W, H, SKColorType.Gray8, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black };
        surface.Canvas.DrawText(s, 8, 36, font, paint);
        using var image = surface.Snapshot();
        using var bmp = new SKBitmap(info);
        Assert.True(image.ReadPixels(bmp.Info, bmp.GetPixels(out _)));
        var dst = new byte[W * H];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), dst, 0, dst.Length);
        return dst;
    }

    [Theory]
    [InlineData("YaheiSub-Regular.ttf")]
    [InlineData("YaheiSub-Bold.ttf")]
    [InlineData("NotoSansSCSub-Regular.ttf")]
    [InlineData("NotoSansSCSub-Bold.ttf")]
    [InlineData("NSimSunSub-Regular.ttf")]
    public void Subset_is_pixel_identical_to_original(string fontFile) => AssertFaithful(fontFile);

    [Fact]
    public void Subset_shrinks_cjk_font_massively()
    {
        using var tf = Load("NotoSansSCSub-Regular.ttf");
        var glyphs = new List<int>();
        for (int cp = 0x4E00; cp <= 0x9FA5; cp += 97) { int g = tf.GetGlyph(cp); if (g != 0) glyphs.Add(g); }
        var bytes = TtfSubsetter.Subset(tf, glyphs)!;
        Assert.NotNull(bytes);
        Assert.True(bytes.Length < 700 * 1024, $"只留 {glyphs.Count} 个字形居然还有 {bytes.Length / 1024} KB");
    }

    /// <summary>端到端：走双趟后的 invoice PDF 必须显著小于“整嵌两份 2.4MB 子集字体”的老体积。</summary>
    [Fact]
    public void RenderPdf_ships_only_used_glyphs()
    {
        var pdf = Fixture.RenderPdf("invoice");
        Assert.True(pdf.Length < 1_400 * 1024,
            $"invoice.pdf {pdf.Length / 1024} KB：仍像整嵌字体（老基线 ~2.4MB），子集路可能静默回退了");
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Contains("/BaseFont", text);
        Assert.Contains("/Type /Page", text);
    }
}
