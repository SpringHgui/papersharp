using System.Buffers.Binary;
using SkiaSharp;

namespace PaperSharp.Pdf;

/// <summary>
/// TrueType 字形裁剪器：**保 GID** 地删掉没用到的字形轮廓，其余表原样保留。
/// Skia 的 PDF 后端不做字体子集化（SkiaSharp 2.88/3.x 实测均整嵌，见 PERF_REPORT §11），
/// 一份 2.4MB 的 CJK 字体会把任何 PDF 撑到 2.5MB 并让编码段占掉单页耗时的 85% 以上。
/// 这里把「整份字体」换成「同一字体 − 没用到的 glyf」：
///  • GID 编号不变 → cmap/hmtx/GSUB/GPOS/复合引用全部照旧，测量与绘制行为与原字体逐位一致；
///  • 其余表（cmap/hhea/maxp/OS2/GPOS…）原字节拷贝 → HarfBuzz shaping、宽度、回退判定零漂移；
///  • loca 按新 glyf 重建，能塞进短格式（÷2 存 u16）就换短格式，省 15% 头部。
/// 无 glyf 的字体（CFF/OTF）返回 null 不改写——宁可整嵌，不做半成品子集。
/// 表数据取自 <see cref="SKTypeface.GetTableData(uint)"/>：系统字体 / TTC 成员面 / woff2 解码后的
/// 注册字体都能拿到解好的 TTF 表，所以这条裁剪路对全部 face 一视同仁。
/// </summary>
public static class TtfSubsetter
{
    const uint TagGlyf = 0x676C7966;   // 'glyf'
    const uint TagLoca = 0x6C6F6361;   // 'loca'
    const uint TagHead = 0x68656164;   // 'head'
    const uint TagMaxp = 0x6D617870;   // 'maxp'

