using System.Runtime.CompilerServices;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace PaperSharp.Pdf;

/// <summary>
/// 一次 PDF 渲染的**字形记账本**：录制每个 typeface 实际画过哪些字形，
/// 真绘前把字体换成 <see cref="TtfSubsetter"/> 裁出的「只用字」子集，Skia 便只嵌这点字节
/// （Skia 的 PDF 后端自己不做任何子集化，见 PERF_REPORT §11）。
///
/// 双趟协议：<see cref="Recording"/> 先对一张 scratch 画布空跑一遍——与真绘完全同一套代码路径，
/// 杜绝「收集逻辑漏枚举某处文字」造成的静默空白；<see cref="BuildSubsets"/> 后第二趟真绘，
/// 绘制点仍逐字录制，遇到第一趟没记到的字形即置 <see cref="Violated"/>，
/// 调用方据此扔掉子集原样重绘（正确性兜底，正常永不变红）。
///
/// 每线程至多一个 <see cref="Current"/>；PNG/SVG 路径不设 Current，所有钩子直接透传。
/// </summary>
public sealed class PdfGlyphUsage : IDisposable
{
    /// <summary>字形集合完全一致的同一张 face 的子集跨渲染复用（同一文档反复导出 / 常驻服务热路径）。</summary>
    static readonly List<CachedSubset> Cache = new();
    static readonly object CacheLock = new();
    const int CacheCap = 48;

    sealed record CachedSubset(SKTypeface Original, int[] Kept, SKTypeface Subset);

    readonly Dictionary<SKTypeface, HashSet<int>> Used = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<SKTypeface, (SKTypeface Face, HashSet<int> Kept)> Subsets = new(ReferenceEqualityComparer.Instance);
    readonly List<SKTypeface> ownedFaces = new();   // 未进全局缓存的子集 face：本次渲染独占，Dispose 释放
    readonly PdfGlyphUsage? outer;

    [ThreadStatic] static PdfGlyphUsage? cur;
    public static PdfGlyphUsage? Current => cur;

    /// <summary>本线程正在录制字形（录制趟与带子集的真绘趟都为 true）。</summary>
    public bool Recording { get; private set; } = true;
    /// <summary>子集已建好：真绘点应把 face 换成 <see cref="Map"/> 的结果。</summary>
    public bool SubsetsBuilt { get; private set; }
    /// <summary>真绘趟出现录制趟没见过的字形：调用方须扔掉子集重绘。</summary>
    public bool Violated { get; private set; }

    public int SubsetbedFaces => Subsets.Count;

    PdfGlyphUsage(PdfGlyphUsage? outerScope) => outer = outerScope;

    public static PdfGlyphUsage Begin()
    {
        var u = new PdfGlyphUsage(cur);
        cur = u;
        return u;
    }

    public void Dispose()
    {
        if (cur == this) cur = outer;
        foreach (var f in ownedFaces) f.Dispose();
        ownedFaces.Clear();
    }

    /// <summary>录制趟结束 → 按用字集合建子集 face（进程级缓存命中则零成本）。</summary>
    public void BuildSubsets()
    {
        foreach (var (tf, set) in Used)
        {
            if (set.Count == 0) continue;
            var sorted = set.ToArray();
            Array.Sort(sorted);
            SKTypeface? face = null;
            lock (CacheLock)
            {
                foreach (var c in Cache)
                    if (ReferenceEquals(c.Original, tf) && c.Kept.SequenceEqual(sorted))
                    { face = c.Subset; break; }
            }
            if (face == null)
            {
                if (IsColorFont(tf)) continue;   // COLR/CBDT/sbix：裁出的 face 经 FromData 会丢彩色层（emoji 变黑白线稿），整嵌不动
                var bytes = TtfSubsetter.Subset(tf, set);
                if (bytes == null) continue;   // CFF/异常数据：保持整嵌
                face = SKTypeface.FromData(SKData.CreateCopy(bytes));
                if (face == null) continue;
                bool cached;
                lock (CacheLock)
                {
                    cached = Cache.Count < CacheCap;
                    if (cached) Cache.Add(new CachedSubset(tf, sorted, face));
                }
                if (!cached) ownedFaces.Add(face);
            }
            Subsets[tf] = (face, new HashSet<int>(sorted));
        }
        SubsetsBuilt = true;   // Recording 保持 true：真绘趟继续录制并逐字校验
    }

    /// <summary>校验失败后用：丢弃全部子集，真绘退回原始 face。</summary>
    public void RetryWithoutSubsets()
    {
        foreach (var f in ownedFaces) f.Dispose();
        ownedFaces.Clear();
        Subsets.Clear();
        SubsetsBuilt = false;
        Recording = false;
        Violated = false;
    }

