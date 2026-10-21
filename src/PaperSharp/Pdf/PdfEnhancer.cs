using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace PaperSharp.Pdf;

/// <summary>书签条目：Level=1..6（h1..h6），Page 从 0 起，YTop 为页内 Skia 坐标（左上原点、y 向下）。</summary>
public sealed class PdfOutline
{
    public int Level;
    public string Title = "";
    public int Page;
    public float YTop;
}

/// <summary>链接注释：Rect 为页内 Skia 坐标（左上原点）。</summary>
public sealed class PdfLink
{
    public int Page;
    public SKRect Rect;
    public string Uri = "";
}

/// <summary>
/// Skia 的 PDF 后端不出书签（outline）与链接注释（/Annots），也不给 catalog 挂 /PageMode。
/// 这里用 **PDF 增量更新** 在既有文件尾部追加新对象：
/// 解析 xref/trailer → 找 catalog 与页对象 → 追加 outline/annot 对象，
/// 并以同对象号重写 catalog（+/Outlines /PageMode）与带链接的页（+/Annots），
/// 最后写只含变更对象的新 xref 段 + /Prev 指回原表。任何解析失败都原样返回，绝不产出坏文件。
/// </summary>
public static class PdfEnhancer
{
    static readonly Encoding Latin1 = Encoding.Latin1;

    /// <param name="pageH">PDF 页高（pt）。</param>
    /// <param name="scale">布局 px → 纸面 pt 的折算系数（引擎坐标全部是 px，PDF 坐标全部是 pt）。</param>
    public static byte[] Enhance(byte[] pdf, IReadOnlyList<PdfOutline> outlines, IReadOnlyList<PdfLink> links, float pageH, float scale)
    {
        if (outlines.Count == 0 && links.Count == 0) return pdf;
        try { return EnhanceCore(pdf, outlines, links, pageH, scale); }
        catch { return pdf; }
    }

