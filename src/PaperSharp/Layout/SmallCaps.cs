using System.Buffers.Binary;
using System.Collections.Concurrent;
using SkiaSharp;

namespace PaperSharp.Layout;

/// <summary>
/// font-variant-caps 的真·字形推进：Blink/Chrome 把 <c>small-caps</c> 交给字体自带的 GSUB
/// <c>smcp</c> 特性替换成小型大写**字形**，其 hmtx advance 与大写/小写都不同
/// （challenge 探针：Segoe UI "Small Caps "@10.5 小写口径 53.28、smcp 口径 54.5，
/// Chrome 盒宽 75.5 = 54.5 + 21（"效果"两个全角），一字不差）。
/// 引擎此前按小写 advance 量、画 xHeight 缩放的大写（整串差 1.2px，盒级超差）。
/// 这里每字面只解析一次 GSUB smcp + hmtx，得「码位 → (替换−原)advance 差 / em」，
/// 按 (family,weight) 缓存；没有 smcp 的字面缓存 null，度量/绘制退回旧口径。
/// </summary>
public sealed class SmallCaps
{
    readonly float[] _deltaEm;    // 下标 = ASCII 码位；em 单位的 advance 差（smcp − 原字形）

    SmallCaps(Dictionary<int, int> subsByCp, SKTypeface tf)
    {
        _deltaEm = new float[128];
        int upm = tf.UnitsPerEm is > 0 and <= 16384 ? tf.UnitsPerEm : 1000;
        var hmtx = Table(tf, "hmtx");
        var hhea = Table(tf, "hhea");
        int numH = hhea != null ? U16(hhea, 34) : 0;
        foreach (var (cp, alt) in subsByCp)
        {
            if (cp is < 'A' or > 'z') continue;
            float adv0 = AdvanceOf(hmtx, numH, tf.GetGlyph(cp));
            float adv1 = AdvanceOf(hmtx, numH, alt);
            _deltaEm[cp] = (adv1 - adv0) / upm;
        }
    }

    static float AdvanceOf(byte[]? hmtx, int numH, int glyph)
    {
        if (hmtx == null || glyph < 0 || numH <= 0) return 0;
        int p = (glyph < numH ? glyph : numH - 1) * 4;
        return p + 1 < hmtx.Length ? U16(hmtx, p) : 0;
    }

    static byte[]? Table(SKTypeface tf, string tag)
    {
        try { return tf.GetTableData((uint)((tag[0] << 24) | (tag[1] << 16) | (tag[2] << 8) | tag[3])); }
        catch { return null; }
    }

    static readonly ConcurrentDictionary<(string, int), SmallCaps?> Cache = new();

    public static SmallCaps? For(SKTypeface? tf) => tf == null ? null
        : Cache.GetOrAdd((tf.FamilyName, tf.FontWeight), _ => Parse(tf));

    /// <summary>整串折叠成 smcp 字形后的 advance 增量（px）。caps=normal / 无表 → 0。</summary>
    public static float Delta(string text, SKTypeface? tf, float sizePx, string caps)
    {
        if (caps == "normal" || string.IsNullOrEmpty(text)) return 0;
        var t = For(tf);
        if (t == null) return 0;
        float em = 0;
        foreach (var ch in text)
            if (Folds(ch, caps) && ch < 128) em += t._deltaEm[ch];
        return em * sizePx;
    }

    /// <summary>单字素的增量（px），绘制通路逐字素推进用。无折叠/无表 → 0。</summary>
    public static float DeltaOf(string grapheme, SKTypeface? tf, float sizePx, string caps)
    {
        if (caps == "normal" || grapheme.Length != 1) return 0;
        char ch = grapheme[0];
        if (!Folds(ch, caps) || ch >= 128) return 0;
        var t = For(tf);
        return t == null ? 0 : t._deltaEm[ch] * sizePx;
    }

    static bool Folds(char ch, string caps) =>
        ch >= 'a' && ch <= 'z' || caps == "all-small-caps" && ch >= 'A' && ch <= 'Z';

    // ── GSUB smcp 解析 ────────────────────────────────────────────────

