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
                    img = SKImage.FromEncodedData(path);
            }
        }
        catch
        {
            img = null;
        }

        Cache[src] = img;
        return img;
    }
}
