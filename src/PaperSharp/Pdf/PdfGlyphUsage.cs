using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using PaperSharp.Layout;

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
    /// <summary>字形集合完全一致的同一张 face 的子集跨渲染复用（同一文档反复导出 / 常驻服务热路径）。
    /// 计价口径（tools/MemStress 实测）：**一张活字面在真渲染里 ≈3.5 MB**，是它子集流本身（0.2–0.6 MB）
    /// 的近十倍——多出来的是 Skia 挂在这张 typeface 上的字形位图/度量缓存，跟着字面一起常驻。
    /// 批量导出内容各异的 2000 份里，每张独有子集只被用一次、永远不会命中，48 张就这么白占 ~172 MB。
    /// 所以：① **第二次命中才准入**（只值一次的子集留在本次渲染的 <see cref="ownedFaces"/> 里当场释放）；
    /// ② LRU 上限 16 张（≈56 MB 封顶），逐出后等最后一个租约归还才 Dispose——并发渲染中直接放掉别人
    /// 正在用的字面会踩空句柄。同模板反复导出的热子集第二次起常驻，速度收益一点不丢。</summary>
    static readonly List<CachedSubset> Cache = new();

    /// <summary>第一趟见过的子集指纹；第二次出现才准进 <see cref="Cache"/>。只存 4 字节，量级 = 批内不同子集数。</summary>
    static readonly HashSet<uint> SeenOnce = new();
    static readonly object CacheLock = new();
    const int CacheCap = 16;

    sealed record CachedSubset(FaceKey Face, int[] Kept, SKTypeface Subset)
    {
        public int Users;      // 正在用这张常驻字面的渲染数（租约）
        public bool Dying;     // 已逐出，等最后一个租约归还再 Dispose
    }

    /// <summary>字面身份的算法与判定见 <see cref="Layout.FaceIdentity"/>：族名+字重+字宽+倾斜档+字形数+head 校验和。</summary>
    static FaceKey KeyOf(SKTypeface tf) => Layout.FaceIdentity.Of(tf);

    readonly Dictionary<FaceKey, HashSet<int>> Used = new();
    readonly Dictionary<FaceKey, SKTypeface> Source = new();   // 每个身份留一个具体实例当裁剪源
    readonly Dictionary<FaceKey, (SKTypeface Face, HashSet<int> Kept)> Subsets = new();
    readonly List<SKTypeface> ownedFaces = new();   // 未进全局缓存的子集 face：本次渲染独占，Dispose 释放
    readonly List<CachedSubset> borrowed = new();   // 从全局缓存取用的字面：Dispose 时归还租约
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
        // 归还常驻字面的租约：引用计数归零且已被逐出的那张，此刻才真还原生内存。
        // 并发渲染里随手 Dispose 一张别人正在用的字面＝踩空句柄，所以逐出与释放必须分两步。
        lock (CacheLock)
        {
            foreach (var c in borrowed)
                if (--c.Users == 0 && c.Dying) c.Subset.Dispose();
        }
        borrowed.Clear();
    }

    /// <summary>录制趟结束 → 按用字集合建子集 face（进程级缓存命中则零成本）。
    /// 一个**字面身份**一份子集：同一张字面被多少条路取出多少实例都只裁一次，用字并成一集。</summary>
    public void BuildSubsets()
    {
        foreach (var (key, set) in Used)
        {
            if (set.Count == 0) continue;
            var tf = Source[key];
            var sorted = set.ToArray();
            Array.Sort(sorted);
            SKTypeface? face = null;
            lock (CacheLock)
            {
                foreach (var c in Cache)
                    if (c.Face == key && c.Kept.SequenceEqual(sorted))
                    {
                        c.Users++;
                        borrowed.Add(c);
                        Cache.Remove(c);
                        Cache.Add(c);          // LRU：命中的挪到队尾，别被新来的挤掉
                        face = c.Subset;
                        break;
                    }
            }
            if (face == null)
            {
                if (HasBitmapColorTables(tf)) continue;   // CBDT/sbix/SVG：裁出的 face 经 FromData 会丢彩色层（emoji 变黑白线稿），整嵌不动。
                // 纯 COLR v1 彩色字体不再一刀切跳过：TtfSubsetter 沿 Paint 图把图层轮廓字形并进闭包
                // 并重写可达子集（§13.6 刀②，m7 那 12.9MB 的 seguiemj 整嵌就是它）。解析失败仍返回
                // null → 整嵌，行为与旧闸口一致。
                var bytes = TtfSubsetter.Subset(tf, set);
                if (bytes == null) continue;   // CFF/异常数据：保持整嵌
                face = SKTypeface.FromData(SKData.CreateCopy(bytes));
                if (face == null) continue;
                uint sig = Stamp(key, sorted);
                bool admit;
                lock (CacheLock)
                {
                    if (SeenOnce.Count > 65_536) SeenOnce.Clear();   // 常驻服务跑百万份：指纹表自己也要封顶
                    admit = !SeenOnce.Add(sig);   // Add 返回 false ＝ 上一趟就见过 → 这次是第二次命中
                    if (admit)
                    {
                        while (Cache.Count >= CacheCap)
                        {
                            var old = Cache[0];
                            Cache.RemoveAt(0);
                            old.Dying = true;
                            if (old.Users == 0) old.Subset.Dispose();
                        }
                        var entry = new CachedSubset(key, sorted, face) { Users = 1 };
                        Cache.Add(entry);
                        borrowed.Add(entry);
                    }
                    else ownedFaces.Add(face);   // 只值一次的子集：本次渲染独占，Dispose 当场释放
                }
            }
            Subsets[key] = (face, new HashSet<int>(sorted));
        }
        SubsetsBuilt = true;   // Recording 保持 true：真绘趟继续录制并逐字校验
    }

    /// <summary>子集**准入**用的指纹：字面身份 + 用字集合。允许哈希撞车——撞车只会让一张子集提前常驻
    /// （多花 3.5 MB），查表命中仍然逐字比 <c>Kept</c>，绝不会拿错 face 去画。</summary>
    static uint Stamp(FaceKey key, int[] kept)
    {
        uint h = ((uint)key.Family.GetHashCode() << 3) ^ (uint)key.GlyphCount ^ key.HeadSum;
        foreach (int g in kept) h = (h * 31) ^ (uint)g;
        return h ^ (uint)kept.Length;
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
    public SKTypeface Map(SKTypeface tf) => Subsets.TryGetValue(KeyOf(tf), out var e) ? e.Face : tf;

    static readonly Dictionary<SKTypeface, bool> ColorFont = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 带**位图/外矢量**彩色表层的字体（CBDT/sbix/SVG）：裁剪后重建的 face 不再被识别为彩色字体，
    /// 必须整嵌。COLR 不在此列——它的图层字形闭包由 <see cref="TtfSubsetter"/> 的 COLR v1 路处理。
    /// </summary>
    static bool HasBitmapColorTables(SKTypeface tf)
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
                    if (n is "CBDT" or "sbix" or "SVG ") { v = true; break; }
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
        if (u.SubsetsBuilt && u.Subsets.TryGetValue(KeyOf(tf), out var e))
        {
            var old = paint.Typeface;
            paint.Typeface = e.Face;
            try { canvas.DrawText(text, x, y, paint); }
            finally { paint.Typeface = old; }
            return;
        }
        canvas.DrawText(text, x, y, paint);
    }

    /// <summary>
    /// 带**字距对**落笔：Skia 一次 <c>DrawText</c> 只按 hmtx 走位，字距是度量端
    /// （<see cref="Layout.TextMeasurer"/> / <see cref="Layout.TextShaper.WidthOf"/>）加进行宽的，
    /// 所以串里只要有非零配对，就必须按配对切开逐段落笔——否则配对之后的每个字形都比布局
    /// 以为的位置往右偏，叠加视图里是一段重影。返回该串占用的推进宽度
    /// （= <c>MeasureText(整串) + KernTable.Of(整串)</c>，与度量端逐项相等）。
    /// 录制趟与真绘趟都走 <see cref="DrawText"/>，字形记账与子集换 face 一并继承。
    /// </summary>
    public static float DrawKerned(SKCanvas canvas, string text, float x, float y, SKPaint paint)
    {
        var kt = Layout.KernTable.For(paint.Typeface);
        if (kt == null) { DrawText(canvas, text, x, y, paint); return paint.MeasureText(text); }
        float cx = x;
        foreach (var (seg, tail) in kt.KernChunks(text, paint.TextSize))
        {
            DrawText(canvas, seg, cx, y, paint);
            cx += paint.MeasureText(seg) + tail;
        }
        return cx - x;
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
                // 输入码位经 cmap 命中的**整形前**目标字形也要进闭包：真绘趟是拿子集 face 重新
                // shape 的，HarfBuzz 第一步仍是 cmap 查询。阿拉伯文的 cmap 指向独立形、
                // 画出来的却是上下文变体形——只记 hb 输出会把 cmap 条目剪掉，真绘趟直接查不到字。
                u.RecordString(tf, text);
            }
            catch { /* 录制失败 → 该 run 缺席 → 真绘趟校验必然变红 → 回退整嵌，不静默出错 */ }
        }
        if (u.SubsetsBuilt && u.Subsets.TryGetValue(KeyOf(tf), out var e))
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
        var set = SetFor(tf);
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
        var set = SetFor(tf);
        foreach (var g in glyphs)
            if (g != 0) Note(tf, set, (int)g);
    }

    // 该字面身份的用字集；顺带记下这个实例作为裁剪源
    HashSet<int> SetFor(SKTypeface tf)
    {
        var key = KeyOf(tf);
        if (!Used.TryGetValue(key, out var set))
        {
            set = new HashSet<int>();
            Used[key] = set;
            Source[key] = tf;
        }
        return set;
    }

    void Note(SKTypeface tf, HashSet<int> set, int g)
    {
        set.Add(g);
        if (SubsetsBuilt && Subsets.TryGetValue(KeyOf(tf), out var e) && !e.Kept.Contains(g)) Violated = true;
    }
}
