using System.IO.Compression;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace PaperSharp.Imaging;

/// <summary>PNG 逐行滤波（RFC 2083 §6）。引擎只用这两种——实测其余三种更慢或不更小。</summary>
public enum PngFilter { None = 0, Up = 2 }

/// <summary>
/// 自研 PNG 编码器（8bit RGBA、非隔行、逐行滤波 + 按行分块并行 deflate）。
///
/// 为什么要自己写：Skia 的 PNG 编码是 Playground 与一切长图导出的头号开销——改之前 pgstage 实测它占
/// 端到端 60–90%（m1 21.2 Mpx 单是 <c>SKImage.Encode(Png,100)</c> 就 732ms 中位 / 628ms 分段最小，
/// 22 样例合计 5.07s；见 PERF_REPORT §6.7），而且它的 quality 参数对 PNG 完全无效
/// （q=100/50/10 三档产物字节一字不差：m1 均 1280KB，全语料均 6643KB），没有可调空间。Skia 慢在两处：
/// 逐行在 5 种滤波里挑最优（PNG_ALL_FILTERS），再跑最高级 zlib，且全程单线程。
///
/// 这里改成「固定滤波 + 按行分块并行 deflate」：各块以 Z_SYNC_FLUSH 收尾，拼成同一条 zlib 流，
/// adler32 用 zlib 的合并公式（adler32_combine）跨块累加。分块损失一点压缩率（块间不能互引），
/// 但省掉了 5 滤波搜索并把 deflate 摊到多核上。逐样例对账见 PngEncoderTests：同尺寸或更小；
/// 提速随像素量增长（小样例数倍，m1 这类 20Mpx 级 20× 上下，22 样例语料合计 19×，见 PERF_REPORT §6.7）。
///
/// 无损性是硬要求：本编码器的输出解码后必须与 Skia 的输出逐字节相同，这条钉在单元测试里。
/// </summary>
public static class PngEncoder
{
    static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>每块最少行数：块太碎既没有并行收益，又把压缩率切没了。</summary>
    const int MinRowsPerChunk = 64;

    /// <summary>
    /// 一条 strip 的字节预算。<c>ArrayPool&lt;byte&gt;.Shared</c> 只缓存 ≤1MB 的数组，
    /// 超过就是「每次 Rent 现 new、每次 Return 即丢」，所以刻意压在 1MB 以下，
    /// 让长图编码的托管堆分配近乎归零（否则 GC 的账会记到下一次渲染的 buildDoc 头上）。
    /// </summary>
    const int StripBytes = 768 * 1024;

    /// <summary>
    /// 分块数上限。**刻意写死而不跟 <c>Environment.ProcessorCount</c> 走**：块数决定 deflate 的
    /// 历史窗口在哪断开，因而决定输出字节；跟着核数走会让同一份文档在不同机器上编出不同的 PNG，
    /// 而 PngGoldenTests 是逐字节比对的。并行本身就是这条杠杆的来源（pgenc 22 样例合计：
    /// 同滤波同档位 单线程 1563ms → 16 块 263ms），8 块已吃到绝大部分收益；调这个数会改动全部
    /// golden 字节，别顺手改。
    /// </summary>
    const int MaxChunks = 8;

    /// <summary>单幅 PNG 的像素上限（int 索引 + 内存）；超出直接拒绝，由调用方退回 Skia。</summary>
    const long MaxFilteredBytes = 1L << 31;

    /// <summary>能否走本编码器：尺寸在可寻址范围内。False 时调用方应退回 <c>SKImage.Encode</c>。</summary>
    public static bool Supports(SKImage image) =>
        (long)image.Height * (image.Width * 4 + 1) < MaxFilteredBytes;

    /// <summary>分块缓冲从这儿租，理由见 <see cref="Encode"/> 里 Work 的注释。</summary>
    static readonly System.Buffers.ArrayPool<byte> Pool = System.Buffers.ArrayPool<byte>.Shared;

