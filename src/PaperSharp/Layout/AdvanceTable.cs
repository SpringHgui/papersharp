using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace PaperSharp.Layout;

/// <summary>
/// 托管推进缓存：4.x 里 <see cref="SKFont.MeasureText(string)"/> 每次都要进原生 strike cache 的
/// 全局互斥锁，并行渲染下 4 线程量字比串行慢 60~92 倍（微基准实测，每线程私有 SKFont 也一样）；
/// 而 <see cref="SKFont.GetGlyphs(ReadOnlySpan{int}, Span{ushort})"/> 只查 cmap、不进那把锁
/// （4 线程恒定 ~13ns/次）。量宽全部改走这里：批量取字形 → 逐字形原生取宽**一次性按批**
/// （<see cref="SKFont.GetGlyphWidths(ReadOnlySpan{ushort}, SKPaint)"/>）缓存到 (字面,字号,口径)
/// 的托管表 → 之后纯托管查表求和，全程不再逐串碰原生。
///
/// 两条口径对应修复前（A0）MeasureWith 的两条老路，逐字形值与原生 MeasureText 全等
/// （60 张系统字面 × 14 字号 × 全字符探针 132398/132398 零失配，打包子集面同验）：
/// - <see cref="Sum"/>：AA 口径（2.88 的 Subpixel=true 池化 font / 4.x 的 Edging=Antialias）=
///   线性 hmtx advance。对普通字面如此，但**打包子集面不成立**：NSimSunSub（upem=256、全字形 1em）
///   在 AA 下量出 13.58844px（13.328125px 字号的 1.0195 倍），hmtx 直算只有 13.328125——Skia 对
///   这类面按 hinting/gasp 改了推进，所以原生取宽不能省。Subpixel/Embolden/SkewX 旗对 advance
///   零影响（逐旗逐面实测）。
/// - <see cref="SumAliased"/>：Alias 口径（2.88 的 Subpixel=false / 4.x 的 Edging=Alias）=
///   逐字形**网格对齐的整数推进**（TrueType 提示后的 advance，与 DirectWrite 同值）——这从来不是
///   偏差：布局端量宽一直用它，对拍 Chrome 的金标就是按它生成的。该值不可从 hmtx 推出
///   （12/320 探针里分数部分低至 0.38 也向上取整），只能原生批量取。
///
/// 求和按左到右逐字形 float 累加（与 Skia measureText 同序），整串值与原生 MeasureText 逐位全等
/// （72/72 探针零差）。
///
/// 缓存按字面实例引用键控（ConditionalWeakTable），字面被 FontManager LRU 逐出时条目跟着走，
/// 既不泄漏也不钉住原生数据。暖机后量宽全程零锁：ConcurrentDictionary 读无锁，
/// Gate 只串行化「新字形批量原生取」。
/// </summary>
public static class AdvanceTable
{
    // 键 = 字面 × 字号 × 口径：Alias/AA 推进只取决于这三者（SkewX/Embolden/Subpixel 取默认值 =
    // A0 量宽 font 的同配置）。值 = gid → 推进宽。
    sealed class Widths
    {
        public readonly ConcurrentDictionary<int, float> W = new();
        public readonly object Gate = new();
    }
    static readonly ConditionalWeakTable<SKTypeface, ConcurrentDictionary<(float Size, bool Aliased), Widths>> Cache = new();

    [ThreadStatic] static SKFont? tFont;
    [ThreadStatic] static int[]? tCps;
    [ThreadStatic] static ushort[]? tGids;

    /// <summary>整串推进宽度（AA 口径，与修复前 AA 字体量宽同值）。空串→0，空字面按 Default 面。</summary>
    public static float Sum(SKTypeface? tf, float size, string text)
        => Native(tf, size, text, aliased: false);

    /// <summary>整串推进宽度（Alias 口径，与修复前 Edging=Alias / 2.88 Subpixel=false 量宽同值）。</summary>
    public static float SumAliased(SKTypeface? tf, float size, string text)
        => Native(tf, size, text, aliased: true);

    static float Native(SKTypeface? tf, float size, string text, bool aliased)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        tf ??= SKTypeface.Default;
        var widths = Cache.GetValue(tf, static _ => new ConcurrentDictionary<(float Size, bool Aliased), Widths>())
            .GetOrAdd((size, aliased), static _ => new Widths());
        var w = widths.W;
        var ids = RentGids(text.Length);
        int n = FillGids(tf, text, ids);
        Span<int> miss = n <= 256 ? stackalloc int[256] : new int[n];
        int m = 0;
        for (int i = 0; i < n; i++)
            if (!w.ContainsKey(ids[i])) miss[m++] = ids[i];
        if (m > 0)
        {
            lock (widths.Gate)
            {
                int m2 = 0;
                for (int i = 0; i < m; i++)
                    if (!w.ContainsKey(miss[i])) miss[m2++] = miss[i];
                m = m2;
                if (m > 0)
                {
                    var gs = new ushort[m];
                    for (int i = 0; i < m; i++) gs[i] = (ushort)miss[i];
                    using var f = new SKFont(tf, size) { Edging = aliased ? SKFontEdging.Alias : SKFontEdging.Antialias };
                    var ws = f.GetGlyphWidths(gs, null);
                    for (int i = 0; i < m; i++) w[miss[i]] = ws[i];
                }
            }
        }
        float sum = 0;
        for (int i = 0; i < n; i++) sum += w[ids[i]];
        return sum;
    }

    static ushort[] RentGids(int len)
    {
        var ids = tGids;
        if (ids == null || ids.Length < len) ids = tGids = new ushort[len];
        return ids;
    }

    /// <summary>UTF-32 码位逐个取字形（代理对合成一个码位 → 一个字形，与 Skia 的 UTF-16→UTF-32 同径）。</summary>
    static int FillGids(SKTypeface tf, string text, ushort[] ids)
    {
        var cps = tCps;
        if (cps == null || cps.Length < text.Length) cps = tCps = new int[text.Length];
        int n = 0;
        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
            cps[n++] = cp;
        }
        var f = tFont ??= new SKFont();
        f.Typeface = tf;
        try
        {
            f.GetGlyphs(cps.AsSpan(0, n), ids.AsSpan(0, n));
        }
        finally
        {
            // 池化 font 对 typeface 持原生 sk_sp 强引用（MeasureWith 的老教训）：
            // 量完立即松手，只钉住共享的 Default 面，否则 LRU 逐出的面数据被钉到 ~20MB。
            f.Typeface = SKTypeface.Default;
        }
        return n;
    }
}
