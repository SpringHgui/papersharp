using System.Collections.Concurrent;
using SkiaSharp;

namespace PaperSharp.Layout;

/// <summary>
/// 按 family name 加载 SKTypeface，跨平台回退。
/// 优先加载 assets/fonts/ 下的**子集字体**（src/PaperSharpSamples/tools/subset-font.py 生成），
/// 这样 PDF 里嵌入的是 ~2.2MB 子集而非 ~17MB 全量字体。
/// **打包了两族 face，按 CSS family 关键字分派**：参考浏览器把 `"Microsoft YaHei"` 解析到本机真装的
/// 雅黑（normal 行盒 16px→21），把 generic `sans-serif` 解析到 Noto Sans SC（16px→24）——两者度量差
/// 一整档，用一族替另一族会让每一行恒差 1~3px（实测反证：inline/table/m3 声明 sans-serif，
/// m1/m2/long/invoice/report/pricing-* 声明雅黑）。找不到子集文件时退回系统字体管理器。
/// 线程安全：@font-face 注册进当前 <see cref="RenderScope"/>（每请求隔离，路径按 Scope.BaseDir 解析）；
/// RegisterFontDirectory 走全局注册表；子集/系统回退结果进程级缓存。
/// </summary>
public static class FontManager
{
    // 进程级缓存：只放与 @font-face 注册态无关的解析结果（子集 / 系统别名回退）。
    static readonly ConcurrentDictionary<(string, int), SKTypeface> Cache = new();

    // 全局注册表（RegisterFontDirectory 等 Scope 外注册）：路径按原样/当前 Scope.BaseDir 解析。
    static readonly ConcurrentDictionary<string, (int weight, string[] paths)[]> GlobalRegistered = new(StringComparer.OrdinalIgnoreCase);

    // 子集字体：一族两份字面（regular/bold），按 CSS family 关键字分派
    static readonly string FontDir = Path.Combine(AppContext.BaseDirectory, "assets", "fonts");
    static readonly string[] NotoFiles =
    [
        Path.Combine(FontDir, "NotoSansSCSub-Regular.ttf"),
        Path.Combine(FontDir, "NotoSansSCSub-Bold.ttf"),
    ];
    static readonly string[] YaheiFiles =
    [
        Path.Combine(FontDir, "YaheiSub-Regular.ttf"),
        Path.Combine(FontDir, "YaheiSub-Bold.ttf"),
    ];
    static readonly string MonoRegularFile = Path.Combine(FontDir, "NSimSunSub-Regular.ttf");

