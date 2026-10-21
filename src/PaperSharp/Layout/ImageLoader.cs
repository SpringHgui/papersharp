using System.Collections.Concurrent;
using SkiaSharp;

namespace PaperSharp.Layout;

/// <summary>
/// 图像加载与解码缓存。支持：
/// - 相对/绝对文件路径（相对路径基于当前 <see cref="RenderScope.BaseDir"/>，即 HTML 所在目录）
/// - data:image/...;base64,... 内联
/// 网络 URL 默认不抓（保证离线 + 确定性输出）；如需可在此扩展。
/// 线程安全：缓存 key 编入 BaseDir（不同目录同名 src 不再串图），容器用 ConcurrentDictionary；
/// 并发下同一 src 最坏重复解码一次，后写者胜出（先解码的实例由 GC 回收原生内存）。
/// </summary>
public static class ImageLoader
{
    static readonly ConcurrentDictionary<(string? BaseDir, string Src), SKImage?> Cache = new();

    public static SKImage? Load(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        return Cache.GetOrAdd((RenderScope.Current?.BaseDir, src), static k => Decode(k.Src, k.BaseDir));
    }

    static SKImage? Decode(string src, string? baseDir)
    {
        try
        {
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = src.IndexOf(',');
                if (comma > 0 && src[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = Convert.FromBase64String(src[(comma + 1)..]);
                    using var data = SKData.CreateCopy(bytes);
                    return SKImage.FromEncodedData(data);
                }
            }
            else if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return null; // 刻意不联网：离线 + 确定性
            }
            else
            {
                var path = Path.IsPathRooted(src)
                    ? src
                    : Path.Combine(baseDir ?? AppContext.BaseDirectory, src);
                if (!File.Exists(path)) return null;
                if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    // <img src="*.svg">：解析为矢量后栅格化，复用图像通路
                    var pic = LoadSvg(File.ReadAllText(path));
                    if (pic == null) return null;
                    return SKImage.FromPicture(pic, new SKSizeI(
                        Math.Max(1, (int)Math.Round(pic.CullRect.Width)),
                        Math.Max(1, (int)Math.Round(pic.CullRect.Height))));
                }
                return SKImage.FromEncodedData(path);
            }
        }
        catch
        {
            return null;
        }
        return null; // data: 但非 base64 / 逗号缺失等畸形输入
    }

    static readonly ConcurrentDictionary<string, SKPicture?> SvgCache = new(StringComparer.Ordinal);

    /// <summary>解析内联 &lt;svg&gt; 标记为 SKPicture（PDF 中保持矢量）。按标记内容缓存，与路径无关。</summary>
    public static SKPicture? LoadSvg(string? markup)
    {
        if (string.IsNullOrWhiteSpace(markup)) return null;
        return SvgCache.GetOrAdd(markup, static m =>
        {
            try
            {
                var svg = new Svg.Skia.SKSvg();
                // AngleSharp 的 OuterHtml 不带 xmlns，补上命名空间声明再解析
                var xml = m;
                int tagEnd = xml.IndexOf(' ');
                if (tagEnd > 0 && !xml.Contains("xmlns=", StringComparison.OrdinalIgnoreCase))
                    xml = xml.Insert(tagEnd, " xmlns='http://www.w3.org/2000/svg'");
                return svg.FromSvg(xml) ?? svg.Picture;
            }
            catch { return null; }
        });
    }
}
