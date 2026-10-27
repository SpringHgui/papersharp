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
    const uint TagCmap = 0x636D6170;   // 'cmap'
    const uint TagName = 0x6E616D65;   // 'name'
    const uint TagColr = 0x434F4C52;   // 'COLR'
    const uint TagHmtx = 0x686D7478;   // 'hmtx'
    const uint TagVmtx = 0x766D7478;   // 'vmtx'
    const uint TagHhea = 0x68686561;   // 'hhea'
    const uint TagVhea = 0x76686561;   // 'vhea'

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

    /// <summary>需要**改写**的表：其余表一律原样透传。glyf/loca 不在此列——它们由
    /// <see cref="BuildSfnt"/> 按保留字形直接从源流搬进输出缓冲，整表载入会把这份收益吐回去。</summary>
    static readonly uint[] Rewritable =
        { TagHead, TagMaxp, TagHmtx, TagVmtx, TagHhea, TagVhea, TagCmap, TagName, TagColr };

    /// <summary>裁出的字体字节；失败/不适用返回 null。</summary>
    public static byte[]? Subset(SKTypeface tf, ICollection<int> usedGlyphs)
    {
        SfntFont? f = null;
        try
        {
            f = SfntFont.Open(tf);
            if (f == null) return null;
            if (!f.Has(TagGlyf) || !f.Has(TagLoca) || !f.Has(TagHead) || !f.Has(TagMaxp)) return null;   // CFF/OTF：不裁

            int numGlyphs = f.U16(TagMaxp, 4);
            if (numGlyphs <= 0) return null;
            short locaFmt = f.I16(TagHead, 50);

            // loca → 每个 GID 的 [start,end)
            var loca = f.Read(TagLoca);
            if (loca == null) return null;
            var offs = new int[numGlyphs + 1];
            for (int g = 0; g <= numGlyphs; g++)
            {
                if (locaFmt == 0)
                {
                    if (g * 2 + 2 > loca.Length) return null;
                    offs[g] = BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(g * 2)) * 2;
                }
                else
                {
                    if (g * 4 + 4 > loca.Length) return null;
                    offs[g] = BinaryPrimitives.ReadInt32BigEndian(loca.AsSpan(g * 4));
                }
            }
            int glyfLen = f.Size(TagGlyf);
            for (int g = 0; g <= numGlyphs; g++)
                if (offs[g] < 0 || offs[g] > glyfLen) return null;

            // COLR v1 预扫描（Segoe UI Emoji 这类彩色字体）：图层轮廓字形从不由 usedGlyphs
            // 直接驱动——Skia 画 base 字形时按 COLR 的 Paint 图展开图层——不把它们并进闭包，
            // 裁出来的就是「彩色层空字形」（§11.4 的 emoji 变黑白根因）。解析任何意外 →
            // 整张 face 放弃裁剪（维持整嵌），绝不带着半张 COLR 出图。
            Colrv1? colr = null;
            if (f.Has(TagColr))
            {
                var colrBytes = f.Read(TagColr);
                if (colrBytes == null) return null;
                colr = Colrv1.TryParse(colrBytes, numGlyphs);
                if (colr == null) return null;
                if (!colr.CollectSeeds(usedGlyphs, out var seeds)) return null;
                usedGlyphs = seeds;
            }

            // 复合闭包：被引用组件必须一起保留
            var keep = ComputeClosure(usedGlyphs, numGlyphs, offs, f);
            if (keep == null) return null;   // 引用越界等异常数据：放弃裁剪保正确

            // 只有**要改写**的表才读进托管内存：head/maxp 几十字节、hmtx/vmtx ≤4B×GID、
            // cmap/name/COLR 几十 KB；glyf（源字体里 10MB 量级的那张）全程留在 stream 上，
            // 按保留字形逐段复制进输出缓冲。旧实现把 21 张表全份搬进 Dictionary 再拼，
            // 一份 18.6MB 的系统雅黑字面要吃掉 21MB LOH，且每张 face、每次渲染都重来一遍。
            var tables = new Dictionary<uint, byte[]>();
            foreach (var tag in Rewritable)
            {
                if (!f.Has(tag)) continue;
                var d = f.Read(tag);
                if (d == null) return null;
                tables[tag] = d;
            }

            // hmtx/vmtx 是「每个 GID 一条」的定长数组，不跟着字形数缩的话，
            // 只用 114 个字形的发票仍要背 6763 条度量（hmtx 30KB + vmtx 16KB × 每个字重）。
            TrimMetrics(tables, keep, numGlyphs);

            // cmap 是「每码位一条」的全量映射：发票只用 ~30 个字形，却要背 7,938（打包子集）
            // ~30,209（系统字体）条 —— 实测占嵌入字体 raw 字节的 27~48%（PERF_REPORT §13）。
            // 剪到只留保留字形的映射；任何解析意外都整表退回原样。
            PruneCmap(tables, keep);
            PruneName(tables);

            // COLR v1 重写：只留可达 base/图层/Paint；引用的轮廓字形必须都已在 keep 里。
            if (colr != null)
            {
                if (!colr.Prune(keep, out var colrNew)) return null;
                tables[TagColr] = colrNew!;
            }

            var rr = BuildSfnt(f, tables, keep, offs, numGlyphs);
            return rr;
        }
        catch { return null; }   // 任何解析意外都退回“不裁”，绝不出坏字体
        finally { f?.Dispose(); }
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

    /// <summary>
    /// cmap 收口：把每张可识别的子表重建为「只含保留字形」的映射。
    /// 保留字形的 (码位 → GID) 逐位不变；未保留码位从映射里消失，查询语义等同「字形不存在」
    /// （GetGlyph → 0）。引擎侧永远用**原字体**做查询/整形（本子集只在 PDF 嵌入与按 GID 绘制时出现），
    /// 所以这条剪枝对渲染零影响；PDF 阅读器对 CIDFontType2+/CIDToGIDMap Identity 的嵌入字体
    /// 也不走 cmap 取字形。支持格式 0/4/6/12，其余格式原样保留；任何异常 → 整表退回原样。
    /// </summary>
    static void PruneCmap(Dictionary<uint, byte[]> tables, bool[] keep)
    {
        try
        {
            if (!tables.TryGetValue(TagCmap, out var cmap)) return;
            int numSub = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(2));
            if (numSub <= 0 || 4 + 8 * numSub > cmap.Length) return;

            var rebuilt = new List<byte[]>();
            var recs = new List<(ushort plat, ushort enc)>();
            for (int i = 0; i < numSub; i++)
            {
                int rec = 4 + i * 8;
                ushort plat = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(rec));
                ushort enc = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(rec + 2));
                int off = (int)BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(rec + 4));
                byte[] sub;
                if (off <= 0 || off + 4 > cmap.Length)
                    continue;   // 记录不成形：整表退回
                ushort fmt = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(off));
                try
                {
                    sub = fmt switch
                    {
                        0 => RebuildFmt0(cmap, off, keep),
                        4 => RebuildFmt4(cmap, off, keep),
                        6 => RebuildFmt6(cmap, off, keep),
                        12 => RebuildFmt12(cmap, off, keep),
                        _ => cmap.Skip(off).ToArray(),   // 2/8/10/13/14 等罕见格式：原样保留
                    };
                }
                catch
                {
                    return;   // 单表解析失败 → 整张 cmap 退回原样，绝不赌半个 cmap
                }
                recs.Add((plat, enc));
                rebuilt.Add(sub);
            }
            if (rebuilt.Count != numSub) return;   // 有跳过的记录 → 退回

            int header = 4 + 8 * numSub;
            int total = header;
            var offs = new int[numSub];
            for (int i = 0; i < numSub; i++)
            {
                offs[i] = total;
                total += (rebuilt[i].Length + 3) & ~3;
            }
            var nc = new byte[total];
            BinaryPrimitives.WriteUInt16BigEndian(nc.AsSpan(0), 0);            // version
            BinaryPrimitives.WriteUInt16BigEndian(nc.AsSpan(2), (ushort)numSub);
            for (int i = 0; i < numSub; i++)
            {
                int rec = 4 + i * 8;
                BinaryPrimitives.WriteUInt16BigEndian(nc.AsSpan(rec), recs[i].plat);
                BinaryPrimitives.WriteUInt16BigEndian(nc.AsSpan(rec + 2), recs[i].enc);
                BinaryPrimitives.WriteUInt32BigEndian(nc.AsSpan(rec + 4), (uint)offs[i]);
                rebuilt[i].CopyTo(nc.AsSpan(offs[i]));
            }
            tables[TagCmap] = nc;
        }
        catch { /* 保守：cmap 原样保留 */ }
    }

    /// <summary>格式 0：256 字节映射，把未保留 GID 清 0（表本来就小，不缩长度）。</summary>
    static byte[] RebuildFmt0(byte[] cmap, int off, bool[] keep)
    {
        int len = 262;
        if (off + len > cmap.Length) throw new InvalidDataException();
        var d = new byte[len];
        cmap.AsSpan(off, len).CopyTo(d);
        for (int cp = 0; cp < 256; cp++)
        {
            int g = d[6 + cp];
            if (g >= keep.Length || !keep[g]) d[6 + cp] = 0;
        }
        return d;
    }

    /// <summary>格式 4（BMP 段）：展开 → 过滤 → 以 delta 段编码重建（rangeOffset 全 0）。</summary>
    static byte[] RebuildFmt4(byte[] cmap, int off, bool[] keep)
    {
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(off + 2));
        if (off + length > cmap.Length) throw new InvalidDataException();
        int segCount = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(off + 6)) / 2;
        int endOff = off + 14;
        int startOff = endOff + 2 * segCount + 2;
        int deltaOff = startOff + 2 * segCount;
        int rangeOff = deltaOff + 2 * segCount;
        if (rangeOff + 2 * segCount > off + length) throw new InvalidDataException();

        // 展开 (cp → gid) 并过滤；0xFFFF 哨兵单独处理
        var pairs = new List<(int cp, int gid)>();
        for (int s = 0; s < segCount; s++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(endOff + 2 * s));
            int start = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(startOff + 2 * s));
            short delta = BinaryPrimitives.ReadInt16BigEndian(cmap.AsSpan(deltaOff + 2 * s));
            int ro = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(rangeOff + 2 * s));
            if (start == 0xFFFF && end == 0xFFFF) continue;
            for (int cp = start; cp <= end; cp++)
            {
                int gid;
                if (ro == 0) gid = (cp + delta) & 0xFFFF;
                else
                {
                    int addr = rangeOff + 2 * s + ro + (cp - start) * 2;
                    if (addr + 2 > cmap.Length) throw new InvalidDataException();
                    gid = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(addr));
                    if (gid != 0) gid = (gid + delta) & 0xFFFF;
                }
                if (gid != 0 && gid < keep.Length && keep[gid]) pairs.Add((cp, gid));
            }
        }
        return BuildFmt4(pairs);
    }

    /// <summary>以 delta 段编码重建格式 4（含 0xFFFF 哨兵段；段内 (gid−cp) 恒定才能并成一段）。</summary>
    static byte[] BuildFmt4(List<(int cp, int gid)> pairs)
    {
        pairs.Sort((a, b) => a.cp != b.cp ? a.cp - b.cp : a.gid - b.gid);
        var segEnd = new List<int>();
        var segDelta = new List<short>();
        int i = 0;
        while (i < pairs.Count)
        {
            int start = pairs[i].cp, end = start;
            short delta = (short)((pairs[i].gid - pairs[i].cp) & 0xFFFF);
            int want = (pairs[i].gid - pairs[i].cp) & 0xFFFF;
            int j = i + 1;
            while (j < pairs.Count && pairs[j].cp == end + 1 && ((pairs[j].gid - pairs[j].cp) & 0xFFFF) == want)
            {
                end = pairs[j].cp;
                j++;
            }
            segEnd.Add(end);
            segDelta.Add((short)delta);
            i = j;
        }
        int segCount = segEnd.Count + 1;   // + 0xFFFF 哨兵
        var d = new byte[16 + 8 * segCount];
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(0), 4);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(2), (ushort)d.Length);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(6), (ushort)(segCount * 2));
        int sr = 2, es = 0;
        while (sr * 2 <= segCount) { sr *= 2; es++; }
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(8), (ushort)(sr * 2));
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(10), (ushort)es);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(12), (ushort)(segCount * 2 - sr * 2));
        // 布局：endCodes@14 | reservedPad | startCodes@16+2n | idDelta@16+4n | idRangeOffset@16+6n
        int idx = 0;
        for (int s = 0; s < segEnd.Count; s++)
        {
            int end = segEnd[s];
            int start = pairs[idx].cp;
            while (idx < pairs.Count && pairs[idx].cp <= end) idx++;
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(14 + 2 * s), (ushort)end);
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(16 + 2 * segCount + 2 * s), (ushort)start);
            BinaryPrimitives.WriteInt16BigEndian(d.AsSpan(16 + 4 * segCount + 2 * s), segDelta[s]);
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(16 + 6 * segCount + 2 * s), 0);
        }
        // 哨兵段：0xFFFF → delta 1（映射到 0）
        int e = 14 + 2 * segEnd.Count;
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(e), 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(16 + 2 * segCount + 2 * segEnd.Count), 0xFFFF);
        BinaryPrimitives.WriteInt16BigEndian(d.AsSpan(16 + 4 * segCount + 2 * segEnd.Count), 1);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(16 + 6 * segCount + 2 * segEnd.Count), 0);
        return d;
    }

    /// <summary>格式 6（连续段）：过滤后重建。</summary>
    static byte[] RebuildFmt6(byte[] cmap, int off, bool[] keep)
    {
        int first = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(off + 6));
        int count = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(off + 8));
        if (off + 10 + 2 * count > cmap.Length) throw new InvalidDataException();
        var pairs = new List<(int cp, int gid)>();
        for (int i = 0; i < count; i++)
        {
            int gid = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(off + 10 + 2 * i));
            if (gid != 0 && gid < keep.Length && keep[gid]) pairs.Add((first + i, gid));
        }
        if (pairs.Count == 0) throw new InvalidDataException();   // 空表退回原样更稳
        int nfirst = pairs[0].cp, ncount = pairs[^1].cp - nfirst + 1;
        var d = new byte[10 + 2 * ncount];
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(0), 6);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(2), (ushort)d.Length);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(6), (ushort)nfirst);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(8), (ushort)ncount);
        var byCp = pairs.ToDictionary(p => p.cp, p => p.gid);
        for (int i = 0; i < ncount; i++)
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(10 + 2 * i), (ushort)(byCp.TryGetValue(nfirst + i, out var g) ? g : 0));
        return d;
    }

    /// <summary>格式 12（分组）：展开 → 过滤 → 重建连续组（组内 gid−cp 恒定）。</summary>
    static byte[] RebuildFmt12(byte[] cmap, int off, bool[] keep)
    {
        uint nGroups = BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(off + 12));
        long groupsAt = off + 16;
        if (groupsAt + 12 * nGroups > cmap.Length) throw new InvalidDataException();
        var pairs = new List<(int cp, int gid)>();
        for (uint g = 0; g < nGroups; g++)
        {
            long rec = groupsAt + 12 * g;
            uint s = BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan((int)rec));
            uint e = BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan((int)rec + 4));
            uint gs = BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan((int)rec + 8));
            for (uint cp = s; cp <= e && cp - s < 0x110000; cp++)
            {
                uint gid = gs + (cp - s);
                if (gid != 0 && gid < keep.Length && keep[gid]) pairs.Add(((int)cp, (int)gid));
            }
        }
        if (pairs.Count == 0) throw new InvalidDataException();
        pairs.Sort((a, b) => a.cp - b.cp);
        var groups = new List<(uint s, uint e, uint g)>();
        int i = 0;
        while (i < pairs.Count)
        {
            uint start = (uint)pairs[i].cp, end = start, gs = (uint)pairs[i].gid;
            int j = i + 1;
            while (j < pairs.Count && pairs[j].cp == end + 1 && pairs[j].gid == gs + (pairs[j].cp - start))
            {
                end = (uint)pairs[j].cp;
                j++;
            }
            groups.Add((start, end, gs));
            i = j;
        }
        var d = new byte[16 + 12 * groups.Count];
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(0), 12);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), (uint)d.Length);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), (uint)groups.Count);
        for (int g = 0; g < groups.Count; g++)
        {
            int rec = 16 + 12 * g;
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(rec), groups[g].s);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(rec + 4), groups[g].e);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(rec + 8), groups[g].g);
        }
        return d;
    }

    /// <summary>
    /// name 瘦身：只留名称链必需的记录（0 版权/1 家族/2 子族/3 唯一/4 全名/6 PS 名/16-17 变体），
    /// 其余（许可证备注、供应商 URL、示例文本……雅黑里 3~4KB）整条删掉。家族名必须原样
    /// —— SKTypeface.FamilyName 与 TtfSubsetterTests 的 FamilyName 断言吃它。
    /// </summary>
    static void PruneName(Dictionary<uint, byte[]> tables)
    {
        try
        {
            if (!tables.TryGetValue(TagName, out var name)) return;
            int count = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(2));
            int strOff = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(4));
            if (6 + 12 * count > name.Length || strOff > name.Length) return;

            var keep = new List<(ushort plat, ushort enc, ushort lang, ushort id, byte[] str)>();
            for (int i = 0; i < count; i++)
            {
                int rec = 6 + 12 * i;
                ushort id = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec + 6));
                if (id is not (0 or 1 or 2 or 3 or 4 or 6 or 16 or 17)) continue;
                int len = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec + 8));
                int off = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec + 10));
                int at = strOff + off;
                if (at + len > name.Length) continue;
                keep.Add((
                    BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec)),
                    BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec + 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec + 4)),
                    id, name.AsSpan(at, len).ToArray()));
            }
            if (keep.Count == 0) return;

            int header = 6 + 12 * keep.Count;
            int total = header;
            var offs = new int[keep.Count];
            for (int i = 0; i < keep.Count; i++)
            {
                offs[i] = total - header;
                total += keep[i].str.Length;
            }
            var n = new byte[total];
            BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(0), 0);
            BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(2), (ushort)keep.Count);
            BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(4), (ushort)header);
            for (int i = 0; i < keep.Count; i++)
            {
                int rec = 6 + 12 * i;
                var (plat, enc, lang, id, str) = keep[i];
                BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(rec), plat);
                BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(rec + 2), enc);
                BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(rec + 4), lang);
                BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(rec + 6), id);
                BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(rec + 8), (ushort)str.Length);
                BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(rec + 10), (ushort)offs[i]);
                str.CopyTo(n.AsSpan(header + offs[i]));
            }
            tables[TagName] = n;
        }
        catch { /* 保守：name 原样保留 */ }
    }

    /// <summary>used 的复合组件闭包（null = 数据异常，放弃）。字形字节按需从源流读，不整表载入。</summary>
    static bool[]? ComputeClosure(ICollection<int> used, int numGlyphs, int[] offs, SfntFont f)
    {
        var keep = new bool[numGlyphs];
        var stack = new Stack<int>(used);
        foreach (var g in used)
        {
            if (g < 0 || g >= numGlyphs) return null;
            keep[g] = true;
        }
        var scratch = new byte[1024];
        while (stack.Count > 0)
        {
            int g = stack.Pop();
            int len = offs[g + 1] - offs[g];
            if (len <= 0) continue;
            if (len > 1 << 20) return null;                       // 异常数据：单字形 1MB 已远超任何真实轮廓
            if (scratch.Length < len) scratch = new byte[Math.Max(len, scratch.Length * 2)];
            if (f.ReadInto(TagGlyf, offs[g], scratch, 0, len) != len) return null;
            short contours = BinaryPrimitives.ReadInt16BigEndian(scratch);
            if (contours >= 0) continue;   // simple：无引用
            int p = 10;                        // 头（含 bbox）
            int guard = 0;
            while (guard++ < 512)
            {
                if (p + 4 > len) return null;
                ushort flags = BinaryPrimitives.ReadUInt16BigEndian(scratch.AsSpan(p));
                ushort idx = BinaryPrimitives.ReadUInt16BigEndian(scratch.AsSpan(p + 2));
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

    /// <summary>
    /// 拼出新的 sfnt：**一次分配、一处落盘**。表内容按最终偏移直接写入输出缓冲——
    /// 需要改写的表从 <paramref name="tables"/> 拷，glyf 按保留字形从源流逐段拷，其余表原样直传。
    /// 旧实现先建 Dictionary&lt;tag, byte[]&gt; 装下整份字体再 BuildSfnt 复制一遍，峰值 = 源表合计 + 输出；
    /// 现在峰值 = 输出（几十~几百 KB）+ 一张小表。
    /// </summary>
    static byte[]? BuildSfnt(SfntFont f, Dictionary<uint, byte[]> tables, bool[] keep, int[] offs, int numGlyphs)
    {
        // —— 新 glyf 布局（只算长度，不读字节）——
        var newOff = new int[numGlyphs + 1];
        int glyfNew = 0;
        for (int g = 0; g < numGlyphs; g++)
        {
            newOff[g] = glyfNew;
            int len = offs[g + 1] - offs[g];
            if (len <= 0 || !keep[g]) continue;
            glyfNew += len;
            glyfNew += (4 - (glyfNew & 3)) & 3;   // 原样拷贝保留项，4 字节对齐
        }
        newOff[numGlyphs] = glyfNew;

        short newFmt = (short)(glyfNew <= 0x1FFFE ? 0 : 1);
        var newLoca = new byte[newFmt == 0 ? (numGlyphs + 1) * 2 : (numGlyphs + 1) * 4];
        for (int g = 0; g <= numGlyphs; g++)
        {
            if (newFmt == 0) BinaryPrimitives.WriteUInt16BigEndian(newLoca.AsSpan(g * 2), (ushort)(newOff[g] / 2));
            else BinaryPrimitives.WriteInt32BigEndian(newLoca.AsSpan(g * 4), newOff[g]);
        }
        tables[TagLoca] = newLoca;
        var head = (byte[])tables[TagHead].Clone();
        head[50] = (byte)(newFmt >> 8); head[51] = (byte)newFmt;     // indexToLocFormat 随新 loca
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);    // checkSumAdjustment 清零，最后回填
        tables[TagHead] = head;

        // —— 表清单与最终偏移 ——
        var emitTags = new List<uint>(f.Tags.Count);
        var emitLen = new List<int>(f.Tags.Count);
        foreach (var tag in f.Tags)
        {
            if (Drop.Contains(tag)) continue;
            int len = tag == TagGlyf ? glyfNew
                    : tables.TryGetValue(tag, out var d) ? d.Length
                    : f.Size(tag);
            if (len <= 0) continue;
            emitTags.Add(tag);
            emitLen.Add(len);
        }
        int n = emitTags.Count;
        if (n == 0) return null;

        int pos = 12 + 16 * n;
        var at = new int[n];
        for (int i = 0; i < n; i++) { at[i] = pos; pos += (emitLen[i] + 3) & ~3; }
        var buf = new byte[pos];   // 精确大小：不必再复制一次（旧实现 ArrayPool + ToArray 两跳）

        int headAt = -1, headDir = -1;
        for (int i = 0; i < n; i++)
        {
            uint tag = emitTags[i];
            int dst = at[i], len = emitLen[i];
            if (tag == TagGlyf)
            {
                for (int g = 0; g < numGlyphs; g++)
                {
                    if (!keep[g]) continue;
                    int l = offs[g + 1] - offs[g];
                    if (l <= 0) continue;
                    if (f.ReadInto(TagGlyf, offs[g], buf, dst + newOff[g], l) != l) return null;
                }
            }
            else if (tables.TryGetValue(tag, out var d)) d.CopyTo(buf, dst);
            else if (f.ReadInto(tag, 0, buf, dst, len) != len) return null;
            if (tag == TagHead) { headAt = dst; headDir = 12 + 16 * i; }
        }

        // —— 目录 ——
        ushort searchR = (ushort)(HighestPowerOfTwo(n) * 16);
        BinaryPrimitives.WriteUInt32BigEndian(buf, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6), searchR);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8), (ushort)Log2Int(n));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10), (ushort)(n * 16 - searchR));
        for (int i = 0; i < n; i++)
        {
            int dir = 12 + 16 * i;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(dir), emitTags[i]);
            uint sum = emitTags[i] == TagHead ? 0 : Checksum(buf.AsSpan(at[i], emitLen[i]));
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(dir + 4), sum);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(dir + 8), (uint)at[i]);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(dir + 12), (uint)emitLen[i]);
        }
        if (headAt < 0) return null;

        // head.checkSumAdjustment = 0xB1B0AFBA − 整文件校验和（计算时该字段与目录条目均为 0）
        uint adjust = 0xB1B0AFBAu - Checksum(buf);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(headAt + 8), adjust);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(headDir + 4), Checksum(buf.AsSpan(headAt, emitLen[emitTags.IndexOf(TagHead)])));
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

    /// <summary>
    /// COLR v1 的可达性闭包 + 重写器。字段布局逐格式对照 fontTools otData 的 PaintFormat1..32：
    /// 子 Paint 偏移是 <b>Offset24、自该 Paint 表首起算</b>；PaintColrLayers 的 firstLayerIndex 是
    /// <b>uint32</b> 指向 LayerList 表项下标；BaseGlyphPaintRecord 的 paint 偏移经 seguiemj 实测为
    /// <b>从 BaseGlyphList 表首起算</b>。只处理静态表：带 DeltaSetIndexMap/ItemVariationStore 的
    /// 可变彩色字体整张放弃（上层维持整嵌，绝不出半张 COLR）。
    /// 收益（PERF_REPORT §13.4 刀 D）：Segoe UI Emoji 的 COLR 表 7.4 MB、188,865 个图层 Paint，
    /// 一页文档真正画到的 emoji 只有几十个 base。
    /// </summary>
    sealed class Colrv1
    {
        readonly byte[] t;
        readonly int numGlyphs;
        readonly int v0Count, v0BaseOff, v0LayerOff, v0LayerCount;
        readonly int bglOff, llOff, clipOff;
        readonly int bglCount, llCount;

        readonly Dictionary<ushort, int> baseByGid = new();     // v1 BaseGlyphList：gid → 记录下标
        readonly Dictionary<ushort, int> v0ByGid = new();       // v0 BaseGlyphRecord：gid → 记录下标
        readonly HashSet<int> reachBaseIdx = new();
        readonly HashSet<int> reachV0Idx = new();
        readonly HashSet<int> reachLayerIdx = new();
        readonly HashSet<ushort> paintGlyphs = new();
        readonly Stack<int> todo = new();
        bool bad;

        Colrv1(byte[] colr, int numGlyphs, int v0Count, int v0BaseOff, int v0LayerOff, int v0LayerCount,
               int bglOff, int llOff, int clipOff, int bglCount, int llCount)
        {
            t = colr; this.numGlyphs = numGlyphs;
            this.v0Count = v0Count; this.v0BaseOff = v0BaseOff; this.v0LayerOff = v0LayerOff; this.v0LayerCount = v0LayerCount;
            this.bglOff = bglOff; this.llOff = llOff; this.clipOff = clipOff;
            this.bglCount = bglCount; this.llCount = llCount;
            for (int i = 0; i < bglCount; i++)
            {
                int rec = bglOff + 4 + 6 * i;
                if (rec + 6 > t.Length) { bad = true; return; }
                baseByGid[(ushort)BE16(t, rec)] = i;
            }
            for (int i = 0; i < v0Count; i++)
            {
                int rec = v0BaseOff + 6 * i;
                if (rec + 6 > t.Length) { bad = true; return; }
                v0ByGid[(ushort)BE16(t, rec)] = i;
            }
        }

        /// <summary>能解析成「静态 COLR v1」才返回实例；v0-only/可变/任何不成形 → null（调用方放弃裁剪）。</summary>
        public static Colrv1? TryParse(byte[] colr, int numGlyphs)
        {
            try
            {
                if (colr.Length < 34 || BE16(colr, 0) != 1) return null;
                int v0c = BE16(colr, 2), v0bo = (int)BEU32(colr, 4), v0lo = (int)BEU32(colr, 8),
                    v0lc = BE16(colr, 12);
                int bgl = (int)BEU32(colr, 14), ll = (int)BEU32(colr, 18), clip = (int)BEU32(colr, 22);
                if ((int)BEU32(colr, 26) != 0 || (int)BEU32(colr, 30) != 0) return null;   // 可变彩色：放弃
                if (bgl <= 0 || ll <= 0 || bgl + 4 > colr.Length || ll + 4 > colr.Length) return null;
                int bglc = (int)BEU32(colr, bgl), llc = (int)BEU32(colr, ll);
                if (bglc < 0 || llc < 0 || bgl + 4 + 6 * (long)bglc > colr.Length ||
                    ll + 4 + 4 * (long)llc > colr.Length) return null;
                return new Colrv1(colr, numGlyphs, v0c, v0bo, v0lo, v0lc, bgl, ll, clip, bglc, llc);
            }
            catch { return null; }
        }

        /// <summary>沿 Paint 图把 used 命中的 base 走闭包；seeds = used ∪ 图层轮廓字形（供 glyf 闭包用）。</summary>
        public bool CollectSeeds(ICollection<int> used, out List<int> seeds)
        {
            seeds = null!;
            if (bad) return false;
            var visitedPaint = new HashSet<int>();
            foreach (int g in used)
            {
                if (g >= 0 && g < 0x10000 && baseByGid.TryGetValue((ushort)g, out var bi))
                    EnqueueBase(bi, visitedPaint);
                if (g >= 0 && g < 0x10000 && v0ByGid.ContainsKey((ushort)g))
                {
                    reachV0Idx.Add(v0ByGid[(ushort)g]);
                    AddV0Layers(v0ByGid[(ushort)g]);
                }
            }
            while (todo.Count > 0 && !bad)
                EnqueueBase(todo.Pop(), visitedPaint);
            if (bad) return false;
            var set = new HashSet<int>(used);
            foreach (var g in paintGlyphs)
            {
                if (g >= numGlyphs) return false;
                set.Add(g);
            }
            seeds = new List<int>(set);
            return true;
        }

        void EnqueueBase(int bi, HashSet<int> visitedPaint)
        {
            if (!reachBaseIdx.Add(bi) || bad) return;
            int rec = bglOff + 4 + 6 * bi;
            ushort gid = (ushort)BE16(t, rec);
            int off = (int)BEU32(t, rec + 2);
            WalkPaint(bglOff + off, 0, visitedPaint);
            // v0 平行记录：旧查看器用它，图层字形同样要留在闭包里
            if (v0ByGid.TryGetValue(gid, out var v0i))
            {
                reachV0Idx.Add(v0i);
                AddV0Layers(v0i);
            }
        }

        void AddV0Layers(int v0i)
        {
            int rec = v0BaseOff + 6 * v0i;
            int first = BE16(t, rec + 2), n = BE16(t, rec + 4);
            if (first < 0 || n < 0 || v0LayerOff + 4 * (first + (long)n) > t.Length) { bad = true; return; }
            for (int k = 0; k < n; k++)
                paintGlyphs.Add((ushort)BE16(t, v0LayerOff + 4 * (first + k)));
        }

        void WalkPaint(int addr, int depth, HashSet<int> visited)
        {
            if (bad || depth > 96) { bad = true; return; }
            if (addr < 1 || addr >= t.Length || !visited.Add(addr)) return;
            switch (t[addr])
            {
                case 1:   // PaintColrLayers：uint8 num + uint32 firstLayerIndex → LayerList 表项
                {
                    int n = t[addr + 1];
                    int first = (int)BEU32(t, addr + 2);
                    for (int k = 0; k < n; k++)
                    {
                        int idx = first + k;
                        if (idx < 0 || idx >= llCount) { bad = true; return; }
                        reachLayerIdx.Add(idx);
                        WalkPaint(llOff + (int)BEU32(t, llOff + 4 + 4 * idx), depth + 1, visited);
                    }
                    break;
                }
                case 2: case 3: case 4: case 5: case 6: case 7: case 8: case 9:
                    break;   // 叶子（渐变的 ColorLine 无嵌套引用）
                case 11:     // PaintColrGlyph：跨引用另一个 base——必须进可达闭包，否则重写后悬空
                    if (baseByGid.TryGetValue((ushort)BE16(t, addr + 1), out var bi))
                        todo.Push(bi);
                    break;
                case 10:     // PaintGlyph：Offset24 子 paint + glyphID 轮廓进闭包
                    paintGlyphs.Add((ushort)BE16(t, addr + 4));
                    WalkPaint(addr + Off24(t, addr + 1), depth + 1, visited);
                    break;
                case 12: case 13:   // Transform：子 paint + 仿射矩阵（无嵌套）
                    WalkPaint(addr + Off24(t, addr + 1), depth + 1, visited);
                    break;
                case 14: case 15: case 16: case 17: case 18: case 19: case 20: case 21:
                case 22: case 23: case 24: case 25: case 26: case 27: case 28: case 29:
                case 30: case 31:
                    WalkPaint(addr + Off24(t, addr + 1), depth + 1, visited);
                    break;
                case 32:     // PaintComposite：source + backdrop
                    WalkPaint(addr + Off24(t, addr + 1), depth + 1, visited);
                    WalkPaint(addr + Off24(t, addr + 5), depth + 1, visited);
                    break;
                default: bad = true; return;
            }
        }

        /// <summary>产出剪好的 COLR v1；引用字形未全部在 keep 中或结构不成形 → false。</summary>
        public bool Prune(bool[] keep, out byte[]? outTable)
        {
            outTable = null;
            if (bad || reachBaseIdx.Count == 0) return false;
            foreach (var g in paintGlyphs)
                if (!keep[g]) return false;

            var layerOrder = new List<int>(reachLayerIdx);
            layerOrder.Sort();
            var newLayerIdx = new Dictionary<int, int>(layerOrder.Count);
            for (int i = 0; i < layerOrder.Count; i++) newLayerIdx[layerOrder[i]] = i;

            // —— 新 LayerList ——
            var layerBlobs = new List<byte[]>(layerOrder.Count);
            foreach (var oi in layerOrder)
                layerBlobs.Add(WritePaint(llOff + (int)BEU32(t, llOff + 4 + 4 * oi), newLayerIdx));
            int llNew = 4 + 4 * layerBlobs.Count;
            foreach (var b in layerBlobs) llNew += b.Length;

            // —— 新 BaseGlyphList ——
            var baseOrder = new List<int>(reachBaseIdx);
            baseOrder.Sort();
            var baseBlobs = new List<byte[]>(baseOrder.Count);
            foreach (var bi in baseOrder)
                baseBlobs.Add(WritePaint(bglOff + (int)BEU32(t, bglOff + 4 + 6 * bi + 2), newLayerIdx));
            int bglNew = 4 + 6 * baseOrder.Count;
            foreach (var b in baseBlobs) bglNew += b.Length;

            // —— v0 平行数组：kept 记录按 gid 升序，图层记录切片按序拼接 ——
            var v0Order = new List<int>(reachV0Idx);
            v0Order.Sort((a, b) => BE16(t, v0BaseOff + 6 * a).CompareTo(BE16(t, v0BaseOff + 6 * b)));
            int v0LayerNew = 0;
            foreach (var vi in v0Order) v0LayerNew += 4 * BE16(t, v0BaseOff + 6 * vi + 4);
            if (v0Order.Count > 0 && v0LayerNew == 0) return false;   // 记录说有自己的图层却一个都没有：不成形

            var clipTail = clipOff > 0 ? t.Length - clipOff : 0;      // ClipList 原样尾拷（GID 保号 → 引用恒有效）

            int off = 34;
            int offV0Base = off; off += 6 * v0Order.Count;
            int offV0Layer = off; off += v0LayerNew;
            int offBgl = off; off += bglNew;
            int offLl = off; off += llNew;
            int offClip = clipTail > 0 ? off : 0; off += clipTail;
            var o = new byte[off];

            BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(0), 1);
            BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(2), (ushort)v0Order.Count);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(4), (uint)offV0Base);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(8), (uint)offV0Layer);
            BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(12), (ushort)(v0LayerNew / 4));
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(14), (uint)offBgl);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(18), (uint)offLl);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(22), (uint)offClip);

            int p = offV0Base, lp = offV0Layer;
            foreach (var vi in v0Order)
            {
                int src = v0BaseOff + 6 * vi;
                int first = BE16(t, src + 2), n = BE16(t, src + 4);
                BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(p), (ushort)BE16(t, src));
                BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(p + 2), (ushort)(v0LayerNew == 0 ? 0 : (lp - offV0Layer) / 4));
                BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(p + 4), (ushort)n);
                for (int k = 0; k < n; k++)
                {
                    t.AsSpan(v0LayerOff + 4 * (first + k), 4).CopyTo(o.AsSpan(lp));
                    lp += 4;
                }
                p += 6;
            }

            p = offBgl;
            int paintPos = offBgl + 4 + 6 * baseOrder.Count;
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(p), (uint)baseOrder.Count); p += 4;
            for (int i = 0; i < baseOrder.Count; i++)
            {
                int rec = bglOff + 4 + 6 * baseOrder[i];
                BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(p), (ushort)BE16(t, rec));
                BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(p + 2), (uint)(paintPos - offBgl));   // 实测口径：偏移从 BaseGlyphList 表首起算
                baseBlobs[i].CopyTo(o.AsSpan(paintPos));
                paintPos += baseBlobs[i].Length;
                p += 6;
            }

            p = offLl;
            int lpaintPos = offLl + 4 + 4 * layerBlobs.Count;
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(p), (uint)layerBlobs.Count); p += 4;
            for (int i = 0; i < layerBlobs.Count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(p + 4 + 4 * i), (uint)(lpaintPos - offLl));
                layerBlobs[i].CopyTo(o.AsSpan(lpaintPos));
                lpaintPos += layerBlobs[i].Length;
            }

            if (clipTail > 0)
                t.AsSpan(clipOff, clipTail).CopyTo(o.AsSpan(offClip));

            outTable = o;
            return true;
        }

        /// <summary>把一棵 Paint 子树序列化成局部缓冲（自相对偏移在缓冲内自洽，嵌到任何基址都成立）。
        /// 共享子树会被各自复制一份（emoji 单树只有几十个节点，代价可忽略）。</summary>
        byte[] WritePaint(int addr, Dictionary<int, int> newLayerIdx)
        {
            if (bad || addr < 1 || addr >= t.Length) { bad = true; return Array.Empty<byte>(); }
            int fmt = t[addr];
            switch (fmt)
            {
                case 1:   // PaintColrLayers：区间连续 → 单点重映射即可
                {
                    int n = t[addr + 1];
                    int first = (int)BEU32(t, addr + 2);
                    var d = new byte[6];
                    d[0] = 1; d[1] = (byte)n;
                    BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(2), (uint)newLayerIdx[first]);
                    return d;
                }
                case 2: return Slice(addr, 6);
                case 3: return Slice(addr, 10);
                case 4: return WriteGradient(addr, 16, false);
                case 5: return WriteGradient(addr, 20, true);
                case 6: return WriteGradient(addr, 16, false);
                case 7: return WriteGradient(addr, 20, true);
                case 8: return WriteGradient(addr, 12, false);
                case 9: return WriteGradient(addr, 16, true);
                case 10: return WriteOneChild(addr, 6, newLayerIdx);
                case 11: return Slice(addr, 3);
                case 12:
                case 13:
                {
                    int tfSize = fmt == 13 ? 16 : 12;
                    var child = WritePaint(addr + Off24(t, addr + 1), newLayerIdx);
                    var d = new byte[7 + child.Length + tfSize];
                    d[0] = (byte)fmt;
                    Write24(d, 1, 7);
                    child.CopyTo(d, 7);
                    Write24(d, 4, 7 + child.Length);
                    t.AsSpan(addr + Off24(t, addr + 4), tfSize).CopyTo(d.AsSpan(7 + child.Length));
                    return d;
                }
                case 14: return WriteOneChild(addr, 8, newLayerIdx);
                case 15: return WriteOneChild(addr, 12, newLayerIdx);
                case 16: return WriteOneChild(addr, 8, newLayerIdx);
                case 17: return WriteOneChild(addr, 12, newLayerIdx);
                case 18: return WriteOneChild(addr, 12, newLayerIdx);
                case 19: return WriteOneChild(addr, 16, newLayerIdx);
                case 20: return WriteOneChild(addr, 6, newLayerIdx);
                case 21: return WriteOneChild(addr, 10, newLayerIdx);
                case 22: return WriteOneChild(addr, 10, newLayerIdx);
                case 23: return WriteOneChild(addr, 14, newLayerIdx);
                case 24: return WriteOneChild(addr, 6, newLayerIdx);
                case 25: return WriteOneChild(addr, 10, newLayerIdx);
                case 26: return WriteOneChild(addr, 10, newLayerIdx);
                case 27: return WriteOneChild(addr, 14, newLayerIdx);
                case 28: return WriteOneChild(addr, 8, newLayerIdx);
                case 29: return WriteOneChild(addr, 12, newLayerIdx);
                case 30: return WriteOneChild(addr, 12, newLayerIdx);
                case 31: return WriteOneChild(addr, 16, newLayerIdx);
                case 32:
                {
                    var src = WritePaint(addr + Off24(t, addr + 1), newLayerIdx);
                    var bk = WritePaint(addr + Off24(t, addr + 5), newLayerIdx);
                    var d = new byte[8 + src.Length + bk.Length];
                    d[0] = 32;
                    Write24(d, 1, 8);
                    src.CopyTo(d, 8);
                    d[4] = t[addr + 4];                                   // CompositeMode uint8
                    Write24(d, 5, 8 + src.Length);
                    bk.CopyTo(d, 8 + src.Length);
                    return d;
                }
                default: bad = true; return Array.Empty<byte>();
            }
        }

        /// <summary>「固定头 + 单个子 Paint」类格式（10、14..31）：子表挪到固定头之后。</summary>
        byte[] WriteOneChild(int addr, int fixedSize, Dictionary<int, int> newLayerIdx)
        {
            var child = WritePaint(addr + Off24(t, addr + 1), newLayerIdx);
            if (bad) return Array.Empty<byte>();
            var d = new byte[fixedSize + child.Length];
            t.AsSpan(addr, fixedSize).CopyTo(d);
            Write24(d, 1, fixedSize);
            child.CopyTo(d, fixedSize);
            return d;
        }

        byte[] WriteGradient(int addr, int fixedSize, bool varLine)
        {
            int lineAddr = addr + Off24(t, addr + 1);
            if (lineAddr + 3 > t.Length) { bad = true; return Array.Empty<byte>(); }
            int n = BE16(t, lineAddr + 1);
            int lineSize = 3 + (varLine ? 10 : 6) * n;
            if (n < 0 || lineAddr + lineSize > t.Length) { bad = true; return Array.Empty<byte>(); }
            var d = new byte[fixedSize + lineSize];
            t.AsSpan(addr, fixedSize).CopyTo(d);
            Write24(d, 1, fixedSize);
            t.AsSpan(lineAddr, lineSize).CopyTo(d.AsSpan(fixedSize));
            return d;
        }

        byte[] Slice(int addr, int size)
        {
            if (addr + size > t.Length) { bad = true; return Array.Empty<byte>(); }
            return t.AsSpan(addr, size).ToArray();
        }

        static int Off24(byte[] b, int i) => (b[i] << 16) | (b[i + 1] << 8) | b[i + 2];
        static void Write24(byte[] b, int i, int v) { b[i] = (byte)(v >> 16); b[i + 1] = (byte)(v >> 8); b[i + 2] = (byte)v; }
        static int BE16(byte[] b, int i) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i));
        static uint BEU32(byte[] b, int i) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i));
    }
}

