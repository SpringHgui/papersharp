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

    /// <summary>为文本挑一个尽量全覆盖的字体：从当前字体出发，逐处缺字位换候选（子集优先、系统 MatchCharacter 兜底），
    /// 迭代至覆盖或达上限。胜者由 FallbackCache 永久持有（进程级，不随 Scope 释放）。失败返回原字体。</summary>
    public static SKTypeface ResolveTypeface(string text, SKTypeface current)
    {
        int cp = FirstUncovered(text, current);
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
                    if (miss == 0) return ReferenceEquals(tf, current) ? null : tf; // 胜者进缓存，不释放
                    var next = FontManager.SubsetIfCovers(miss, k.Weight)
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
    public static float MonoGridWidth(string text, SKTypeface tf, float size, float ls)
    {
        if (string.IsNullOrEmpty(text) || size >= MonoFitMaxPx || NeedsShaping(text)) return -1;
        bool legacy = FontManager.IsLegacyMonoSubset(tf);
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
            if (!legacy && tf.GetGlyph(cp) != 0) { w += own; continue; }
            w += (wide ? half * 2 : half) + fauxBold;
        }
        return w + ls * GraphemeCount(text);
    }

    /// <summary>统一宽度：shaped run 用 HarfBuzz advance（字距对连写 run 不加 ls，见类注释）；否则走老公式。</summary>
    public static float WidthOf(string text, SKTypeface tf, float size, float ls)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        float mono = MonoGridWidth(text, tf, size, ls);
        if (mono >= 0) return mono;
        if (NeedsShaping(text))
        {
            var typeface = ResolveTypeface(text, tf);
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
        var typeface = ResolveTypeface(text, paint.Typeface!);
        bool swapped = !ReferenceEquals(typeface, paint.Typeface);
        var old = paint.Typeface;
        if (swapped) paint.Typeface = typeface;
        try
        {
            using var shaper = new SKShaper(typeface);
            Pdf.PdfGlyphUsage.DrawShaped(canvas, shaper, text, x, y, paint);
            return true;
        }
        catch
        {
            return false; // 失败回退普通绘制
        }
        finally
        {
            if (swapped) paint.Typeface = old;
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
