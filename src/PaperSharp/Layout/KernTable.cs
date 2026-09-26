using System.Buffers.Binary;
using System.Collections.Concurrent;
using SkiaSharp;

namespace PaperSharp.Layout;

/// <summary>
/// 字体自带的**字距对**（pair kerning）。浏览器排版会按字体的字距表调整相邻字形的前进量，
/// 而 Skia 的 MeasureText 只加 hmtx、完全不做字距。实测参考浏览器里雅黑
/// <c>TAVoWyA!</c>@40 = hmtx 206.074 − 字距 8.418 = 197.656，Noto Sans SC 同理（它的字距在 GPOS）。
/// 缺这块每行文本窄 0.2~0.5px，窄盒的临界折行会多算一行（m4 的 <c>.calc-box</c>：
/// 引擎 8 行 vs 浏览器 7 行，下游 21 个盒 dy −22.6）。
/// 数据来自子集里的自定义表 <c>PRKT</c>（src/PaperSharpSamples/tools/subset-font.py 把源的 legacy
/// <c>kern</c> 表 + 子集 GPOS pair-pos 合并成「码位对 → int16 font units」）：Skia 读未知表只是忽略，
/// PDF 嵌字体时也只是丢掉它。系统回退字体没这张表，改从字面自带的 GPOS 现读现解析
/// （<see cref="AttachFromTables"/>，同一套算法），完全不带 GPOS/kern 的字面才退回旧口径。
/// </summary>
public sealed class KernTable
{
    /// <summary>PRKT 里保留的最大码位（含）——两侧都超过它的对在构建期就被裁掉了（CJK 全宽，无字距）。</summary>
    const int Limit = 0x2E80;

    readonly float[]? _ascii;                 // 相邻 ASCII 码位对：[a << 7 | b] → em
    readonly Dictionary<int, float>? _wide;   // 其余非 CJK 对：[a << 16 | b] → em
    readonly int _count;

    KernTable(List<(int A, int B, float Em)> rows)
    {
        _count = rows.Count;
        foreach (var (a, b, _) in rows)
        {
            if (a < 128 && b < 128) _ascii ??= new float[128 * 128];
            else _wide ??= new Dictionary<int, float>();
        }
        foreach (var (a, b, em) in rows)
        {
            if (a < 128 && b < 128) _ascii![(a << 7) | b] = em;
            else _wide![(a << 16) | b] = em;
        }
    }

    /// <summary>
    /// 已登记的字距表，按 (family, weight) 键控。**不能**按 SKTypeface 实例键控：
    /// SKFont.Typeface 每次读取都新建一个托管包装，用实例当键会让每次度量都重新解析一遍 PRKT
    /// （Noto 那份 3 万对 → 单次约 3ms，折行循环里直接把渲染跑成挂死）。
    /// null 也是有意义的条目（该字面无 PRKT，负缓存，不再重复读文件）。
    /// </summary>
    static readonly ConcurrentDictionary<(string Family, int Weight), KernTable?> Cache = new();

    /// <summary>度量路径取字距表；未登记（系统字体、@font-face 无 PRKT）返回 null。</summary>
    public static KernTable? For(SKTypeface? tf) => tf == null ? null
        : Cache.TryGetValue((tf.FamilyName, tf.FontWeight), out var t) ? t : null;

    /// <summary>字符串的字距增量（px）；无表 → 0。</summary>
    public static float Of(string s, SKTypeface? tf, float sizePx) => For(tf)?.Adjust(s, sizePx) ?? 0f;

    /// <summary>该字面是否带字距表（自检用）。</summary>
    public static bool HasTable(SKTypeface tf) => For(tf) != null;

