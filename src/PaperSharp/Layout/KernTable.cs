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
    /// 已登记的字距表，按 (family, weight, slant) 键控。**不能**按 SKTypeface 实例键控：
    /// SKFont.Typeface 每次读取都新建一个托管包装，用实例当键会让每次度量都重新解析一遍 PRKT
    /// （Noto 那份 3 万对 → 单次约 3ms，折行循环里直接把渲染跑成挂死）。
    /// null 也是有意义的条目（该字面无 PRKT，负缓存，不再重复读文件）。
    ///
    /// 键里**必须带倾斜档**：FamilyName+Weight 撞得上不等于同一张表——"Segoe UI" 的正立面与
    /// 真斜面（Arial Italic 那一类）族名和字重完全相同，但 GPOS 配对不同（实测正立 Te=−200 units、
    /// 斜面 −184）。少带这一档，先跑的那条渲染把斜面的表登记进 ("Segoe UI",400)，后跑的度量
    /// 就整页吃错字距；并行套件里谁先跑不确定，于是表现为「只有满载时才偶发」的红。
    /// 与 <c>FontManager.Cache</c> 的 (family, weight, slant) 口径一致。
    /// 副作用是可接受的：向系统要不到斜面的请求会带回**正立面 + 请求的 slant 值**（见
    /// <see cref="FontManager.FaceIsSlanted"/>），于是同一张物理面按两个 slant 键各解析一次，
    /// 有上界（≤3 档）、不改变结果。
    /// </summary>
    static readonly ConcurrentDictionary<(string Family, int Weight, SKFontStyleSlant Slant), KernTable?> Cache = new();

    /// <summary>
    /// **按字面实例**的字距覆盖表（键 = SKTypeface 的原生指针 Handle，SKObject.Handle 跨托管包装稳定）。
    /// @font-face 网络字体走这条：浏览器加载的是**那份子集文件本身**，只认它自带的 GPOS
    /// （子集的 GPOS 是空的 → 字距 0），而**不**套用打包时烘进 PRKT 的那份「源 legacy kern」。
    /// 同一份子集文件若经「已安装字面」通路（FontManager 的 Microsoft YaHei / Noto 分派）加载，
    /// 浏览器用的是真装的雅黑/思源，那份 legacy kern 照样生效 → 仍走 (family,weight) 的 PRKT 缓存。
    /// 两条通路内部 family 相同，只能按实例区分，故用这张覆盖表。
    /// null 也是有意义条目（该 webfont 实例无自带字距表）。
    /// </summary>
    static readonly ConcurrentDictionary<long, KernTable?> InstanceCache = new();

    /// <summary>字距表的缓存键：族名 + 字重 + 倾斜档（三者缺一就会把另一张面的表当自己的用，
    /// 见 <see cref="Cache"/>）。用 <c>FontSlant</c> 而不是 <c>FontStyle.Slant</c>：后者每次新建一个
    /// SKFontStyle 包装，度量热路径上白掏分配。</summary>
    static (string, int, SKFontStyleSlant) Key(SKTypeface tf) => (tf.FamilyName, tf.FontWeight, tf.FontSlant);

    /// <summary>度量路径取字距表；未登记（系统字体、@font-face 无 PRKT）返回 null。</summary>
    public static KernTable? For(SKTypeface? tf) => tf == null ? null
        : InstanceCache.TryGetValue(tf.Handle.ToInt64(), out var ov) ? ov
        : Cache.TryGetValue(Key(tf), out var t) ? t : null;

    /// <summary>字符串的字距增量（px）；无表 → 0。</summary>
    public static float Of(string s, SKTypeface? tf, float sizePx) => For(tf)?.Adjust(s, sizePx) ?? 0f;

    /// <summary>相邻码位对的字距（px）。查表规则与 <see cref="Adjust"/> 逐字相同：两侧任一达到
    /// <see cref="Limit"/>（CJK 全宽，源里本就没有配对）或该对没配 → 0。</summary>
    public float Pair(int prev, int cp, float sizePx)
    {
        if (_count == 0 || prev >= Limit || cp >= Limit) return 0;
        if (prev < 128 && cp < 128) return (_ascii?[(prev << 7) | cp] ?? 0f) * sizePx;
        return _wide != null && _wide.TryGetValue((prev << 16) | cp, out var v) ? v * sizePx : 0f;
    }

    /// <summary>
    /// 把串按「非零字距对」切开：每段内部的相邻对全为 0 ⇒ 段宽就是纯 hmtx 宽，
    /// 段尾 <c>Tail</c> 是该段末码位到下一段首码位那一对的调整量。
    /// Σ(段宽 + Tail) 与 <c>MeasureText(整串) + Adjust(整串)</c> 逐项相等 —— 这正是绘制端要的口径：
    /// Skia 一次 DrawText 只按 hmtx 走位，字距是度量端（<c>TextMeasurer.LineWidth</c>）加进行宽的，
    /// 不切开画就会「量得窄、画得宽」，配对之后的每个字形都比布局以为的位置更往右。
    /// 串里没有配对时返回单段且 Tail=0，且段就是原串本身（无拷贝），调用方等同整串一次绘制。
    /// </summary>
    public List<(string Seg, float Tail)> KernChunks(string s, float sizePx)
    {
        var list = new List<(string, float)>();
        if (_count == 0 || s.Length < 2) { list.Add((s, 0f)); return list; }
        int start = 0, prev = First(s, 0);
        for (int i = Advance(s, 0); i < s.Length; i += Advance(s, i))
        {
            int cp = First(s, i);
            float k = Pair(prev, cp, sizePx);
            if (k != 0) { list.Add((s.Substring(start, i - start), k)); start = i; }
            prev = cp;
        }
        list.Add((s[start..], 0f));
        return list;
    }

    /// <summary>该字面下串内是否存在非零字距对（绘制端据此决定要不要走整串快路）。</summary>
    public static bool HasPairs(string s, SKTypeface? tf, float sizePx) => Of(s, tf, sizePx) != 0f;

    /// <summary>串首码位（代理对算一个）。</summary>
    public static int FirstCp(string s) => First(s, 0);
    /// <summary>串末码位（代理对算一个）。空串 → 0（0 是 .notdef，落不到任何配对）。</summary>
    public static int LastCp(string s)
    {
        int last = s.Length - 1;
        if (last < 0) return 0;
        return last > 0 && char.IsLowSurrogate(s[last]) ? char.ConvertToUtf32(s, last - 1) : s[last];
    }

    /// <summary>该字面是否带字距表（自检用）。</summary>
    public static bool HasTable(SKTypeface tf) => For(tf) != null;

    /// <summary>从字体文件登记字距表：只读 PRKT 那一段，不把整份字体留在内存。加载字面时调用一次。</summary>
    public static void Attach(SKTypeface tf, string path)
    {
        var key = Key(tf);
        if (Cache.ContainsKey(key)) return;
        // PRKT 优先：它是烘焙期把「源的 legacy kern + 子集 GPOS pair-pos」合并成的码位对表，
        // 覆盖面比运行期再解析一遍 GPOS 更全（浏览器用**已安装字面**时那份 legacy kern 照样生效）。
        var t = PrktTable(path);
        if (t == null)
        {
            AttachFromTables(tf);   // 自定义表没有 → 退回标准 GPOS 通路（下面那条）
            return;
        }
        Cache.TryAdd(key, t);
    }

    /// <summary>
    /// @font-face 网络字体实例的字距登记：只解析**该字面自带的 GPOS**（不读 PRKT 里那份源 legacy kern），
    /// 结果按 SKTypeface 实例（UniqueKey）缓存。浏览器的 DirectWrite/Skia 对 webfont 只应用文件内的 GPOS，
    /// 子集文件的 GPOS 是空的 → 字距 0（m4 SubSrcTest 实测：浏览器 = 纯 hmtx 78.03，引擎此前多套 −2.07）。
    /// </summary>
    public static void AttachWebfont(SKTypeface tf)
    {
        long key = tf.Handle.ToInt64();
        if (InstanceCache.ContainsKey(key)) return;
        InstanceCache.TryAdd(key, OwnTable(tf));   // null 也存：负缓存，度量退回纯 hmtx
    }

    /// <summary>读该文件的 PRKT 自定义表；没有或解析失败 → null。</summary>
    static KernTable? PrktTable(string path)    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var rows = ReadPrkt(fs);
            return rows is { Count: > 0 } ? new KernTable(rows) : null;
        }
        catch { return null; }   // 读不到就当没有字距
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
    // 系统字体没有 PRKT（那是子集构建期烘进去的自定义表），字距有两条来源，两条都吃：
    // ① GPOS 的 'kern'/'dist' 特性，挂在**字面对**上（format 1 精确对 / format 2 字距类对）。口径照
    //    src/PaperSharpSamples/tools/subset-font.py 的 _gpos_pairs：特性取 kern+dist 的并集（不分 script）、
    //    同一对跨 lookup 累加、每个字面只认 cmap 里最小的那个码位。Chrome 的 HarfBuzz 也是这个算法，
    //    所以对得上参考浏览器：Segoe UI「Page 1 - Typography & Text」@10 hmtx 123.989 + 字距 −1.870
    //    = 122.119 = Chrome 实测。
    // ② legacy <c>kern</c> 表（见 <see cref="LegacyKernRows"/>）——Arial / Times New Roman / Verdana 这批
    //    系统字面的字距**全**在这一张里，它们的 GPOS 里没有可用 pair-pos，只吃①就等于整张表为 null、
    //    度量退回纯 hmtx，于是每一行拉丁从第一个配对起就一路往右漂（真值矩阵实测 Arial@20「Te」偏宽
    //    2.21px、「TAVoWyA!」偏宽 4.25px；Times@20「TAVoWyA!」偏宽 7.96px）。
    // 只保留双码位 < Limit 的对（同 PRKT：CJK 全宽，字距交给 palt）。

    /// <summary>
    /// 从内存里的 sfnt 字节登记 PRKT 字距表——嵌资源加载的打包字体走这条（没有文件路径可读）。
    ///
    /// 必须存在的原因：打包子集的字距在项目自定义表 <c>PRKT</c> 里，而 <see cref="AttachFromTables"/>
    /// 只认标准 GPOS。若嵌资源改造只换成 AttachFromTables，PRKT 会被**静默丢弃**，
    /// 度量退回 hmtx 口径——正是重影/字距那批工作（P0-6/P0-7/P1-16）的地基。
    /// 所以嵌资源路径要么按字节读 PRKT，要么就不做。
    /// </summary>
    public static void AttachFromBytes(SKTypeface tf, byte[] sfnt)
    {
        var key = Key(tf);
        if (Cache.ContainsKey(key)) return;
        KernTable? t = null;
        try
        {
            using var ms = new MemoryStream(sfnt, writable: false);
            var rows = ReadPrkt(ms);
            if (rows is { Count: > 0 }) t = new KernTable(rows);
        }
        catch { /* 读不到就当没有自定义字距表，下面退 GPOS */ }
        if (t == null)
        {
            AttachFromTables(tf);
            return;
        }
        Cache.TryAdd(key, t);
    }

    /// <summary>
    /// 从字面自带的 sfnt 表登记字距（系统字体没有 PRKT，字距在 GPOS + legacy kern 里；
    /// @font-face 的子集若无 PRKT 也走这条，但只认自己那份 GPOS，见 <see cref="AttachWebfont"/>）。
    /// 解析一次即缓存，<see cref="SKTypeface.GetTableData"/> 只把用到的表体读进临时数组，不留字面数据。
    /// </summary>
    public static void AttachFromTables(SKTypeface tf)
    {
        var key = Key(tf);
        if (Cache.ContainsKey(key)) return;
        Cache.TryAdd(key, OwnTable(tf, legacyKern: true));   // null 也要存：负缓存，度量退回 hmtx 口径
    }

    /// <summary>按字面自己的 cmap 现算字距对：优先 GPOS，字面没有 GPOS 配对时（<paramref name="legacyKern"/>）
    /// 整张改吃 legacy kern 表；两条路都没配对 → null。</summary>
    static KernTable? OwnTable(SKTypeface tf, bool legacyKern = false)
    {
        try
        {
            var glyph2cp = CmapReps(tf);
            var rows = glyph2cp.Count > 0 ? GposRows(tf, glyph2cp) : null;
            // **按字面整体二选一**，不是逐对合并：浏览器（HarfBuzz / DirectWrite）在字面自带 GPOS 'kern'
            // 时就不再吃 legacy kern 表。逐对补会把只在 legacy 里出现的对（Segoe UI 的 r+y）也塞进来，
            // 真值矩阵实测 Segoe UI 从 0/120 坏成 8/120（「ry」@20 偏 0.79px），而 Arial / Times
            // 这种 GPOS 一条配对都没有的字面必须整张走 legacy（否则 66/120、75/120 全坏）。
            if (legacyKern && rows is not { Count: > 0 }) rows = LegacyKernRows(tf, glyph2cp, null);
            return rows is { Count: > 0 } ? new KernTable(rows) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 读字面自带的 legacy <c>kern</c>（format 0 配对子表）成码位对表。<paramref name="have"/> 传 null：
    /// 调用方 <see cref="OwnTable"/> 按字面整体二选一，只有 GPOS 一条配对都没有的字面才走到这里。
    /// 表内规则与 <c>bake_kern</c> 的 <c>_legacy_pairs</c> 同：子表内同名对后到覆盖（fontTools 反编译成
    /// dict）、跨子表相加、零值不进表。
    ///
    /// 子表头字节序按 fontTools 的 <c>table__k_e_r_n.decompile</c> 为准——PRKT 就是它烘的：
    /// <c>u16 version / u16 length / u8 format / u8 coverage</c>，与 OpenType 文档里 coverage 在前的
    /// 写法相反。照文档读会把 arial 那 909 对当成 format 1 状态表，整张静默丢掉。
    /// coverage 不做横竖过滤（fontTools 也不做；这批字面只有 1 张子表，且都是横向）。
    ///
    /// 命中这张表的都是系统字面：打包子集的 legacy kern 早在构建期被并进 PRKT，PRKT 命中就轮不到这里。
    /// </summary>
    static List<(int, int, float)>? LegacyKernRows(SKTypeface tf, Dictionary<int, int> glyph2cp,
        List<(int, int, float)>? have)
    {
        var t = Table(tf, "kern");
        if (t == null || t.Length < 10) return have;
        int n = Sfnt.U16(t, 2);
        if (n is <= 0 or > 64) return have;

        var merged = new Dictionary<long, int>();
        int p = 4;
        for (int i = 0; i < n && p + 14 <= t.Length; i++)
        {
            int len = Sfnt.U16(t, p + 2), fmt = t[p + 4], pairs = Sfnt.U16(t, p + 6);
            // 有些字面（OpenSans / Calibri）声明的 length 比实际短，按 nPairs 反推为准（同 fontTools）
            int body = 14 + pairs * 6;
            if (len <= 0 || len > t.Length - p) len = Math.Min(body, t.Length - p);
            if (fmt == 0 && len >= body)
            {
                var sub = new Dictionary<long, int>(pairs);
                for (int k = 0; k < pairs; k++)
                {
                    int e = p + 14 + k * 6;
                    if (!glyph2cp.TryGetValue(Sfnt.U16(t, e), out int a)
                     || !glyph2cp.TryGetValue(Sfnt.U16(t, e + 2), out int b)) continue;
                    short v = (short)Sfnt.U16(t, e + 4);
                    if (v != 0) sub[((long)a << 16) | (uint)b] = v;
                }
                foreach (var (key, v) in sub) merged[key] = merged.GetValueOrDefault(key) + v;
            }
            p += len;
        }
        if (merged.Count == 0) return have;

        var rows = have ?? new List<(int, int, float)>();
        var seen = new HashSet<long>();
        if (have != null) foreach (var (a, b, _) in have) seen.Add(((long)a << 16) | (uint)b);

        int upm = tf.UnitsPerEm is > 0 and <= 16384 ? tf.UnitsPerEm : 1000;
        foreach (var (key, units) in merged)
        {
            if (rows.Count >= MaxRows) break;
            if (seen.Add(key)) rows.Add(((int)(key >> 16), (int)(key & 0xFFFF), units / (float)upm));
        }
        return rows;
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
        // 先 format 4 再 format 12，两边都是「先到先得」：字距只用到码位 < Limit（0x2E80，全在 BMP），
        // BMP 归 format 4 说话，format 12 只补它没覆盖到的字形（个别只有 (3,10) 子表的字面）。
        // 实测这个先后对现存的 9 张字面没有影响（两张子表一致），排在前面只是把「BMP 以 format 4 为准」
        // 写死，免得以后遇到两张表打架的字面时又要重新查是谁盖了谁。
        if (fmt4 >= 0) Cmap4(cmap, fmt4, reps);
        if (fmt12 >= 0) Cmap12(cmap, fmt12, reps);
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

    /// <summary>
    /// cmap format 12 的组表。表头是 **16 字节**：u16 format / u16 reserved / u32 length /
    /// u32 language / u32 numGroups，组从 off+16 起、每组 12 字节。
    /// 少读那个 u32 language 会把 length 当组数、把组表起点少挪 4 字节 —— Arial 实测读成
    /// 「9784 组、首组 0x0000..0x032E→gid 32」，凭空造出几千条 glyph→码位 的假映射；
    /// 而 <see cref="CmapReps"/> 里这条通路先跑且 <see cref="TryAddRep"/> 先到先得，
    /// format 4 那份正确表就被盖掉了（gid 89 本该是 'v'，假表里成了 '9'）——
    /// 字距对于是登记到别的码位上，查表永远查不到（Arial 只有 A+V/A+W 这类侥幸命中的对活下来）。
    /// </summary>
    static void Cmap12(byte[] t, int off, Dictionary<int, int> reps)
    {
        if (off + 16 > t.Length) return;
        int groups = (int)Sfnt.U32(t, off + 12);
        if (groups is <= 0 or > 200_000) return;
        long lastEnd = 0;
        for (int i = 0; i < groups; i++)
        {
            int rec = off + 16 + i * 12;
            if (rec + 11 >= t.Length) return;
            long start = Sfnt.U32(t, rec), end = Sfnt.U32(t, rec + 4);
            uint glyph = Sfnt.U32(t, rec + 8);
            if (start >= Limit) return;
            if (start > end || start < lastEnd) continue;   // 倒挂/重叠组：照 HarfBuzz 跳过，别展开
            lastEnd = end;
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
                var cov = Coverage(t, st + Sfnt.U16(t, st + 2));
                int vf1 = Sfnt.U16(t, st + 4), vf2 = Sfnt.U16(t, st + 6);
                int c1n = Sfnt.U16(t, st + 8), c2n = Sfnt.U16(t, st + 10);
                int size = ValueSize(vf1) + ValueSize(vf2);
                if (size == 0 || c1n <= 0 || c2n <= 0 || c1n > 4096 || c2n > 4096 || cov.Count == 0) return;
                int tail = st + 12 + c1n * c2n * size;      // Class1Record 数组之后才是两张 ClassDef
                if (tail + 4 > t.Length) return;
                var cd1 = ClassDef(t, st + Sfnt.U16(t, tail));
                var cd2 = ClassDef(t, st + Sfnt.U16(t, tail + 2));
                if (cd1.Count == 0 && cd2.Count == 0) return;
                // 只对「有码位代表」的字形分类聚合（其余字形在度量时落不到，无需展开）。
                // 首字形必须先过 Coverage：PairPosFormat2 的 Coverage 列出的就是 Class1 字形，
                // **不在其中的字形整体退出**、而不是按「ClassDef 未列 → class 0」参与配对。原先漏了这一步，
                // 于是 Coverage 里没有的 H/M 也吃到 class0×class1 的负字距（Noto 那份 lookup 只有 1 个子表，
                // class1=0 里带着 HT −0.063em）→ "HTML"@12 被整体压窄 0.76px，连带其后每个行内盒 dx
                // （inline 样例 p[3] 三个 span 齐偏 0.76；探针 _probe-inline：浏览器 canvas 逐字求和 = hmtx，
                // 即浏览器根本不配这一对）。
                var covered = new HashSet<int>(cov);
                var by1 = new Dictionary<int, List<int>>();
                var by2 = new Dictionary<int, List<int>>();
                foreach (int g in kernable)
                {
                    int c2 = cd2.TryGetValue(g, out var v2) ? v2 : 0;
                    if (c2 < c2n) (by2.TryGetValue(c2, out var l2) ? l2 : (by2[c2] = [])).Add(g);
                    if (!covered.Contains(g)) continue;
                    int c1 = cd1.TryGetValue(g, out var v1) ? v1 : 0;
                    if (c1 < c1n) (by1.TryGetValue(c1, out var l1) ? l1 : (by1[c1] = [])).Add(g);
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