    /// <summary>原 face → 子集 face；无子集（CFF/没用到）时原样返回。</summary>
    public SKTypeface Map(SKTypeface tf) => Subsets.TryGetValue(tf, out var e) ? e.Face : tf;

    static readonly Dictionary<SKTypeface, bool> ColorFont = new(ReferenceEqualityComparer.Instance);

    /// <summary>带彩色表层的字体（emoji 常见）。裁剪后重建的 face 不再被识别为彩色字体，必须整嵌。</summary>
    static bool IsColorFont(SKTypeface tf)
    {
        lock (ColorFont)
        {
            if (ColorFont.TryGetValue(tf, out var hit)) return hit;
            bool v = false;
            try
            {
                foreach (var tag in tf.GetTableTags())
                {
                    string n = new string(new[]
                    {
                        (char)((tag >> 24) & 0xFF), (char)((tag >> 16) & 0xFF), (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF),
                    });
                    if (n is "COLR" or "CBDT" or "sbix" or "SVG ") { v = true; break; }
                }
            }
            catch { v = true; }   // 读表失败 → 保守不裁
            ColorFont[tf] = v;
            return v;
        }
    }

    // ── 绘制点统一走这两个静态入口（PNG 路径 cur==null，全部直过）──────

    /// <summary>普通字符串绘制：录制（原 face 的码位→字形）+ 真绘趟换子集 face 落笔。</summary>
    public static void DrawText(SKCanvas canvas, string text, float x, float y, SKPaint paint)
    {
        var u = cur;
        if (u == null) { canvas.DrawText(text, x, y, paint); return; }
        var tf = paint.Typeface ?? SKTypeface.Default;
        if (u.Recording) u.RecordString(tf, text);
        if (u.SubsetsBuilt && u.Subsets.TryGetValue(tf, out var e))
        {
            var old = paint.Typeface;
            paint.Typeface = e.Face;
            try { canvas.DrawText(text, x, y, paint); }
            finally { paint.Typeface = old; }
            return;
        }
        canvas.DrawText(text, x, y, paint);
    }

    /// <summary>HarfBuzz 整段绘制：录制取真实 shaped 字形；真绘趟连 shaper 带 paint 换同一张子集 face。</summary>
    public static void DrawShaped(SKCanvas canvas, SKShaper shaper, string text, float x, float y, SKPaint paint)
    {
        var u = cur;
        if (u == null) { canvas.DrawShapedText(shaper, text, x, y, paint); return; }
        var tf = paint.Typeface ?? SKTypeface.Default;
        if (u.Recording)
        {
            try
            {
                var res = shaper.Shape(text, paint);
                u.RecordGlyphs(tf, res.Codepoints);   // HB 整形后 Codepoints 槽位里就是字形 ID（SkiaSharp 的历史命名）
            }
            catch { /* 录制失败 → 该 run 缺席 → 真绘趟校验必然变红 → 回退整嵌，不静默出错 */ }
        }
        if (u.SubsetsBuilt && u.Subsets.TryGetValue(tf, out var e))
        {
            var oldTf = paint.Typeface;
            paint.Typeface = e.Face;
            try
            {
                using var ms = new SKShaper(e.Face);
                canvas.DrawShapedText(ms, text, x, y, paint);
            }
            finally { paint.Typeface = oldTf; }
            return;
        }
        canvas.DrawShapedText(shaper, text, x, y, paint);
    }

    // ── 记账内核 ─────────────────────────────────────────────

    void RecordString(SKTypeface tf, string s)
    {
        if (!Used.TryGetValue(tf, out var set)) Used[tf] = set = new HashSet<int>();
        for (int i = 0; i < s.Length;)
        {
            int cp = char.ConvertToUtf32(s, i);
            i += char.IsHighSurrogate(s[i]) ? 2 : 1;
            int g = tf.GetGlyph(cp);
            if (g != 0) Note(tf, set, g);
        }
    }

    void RecordGlyphs(SKTypeface tf, UInt32[] glyphs)
    {
        if (!Used.TryGetValue(tf, out var set)) Used[tf] = set = new HashSet<int>();
        foreach (var g in glyphs)
            if (g != 0) Note(tf, set, (int)g);
    }

    void Note(SKTypeface tf, HashSet<int> set, int g)
    {
        set.Add(g);
        if (SubsetsBuilt && Subsets.TryGetValue(tf, out var e) && !e.Kept.Contains(g)) Violated = true;
    }
}