/// <summary>
/// 一张字面的**表字节视图**：优先用 <see cref="SKTypeface.OpenStream"/> 直接定位字体文件里的表段
/// （读一表、写一表，永不把整份字体搬进托管堆）；拿不到可用流时退回
/// <see cref="SKTypeface.GetTableData(uint)"/>，行为与旧实现一致（此时整表进托管堆）。
/// 为什么值得这么绕：`GetTableData` 只能整表取，一份系统雅黑的 21 张表合计 18.6MB，
/// 而子集真正要保留的通常不到 300KB——旧实现每次裁剪都把这 18.6MB 全份读进 LOH 再拼一遍，
/// 峰值 21MB/face，一批 4 张 CJK face 就是 ~80MB 的 GC 高水位（见 PERF_REPORT §14）。
/// sfnt 目录解析失败（woff2 原始流、畸形表）一律返回 null，由调用方退回整嵌——绝不出坏字体。
/// </summary>
internal sealed class SfntFont : IDisposable
{
    readonly SKTypeface _tf;
    readonly SKStreamAsset? _stream;
    readonly int _ttcIndex;
    readonly Dictionary<uint, (long Off, int Len)> _dir = new();

    /// <summary>本面的表标记，升序（与 sfnt 目录序一致）；输出目录按它排。</summary>
    public List<uint> Tags { get; } = new();

