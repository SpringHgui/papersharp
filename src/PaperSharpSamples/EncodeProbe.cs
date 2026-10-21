using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using SkiaSharp;
using PaperSharp;
using PaperSharp.Css;
using PaperSharp.Imaging;
using PaperSharp.Layout;
using PaperSharp.Paint;

namespace PaperSharpSamples;

/// <summary>
/// 编码阶段专项探针（`dotnet run -c Release -- pgenc [iters] [样例名…]`）。
///
/// pgstage 实测「位图 → 字节」占 Playground 端到端 60–90%，所以这一段单独拎出来量：
/// Skia 的 PNG / JPEG / WebP 各档 vs 引擎自研 PngEncoder 的（滤波 × deflate 档位 × 并行度）组合。
/// 每档都做「解回来与 Skia 的 PNG 解码结果逐字节比对」——只看体积会骗人。
/// </summary>
public static class EncodeProbe
{
    const float W = 1284f, S = 2f;

    public static void Run(string assets, int iters, string[]? only = null)
    {
        var ci = CultureInfo.InvariantCulture;
        var names = Directory.EnumerateFiles(assets, "*.html").Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is { Length: > 0 } && !n!.StartsWith('_') && (only == null || only.Contains(n!)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        Console.WriteLine("ENC,sample,codec,params,ms,kb,same-pixels-as-skia");
        foreach (var n in names!)
        {
            var html = File.ReadAllText(Path.Combine(assets, n + ".html"));
            var css = File.Exists(Path.Combine(assets, n + ".css")) ? File.ReadAllText(Path.Combine(assets, n + ".css")) : "";

            float sc = S;
            using var scope = Prepare(css, assets);
            var spec = AtPageParser.Parse(css, 595.28f, 841.89f);
            var computed = StyleResolver.BuildDocument(html, css, null, false, W, domPath: true);
            var body = LayoutBuilder.ToBoxTree(computed);
            LayoutBuilder.Measure(body, Math.Max(1, W - spec.MarginLeft - spec.MarginRight));
            LayoutBuilder.Position(body, spec.MarginLeft, spec.MarginTop);
            int pw = (int)Math.Round(W * sc), ph = Math.Max(100, (int)Math.Ceiling((body.TotalHeight + spec.MarginTop + spec.MarginBottom) * sc));
            using var surface = SKSurface.Create(new SKImageInfo(pw, ph));
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.Scale(sc);
            Painter.Paint(surface.Canvas, body, surface);
            using var img = surface.Snapshot();

            byte[] skiaPng = img.Encode(SKEncodedImageFormat.Png, 100)!.ToArray();
            foreach (int q in new[] { 100, 50, 10 })
                Row(ci, n, "skia-png", "q=" + q, () => (int)img.Encode(SKEncodedImageFormat.Png, q)!.Size, iters, "ref");
            foreach (int q in new[] { 95, 90 })
                Row(ci, n, "skia-jpeg", "q=" + q, () => (int)img.Encode(SKEncodedImageFormat.Jpeg, q)!.Size, iters, "lossy");

            foreach (var (f, lvl, chunks) in Configs())
            {
                string tag = $"{f} {Lvl(lvl)} t{chunks}";
                byte[] Make() => PngEncoder.Encode(img, f, lvl, chunks);
                Row(ci, n, "own-png", tag, () => Make().Length, iters, SameAs(skiaPng, Make()) ? "Y" : "N");
            }
        }
    }

    static IEnumerable<(PngFilter, CompressionLevel, int)> Configs()
    {
        int cores = Math.Max(2, Environment.ProcessorCount);
        foreach (var f in new[] { PngFilter.Up, PngFilter.None })
            foreach (var l in new[] { CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
            {
                yield return (f, l, cores);
                if (f == PngFilter.Up) yield return (f, l, 1);       // 单线程档：看并行到底赚了多少
            }
    }

    static string Lvl(CompressionLevel l) => l switch
    {
        CompressionLevel.Fastest => "fast",
        CompressionLevel.Optimal => "opt",
        _ => "mini",
    };

    static void Row(CultureInfo ci, string n, string codec, string p, Func<int> bytes, int iters, string same)
    {
        bytes();                                        // 预热（JIT + 首次分配）
        var t = Time(bytes, iters);
        Console.WriteLine("ENC," + n + "," + codec + "," + p + "," + t.med.ToString("0.0", ci) + "," +
                          (t.bytes / 1024.0).ToString("0", ci) + "," + same);
    }

    /// <summary>与 Skia 的 PNG 解码结果逐字节比对（RGBA）：证明「换编码器」不动一个像素。</summary>
    static bool SameAs(byte[] skiaPng, byte[] mine)
    {
        try
        {
            using var a = SKImage.FromEncodedData(skiaPng);
            using var b = SKImage.FromEncodedData(mine);
            if (a == null || b == null || a.Width != b.Width || a.Height != b.Height) return false;
            var pa = new byte[a.Width * a.Height * 4];
            var pb = new byte[b.Width * b.Height * 4];
            return Rgba(a, pa) && Rgba(b, pb) && pa.AsSpan().SequenceEqual(pb.AsSpan());
        }
        catch { return false; }
    }

    static bool Rgba(SKImage img, byte[] dst)
    {
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(dst, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            return img.ReadPixels(info, handle.AddrOfPinnedObject(), img.Width * 4);
        }
        finally { handle.Free(); }
    }

    static (double med, int bytes) Time(Func<int> f, int iters)
    {
        var t = new double[iters]; int last = 0;
        for (int i = 0; i < iters; i++) { var sw = Stopwatch.StartNew(); last = f(); t[i] = sw.Elapsed.TotalMilliseconds; }
        Array.Sort(t);
        return (t[iters / 2], last);
    }

    static RenderScope Prepare(string css, string assets)
    {
        var scope = RenderScope.Begin(assets);
        FontFaceParser.ParseAndRegister(css);
        return scope;
    }
}
