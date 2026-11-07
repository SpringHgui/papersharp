using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using PaperSharp.Layout;

namespace PaperSharp.Pdf;

/// <summary>
/// 一次 PDF 渲染的**字形记账本**：录制每个 typeface 实际画过哪些字形，
/// 真绘前把字体换成 <see cref="TtfSubsetter"/> 裁出的「只用字」子集，Skia 便只嵌这点字节
/// （Skia 的 PDF 后端自己不做任何子集化，见 PERF_REPORT §11）。
///
/// 双趟协议：<see cref="Recording"/> 先对一张 scratch 画布空跑一遍——与真绘完全同一套代码路径，
/// 杜绝「收集逻辑漏枚举某处文字」造成的静默空白；<see cref="BuildSubsets"/> 后第二趟真绘，
/// 绘制点仍逐字录制，遇到第一趟没记到的字形即置 <see cref="Violated"/>，
/// 调用方据此扔掉子集原样重绘（正确性兜底，正常永不变红）。
///
/// **空壳 + 换流**（<see cref="UseHollowFaces"/>，默认开）：真绘趟画字的子集 face 不带轮廓，
/// Skia 写完 PDF 后由 <see cref="PdfFontSwap"/> 把空壳字体流换成带轮廓的子集字体。
/// 空壳本身是**进程级共享**的（见 <see cref="TtfSubsetter.BuildHollowShell"/>）：同一张物理字面
/// 按用字并集建一张壳，之后每份文档复用——「连续出 PDF」不再有每份文档的 typeface 创建/销毁，
/// 每份只产出自己那份带轮廓的嵌入子集（同模板反复导出时连它都走缓存）。
/// 只对「朴素文字」生效——凡是要拿轮廓去画的（假粗体、描边、模糊阴影、渐变填充……见 <see cref="NotePaint"/>）、
/// 走 HarfBuzz 整形的、彩色/可变/CFF/受限的字面，一律保持带轮廓的子集 face；
/// 换流验证失败则整份文档按带轮廓子集重画（<see cref="RetryWithFullSubsets"/>）。
///
/// 每线程至多一个 <see cref="Current"/>；PNG/SVG 路径不设 Current，所有钩子直接透传。
/// </summary>
public sealed class PdfGlyphUsage : IDisposable
{
    /// <summary>字形集合完全一致的同一张 face 的子集跨渲染复用（同一文档反复导出 / 常驻服务热路径）。
    /// 计价口径（tools/MemStress 实测）：**一张活字面在真渲染里 ≈3.5 MB**，是它子集流本身（0.2–0.6 MB）
    /// 的近十倍——多出来的是 Skia 挂在这张 typeface 上的字形位图/度量缓存，跟着字面一起常驻。
    /// 批量导出内容各异的 2000 份里，每张独有子集只被用一次、永远不会命中，48 张就这么白占 ~172 MB。
    /// 所以：① **第二次命中才准入**（只值一次的子集留在本次渲染的 <see cref="ownedFaces"/> 里当场释放）；
    /// ② LRU 上限 16 张（≈56 MB 封顶），逐出后等最后一个租约归还才 Dispose——并发渲染中直接放掉别人
    /// 正在用的字面会踩空句柄。同模板反复导出的热子集第二次起常驻，速度收益一点不丢。
    /// 空壳路径不再用这张表：它常驻的是共享壳，嵌入流另有 <see cref="EmbedCache"/>。</summary>
    static readonly List<CachedSubset> Cache = new();

    /// <summary>第一趟见过的子集指纹；第二次出现才准进 <see cref="Cache"/>。只存 4 字节，量级 = 批内不同子集数。</summary>
    static readonly HashSet<uint> SeenOnce = new();
    static readonly object CacheLock = new();
    const int CacheCap = 16;

    sealed record CachedSubset(FaceKey Face, int[] Kept, SKTypeface Subset)
    {
        public int Users;      // 正在用这张常驻字面的渲染数（租约）
        public bool Dying;     // 已逐出，等最后一个租约归还再 Dispose
    }

    /// <summary>
    /// 进程级共享的**空壳**：一张物理字面一个，覆盖「到目前为止见过的用字并集」。
    /// 模板反复导出时并集收敛、壳不再变；内容确实各异的批量里壳会长大，
    /// 超过 <see cref="ShellGlyphCap"/> 就退回逐份自裁（见 <see cref="BuildSubsets"/>）。
    /// </summary>
    sealed class Shell(SKTypeface face, string marker, int length, string? psName, HashSet<int> covered)
    {
        public readonly SKTypeface Face = face;
        public readonly string Marker = marker;
        public readonly int Length = length;      // Skia 写进 PDF 的 /Length1，换流时用它核对流
        public readonly string? PsName = psName;
        public readonly HashSet<int> Covered = covered;
        public int Users;                          // 租约：正在用它的渲染数
        public bool Dying;                         // 已被更大的壳替换，等租约归零再 Dispose
    }

