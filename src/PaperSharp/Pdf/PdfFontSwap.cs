using System.IO.Compression;
using System.Text;

namespace PaperSharp.Pdf;

/// <summary><see cref="TtfSubsetter.SubsetForPdf"/> 的产物：<c>Hollow</c> 交给 Skia 画字，<c>Embed</c> 是最终嵌进 PDF 的字体程序。</summary>
internal sealed record PdfFontPair(byte[] Embed, byte[] Hollow, string? PsName);

/// <summary>
/// 把 Skia 写出的 PDF 里的「空壳字体」流换成真正带轮廓的子集字体（见 <see cref="TtfSubsetter.SubsetForPdf"/>）。
/// 只处理 Skia 自己写出的那种平整文件：单段 xref、无 /Prev、对象顺序即偏移顺序。
/// 做法是**整份重排**而不是增量更新——增量更新会把空壳流当死字节留在文件里（每张 CJK 面几十 KB）。
/// 任何不符合预期的地方（xref 对不上、描述符里找不到某个空壳、Length1 不等于空壳长度）一律返回 null，
/// 调用方据此退回「带轮廓子集 face 直接画」的老路径——所以这里宁可保守拒绝，绝不产出缺字的文件。
/// </summary>
internal static class PdfFontSwap
{
    /// <summary>一张待换的字体：<paramref name="marker"/> 是空壳的 PostScript 名，<paramref name="realName"/> 是写回 PDF 的真名。</summary>
    internal sealed class Target(string marker, string realName, int hollowLength, byte[] embed)
    {
        public readonly string Marker = marker;
        public readonly string RealName = realName;
        public readonly int HollowLength = hollowLength;
        public readonly byte[] Embed = embed;
    }

    public static (byte[] Buf, int Len)? Apply(byte[] pdf, int length, IReadOnlyList<Target> targets)
    {
        try { return ApplyCore(pdf, length, targets); }
        catch { return null; }
    }

