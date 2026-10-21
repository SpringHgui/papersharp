using System.Text.RegularExpressions;

namespace PaperSharp.Css;

/// <summary>
/// 解析 CSS 里的 @font-face 块，把 family → 字体文件登记到当前 <see cref="RenderScope"/>。
/// src 里的多个 url(...)（如 woff2 → woff → ttf 兜底链）按声明顺序记录，加载时逐个尝试；
/// data: 与 local() 跳过。相对路径由 RenderScope.BaseDir 解析。
/// 必须在 <see cref="RenderScope.Begin"/> 内调用才具备跨请求隔离（Renderer 入口已自动开启）。
/// </summary>
public static class FontFaceParser
{
    static readonly Regex ReBlock = new(@"@font-face\s*\{([^}]*)\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ReFamily = new(@"font-family\s*:\s*([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ReWeight = new(@"font-weight\s*:\s*([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ReUrl = new(@"url\(\s*['""]?([^'"")]+)['""]?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void ParseAndRegister(string css)
    {
        if (string.IsNullOrWhiteSpace(css)) return;
        foreach (Match block in ReBlock.Matches(css))
        {
            var body = block.Groups[1].Value;
            var fam = ReFamily.Match(body);
            if (!fam.Success) continue;
            var paths = ReUrl.Matches(body).Select(u => u.Groups[1].Value.Trim().Trim('\'', '"'))
                        .Where(p => p.Length > 0 && !p.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        .ToList();
            if (paths.Count == 0) continue;

            string family = fam.Groups[1].Value.Trim().Trim('\'', '"');
            if (string.IsNullOrEmpty(family)) continue;

            int weight = 400;
            var w = ReWeight.Match(body);
            if (w.Success)
            {
                var wv = w.Groups[1].Value.Trim().ToLowerInvariant();
                weight = wv == "bold" ? 700 : wv == "normal" ? 400
                       : (int.TryParse(wv, out var n) ? n : 400);
            }
            Layout.FontManager.RegisterFont(family, paths, weight);
        }
    }
}