    /// <summary>回退路径的表缓存（只有这条路上整表会进托管堆）。</summary>
    Dictionary<uint, byte[]>? _cache;

    SfntFont(SKTypeface tf, SKStreamAsset? stream, int ttcIndex)
    {
        _tf = tf; _stream = stream; _ttcIndex = ttcIndex;
    }

    public static SfntFont? Open(SKTypeface tf)
    {
        SKStreamAsset? stream = null;
        int ttc = 0;
        try { stream = tf.OpenStream(out ttc); } catch { stream = null; }
        if (stream != null)
        {
            var f = new SfntFont(tf, stream, ttc);
            if (f.TryParseDirectory()) return f;
            f.Dispose();   // 流对不上（woff2 原始流 / 畸形 sfnt）→ 退回 GetTableData
        }
        try
        {
            var tags = tf.GetTableTags();
            if (tags == null || tags.Length == 0) return null;
            Array.Sort(tags);
            var g = new SfntFont(tf, null, 0);
            foreach (var t in tags)
            {
                int len = tf.GetTableSize(t);
                if (len > 0) { g._dir[t] = (0, len); g.Tags.Add(t); }
            }
            if (g.Tags.Count == 0) { g.Dispose(); return null; }
            return g;
        }
        catch { return null; }
    }