    /// <summary>字面身份 → 常驻空壳。键是物理字面，条目数与**文档数无关**（一张字体一条）。</summary>
    static readonly Dictionary<FaceKey, Shell> Shells = new();
    static readonly object ShellLock = new();
    /// <summary>壳表上限：宿主若动态注册成百上千张字体，别让空壳把内存占满——超过即退回每份文档自裁。</summary>
    const int ShellCap = 64;
    /// <summary>单张壳最多覆盖多少字形：内容完全各异的批量里壳会一路长大，超过它就退回逐份自裁
    /// （逐份自裁＝旧行为：正确但每份一个原生字面）。2048 ≈ 常见报表/发票用字的上限。</summary>
    const int ShellGlyphCap = 2048;

    /// <summary>「同模板反复导出」的嵌入子集缓存：键 = 字面身份 + 用字集合，值 = 那份带轮廓的字体程序（几十 KB）。
    /// 常驻的是**字节**不是 typeface——旧缓存在这里存整张子集 face（≈0.6MB 原生 + 加载开销），
    /// 现在画字一律用共享壳，缓存只需留下嵌入流，同样省掉重复导出的裁字开销，代价小一个量级。
    /// 与旧子集缓存同一条准入规则：**第二次命中才准入**。内容各异的批量里每份的用字集合只出现一次，
    /// 无条件缓存等于把 4MB 预算全换成死条目；同模板反复导出第二次起仍稳定命中，收益不变。</summary>
    static readonly List<EmbedEntry> EmbedCache = new();
    static readonly HashSet<uint> EmbedSeen = new();
    const long EmbedCacheBytes = 4L << 20;

    sealed class EmbedEntry(FaceKey face, int[] kept, byte[] embed)
    {
        public readonly FaceKey Face = face;
        public readonly int[] Kept = kept;
        public readonly byte[] Embed = embed;
    }

    /// <summary>字面身份的算法与判定见 <see cref="Layout.FaceIdentity"/>：族名+字重+字宽+倾斜档+字形数+head 校验和。</summary>
    static FaceKey KeyOf(SKTypeface tf) => Layout.FaceIdentity.Of(tf);

    readonly Dictionary<FaceKey, HashSet<int>> Used = new();
    readonly Dictionary<FaceKey, SKTypeface> Source = new();   // 每个身份留一个具体实例当裁剪源
    readonly Dictionary<FaceKey, SubsetEntry> Subsets = new();
    readonly HashSet<FaceKey> shapedKeys = new();   // 走过 HarfBuzz 整形的字面：空壳要留 GSUB/GPOS
    readonly HashSet<FaceKey> outlineKeys = new();  // 被画成非朴素文字的字面：必须带轮廓，不走空壳
    readonly List<SKTypeface> ownedFaces = new();   // 未进全局缓存的子集 face：本次渲染独占，Dispose 释放
    readonly List<CachedSubset> borrowed = new();   // 从全局缓存取用的字面：Dispose 时归还租约
    readonly List<Shell> borrowedShells = new();    // 从共享壳表取用的壳：Dispose 时归还租约
    readonly PdfGlyphUsage? outer;
    bool hollowOff;   // 本次渲染放弃空壳方案（换流失败后重画时置位）

    /// <summary>空壳的占位名序号：进程级递增，常驻缓存里的空壳跨文档复用也不会撞名。</summary>
    static int hollowSeq;
    static string NextMarker() => "PSK" + Interlocked.Increment(ref hollowSeq);

    /// <summary>一个字面身份对应的绘制 face。走空壳时 <see cref="Face"/> 是**共享壳或逐份空壳**
    /// （都没有轮廓），<see cref="Target"/> 记着本份文档该换进去的带轮廓嵌入子集。</summary>
    sealed class SubsetEntry(SKTypeface face, HashSet<int> kept)
    {
        public readonly SKTypeface Face = face;
        public readonly HashSet<int> Kept = kept;
        public Shell? Shell;              // 非空 = 画的是进程共享壳（Dispose 时归还租约）
        public EmbedTarget? Target;       // 非空 = 画的是空壳，Skia 写完后要换流
        public bool Drawn;                // 真绘趟里确实用这张 face 落过笔
        public SKTypeface? OutlineFace;   // 贴着页/裁剪边的文字改用它（带轮廓，见 NearClipEdge）
        public bool Hollowed => Target != null;
        public bool NeedsSwap => Target != null && Drawn;
    }

    /// <summary>一次换流的全部参数：占位名、Skia 会写下的空壳流长度、真名、以及本份文档的嵌入字节。</summary>
    sealed class EmbedTarget(string marker, int hollowLength, string? psName, byte[] embed)
    {
        public readonly string Marker = marker;
        public readonly int HollowLength = hollowLength;
        public readonly string? PsName = psName;
        public readonly byte[] Embed = embed;
    }

    /// <summary>
    /// 空壳 + 换流方案的总开关。默认开：Windows/DirectWrite 上
    /// Skia 对每个新 typeface 的每个新 CJK 字形要花 ~250µs，批量导出时这一项占单份耗时的三成以上。
    /// 关掉即退回「带轮廓子集 face 直接画」——两条路的页面内容流逐字节相同，只有字体流不同。
    /// </summary>
    public static bool UseHollowFaces { get; set; } = true;