    static SmallCaps? Parse(SKTypeface tf)
    {
        var gsub = Table(tf, "GSUB");
        if (gsub == null || gsub.Length < 10) return null;
        try
        {
            int featList = U16(gsub, 6), lookupList = U16(gsub, 8);
            uint Smcp = (uint)(('s' << 24) | ('m' << 16) | ('c' << 8) | 'p');
            var map = new Dictionary<int, int>();
            int nfeat = U16(gsub, featList);
            // 同名特性可能有**多条** FeatureRecord（Segoe UI 的 smcp 就有两条：一条只覆盖非 ASCII，
            // 一条才带拉丁小写），逐条都收，找到第一条就 break 会漏掉真正那半张表。
            for (int i = 0; i < nfeat; i++)
            {
                int rec = featList + 2 + i * 6;
                if (rec + 5 >= gsub.Length || U32(gsub, rec) != Smcp) continue;
                int feat = featList + U16(gsub, rec + 4);
                int nlk = U16(gsub, feat + 2);
                for (int j = 0; j < nlk; j++)
                    CollectLookup(gsub, Lookup(gsub, lookupList, U16(gsub, feat + 4 + j * 2)), tf, map);
            }
            return map.Count > 0 ? new SmallCaps(map, tf) : null;
        }
        catch { return null; }
    }

    /// <summary>LookupList 里第 <paramref name="index"/> 个 lookup 的表体起点（偏移相对 LookupList 开头）。</summary>
    static int Lookup(byte[] t, int lookupList, int index) => lookupList + U16(t, lookupList + 2 + index * 2);

    static void CollectLookup(byte[] t, int lk, SKTypeface tf, Dictionary<int, int> map)
    {
        if (lk + 6 >= t.Length) return;
        int type = U16(t, lk), nsub = U16(t, lk + 4);
        for (int si = 0; si < nsub; si++)
        {
            int st = lk + U16(t, lk + 6 + si * 2);
            if (st + 2 >= t.Length) return;
            if (type == 7)   // GSUB 的 Extension（注意：GPOS 里是 9）：u16 fmt@0、真 lookupType@2、u32 偏移@4
            {
                type = U16(t, st + 2);
                st += (int)U32(t, st + 4);
                if (st + 2 >= t.Length) continue;
            }
            if (type != 1) continue;
            if (U16(t, st) is not (1 or 2)) continue;
            SingleSubst(t, st, tf, map);
        }
    }

    static void SingleSubst(byte[] t, int st, SKTypeface tf, Dictionary<int, int> map)
    {
        int fmt = U16(t, st);
        var cov = Coverage(t, st + U16(t, st + 2));
        if (cov.Count == 0) return;
        var g2cp = AsciiGlyphToCp(tf);
        if (fmt == 1)
        {
            short delta = (short)U16(t, st + 4);
            foreach (int g in cov)
                if (g2cp.TryGetValue(g, out int cp)) map[cp] = (g + delta) & 0xFFFF;
        }
        else
        {
            int n = U16(t, st + 4);
            for (int i = 0; i < n && i < cov.Count; i++)
            {
                int g2 = U16(t, st + 6 + i * 2);
                if (g2cp.TryGetValue(cov[i], out int cp) && g2 != 0) map[cp] = g2;
            }
        }
    }

    static readonly ConcurrentDictionary<(string, int), Dictionary<int, int>> AsciiMaps = new();

    static Dictionary<int, int> AsciiGlyphToCp(SKTypeface tf)
    {
        var key = (tf.FamilyName, tf.FontWeight);
        return AsciiMaps.GetOrAdd(key, _ =>
        {
            var m = new Dictionary<int, int>();
            for (int cp = 0x20; cp < 128; cp++)
            {
                int g = tf.GetGlyph(cp);
                if (g != 0) m.TryAdd(g, cp);   // 反查同名多字形时留首个（ASCII 每个码位只有一个形）
            }
            return m;
        });
    }

    /// <summary>Coverage 表（format 1/2）→ 字形列表。</summary>
    static List<int> Coverage(byte[] t, int off)
    {
        var glyphs = new List<int>();
        if (off <= 0 || off + 4 > t.Length) return glyphs;
        if (U16(t, off) == 1)
        {
            int n = U16(t, off + 2);
            for (int i = 0; i < n && off + 4 + i * 2 + 1 < t.Length; i++) glyphs.Add(U16(t, off + 4 + i * 2));
        }
        else
        {
            int n = U16(t, off + 2);
            for (int i = 0; i < n; i++)
            {
                int rec = off + 4 + i * 6;
                if (rec + 5 >= t.Length) break;
                for (int g = U16(t, rec), end = U16(t, rec + 2); g <= end && glyphs.Count < 65536; g++)
                    glyphs.Add(g);
            }
        }
        return glyphs;
    }

    static int U16(byte[] t, int p) => p >= 0 && p + 1 < t.Length ? BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(p)) : 0;
    static uint U32(byte[] t, int p) => p >= 0 && p + 3 < t.Length ? BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan(p)) : 0;
}
