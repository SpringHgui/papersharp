using System.Collections.Concurrent;
using SkiaSharp;

namespace PaperSharp.Layout;

/// <summary>
/// 按 family name 加载 SKTypeface，跨平台回退。
/// 优先加载 assets/fonts/ 下的**子集字体**（tools/subset-font.py 生成），
/// 这样 PDF 里嵌入的是 ~1.8MB 子集而非 ~20MB 全量雅黑。
/// 找不到子集文件时退回系统字体管理器（DirectWrite/CoreText/FontConfig）。
/// 线程安全：@font-face 注册进当前 <see cref="RenderScope"/>（每请求隔离，路径按 Scope.BaseDir 解析）；
/// RegisterFontDirectory 走全局注册表；子集/系统回退结果进程级缓存。
/// </summary>
public static class FontManager
{
    // 进程级缓存：只放与 @font-face 注册态无关的解析结果（子集 / 系统别名回退）。
    static readonly ConcurrentDictionary<(string, int), SKTypeface> Cache = new();

    // 全局注册表（RegisterFontDirectory 等 Scope 外注册）：路径按原样/当前 Scope.BaseDir 解析。
    static readonly ConcurrentDictionary<string, (int weight, string[] paths)[]> GlobalRegistered = new(StringComparer.OrdinalIgnoreCase);

    // 子集字体：family 别名 → (regular 文件, bold 文件)
    static readonly string FontDir = Path.Combine(AppContext.BaseDirectory, "assets", "fonts");
    static readonly string RegularFile = Path.Combine(FontDir, "YaheiSub-Regular.ttf");
    static readonly string BoldFile = Path.Combine(FontDir, "YaheiSub-Bold.ttf");

    // 命中这些 family 名时优先用打包的子集字体
    static readonly HashSet<string> SubsetFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft YaHei", "微软雅黑", "YaheiSub", "sans-serif", "system-ui",
    };

    static readonly object SubsetLock = new();
    static SKTypeface? _subsetRegular;
    static SKTypeface? _subsetBold;
    static volatile bool _loadedSubsetAttempted;

    /// <summary>登记一条 @font-face（单文件）。同 family+weight 覆盖旧的；在 <see cref="RenderScope"/> 内则登记为该请求私有。</summary>
    public static void RegisterFont(string family, string path, int weight)
        => RegisterFont(family, new List<string> { path }, weight);

    /// <summary>登记一条 @font-face：paths 是按声明顺序的候选文件（如 woff2→ttf 兜底链），加载时逐个尝试。</summary>
    public static void RegisterFont(string family, List<string> paths, int weight)
    {
        var scope = RenderScope.Current;
        if (scope != null)
        {
            scope.AddFont(family, paths, weight);
            return;
        }
        GlobalRegistered.AddOrUpdate(family,
            _ => new[] { (weight, paths.ToArray()) },
            (_, old) => old.Where(e => e.weight != weight).Append((weight, paths.ToArray())).ToArray());
        foreach (var k in Cache.Keys.Where(k => string.Equals(k.Item1, family, StringComparison.OrdinalIgnoreCase)).ToList())
            Cache.TryRemove(k, out _);
    }

    /// <summary>
    /// 简单字体目录注册 helper：扫目录里的 ttf/otf/woff/woff2，读出真实 family name 逐一登记（全局）。
    /// 之后 CSS 里直接写该 family 即可命中，无需手写 @font-face。
    /// </summary>
    public static void RegisterFontDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".ttf" or ".otf" or ".woff" or ".woff2")) continue;
            try
            {
                using var tf = SKTypeface.FromFile(file);
                if (tf == null) continue;
                string family = tf.FamilyName;
                if (string.IsNullOrWhiteSpace(family)) continue;
                int weight = tf.FontWeight is >= 1 and <= 1000 ? tf.FontWeight : 400;
                RegisterFont(family, file, weight);
            }
            catch { /* 坏文件跳过，不影响其他字体 */ }
        }
    }

    static string? ResolveFontPath(string path, string? baseDir)
    {
        if (Path.IsPathRooted(path)) return path;
        return baseDir != null ? Path.Combine(baseDir, path) : path;
    }

    static void EnsureSubsetLoaded()
    {
        if (_loadedSubsetAttempted) return;
        lock (SubsetLock)
        {
            if (_loadedSubsetAttempted) return;
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
            _loadedSubsetAttempted = true; // 最后置位：读侧见到 flag 即见到完整赋值（lock 提供屏障）
        }
    }

    /// <summary>字形级回退首选：打包子集字体若覆盖该码位就优先用它（与正文同字体，PDF 嵌入不额外膨胀）。</summary>
    public static SKTypeface? SubsetIfCovers(int cp, int weight)
    {
        EnsureSubsetLoaded();
        var primary = weight >= 700 ? _subsetBold : _subsetRegular;
        var fallback = weight >= 700 ? _subsetRegular : _subsetBold;
        if (primary != null && primary.GetGlyph(cp) != 0) return primary;
        if (fallback != null && fallback.GetGlyph(cp) != 0) return fallback;
        return null;
    }

    /// <summary>该实例是否为进程级共享字体（子集/默认）——调用方不得 Dispose。</summary>
    public static bool IsSharedTypeface(SKTypeface tf) =>
        ReferenceEquals(tf, _subsetRegular) || ReferenceEquals(tf, _subsetBold) || ReferenceEquals(tf, SKTypeface.Default);

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
        var scope = RenderScope.Current;

        // 1) 文档级 @font-face（请求私有）：缓存归 Scope，路径按 Scope.BaseDir 解析
        var faces = scope?.TryGetFaces(family)
                 ?? (GlobalRegistered.TryGetValue(family, out var g) ? g : null);
        if (faces != null && scope != null)
        {
            var cached = scope.GetOrAddTypeface(family, weight, () => LoadRegistered(faces, weight, scope.BaseDir));
            if (cached != null) return cached;
        }
        else if (faces != null)
        {
            // Scope 外直接用全局注册表：不缓存（文件命中很少走这条；避免注册态与缓存键耦合）
            var tf = LoadRegistered(faces, weight, null);
            if (tf != null) return tf;
        }

        // 2) 与注册态无关的回退：进程级缓存
        return Cache.GetOrAdd((family, weight), k => ResolveFallback(k.Item1, k.Item2));
    }

    static SKTypeface? LoadRegistered((int weight, string[] paths)[] faces, int weight, string? baseDir)
    {
        // 按字重就近取；同字重内按 src 声明顺序逐个尝试文件
        var pick = faces.OrderBy(f => Math.Abs(f.weight - weight)).First();
        foreach (var raw in pick.paths)
        {
            var full = ResolveFontPath(raw, baseDir);
            if (full == null || !File.Exists(full)) continue;
            try { var tf = SKTypeface.FromFile(full); if (tf != null) return tf; }
            catch { /* 该候选损坏 → 试下一个 src */ }
        }
        return null;
    }

    static SKTypeface ResolveFallback(string family, int weight)
    {
        EnsureSubsetLoaded();

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