    /// <summary>
    /// 空壳是否跨文档共享（默认开）。关掉即退回「每份文档按自己的用字集裁一张空壳」的旧行为：
    /// 产物逐字节相同，但每份文档都要建一张原生 typeface——内容各异的批量里这是内存大头。
    /// 保留开关是为了 A/B 对照与极端宿主的兜底（`PS_SHELLS=0`）。
    /// </summary>
    public static bool UseSharedShells { get; set; } = Environment.GetEnvironmentVariable("PS_SHELLS") != "0";

    [ThreadStatic] static bool? hollowOverride;

    /// <summary>只作用于**当前线程**的空壳开关（对照测试/A-B 用：不必翻进程级开关去影响并发中的其它渲染）。</summary>
    internal static IDisposable HollowScope(bool enabled)
    {
        var prev = hollowOverride;
        hollowOverride = enabled;
        return new ScopeRestore(() => hollowOverride = prev);
    }

    static bool HollowEnabled => (hollowOverride ?? UseHollowFaces) && !SwapUnsupported;

    sealed class ScopeRestore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    [ThreadStatic] static PdfGlyphUsage? cur;
    public static PdfGlyphUsage? Current => cur;

    [ThreadStatic] internal static long cDrawText, cNearClip, cRecordString;   // 调试计数（PS_COUNTS）
    // NearClipEdge 是热路径：这里刻意不留任何计时/日志（旧代码每次落笔都 new 一个 Stopwatch）。

    /// <summary>本线程正在录制字形（录制趟与带子集的真绘趟都为 true）。</summary>
    public bool Recording { get; private set; } = true;
    /// <summary>子集已建好：真绘点应把 face 换成 <see cref="Map"/> 的结果。</summary>
    public bool SubsetsBuilt { get; private set; }
    /// <summary>真绘趟出现录制趟没见过的字形：调用方须扔掉子集重绘。</summary>
    public bool Violated { get; private set; }

    public int SubsetbedFaces => Subsets.Count;

    PdfGlyphUsage(PdfGlyphUsage? outerScope) => outer = outerScope;

    public static PdfGlyphUsage Begin()
    {
        var u = new PdfGlyphUsage(cur);
        cur = u;
        return u;
    }

    public void Dispose()
    {
        if (cur == this) cur = outer;
        foreach (var f in ownedFaces) f.Dispose();
        ownedFaces.Clear();
        // 归还常驻字面的租约：引用计数归零且已被逐出的那张，此刻才真还原生内存。
        // 并发渲染里随手 Dispose 一张别人正在用的字面＝踩空句柄，所以逐出与释放必须分两步。
        lock (CacheLock)
        {
            foreach (var c in borrowed)
                if (--c.Users == 0 && c.Dying) c.Subset.Dispose();
        }
        borrowed.Clear();
        // 共享壳同理：被更大的壳替换掉的那张，等最后一个用户归还才 Dispose。
        lock (ShellLock)
        {
            foreach (var s in borrowedShells)
                if (--s.Users == 0 && s.Dying) s.Face.Dispose();
        }
        borrowedShells.Clear();
    }

