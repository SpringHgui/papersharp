using SkiaSharp;
using PaperSharp.Imaging;

namespace PaperSharp.Tests;

/// <summary>
/// 自研 PNG 编码器的正确性守卫。口径不是「看起来一样」，而是
/// **解码后的 RGBA 与 Skia 自己编的 PNG 解码结果逐字节相同**——编码器换实现最容易悄悄错在
/// 多块拼接的 deflate 流、跨块的 Up 参考行、adler 合并这三处，任何一种都只会让画面某一块变花，
/// 肉眼扫一遍未必发现，但像素比对跑不掉。
/// </summary>
public class PngEncoderTests
{
    /// <summary>造一张有结构的图：横向渐变 + 几条实色带 + 半透明区，保证滤波与压缩两条路都被踩到。</summary>
    static SKImage Make(int w, int h)
    {
        var info = new SKImageInfo(w, h);      // 与 Renderer.RasterLayout 同一口径（平台默认 N32/Premul）
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(255, 255, 255, 255));
        using var paint = new SKPaint { Color = new SKColor(200, 30, 30, 255) };
        canvas.DrawRect(0, h / 3, w, h / 6, paint);
        using var paint2 = new SKPaint { Color = new SKColor(30, 30, 200, 128) };
        canvas.DrawRect(w / 4, h / 2, w / 2, h / 4, paint2);
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(w, 0),
            new[] { new SKColor(0, 0, 0, 255), new SKColor(255, 255, 255, 255) }, null, SKShaderTileMode.Clamp);
        using var paint3 = new SKPaint { Shader = shader };
        canvas.DrawRect(0, h - 8, w, 8, paint3);
        return surface.Snapshot();
    }

    static byte[] Rgba(SKImage img)
    {
        var dst = new byte[img.Width * img.Height * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(dst, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            Assert.True(img.ReadPixels(info, handle.AddrOfPinnedObject(), img.Width * 4));
        }
        finally { handle.Free(); }
        return dst;
    }

    static void AssertSamePixels(SKImage source, byte[] png)
    {
        using var decoded = SKImage.FromEncodedData(png);
        Assert.NotNull(decoded);
        Assert.Equal(source.Width, decoded!.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.True(Rgba(source).AsSpan().SequenceEqual(Rgba(decoded)), "解码像素与源位图不一致");
    }

    [Theory]
    [InlineData(8, 8)]                       // 单块
    [InlineData(64, 4096)]                   // 多块（4096/64 = 64 块）
    [InlineData(37, 1000)]                   // 奇数宽 + 需要均摊余数的行数
    public void Roundtrip_is_lossless(int w, int h)
    {
        using var img = Make(w, h);
        foreach (var filter in new[] { PngFilter.None, PngFilter.Up })
            foreach (var level in new[] { System.IO.Compression.CompressionLevel.Fastest,
                                          System.IO.Compression.CompressionLevel.Optimal,
                                          System.IO.Compression.CompressionLevel.SmallestSize })
                foreach (var chunks in new[] { 1, 3, 0 })
                    AssertSamePixels(img, PngEncoder.Encode(img, filter, level, chunks));
    }

    /// <summary>
    /// 为什么先按 stride 取到 raw、再另写一份带滤波标记的 buf：实测把 Skia 的 rowBytes 设成
    /// pitch（= stride+1，想让每行数据前正好留下标记字节）会让 ReadPixels 直接返回 false，
    /// 缓冲区保持全零 → 整幅透明。这条钉住「不能省掉第二次写」的事实依据。
    /// </summary>
    [Fact]
    public void Skia_readPixels_needs_exact_row_stride()
    {
        using var img = Make(16, 8);
        var stride = img.Width * 4;
        var info = new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        var exact = new byte[stride * img.Height];
        var eh = System.Runtime.InteropServices.GCHandle.Alloc(exact, System.Runtime.InteropServices.GCHandleType.Pinned);
        bool okExact;
        try { okExact = img.ReadPixels(info, eh.AddrOfPinnedObject(), stride); }
        finally { eh.Free(); }

        var pitched = new byte[(stride + 1) * img.Height];
        var ph = System.Runtime.InteropServices.GCHandle.Alloc(pitched, System.Runtime.InteropServices.GCHandleType.Pinned);
        bool okPitched;
        try { okPitched = img.ReadPixels(info, ph.AddrOfPinnedObject() + 1, stride + 1); }
        finally { ph.Free(); }

        Assert.True(okExact, "rowBytes=stride 应当可用（这是本编码器的取像素方式）");
        Assert.False(okPitched, "rowBytes=stride+1 的实测行为已变，需回头确认能否省掉 raw 缓冲");
    }

    [Theory]
    [InlineData(64, 4096)]
    [InlineData(37, 1000)]
    public void Matches_skia_encoder_pixel_for_pixel(int w, int h)
    {
        using var img = Make(w, h);
        using var skia = SKImage.FromEncodedData(img.Encode(SKEncodedImageFormat.Png, 100)!.ToArray());
        Assert.NotNull(skia);
        var reference = Rgba(skia!);
        foreach (var filter in new[] { PngFilter.None, PngFilter.Up })
            Assert.True(reference.AsSpan().SequenceEqual(
                Rgba(SKImage.FromEncodedData(PngEncoder.Encode(img, filter))!)),
                $"{filter} 与 Skia 输出解码后不一致");
    }

    /// <summary>并行分块不得影响结果：单块与多块的字节流可以不同（压缩历史被切断），像素必须相同。</summary>
    [Fact]
    public void Chunking_does_not_change_pixels()
    {
        using var img = Make(64, 4096);
        var one = PngEncoder.Encode(img, PngFilter.Up, System.IO.Compression.CompressionLevel.Optimal, 1);
        var many = PngEncoder.Encode(img, PngFilter.Up, System.IO.Compression.CompressionLevel.Optimal, 16);
        Assert.True(Rgba(SKImage.FromEncodedData(one)!)
            .AsSpan().SequenceEqual(Rgba(SKImage.FromEncodedData(many)!)));
    }
}