    static (byte[] Buf, int Len)? ApplyCore(byte[] pdf, int length, IReadOnlyList<Target> targets)
    {
        var span = pdf.AsSpan(0, length);
        int sx = span.LastIndexOf("startxref"u8);
        if (sx < 0) return null;
        int xrefOff = (int)ParseLong(span, sx + 9);
        if (xrefOff <= 0 || xrefOff >= length || !span[xrefOff..].StartsWith("xref"u8)) return null;

        // —— xref 表 ——
        var objAt = new Dictionary<int, int>();
        int size = 0, p = xrefOff + 4;
        while (true)
        {
            p = SkipWs(span, p);
            if (span[p..].StartsWith("trailer"u8)) break;
            int start = (int)ParseLong(span, p, out p);
            int count = (int)ParseLong(span, p, out p);
            p = SkipWs(span, p);
            if (count <= 0) return null;
            for (int i = 0; i < count; i++, p += 20)
            {
                if (p + 20 > length) return null;
                bool inUse = span[p + 17] == (byte)'n';
                if (inUse) objAt[start + i] = (int)ParseLong(span, p);
                else if (start + i != 0) return null;   // 有 free 项：不是 Skia 的平整输出
            }
            size = Math.Max(size, start + count);
        }
        int trailerAt = p;
        int dictStart = span[trailerAt..].IndexOf("<<"u8);
        int tail = span[trailerAt..].IndexOf("startxref"u8);
        if (dictStart < 0 || tail < 0) return null;
        var trailerDict = span.Slice(trailerAt + dictStart, tail - dictStart);   // "<<…>>\n"
        if (trailerDict.IndexOf("/Prev"u8) >= 0) return null;                    // 已是增量文件

        // —— 对象按偏移排序，逐个核对 "N 0 obj" ——
        var order = objAt.OrderBy(kv => kv.Value).ToArray();
        if (order.Length == 0) return null;
        int firstObj = order[0].Value;
        var objStart = new int[order.Length];
        var objEnd = new int[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            objStart[i] = order[i].Value;
            objEnd[i] = i + 1 < order.Length ? order[i + 1].Value : xrefOff;
            if (!HasObjHeader(span, objStart[i], order[i].Key)) return null;
        }

        // —— 认出每个空壳的 FontDescriptor → 它指向的 FontFile2 流对象 ——
        var byMarker = new Dictionary<string, Target>(targets.Count);
        foreach (var t in targets) byMarker[t.Marker] = t;
        var streamTarget = new Dictionary<int, Target>();
        var found = new HashSet<string>();
        for (int i = 0; i < order.Length; i++)
        {
            var body = span[objStart[i]..objEnd[i]];
            if (body.Length > 4096) continue;   // 描述符只有几百字节，别去扫流
            if (body.IndexOf("/Type /FontDescriptor"u8) < 0) continue;
            string? name = ReadName(body, "/FontName /"u8);
            int ff = body.IndexOf("/FontFile2 "u8);
            if (name == null || ff < 0 || !byMarker.TryGetValue(name, out var target)) continue;
            int streamObj = (int)ParseLong(body, ff + 11);
            if (!objAt.ContainsKey(streamObj) || !streamTarget.TryAdd(streamObj, target)) return null;
            found.Add(name);
        }
        if (found.Count != targets.Count) return null;   // 有一张空壳没被 Skia 当 TrueType 嵌入（Type3/未出现）→ 放弃

        // —— 新的对象字节（只有被改的对象才有）——
        var replaced = new byte[order.Length][];
        int total = firstObj;
        for (int i = 0; i < order.Length; i++)
        {
            int num = order[i].Key;
            var body = span[objStart[i]..objEnd[i]];
            byte[]? repl = null;
            if (streamTarget.TryGetValue(num, out var t))
            {
                int l1 = body.IndexOf("/Length1 "u8);
                if (l1 < 0 || ParseLong(body, l1 + 9) != t.HollowLength) return null;
                repl = StreamObject(num, t.Embed);
            }
            else if (body.Length < 1 << 20 && body.IndexOf("PSK"u8) >= 0 && body.IndexOf("stream\n"u8) < 0)
                repl = Rename(body, byMarker);
            replaced[i] = repl!;
            total += repl?.Length ?? body.Length;
        }

        // —— 组装：头 + 对象 + 新 xref + trailer ——
        var xref = new StringBuilder(32 + 20 * size);
        var newOff = new int[order.Length];
        int pos = firstObj;
        for (int i = 0; i < order.Length; i++)
        {
            newOff[i] = pos;
            pos += replaced[i]?.Length ?? objEnd[i] - objStart[i];
        }
        int newXrefOff = pos;
        var slot = new int[size];
        Array.Fill(slot, -1);
        for (int i = 0; i < order.Length; i++) slot[order[i].Key] = newOff[i];
        xref.Append("xref\n0 ").Append(size).Append('\n');
        for (int n = 0; n < size; n++)
            xref.Append(slot[n] >= 0 ? slot[n].ToString("D10") + " 00000 n \n"
                      : n == 0 ? "0000000000 65535 f \n" : "0000000000 00000 f \n");
        xref.Append("trailer\n");
        var xrefBytes = Encoding.Latin1.GetBytes(xref.ToString());
        var tailBytes = Encoding.Latin1.GetBytes("\nstartxref\n" + newXrefOff + "\n%%EOF\n");

        var result = new byte[newXrefOff + xrefBytes.Length + trailerDict.Length + tailBytes.Length];
        span[..firstObj].CopyTo(result);
        for (int i = 0; i < order.Length; i++)
        {
            if (replaced[i] != null) replaced[i].CopyTo(result, newOff[i]);
            else span[objStart[i]..objEnd[i]].CopyTo(result.AsSpan(newOff[i]));
        }
        xrefBytes.CopyTo(result, newXrefOff);
        int w = newXrefOff + xrefBytes.Length;
        trailerDict.CopyTo(result.AsSpan(w));
        w += trailerDict.Length;
        // trailerDict 末尾自带换行时去掉多余的一个，保持 "startxref" 单独成行
        if (trailerDict.Length > 0 && trailerDict[^1] == (byte)'\n') w--;
        tailBytes.CopyTo(result, w);
        w += tailBytes.Length;

        // 自检：新 xref 里每个偏移处都必须是 "N 0 obj"
        var rs = result.AsSpan(0, w);
        for (int i = 0; i < order.Length; i++)
            if (!HasObjHeader(rs, newOff[i], order[i].Key)) return null;
        return (result, w);
    }

