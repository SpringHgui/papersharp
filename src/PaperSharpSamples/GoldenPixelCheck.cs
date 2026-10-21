using SkiaSharp;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PaperSharp;

namespace PaperSharpSamples;

/// <summary>
/// 一次性迁移取证（`dotnet run -c Release -- goldcheck`）。换 PNG 编码器这种事，「看起来一样」不算证据，
/// 所以要同时回答两个问题：
///   EQUALS — 同一份布局，Skia 编的 PNG 与自研编的 PNG 解码后是否逐字节相同（编码器有没有改变画面）；
///   GOLD   — 当前布局用*旧*编码器渲出来是否仍等于已提交基准（排除「差异其实来自布局漂移」这个混淆项）。
/// 两条都过，才允许把基准重生成为新编码器的字节。跑完把结论记进 PERF_REPORT，本模式即可删除。
/// </summary>
public static class GoldenPixelCheck
{
    public static void Run(string assets, string goldensDir)
    {
        Console.WriteLine("CHK,sample,encoder,kb,pixel-sha,equal-to-skia,golden");
        int bad = 0;
        foreach (var name in Directory.EnumerateFiles(assets, "*.html").Select(Path.GetFileNameWithoutExtension)
                     .Where(n => n is { Length: > 0 } && !n!.StartsWith('_')).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var html = File.ReadAllText(Path.Combine(assets, name + ".html"));
            var cssPath = Path.Combine(assets, name + ".css");
            var css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";

            Renderer.UseFastPng = false;
            var skia = Renderer.RenderPng(html, css, 794, baseDir: assets, scale: 2f);      // 与 Fixture 同参数
            Renderer.UseFastPng = true;
            var fast = Renderer.RenderPng(html, css, 794, baseDir: assets, scale: 2f);

            string sa = PixelSha(skia), sb = PixelSha(fast);
            bool same = sa == sb;
            if (!same) bad++;

            var oldPath = Path.Combine(goldensDir, "png", name + ".png");
            string golden = File.Exists(oldPath) ? (PixelSha(File.ReadAllBytes(oldPath)) == sa ? "match" : "DRIFT") : "none";
            Console.WriteLine("CHK," + name + ",skia," + (skia.Length / 1024) + "," + sa[..12] + ",ref," + golden);
            Console.WriteLine("CHK," + name + ",fast," + (fast.Length / 1024) + "," + sb[..12] + "," + (same ? "Y" : "N") + "," + golden);
        }
        Console.WriteLine(bad == 0 ? "CHK,ALL,EQUALS,0-diff" : "CHK,ALL,EQUALS," + bad + "-diff");
    }

    /// <summary>解码成 RGBA8888 后取 SHA——比文件字节比的是「画面上有没有变」。</summary>
    static string PixelSha(byte[] png)
    {
        using var img = SKImage.FromEncodedData(png) ?? throw new InvalidDataException("PNG 解不开");
        int w = img.Width, h = img.Height;
        var dst = new byte[w * h * 4];
        var g = GCHandle.Alloc(dst, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            if (!img.ReadPixels(info, g.AddrOfPinnedObject(), w * 4)) throw new InvalidDataException("取像素失败");
        }
        finally { g.Free(); }
        return Convert.ToHexString(SHA256.HashData(dst));
    }
}