    // 命中这些 family 名时直接用打包子集，而不是去系统里找同名字体（PDF 体积 + 跨平台一致）。
    // "YaheiSub" 是历史别名：样例 CSS 里直接把它当 family 名写过（m4 的 .dirfont），浏览器侧那是不
    // 认识的名字、会落到下一个候选，引擎侧保持指向同一份子集才对得上。
    static readonly HashSet<string> YaheiSubsetFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft YaHei", "微软雅黑", "YaheiSub", "Microsoft YaHei UI", "system-ui",
    };
    static readonly HashSet<string> NotoSubsetFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Noto Sans SC", "sans-serif",
    };
    // Blink 的 generic monospace 在本机解析到 NSimSun（ascent+descent 恰好 1.0em、Latin 字宽 0.5em），
    // 不是 Consolas（1.17em / 0.55em）——写 `font-family: monospace` 或 UA 样式的 <code> 都走这里，
    // 用 Consolas 会让每个 <code> 盒高 +2px、宽度 +3~11px，并把所在行整行撑高 1px（m1 全页下移 8px 之源）。
    static readonly HashSet<string> MonoSubsetFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "monospace",
    };

    static readonly object SubsetLock = new();
    static SKTypeface? _notoRegular;
    static SKTypeface? _notoBold;
    static SKTypeface? _yaheiRegular;
    static SKTypeface? _yaheiBold;
    static SKTypeface? _monoRegular;
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
            (_notoRegular, _notoBold) = LoadPair(NotoFiles);
            (_yaheiRegular, _yaheiBold) = LoadPair(YaheiFiles);
            try { _monoRegular = FromFile(MonoRegularFile); }
            catch { _monoRegular = null; }
            _loadedSubsetAttempted = true; // 最后置位：读侧见到 flag 即见到完整赋值（lock 提供屏障）
        }
    }

    static (SKTypeface?, SKTypeface?) LoadPair(string[] files)
    {
        try
        {
            return (FromFile(files[0]), FromFile(files[1]));
        }
        catch { return (null, null); }
    }

    /// <summary>从文件加载字面，并顺带登记它的字距对表（<see cref="KernTable"/>，只有打包子集带）。</summary>
    static SKTypeface? FromFile(string path)
    {
        if (!File.Exists(path)) return null;
        var tf = SKTypeface.FromFile(path);
        if (tf != null) KernTable.Attach(tf, path);
        return tf;
    }

    /// <summary>字形级回退首选：打包子集字体若覆盖该码位就优先用它（与正文同字体，PDF 嵌入不额外膨胀）。</summary>
    public static SKTypeface? SubsetIfCovers(int cp, int weight)
    {
        EnsureSubsetLoaded();
        // 两族子集覆盖同一字符集，字形级回退统一走 Noto（浏览器缺字时的兜底 face 也是它）
        var primary = weight >= 700 ? _notoBold : _notoRegular;
        var fallback = weight >= 700 ? _notoRegular : _notoBold;
        if (primary != null && primary.GetGlyph(cp) != 0) return primary;
        if (fallback != null && fallback.GetGlyph(cp) != 0) return fallback;
        return null;
    }

    /// <summary>该实例是否为打包的 legacy 等宽子集（NSimSun）。浏览器渲染这张 face 时不做线性缩放，
    /// 而是把 advance 网格对齐到整半格，度量必须走 <see cref="TextShaper.MonoGridWidth"/> 那条路。</summary>
    public static bool IsLegacyMonoSubset(SKTypeface tf)
    {
        EnsureSubsetLoaded();
        return ReferenceEquals(tf, _monoRegular);
    }

    /// <summary>该实例是否为进程级共享字体（子集/默认）——调用方不得 Dispose。</summary>
    public static bool IsSharedTypeface(SKTypeface tf) =>
        ReferenceEquals(tf, _notoRegular) || ReferenceEquals(tf, _notoBold)
        || ReferenceEquals(tf, _yaheiRegular) || ReferenceEquals(tf, _yaheiBold)
        || ReferenceEquals(tf, _monoRegular)
        || ReferenceEquals(tf, SKTypeface.Default);

    // 常见 family 的跨平台别名表，尽量让同一份 CSS 在三大平台都能落到相似字形上。
    static readonly Dictionary<string, string[]> Fallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sans-serif"] = new[] { "Segoe UI", "Arial", "Helvetica Neue", "DejaVu Sans", "Noto Sans" },
        ["serif"] = new[] { "Times New Roman", "Times", "Georgia", "DejaVu Serif", "Noto Serif" },
        ["monospace"] = new[] { "NSimSun", "SimSun", "Consolas", "Courier New", "Menlo", "DejaVu Sans Mono", "Noto Sans Mono" },
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

    // 字面 → 该字体的纵向度量（相对 em）。按 SKTypeface 实例引用键控（Get 的结果本就带缓存），
    // 不同文档的 @font-face 同名不同面也不会串味；字面被回收时条目跟着走，不泄漏。
    sealed class VertMetrics(float ascent, float descent, float normalLh, float xHeight)
    {
        public readonly float Ascent = ascent, Descent = descent, NormalLh = normalLh, XHeight = xHeight;
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKTypeface, VertMetrics> MetricsTable = new();

    static VertMetrics Of(SKTypeface t) => MetricsTable.GetValue(t, static t =>
    {
        using var f = new SKFont(t, 100f);
        var m = f.Metrics;
        float a = -m.Ascent / 100f, d = m.Descent / 100f;   // SKFontMetrics.Ascent 为负
        if (!(a > 0 && d >= 0) || float.IsNaN(a + d)) return new VertMetrics(0.8f, 0.2f, 1.4f, 0.5f);
        float lh = a + d;   // 不含 lineGap：Blink 的 normal 行盒就是取整后的 ascent+descent
        float xh = m.XHeight / 100f;
        return new VertMetrics(a, d, lh > 0 && !float.IsNaN(lh) ? lh : 1.4f, xh > 0.05f && xh < a ? xh : 0.5f);
    });

    /// <summary>该字族+字重的字体 ascent/descent 相对 em 的比值（只要比例，缩放交给 <see cref="TextExtents"/>）。</summary>
    public static (float Ascent, float Descent) ExtentsPerEm(string family, int weight)
    {
        var v = Of(Get(family, weight));
        return (v.Ascent, v.Descent);
    }

    /// <summary>
    /// 某字号下字体的 ascent/descent（px）。浏览器把两者各自<b>取整到整像素</b>（半值向上）后才用：
    /// 15px 雅黑是 16/4，不是 15.87/3.93。不取整会让每个行内盒恒差 ~0.5–1.2px、基线跟着偏，
    /// 并按行往下累积（inline/m1 里每个 span 稳定 1.2px 的偏差就是这一处）。
    /// 取整口径已用 Arial/雅黑/Noto SC/宋体五张 face × 10 个字号的浏览器实测反证吻合。
    /// </summary>
    public static (float Asc, float Desc) TextExtents(string family, int weight, float sizePx)
    {
        var v = Of(Get(family, weight));
        return (Px(v.Ascent * sizePx), Px(v.Descent * sizePx));
    }

    static float Px(float v) => (float)Math.Floor(v + 0.5f);   // = SkScalarRoundToInt：half-up，不是 banker's

    /// <summary>该字族的 x-height 相对 em（vertical-align:middle 用）。</summary>
    public static float XHeightPerEm(string family, int weight) => Of(Get(family, weight)).XHeight;

    /// <summary>
    /// 该字族+字重的 <c>line-height: normal</c> 行距比 = 字体 ascent+descent 相对 em（不含 lineGap）。
    /// 只作 ratio 用；行盒高一律走 <see cref="NormalLineHeightPx"/>——浏览器是**逐度量取整后相加**，
    /// 不是「比值 × 字号」（13px 雅黑：round(13·0.9678)+round(13·0.3310)=13+4=17，而乘出来 16.88）。
    /// 统一按 1.4 或按不取整的比值兜底，会让每段未显式设行高的正文各差零点几 px，逐段累积成整页漂移。
    /// </summary>
    public static float NormalLineHeight(string family, int weight) => Of(Get(family, weight)).NormalLh;

    /// <summary><c>line-height: normal</c> 在该字号下的行盒高（px）= 取整后的 ascent + descent。</summary>
    public static float NormalLineHeightPx(string family, int weight, float sizePx)
    {
        var (asc, desc) = TextExtents(family, weight, sizePx);
        return asc + desc;
    }

    static SKTypeface? LoadRegistered((int weight, string[] paths)[] faces, int weight, string? baseDir)    {
        // 按字重就近取；同字重内按 src 声明顺序逐个尝试文件
        var pick = faces.OrderBy(f => Math.Abs(f.weight - weight)).First();
        foreach (var raw in pick.paths)
        {
            var full = ResolveFontPath(raw, baseDir);
            if (full == null || !File.Exists(full)) continue;
            try { var tf = FromFile(full); if (tf != null) return tf; }
            catch { /* 该候选损坏 → 试下一个 src */ }
        }
        return null;
    }

    static SKTypeface ResolveFallback(string family, int weight)
    {
        EnsureSubsetLoaded();

        // 命中子集字体族且子集可用 → 用打包子集（按字重选 regular/bold）
        if (MonoSubsetFamilies.Contains(family))
        {
            if (_monoRegular != null) return _monoRegular;
        }
        else if (YaheiSubsetFamilies.Contains(family) || NotoSubsetFamilies.Contains(family))
        {
            bool yahei = YaheiSubsetFamilies.Contains(family);
            var regular = yahei ? _yaheiRegular : _notoRegular;
            var bold = yahei ? _yaheiBold : _notoBold;
            var subset = weight >= 700 ? (bold ?? regular) : regular;
            if (subset != null) return subset;
        }

        var slant = SKFontStyleSlant.Upright;
        var width = SKFontStyleWidth.Normal;
        // 按 CSS 字重映射到 Skia 字重枚举，交给系统字体管理器就近匹配真实字面。
        // 旧代码把 <700 一律压成 Normal，导致 500/600 的标题、按钮比浏览器明显偏细。
        var style = weight switch
        {
            >= 900 => SKFontStyleWeight.Black,
            >= 800 => SKFontStyleWeight.ExtraBold,
            >= 700 => SKFontStyleWeight.Bold,
            >= 600 => SKFontStyleWeight.SemiBold,
            >= 500 => SKFontStyleWeight.Medium,
            >= 400 => SKFontStyleWeight.Normal,
            >= 300 => SKFontStyleWeight.Light,
            >= 200 => SKFontStyleWeight.ExtraLight,
            _ => SKFontStyleWeight.Thin,
        };
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
                // 系统字面没有 PRKT，但字距在自带的 GPOS 里：解析一次登记进缓存（后续按 family/weight 命中）。
                KernTable.AttachFromTables(tf);
                return tf;
            }
            tf?.Dispose();
        }

        // 最后兜底：宁可返回子集也不返回纯拉丁默认字体（避免中文方框）
        return _notoRegular ?? _yaheiRegular ?? SKTypeface.Default;
    }
}