    static uint Tag(string s) => (uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]);

    /// <summary>
    /// 与字形绘制无关的表，裁剪时直接扔：`PRKT`(微软私有，雅黑里 176KB)、`hdmx`/`LTSH`(设备像素度量表，
    /// 现代渲染器一律走 glyf 轮廓，雅黑里 501KB/29KB)、`MERG`(废弃的 break-pair)、`meta`(语言/版权元数据)、
    /// `DSIG`(签名，改表后必然失效)。留着它们，纯文字 PDF 的体积就还是被这些死数据占掉一大半。
    /// **不碰** `VDMX`（部分渲染器据此取 CJK 设备行高）与任何 shaping/度量表。
    /// </summary>
    static readonly HashSet<uint> Drop = new()
    {
        0x44534947,                       // DSIG
        Tag("PRKT"), Tag("hdmx"), Tag("LTSH"), Tag("MERG"), Tag("meta"),
    };

    /// <summary>裁出的字体字节；失败/不适用返回 null。</summary>
    public static byte[]? Subset(SKTypeface tf, ICollection<int> usedGlyphs)
    {
        try
        {
            var tags = tf.GetTableTags();
            Array.Sort(tags);
            if (Array.IndexOf(tags, TagGlyf) < 0) return null;   // CFF/OTF：不裁

            var tables = new Dictionary<uint, byte[]>();
            foreach (var t in tags)
            {
                if (Drop.Contains(t)) continue;
                var data = tf.GetTableData(t);
                if (data == null) return null;
                tables[t] = data;
            }
            if (!tables.TryGetValue(TagHead, out var head) || !tables.TryGetValue(TagMaxp, out var maxp)) return null;

            int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(4));
            short locaFmt = BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50));
            if (!tables.TryGetValue(TagLoca, out var loca) || !tables.TryGetValue(TagGlyf, out var glyf)) return null;

            // loca → 每个 GID 的 [start,end)
            var offs = new int[numGlyphs + 1];
            for (int g = 0; g <= numGlyphs; g++)
                offs[g] = locaFmt == 0
                    ? BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(g * 2)) * 2
                    : BinaryPrimitives.ReadInt32BigEndian(loca.AsSpan(g * 4));

            // 复合闭包：被引用组件必须一起保留
            var keep = ComputeClosure(usedGlyphs, numGlyphs, offs, glyf);
            if (keep == null) return null;   // 引用越界等异常数据：放弃裁剪保正确

            // hmtx/vmtx 是「每个 GID 一条」的定长数组，不跟着字形数缩的话，
            // 只用 114 个字形的发票仍要背 6763 条度量（hmtx 30KB + vmtx 16KB × 每个字重）。
            TrimMetrics(tables, keep, numGlyphs);

            // 重建 glyf（原样拷贝保留项，4 字节对齐）+ 新 loca
            var zeros = new byte[4];
            using (var ms = new MemoryStream())
            {
                var newOff = new int[numGlyphs + 1];
                int w = 0;
                for (int g = 0; g < numGlyphs; g++)
                {
                    newOff[g] = w;
                    int len = offs[g + 1] - offs[g];
                    if (len <= 0 || !keep[g]) continue;
                    ms.Write(glyf, offs[g], len);
                    w += len;
                    int fix = (4 - (w & 3)) & 3;
                    if (fix > 0) { ms.Write(zeros, 0, fix); w += fix; }
                }
                newOff[numGlyphs] = w;
                short newFmt = (short)(w <= 0x1FFFE ? 0 : 1);
                var newLoca = new byte[newFmt == 0 ? (numGlyphs + 1) * 2 : (numGlyphs + 1) * 4];
                for (int g = 0; g <= numGlyphs; g++)
                {
                    if (newFmt == 0) BinaryPrimitives.WriteUInt16BigEndian(newLoca.AsSpan(g * 2), (ushort)(newOff[g] / 2));
                    else BinaryPrimitives.WriteInt32BigEndian(newLoca.AsSpan(g * 4), newOff[g]);
                }
                tables[TagGlyf] = ms.ToArray();
                tables[TagLoca] = newLoca;
                head = (byte[])head.Clone();
                head[50] = (byte)(newFmt >> 8); head[51] = (byte)newFmt;   // indexToLocFormat 随新 loca
                BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);   // checkSumAdjustment 清零，BuildSfnt 里回填
                tables[TagHead] = head;
                return BuildSfnt(tables);
            }
        }
        catch { return null; }   // 任何解析意外都退回“不裁”，绝不出坏字体
    }

    /// <summary>
    /// 重写 hmtx / vmtx：保留项的 (advance, lsb) 逐字节不变，未保留的尾部折叠成「最后一条 + 零长度数组」
    /// （这正是这两张表原生的尾部表达法，numberOfHMetrics/numberOfVMetrics 之后所有 GID 共用最后一条）。
    /// 中间的空洞没法用「尾部共用一条」表达（那要求空洞 GID 恰好与保留项同 advance），所以只收尾部。
    /// </summary>
    static void TrimMetrics(Dictionary<uint, byte[]> tables, bool[] keep, int numGlyphs)
    {
        int last = -1;
        for (int g = numGlyphs - 1; g >= 0; g--) if (keep[g]) { last = g; break; }
        if (last < 0) return;

        foreach (var (mtx, hhea, countOff) in new[]
                 { (Tag("hmtx"), Tag("hhea"), 34), (Tag("vmtx"), Tag("vhea"), 34) })
        {
            if (!tables.TryGetValue(mtx, out var m) || !tables.TryGetValue(hhea, out var h)) continue;
            int stride = mtx == Tag("vmtx") ? 4 : 2;   // hmtx 只有 lsb 数组；vmtx 还有 top/bottom
            int n = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(countOff));
            int total = m.Length / (2 + stride);
            if (n < 1 || n > total || last >= total) continue;   // 表不成形：原样留着，别赌

            var adv = new int[total];
            for (int g = 0; g < n; g++) adv[g] = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(g * 2));
            for (int g = n; g < total; g++) adv[g] = adv[n - 1];

            int defAdv = adv[n - 1];
            int newCount = last + 1;   // 中间空洞无法用「尾部共用一条」表达，所以只收最后一个保留字形之后的部分
            byte[] extra = Array.Empty<byte>();
            if (adv[last] != defAdv && last + 1 < total)
            {
                // 折叠后 GID>last 会共用最后一条度量；若原字体的默认 advance 与之不同，
                // 补一条 defAdv 当共用尾——这些 GID 都没被保留，值取原样即可，lsb 借前一条。
                extra = new byte[2 + stride];
                BinaryPrimitives.WriteUInt16BigEndian(extra, (ushort)defAdv);
                m.AsSpan(last * (2 + stride) + 2, stride).CopyTo(extra.AsSpan(2));
                newCount++;
            }

            var nw = new byte[newCount * (2 + stride) + extra.Length];
            m.AsSpan(0, (newCount - (extra.Length > 0 ? 1 : 0)) * (2 + stride)).CopyTo(nw);
            extra.CopyTo(nw.AsSpan((newCount - (extra.Length > 0 ? 1 : 0)) * (2 + stride)));
            tables[mtx] = nw;

            h = (byte[])h.Clone();
            BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(countOff), (ushort)newCount);
            tables[hhea] = h;
        }
    }

    /// <summary>used 的复合组件闭包（null = 数据异常，放弃）。</summary>
    static bool[]? ComputeClosure(ICollection<int> used, int numGlyphs, int[] offs, byte[] glyf)
    {
        var keep = new bool[numGlyphs];
        var stack = new Stack<int>(used);
        foreach (var g in used)
        {
            if (g < 0 || g >= numGlyphs) return null;
            keep[g] = true;
        }
        while (stack.Count > 0)
        {
            int g = stack.Pop();
            int len = offs[g + 1] - offs[g];
            if (len <= 0) continue;
            int p = offs[g];
            short contours = BinaryPrimitives.ReadInt16BigEndian(glyf.AsSpan(p));
            if (contours >= 0) continue;   // simple：无引用
            p += 10;                        // 头（含 bbox）
            int guard = 0;
            while (guard++ < 512)
            {
                if (p + 4 > offs[g + 1]) return null;
                ushort flags = BinaryPrimitives.ReadUInt16BigEndian(glyf.AsSpan(p));
                ushort idx = BinaryPrimitives.ReadUInt16BigEndian(glyf.AsSpan(p + 2));
                p += 4;
                int argSize = (flags & 0x0001) != 0 ? 4 : 2;
                p += argSize;
                if ((flags & 0x0008) != 0) p += 2;        // 1×1 缩放
                else if ((flags & 0x0040) != 0) p += 4;   // X/Y 缩放
                else if ((flags & 0x0080) != 0) p += 8;   // 2×2
                if (idx < numGlyphs && !keep[idx])
                {
                    keep[idx] = true;
                    stack.Push(idx);
                }
                if ((flags & 0x0020) == 0) break;   // MORE_COMPONENTS
            }
        }
        return keep;
    }

    static byte[] BuildSfnt(Dictionary<uint, byte[]> tables)
    {
        var keptTags = tables.Keys.ToArray();
        Array.Sort(keptTags);
        int n = keptTags.Length;
        int dirSize = 12 + 16 * n;
        int total = dirSize;
        foreach (var t in keptTags) total += (tables[t].Length + 3) & ~3;
        var buf = new byte[total];
        ushort searchR = (ushort)(HighestPowerOfTwo(n) * 16);
        BinaryPrimitives.WriteUInt32BigEndian(buf, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6), searchR);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8), (ushort)(n > 0 ? Log2Int(n) : 0));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10), (ushort)(n * 16 - searchR));

        int headDirOffset = -1;
        int pos = dirSize;
        for (int i = 0; i < n; i++)
        {
            uint tag = keptTags[i];
            var d = tables[tag];
            int dir = 12 + i * 16;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(dir), tag);
            uint sum = Checksum(d);
            if (tag == TagHead) { headDirOffset = dir; sum = 0; }   // 占位，最后补
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(dir + 4), sum);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(dir + 8), (uint)pos);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(dir + 12), (uint)d.Length);
            d.CopyTo(buf.AsSpan(pos));
            pos += (d.Length + 3) & ~3;
        }

        // head.checkSumAdjustment = 0xB1B0AFBA − 整文件校验和（计算时该字段与目录条目均为 0）
        uint whole = Checksum(buf);
        uint adjust = 0xB1B0AFBAu - whole;
        var headT = tables[TagHead];
        BinaryPrimitives.WriteUInt32BigEndian(headT.AsSpan(8), adjust);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(headDirOffset + 4), Checksum(headT));
        return buf;

        static int HighestPowerOfTwo(int n) { int p = 1; while (p * 2 <= n) p *= 2; return p; }
        static int Log2Int(int n) { int l = 0; while (n > 1) { n /= 2; l++; } return l; }
    }

    static uint Checksum(ReadOnlySpan<byte> d)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= d.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(d.Slice(i));
        if (i < d.Length)
        {
            uint last = 0;
            for (int k = 0; k < 4; k++)
                last |= (uint)((i + k < d.Length ? d[i + k] : 0) << (24 - 8 * k));
            sum += last;
        }
        return sum;
    }
}