    /// <summary>解析 sfnt / TTC 目录（只读 12B 头 + n×16B 目录项）。任何不自洽 → false。</summary>
    bool TryParseDirectory()
    {
        if (_stream == null) return false;
        long len = _stream.Length;
        if (len < 12 + 16) return false;
        var hdr = new byte[12];
        if (ReadAt(0, hdr, 0, 12) != 12) return false;

        long at = 0;
        uint magic = BE32(hdr, 0);
        if (magic == 0x74746366)   // 'ttcf'：成员面的目录在 TTC 头之后，表偏移是全文件绝对偏移
        {
            if (_ttcIndex < 0) return false;
            var rec = new byte[4];
            if (ReadAt(12 + 4L * _ttcIndex, rec, 0, 4) != 4) return false;
            at = BE32(rec, 0);
            if (at + 12 > len || ReadAt(at, hdr, 0, 12) != 12) return false;
            magic = BE32(hdr, 0);
        }
        // 00010000 = TrueType、'true'/'typ1' = 老式 sfnt、'OTTO' = CFF（下面没有 glyf，调用方会放弃裁剪）
        if (magic is not (0x00010000 or 0x74727565 or 0x74797031 or 0x4F54544F)) return false;

        int n = (hdr[4] << 8) | hdr[5];
        if (n <= 0 || n > 512) return false;
        var dir = new byte[16 * n];
        if (ReadAt(at + 12, dir, 0, dir.Length) != dir.Length) return false;

        for (int i = 0; i < n; i++)
        {
            int p = i * 16;
            uint tag = BE32(dir, p);
            for (int k = 0; k < 4; k++)   // 表标记必须是可见 ASCII，否则这份目录不是我们以为的东西
            {
                byte c = dir[p + k];
                if (c < 0x20 || c > 0x7E) return false;
            }
            uint off = BE32(dir, p + 8), tlen = BE32(dir, p + 12);
            if (off + (long)tlen > len) return false;
            _dir[tag] = (off, (int)tlen);
            Tags.Add(tag);
        }
        Tags.Sort();
        return _dir.Count > 0;
    }