    /// <summary>
    /// 把已光栅的位图编成 PNG。<paramref name="maxChunks"/> ≤ 0 时按核数分块。
    /// WASM 单线程下 <c>Parallel.For</c> 退化为顺序执行，结果与多线程逐字节一致。
    /// </summary>
    public static byte[] Encode(SKImage image, PngFilter filter = PngFilter.Up,
        CompressionLevel level = CompressionLevel.Optimal, int maxChunks = 0)
    {
        int w = image.Width, h = image.Height, stride = w * 4, pitch = stride + 1;
        int chunks = PlanChunks(h, maxChunks);

        var deflated = new byte[chunks][];
        var adler = new uint[chunks];
        var start = new int[chunks + 1];
        for (int c = 0; c <= chunks; c++) start[c] = c * h / chunks;          // 均匀切行，余数摊给前面的块

        void Work(int c)
        {
            int y0 = start[c], n = start[c + 1] - start[c];
            // 一条 strip 的行数：让每次租用的两块缓冲都 ≤ StripBytes。ArrayPool.Shared 只缓存
            // ≤1MB 的 byte[]，超过就变成「每次 Rent 都 new、每次 Return 都丢」——一张 2× 长图
            // 按 8 块分，每块就是几 MB，全砸在托管堆上；pgstage 实测这些垃圾会记到**下一次**
            // buildDoc 头上（img 的 buildDoc 从 12ms 涨到 30ms 量级），编码省下的时间被 GC 吃回去。
            int rows = Math.Max(1, StripBytes / pitch);
            var payload = new MemoryStream(Math.Max(1024, n * pitch / 4));
            var dz = new DeflateStream(payload, level, leaveOpen: true);
            uint ad = 0;
            long written = 0;
            try
            {
                for (int r0 = 0; r0 < n; r0 += rows)
                {
                    int k = Math.Min(rows, n - r0);
                    var src = new SKImageInfo(w, k, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                    byte[]? raw = null, buf = null;
                    try
                    {
                        raw = Pool.Rent(k * stride);
                        buf = Pool.Rent(k * pitch);
                        var handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
                        bool read;
                        try
                        {
                            // 一次取本段全部行，目标行距 = stride（Skia 对 rowBytes 有对齐要求，不能省掉
                            // 每行前置的滤波标记位，所以滤波另写一份 buf）。
                            read = image.ReadPixels(src, handle.AddrOfPinnedObject(), stride, 0, y0 + r0);
                        }
                        finally { handle.Free(); }
                        if (!read) throw new InvalidOperationException(
                            $"PNG 编码取像素失败（{w}×{k} @y{y0 + r0}，rowBytes={stride}）——Rgba8888/Unpremul 目标格式不被支持？");

                        if (filter == PngFilter.Up) UpFilter(raw, buf, k, stride, pitch, image, w, y0 + r0);
                        else for (int i = 0; i < k; i++) { buf[i * pitch] = 0; Buffer.BlockCopy(raw, i * stride, buf, i * pitch + 1, stride); }

                        var slice = new ReadOnlySpan<byte>(buf, 0, k * pitch);
                        ad = written == 0 ? Adler(slice) : AdlerCombine(ad, Adler(slice), slice.Length);
                        written += slice.Length;
                        dz.Write(slice);       // 同一块的各段共用一条 deflate 流，历史可以互相引用
                    }
                    finally
                    {
                        if (raw != null) Pool.Return(raw);
                        if (buf != null) Pool.Return(buf);
                    }
                }
                if (c == chunks - 1) dz.Dispose();                    // 末块：补上最终块，整条 zlib 流到此结束
                else { dz.Flush(); GC.SuppressFinalize(dz); }         // 其余块：Z_SYNC_FLUSH 收尾，拼得出连续流
                deflated[c] = payload.ToArray();
                adler[c] = ad;
            }
            finally { payload.Dispose(); }
        }

        if (chunks == 1) Work(0);
        else Parallel.For(0, chunks, Work);

        uint a = adler[0];
        for (int c = 1; c < chunks; c++) a = AdlerCombine(a, adler[c], (long)(start[c + 1] - start[c]) * pitch);

        int payload = 6;
        for (int c = 0; c < chunks; c++) payload += deflated[c].Length;
        using var ms = new MemoryStream(Signature.Length + 41 + payload + 12 + 12);
        ms.Write(Signature);
        Chunk(ms, "IHDR", Header(w, h));
        Chunk(ms, "IDAT", Stream(payload, deflated, level, a));
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>
    /// Up 滤波（本行减上一行）。跨块的那条边界不能省：块首行的「上一行」属于前一块，
    /// 前一块的缓冲区是它私有的，所以一律从只读的源图另取一行原始像素。
    /// y0==0 时上一行按规范视作全 0，Up 退化为恒等，留零数组即可。
    /// </summary>
    static void UpFilter(byte[] raw, byte[] buf, int n, int stride, int pitch, SKImage image, int w, int y0)
    {
        var prev = new byte[stride];
        if (y0 > 0) ReadRawRow(image, w, y0 - 1, prev);
        for (int i = 0; i < n; i++)
        {
            buf[i * pitch] = 2;
            int at = i * stride;
            var outRow = new Span<byte>(buf, i * pitch + 1, stride);
            for (int j = 0; j < stride; j++) outRow[j] = (byte)(raw[at + j] - prev[j]);
            Buffer.BlockCopy(raw, at, prev, 0, stride);
        }
    }

    /// <summary>从只读源图取一行的原始 RGBA 像素（块首行滤波要参考前一块那一行）。</summary>
    static void ReadRawRow(SKImage image, int w, int y, byte[] dst)
    {
        var info = new SKImageInfo(w, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var h = GCHandle.Alloc(dst, GCHandleType.Pinned);
        try
        {
            if (!image.ReadPixels(info, h.AddrOfPinnedObject(), dst.Length, 0, y))
                throw new InvalidOperationException($"PNG 编码取参考行失败（y={y}）");
        }
        finally { h.Free(); }
    }

    /// <summary>并行度：块数只由行数决定（见 <see cref="MaxChunks"/> 的字节确定性理由），与核数无关。</summary>
    static int PlanChunks(int h, int maxChunks) =>
        Math.Clamp(maxChunks > 0 ? maxChunks : MaxChunks, 1, Math.Max(1, h / MinRowsPerChunk));

    static byte[] Header(int w, int h)
    {
        var b = new byte[13];
        Put(b, 0, (uint)w); Put(b, 4, (uint)h);
        b[8] = 8;      // bit depth
        b[9] = 6;      // color type = RGBA
        return b;      // 压缩法 0 / 滤波法 0 / 非隔行，余下三字节留 0
    }

    /// <summary>zlib 头两条字节 + 各块 deflate 结果 + adler 尾，即一整条 IDAT 载荷。</summary>
    static byte[] Stream(int payload, byte[][] deflated, CompressionLevel level, uint adler)
    {
        var body = new byte[payload];
        int at = 0;
        body[at++] = 0x78;                                     // CMF：CM=8 deflate，CINFO=7 → 32K 窗口
        body[at++] = Flg(level);                               // FLG：FLEVEL 提示 + FCHECK 使 16 位整字被 31 整除
        foreach (var d in deflated) { Buffer.BlockCopy(d, 0, body, at, d.Length); at += d.Length; }
        body[at++] = (byte)(adler >> 24); body[at++] = (byte)(adler >> 16);
        body[at++] = (byte)(adler >> 8); body[at++] = (byte)adler;
        return body;
    }

    static byte Flg(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => 0x01,      // zlib 对 1 级的写法
        CompressionLevel.Optimal => 0x9C,      // 6 级
        _ => 0xDA,                             // 9 级
    };

    /// <summary>写一个 PNG 块：长度 + 类型 + 数据 + CRC（CRC 覆盖类型与数据，不含长度）。</summary>
    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4]; Put(len, (uint)data.Length); s.Write(len);
        var head = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(head); s.Write(data);
        Span<byte> crc = stackalloc byte[4]; Put(crc, Crc(head, data)); s.Write(crc);
    }

    static void Put(Span<byte> b, uint v) { b[0] = (byte)(v >> 24); b[1] = (byte)(v >> 16); b[2] = (byte)(v >> 8); b[3] = (byte)v; }
    static void Put(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static uint[]? _crc;
    static uint Crc(byte[] type, byte[] data)
    {
        var t = _crc ??= CrcTable();
        uint c = 0xFFFFFFFF;
        foreach (byte b in type) c = t[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = t[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    static uint[] CrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    const uint AdlerBase = 65521;

    static uint Adler(ReadOnlySpan<byte> data)
    {
        uint s1 = 1, s2 = 0;
        while (data.Length > 0)
        {
            int n = Math.Min(data.Length, 5552);          // 该长度内两个累加器不会溢出 u32
            for (int i = 0; i < n; i++) { s1 += data[i]; s2 += s1; }
            s1 %= AdlerBase; s2 %= AdlerBase;
            data = data[n..];
        }
        return (s2 << 16) | s1;
    }

    /// <summary>zlib 的 adler32_combine：由两段各自的 adler 与后段长度合成整段 adler。</summary>
    static uint AdlerCombine(uint a1, uint a2, long len2)
    {
        uint s1a = a1 & 0xFFFF, s2a = a1 >> 16, s1b = a2 & 0xFFFF, s2b = a2 >> 16;
        ulong rem = (ulong)(len2 % AdlerBase);
        ulong s1 = (ulong)s1a + s1b + AdlerBase - 1;
        ulong s2 = (rem * s1a) % AdlerBase + s2a + s2b + AdlerBase - rem;
        s1 %= AdlerBase; s2 %= AdlerBase;
        return (uint)(s1 | (s2 << 16));
    }
}