    /// <summary>录制趟结束 → 按用字集合建子集 face（进程级缓存命中则零成本）。
    /// 一个**字面身份**一份子集：同一张字面被多少条路取出多少实例都只裁一次，用字并成一集。</summary>
    public void BuildSubsets()
    {
        foreach (var (key, set) in Used)
        {
            if (set.Count == 0) continue;
            var tf = Source[key];
            var sorted = set.ToArray();
            Array.Sort(sorted);
            // 贴边文字与非朴素笔触（录制趟已定论）必须带轮廓，所以只有它们「不想要空壳」。
            bool wantHollow = HollowEnabled && !hollowOff && !outlineKeys.Contains(key) && !shapedKeys.Contains(key);

            // ① 空壳路：画字用（进程级共享的）空壳，本份只产出带轮廓的嵌入子集。
            if (wantHollow)
            {
                var shell = UseSharedShells ? ShellFor(key, tf, set) : null;
                if (shell != null)
                {
                    var embed = EmbedFor(key, tf, set, sorted);
                    if (embed != null)
                    {
                        Subsets[key] = new SubsetEntry(shell.Face, new HashSet<int>(sorted))
                        {
                            Shell = shell,
                            Target = new EmbedTarget(shell.Marker, shell.Length, shell.PsName, embed),
                        };
                        continue;
                    }
                }
                // 共享壳不适用/超限，或嵌入子集裁不出来：逐份自裁一张空壳（旧行为，正确但每份一个原生字面）。
                if (PerDocHollow(tf, set, sorted) is { } perDoc)
                {
                    Subsets[key] = perDoc;
                    continue;
                }
                // 连逐份空壳都建不出来（CFF/彩色/受限）：落到下面的带轮廓子集路径。
            }

            // ② 带轮廓子集路：每份文档一张 face。只值一次的子集不常驻（见 Cache 注释）。
            CachedSubset? hit = null;
            lock (CacheLock)
            {
                foreach (var c in Cache)
                    if (c.Face == key && c.Kept.SequenceEqual(sorted)) { hit = c; break; }
                if (hit != null)
                {
                    hit.Users++;
                    borrowed.Add(hit);
                    Cache.Remove(hit);
                    Cache.Add(hit);          // LRU：命中的挪到队尾，别被新来的挤掉
                }
            }

            SubsetEntry entry;
            if (hit != null)
                entry = new SubsetEntry(hit.Subset, new HashSet<int>(sorted));
            else
            {
                if (HasBitmapColorTables(tf)) continue;   // CBDT/sbix/SVG：裁出的 face 经 FromData 会丢彩色层（emoji 变黑白线稿），整嵌不动。
                // 纯 COLR v1 彩色字体不再一刀切跳过：TtfSubsetter 沿 Paint 图把图层轮廓字形并进闭包
                // 并重写可达子集（§13.6 刀②，m7 那 12.9MB 的 seguiemj 整嵌就是它）。解析失败仍返回
                // null → 整嵌，行为与旧闸口一致。
                var bytes = TtfSubsetter.Subset(tf, set);
                if (bytes == null) continue;   // CFF/异常数据：保持整嵌
                var face = SKTypeface.FromData(SKData.CreateCopy(bytes));
                if (face == null) continue;
                entry = new SubsetEntry(face, new HashSet<int>(sorted));

                uint sig = Stamp(key, sorted);
                lock (CacheLock)
                {
                    if (SeenOnce.Count > 65_536) SeenOnce.Clear();   // 常驻服务跑百万份：指纹表自己也要封顶
                    if (!SeenOnce.Add(sig))   // Add 返回 false ＝ 上一趟就见过 → 这次是第二次命中
                    {
                        while (Cache.Count >= CacheCap)
                        {
                            var old = Cache[0];
                            Cache.RemoveAt(0);
                            old.Dying = true;
                            if (old.Users == 0) old.Subset.Dispose();
                        }
                        var cached = new CachedSubset(key, sorted, entry.Face) { Users = 1 };
                        Cache.Add(cached);
                        borrowed.Add(cached);
                    }
                    else ownedFaces.Add(entry.Face);   // 只值一次的子集：本次渲染独占，Dispose 当场释放
                }
            }
            Subsets[key] = entry;
        }
        SubsetsBuilt = true;   // Recording 保持 true：真绘趟继续录制并逐字校验
    }

    /// <summary>
    /// 取（或建）这张物理字面的共享空壳：覆盖集合必须包含本份的全部用字，否则按**并集**重建一张更大的。
    /// 模板反复导出时第二次起全命中（零 typeface 创建）；内容各异的批量里并集缓慢长大，
    /// 到 <see cref="ShellGlyphCap"/> 就放弃共享、退回逐份自裁。
    /// 旧壳不立刻 Dispose——并发渲染可能还在拿它画字，等租约归零（见 <see cref="Dispose"/>）。
    /// </summary>
    Shell? ShellFor(FaceKey key, SKTypeface tf, HashSet<int> set)
    {
        lock (ShellLock)
        {
            if (Shells.TryGetValue(key, out var cur) && cur.Covered.IsSupersetOf(set))
            {
                cur.Users++;
                borrowedShells.Add(cur);
                return cur;
            }
            if (cur == null && Shells.Count >= ShellCap) return null;

            var union = cur == null ? new HashSet<int>(set) : new HashSet<int>(cur.Covered);
            union.UnionWith(set);
            if (union.Count > ShellGlyphCap) return null;

            string marker = NextMarker();
            if (TtfSubsetter.BuildHollowShell(tf, marker, union) is not { } built) return null;
            var face = SKTypeface.FromData(SKData.CreateCopy(built.Hollow));
            if (face == null) return null;
            var shell = new Shell(face, marker, built.Hollow.Length, built.PsName, union) { Users = 1 };
            Shells[key] = shell;
            borrowedShells.Add(shell);
            if (cur != null)
            {
                cur.Dying = true;
                if (cur.Users == 0) cur.Face.Dispose();
            }
            return shell;
        }
    }

    /// <summary>逐份自裁的空壳（共享壳不可用时）：一张 face ＋ 一对 Hollow/Embed 字节，归本次渲染独占。</summary>
    SubsetEntry? PerDocHollow(SKTypeface tf, HashSet<int> set, int[] sorted)
    {
        string marker = NextMarker();
        var pair = TtfSubsetter.SubsetForPdf(tf, set, marker);
        if (pair == null) return null;
        var face = SKTypeface.FromData(SKData.CreateCopy(pair.Hollow));
        if (face == null) return null;
        ownedFaces.Add(face);
        return new SubsetEntry(face, new HashSet<int>(sorted))
        {
            Target = new EmbedTarget(marker, pair.Hollow.Length, pair.PsName, pair.Embed),
        };
    }

