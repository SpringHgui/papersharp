using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace PaperSharp.Layout;

/// <summary>
/// 复杂文字系统支持（M7）：HarfBuzz shaping（阿拉伯文连写等）、RTL 文本分段、字形级字体回退。
/// 门控原则：纯 ASCII / 已被当前字体覆盖的普通文本一律走原有 MeasureText 路径，保证既有文档像素零回归。
/// 已知限制：shaped run 的 letter-spacing 不逐字形加距；嵌套双向（RTL 中的整句引语）只做单层 run 反转。
/// </summary>
public static class TextShaper
{
    /// <summary>是否需要 HarfBuzz shaping（RTL 文字系统、组合符号、ZWJ 序列）。</summary>
    public static bool NeedsShaping(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (IsRtlCodepoint(c)) return true;
            if (c == '‍') return true; // ZWJ：emoji 家庭序列 / 阿拉伯连写强制符
            if (c is >= '\u0300' and <= '\u036F') return true; // 拉丁组合变音符
        }
        return false;
    }

    /// <summary>该文本是否需要特殊通路（shaping 或字形回退）。ASCII 恒为 false——保证既有像素路径零改动。</summary>
    public static bool NeedsSpecial(string text, SKTypeface tf)
        => NeedsShaping(text) || FirstUncovered(text, tf) != 0;

    /// <summary>码位是否强 RTL（阿拉伯/Hebrew 系文字）。数字/标点按中性处理，不在此列。</summary>
    public static bool IsRtlCodepoint(int cp) =>
        cp is >= 0x0590 and <= 0x07BF        // Hebrew..Thaana（含阿拉伯段）
        or >= 0x07C0 and <= 0x08FF           // NKo/阿拉伯补充
        or >= 0xFB1D and <= 0xFDFF
        or >= 0xFE70 and <= 0xFEFF;

    private static int DirOfCodepoint(int cp)
    {
        if (IsRtlCodepoint(cp)) return 1;
        if (cp > 0xFFFF) return 0; // 增补平面（emoji 等）按中性处理
        if (char.IsLetter((char)cp) || char.IsNumber((char)cp)) return -1; // 拉丁/CJK/假名/数字 → LTR
        return 0;                                  // 中性：空格/标点
    }

    // ── 字形级字体回退 ─────────────────────────────────────────────────

    static readonly ConcurrentDictionary<(string Family, int Weight, int Cp), SKTypeface?> FallbackCache = new();

    /// <summary>文本首处当前字体未覆盖的码位（0=全覆盖）。ASCII 码位跳过；组合符逐个字素内码位检查。</summary>
    public static int FirstUncovered(string text, SKTypeface tf)
    {
        foreach (var g in Graphemes(text))
            for (int i = 0; i < g.Length;)
            {
                int cp = char.ConvertToUtf32(g, i);
                i += char.IsHighSurrogate(g[i]) ? 2 : 1;
                if (cp < 0x80) continue;
                if (tf.GetGlyph(cp) == 0) return cp;
            }
        return 0;
    }

    static bool IsArabicCodepoint(int cp) =>
        cp is >= 0x0600 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF;

    /// <summary>CJK 子集面不画阿拉伯文——真雅黑/宋体的 cmap 虽有字形，但 OpenType 不申报 arab 脚本，
    /// Blink 逐字回退照样落到 Tahoma（m7 经 CDP getPlatformFontsForNode 实测：Tahoma 68 形 / YaHei 27 形）。
    /// 子集派生自同一 cmap，<see cref="FirstUncovered"/> 的覆盖检查看不出这个缺口，必须按 family 显式改写。</summary>
    static int FirstArabicNeedingSystem(string text, SKTypeface tf)
    {
        if (!FontManager.CjkSubsetFamily(tf.FamilyName)) return 0;
        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
            if (IsArabicCodepoint(cp)) return cp;
        }
        return 0;
    }

    static SKTypeface? SystemArabicFace(int cp)
    {
        var t = SKTypeface.FromFamilyName("Tahoma");
        if (t != null && t.GetGlyph(cp) != 0) return t;
        t?.Dispose();
        return null;
    }

    // 码位 → 实际落点字面（null = 就用请求的那张，同时作负缓存）。键与 FallbackCache 同构。
    static readonly ConcurrentDictionary<(string Family, int Weight, int Cp), SKTypeface?> CpFaceCache = new();

    static SKTypeface? FaceFor(int cp, SKTypeface current)
        => CpFaceCache.GetOrAdd((current.FamilyName, current.FontWeight, cp),
            k =>
            {
                var f = ResolveTypeface(char.ConvertFromUtf32(k.Cp), current);
                return ReferenceEquals(f, current) ? null : f;
            });

    /// <summary>
    /// 一段文字对**行盒**的上下伸出量：除了主字体，逐字回退到的那张 face 也按自己的度量参与撑行盒，
    /// 上下各取最大值（CSS 2.1 §10.8 的并集，与浏览器一致）。
    /// 浏览器实测（探针 _probe-bl/_probe-fb，13px Arial）：纯拉丁 12/3=15，掺一个中文即 15/4=19，
    /// 中文在句首、句中、句尾都一样；<b>但元素自己的行高盒/背景盒仍是主字体的 15</b>
    /// （混合行里的 span 盒高恒 15，行盒 19 时它落在基线上方 12 处）。
    /// 因此这条只喂行盒并集，不喂 <see cref="InlineFrag.Ascent"/>；且只在 <c>line-height: normal</c> 下生效——
    /// 显式行高时浏览器按主字体定基线（13px Arial / 行高 27 掺中文：基线 18 = 12+⌊(27−15)/2⌋，不吃回退的 15）。
    /// <paramref name="familyList"/> 是声明的整条 font-family：浏览器逐字回退时**先沿这条列表往下找**，
    /// 找到覆盖的字面就用它（challenge 的 <c>"Segoe UI","Microsoft YaHei",…</c> 中文由第 2 项雅黑承接，
    /// 行盒 14 不变；只有列表全都不覆盖时才落到 generic 系统回退，那里才是 Noto 的 15/4=19）。
    /// </summary>
    public static (float Asc, float Desc) LineExtents(string text, SKTypeface primary, float sizePx,
        string familyList, int weight, string style = "normal")
    {
        var light = weight <= 300 ? FontManager.LightCjkExtents(sizePx) : null;
        var (a, d) = light != null && FontManager.YaheiFamily(primary.FamilyName)
            ? light.Value
            : FontManager.InlineExtentsAt(primary, sizePx);
        if (text.Length == 0) return (a, d);
        SKTypeface? last = null;
        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
            if (cp < 0x80) continue;   // ASCII 永不回退（FirstUncovered 同口径）
            var face = FaceFromList(familyList, weight, cp, style) ?? FaceFor(cp, primary);
            if (face == null || ReferenceEquals(face, last)) continue;
            last = face;
            // 轻字重 + 雅黑族：浏览器用的是「雅黑 Light」那张面，行盒按它的度量长（见 LightCjkExtents），
            // 不是子集 Regular 的——引擎画面仍用子集，度量得换成 Light 那一档。
            var (x, y) = light != null && FontManager.YaheiFamily(face.FamilyName)
                ? light.Value
                : FontManager.InlineExtentsAt(face, sizePx);
            if (x > a) a = x;
            if (y > d) d = y;
        }
        return (a, d);
    }

    // 声明列表里的某族 → 该码位的落点字面（null = 整条列表都不覆盖，交给 generic 回退）。
    static readonly ConcurrentDictionary<(string List, int Weight, int Cp, string Style), SKTypeface?> ListFaceCache = new();

    static SKTypeface? FaceFromList(string familyList, int weight, int cp, string style = "normal")
    {
        // 单项列表也要走（原来在这里直接 return null）：`font-family: monospace` 一项时，
        // 主字面是 ASCII 子集、汉字不覆盖，同样要沿 generic 的候选链往下找真 NSimSun ——
        // 与 Chrome 一致（探针 _probe-mono：纯 monospace 的 13px 中文行盒 Chrome 15、引擎 19）。
        if (string.IsNullOrEmpty(familyList)) return null;
        return ListFaceCache.GetOrAdd((familyList, weight, cp, style), k =>
        {
            foreach (var name in SplitFamilies(k.List))
            {
                // 一项 generic/别名族要沿它自己的候选链找覆盖。`monospace` 直接 Get 拿到的是只含
                // ASCII 的打包子集（96 字面），汉字判不覆盖 → 整条列表走空 → 掉到 generic 系统回退
                // （Noto 15/4=19），而 Blink 在这一项就落到链首的真 NSimSun（13px 行盒 15）。
                // m7 表头 `.num{font-family:"Consolas",monospace}` 那 2px 行盒差即此
                // （tools/platfonts.mjs 走 CDP getPlatformFontsForNode 实证 Chrome 用 NSimSun×12 + Consolas×3）。
                foreach (var cand in FontManager.CandidatesOf(name))
                {
                    var tf = FontManager.Get(cand, k.Weight, k.Style);
                    if (tf != null && tf.GetGlyph(k.Cp) != 0) return tf;
                }
            }
            return null;
        });
    }

    /// <summary>拆 font-family 列表（去引号、按逗号分段；带逗号的引号族名不管）。</summary>
    static IEnumerable<string> SplitFamilies(string list)
    {
        int i = 0;
        while (i < list.Length)
        {
            int comma = list.IndexOf(',', i);
            if (comma < 0) comma = list.Length;
            var name = list[i..comma].Trim().Trim('"', '\'');
            if (name.Length > 0) yield return name;
            i = comma + 1;
        }
    }

    /// <summary>为文本挑一个尽量全覆盖的字体：从当前字体出发，逐处缺字位换候选（子集优先、系统 MatchCharacter 兜底），
    /// 迭代至覆盖或达上限。胜者由 FallbackCache 永久持有（进程级，不随 Scope 释放）。失败返回原字体。</summary>
    /// <summary>逐字素回退（绘制端）：先沿**声明的 font-family 列表**找覆盖该码位的字面，
    /// 整条列表都不覆盖才交给系统 generic 回退。
    /// 度量端 <see cref="LineExtents"/> 一直吃的是列表，绘制端以前只拿主面去问系统 ——
    /// 于是 <c>.num{font-family:"Consolas",monospace}</c> 里的汉字被系统回退成**黑体**，
    /// 而 Blink 在 `monospace` 这一项就落到候选链首的 NSimSun（**宋体**）。
    /// 同一段字两张面、位置还错开，Playground 叠加视图里那几列最响的重影就是它。</summary>
    public static SKTypeface ResolveTypeface(string text, SKTypeface current,
        string? familyList, int weight, string style = "normal")
    {
        if (!string.IsNullOrEmpty(familyList))
        {
            int cp = FirstUncovered(text, current);
            if (cp != 0)
            {
                var face = FaceFromList(familyList, weight, cp, style);
                if (face != null) return face;
            }
        }
        return ResolveTypeface(text, current);
    }

    public static SKTypeface ResolveTypeface(string text, SKTypeface current)
    {
        int cp = FirstUncovered(text, current);
        if (cp == 0) cp = FirstArabicNeedingSystem(text, current);
        if (cp == 0) return current;
        var key = (current.FamilyName, current.FontWeight, cp);
        var hit = FallbackCache.GetOrAdd(key, k =>
        {
            try
            {
                var tf = current;
                SKTypeface? owned = null; // 本循环 MatchCharacter 出来的新实例（非共享，需管理释放）
                for (int guard = 0; guard < 4; guard++)
                {
                    int miss = FirstUncovered(text, tf);
                    if (miss == 0) miss = FirstArabicNeedingSystem(text, tf);
                    if (miss == 0) return ReferenceEquals(tf, current) ? null : tf; // 胜者进缓存，不释放
                    var next = (IsArabicCodepoint(miss) ? SystemArabicFace(miss) : null)
                            ?? FontManager.SubsetIfCovers(miss, k.Weight)
                            ?? SKFontManager.Default.MatchCharacter(miss);
                    bool ownedNext = next != null && !FontManager.IsSharedTypeface(next) && !ReferenceEquals(next, current);
                    if (next == null || next.GetGlyph(miss) == 0 || ReferenceEquals(next, tf))
                    {
                        if (ownedNext) next?.Dispose();
                        owned?.Dispose();
                        return null; // 无法再改善（含负缓存）
                    }
                    owned?.Dispose();
                    owned = ownedNext ? next : null;
                    tf = next;
                }
                owned?.Dispose();
                return null;
            }
            catch { return null; }
        });
        return hit ?? current;
    }

    /// <summary>generic <c>monospace</c> 候选链上第一个覆盖该码位的字面（本机 = NSimSun）。
    /// 度量端拿不到声明列表时的默认落点，与 Blink 对 generic 关键字的解析同规则。</summary>
    static SKTypeface? MonoGenericFace(int weight, int cp)
    {
        foreach (var cand in FontManager.CandidatesOf("monospace"))
        {
            var t = FontManager.Get(cand, weight);
            if (t != null && t.GetGlyph(cp) != 0) return t;
        }
        return null;
    }

    // ── 度量与绘制 ─────────────────────────────────────────────────────

    // ── 等宽链的整像素 advance 网格 ─────────────────────────────────────
    // 参考浏览器（Blink + DirectWrite）渲染 legacy 等宽 face（NSimSun，Blink 的 generic monospace 落点）时
    // 不对 advance 做线性缩放，而是网格对齐到整「半格」：半宽字形 = ⌈fs/2⌉，全宽（CJK/全角）= 2×⌈fs/2⌉，
    // 合成粗体（NSimSun 无粗体面）每字再 +1px。实测 12~17px 恒成立、≥18px 起回到线性。
    // 14.6667px 的 <code> 每字 8px（hmtx 线性只有 7.333px → 整串窄 12/11，m1 六个 code 盒各差 12~24px）；
    // 13.3333px 的 .num 表头「序号」粗体 = 2×15 = 30（引擎按 Noto 兜底只给 26.67 → 该列最大内容宽少 3.33px，
    // 自动表格布局按比例摊到别列，整张表 357 个格 dx≈5）。
    // Consolas / Courier New 这类真等宽 face 自身仍是线性（实测各字号恒 0.5498em），只有它们覆盖不到、
    // 由 CSS family 链兜到 generic monospace 的码位才落进这张网格。
    const float MonoFitMaxPx = 18f;

    /// <summary>等宽网格口径下的整串宽（含 letter-spacing）。不适用该口径时返回 -1，由调用方走线性通路。</summary>
    public static float MonoGridWidth(string text, SKTypeface tf, float size, float ls,
        string list = "", int weight = 0)
    {
        if (string.IsNullOrEmpty(text) || size >= MonoFitMaxPx || NeedsShaping(text)) return -1;
        bool legacy = FontManager.IsLegacyMonoFace(tf);
        if (!legacy && (!tf.IsFixedPitch || FirstUncovered(text, tf) == 0)) return -1;

        float half = (float)Math.Ceiling(size / 2f);
        int fauxBold = tf.FontWeight >= 700 ? 1 : 0;
        // 真等宽 face 自己覆盖的码位：全字符同一 advance，量一次就够
        float own = 0;
        if (!legacy)
        {
            for (int i = 0; i < text.Length;)
            {
                int cp = char.ConvertToUtf32(text, i);
                i += char.IsHighSurrogate(text[i]) ? 2 : 1;
                if (tf.GetGlyph(cp) == 0) continue;
                using var paint = new SKPaint { Typeface = tf, TextSize = size };
                own = paint.MeasureText(char.ConvertFromUtf32(cp));
                break;
            }
        }

        float w = 0;
        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
            // 全宽判定：CJK/全角之外，**legacy 等宽网格**（Chrome generic monospace 落 NSimSun）还要把
            // UAX#11 的 A 类也算全宽——Ω/±/°/→/Ⅲ/А/π/≥ 在 NSimSun 下浏览器实测都是全宽单元，
            // 按 IsCjkChar 只给半格会让每个 A 类字符少算一格（14px 的 Ω：7 vs 14）。
            // 严格限定 legacy：非 legacy（真等宽 face 线性 own advance / 缺字回退）不参与，
            // 雅黑/Noto/Arial 下的 A 类不让位（逐 face 单字对拍已证两边本就吻合）。
            bool wide = cp is > 0 and <= 0xFFFF
                && (TextMeasurer.IsCjkChar((char)cp) || (legacy && EastAsianWidth.IsAmbiguous(cp)));
            // 主面自己覆盖得住的码位，就地定宽：非 legacy 用它线性 hmtx，legacy 用半格网格。
            // 以前 legacy 也要往下走「问回退面要真 advance」那条路，而 NSimSun 覆盖 ASCII，
            // 回退面就是它自己 ⇒ 量到的永远是 0.5em 线性，网格分支一次也没执行过
            // （11pt 的 <code> 每字 7.336 而非 8，m1 六处 code 盒各窄 12~24px，整张表列宽跟着摊歪）。
            bool covered = tf.GetGlyph(cp) != 0;
            if (covered && (!legacy || !wide)) { w += legacy ? half + fauxBold : own; continue; }
            // 主面不覆盖的码位（等宽链里的 CJK/全角）：走**回退字面的真实 advance**，含合成粗体加宽。
            // 半格网格（2×⌈fs/2⌉ + 粗体每字 1）在这条路上是错的：有头 Edge 实测 bold 13.328125px、
            // `Consolas,monospace` 下「序号」= 27.172（13.586/字 = 1em × 合成粗体加宽 1.0194），
            // 网格给 30 —— 多出的 2.83px 经自动表格分配放大成 5.78px 的列错位，
            // 叠加视图里就是「左半整表糊、右半锐」那条斜线（无头参照当初没暴露它）。
            var gs = char.ConvertFromUtf32(cp);
            // 度量端不一定拿得到声明列表（WidthOf/MonoGridWidth 的老签名只有主面）。主面是等宽/legacy 子集时，
            // Chrome 对不覆盖的码位会沿 generic `monospace` 的候选链落（本机 = NSimSun），
            // 而不是引擎默认的 generic 回退（Noto Sans SC）——两张面 advance 差 2%，
            // 经自动表格分配放大成 5.78px 的列错位。给了列表就严格走列表（与绘制端同一条 FaceFromList）。
            var fb = !string.IsNullOrEmpty(list)
                ? ResolveTypeface(gs, tf, list, weight > 0 ? weight : tf.FontWeight)
                : (tf.IsFixedPitch || legacy
                    ? MonoGenericFace(weight > 0 ? weight : tf.FontWeight, cp) ?? ResolveTypeface(gs, tf)
                    : ResolveTypeface(gs, tf));
            if (fb != null && fb.GetGlyph(cp) != 0)
            {
                using var fp = new SKPaint
                {
                    Typeface = fb, TextSize = size, IsAntialias = true, SubpixelText = true,
                    FakeBoldText = tf.FontWeight >= 700 && FontManager.LacksRealBold(fb.FamilyName),
                };
                w += fp.MeasureText(gs);
                continue;
            }
            w += (wide ? half * 2 : half) + fauxBold;
        }
        return w + ls * GraphemeCount(text);
    }

    /// <summary>统一宽度：shaped run 用 HarfBuzz advance（字距对连写 run 不加 ls，见类注释）；否则走老公式。</summary>
    public static float WidthOf(string text, SKTypeface tf, float size, float ls,
        string list = "", int weight = 0)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        float mono = MonoGridWidth(text, tf, size, ls, list, weight);
        if (mono >= 0) return mono;
        if (NeedsShaping(text))
        {
            var typeface = ResolveTypeface(text, tf);
            var segs = MixedNeutralSegments(text, tf, typeface);
            if (segs != null) return SegWidths(segs, typeface, tf, size).Sum();
            using var paint = new SKPaint { Typeface = typeface, TextSize = size };
            using var shaper = new SKShaper(typeface);
            var res = shaper.Shape(text, paint);
            return res?.Width ?? paint.MeasureText(text);
        }
        // 按「哪个字面覆盖它」切段分别量：整串锁一个回退面会把混合行里的拉丁段也交给 CJK 字体，
        // advance 差 0.1~0.5em（"abc 中文" @9px 多 1.4px → flex 项内在宽、右/中对齐的 textW 一起偏），
        // 而绘制端 DrawSpaced 本就是逐字素换面的——量得比画出来的宽。
        float w = 0; int units = 0;
        foreach (var (seg, face) in FaceRuns(text, tf))
        {
            using var p = new SKPaint { Typeface = face, TextSize = size };
            w += p.MeasureText(seg) + KernTable.Of(seg, face, size);
            units += GraphemeCount(seg);
        }
        return w + ls * units;
    }

    /// <summary>Chrome 在 shaped RTL run 里对 ASCII 中性字素（空格/冒号/数字）仍用**请求面**，
    /// 只有脚本字才落系统面（m7 探针：冒号雅黑 3.14px、Tahoma 4.59px，整串宽因此差 1.5px）。
    /// SkiaSharp 单 shaper 混不了两张面 → 按「全 ASCII 字素 / 其余」交替切段分开量画；
    /// 中性字不参与连写，切开不改变阿拉伯文造型。返回 null = 不需要切（同面或全一类）。</summary>
    static List<(string Seg, bool Primary)>? MixedNeutralSegments(string text, SKTypeface tf, SKTypeface resolved)
    {
        if (ReferenceEquals(tf, resolved)) return null;
        List<(string Seg, bool Primary)>? segs = null;
        var sb = new StringBuilder();
        bool cur = false;
        foreach (var g in Graphemes(text))
        {
            bool ascii = true;
            for (int i = 0; i < g.Length;)
            {
                int cp = char.ConvertToUtf32(g, i);
                i += char.IsHighSurrogate(g[i]) ? 2 : 1;
                if (cp >= 0x80) { ascii = false; break; }
            }
            if (sb.Length > 0 && ascii != cur) { (segs ??= new()).Add((sb.ToString(), cur)); sb.Clear(); }
            cur = ascii;
            sb.Append(g);
        }
        if (sb.Length > 0) (segs ??= new()).Add((sb.ToString(), cur));
        return segs is { Count: > 1 } ? segs : null;
    }

    /// <summary>
    /// 混合方向行的逐段宽度：<b>度量端 <see cref="WidthOf"/> 与绘制端 <see cref="DrawShaped"/> 共用这一份</b>，
    /// 所以两边必须同口径——主面段是普通字符串，Skia/HarfBuzz 都不替我们加字距，得自己补
    /// <see cref="KernTable.Of"/>（系统面段走 shaped，字距已在 HarfBuzz 的 advance 里，不能再加）。
    /// </summary>
    static float[] SegWidths(List<(string Seg, bool Primary)> segs, SKTypeface sys, SKTypeface pri, float size)
    {
        var ws = new float[segs.Count];
        using var pPri = new SKPaint { Typeface = pri, TextSize = size };
        using var pSys = new SKPaint { Typeface = sys, TextSize = size };
        using var shSys = new SKShaper(sys);
        for (int i = 0; i < segs.Count; i++)
        {
            var (seg, primary) = segs[i];
            if (primary) ws[i] = pPri.MeasureText(seg) + KernTable.Of(seg, pri, size);
            else
            {
                var res = shSys.Shape(seg, pSys);
                ws[i] = res?.Width ?? pSys.MeasureText(seg);
            }
        }
        return ws;
    }

    /// <summary>切成「同一字面」的连续段（以字素为界，与绘制端逐字素回退同口径）。</summary>
    static IEnumerable<(string Seg, SKTypeface Face)> FaceRuns(string text, SKTypeface tf)
    {
        var sb = new StringBuilder();
        SKTypeface cur = tf;
        foreach (var g in Graphemes(text))
        {
            var gt = ResolveTypeface(g, tf);
            if (sb.Length > 0 && !ReferenceEquals(gt, cur)) { yield return (sb.ToString(), cur); sb.Clear(); }
            cur = gt;
            sb.Append(g);
        }
        if (sb.Length > 0) yield return (sb.ToString(), cur);
    }

    /// <summary>Shaped 整段绘制（连写形式正确）。返回是否消费了该文本。</summary>
    public static bool DrawShaped(SKCanvas canvas, string text, float x, float y, SKPaint paint)
    {
        var primary = paint.Typeface!;
        var typeface = ResolveTypeface(text, primary);
        bool swapped = !ReferenceEquals(typeface, primary);
        var segs = swapped ? MixedNeutralSegments(text, primary, typeface) : null;
        var old = paint.Typeface;
        if (swapped) paint.Typeface = typeface;
        try
        {
            if (segs == null)
            {
                using var shaper = new SKShaper(typeface);
                Pdf.PdfGlyphUsage.DrawShaped(canvas, shaper, text, x, y, paint);
                return true;
            }
            // 视觉序：RTL run 里逻辑首段贴右 → 从左到右画的是段的倒序（中性字素不连写，段内造型不受切割影响）
            var ws = SegWidths(segs, typeface, primary, paint.TextSize);
            float cursor = x;
            for (int k = 0; k < segs.Count; k++)
            {
                int i = StrongDir(text) == 1 ? segs.Count - 1 - k : k;
                var (seg, isPrimary) = segs[i];
                if (isPrimary)
                {
                    paint.Typeface = primary;
                    // 与 SegWidths 同口径：段内遇非零字距对要切开落笔，否则配对之后的字形整体右偏
                    Pdf.PdfGlyphUsage.DrawKerned(canvas, seg, cursor, y, paint);
                    paint.Typeface = typeface;
                }
                else
                {
                    using var shaper = new SKShaper(typeface);
                    Pdf.PdfGlyphUsage.DrawShaped(canvas, shaper, seg, cursor, y, paint);
                }
                cursor += ws[i];
            }
            return true;
        }
        catch
        {
            return false; // 失败回退普通绘制
        }
        finally
        {
            paint.Typeface = old;
        }
    }

    // ── 轻量 bidi（UAX#9 简化）─────────────────────────────────────────

    /// <summary>
    /// 把逻辑序字符串切成强方向 run（Dir: 1=RTL，-1=LTR）。中性段并入前一个 run（句首中性并入后一个）。
    /// baseRtl 时调用方将 run 序反转得到视觉序。嵌套双向不处理——已知限制。
    /// </summary>
    public static List<(string Text, int Dir)> BidiRuns(string text)
    {
        var runs = new List<(string, int)>();
        if (text.Length == 0) return runs;

        // 1) 逐字素标注方向
        var elems = new List<(string G, int Dir)>(text.Length);
        foreach (var g in Graphemes(text)) elems.Add((g, DirOfGrapheme(g)));

        // 2) 中性归属：前强已出现 → 归前；否则暂存（句首中性归后面的强方向 run 之前文本）
        string? leading = null;
        var sb = new StringBuilder();
        int cur = 0;
        void Flush() { if (sb.Length > 0) { runs.Add((sb.ToString(), cur == 0 ? -1 : cur)); sb.Clear(); } }
        foreach (var (g, dir) in elems)
        {
            if (dir == 0)
            {
                if (cur == 0) { leading ??= ""; leading += g; } // 还没有强方向 run：攒进前导中性
                else sb.Append(g);                              // 中性归前 run
                continue;
            }
            if (cur == 0)
            {
                if (leading != null) sb.Append(leading); // 前导中性归第一个 run
                cur = dir;
            }
            else if (dir != cur) { Flush(); cur = dir; leading = null; }
            sb.Append(g);
        }
        Flush();
        if (runs.Count == 0 && leading != null) runs.Add((leading, -1)); // 全文中性
        return runs;
    }

    /// <summary>文本的首个强方向（1=RTL，-1=LTR，0=全文中性）。供 inline 行盒 RTL 重排用。</summary>
    public static int StrongDir(string text)
    {
        foreach (var g in Graphemes(text))
        {
            int d = DirOfGrapheme(g);
            if (d != 0) return d;
        }
        return 0;
    }

    static int DirOfGrapheme(string g)
    {
        for (int i = 0; i < g.Length; i++)
        {
            int cp = char.ConvertToUtf32(g, i);
            if (char.IsHighSurrogate(g[i])) i++; // 合成码位占两个 UTF-16 槽位
            int d = DirOfCodepoint(cp);
            if (d != 0) return d;
        }
        return 0;
    }

    // ── 字素工具 ───────────────────────────────────────────────────────

    /// <summary>扩展字素簇序列（emoji ZWJ 序列 / 组合符号不拆散）。ASCII 快路径退化为单字符。</summary>
    public static IEnumerable<string> Graphemes(string s)
    {
        if (s.Length == 0) yield break;
        if (AllAscii(s)) // 快路径：纯 ASCII 逐字符
        {
            foreach (var c in s) yield return c.ToString();
            yield break;
        }
        var en = StringInfo.GetTextElementEnumerator(s);
        while (en.MoveNext()) yield return (string)en.Current;
    }

    public static int GraphemeCount(string s)
    {
        if (AllAscii(s)) return s.Length;
        int n = 0;
        var en = StringInfo.GetTextElementEnumerator(s);
        while (en.MoveNext()) n++;
        return n;
    }

    static bool AllAscii(string s)
    {
        for (int i = 0; i < s.Length; i++) if (s[i] >= 0x80) return false;
        return true;
    }
}
