using System.Collections.Concurrent;
using SkiaSharp;

namespace PaperSharp.Layout;

/// <summary>
/// 按 family name 加载 SKTypeface，跨平台回退。
/// 优先加载 assets/fonts/ 下的**子集字体**（tools/subset-font.py 生成），
/// 这样 PDF 里嵌入的是 ~1.8MB 子集而非 ~20MB 全量雅黑。
/// 找不到子集文件时退回系统字体管理器（DirectWrite/CoreText/FontConfig）。
/// </summary>
public static class FontManager
{
    static readonly ConcurrentDictionary<(string, int), SKTypeface> Cache = new();

    // 子集字体：family 别名 → (regular 文件, bold 文件)
    static readonly string FontDir = Path.Combine(AppContext.BaseDirectory, "assets", "fonts");
    static readonly string RegularFile = Path.Combine(FontDir, "YaheiSub-Regular.ttf");
    static readonly string BoldFile = Path.Combine(FontDir, "YaheiSub-Bold.ttf");

    // 命中这些 family 名时优先用打包的子集字体
    static readonly HashSet<string> SubsetFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft YaHei", "微软雅黑", "YaheiSub", "sans-serif", "system-ui",
    };

    static SKTypeface? _subsetRegular;
    static SKTypeface? _subsetBold;
    static bool _loadedSubsetAttempted;

    // @font-face 登记的自定义字体：family → [(weight, 原始路径)]。路径在加载时按 BaseDir 解析。
    public static string? BaseDir;
    static readonly Dictionary<string, List<(int weight, string path)>> Registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>登记一条 @font-face。同 family+weight 覆盖旧的，并清掉该 family 的缓存。</summary>
    public static void RegisterFont(string family, string path, int weight)
    {
        if (!Registered.TryGetValue(family, out var list)) { list = new(); Registered[family] = list; }
        list.RemoveAll(e => e.weight == weight);
        list.Add((weight, path));
        foreach (var k in Cache.Keys.Where(k => string.Equals(k.Item1, family, StringComparison.OrdinalIgnoreCase)).ToList())
            Cache.TryRemove(k, out _);
    }

    static string? ResolveFontPath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return BaseDir != null ? Path.Combine(BaseDir, path) : path;
    }

    static void EnsureSubsetLoaded()
    {
        if (_loadedSubsetAttempted) return;
        _loadedSubsetAttempted = true;
        try
        {
            if (File.Exists(RegularFile))
                _subsetRegular = SKTypeface.FromFile(RegularFile);
            if (File.Exists(BoldFile))
                _subsetBold = SKTypeface.FromFile(BoldFile);
        }
        catch
        {
            _subsetRegular = _subsetBold = null;
        }
    }

    // 常见 family 的跨平台别名表，尽量让同一份 CSS 在三大平台都能落到相似字形上。
    static readonly Dictionary<string, string[]> Fallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sans-serif"] = new[] { "Segoe UI", "Arial", "Helvetica Neue", "DejaVu Sans", "Noto Sans" },
        ["serif"] = new[] { "Times New Roman", "Times", "Georgia", "DejaVu Serif", "Noto Serif" },
        ["monospace"] = new[] { "Consolas", "Courier New", "Menlo", "DejaVu Sans Mono", "Noto Sans Mono" },
        ["Microsoft YaHei"] = new[] { "Microsoft YaHei", "微软雅黑", "PingFang SC", "Noto Sans CJK SC", "Source Han Sans CN", "WenQuanYi Micro Hei" },
        ["微软雅黑"] = new[] { "Microsoft YaHei", "微软雅黑", "PingFang SC", "Noto Sans CJK SC" },
        ["SimSun"] = new[] { "SimSun", "宋体", "Songti SC", "Noto Serif CJK SC" },
        ["宋体"] = new[] { "SimSun", "宋体", "Songti SC", "Noto Serif CJK SC" },
        ["PingFang SC"] = new[] { "PingFang SC", "Microsoft YaHei", "Noto Sans CJK SC" },
        ["Helvetica"] = new[] { "Helvetica Neue", "Helvetica", "Arial", "DejaVu Sans" },
        ["Arial"] = new[] { "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans" },
    };

    public static SKTypeface Get(string family, int weight)
    {
        return Cache.GetOrAdd((family, weight), key => LoadTypeface(key.Item1, key.Item2));
    }

    static SKTypeface LoadTypeface(string family, int weight)
    {
        EnsureSubsetLoaded();

        // @font-face 登记的自定义字体优先（按字重就近取）
        if (Registered.TryGetValue(family, out var faces) && faces.Count > 0)
        {
            var pick = faces.OrderBy(f => Math.Abs(f.weight - weight)).First();
            var full = ResolveFontPath(pick.path);
            if (full != null && File.Exists(full))
            {
                try { var tf = SKTypeface.FromFile(full); if (tf != null) return tf; }
                catch { /* 字体损坏则回退系统字体 */ }
            }
        }

        // 命中子集字体族且子集可用 → 用打包子集（按字重选 regular/bold）
        if (SubsetFamilies.Contains(family))
        {
            var subset = weight >= 700 ? (_subsetBold ?? _subsetRegular) : _subsetRegular;
            if (subset != null) return subset;
        }

        var slant = SKFontStyleSlant.Upright;
        var width = SKFontStyleWidth.Normal;
        var style = weight >= 700 ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var fontStyle = new SKFontStyle(style, width, slant);

        var candidates = Fallbacks.TryGetValue(family, out var list) ? list : new[] { family };

        foreach (var name in candidates)
        {
            var tf = SKTypeface.FromFamilyName(name, fontStyle);
            if (tf != null && !string.IsNullOrEmpty(tf.FamilyName)
                && (tf.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || tf.FamilyName.Contains(name, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(tf.FamilyName, StringComparison.OrdinalIgnoreCase)))
            {
                return tf;
            }
            tf?.Dispose();
        }

        // 最后兜底：如果加载了任何子集，宁可返回子集也不返回纯拉丁默认字体（避免中文方框）
        return _subsetRegular ?? SKTypeface.Default;
    }
}
