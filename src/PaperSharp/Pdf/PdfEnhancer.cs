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
    static readonly Regex Kids = new(@"/Kids\s*\[([^\]]*)\]", RegexOptions.Compiled);
    static readonly Regex Ref = new(@"(\d+)\s+\d+\s+R", RegexOptions.Compiled);

    /// <param name="pageH">PDF 页高（pt）。</param>
    /// <param name="scale">布局 px → 纸面 pt 的折算系数（引擎坐标全部是 px，PDF 坐标全部是 pt）。</param>
    public static byte[] Enhance(byte[] pdf, IReadOnlyList<PdfOutline> outlines, IReadOnlyList<PdfLink> links, float pageH, float scale)
        => Enhance(pdf, pdf.Length, outlines, links, pageH, scale);

    /// <summary>
    /// <paramref name="pdf"/> 的前 <paramref name="length"/> 字节是有效 PDF（后面可能有装箱器留下的余量）。
    /// 走这条路时**全程按字节扫描**：旧实现一上来 <c>Latin1.GetString(整份 PDF)</c>，2.4MB 的文档
    /// 先变成 4.8MB 的 UTF-16 串，18MB 的（emoji/大图）文档变成 36MB，且之后每截一段对象就再复制一次。
    /// </summary>
    public static byte[] Enhance(byte[] pdf, int length, IReadOnlyList<PdfOutline> outlines, IReadOnlyList<PdfLink> links, float pageH, float scale)
    {
        if (outlines.Count == 0 && links.Count == 0)
            return length == pdf.Length ? pdf : pdf.AsSpan(0, length).ToArray();
        try { return EnhanceCore(pdf, length, outlines, links, pageH, scale); }
        catch { return length == pdf.Length ? pdf : pdf.AsSpan(0, length).ToArray(); }
    }

    static byte[] EnhanceCore(byte[] pdf, int length, IReadOnlyList<PdfOutline> outlines, IReadOnlyList<PdfLink> links, float pageH, float scale)
    {
        var text = new PdfBytes(pdf, length);

        // 尾部 startxref → xref 表 → trailer
        int sx = text.LastIndexOf("startxref");
        if (sx < 0) return pdf;
        long xrefOff = text.ParseLongAt(sx + 9) ?? throw new FormatException("startxref");
        var xref = text.ParseXrefTable(xrefOff);                        // obj → (offset, gen)
        int tIdx = text.IndexAfter("trailer", (int)xrefOff);
        string trailer = text.ExtractDict(tIdx);                        // "<< ... >>"
        int rootObj = ParseRef(trailer, "Root") ?? throw new FormatException("/Root");
        long prevStartxref = xrefOff;

        // 2) catalog → 页对象表（按页序，递归 Pages 树）
        string catalog = text.ReadObject(xref, rootObj);
        int pagesObj = ParseRef(catalog, "Pages") ?? throw new FormatException("/Pages");
        var pageObjs = new List<int>();
        text.CollectPages(xref, pagesObj, pageObjs, 0);
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
            string body = text.ReadObject(xref, objNo);
            var arr = string.Join(" ", ids.Select(id => $"{id} 0 R"));
            patched[objNo] = Inject(body, $" /Annots [{arr}]");
        }

        // 5) 序列化增量段（xref 偏移必须是全文件绝对位置 = 原文件长度 + 段内偏移）
        long baseOff = length;
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
        var result = new byte[length + tail.Length];
        Array.Copy(pdf, result, length);
        Array.Copy(tail, 0, result, length, tail.Length);
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
    //
    // PDF 的语法全是 ASCII 字节，Latin1 解码 1 字节 ↔ 1 字符，所以整套**定位**直接在 byte[] 上做
    // （见 PdfBytes）：不再需要「把整份 PDF 变成字符串」这一步，那是本类最大的一笔分配。
    // 只有真正要改写的那几个对象体（字典）才允许变成小字符串。

    static int? ParseRef(string s, string key)
    {
        var m = Regex.Match(s, @"/" + key + @"\s+(\d+)\s+\d+\s+R");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
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
        var re = new Regex(Regex.Escape(key) + @"\s+-?\d+");
        if (re.IsMatch(dict)) return re.Replace(dict, entry, 1);
        int i = dict.LastIndexOf(">>", StringComparison.Ordinal);
        if (i < 0) throw new FormatException(key);
        return dict[..i] + " " + entry + " " + dict[i..];
    }


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

    /// <summary>
    /// <see cref="byte"/> 数组上的只读 ASCII 扫描器（Latin1 一字节一字符，故判定口径与字符串完全一致）。
    /// 只用来**定位**：真正取出来的内容（对象体、字典）才有资格变成小字符串。
    /// </summary>
    readonly struct PdfBytes(byte[] buf, int len)
    {
        readonly byte[] B = buf;
        readonly int N = len;

        public int LastIndexOf(string token)
        {
            for (int i = N - token.Length; i >= 0; i--)
                if (Match(i, token)) return i;
            return -1;
        }

        bool Match(int at, string token)
        {
            if (at + token.Length > N) return false;
            for (int k = 0; k < token.Length; k++)
                if (B[at + k] != token[k]) return false;
            return true;
        }

        public int IndexOf(string token, int from)
        {
            for (int i = Math.Max(0, from); i + token.Length <= N; i++)
                if (Match(i, token)) return i;
            return -1;
        }

        public int IndexAfter(string token, int from)
        {
            int i = IndexOf(token, from);
            if (i < 0) throw new FormatException(token);
            return SkipWs(i + token.Length);
        }

        public int SkipWs(int i) { while (i < N && IsWs(B[i])) i++; return i; }
        static bool IsWs(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0;

        /// <summary>从 <paramref name="at"/> 起解析一个十进制整数（跳过前导空白）。</summary>
        public long? ParseLongAt(int at)
        {
            int i = SkipWs(at), j = i;
            while (j < N && B[j] >= (byte)'0' && B[j] <= (byte)'9') j++;
            if (j == i) return null;
            long v = 0;
            for (int k = i; k < j; k++) v = v * 10 + (B[k] - '0');
            return v;
        }

        /// <summary>从 '<c>&lt;&lt;</c>' 起提取平衡的字典，返回其文本（只有字典本体，量级 = 一个对象）。</summary>
        public string ExtractDict(int at)
        {
            int i = IndexOf("<<", at);
            if (i < 0) throw new FormatException("<<");
            int depth = 0, j = i;
            while (j < N)
            {
                if (j + 1 < N && B[j] == '<' && B[j + 1] == '<') { depth++; j += 2; }
                else if (j + 1 < N && B[j] == '>' && B[j + 1] == '>') { depth--; j += 2; if (depth == 0) return Str(i, j); }
                else j++;
            }
            throw new FormatException("unbalanced dict");
        }

        /// <summary>xref 表：obj → (offset, gen)。逐行读，不落地任何中间字符串。</summary>
        public Dictionary<int, (long off, int gen)> ParseXrefTable(long at)
        {
            var map = new Dictionary<int, (long, int)>();
            int p = (int)at;
            while (p < N && B[p] != (byte)'x') p++;          // 容错：跳到 'xref'
            if (!Match(p, "xref")) throw new FormatException("xref");
            p += 4;
            while (true)
            {
                p = SkipWs(p);
                if (Match(p, "trailer")) break;
                int start = 0, count = 0;
                int q = p;
                while (q < N && B[q] >= (byte)'0' && B[q] <= (byte)'9') start = start * 10 + (B[q++] - '0');
                while (q < N && IsWs(B[q])) q++;
                while (q < N && B[q] >= (byte)'0' && B[q] <= (byte)'9') count = count * 10 + (B[q++] - '0');
                if (count <= 0) break;
                int nl = Array.IndexOf(B, (byte)'\n', q, Math.Max(0, N - q));
                if (nl < 0) break;
                p = nl + 1;
                for (int i = 0; i < count; i++)
                {
                    long off = -1;
                    int gen = 0;
                    int e = p;
                    if (e + 17 > N) { p = N; break; }
                    long acc = 0;
                    while (e < p + 10 && B[e] >= (byte)'0' && B[e] <= (byte)'9') acc = acc * 10 + (B[e++] - '0');
                    off = acc;
                    e = p + 11;
                    int gacc = 0;
                    while (e < p + 16 && B[e] >= (byte)'0' && B[e] <= (byte)'9') gacc = gacc * 10 + (B[e++] - '0');
                    gen = gacc;
                    char use = (char)B[p + 17];
                    if (use == 'n' && off >= 0) map[start + i] = (off, gen);
                    p += 20;
                }
            }
            return map;
        }

        /// <summary>按 xref 取出一个对象的字典体（"obj" 之后到 "endobj" 之前）。</summary>
        public string ReadObject(Dictionary<int, (long off, int gen)> xref, int obj)
        {
            if (!xref.TryGetValue(obj, out var e)) throw new FormatException($"obj {obj}");
            int start = (int)e.off;
            if (start < 0 || start >= N) throw new FormatException("obj off");
            int end = IndexOf("endobj", start);
            if (end < 0) throw new FormatException("endobj");
            int pre = IndexOf("obj", start);
            if (pre < 0 || pre > end) throw new FormatException("obj kw");
            return Str(pre + 3, end).Trim();
        }

        /// <summary>递归 Pages 树，收集叶子页对象号（按页序）。</summary>
        public void CollectPages(Dictionary<int, (long off, int gen)> xref, int node, List<int> pages, int depth)
        {
            if (depth > 16 || pages.Count > 10000) return;
            string body = ReadObject(xref, node);
            var m = Kids.Match(body);
            if (!m.Success) { pages.Add(node); return; }
            foreach (System.Text.RegularExpressions.Match r in Ref.Matches(m.Groups[1].Value))
            {
                int child = int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                string cb = ReadObject(xref, child);
                if (cb.Contains("/Type /Pages") || cb.Contains("/Type/Pages")) CollectPages(xref, child, pages, depth + 1);
                else pages.Add(child);
            }
        }

        string Str(int start, int end) => Latin1.GetString(B, start, Math.Max(0, end - start));
    }

}
