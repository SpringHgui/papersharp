using System.Buffers.Binary;
using System.Collections.Concurrent;
using SkiaSharp;

namespace PaperSharp.Layout;

/// <summary>
/// 按 family name 加载 SKTypeface，跨平台回退。
/// 优先用**打包的子集字体**（src/PaperSharpSamples/tools/subset-font.py 生成，解析顺序见 <see cref="LoadFace"/>），
/// 这样 PDF 里嵌入的是 ~2.2MB 子集而非 ~17MB 全量字体。NuGet 包只内嵌 OFL 授权的 Noto 两族字面，
/// 雅黑/宋体子集由 <see cref="BundledFontDirectory"/> 指过来才生效（样例工程即如此）。
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
    // 键里带倾斜档：同一族在 italic 下可能落到系统里另一张字面（Arial → Arial Italic）。
    static readonly ConcurrentDictionary<(string, int, SKFontStyleSlant), SKTypeface> Cache = new();

    // 全局注册表（RegisterFontDirectory 等 Scope 外注册）：路径按原样/当前 Scope.BaseDir 解析。
    static readonly ConcurrentDictionary<string, (int weight, string[] paths)[]> GlobalRegistered = new(StringComparer.OrdinalIgnoreCase);

    // 子集字体：一族两份字面（regular/bold），按 CSS family 关键字分派
    //
    // 历史坑（PERF_REPORT §12.6）：这批字体原先只按 AppContext.BaseDirectory/assets/fonts 找，
    // 那是**入口程序集**旁边——库说了不算，而且字体根本没进 NuGet 包。结果消费方静默退回系统字体，
    // 同一份 HTML 体积差 2.9~12.7 倍，「PDF 体积 + 跨平台一致」这两个设计目的双双失效，且毫无信号。
    // 现在字体嵌在 dll 里，路径只作为显式覆盖与老宿主的兼容回退存在。
    static readonly string FontDir = Path.Combine(AppContext.BaseDirectory, "assets", "fonts");

    /// <summary>
    /// 显式指定打包子集字体所在目录（在首次渲染之前设置）。为 null 时用 dll 内嵌的那份。
    /// 用途：重新烤了子集、想在不改代码的情况下让引擎吃自己那份字体的宿主（含本仓样例工程）。
    /// </summary>
    public static string? BundledFontDirectory { get; set; }

    /// <summary>
    /// 打包子集实际从哪儿来的："<c>directory:…</c>" / "<c>embedded</c>" / "<c>legacy:…</c>" /
    /// "<c>none</c>"（= 全部落空，正文将退回系统字体）。首次用到子集字体时定格。
    /// 消费方可以在启动时断言它，CI 里更该断言——静默降级成系统字体是这个库最容易踩的坑。
    /// </summary>
    public static string FontSource { get; private set; } = "none";

    /// <summary>打包子集完全不可用时的一次性告警（只发一次，不改变渲染结果，纯给宿主听见的机会）。
    public static event Action<string>? BundledFontsUnavailable;

    // 打包子集的字面**文件名** —— <see cref="RegisterFontDirectory"/> 必须跳过它们（原因见该方法）。
    // 认名字而不是绝对路径：同一份子集现在可能落在显式目录、dll 内嵌、老路径三处任一，只有名字稳定。
    static readonly HashSet<string> BundledSubsetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "NotoSansSCSub-Regular.ttf", "NotoSansSCSub-Bold.ttf",
        "YaheiSub-Regular.ttf", "YaheiSub-Bold.ttf",
        "NSimSunSub-Regular.ttf",
    };

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
    /// <summary>该 family 是否由打包 CJK 子集承接（雅黑/Noto/宋体系）——这类面不申报 arab 脚本，
    /// 阿拉伯文必须像 Blink 那样回退系统面（见 <see cref="TextShaper"/>）。</summary>
    internal static bool CjkSubsetFamily(string family) =>
        YaheiSubsetFamilies.Contains(family) || NotoSubsetFamilies.Contains(family) || MonoSubsetFamilies.Contains(family)
        || family is "SimSun" or "宋体" or "NSimSun";
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
    /// **跳过打包子集**：那几份已由上面的按族分派表接管，再按真实族名登记一遍等于用裁过的字面抢掉系统真面。
    /// NSimSun 子集只含 ASCII（96 字面，专给 generic <c>monospace</c> 压 PDF 体积），一旦被登记成 family
    /// "NSimSun"，Playground 里写 <c>font-family:"NSimSun"</c> 的中文就整族掉进 Noto 兜底 → 宋体画成黑体
    /// （fontvs 第 11 格实测：引擎 sans、浏览器 serif）。
    /// </summary>
    public static void RegisterFontDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".ttf" or ".otf" or ".woff" or ".woff2")) continue;
            if (BundledSubsetNames.Contains(Path.GetFileName(file))) continue;
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
            (_notoRegular, _notoBold) = LoadPair("NotoSansSCSub");
            (_yaheiRegular, _yaheiBold) = LoadPair("YaheiSub");
            _monoRegular = LoadFace("NSimSunSub-Regular");
            if (FontSource == "none")
            {
                // 全部落空 ⇒ 正文会静默退回系统字体：体积与跨平台一致双双失守，必须让宿主有机会听见
                try { BundledFontsUnavailable?.Invoke(FontDir); } catch { /* 宿主订阅方炸了不影响渲染 */ }
            }
            _loadedSubsetAttempted = true; // 最后置位：读侧见到 flag 即见到完整赋值（lock 提供屏障）
        }
    }

    static (SKTypeface?, SKTypeface?) LoadPair(string stem)
        => (LoadFace(stem + "-Regular"), LoadFace(stem + "-Bold"));

    /// <summary>
    /// 一张打包子集字面的三级解析：显式目录 → dll 内嵌 → 老的 BaseDirectory 路径。
    /// 内嵌排在老路径之前，正是为了让「测试宿主」与「NuGet 消费方」拿到**同一份**字体——
    /// 以前两者拿到的是不同字面，仓库的金标因此从没覆盖过消费方实际走的那条分支。
    /// 老路径仍留着：重烤字体后想临时验证的宿主可以就地放文件，不必改代码。
    /// </summary>
    static SKTypeface? LoadFace(string stem)
    {
        var dir = BundledFontDirectory;
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var tf = FromFile(Path.Combine(dir, stem + ".ttf"));
            if (tf != null) { NoteSource("directory:" + dir); return tf; }
        }
        var bytes = Fonts.Bundled.Read("PaperSharp.Fonts." + stem + ".ttf");
        if (bytes is { Length: > 0 })
        {
            var tf = FromBytes(bytes);
            if (tf != null) { NoteSource("embedded"); return tf; }
        }
        var legacy = FromFile(Path.Combine(FontDir, stem + ".ttf"));
        if (legacy != null) { NoteSource("legacy:" + FontDir); return legacy; }
        return null;
    }

    /// <summary>记录来源；若不同字面来自不同处，如实标成 mixed（那说明部署状态本身就不一致）。</summary>
    static void NoteSource(string src)
    {
        if (FontSource == "none") FontSource = src;
        else if (FontSource != src && !FontSource.StartsWith("mixed:")) FontSource = "mixed:" + FontSource + "|" + src;
    }

    // SKData 交给 SKTypeface 后仍自持一份引用：Skia 的 FromData 语义在各版本上是「借用」，
    // 提前 Dispose 会得到一张静默失效的 face。5 张 face、8 MB 级，随进程活着可接受。
    static readonly List<SKData> KeptData = new();

    /// <summary>从内存里的 sfnt 加载字面，并按同一份字节登记 PRKT 字距表。</summary>
    static SKTypeface? FromBytes(byte[] sfnt)
    {
        try
        {
            var data = SKData.CreateCopy(sfnt);
            var tf = SKTypeface.FromData(data);
            if (tf == null) { data.Dispose(); return null; }
            lock (KeptData) KeptData.Add(data);
            KernTable.AttachFromBytes(tf, sfnt);   // 不能退成 AttachFromTables：那会丢 PRKT
            return tf;
        }
        catch { return null; }
    }

    /// <summary>从文件加载字面，并顺带登记它的字距对表（<see cref="KernTable"/>，只有打包子集带）。
    /// webfont=true：这是 <c>@font-face</c> 声明的网络字体实例——浏览器只认文件自带的 GPOS，
    /// 不套用打包时烘进 PRKT 的源 legacy kern，故走 <see cref="KernTable.AttachWebfont"/> 的实例覆盖。</summary>
    static SKTypeface? FromFile(string path, bool webfont = false)
    {
        if (!File.Exists(path)) return null;
        var tf = SKTypeface.FromFile(path);
        if (tf != null)
        {
            if (webfont) KernTable.AttachWebfont(tf);
            else KernTable.Attach(tf, path);
        }
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

    /// <summary>该实例是否为 Blink 会「整半格」网格化的 legacy 等宽面（NSimSun 系）。浏览器渲染这类面时不做线性缩放，
    /// 而是网格对齐到整「半格」，度量必须走 <see cref="TextShaper.MonoGridWidth"/> 那条路。</summary>
    /// <remarks>以前只认打包子集那一个实例，而 generic <c>monospace</c> 的候选链（<see cref="CandidatesOf"/>）
    /// 首项是族名 NSimSun → <c>Get("NSimSun")</c> 拿到的是系统真面（28905 字面），于是同一张 face 换条路进来
    /// 就掉出网格、按 hmtx 的 0.5em 线性量：11pt 的 <c>&lt;code&gt;</c> 每字少 0.667px（m1 六处 code 盒各窄 12~24px）。</remarks>
    public static bool IsLegacyMonoFace(SKTypeface tf)
    {
        EnsureSubsetLoaded();
        return tf != null && (ReferenceEquals(tf, _monoRegular) || LegacyMonoFamilies.Contains(tf.FamilyName));
    }

    /// <summary>实测（CDP 量 span 宽）会被网格化的族：NSimSun/SimSun/MS Gothic 在 8~17px 全部走 ⌈fs/2⌉；
    /// 同为 0.5em 的 KaiTi/SimHei/FangSong 与真等宽 Consolas/Courier New 都是线性，别混进来。</summary>
    static readonly HashSet<string> LegacyMonoFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "NSimSun", "NSimSun-18030", "SimSun", "SimSun-18030", "宋体", "MS Gothic",
    };

    /// <summary>
    /// 没有真粗体面的字族：请求 weight≥700 时浏览器（Blink + DirectWrite）会**合成粗体并把 advance 加宽**
    /// （有头 Edge 实测 13.328125px 下 NSimSun 的 CJK 每字 13.586 = 1em × 1.0194），Skia 只在 paint 上开
    /// <c>FakeBoldText</c> 才加宽。度量与绘制两端都必须开，否则「量得窄、画得窄」一起错。
    /// 标定来源：有头 Edge 的 span 宽度真值 + <c>CSS.getPlatformFontsForNode</c>。
    /// </summary>
    public static bool LacksRealBold(string family) =>
        family != null && NoBoldFamilies.Contains(family);

    static readonly System.Collections.Generic.HashSet<string> NoBoldFamilies =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            "NSimSun", "SimSun", "宋体", "SimSun-ExtB", "NSimSun-18030",
            "FangSong", "仿宋", "KaiTi", "楷体",
        };

    /// <summary>该实例是否为进程级共享字体（子集/默认）——调用方不得 Dispose。</summary>
    public static bool IsSharedTypeface(SKTypeface tf) =>
        ReferenceEquals(tf, _notoRegular) || ReferenceEquals(tf, _notoBold)
        || ReferenceEquals(tf, _yaheiRegular) || ReferenceEquals(tf, _yaheiBold)
        || ReferenceEquals(tf, _monoRegular)
        || ReferenceEquals(tf, SKTypeface.Default);

    // 常见 family 的跨平台别名表，尽量让同一份 CSS 在三大平台都能落到相似字形上。
    //
    // 通用族（serif / sans-serif / monospace / cursive / fantasy / math）这几条**不是**泛泛的别名候选，
    // 而是「参考浏览器把该关键字落到哪张物理字面」的标定，顺序就是优先级。改之前先用
    // `node tools/_genfonts.mjs` 问一次真值（它走 CDP 的 CSS.getPlatformFontsForNode，回的是 Blink
    // 真正拿去画字形的那张面），别拿 getComputedStyle 的 font-family 当答案——它只会原样吐回关键字。
    // 探针页里也务必写成**不带引号**的 `font-family:serif`：加上引号就成了一个不存在的自定义族名，
    // Blink 直接落到文档默认字体，量出来的「映射差」全是假信号（本轮就栽过一次）。
    // 本机（Windows + 装了 Noto Sans/Serif SC VF）实测：serif→Noto Serif SC、sans-serif→Noto Sans SC、
    // monospace→NSimSun、cursive→KaiTi、fantasy→Impact、math→Cambria Math、ui-*/emoji→Noto Sans SC、
    // system-ui→Microsoft YaHei UI。原先 serif 排在 Times New Roman（行盒 22 vs 浏览器 29、
    // 「ilTAVoWy0123 中文皮」@20 窄 10.76px），cursive/fantasy/math 则整族掉到表底的 Noto Sans SC 兜底。
    /// <summary>声明列表里一个名字（generic 关键字或别名族）对应的**候选链**，顺序即优先级。
    /// 逐字形回退判「这一项到底覆不覆盖该码位」必须走这条链：`monospace` 直接 Get 会拿到只含
    /// ASCII 的打包子集（96 字面，为压 PDF 体积而裁），它不覆盖汉字；而 Blink 把 monospace 落到
    /// 链首的真 NSimSun（CDP CSS.getPlatformFontsForNode 实证），覆盖。</summary>
    public static IReadOnlyList<string> CandidatesOf(string family) =>
        family != null && Fallbacks.TryGetValue(family, out var c) ? c : new[] { family };

    static readonly Dictionary<string, string[]> Fallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sans-serif"] = new[] { "Segoe UI", "Arial", "Helvetica Neue", "DejaVu Sans", "Noto Sans" },
        ["serif"] = new[] { "Noto Serif SC", "Times New Roman", "Times", "Georgia", "DejaVu Serif", "Noto Serif" },
        ["monospace"] = new[] { "NSimSun", "SimSun", "Consolas", "Courier New", "Menlo", "DejaVu Sans Mono", "Noto Sans Mono" },
        ["cursive"] = new[] { "KaiTi", "STKaiti", "Kaiti SC", "Bradley Hand", "Segoe Script" },
        ["fantasy"] = new[] { "Impact", "Haettenschweiler", "Arial Narrow Bold", "Papyrus" },
        ["math"] = new[] { "Cambria Math", "OpenSymbol", "Noto Sans Math", "STIX Two Math" },
        ["Microsoft YaHei"] = new[] { "Microsoft YaHei", "微软雅黑", "PingFang SC", "Noto Sans CJK SC", "Source Han Sans CN", "WenQuanYi Micro Hei" },
        ["微软雅黑"] = new[] { "Microsoft YaHei", "微软雅黑", "PingFang SC", "Noto Sans CJK SC" },
        ["SimSun"] = new[] { "SimSun", "宋体", "Songti SC", "Noto Serif CJK SC" },
        ["宋体"] = new[] { "SimSun", "宋体", "Songti SC", "Noto Serif CJK SC" },
        ["PingFang SC"] = new[] { "PingFang SC", "Microsoft YaHei", "Noto Sans CJK SC" },
        ["Helvetica"] = new[] { "Helvetica Neue", "Helvetica", "Arial", "DejaVu Sans" },
        ["Arial"] = new[] { "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans" },
    };

    /// <summary>
    /// 无真斜字面时**合成**斜体用的横向错切量（tan 14°）。浏览器真值（探针 _probe-it + canvas 逐行拟合竖笔）：
    /// 100px 与 400px 下 <c>italic</c> / <c>oblique</c> 的 | 斜率都是 0.24998 ≈ 0.25 整，
    /// 且 CDP <c>CSS.getPlatformFontsForNode</c> 报的字面就是正立那张 <c>Noto-Sans-SC</c> /
    /// <c>MicrosoftYaHei</c> —— 即浏览器也是合成的，不是找到了斜体面。
    /// 有真斜字面的族（Arial → <c>Arial-ItalicMT</c>、Georgia → <c>Georgia-Italic</c>）走字面选择，不合成。
    /// </summary>
    public const float SyntheticSkew = 0.25f;

    /// <summary>该 font-style 计算值是否要求斜体字面（选面用：italic 优先真斜体，oblique 优先 oblique 身）。</summary>
    public static SKFontStyleSlant SlantOf(string style) => style switch
    {
        "italic" => SKFontStyleSlant.Italic,
        "oblique" => SKFontStyleSlant.Oblique,
        var s when s.StartsWith("oblique ", StringComparison.Ordinal) => SKFontStyleSlant.Oblique,
        _ => SKFontStyleSlant.Upright,
    };

    /// <summary>
    /// 该 font-style 想要的错切量（0 = 正立）。**只按样式判**，落笔处还要看字面本身有没有斜身
    /// （<see cref="SkewFor"/> 才是最终口径）。
    /// 带角度的 <c>oblique &lt;deg&gt;</c> 只有 14deg（= 缺省角）会合成，别的角度浏览器保持正立：
    /// 实测 <c>oblique 12deg</c> / <c>20deg</c> / <c>-20deg</c> 的竖笔斜率都是 0，
    /// 而 <c>oblique</c> 与 <c>oblique 14deg</c> 都是 0.25（同一张正立字面，见 probe7/probe9）。
    /// </summary>
    public static float RequestedSkew(string style)
    {
        if (style == "italic" || style == "oblique") return SyntheticSkew;
        if (style.StartsWith("oblique ", StringComparison.Ordinal))
        {
            var a = style[8..];   // "oblique 14deg" → "14deg"
            if (a.EndsWith("deg", StringComparison.Ordinal)
                && float.TryParse(a[..^3], System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out var deg)
                && System.Math.Abs(deg - 14f) < 0.001f) return SyntheticSkew;
        }
        return 0f;
    }

    /// <summary>落笔字面在给定样式下实际该用的错切量：字面自带斜身就交给它，否则按样式合成。</summary>
    public static float SkewFor(string style, SKTypeface tf) => FaceIsSlanted(tf) ? 0f : RequestedSkew(style);

    // 字面实例 → 是否自带斜身。ConditionalWeakTable：字面回收条目跟着走，不钉住原生数据。
    sealed class SlantFlag(bool value) { public readonly bool Value = value; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKTypeface, SlantFlag> SlantedFace = new();

    /// <summary>
    /// 这张字面**自己**是不是斜体（真斜字面 / oblique 面）。
    /// 不能用 <see cref="SKTypeface.FontSlant"/>：Skia 在按样式向系统要面时，要不到就把**请求值**回填上去——
    /// 本机实测 <c>FromFamilyName("Noto Sans SC", Italic)</c> 报 <c>FontSlant=Oblique</c>，
    /// 而它的 <c>post.italicAngle=0</c>、fsSelection 也没斜体位，即拿回来的还是正立那张
    /// （浏览器同样如此：CDP 报的字面是正立的 <c>Noto-Sans-SC</c>，斜体靠合成）。
    /// 真斜字面（Arial Italic −12°、Georgia Italic −13°、Times Italic −16.3°）两项都留得下痕迹。
    /// </summary>
    public static bool FaceIsSlanted(SKTypeface? tf)
    {
        if (tf == null) return false;
        return SlantedFace.GetValue(tf, static t => new SlantFlag(Detect(t))).Value;

        static bool Detect(SKTypeface t)
        {
            try
            {
                // OS/2.fsSelection：bit0 ITALIC、bit1 OBLIQUE（偏移 62 处 u16）
                var os2 = t.GetTableData((uint)((0x4F << 24) | 0x53 << 16 | 0x2F << 8 | 0x32));
                if (os2 is { Length: >= 64 } && (BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(62)) & 0x3) != 0) return true;
                // post.italicAngle（Fixed 16.16，偏移 4）：非 0 即字身带倾角
                var post = t.GetTableData((uint)((0x70 << 24) | 0x6F << 16 | 0x73 << 8 | 0x74));
                return post is { Length: >= 8 } && BinaryPrimitives.ReadInt32BigEndian(post.AsSpan(4)) != 0;
            }
            catch { return false; }   // 读不到表 → 按正立处理，交给合成（宁可斜一点也不要整段正立）
        }
    }

    /// <summary>
    /// 按 family + 字重 + font-style 解析字面。斜体档只影响**系统回退**那条路（Arial/Georgia 有真斜体面，
    /// 交给 Skia 的就近匹配）；打包子集只有 Regular/Bold 两张正立面，浏览器对同一族也是拿正立面合成，
    /// 所以这里刻意不因斜体改换字面 —— 换成系统里的 Noto 斜体会与参考浏览器对不上。
    /// </summary>
    public static SKTypeface Get(string family, int weight, string style = "normal")
    {
        var scope = RenderScope.Current;

        // 1) 文档级 @font-face（请求私有）：缓存归 Scope，路径按 Scope.BaseDir 解析。
        // 只认 Scope 自己的注册表——GlobalRegistered（RegisterFontDirectory 装的本地字体）**不能**
        // 合并进这条分支：浏览器对安装字体照样吃 legacy kern，用 webfont 口径（仅自身 GPOS、无 PRKT）
        // 加载会把雅黑字面加宽，m4 的 .calc-box 因此多折一行。
        var faces = scope?.TryGetFaces(family);
        if (faces != null && scope != null)
        {
            var cached = scope.GetOrAddTypeface(family, weight, () => LoadRegistered(faces, weight, scope.BaseDir, webfont: true));
            if (cached != null) return cached;
        }
        else if (GlobalRegistered.TryGetValue(family, out var g) && !(weight <= 350 && YaheiSubsetFamilies.Contains(family)))
        {
            // 全局注册表：不缓存（文件命中很少走这条；避免注册态与缓存键耦合）。
            // 轻字重的雅黑例外——注册进来的只有 Regular/Bold 两档子集，会让 300 档吃 Regular，
            // 跳过注册表走进程级解析去匹配系统的「Microsoft YaHei Light」（见 ResolveFallback）。
            var tf = LoadRegistered(g, weight, scope?.BaseDir);
            if (tf != null) return tf;
        }

        // 2) 与注册态无关的回退：进程级缓存（斜体档要进键——系统回退按它就近匹配真斜字面）
        return Cache.GetOrAdd((family, weight, SlantOf(style)),
            k => ResolveFallback(k.Item1, k.Item2, k.Item3));
    }

    // 字面 → 该字体的纵向度量（相对 em）。按 SKTypeface 实例引用键控（Get 的结果本就带缓存），
    // 不同文档的 @font-face 同名不同面也不会串味；字面被回收时条目跟着走，不泄漏。
    sealed class VertMetrics(float ascent, float descent, float lineGap, float normalLh, float xHeight)
    {
        public readonly float Ascent = ascent, Descent = descent, LineGap = lineGap;
        public readonly float NormalLh = normalLh, XHeight = xHeight;
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKTypeface, VertMetrics> MetricsTable = new();

    static VertMetrics Of(SKTypeface t) => MetricsTable.GetValue(t, static t =>
    {
        using var f = new SKFont(t, 100f);
        var m = f.Metrics;
        float a = -m.Ascent / 100f, d = m.Descent / 100f;   // SKFontMetrics.Ascent 为负
        if (!(a > 0 && d >= 0) || float.IsNaN(a + d)) return new VertMetrics(0.8f, 0.2f, 0f, 1.4f, 0.5f);
        // **normal 行盒含字体建议行距 lineGap**（CSS Fonts 4 的 line-gap 语义）。原先按
        // 「Blink 的 normal 行盒 = 取整后的 ascent+descent」把它剔掉了，对 lineGap=0 的面（雅黑、
        // Noto Sans SC 子集）看不出差别，对带行距的面就整档偏：宋体/楷体 hhea 是 220/36/gap 36
        // （upm 256），剔掉 gap 后 20px 行盒 20 而浏览器 23，且 10 个字号逐点全偏（见 _probe-lh）。
        // 取整口径同 ascent/descent：**三个量各自取整再相加**，不是加完再取整——
        // 10.5px 下 9 + 1 + 1 = 11 才对得上（先加后取整会得 12）。
        float g = m.Leading / 100f;
        if (!(g >= 0) || float.IsNaN(g)) g = 0f;
        float lh = a + d + g;
        float xh = m.XHeight / 100f;
        return new VertMetrics(a, d, g, lh > 0 && !float.IsNaN(lh) ? lh : 1.4f, xh > 0.05f && xh < a ? xh : 0.5f);
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


    /// <summary>字面在指定字号下取整后的上下伸出量（口径同 <see cref="TextExtents"/>，只是已经拿到字面了）。</summary>
    public static (float Asc, float Desc) ExtentsAt(SKTypeface tf, float sizePx)
    {
        var v = Of(tf);
        return (Px(v.Ascent * sizePx), Px(v.Descent * sizePx));
    }

    /// <summary>
    /// <b>inline 字盒</b>的上下伸出量：按 CSS Fonts 4 的 normal 内容区口径，把字体的 hhea
    /// <c>lineGap</c> 当 half-leading 均摊到基线上下（<c>asc+g/2</c> / <c>desc+g/2</c>）。
    ///
    /// 为什么不能只用 asc+desc：宋体系 hhea 是 <c>220/36/gap 36</c>（upm 256），gap 占 14% em。
    /// 有头 Edge 实测 NSimSun 14.667px 的 span 高 16.5 = <code>(220+36+36)/256 × 14.667</code>，
    /// 而只取 asc+desc 给 15 —— 每行矮 1.5px 并按行往下累积：m1 整张表纵向漂到 4.3px、
    /// 每个含 <c>&lt;code&gt;</c> 的行 +0.47px（无头参照下这些全部被掩盖，见台账 P0-10）。
    /// </summary>
    public static (float Asc, float Desc) InlineExtentsAt(SKTypeface tf, float sizePx)
    {
        var v = Of(tf);
        float half = v.LineGap / 2f;
        if (!(half > 0f)) return (Px(v.Ascent * sizePx), Px(v.Descent * sizePx));
        return (Px((v.Ascent + half) * sizePx), Px((v.Descent + half) * sizePx));
    }

    static SKTypeface? _yaheiLight;
    static volatile bool _lightProbed;
    static readonly object LightLock = new();

    /// <summary>本机是否另装了「雅黑 Light」字面（只解析一次）。Skia 会把它的 FamilyName 归一化成
    /// "Microsoft YaHei"，所以只能按**字重**认出（请求 Light 档却拿回 ≤300 的实例才算数）。</summary>
    static SKTypeface? ResolveYaheiLight()
    {
        if (_lightProbed) return _yaheiLight;
        lock (LightLock)
        {
            if (_lightProbed) return _yaheiLight;
            var t = SKTypeface.FromFamilyName("Microsoft YaHei",
                new SKFontStyle(SKFontStyleWeight.Light, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
            if (t != null && t.FontWeight <= 300) _yaheiLight = t;
            else t?.Dispose();
            _lightProbed = true;
        }
        return _yaheiLight;
    }

    /// <summary>该 family 名是否就是雅黑（打包子集、历史别名、以及系统真面/真 Light 面）。</summary>
    internal static bool YaheiFamily(string family) =>
        YaheiSubsetFamilies.Contains(family) || family.Contains("YaHei", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 轻字重（CSS ≤300）中文在浏览器里交给「Microsoft YaHei Light」面，其 <c>normal</c> 行盒吃的是
    /// **提示后**的纵向度量：ascent 与该面的 usWinAscent 线性吻合，descent 却比字体表值（OS/2 的
    /// usWinDesc）取整后<b>恒多 1px</b>（10.5px：表 11/3=14，浏览器 11/4=15）。这条 +1 用任何字体表口径
    /// 都推不出来——<c>hhea</c> 给 2210/−514、canvas 的 fontBoundingBox 也只给 11/3，它出自 Blink 布局层
    /// 读到的 VDMX 提示度量（msyhl.ttc 带 VDMX，numRatios=1）。实测 27 个字号 × 4 族
    /// （<c>_probe-lw.html</c> + <c>tools/platfonts.mjs</c>）：Regular 与字体表口径 27/27 吻合，只有 Light
    /// 偏这一档；不补它则含轻体中文的每个行盒恒矮 1px，并沿页面向下累积（challenge 首行 14↔15 之源）。
    /// 返回 null = 本机没有这张面（mac/linux 的浏览器同样没有，维持子集口径）。
    /// 注意：**只取度量，不拿去绘制**——Light 面没有子集版，嵌进 PDF 会多出整份字体。
    /// </summary>
    public static (float Asc, float Desc)? LightCjkExtents(float sizePx)
    {
        var light = ResolveYaheiLight();
        if (light == null) return null;
        var (a, d) = ExtentsAt(light, sizePx);
        return sizePx >= 9.5f ? (a, d + 1) : (a, d);
    }

    static float Px(float v) => (float)Math.Floor(v + 0.5f);   // = SkScalarRoundToInt：half-up，不是 banker's

    /// <summary>该字族的 x-height 相对 em（vertical-align:middle 用）。</summary>
    public static float XHeightPerEm(string family, int weight) => Of(Get(family, weight)).XHeight;

    /// <summary>
    /// 该字族+字重的 <c>line-height: normal</c> 行距比 = 字体 ascent + descent + lineGap 相对 em。
    /// 只作 ratio 用；行盒高一律走 <see cref="NormalLineHeightPx"/>——浏览器是**逐度量取整后相加**，
    /// 不是「比值 × 字号」（13px 雅黑：round(13·0.9678)+round(13·0.3310)=13+4=17，而乘出来 16.88）。
    /// 统一按 1.4 或按不取整的比值兜底，会让每段未显式设行高的正文各差零点几 px，逐段累积成整页漂移。
    /// </summary>
    public static float NormalLineHeight(string family, int weight) => Of(Get(family, weight)).NormalLh;

    /// <summary><c>line-height: normal</c> 在该字号下的行盒高（px）= 取整后的 ascent + descent + lineGap。</summary>
    public static float NormalLineHeightPx(string family, int weight, float sizePx)
    {
        var v = Of(Get(family, weight));
        return Px(v.Ascent * sizePx) + Px(v.Descent * sizePx) + Px(v.LineGap * sizePx);
    }

    static SKTypeface? LoadRegistered((int weight, string[] paths)[] faces, int weight, string? baseDir, bool webfont = false)    {
        // 按字重就近取；同字重内按 src 声明顺序逐个尝试文件
        var pick = faces.OrderBy(f => Math.Abs(f.weight - weight)).First();
        foreach (var raw in pick.paths)
        {
            var full = ResolveFontPath(raw, baseDir);
            if (full == null || !File.Exists(full)) continue;
            try { var tf = FromFile(full, webfont); if (tf != null) return tf; }
            catch { /* 该候选损坏 → 试下一个 src */ }
        }
        return null;
    }

    static SKTypeface ResolveFallback(string family, int weight, SKFontStyleSlant slant = SKFontStyleSlant.Upright)
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
            // 轻字重（≤300）浏览器会换到系统另装的「微软雅黑 Light」面。**这里仍然返回子集 Regular**：
            // Light 没有子集版，拿去绘制会让 PDF 整嵌一份 ~17MB 字体。只借它的**行盒度量**，
            // 那条见 <see cref="LightCjkExtents"/>（由 TextShaper.LineExtents 在 normal 行高下按字重取用）。
            var regular = yahei ? _yaheiRegular : _notoRegular;
            var bold = yahei ? _yaheiBold : _notoBold;
            // Blink 字重级联：≥600 先向上找（子集只有 400/700 两档 → 600 落在 Bold，pricing 的 .t
            // CDP 探针实证 postScriptName=MicrosoftYaHei-Bold），≤500 先向下找（500 落 Regular）。
            var subset = weight >= 600 ? (bold ?? regular) : regular;
            // 斜体**不改这里**：子集只有正立的 Regular/Bold，浏览器对同一族也是拿正立面合成
            // （CDP getPlatformFontsForNode 实证 postScriptName=Noto-Sans-SC / MicrosoftYaHei），
            // 错切量在绘制端按 FontManager.SkewFor 补。
            if (subset != null) return subset;
        }

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