    /// <summary>
    /// 本份文档的**嵌入字体程序**（带轮廓、去排版表）。同一模板反复导出时用字集合恒定，
    /// 直接吃进程级缓存（命中率 = 重复率），一次几十 KB 的裁剪、cmap 收口、校验和全部省掉。
    /// 未命中就现裁并放进缓存；缓存按字节封顶，内容各异的批量里它只是个小 LRU，不会变成常驻负担。
    /// </summary>
    static byte[]? EmbedFor(FaceKey key, SKTypeface tf, HashSet<int> set, int[] sorted)
    {
        lock (EmbedCache)
        {
            for (int i = 0; i < EmbedCache.Count; i++)
            {
                var e = EmbedCache[i];
                if (e.Face != key || e.Kept.Length != sorted.Length) continue;
                if (!e.Kept.AsSpan().SequenceEqual(sorted)) continue;
                if (i != EmbedCache.Count - 1)   // LRU：命中的挪到队尾
                {
                    EmbedCache.RemoveAt(i);
                    EmbedCache.Add(e);
                }
                return e.Embed;
            }
        }
        var bytes = TtfSubsetter.SubsetForPdfEmbed(tf, set);
        if (bytes == null) return null;
        uint sig = Stamp(key, sorted);
        lock (EmbedCache)
        {
            if (EmbedSeen.Count > 65_536) EmbedSeen.Clear();
            if (EmbedSeen.Add(sig)) return bytes;   // 首次出现：不常驻（内容各异的批量里这就是全部）
            long total = 0;
            foreach (var e in EmbedCache) total += e.Embed.Length;
            while (EmbedCache.Count > 0 && total + bytes.Length > EmbedCacheBytes)
            {
                total -= EmbedCache[0].Embed.Length;
                EmbedCache.RemoveAt(0);
            }
            EmbedCache.Add(new EmbedEntry(key, sorted, bytes));
        }
        return bytes;
    }

    /// <summary>真绘用到的空壳是否存在（存在才需要换流）。</summary>
    public bool HasHollow => Subsets.Values.Any(e => e.Hollowed);

    /// <summary>
    /// Skia 写完 PDF 后，把空壳字体流换成带轮廓的子集字体。返回 null = 换不成（任何一张用到的空壳没被找到或对不上），
    /// 调用方须 <see cref="RetryWithFullSubsets"/> 重画——所以这里绝不返回半成品。
    /// </summary>
    public (byte[] Buf, int Len)? SwapFonts(byte[] pdf, int length)
    {
        var targets = new List<PdfFontSwap.Target>();
        foreach (var e in Subsets.Values)
            if (e.NeedsSwap)
                targets.Add(new PdfFontSwap.Target(e.Target!.Marker, e.Target.PsName ?? "Font" + e.Target.Marker,
                    e.Target.HollowLength, e.Target.Embed));
        if (targets.Count == 0) return (pdf, length);
        var swapped = PdfFontSwap.Apply(pdf, length, targets);
        if (swapped != null) { Interlocked.Increment(ref swapOk); threadSwaps++; }
        else Interlocked.Increment(ref swapFail);
        return swapped;
    }

    static int swapOk, swapFail;
    [ThreadStatic] static int threadSwaps;

    /// <summary>本线程至今换流成功的文档数（测试用：确认空壳路径真的跑了，而不是每份都退回重画）。</summary>
    internal static int SwapsOnThisThread => threadSwaps;

    /// <summary>
    /// 自校准：换流一次都没成功过、却已经连续失败多次，说明当前 Skia 版本写出的 PDF 版式不是 <see cref="PdfFontSwap"/> 认得的，
    /// 此后不再尝试空壳方案（否则每份文档都要「画一遍、换流失败、再画一遍」，比没有这个方案还慢一倍）。
    /// </summary>
    static bool SwapUnsupported => Volatile.Read(ref swapOk) == 0 && Volatile.Read(ref swapFail) >= 3;

    /// <summary>换流失败后用：丢掉全部空壳，按「带轮廓子集 face 直接画」重建子集（本次渲染不再尝试空壳）。</summary>
    public void RetryWithFullSubsets()
    {
        foreach (var f in ownedFaces) f.Dispose();
        ownedFaces.Clear();
        Subsets.Clear();
        SubsetsBuilt = false;
        Violated = false;
        hollowOff = true;
        BuildSubsets();
    }

    /// <summary>子集**准入**用的指纹：字面身份 + 用字集合。允许哈希撞车——撞车只会让一张子集提前常驻
    /// （多花 3.5 MB），查表命中仍然逐字比 <c>Kept</c>，绝不会拿错 face 去画。</summary>
    static uint Stamp(FaceKey key, int[] kept)
    {
        uint h = ((uint)key.Family.GetHashCode() << 3) ^ (uint)key.GlyphCount ^ key.HeadSum;
        foreach (int g in kept) h = (h * 31) ^ (uint)g;
        return h ^ (uint)kept.Length;
    }