    /// <summary>与 Skia 写出的字体流对象同一版式：<c>&lt;&lt;/Length1 … /Filter /FlateDecode /Length …&gt;&gt; stream</c>。</summary>
    static byte[] StreamObject(int num, byte[] embed)
    {
        var head = Encoding.Latin1.GetBytes($"{num} 0 obj\n<</Length1 {embed.Length}\n/Filter /FlateDecode\n/Length ");
        // 先压缩到临时流拿到长度，再拼头
        using var z = new MemoryStream(embed.Length / 3 + 64);
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(embed);
        var comp = z.GetBuffer().AsSpan(0, (int)z.Length);
        var len = Encoding.Latin1.GetBytes($"{comp.Length}>> stream\n");
        var end = "\nendstream\nendobj\n"u8;
        var o = new byte[head.Length + len.Length + comp.Length + end.Length];
        head.CopyTo(o, 0);
        len.CopyTo(o, head.Length);
        comp.CopyTo(o.AsSpan(head.Length + len.Length));
        end.CopyTo(o.AsSpan(head.Length + len.Length + comp.Length));
        return o;
    }

    /// <summary>把对象字典里的占位名 <c>PSKn</c> 换回字体真名；Skia 较新版本会给子集字体名前缀 <c>ABCDEF+</c>，前缀原样保留。</summary>
    static byte[] Rename(ReadOnlySpan<byte> body, Dictionary<string, Target> byMarker)
    {
        var sb = new StringBuilder(body.Length + 32);
        int i = 0;
        while (i < body.Length)
        {
            int at = body[i..].IndexOf("PSK"u8);
            if (at < 0) { Append(sb, body[i..]); break; }
            int p = i + at;
            int k = p + 3;
            while (k < body.Length && body[k] is >= (byte)'0' and <= (byte)'9') k++;
            string name = Encoding.Latin1.GetString(body[p..k]);
            bool nameStart = p > 0 && body[p - 1] is (byte)'/' or (byte)'+';
            if (nameStart && k > p + 3 && byMarker.TryGetValue(name, out var t))
            {
                Append(sb, body[i..p]);
                sb.Append(t.RealName);
            }
            else Append(sb, body[i..k]);
            i = k;
        }
        return Encoding.Latin1.GetBytes(sb.ToString());

        static void Append(StringBuilder sb, ReadOnlySpan<byte> s) { foreach (var b in s) sb.Append((char)b); }
    }

    /// <summary>读 <c>/FontName /X</c> 的 X；去掉可能的 <c>ABCDEF+</c> 子集标签。</summary>
    static string? ReadName(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key)
    {
        int at = body.IndexOf(key);
        if (at < 0) return null;
        int s = at + key.Length, e = s;
        while (e < body.Length && body[e] is not ((byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'/' or (byte)'>')) e++;
        var name = body[s..e];
        if (name.Length > 7 && name[6] == (byte)'+')
        {
            bool tag = true;
            for (int i = 0; i < 6; i++) tag &= name[i] is >= (byte)'A' and <= (byte)'Z';
            if (tag) name = name[7..];
        }
        return Encoding.Latin1.GetString(name);
    }

    static bool HasObjHeader(ReadOnlySpan<byte> s, int at, int num)
    {
        if (at < 0 || at >= s.Length) return false;
        var hdr = Encoding.ASCII.GetBytes($"{num} 0 obj");
        return s[at..].StartsWith(hdr);
    }

    static int SkipWs(ReadOnlySpan<byte> s, int i)
    {
        while (i < s.Length && s[i] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\f' or 0) i++;
        return i;
    }

    static long ParseLong(ReadOnlySpan<byte> s, int at) => ParseLong(s, at, out _);

    static long ParseLong(ReadOnlySpan<byte> s, int at, out int end)
    {
        int i = SkipWs(s, at), j = i;
        long v = 0;
        while (j < s.Length && s[j] >= (byte)'0' && s[j] <= (byte)'9') v = v * 10 + (s[j++] - '0');
        if (j == i) throw new FormatException("number");
        end = j;
        return v;
    }
}