    public bool Has(uint tag) => _dir.ContainsKey(tag);
    public int Size(uint tag) => _dir.TryGetValue(tag, out var e) ? e.Len : 0;
    public int U16(uint tag, int at) { var d = Read(tag); return d == null || at + 2 > d.Length ? 0 : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at)); }
    public short I16(uint tag, int at) { var d = Read(tag); return d == null || at + 2 > d.Length ? (short)0 : BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(at)); }

    /// <summary>整表读取（只该用于要改写的小表：head/maxp/loca/hmtx/vmtx/cmap/name/COLR）。</summary>
    public byte[]? Read(uint tag)
    {
        if (!_dir.TryGetValue(tag, out var e)) return null;
        if (_stream == null)
        {
            _cache ??= new Dictionary<uint, byte[]>();
            if (_cache.TryGetValue(tag, out var hit)) return hit;
            var d = _tf.GetTableData(tag);
            if (d == null) return null;
            _cache[tag] = d;
            return d;
        }
        var buf = new byte[e.Len];
        return ReadAt(e.Off, buf, 0, e.Len) == e.Len ? buf : null;
    }

    /// <summary>把某张表从 <paramref name="relOff"/> 起的 <paramref name="len"/> 字节直接读进目标缓冲的指定位置。</summary>
    public int ReadInto(uint tag, long relOff, byte[] dst, int dstOff, int len)
    {
        if (!_dir.TryGetValue(tag, out var e)) return 0;
        if (relOff < 0 || len < 0 || relOff + len > e.Len) return 0;
        if (_stream == null)
        {
            var d = Read(tag);
            if (d == null) return 0;
            Buffer.BlockCopy(d, (int)relOff, dst, dstOff, len);
            return len;
        }
        return ReadAt(e.Off + relOff, dst, dstOff, len);
    }

    int ReadAt(long off, byte[] dst, int dstOff, int len)
    {
        if (_stream == null || len <= 0) return 0;
        if (!_stream.Seek(checked((int)off))) return 0;
        if (dstOff == 0 && len == dst.Length) return _stream.Read(dst, len);
        // SKStream 只提供 Read(byte[], size) / Read(IntPtr, size)：借一次 pin 读进目标偏移
        var h = System.Runtime.InteropServices.GCHandle.Alloc(dst, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { return _stream.Read(System.IntPtr.Add(h.AddrOfPinnedObject(), dstOff), len); }
        finally { h.Free(); }
    }

    static uint BE32(byte[] b, int i) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i));

    public void Dispose()
    {
        _cache?.Clear();
        _cache = null;
        _dir.Clear();
        Tags.Clear();
        _stream?.Dispose();
    }
}