    /// <summary>校验失败后用：丢弃全部子集，真绘退回原始 face。</summary>
    public void RetryWithoutSubsets()
    {
        foreach (var f in ownedFaces) f.Dispose();
        ownedFaces.Clear();
        Subsets.Clear();
        SubsetsBuilt = false;
        Recording = false;
        Violated = false;
    }

    /// <summary>原 face → 子集 face；无子集（CFF/没用到）时原样返回。</summary>
    public SKTypeface Map(SKTypeface tf) => Subsets.TryGetValue(KeyOf(tf), out var e) ? e.Face : tf;

    // 字面 → 是否带位图/外矢量彩表层。用 ConditionalWeakTable 而不是字典：字面被释放/回收时条目
    // 跟着走，不会把这个进程级表变成钉住原生字体流的根（批量导出里每份文档的字面都不同）。
    sealed class BoolBox(bool value) { public readonly bool Value = value; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKTypeface, BoolBox> ColorFont = new();

    /// <summary>
    /// 带**位图/外矢量**彩色表层的字体（CBDT/sbix/SVG）：裁剪后重建的 face 不再被识别为彩色字体，
    /// 必须整嵌。COLR 不在此列——它的图层字形闭包由 <see cref="TtfSubsetter"/> 的 COLR v1 路处理。
    /// </summary>
    static bool HasBitmapColorTables(SKTypeface tf) => ColorFont.GetValue(tf, static t => new BoolBox(ScanColorTables(t))).Value;

    static bool ScanColorTables(SKTypeface tf)
    {
        try
        {
            foreach (var tag in tf.GetTableTags())
            {
                string n = new string(new[]
                {
                    (char)((tag >> 24) & 0xFF), (char)((tag >> 16) & 0xFF), (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF),
                });
                if (n is "CBDT" or "sbix" or "SVG ") return true;
            }
        }
        catch { return true; }   // 读表失败 → 保守不裁
        return false;
    }

    // ── 绘制点统一走这两个静态入口（PNG 路径 cur==null，全部直过）──────

    /// <summary>普通字符串绘制：录制（原 face 的码位→字形）+ 真绘趟换子集 face 落笔。
    /// font 携带字面/字号（换子集 face 时临时改它的 Typeface），paint 只管颜色等绘制属性。</summary>
    public static void DrawText(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint paint)
    {
        cDrawText++;
        var u = cur;
        if (u == null) { canvas.DrawText(text, x, y, font, paint); return; }
        var tf = font.Typeface ?? SKTypeface.Default;
        if (u.Recording)
        {
            u.RecordString(tf, text);
            if (!u.SubsetsBuilt)
            {
                u.NotePaint(tf, font, paint);
                // 贴边文字在录制趟就排除空壳：真绘趟为它另建带轮廓 face 的话，那份字体程序与换流写进去的
                // 是同一份字节，PDF 里会白多一条重复的字体流。录制画布与真绘画布同参，裁剪包围盒一致。
                if (NearClipEdge(canvas, font, paint, text, x, y)) u.outlineKeys.Add(KeyOf(tf));
            }
        }
        if (u.SubsetsBuilt && u.Subsets.TryGetValue(KeyOf(tf), out var e))
        {
            var face = e.Face;
            if (e.Hollowed)
            {
                // 录制趟已定论：这张字面**从没有**贴边文字，真绘趟就不必再逐串判一遍
                // （绘制调用与录制趟逐字同参，判据必然同结论）。贴边判定含原生裁剪/矩阵读取与
                // 整串推进量算，是 PDF 绘制段里最贵的一处，正常情况下这一步把它整段省掉。
                if (u.outlineKeys.Contains(KeyOf(tf)) && NearClipEdge(canvas, font, paint, text, x, y))
                    face = u.OutlineFaceOf(e);
                else e.Drawn = true;
            }
            var old = font.Typeface;
            font.Typeface = face;
            try { canvas.DrawText(text, x, y, font, paint); }
            finally { font.Typeface = old; }
            return;
        }
        // 录制趟不落笔：NoDraw 画布上 DrawText 只白做字形解析（4.x 还要每次建 SKTextBlob），
        // 记账/贴边判定都在上方完成，画的这一下没有任何消费者。
        if (!u.SubsetsBuilt) return;
        canvas.DrawText(text, x, y, font, paint);
    }

