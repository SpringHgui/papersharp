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
/// PDF 嵌字体时也只是丢掉它。只有打包子集带这张表，系统回退字体没有 → 度量退回旧口径。
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
}
