using System.Text.RegularExpressions;

namespace PaperSharp.Css;

/// <summary>
/// 解析 CSS 里的 @font-face 块，把 family → 字体文件登记到 FontManager。
/// 只取 src 里第一个 url(...)，相对路径由 FontManager.BaseDir 解析。
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
            var url = ReUrl.Match(body);
            if (!fam.Success || !url.Success) continue;

            string family = fam.Groups[1].Value.Trim().Trim('\'', '"');
            string path = url.Groups[1].Value.Trim().Trim('\'', '"');
            if (string.IsNullOrEmpty(family) || string.IsNullOrEmpty(path)) continue;

            int weight = 400;
            var w = ReWeight.Match(body);
            if (w.Success)
            {
                var wv = w.Groups[1].Value.Trim().ToLowerInvariant();
                weight = wv == "bold" ? 700 : wv == "normal" ? 400
                       : (int.TryParse(wv, out var n) ? n : 400);
            }
            Layout.FontManager.RegisterFont(family, path, weight);
        }
    }
}