    /// <summary>
    /// Skia 的 PDF 设备按**字形自己的轮廓包围盒**与裁剪区求交，整个落在区外的字形就不写进内容流。
    /// 空壳的字形包围盒是空的（退化成原点一个点），于是「原点在裁剪外、字形主体在裁剪内」的字会被它剔掉——
    /// 实测：基线在页底下方 3pt、上半身还在页内的字，带轮廓 face 保留、空壳剔除，页边会少一道字。
    /// 所以只有「原点在裁剪外、但墨迹伸回裁剪内」这种字串必须换带轮廓的同一子集：
    /// ① 原点全在区内 → 空壳与带轮廓画出来一样；② 整串墨迹都在区外 → 两条路都剔掉，多剔几个不可见。
    /// 带旋转/错切的矩阵（局部裁剪包围盒不再等价于真实裁剪）一律按贴边处理。
    /// </summary>
    static bool NearClipEdge(SKCanvas canvas, SKFont font, SKPaint paint, string text, float x, float y)
    {
        cNearClip++;
        if (text.AsSpan().Trim().IsEmpty) return false;   // 纯空白不落墨，换不换 face 都一样
        var m = canvas.TotalMatrix;
        if (m.SkewX != 0 || m.SkewY != 0 || m.Persp0 != 0 || m.Persp1 != 0 || m.Persp2 != 1) return true;
        var clip = canvas.LocalClipBounds;
        var tf = font.Typeface ?? SKTypeface.Default;
        float lastOrigin = x + Layout.AdvanceTable.Sum(tf, font.Size, text);   // 末字形原点不会超过整串的推进终点
        bool insideY = y >= clip.Top + 1 && y <= clip.Bottom - 1;
        if (x >= clip.Left + 1 && lastOrigin <= clip.Right - 1 && insideY) return false;
        // 只从右端溢出：左起文字（DrawText 不走整形，全是 LTR）的原点在外时墨迹继续往右，两条路都会剔。
        // 只看末字形自己的原点：推进终点压线但原点还在区内不用换；原点刚过线 1em 以内留余量兜负左边距。
        if (insideY && x >= clip.Left + 1)
        {
            float lastGlyphOrigin = text.Length > 1 ? x + Layout.AdvanceTable.Sum(tf, font.Size, text[..^1]) : x;
            return lastGlyphOrigin > clip.Right - 1 && lastGlyphOrigin < clip.Right + Math.Max(font.Size, 4);
        }
        // 左/上/下溢或横跨整块：墨迹还可能伸回区内就要带轮廓；整串墨迹都在区外则两条路都剔，继续用空壳。
        // 上下界必须用 font.Metrics（与 A0 同口径）：ExtentsAt 的 hhea 口径对 shell/子集面取整后
        // 差出 0.45px 级，贴边判定会翻面（challenge 一处 marker 3px 光栅差）。该分支只在贴边文字
        // 才走到，原生取度量的次数远低于逐串量宽。
        var fm = font.Metrics;
        float top = y + fm.Ascent - 2, bottom = y + fm.Descent + 2, left = x - 2, right = lastOrigin + 2;
        return !(right < clip.Left || left > clip.Right || bottom < clip.Top || top > clip.Bottom);
    }

    /// <summary>贴边文字用的带轮廓字面：由本份文档的嵌入字体程序直接建（每次渲染至多一张，Dispose 释放）。</summary>
    SKTypeface OutlineFaceOf(SubsetEntry e)
    {
        if (e.OutlineFace != null) return e.OutlineFace;
        var f = SKTypeface.FromData(SKData.CreateCopy(e.Target?.Embed ?? Array.Empty<byte>()));
        if (f == null) { Violated = true; return e.Face; }   // 建不出来：整趟作废，调用方退回整面字体重绘
        ownedFaces.Add(f);
        e.OutlineFace = f;
        return f;
    }

    /// <summary>
    /// 记下「这张字面有没有被画成非朴素文字」。空壳 face 没有轮廓，凡是 Skia 要**拿轮廓去画**的文字都会变成空白：
    /// 假粗体（embolden 走描边路径）、描边/路径效果/遮罩滤镜、模糊类图像滤镜（先光栅化到位图）、渐变等 shader 填充、
    /// 混合模式与颜色滤镜。这些字面一律不走空壳，保持带轮廓的子集 face。
    /// 判据只看 font+paint——录制趟与真绘趟传入的逐字相同，所以在录制趟就能定论。</summary>
    void NotePaint(SKTypeface tf, SKFont f, SKPaint p)
    {
        if (p.Style == SKPaintStyle.Fill && !f.Embolden && p.Shader == null && p.ImageFilter == null
            && p.MaskFilter == null && p.PathEffect == null && p.ColorFilter == null && p.BlendMode == SKBlendMode.SrcOver) return;
        outlineKeys.Add(KeyOf(tf));
    }

    /// <summary>
    /// 带**字距对**落笔：Skia 一次 <c>DrawText</c> 只按 hmtx 走位，字距是度量端
    /// （<see cref="Layout.TextMeasurer"/> / <see cref="Layout.TextShaper.WidthOf"/>）加进行宽的，
    /// 所以串里只要有非零配对，就必须按配对切开逐段落笔——否则配对之后的每个字形都比布局
    /// 以为的位置往右偏，叠加视图里是一段重影。返回该串占用的推进宽度
    /// （= <c>MeasureText(整串) + KernTable.Of(整串)</c>，与度量端逐项相等）。
    /// 录制趟与真绘趟都走 <see cref="DrawText"/>，字形记账与子集换 face 一并继承。
    /// </summary>
    public static float DrawKerned(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint paint)
    {
        var kt = Layout.KernTable.For(font.Typeface);
        if (kt == null) { DrawText(canvas, text, x, y, font, paint); return Layout.AdvanceTable.Sum(font.Typeface, font.Size, text); }
        float cx = x;
        foreach (var (seg, tail) in kt.KernChunks(text, font.Size))
        {
            DrawText(canvas, seg, cx, y, font, paint);
            cx += Layout.AdvanceTable.Sum(font.Typeface, font.Size, seg) + tail;
        }
        return cx - x;
    }

