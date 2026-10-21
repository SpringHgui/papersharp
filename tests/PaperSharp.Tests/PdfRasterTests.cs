using SkiaSharp;
using PDFtoImage;

namespace PaperSharp.Tests;

/// <summary>
/// 像素层（贵的一半）：PDF 经 PDFium 栅格化后与基准 PNG 做像素 diff。
/// 与 PngGoldenTests 的区别——那条守"我们自己画的对不对"，这条守"写出去的 PDF
/// 被独立渲染器读回来画得对不对"（嵌字体/内容流/坐标全链路）。
/// 慢（每样例一次 PDF 解析+光栅），CI 里可用 Category=Slow 过滤：
///   dotnet test --filter "Category!=Slow"
/// 重生成：UPDATE_GOLDENS=1 dotnet test --filter PdfRasterTests
/// </summary>
[Trait("Category", "Slow")]
public class PdfRasterTests
{
    const double MaxDiffRatio = 0.001;   // 允许 0.1% 像素抖动（PDFium 解码噪声）
    const int ChannelTolerance = 16;     // 单通道差 ≤16 视为相同

    [Theory]
    [MemberData(nameof(FixtureSamples))]
    public void Pdf_page1_rasterization_matches_golden(string name)
    {
        var pdf = Fixture.RenderPdf(name);
        byte[] rendered;
        using (var ms = new MemoryStream(pdf))
        {
            var bmp = Conversion.ToImage(ms, 0); // Index 0 = 第一页
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            rendered = data.ToArray();
        }

        Assert.True(Fixture.MatchGolden($"raster/{name}_p1.png", rendered, DiffWithinTolerance),
            $"{name} 第 1 页栅格 diff 超阈值 → 生成 actual/golden 并排图确认后再 UPDATE_GOLDENS");
    }

    static bool DiffWithinTolerance(byte[] actualPng, byte[] goldenPng)
    {
        using var a = SKBitmap.Decode(actualPng);
        using var g = SKBitmap.Decode(goldenPng);
        if (a is null || g is null) return false;
        if (a.Width != g.Width || a.Height != g.Height) return false;
        if (a.ColorType != g.ColorType || a.RowBytes != g.RowBytes) return false;

        int w = a.Width, h = a.Height, rowBytes = a.RowBytes, bpp = a.BytesPerPixel;
        var pa = new byte[rowBytes * h];
        var pg = new byte[rowBytes * h];
        System.Runtime.InteropServices.Marshal.Copy(a.GetPixels(), pa, 0, pa.Length);
        System.Runtime.InteropServices.Marshal.Copy(g.GetPixels(), pg, 0, pg.Length);

        long diff = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < w * bpp; x += bpp)
            {
                int i = row + x;
                if (Math.Abs(pa[i] - pg[i]) > ChannelTolerance ||
                    Math.Abs(pa[i + 1] - pg[i + 1]) > ChannelTolerance ||
                    Math.Abs(pa[i + 2] - pg[i + 2]) > ChannelTolerance)
                    diff++;
            }
        }
        return (double)diff / (w * h) <= MaxDiffRatio;
    }

    public static TheoryData<string> FixtureSamples => new(Fixture.Samples);
}