    /// <summary>从字体文件登记字距表：只读 PRKT 那一段，不把整份字体留在内存。加载字面时调用一次。</summary>
    public static void Attach(SKTypeface tf, string path)
    {
        var key = (tf.FamilyName, tf.FontWeight);
        if (Cache.ContainsKey(key)) return;
        KernTable? t = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var rows = ReadPrkt(fs);
            if (rows is { Count: > 0 }) t = new KernTable(rows);
        }
        catch { /* 读不到就当没有字距 */ }
        if (t == null)
        {
            AttachFromTables(tf);   // 自定义表没有 → 退回标准 GPOS 通路（下面那条）
            return;
        }
        Cache.TryAdd(key, t);
    }

    /// <summary>串内相邻码位对的字距合计（px）。</summary>
    public float Adjust(string s, float sizePx)
    {
        if (_count == 0 || s.Length < 2) return 0;
        float em = 0;
        bool ascii = true;
        for (int i = 0; i < s.Length; i++) if (s[i] >= 128) { ascii = false; break; }
        if (ascii)
        {
            if (_ascii == null) return 0;
            var a = _ascii;
            for (int i = 1; i < s.Length; i++) em += a[(s[i - 1] << 7) | s[i]];
            return em * sizePx;
        }
        int prev = First(s, 0);
        for (int i = Advance(s, 0); i < s.Length; i += Advance(s, i))
        {
            int cp = First(s, i);
            if (prev < Limit && cp < Limit)
            {
                if (prev < 128 && cp < 128) em += _ascii?[(prev << 7) | cp] ?? 0f;
                else if (_wide != null && _wide.TryGetValue((prev << 16) | cp, out var v)) em += v;
            }
            prev = cp;
        }
        return em * sizePx;
    }

    static int First(string s, int i) => char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? char.ConvertToUtf32(s, i) : s[i];
    static int Advance(string s, int i) => char.IsHighSurrogate(s[i]) ? 2 : 1;

    // ── sfnt 读取 ─────────────────────────────────────────────────────

    /// <summary>解析 sfnt 目录找 PRKT 并解码成 (cp1, cp2, em) 列表。非 sfnt / 无该表 → null。</summary>
    static List<(int, int, float)>? ReadPrkt(Stream s)
    {
        Span<byte> hdr = stackalloc byte[12];
        if (!ReadExact(s, hdr)) return null;
        // 只认 sfnt（0x00010000 / 'OTTO' / 'true'）；ttc、woff、woff2 没有这套目录，直接放过
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(hdr);
        if (magic is not (0x00010000u or 0x4F54544Fu or 0x74727565u)) return null;
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(hdr.Slice(4));
        if (numTables <= 0 || numTables > 512) return null;

        var dir = new byte[numTables * 16];
        if (!ReadExact(s, dir.AsSpan())) return null;
        int tableOffset = -1, tableLength = 0;
        for (int i = 0; i < numTables; i++)
        {
            int p = i * 16;
            if (dir[p] != 'P' || dir[p + 1] != 'R' || dir[p + 2] != 'K' || dir[p + 3] != 'T') continue;
            tableOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(dir.AsSpan(p + 8));
            tableLength = (int)BinaryPrimitives.ReadUInt32BigEndian(dir.AsSpan(p + 12));
            break;
        }
        if (tableOffset < 0 || tableLength < 8) return null;

        var body = new byte[tableLength];
        s.Position = tableOffset;
        if (!ReadExact(s, body.AsSpan())) return null;
        int upm = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2));
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4));
        if (upm <= 0 || count <= 0 || 8 + count * 6 > tableLength) return null;

        var rows = new List<(int, int, float)>(count);
        for (int i = 0; i < count; i++)
        {
            int p = 8 + i * 6;
            int a = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(p));
            int b = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(p + 2));
            int v = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(p + 4));
            if (v != 0) rows.Add((a, b, v / (float)upm));
        }
        return rows;
    }

    static bool ReadExact(Stream s, Span<byte> buffer)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = s.Read(buffer.Slice(got));
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    // ── 系统字体：GPOS pair-pos ───────────────────────────────────────
    //
    // 系统字体没有 PRKT（那是子集构建期烘进去的自定义表），字距在 GPOS 的 'kern'/'dist' 特性里，
    // 挂在**字面对**上（format 1 精确对 / format 2 字距类对）。口径照 src/PaperSharpSamples/tools/
    // subset-font.py 的 _gpos_pairs：特性取 kern+dist 的并集（不分 script）、同一对跨 lookup 累加、
    // 每个字面只认 cmap 里最小的那个码位。Chrome 的 HarfBuzz 也是这个算法，所以对得上参考浏览器：
    // Segoe UI「Page 1 - Typography & Text」@10 hmtx 123.989 + 字距 −1.870 = 122.119 = Chrome 实测。
    // 只保留双码位 < Limit 的对（同 PRKT：CJK 全宽，字距交给 palt）。
    // 注：字距完全躲在 legacy <c>kern</c> 表里的字体（雅黑那种）不在这条通路内——打包子集已带 PRKT，
    // 只有系统回退命中这类字体时才退回旧的「无字距」口径。

    /// <summary>
    /// 从字面自带的 sfnt 表登记字距（系统字体没有 PRKT，字距在 GPOS 里；@font-face 的子集若无 PRKT 也走这条）。
    /// 解析一次即缓存，<see cref="SKTypeface.GetTableData"/> 只把 GPOS/cmap 两段表体读进临时数组，不留字面数据。
    /// </summary>
    public static void AttachFromTables(SKTypeface tf)
    {
        var key = (tf.FamilyName, tf.FontWeight);
        if (Cache.ContainsKey(key)) return;
        KernTable? t = null;
        try
        {
            var glyph2cp = CmapReps(tf);
            var rows = glyph2cp.Count > 0 ? GposRows(tf, glyph2cp) : null;
            if (rows is { Count: > 0 }) t = new KernTable(rows);
        }
        catch { /* 表读不到 / 解析失败 → 负缓存，度量退回 hmtx 口径 */ }
        Cache.TryAdd(key, t);
    }

    /// <summary>字面 → 该字面自带的 sfnt 表正文；非 sfnt face 或无该表 → null。</summary>
    static byte[]? Table(SKTypeface tf, string tag)
    {
        try { return tf.GetTableData((uint)((tag[0] << 24) | (tag[1] << 16) | (tag[2] << 8) | tag[3])); }
        catch { return null; }
    }

    /// <summary>cmap（format 4 / 12）里码位 &lt; Limit 的「字形 → 最小码位」代表表。字距对最终要落回码位。</summary>
    static Dictionary<int, int> CmapReps(SKTypeface tf)
    {
        var reps = new Dictionary<int, int>();
        var cmap = Table(tf, "cmap");
        if (cmap == null || cmap.Length < 4) return reps;
        int n = Sfnt.U16(cmap, 2);
        int fmt12 = -1, fmt4 = -1;
        for (int i = 0; i < n; i++)
        {
            int rec = 4 + i * 8;
            uint sub = (uint)((Sfnt.U16(cmap, rec) << 16) | Sfnt.U16(cmap, rec + 2));   // (platform<<16)|encoding
            int off = (int)Sfnt.U32(cmap, rec + 4);
            if (off <= 0 || off + 2 > cmap.Length) continue;
            int f = Sfnt.U16(cmap, off);
            if (sub is 0x00030001 or 0x00000003 or 0x00010000) { if (f == 4 && fmt4 < 0) fmt4 = off; }
            else if (sub is 0x0003000A or 0x00000004) { if (f == 12 && fmt12 < 0) fmt12 = off; }
        }
        if (fmt12 >= 0) Cmap12(cmap, fmt12, reps);
        if (fmt4 >= 0) Cmap4(cmap, fmt4, reps);
        return reps;
    }

    static void Cmap4(byte[] t, int off, Dictionary<int, int> reps)
    {
        int segX2 = Sfnt.U16(t, off + 6);
        int segs = segX2 / 2;
        int ends = off + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
        if (segs <= 0 || ranges + segX2 > t.Length) return;
        for (int i = 0; i < segs; i++)
        {
            int end = Sfnt.U16(t, ends + i * 2), start = Sfnt.U16(t, starts + i * 2);
            if (start >= Limit) continue;
            int stop = Math.Min(end, Limit - 1);
            int delta = Sfnt.U16(t, deltas + i * 2), ro = Sfnt.U16(t, ranges + i * 2);
            for (int cp = Math.Max(start, 0x20); cp <= stop; cp++)
            {
                int g;
                if (ro == 0) g = (cp + delta) & 0xFFFF;
                else
                {
                    int p = ranges + i * 2 + ro + (cp - start) * 2;
                    if (p + 1 >= t.Length) continue;
                    g = Sfnt.U16(t, p);
                    if (g != 0) g = (g + delta) & 0xFFFF;
                }
                if (g != 0) TryAddRep(reps, g, cp);
            }
        }
    }

    static void Cmap12(byte[] t, int off, Dictionary<int, int> reps)
    {
        int groups = (int)Sfnt.U32(t, off + 4);
        for (int i = 0; i < groups; i++)
        {
            int rec = off + 8 + i * 12;
            if (rec + 11 >= t.Length) return;
            long start = Sfnt.U32(t, rec), end = Sfnt.U32(t, rec + 4);
            uint glyph = Sfnt.U32(t, rec + 8);
            if (start >= Limit) return;
            for (long cp = start; cp <= Math.Min(end, Limit - 1); cp++)
                if (cp >= 0x20) TryAddRep(reps, (int)(glyph + (uint)(cp - start)), (int)cp);
        }
    }

    static void TryAddRep(Dictionary<int, int> reps, int glyph, int cp)
        => reps.TryAdd(glyph, cp);   // 码位升序扫描 → 首个即最小码位，和 PRKT 构建期一致

    /// <summary>GPOS 里 kern/dist 特性的 pair-pos（format 1 + 2），折成码位对行。</summary>
    static List<(int, int, float)>? GposRows(SKTypeface tf, Dictionary<int, int> glyph2cp)
    {
        var gpos = Table(tf, "GPOS");
        if (gpos == null || gpos.Length < 10) return null;
        int featOff = Sfnt.U16(gpos, 6), lookupOff = Sfnt.U16(gpos, 8);
        uint Kern = Tag("kern"), Dist = Tag("dist");

        // 同一个 lookup 常被多个特性引用；HarfBuzz 只应用一次，重复累加会把字距算重。
        var lookupIdx = new HashSet<int>();
        int nfeat = Sfnt.U16(gpos, featOff);
        for (int i = 0; i < nfeat; i++)
        {
            int rec = featOff + 2 + i * 6;
            if (rec + 5 >= gpos.Length) break;
            uint tag = Sfnt.U32(gpos, rec);
            if (tag != Kern && tag != Dist) continue;
            int feat = featOff + Sfnt.U16(gpos, rec + 4);
            int nlk = Sfnt.U16(gpos, feat + 2);
            for (int j = 0; j < nlk; j++) lookupIdx.Add(Sfnt.U16(gpos, feat + 4 + j * 2));
        }
        if (lookupIdx.Count == 0) return null;

        int nlookup = Sfnt.U16(gpos, lookupOff);
        var pairs = new Dictionary<int, int>();           // (g1 &lt;&lt; 16) | g2 → 字体单位
        foreach (int li in lookupIdx)
        {
            if (li < 0 || li >= nlookup) continue;
            // LookupOffset 相对 LookupList 开头（不是那个偏移数组）
            int lk = lookupOff + Sfnt.U16(gpos, lookupOff + 2 + li * 2);
            if (lk + 6 >= gpos.Length) continue;
            int type = Sfnt.U16(gpos, lk), nsub = Sfnt.U16(gpos, lk + 4);
            for (int si = 0; si < nsub; si++)
            {
                // SubTable 偏移同样相对 Lookup 表开头
                int st = lk + Sfnt.U16(gpos, lk + 6 + si * 2);
                if (st + 2 > gpos.Length) continue;
                if (type == 9)                            // Extension：真子表在偏移 8 处（u32）
                {
                    if (Sfnt.U16(gpos, st + 2) != 2) continue;
                    st += (int)Sfnt.U32(gpos, st + 4);
                    if (st + 2 >= gpos.Length) continue;
                }
                else if (type != 2) continue;
                PairPos(gpos, st, pairs, glyph2cp.Keys);
            }
        }
        if (pairs.Count == 0) return null;

        int upm = tf.UnitsPerEm is > 0 and <= 16384 ? tf.UnitsPerEm : 1000;
        var rows = new List<(int, int, float)>(pairs.Count);
        foreach (var (key, val) in pairs)
        {
            if (rows.Count >= MaxRows) break;
            if (!glyph2cp.TryGetValue(key >> 16, out int a) || !glyph2cp.TryGetValue(key & 0xFFFF, out int b)) continue;
            rows.Add((a, b, val / (float)upm));
        }
        return rows;
    }

    const int MaxRows = 400_000;                          // 类对笛卡尔展开的护栏（异常字体兜底）
    const int MaxCell = 200_000;

    static void PairPos(byte[] t, int st, Dictionary<int, int> pairs, ICollection<int> kernable)
    {
        switch (Sfnt.U16(t, st))
        {
            case 1:
            {
                var glyphs = Coverage(t, st + Sfnt.U16(t, st + 2));
                int vf1 = Sfnt.U16(t, st + 4), vf2 = Sfnt.U16(t, st + 6);
                int size = ValueSize(vf1) + ValueSize(vf2);
                int cnt = Sfnt.U16(t, st + 8);
                if (size == 0 || cnt == 0 || glyphs.Count == 0) return;
                for (int i = 0; i < Math.Min(cnt, glyphs.Count); i++)
                {
                    int ps = st + Sfnt.U16(t, st + 10 + i * 2);
                    int n = Sfnt.U16(t, ps);
                    int p = ps + 2;
                    for (int j = 0; j < n; j++, p += 2 + size)
                    {
                        if (p + 2 + size > t.Length) return;
                        int v = ValueXAdvance(t, p + 2, vf1);
                        if (v != 0) Add(pairs, glyphs[i], Sfnt.U16(t, p), v);
                    }
                }
                break;
            }
            case 2:
            {
                int vf1 = Sfnt.U16(t, st + 4), vf2 = Sfnt.U16(t, st + 6);
                int c1n = Sfnt.U16(t, st + 8), c2n = Sfnt.U16(t, st + 10);
                int size = ValueSize(vf1) + ValueSize(vf2);
                if (size == 0 || c1n <= 0 || c2n <= 0 || c1n > 4096 || c2n > 4096) return;
                int tail = st + 12 + c1n * c2n * size;      // Class1Record 数组之后才是两张 ClassDef
                if (tail + 4 > t.Length) return;
                var cd1 = ClassDef(t, st + Sfnt.U16(t, tail));
                var cd2 = ClassDef(t, st + Sfnt.U16(t, tail + 2));
                if (cd1.Count == 0 && cd2.Count == 0) return;
                // 只对「有码位代表」的字形分类聚合（其余字形在度量时落不到，无需展开）
                var by1 = new Dictionary<int, List<int>>();
                var by2 = new Dictionary<int, List<int>>();
                foreach (int g in kernable)
                {
                    int c1 = cd1.TryGetValue(g, out var v1) ? v1 : 0;
                    int c2 = cd2.TryGetValue(g, out var v2) ? v2 : 0;
                    if (c1 < c1n) (by1.TryGetValue(c1, out var l1) ? l1 : (by1[c1] = [])).Add(g);
                    if (c2 < c2n) (by2.TryGetValue(c2, out var l2) ? l2 : (by2[c2] = [])).Add(g);
                }
                foreach (var (c1, gs1) in by1)
                {
                    foreach (var (c2, gs2) in by2)
                    {
                        if ((long)gs1.Count * gs2.Count > MaxCell) continue;
                        int v = ValueXAdvance(t, st + 12 + (c1 * c2n + c2) * size, vf1);
                        if (v == 0) continue;
                        foreach (int a in gs1) foreach (int b in gs2) Add(pairs, a, b, v);
                    }
                }
                break;
            }
        }
    }

    static void Add(Dictionary<int, int> pairs, int g1, int g2, int units)
    {
        if (pairs.Count >= MaxRows) return;
        int key = (g1 << 16) | g2;
        pairs[key] = pairs.TryGetValue(key, out var old) ? old + units : units;
    }

    /// <summary>Coverage 表（format 1 字形列表 / format 2 区间展开）→ 有序字形。</summary>
    static List<int> Coverage(byte[] t, int off)
    {
        var glyphs = new List<int>();
        if (off <= 0 || off + 4 > t.Length) return glyphs;
        if (Sfnt.U16(t, off) == 1)
        {
            int n = Sfnt.U16(t, off + 2);
            for (int i = 0; i < n && off + 4 + i * 2 + 1 < t.Length; i++) glyphs.Add(Sfnt.U16(t, off + 4 + i * 2));
        }
        else
        {
            int n = Sfnt.U16(t, off + 2);
            for (int i = 0; i < n; i++)
            {
                int rec = off + 4 + i * 6;
                if (rec + 5 >= t.Length) break;
                for (int g = Sfnt.U16(t, rec), end = Sfnt.U16(t, rec + 2); g <= end && glyphs.Count < MaxRows; g++)
                    glyphs.Add(g);
            }
        }
        return glyphs;
    }

    /// <summary>ClassDef 表（format 1/2/3）→ 字形到类号的显式映射；未列出的字形类号为 0。</summary>
    static Dictionary<int, int> ClassDef(byte[] t, int off)
    {
        var map = new Dictionary<int, int>();
        if (off <= 0 || off + 4 > t.Length) return map;
        switch (Sfnt.U16(t, off))
        {
            case 1:
            {
                int start = Sfnt.U16(t, off + 2), n = Sfnt.U16(t, off + 4);
                for (int i = 0; i < n && off + 6 + i * 2 + 1 < t.Length; i++)
                    if (Sfnt.U16(t, off + 6 + i * 2) != 0) map[start + i] = Sfnt.U16(t, off + 6 + i * 2);
                break;
            }
            case 2:
            {
                int n = Sfnt.U16(t, off + 2);
                for (int i = 0; i < n; i++)
                {
                    int rec = off + 4 + i * 6;
                    if (rec + 5 >= t.Length) break;
                    int c = Sfnt.U16(t, rec + 4);
                    if (c == 0) continue;
                    for (int g = Sfnt.U16(t, rec), end = Sfnt.U16(t, rec + 2); g <= end && map.Count < MaxRows; g++)
                        map[g] = c;
                }
                break;
            }
            case 3:
            {
                int n = Sfnt.U16(t, off + 2);
                for (int i = 0; i < n && off + 4 + i * 4 + 1 < t.Length; i++)
                {
                    int g = Sfnt.U16(t, off + 4 + i * 2), c = Sfnt.U16(t, off + 4 + n * 2 + i * 2);
                    if (c != 0) map[g] = c;
                }
                break;
            }
        }
        return map;
    }

    // ValueRecord 是变长的：按 ValueFormat 的位决定字段，顺序固定为
    // xPlacement(0x1) yPlacement(0x2) xAdvance(0x4) yAdvance(0x8) + 四个 device 偏移(0x10..0x80)。
    const int VF_X_ADVANCE = 0x0004;

    static int ValueSize(int vf)
    {
        int n = 0;
        for (int bit = 0x1; bit <= 0x80; bit <<= 1) if ((vf & bit) != 0) n++;
        return n * 2;
    }

    /// <summary>该 ValueRecord 里的 XAdvance（字体单位）；没有该字段 → 0。</summary>
    static int ValueXAdvance(byte[] t, int p, int vf)
    {
        if ((vf & VF_X_ADVANCE) == 0) return 0;
        int pos = p;
        for (int bit = 0x1; bit < VF_X_ADVANCE; bit <<= 1)
            if ((vf & bit) != 0) pos += 2;
        return pos + 1 < t.Length ? Sfnt.S16(t, pos) : 0;
    }

    static uint Tag(string s) => (uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]);
    /// <summary>sfnt 表体的大端读取器；越界一律读 0（畸形表只会少几对，不会抛）。</summary>
    static class Sfnt
    {
        public static int U16(byte[] t, int p) => p >= 0 && p + 1 < t.Length
            ? BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(p)) : 0;
        public static int S16(byte[] t, int p) => p >= 0 && p + 1 < t.Length
            ? BinaryPrimitives.ReadInt16BigEndian(t.AsSpan(p)) : 0;
        public static uint U32(byte[] t, int p) => p >= 0 && p + 3 < t.Length
            ? BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan(p)) : 0;
    }
}