    /// <summary>HarfBuzz 整段绘制：录制取真实 shaped 字形；真绘趟连 shaper 带 font 换同一张子集 face。</summary>
    public static void DrawShaped(SKCanvas canvas, SKShaper shaper, string text, float x, float y, SKFont font, SKPaint paint)
    {
        var u = cur;
        if (u == null) { canvas.DrawShapedText(shaper, text, x, y, SKTextAlign.Left, font, paint); return; }
        var tf = font.Typeface ?? SKTypeface.Default;
        if (u.Recording)
        {
            if (!u.SubsetsBuilt) u.NotePaint(tf, font, paint);
            try
            {
                var res = shaper.Shape(text, font);
                u.shapedKeys.Add(KeyOf(tf));
                u.RecordGlyphs(tf, res.Codepoints);   // HB 整形后 Codepoints 槽位里就是字形 ID（SkiaSharp 的历史命名）
                // 输入码位经 cmap 命中的**整形前**目标字形也要进闭包：真绘趟是拿子集 face 重新
                // shape 的，HarfBuzz 第一步仍是 cmap 查询。阿拉伯文的 cmap 指向独立形、
                // 画出来的却是上下文变体形——只记 hb 输出会把 cmap 条目剪掉，真绘趟直接查不到字。
                u.RecordString(tf, text);
            }
            catch { /* 录制失败 → 该 run 缺席 → 真绘趟校验必然变红 → 回退整嵌，不静默出错 */ }
        }
        if (u.SubsetsBuilt && u.Subsets.TryGetValue(KeyOf(tf), out var e))
        {
            e.Drawn = true;
            var oldTf = font.Typeface;
            font.Typeface = e.Face;
            try
            {
                using var ms = new SKShaper(e.Face);
                canvas.DrawShapedText(ms, text, x, y, SKTextAlign.Left, font, paint);
            }
            finally { font.Typeface = oldTf; }
            return;
        }
        // 录制趟不落笔（同 DrawText）：shaped 字形已在 RecordGlyphs 里，NoDraw 画布不用真画
        if (!u.SubsetsBuilt) return;
        canvas.DrawShapedText(shaper, text, x, y, SKTextAlign.Left, font, paint);
    }

    // ── 记账内核 ─────────────────────────────────────────────

    /// <summary>批量码位→字形的租用缓冲区：4.x 逐码位 unichar_to_glyph 单发比 2.88 慢一个量级，
    /// 整串一次 text_to_glyphs 平摊（FirstUncovered 同法，见 TextShaper）。</summary>
    [ThreadStatic] static SKFont? tGlyphFont;
    [ThreadStatic] static int[]? tGlyphCps;
    [ThreadStatic] static ushort[]? tGlyphIds;

    void RecordString(SKTypeface tf, string s)
    {
        var set = SetFor(tf);
        var f = tGlyphFont ??= new SKFont(tf);
        if (!ReferenceEquals(f.Typeface, tf)) f.Typeface = tf;
        int n = 0;
        var cps = tGlyphCps ??= new int[s.Length];
        if (cps.Length < s.Length) cps = tGlyphCps = new int[s.Length];
        for (int i = 0; i < s.Length;)
        {
            int cp = char.ConvertToUtf32(s, i);
            i += char.IsHighSurrogate(s[i]) ? 2 : 1;
            cps[n++] = cp;
        }
        var ids = tGlyphIds ??= new ushort[s.Length];
        if (ids.Length < s.Length) ids = tGlyphIds = new ushort[s.Length];
        f.GetGlyphs(cps.AsSpan(0, n), ids.AsSpan(0, n));
        for (int k = 0; k < n; k++)
        {
            int g = ids[k];
            if (g == 0) continue;
            Note(tf, set, g);
        }
    }

    void RecordGlyphs(SKTypeface tf, UInt32[] glyphs)
    {
        var set = SetFor(tf);
        foreach (var g in glyphs)
            if (g != 0) Note(tf, set, (int)g);
    }

    // 该字面身份的用字集；顺带记下这个实例作为裁剪源
    HashSet<int> SetFor(SKTypeface tf)
    {
        var key = KeyOf(tf);
        if (!Used.TryGetValue(key, out var set))
        {
            set = new HashSet<int>();
            Used[key] = set;
            Source[key] = tf;
        }
        return set;
    }

    void Note(SKTypeface tf, HashSet<int> set, int g)
    {
        set.Add(g);
        if (SubsetsBuilt && Subsets.TryGetValue(KeyOf(tf), out var e) && !e.Kept.Contains(g)) Violated = true;
    }
}