    static byte[] EnhanceCore(byte[] pdf, IReadOnlyList<PdfOutline> outlines, IReadOnlyList<PdfLink> links, float pageH, float scale)
    {
        var text = Latin1.GetString(pdf);

        // 尾部 startxref → xref 表 → trailer
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (sx < 0) return pdf;
        long xrefOff = ParseLongAt(text, sx + 9) ?? throw new FormatException("startxref");
        var xref = ParseXrefTable(text, xrefOff);                      // obj → (offset, gen)
        int tIdx = IndexAfter(text, "trailer", (int)xrefOff);
        string trailer = ExtractDict(text, tIdx);                       // "<< ... >>"
        int rootObj = ParseRef(trailer, "Root") ?? throw new FormatException("/Root");
        long prevStartxref = xrefOff;

        // 2) catalog → 页对象表（按页序，递归 Pages 树）
        string catalog = ReadObject(text, xref, rootObj);
        int pagesObj = ParseRef(catalog, "Pages") ?? throw new FormatException("/Pages");
        var pageObjs = new List<int>();
        CollectPages(text, xref, pagesObj, pageObjs, 0);
        if (pageObjs.Count == 0) return pdf;

        int next = xref.Keys.Max() + 1;

        // 3) 新对象字节：编号 → 内容
        var newObjs = new SortedDictionary<int, string>();

        // 3a) 书签树
        int outlinesId = -1;
        if (outlines.Count > 0)
        {
            // 先给每个 item 分配对象号，再按 Level 组树（h1 顶层，h2 挂最近 h1 下……）
            var ids = new int[outlines.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = next++;
            var children = new List<int>[outlines.Count];
            var top = new List<int>();                 // 顶层节点下标
            var stack = new List<int>();               // 各层最近节点的下标
            for (int i = 0; i < outlines.Count; i++)
            {
                children[i] = new List<int>();
                while (stack.Count > 0 && outlines[stack[^1]].Level >= outlines[i].Level) stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) top.Add(i);
                else children[stack[^1]].Add(i);
                stack.Add(i);
            }

            int outlinesDict = next++;
            outlinesId = outlinesDict;

            string Dest(int i)
            {
                int pg = Math.Min(outlines[i].Page, pageObjs.Count - 1);
                float y = Math.Max(0, pageH - outlines[i].YTop * scale);
                return $"[{pageObjs[pg]} 0 R /XYZ null {F(y)} null]";
            }
            void EmitItem(int i, int parent, int first, int last, int prev, int nextSib, int count)
            {
                var sb = new StringBuilder($"<<{Env(outlines[i])} /Parent {parent} 0 R /Dest {Dest(i)}");
                if (prev >= 0) sb.Append($" /Prev {prev} 0 R");
                if (nextSib >= 0) sb.Append($" /Next {nextSib} 0 R");
                if (first >= 0) sb.Append($" /First {first} 0 R /Last {last} 0 R /Count {count}");
                sb.Append(">>");
                newObjs[ids[i]] = sb.ToString();
            }
            void EmitLevel(List<int> list, int parent)
            {
                for (int k = 0; k < list.Count; k++)
                {
                    int i = list[k];
                    var kids = children[i];
                    EmitItem(i, parent,
                        kids.Count > 0 ? ids[kids[0]] : -1,
                        kids.Count > 0 ? ids[kids[^1]] : -1,
                        k > 0 ? ids[list[k - 1]] : -1,
                        k < list.Count - 1 ? ids[list[k + 1]] : -1,
                        DescCount(i, children));
                    EmitLevel(kids, ids[i]);
                }
            }
            EmitLevel(top, outlinesDict);

            var osb = new StringBuilder($"<</Type /Outlines /Count {outlines.Count}");
            if (top.Count > 0) osb.Append($" /First {ids[top[0]]} 0 R /Last {ids[top[^1]]} 0 R");
            osb.Append(">>");
            newObjs[outlinesDict] = osb.ToString();
        }

        // 3b) 链接注释
        var annotsByPage = new Dictionary<int, List<int>>();
        foreach (var g in links.GroupBy(l => Math.Min(l.Page, pageObjs.Count - 1)))
        {
            var ids = new List<int>();
            foreach (var l in g)
            {
                int id = next++;
                var r = l.Rect;
                float x1 = Math.Min(r.Left, r.Right) * scale, x2 = Math.Max(r.Left, r.Right) * scale;
                float y1 = Math.Max(0, pageH - Math.Max(r.Top, r.Bottom) * scale);
                float y2 = Math.Min(pageH, pageH - Math.Min(r.Top, r.Bottom) * scale);
                newObjs[id] = $"<</Type /Annot /Subtype /Link /Rect [{F(x1)} {F(y1)} {F(x2)} {F(y2)}] /Border [0 0 0] /A <</S /URI /URI ({PdfStr(l.Uri)})>>>>";
                ids.Add(id);
            }
            annotsByPage[g.Key] = ids;
        }

        // 4) 重写 catalog / 带注释的页（同对象号 → 增量覆盖）
        var patched = new Dictionary<int, string> { [rootObj] = Inject(catalog,
            (outlinesId > 0 ? $" /Outlines {outlinesId} 0 R" : "") + " /PageMode /UseOutlines") };
        foreach (var (pi, ids) in annotsByPage)
        {
            int objNo = pageObjs[pi];
            string body = ReadObject(text, xref, objNo);
            var arr = string.Join(" ", ids.Select(id => $"{id} 0 R"));
            patched[objNo] = Inject(body, $" /Annots [{arr}]");
        }

        // 5) 序列化增量段（xref 偏移必须是全文件绝对位置 = 原文件长度 + 段内偏移）
        long baseOff = pdf.Length;
        var outp = new StringBuilder();
        outp.Append("%\xE2\xE3\xCF\xDF\n");
        var offsets = new SortedDictionary<int, long>();
        foreach (var (id, body) in newObjs)
        {
            offsets[id] = baseOff + outp.Length;
            outp.Append($"{id} 0 obj\n{body}\nendobj\n");
        }
        foreach (var (id, body) in patched)
        {
            offsets[id] = baseOff + outp.Length;
            outp.Append($"{id} 0 obj\n{body}\nendobj\n");
        }

        long newXref = baseOff + outp.Length;
        outp.Append("xref\n0 1\n0000000000 65535 f \n");
        foreach (var run in Ranges(offsets.Keys))
        {
            outp.Append($"{run.start} {run.count}\n");
            for (int id = run.start; id < run.start + run.count; id++)
                outp.Append(id == 0 ? "0000000000 65535 f \n" : $"{offsets[id]:D10} 00000 n \n");
        }
        string tr = trailer;
        tr = ReplaceOrAdd(tr, "/Size", $"/Size {next}");
        tr = ReplaceOrAdd(tr, "/Prev", $"/Prev {prevStartxref}");
        outp.Append($"trailer\n{tr}\nstartxref\n{newXref}\n%%EOF\n");

        // 原文件 + 增量（同一份字节里追加）
        var tail = Latin1.GetBytes(outp.ToString());
        var result = new byte[pdf.Length + tail.Length];
        Array.Copy(pdf, result, pdf.Length);
        Array.Copy(tail, 0, result, pdf.Length, tail.Length);
        return result;
    }

    static string Env(PdfOutline o) => $"/Title <FEFF{Utf16Hex(o.Title)}>";

    static int DescCount(int i, List<int>[] children)
    {
        int n = 0;
        void Rec(int k) { foreach (var c in children[k]) { n++; Rec(c); } }
        Rec(i);
        return n;
    }

    // ── 解析小工具 ────────────────────────────────────────────────

