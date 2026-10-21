using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PaperSharp.Css;

/// <summary>
/// Google Fonts 式字体 helper：按 family 规格拉取 fonts.googleapis.com 的 @font-face CSS，
/// 把其中的字体文件下载到本地缓存目录，并把 url() 重写为缓存绝对路径。
/// 联网一次后完全离线复用（缓存命中不再发请求）。返回的 CSS 拼进用户样式表即可，
/// FontFaceParser 会解析并注册这些本地文件。
/// </summary>
public static class GoogleFonts
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>缓存目录（默认 %LocalAppData%/PaperSharp/gfonts，可改）。</summary>
    public static string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperSharp", "gfonts");

    /// <summary>
    /// 拉取字体并返回改写为本地缓存路径的 @font-face CSS。
    /// spec 例："Roboto"、"Roboto:ital,wght@400;700"、"Noto Serif SC:wght@400;700"。
    /// 网络失败但本机拉过（有缓存清单）时直接返回缓存版 CSS（离线复用）；两者都没有返回 ""（渲染端自然回退）。
    /// </summary>
    public static string Fetch(string spec)
    {
        var meta = Path.Combine(CacheDir, Key(spec) + ".css");
        string css;
        try
        {
            var cssUrl = "https://fonts.googleapis.com/css2?family=" + spec.Trim().Replace(' ', '+') + "&display=swap";
            // Chrome UA → Google 返回 woff2（而非老的 ttf/eot）。逐请求携带，不污染共享 HttpClient 头。
            using var req = new HttpRequestMessage(HttpMethod.Get, cssUrl);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            css = Http.Send(req).Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch
        {
            try { return File.Exists(meta) ? File.ReadAllText(meta) : ""; } catch { return ""; }
        }

        try
        {
            Directory.CreateDirectory(CacheDir);
            var sb = new StringBuilder();
            foreach (Match block in Regex.Matches(css, @"@font-face\s*\{[^}]*\}"))
            {
                var b = ReUrl.Replace(block.Value, m =>
                {
                    var local = CacheAsset(m.Groups[1].Value);
                    return local != null ? $"url('{local}')" : m.Value;
                });
                sb.AppendLine(b);
            }
            var result = sb.ToString();
            if (result.Length > 0) File.WriteAllText(meta, result);   // 清单落盘 → 下次离线可用
            return result;
        }
        catch { return File.Exists(meta) ? File.ReadAllText(meta) : ""; }
    }

    static readonly Regex ReUrl = new(@"url\(\s*['""]?([^'"")]+)['""]?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string Key(string spec) => Regex.Replace(spec, @"[^A-Za-z0-9]+", "_").Trim('_');

    /// <summary>按 URL 散列入缓存；成功返回绝对路径，失败返回 null（保留原 url 不动）。</summary>
    static string? CacheAsset(string url)
    {
        try
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
            var ext = Path.GetExtension(url.Split('?')[0]);
            if (string.IsNullOrEmpty(ext)) ext = ".woff2";
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();
            var path = Path.Combine(CacheDir, hash + ext);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(CacheDir);
                var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);   // 并发同 URL：各写各的 tmp，原子改名，后到覆盖先到
            }
            return path;
        }
        catch { return null; }
    }
}
