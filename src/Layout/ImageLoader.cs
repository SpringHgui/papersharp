using System.Text;
using SkiaSharp;

namespace PaperSharp.Layout;

/// <summary>
/// 图像加载与解码缓存。支持：
/// - 相对/绝对文件路径（相对路径基于 <see cref="BaseDir"/>，即 HTML 所在目录）
/// - data:image/...;base64,... 内联
/// 网络 URL 默认不抓（保证离线 + 确定性输出）；如需可在此扩展。
/// 同一 src 只解码一次，PDF/PNG 复用。
/// </summary>
public static class ImageLoader
{
    public static string? BaseDir { get; set; }

    static readonly Dictionary<string, SKImage?> Cache = new(StringComparer.Ordinal);

    public static SKImage? Load(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        if (Cache.TryGetValue(src, out var hit)) return hit;

        SKImage? img = null;
        try
        {
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = src.IndexOf(',');
                if (comma > 0 && src[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = Convert.FromBase64String(src[(comma + 1)..]);
                    using var data = SKData.CreateCopy(bytes);
                    img = SKImage.FromEncodedData(data);
                }
            }
            else if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                img = null; // demo 不联网
            }
            else
            {
                var path = Path.IsPathRooted(src)
                    ? src
                    : Path.Combine(BaseDir ?? AppContext.BaseDirectory, src);
                if (File.Exists(path))
                {
                    if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        // <img src="*.svg">：解析为矢量后栅格化，复用图像通路
                        var pic = LoadSvg(File.ReadAllText(path));
                        if (pic != null)
                        {
                            img = SKImage.FromPicture(pic, new SKSizeI(
                                Math.Max(1, (int)Math.Round(pic.CullRect.Width)),
                                Math.Max(1, (int)Math.Round(pic.CullRect.Height))));
                        }
                    }
                    else
                    {
                        img = SKImage.FromEncodedData(path);
                    }
                }
            }
        }
        catch
        {
            img = null;
        }

        Cache[src] = img;
        return img;
    }

    static readonly Dictionary<string, SKPicture?> SvgCache = new(StringComparer.Ordinal);

    /// <summary>解析内联 &lt;svg&gt; 标记为 SKPicture（PDF 中保持矢量）。按标记缓存。</summary>
    public static SKPicture? LoadSvg(string? markup)
    {
        if (string.IsNullOrWhiteSpace(markup)) return null;
        if (SvgCache.TryGetValue(markup, out var hit)) return hit;
        SKPicture? pic = null;
        try
        {
            var svg = new Svg.Skia.SKSvg();
            // AngleSharp 的 OuterHtml 不带 xmlns，补上命名空间声明再解析
            var xml = markup;
            int tagEnd = xml.IndexOf(' ');
            if (tagEnd > 0 && !xml.Contains("xmlns=", StringComparison.OrdinalIgnoreCase))
                xml = xml.Insert(tagEnd, " xmlns='http://www.w3.org/2000/svg'");
            pic = svg.FromSvg(xml) ?? svg.Picture;
        }
        catch { pic = null; }
        SvgCache[markup] = pic;
        return pic;
    }
}