    static Dictionary<int, (long off, int gen)> ParseXrefTable(string text, long at)
    {
        var map = new Dictionary<int, (long, int)>();
        int p = (int)at;
        while (p < text.Length && text[p] != 'x') p++;   // 容错：跳到 'xref'
        if (!text.AsSpan(p, Math.Min(4, text.Length - p)).StartsWith("xref")) throw new FormatException("xref");
        p += 4;
        while (true)
        {
            p = SkipWs(text, p);
            if (text.AsSpan(p, Math.Min(7, text.Length - p)).StartsWith("trailer")) break;
            int nl = text.IndexOf('\n', p);
            if (nl < 0) break;
            var head = text[p..nl].Trim();
            p = nl + 1;
            var hp = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (hp.Length != 2 || !int.TryParse(hp[0], out int start) || !int.TryParse(hp[1], out int count)) break;
            for (int i = 0; i < count; i++)
            {
                string entry = text.Substring(p, Math.Min(20, text.Length - p));
                p += 20;
                if (entry.Length < 17) continue;
                long off = ParseLong(entry.Substring(0, 10)) ?? -1;
                int gen = (int)(ParseLong(entry.Substring(11, 5)) ?? 0);
                char use = entry[17];
                if (use == 'n' && off >= 0) map[start + i] = (off, gen);
            }
        }
        return map;
    }

    static int IndexAfter(string s, string token, int from)
    {
        int i = s.IndexOf(token, from, StringComparison.Ordinal);
        if (i < 0) throw new FormatException(token);
        return SkipWs(s, i + token.Length);
    }

    /// <summary>从 '<' 起始处提取平衡的 &lt;&lt;…&gt;&gt; 字典文本。</summary>
    static string ExtractDict(string s, int at)
    {
        int i = s.IndexOf("<<", at, StringComparison.Ordinal);
        if (i < 0) throw new FormatException("<<");
        int depth = 0, j = i;
        while (j < s.Length)
        {
            if (j + 1 < s.Length && s[j] == '<' && s[j + 1] == '<') { depth++; j += 2; }
            else if (j + 1 < s.Length && s[j] == '>' && s[j + 1] == '>') { depth--; j += 2; if (depth == 0) return s[i..j]; }
            else j++;
        }
        throw new FormatException("unbalanced dict");
    }

    static int? ParseRef(string s, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"/" + key + @"\s+(\d+)\s+\d+\s+R");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    static string ReadObject(string text, Dictionary<int, (long off, int gen)> xref, int obj)
    {
        if (!xref.TryGetValue(obj, out var e)) throw new FormatException($"obj {obj}");
        int start = (int)e.off, end = text.IndexOf("endobj", start, StringComparison.Ordinal);
        if (end < 0) throw new FormatException("endobj");
        string body = text[start..end];
        int pre = body.IndexOf("obj", StringComparison.Ordinal);
        if (pre < 0) throw new FormatException("obj kw");
        return body[(pre + 3)..].Trim();
    }

    static void CollectPages(string text, Dictionary<int, (long off, int gen)> xref, int node, List<int> pages, int depth)
    {
        if (depth > 16 || pages.Count > 10000) return;
        string body = ReadObject(text, xref, node);
        var m = System.Text.RegularExpressions.Regex.Match(body, @"/Kids\s*\[([^\]]*)\]");
        if (!m.Success) { pages.Add(node); return; }
        foreach (System.Text.RegularExpressions.Match r in
                 System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, @"(\d+)\s+\d+\s+R"))
        {
            int child = int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
            string cb = ReadObject(text, xref, child);
            if (cb.Contains("/Type /Pages") || cb.Contains("/Type/Pages")) CollectPages(text, xref, child, pages, depth + 1);
            else pages.Add(child);
        }
    }

    /// <summary>把片段插到对象字典末尾（最后一个 '&gt;&gt;' 之前）。</summary>
    static string Inject(string dict, string add)
    {
        int i = dict.LastIndexOf(">>", StringComparison.Ordinal);
        if (i < 0) throw new FormatException("inject");
        return dict[..i] + add + dict[i..];
    }

    /// <summary>字典里替换 "/Key 数字" 或追加（追加在末尾 '&gt;&gt;' 前）。</summary>
    static string ReplaceOrAdd(string dict, string key, string entry)
    {
        var re = new System.Text.RegularExpressions.Regex(Regex.Escape(key) + @"\s+-?\d+");
        if (re.IsMatch(dict)) return re.Replace(dict, entry, 1);
        int i = dict.LastIndexOf(">>", StringComparison.Ordinal);
        if (i < 0) throw new FormatException(key);
        return dict[..i] + " " + entry + " " + dict[i..];
    }

    static long? ParseLong(string s) => long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    static long? ParseLongAt(string s, int at)
    {
        int i = SkipWs(s, at);
        int j = i;
        while (j < s.Length && char.IsDigit(s[j])) j++;
        return j > i ? ParseLong(s[i..j]) : null;
    }
    static int SkipWs(string s, int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; return i; }

    static IEnumerable<(int start, int count)> Ranges(IEnumerable<int> sorted)
    {
        int s = -1, e = -1;
        foreach (var v in sorted)
        {
            if (s < 0) { s = e = v; continue; }
            if (v == e + 1) { e = v; continue; }
            yield return (s, e - s + 1);
            s = e = v;
        }
        if (s >= 0) yield return (s, e - s + 1);
    }

    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string PdfStr(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    static string Utf16Hex(string s)
    {
        var sb = new StringBuilder(s.Length * 4);
        foreach (var c in System.Text.Encoding.BigEndianUnicode.GetBytes(s)) sb.Append(c.ToString("X2"));
        return sb.ToString();
    }
}
