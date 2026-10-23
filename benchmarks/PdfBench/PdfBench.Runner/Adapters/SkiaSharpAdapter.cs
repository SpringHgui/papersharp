using System.Diagnostics;
using System.Globalization;
using PaperSharp.Pdf;
using SkiaSharp;

namespace PdfBench.Runner.Adapters;

/// <summary>
/// SkiaSharp 2.88.9 还原适配器 —— 基准里的「裸画布基线」。
///
/// 定位：Skia 只是一个 2D 矢量/位图画布，没有 HTML、没有 CSS、没有盒模型、没有布局、
/// 没有分页语义，连断行和字体回退都要自己数度量。它恰好是 PaperSharp 的下层（PaperSharp
/// 出 PDF 走的就是 SKDocument.CreatePdf），所以这位选手衡量的是「不用任何文档引擎、
/// 直接从画布手搭」要付多少代码、多少毫秒、多大体积。三页 A4 全部按 challenge.html
/// 逐块手画，坐标系为 PDF point（1/72 英寸），CSS px 按 96dpi 乘 0.75 换算。
///
/// 字体不走 SKTypeface.FromFamilyName（家族名解析依赖平台字体管理器，跨机器结果不稳，
/// 且 msyh.ttc 的面 0 是 "Microsoft YaHei UI" 而非规格页用的 "Microsoft YaHei"），
/// 一律从 C:\Windows\Fonts 的具体文件按 index 载入。
///
/// 出 PDF 这一侧必须自己做字体裁剪：Skia 的 PDF 后端在 Close() 时按 SKTypeface 实例把
/// glyf/cmap/hmtx **整份**嵌进文件，三张雅黑面就是 30.9MB、编码段 1.5s（占裸画布单次的 95%）。
/// 所以 Generate 是两趟：预排版趟在不落一个像素的 1×1 画布上记下每个面真被画到的 GID，
/// 用 PaperSharp 的保 GID 裁剪器把面换成「同一字体 − 没用到的 glyf」，真绘趟用子集面出图——
/// 度量表原样拷贝 ⇒ 栅格结果逐像素不变（体积 31.8MB→0.7MB）。版面固定，第二次起照上一次的
/// 用字清单直接裁面、跳过预排版趟，真绘趟末尾验收清单，不符就退回两趟重画。
///
/// 本文件只放共享原语（调色板/字体路由/断行排版/盒模型/渐变/阴影/图片/占位块），
/// 三页内容在 SkiaSharpAdapter.PageN.cs。
/// </summary>
public sealed partial class SkiaSharpAdapter : IPdfAdapter
{
    public string Name => "skiasharp";

    // ---------- 页面几何（pt）----------
    const float PW = 595.276f, PH = 841.89f;             // A4
    const float MXmm = 14f, TYmm = 12f, BYmm = 16f;
    static float MM(float mm) => mm * 72f / 25.4f;       // CSS mm -> pt
    static float P(float px) => px * 0.75f;              // CSS px -> pt
    static readonly float ML = MM(MXmm), MT = MM(TYmm);
    static readonly float CW = PW - 2 * MM(MXmm);        // 内容宽 182mm
    static readonly float FT = PH - MM(BYmm);            // 页脚区顶

    AdapterContext _ctx = null!;
    readonly Action<SKCanvas>[] _pages;

    public SkiaSharpAdapter() => _pages = new Action<SKCanvas>[] { DrawPage1, DrawPage2, DrawPage3 };

    /// <summary>SKISPLIT=1 时分段计时打到 stderr（本机归因用，不进基准口径）。</summary>
    static bool Timed => Environment.GetEnvironmentVariable("SKISPLIT") is not null;

    /// <summary>
    /// 本文档的用字清单（面 → 排序后的 GID）。版面是写死的 ⇒ 第二次 Generate 起照单裁面、
    /// 整趟预排版（约 150ms）就不必再跑；真绘趟照常记账，末尾与清单比对，不一致说明版面变了，
    /// 自动退回两趟重画一遍（自愈，不会把缺字体的豆腐块留在产物里）。
    /// </summary>
    Dictionary<SKTypeface, int[]>? _plan;

    public void Generate(AdapterContext ctx, string outPdfPath)
    {
        _ctx = ctx;
        if (!Once(outPdfPath, _plan, out var used)) Once(outPdfPath, null, out used);
        _plan = ToPlan(used);
    }

    static Dictionary<SKTypeface, int[]> ToPlan(Dictionary<SKTypeface, HashSet<int>> used)
    {
        var p = new Dictionary<SKTypeface, int[]>(ReferenceEqualityComparer.Instance);
        foreach (var (tf, set) in used) { var a = set.ToArray(); Array.Sort(a); p[tf] = a; }
        return p;
    }

    static bool SamePlan(Dictionary<SKTypeface, int[]> a, Dictionary<SKTypeface, HashSet<int>> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (tf, ids) in a)
            if (!b.TryGetValue(tf, out var set) || set.Count != ids.Length || !ids.All(set.Contains)) return false;
        return true;
    }

    /// <summary>
    /// 生成一遍。plan==null ⇒ 两趟：先在不落一个像素的画布上预排版记字，再裁面真绘；
    /// plan 非空 ⇒ 照单裁面，只跑真绘趟。返回「实际用字与清单是否一致」。
    /// </summary>
    bool Once(string outPdfPath, Dictionary<SKTypeface, int[]>? plan, out Dictionary<SKTypeface, HashSet<int>> used)
    {
        var run = _run = new Pass();
        var sw = Timed ? Stopwatch.StartNew() : null;
        double last = 0;
        double Mark()
        {
            if (sw is null) return 0;
            double now = sw.Elapsed.TotalMilliseconds, d = now - last;
            last = now;
            return d;
        }

        // 预排版趟：走一张 1×1 光栅画布，所有绘制都落在空裁剪外，于是这一趟只剩排版与测量。
        // 之所以要先跑一遍：Skia 的 PDF 后端不裁字体，Close() 按 SKTypeface 实例把 glyf/cmap/hmtx
        // 整份写进 PDF，三张雅黑面就是 30.9MB、编码段 1.5s，占单次生成的 95%。
        if (plan is null)
            using (var surf = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul)))
                foreach (var page in _pages)
                {
                    var c1 = surf!.Canvas;
                    c1.Clear(SKColors.White);
                    page(c1);
                }
        double tRec = Mark();

        // 裁面：保 GID 裁剪器把「整张字体」换成「同一字体 − 没用到的 glyf」，
        // 度量/整形/回退判定与原面逐位一致 ⇒ 出图结果不变，只是嵌进去的字节少了两个数量级。
        var built = plan ?? ToPlan(run.Used);   // 快照：真绘趟继续往 run.Used 加，末尾拿它验收
        foreach (var (src, ids) in built)
            if (SubsetFace(src, ids, run.Owned) is SKTypeface sub) run.Live[src] = sub;
        run.Built = true;
        double tSub = Mark();

        using var doc = SKDocument.CreatePdf(outPdfPath, new SKDocumentPdfMetadata
        {
            Title = "PdfBench Visual Challenge",
            Creator = "SkiaSharp 2.88.9 (hand-drawn)",
            Producer = "PdfBench.Runner",
        })!;
        foreach (var page in _pages)
        {
            var canvas = doc.BeginPage(PW, PH);
            canvas.Clear(SKColors.White);
            page(canvas);
            doc.EndPage();
        }
        double tDraw = Mark();
        doc.Close();
        double tClose = Mark();

        foreach (var f in run.Fonts.Values) f.Dispose();
        foreach (var tf in run.Owned) tf.Dispose();
        _run = null;
        bool match = SamePlan(built, run.Used);
        if (sw is not null)
            Console.Error.WriteLine($"[ski-split] {(plan is null ? "2pass" : "1pass")} rec={tRec:F0} subset={tSub:F0} draw={tDraw:F0} close={tClose:F0} faces={run.Live.Count} glyphs={run.Used.Values.Sum(v => v.Count)} match={match}");
        used = run.Used;
        return match;
    }

    partial void DrawPage1(SKCanvas canvas);
    partial void DrawPage2(SKCanvas canvas);
    partial void DrawPage3(SKCanvas canvas);

    // ---------- 调色板 ----------
    static SKColor C(string hex) => new(
        byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber),
        byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber),
        byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber));
    static SKColor A(SKColor c, float alpha) =>
        new(c.Red, c.Green, c.Blue, (byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255));

    static readonly SKColor
        Ink = C("222222"), Slate = C("34495e"), Tag = C("7f8c8d"), H2Ink = C("2c3e50"),
        Accent = C("3498db"), LblC = C("95a5a6"), Tint = C("ecf0f1"), FtrLn = C("bdc3c7"),
        Red = C("e74c3c"), Blue = C("2980b9"), Green = C("27ae60"), Orange = C("f39c12"),
        Purple = C("8e44ad"), DOrange = C("d35400"), Teal = C("16a085"), Yellow = C("f1c40f"),
        DarkRed = C("c0392b"), Cream = C("fdf6ec"), GrayBg = C("dddddd");

    // ---------- 字体 ----------
    enum Fam { Sans, Light, Mono }

    readonly Dictionary<(Fam, bool, bool), SKTypeface> _tf = new();

    SKTypeface Tf(Fam fam, bool bold = false, bool italic = false)
    {
        if (!_tf.TryGetValue((fam, bold, italic), out var tf))
        {
            const string F = @"C:\Windows\Fonts\";
            static SKTypeface Load(string path) => SKTypeface.FromFile(path, 0);
            tf = (fam, bold, italic) switch
            {
                (Fam.Sans, false, false) => Load(F + "segoeui.ttf"),
                (Fam.Sans, true, false) => Load(F + "segoeuib.ttf"),
                (Fam.Sans, false, true) => Load(F + "segoeuii.ttf"),
                (Fam.Sans, true, true) => Load(F + "segoeuiz.ttf"),
                (Fam.Light, _, false) => Load(F + "msyhl.ttc"),
                (Fam.Light, _, true) => Load(F + "segoeuii.ttf"),
                (Fam.Mono, false, false) => Load(F + "consola.ttf"),
                (Fam.Mono, true, false) => Load(F + "consolab.ttf"),
                (Fam.Mono, false, true) => Load(F + "consolai.ttf"),
                (Fam.Mono, true, true) => Load(F + "consolaz.ttf"),
                _ => Load(F + "segoeui.ttf"),
            };
            _tf[(fam, bold, italic)] = tf;
        }
        return tf;
    }

    /// <summary>
    /// 一次 Generate 的私有状态：字形用量、子集面、SKFont 缓存。
    /// 每线程一份（[ThreadStatic]）——threadsafe 用例会拿同一个适配器实例并发 Generate，
    /// 而子集面/字形集合天生按调用分趟构建，共享可变字典会直接竞态。
    /// </summary>
    sealed class Pass
    {
        public readonly Dictionary<SKTypeface, HashSet<int>> Used = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<SKTypeface, SKTypeface> Live = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<(SKTypeface, float), SKFont> Fonts = new();
        public readonly List<SKTypeface> Owned = new();   // 没进缓存的子集面
        public bool Built;
    }


    /// <summary>
    /// 子集面缓存：同一张面 + 同一套用字 ⇒ 同一份子集字节，裁一次就够。
    /// 每份文档重裁要把整张 glyf 读出来拷一遍（雅黑 12MB），gen2 会被顶到几十次、
    /// 并发吞吐直接塌在 GC 上；留住子集面后单次裁面从 ~45ms 掉到 ~0。
    /// 装不下的（超出上限）挂进本次 Generate 的 Owned，用完即释。
    /// </summary>
    const int SubsetCacheCap = 32;
    static readonly List<(SKTypeface Face, int[] Kept, SKTypeface Subset)> _subsetCache = new();
    static readonly object _subsetLock = new();

    static SKTypeface? SubsetFace(SKTypeface src, int[] ids, List<SKTypeface> owned)
    {
        if (ids.Length == 0) return null;
        lock (_subsetLock)
            foreach (var (f, kept, sub) in _subsetCache)
                if (ReferenceEquals(f, src) && kept.SequenceEqual(ids)) return sub;
        if (TtfSubsetter.Subset(src, ids) is not byte[] bytes) return null;   // CFF/异常 → 维持整嵌
        // 再砍两张「实测零代价」的表（−92KB / 三页 0 像素变化）：
        //   kern —— HarfBuzz 在 GPOS 存在时根本不读 legacy kern，它是死重量；
        //   VDMX —— 只有竖排/设备行高用得上，本文档横排。
        // 不能碰 gasp（砍掉后三页 58,695 像素变化，DirectWrite 按它决定网格拟合与灰度 AA），
        // 也不要碰 GPOS/GSUB/GDEF/name/post/vmtx/vhea：本适配器的真绘趟是拿子集面排版的，
        // 砍 shaping 表等于换字体排版，实测第 1 页会漂 123 像素（详见 PdfBench 报告口径）。
        if (StripTables(bytes, FreeDrop) is byte[] slim) bytes = slim;
        if (SKTypeface.FromData(SKData.CreateCopy(bytes)) is not SKTypeface face) return null;
        lock (_subsetLock)
        {
            if (_subsetCache.Count < SubsetCacheCap)
            {
                _subsetCache.Add((src, ids, face));
                return face;
            }
        }
        owned.Add(face);
        return face;
    }

    static uint TagOf(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    static readonly HashSet<uint> FreeDrop = new() { TagOf("kern"), TagOf("VDMX") };

    /// <summary>
    /// 按 tag 黑名单重建 sfnt：重排表目录与偏移、补 4 字节对齐、重算每表校验和与 head.checkSumAdjustment。
    /// 任何越界/异常一律返回 null（调用方退回未裁表的字节，不赌产物）。
    /// </summary>
    static byte[]? StripTables(byte[] f, HashSet<uint> drop)
    {
        try
        {
            if (f.Length < 12) return null;
            int numTables = f[4] << 8 | f[5];
            if (numTables <= 0 || 12 + 16 * numTables > f.Length) return null;

            var keep = new List<(uint Tag, int Off, int Len)>();
            for (int i = 0; i < numTables; i++)
            {
                int r = 12 + 16 * i;
                uint tag = (uint)(f[r] << 24 | f[r + 1] << 16 | f[r + 2] << 8 | f[r + 3]);
                int off = f[r + 8] << 24 | f[r + 9] << 16 | f[r + 10] << 8 | f[r + 11];
                int len = f[r + 12] << 24 | f[r + 13] << 16 | f[r + 14] << 8 | f[r + 15];
                if (off < 0 || len < 0 || (long)off + len > f.Length) return null;
                if (!drop.Contains(tag)) keep.Add((tag, off, len));
            }
            if (keep.Count == 0 || keep.Count == numTables) return null;

            int total = 12 + 16 * keep.Count;
            foreach (var (_, _, len) in keep) total += len + (4 - (len & 3) & 3);
            var o = new byte[total];
            Buffer.BlockCopy(f, 0, o, 0, 4);
            o[4] = (byte)(keep.Count >> 8); o[5] = (byte)keep.Count;
            int es = 0; while (1 << (es + 1) <= keep.Count) es++;
            int sr = (1 << es) * 16;
            PutBE(o, 6, (uint)sr); PutBE(o, 10, (uint)(keep.Count * 16 - sr));

            int pos = 12 + 16 * keep.Count, headOff = -1;
            for (int i = 0; i < keep.Count; i++)
            {
                var (tag, soff, len) = keep[i];
                int r = 12 + 16 * i;
                PutBE(o, r, tag);
                PutBE(o, r + 4, Checksum(f.AsSpan(soff, len)));
                PutBE(o, r + 8, (uint)pos);
                PutBE(o, r + 12, (uint)len);
                Buffer.BlockCopy(f, soff, o, pos, len);
                if (tag == 0x68656164) headOff = pos;   // "head"
                pos += len + (4 - (len & 3) & 3);
            }
            if (headOff >= 0 && headOff + 12 <= o.Length)
            {
                PutBE(o, headOff + 8, 0);
                PutBE(o, headOff + 8, 0xB1B0AFBAu - Checksum(o));
            }
            return o;
        }
        catch { return null; }
    }

    static void PutBE(byte[] b, int i, uint v)
    {
        b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v;
    }

    static uint Checksum(ReadOnlySpan<byte> d)
    {
        uint sum = 0;
        for (int i = 0; i + 4 <= d.Length; i += 4)
            sum += (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);
        if (d.Length % 4 != 0)
        {
            uint tail = 0;
            for (int i = d.Length - d.Length % 4; i < d.Length; i++) tail |= (uint)d[i] << (24 - 8 * (i % 4));
            sum += tail;
        }
        return sum;
    }

    [ThreadStatic] static Pass? _run;
    static Pass Enter() => _run ??= new Pass();

    /// <summary>记字：记下本面真被画到的 GID，供裁面只删没用的轮廓。两趟都记 ⇒ 第二趟的账本
    /// 就是「清单与实际用字是否一致」的验收依据（单子记在**原面**上，与子集面无关）。</summary>
    static void NoteGlyphs(SKTypeface tf, string text)
    {
        var run = _run;
        if (run is null) return;
        if (!run.Used.TryGetValue(tf, out var set)) run.Used[tf] = set = new HashSet<int>();
        foreach (var ch in text) set.Add(tf.GetGlyph(ch));
    }

    SKFont Font(SKTypeface tf, float sizePt)
    {
        var run = Enter();
        if (run.Built && run.Live.TryGetValue(tf, out var sub)) tf = sub;
        if (!run.Fonts.TryGetValue((tf, sizePt), out var f))
            run.Fonts[(tf, sizePt)] = f = new SKFont(tf, sizePt);
        return f;
    }

    SKFont Font(Fam fam, float sizePt, bool bold, bool italic) => Font(Tf(fam, bold, italic), sizePt);

    /// <summary>
    /// Skia 不做字体回退：Segoe UI / Consolas 遇到汉字直接出豆腐块。
    /// 所以逐字选面——当前面缺字形时换 YaHei 面，与 Chrome 的 CJK 回退行为对齐。
    /// </summary>
    SKTypeface FaceFor(Style s, char ch)
    {
        var tf = Tf(s.Kind, s.Bold, s.Italic);
        if (ch < 0x80 || tf.GetGlyph(ch) != 0) return tf;
        var cjk = CjkFace(s.Kind == Fam.Light ? null : s.Bold);
        return cjk.GetGlyph(ch) != 0 ? cjk : tf;
    }

    /// <summary>中文字体链：Regular=msyh.ttc、Bold=msyhbd.ttc、Light=msyhl.ttc（雅黑无斜体面）。</summary>
    readonly Dictionary<bool?, SKTypeface> _cjk = new();

    SKTypeface CjkFace(bool? bold)
    {
        if (!_cjk.TryGetValue(bold, out var tf))
        {
            const string F = @"C:\Windows\Fonts\";
            tf = SKTypeface.FromFile(bold switch
            {
                null => F + "msyhl.ttc",     // Light
                true => F + "msyhbd.ttc",
                _ => F + "msyh.ttc",
            }, 0);
            if (tf.GetGlyph('排') == 0) tf = SKTypeface.FromFile(F + "msyh.ttc", 0);
            _cjk[bold] = tf;
        }
        return tf;
    }

    // ---------- 文本样式 ----------
    enum Align { Left, Center, Right, Justify }

    /// <summary>一个内联片段的样式。Skia 没有 CSS 层，字重/斜体/字距/下划线全靠这里带。</summary>
    sealed record Style
    {
        public float Size { get; init; } = 10.5f;
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public Fam Kind { get; init; } = Fam.Sans;
        public SKColor Color { get; init; } = Ink;
        public float Spacing { get; init; }        // CSS letter-spacing（px）
        public bool Underline { get; init; }
        public bool Strike { get; init; }
        public bool SmallCaps { get; init; }       // Skia 2.88 无 OpenType 特性开关 → 假小型大写
        public float Alpha { get; init; } = 1f;
    }

    static readonly Style Base = new();

    /// <summary>富文本片段：一段同样式文字。</summary>
    sealed record Run(string Text, Style S);

    /// <summary>把若干 (文字, 样式) 拼成一段，省去样板。</summary>
    static Run[] Segs(params (string text, Style style)[] parts) =>
        parts.Where(p => p.text.Length > 0).Select(p => new Run(p.text, p.style)).ToArray();

    static Run[] Segs(params Run[] runs) => runs;

    // ---------- 断行与排版 ----------
    /// <summary>最小排版块：一个断行单元（CJK 单字、拉丁单词、或空白串）。</summary>
    sealed record Atom(string Text, Style S)
    {
        public float W { get; set; }
    }

    readonly Dictionary<(Style, char), float> _cw = new();

    /// <summary>
    /// 单字推进（不含 letter-spacing），按 (样式, 字符) 记忆。
    /// 断行趟与绘制趟、以及两趟之间的重复都要问同一个字的宽，而 MeasureText 是一次跨原生调用的
    /// UTF-16  marshaling——三页上万字 × 两趟，缓存前它是本适配器最大的一块 CPU。
    /// </summary>
    readonly Dictionary<(Style, char), float> _adv = new();

    /// <summary>单字字符串缓存：逐字排版每字符都要一个 string，别在热循环里反复分配。</summary>
    readonly Dictionary<char, string> _cs = new();

    string CharStr(char ch)
    {
        if (!_cs.TryGetValue(ch, out var s)) _cs[ch] = s = ch.ToString();
        return s;
    }

    float AdvOf(Style s, char ch)
    {
        if (_adv.TryGetValue((s, ch), out var w)) return w;
        var tf = FaceFor(s, ch);
        bool small = s.SmallCaps && char.IsLower(ch);
        string t = small ? CharStr(char.ToUpperInvariant(ch)) : CharStr(ch);
        float size = small ? P(s.Size * 0.78f) : P(s.Size);
        // 一律问**原面**：子集面保 GID、度量表原样拷贝，宽度本就不会变，但把测量钉在原面上
        // 可以让「记字趟/真绘趟」两趟的排版数字与裁剪完全解耦。
        w = Measure(tf, size, t);
        _adv[(s, ch)] = w;
        return w;
    }

    /// <summary>逐字累加宽度（letter-spacing 与字体回退都要求逐字，Skia 2.88 的 SKPaint 没有 LetterSpacing）。</summary>
    float WidthOf(Style s, string text)
    {
        float total = 0;
        foreach (var ch in text)
        {
            if (!_cw.TryGetValue((s, ch), out var w))
            {
                w = AdvOf(s, ch);
                if (s.Spacing != 0f && ch != ' ') w += P(s.Spacing);
                _cw[(s, ch)] = w;
            }
            total += w;
        }
        return total;
    }

    /// <summary>量宽与逐字绘制的草稿画刷：threadstatic —— throughput/threadsafe 用例会 4 线程并发
    /// 跑同一个适配器实例，共享可变 SKPaint 会让彼此的颜色/字面串味（产物字节随之漂移）。</summary>
    [ThreadStatic] static SKPaint? _mpT, _dpT;
    static SKPaint Mp => _mpT ??= new SKPaint();

    /// <summary>SKFont 的 MeasureText 只接受已整形的 ushort 字形序列，量宽只能走 SKPaint 的遗留口。</summary>
    float Measure(SKTypeface tf, float sizePt, string t)
    {
        var mp = Mp;
        mp.Typeface = tf;
        mp.TextSize = sizePt;
        return mp.MeasureText(t);
    }

    static bool IsCjk(char c) =>
        c is >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff' or >= '\uf900' and <= '\ufaff'
        or >= '\u3040' and <= '\u30ff';

    static bool IsCjkPunct(char c) =>
        c is >= '\u3000' and <= '\u303f' or '\uff01' or '\uff08' or '\uff09' or '\uff0c' or '\uff1a'
        or '\uff1b' or '\uff1f' or '\u2014' or '\u2018' or '\u2019' or '\u201c' or '\u201d' or '\u00b7';

    /// <summary>断行点：空白之后、CJK 字之后（句读除外，标点不落行首）。</summary>
    static bool BreakableAfter(string t, int i)
    {
        char ch = t[i];
        if (char.IsWhiteSpace(ch)) return true;
        if (i + 1 >= t.Length) return true;
        char nx = t[i + 1];
        if (IsCjk(ch) && !IsCjkPunct(nx)) return true;
        if (IsCjkPunct(ch)) return true;
        if (IsCjk(nx) || IsCjkPunct(nx)) return true;
        return char.IsWhiteSpace(nx);
    }

    static List<string> AtomsOf(string t)
    {
        var list = new List<string>();
        int start = 0;
        for (int i = 0; i < t.Length; i++)
            if (BreakableAfter(t, i)) { list.Add(t[start..(i + 1)]); start = i + 1; }
        if (start < t.Length) list.Add(t[start..]);
        return list;
    }

    List<Atom> Flatten(IReadOnlyList<Run> runs, bool breakAll)
    {
        var list = new List<Atom>();
        foreach (var r in runs)
        {
            if (r.Text.Length == 0) continue;
            if (breakAll) foreach (var ch in r.Text) { list.Add(new Atom(ch.ToString(), r.S) { W = WidthOf(r.S, ch.ToString()) }); }
            else foreach (var a in AtomsOf(r.Text)) { list.Add(new Atom(a, r.S) { W = WidthOf(r.S, a) }); }
        }
        return list;
    }

    sealed class Line
    {
        public readonly List<Atom> Atoms = new();
        public float Width;
        public int Gaps;                 // 可拉伸的断点间隙数（justify 用）
        public void Add(Atom a) { Atoms.Add(a); Width += a.W; }

    }

    /// <summary>贪心断行（word-break:normal 口径，拉丁词不拆；breakAll 时逐字可拆）。</summary>
    List<Line> WrapLines(List<Atom> atoms, float maxW)
    {
        var lines = new List<Line>();
        var cur = new Line();
        foreach (var a in atoms)
        {
            if (a.Text.Contains('\n'))
            {
                foreach (var part in a.Text.Split('\n'))
                {
                    if (part.Length > 0)
                    {
                        var pa = new Atom(part, a.S) { W = WidthOf(a.S, part) };
                        if (cur.Width + pa.W > maxW && cur.Atoms.Count > 0) { lines.Add(cur); cur = new Line(); }
                        cur.Add(pa);
                    }
                    if (cur.Atoms.Count > 0) { lines.Add(cur); cur = new Line(); }
                }
                continue;
            }
            if (cur.Width + a.W > maxW && cur.Atoms.Count > 0)
            {
                lines.Add(cur);
                cur = new Line();
                if (a.Text.TrimStart().Length == 0) continue;      // 行首空白吞掉
                var trimmed = new Atom(a.Text.TrimStart(), a.S) { W = WidthOf(a.S, a.Text.TrimStart()) };
                cur.Add(trimmed);
                continue;
            }
            cur.Add(a);
        }
        if (cur.Atoms.Count > 0) lines.Add(cur);
        if (lines.Count == 0) lines.Add(new Line());
        foreach (var l in lines) l.Gaps = Math.Max(0, l.Atoms.Count - 1);
        return lines;
    }

    /// <summary>行盒高 = max(CSS 行高, 字号×1.15)；基线按 Chrome 的 half-leading 居中。</summary>
    float LineBoxPx(List<Atom> atoms, float lineHeightPx) =>
        Math.Max(lineHeightPx, atoms.Count == 0 ? 0 : atoms.Max(a => a.S.Size) * 1.15f);

    float DrawLine(SKCanvas c, Line line, float x, float top, float lineHeightPx, Align align, float boxW)
    {
        float lhPt = P(LineBoxPx(line.Atoms, lineHeightPx));
        float asc = 0, desc = 0;
        foreach (var a in line.Atoms)
        {
            var f = Font(FaceFor(a.S, a.Text.Length > 0 ? a.Text[0] : 'a'), P(a.S.Size));
            f.GetFontMetrics(out var m);
            asc = Math.Max(asc, -m.Ascent);
            desc = Math.Max(desc, m.Descent);
        }
        if (asc == 0) { asc = lhPt * 0.8f; desc = lhPt * 0.2f; }
        float baseline = top + (lhPt - (asc + desc)) / 2 + asc;

        float gap = 0, shift = 0;
        switch (align)
        {
            case Align.Right: shift = Math.Max(0, boxW - line.Width); break;
            case Align.Center: shift = Math.Max(0, (boxW - line.Width) / 2); break;
            case Align.Justify when line.Gaps > 0 && line.Width < boxW:
                gap = (boxW - line.Width) / line.Gaps;
                break;
        }

        float cx = x + shift;
        for (int i = 0; i < line.Atoms.Count; i++)
        {
            var a = line.Atoms[i];
            cx += DrawAtom(c, a, cx, baseline);
            if (gap > 0 && i < line.Atoms.Count - 1) cx += gap;
        }
        return lhPt;
    }

    /// <summary>逐字绘制是热循环（三页上万次），paint 复用；四个字段每轮全部就地覆盖，无残留状态。</summary>
    static SKPaint Dp => _dpT ??= new SKPaint { IsAntialias = true };

    /// <summary>逐字绘制（含下划线/删除线/假小型大写），返回推进宽度。</summary>
    float DrawAtom(SKCanvas c, Atom a, float x, float baseline)
    {
        float adv = 0;
        float ulPos = 0, ulThk = 0, skPos = 0, skThk = 0;
        foreach (var ch in a.Text)
        {
            bool small = a.S.SmallCaps && char.IsLower(ch);
            string t = small ? CharStr(char.ToUpperInvariant(ch)) : CharStr(ch);
            var tf = FaceFor(a.S, ch);
            float sizePt = small ? P(a.S.Size * 0.78f) : P(a.S.Size);
            var font = Font(tf, sizePt);
            var paint = Dp;
            paint.Color = a.S.Alpha >= 1f ? a.S.Color : A(a.S.Color, a.S.Alpha);
            paint.Typeface = font.Typeface;        // 落笔用子集面（裁不动的面就是原面）
            paint.TextSize = sizePt;
            float w = AdvOf(a.S, ch);
            NoteGlyphs(tf, t);
            c.DrawText(t, x + adv, baseline, font, paint);
            adv += w;
            if (a.S.Spacing != 0f && ch != ' ') adv += P(a.S.Spacing);

            if (a.S.Underline || a.S.Strike)
            {
                font.GetFontMetrics(out var m);
                ulPos = Math.Max(ulPos, (float?)m.UnderlinePosition ?? 0f);
                ulThk = Math.Max(ulThk, (float?)m.UnderlineThickness ?? 0f);
                skPos = Math.Max(skPos, (float?)m.StrikeoutPosition ?? 0f);
                skThk = Math.Max(skThk, (float?)m.StrikeoutThickness ?? 0f);
            }
        }
        float total = adv - (a.S.Spacing != 0f && !a.Text.EndsWith(' ') ? P(a.S.Spacing) : 0);
        if (a.S.Underline)
        {
            using var p = new SKPaint { Color = a.S.Color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(P(0.5f), ulThk) };
            float y = baseline + ulPos;
            c.DrawLine(x, y, x + total, y, p);
        }
        if (a.S.Strike)
        {
            using var p = new SKPaint { Color = a.S.Color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(P(0.5f), skThk) };
            float y = baseline + skPos;
            c.DrawLine(x, y, x + total, y, p);
        }
        return adv;
    }

    /// <summary>
    /// 富文本段落：从 y 起排，返回下一段起点 y。lineHeightPx 传 CSS 行高的 px 值
    /// （倍数×字号在这里由调用方算好）。bg 非空时先铺整段行盒背景。
    /// </summary>
    float Para(SKCanvas c, IReadOnlyList<Run> runs, float x, float y, float maxW, float lineHeightPx,
               Align align = Align.Left, float indentPx = 0, bool breakAll = false,
               SKColor? bg = null, Radii rad = default)
    {
        var lines = WrapLines(Flatten(runs, breakAll), maxW - P(indentPx));
        if (bg is not null)
        {
            float h = lines.Sum(l => P(LineBoxPx(l.Atoms, lineHeightPx)));
            Fill(c, new SKRect(x, y, x + maxW, y + h), bg.Value, rad);
        }
        foreach (var l in lines) y += DrawLine(c, l, x, y, lineHeightPx, align, maxW);
        return y;
    }

    /// <summary>单行绘制（不断行），baseline 直接给基线 y。用于表头、标签、页眉页脚。</summary>
    void Text(SKCanvas c, IReadOnlyList<Run> runs, float x, float baseline, Align align = Align.Left, float boxW = 0)
    {
        float w = 0;
        var atoms = Flatten(runs, false);
        foreach (var a in atoms) w += a.W;
        float cx = x + align switch
        {
            Align.Right => Math.Max(0, boxW - w),
            Align.Center => Math.Max(0, (boxW - w) / 2),
            _ => 0,
        };
        foreach (var a in atoms) cx += DrawAtom(c, a, cx, baseline);
    }

    // ---------- 盒模型 ----------
    enum BStyle { None, Solid, Dashed, Dotted, Double, Groove, Ridge }

    readonly record struct Side(float W, BStyle Style, SKColor Color)
    {
        public static readonly Side NoBorder = new(0, BStyle.None, default);
        public static Side Solid(float px, SKColor c) => new(px, BStyle.Solid, c);
        public static Side Dashed(float px, SKColor c) => new(px, BStyle.Dashed, c);
        public static Side Dotted(float px, SKColor c) => new(px, BStyle.Dotted, c);
        public static Side Double(float px, SKColor c) => new(px, BStyle.Double, c);
        public static Side Groove(float px, SKColor c) => new(px, BStyle.Groove, c);
        public static Side Ridge(float px, SKColor c) => new(px, BStyle.Ridge, c);
    }

    readonly record struct Radii(SKPoint TL, SKPoint TR, SKPoint BR, SKPoint BL)
    {
        public static readonly Radii Zero = default;
        public SKPoint[] AsArray() => new[] { TL, TR, BR, BL };
        public float Max => Math.Max(Math.Max(TL.X, TR.X), Math.Max(BR.X, BL.X));
    }

    /// <summary>圆角按 CSS px 给（四角 TL,TR,BR,BL），内部换算 pt 并按盒子尺寸等比收缩。</summary>
    static Radii Rad(float tl, float tr = -1, float br = -1, float bl = -1)
    {
        if (tr < 0) { tr = br = bl = tl; }
        else if (br < 0) { br = tl; bl = tr; }            // CSS 二值写法：8px 2px → 对角相同
        return new Radii(new SKPoint(P(tl), P(tl)), new SKPoint(P(tr), P(tr)),
                         new SKPoint(P(br), P(br)), new SKPoint(P(bl), P(bl)));
    }

    static SKRoundRect RRect(SKRect r, Radii rad)
    {
        var rr = new SKRoundRect();
        var pts = rad.AsArray();
        float scale = 1f;
        float sumX = pts[0].X + pts[1].X, sumY = pts[1].Y + pts[2].Y, sumB = pts[2].X + pts[3].X, sumT = pts[3].Y + pts[0].Y;
        if (r.Width > 0 && sumX > 0) scale = Math.Min(scale, r.Width / sumX);
        if (r.Height > 0 && sumY > 0) scale = Math.Min(scale, r.Height / sumY);
        if (r.Width > 0 && sumB > 0) scale = Math.Min(scale, r.Width / sumB);
        if (r.Height > 0 && sumT > 0) scale = Math.Min(scale, r.Height / sumT);
        if (scale < 1f) pts = pts.Select(p => new SKPoint(p.X * scale, p.Y * scale)).ToArray();
        rr.SetRectRadii(r, pts);
        return rr;
    }

    void Fill(SKCanvas c, SKRect r, SKColor color, Radii rad = default)
    {
        if (color.Alpha == 0) return;
        using var p = new SKPaint { Color = color, IsAntialias = true };
        c.DrawRoundRect(RRect(r, rad), p);
    }

    void FillShader(SKCanvas c, SKRect r, SKShader shader, Radii rad = default)
    {
        using var p = new SKPaint { Shader = shader, IsAntialias = true };
        c.DrawRoundRect(RRect(r, rad), p);
    }

    /// <summary>整块透明度：Skia 用 saveLayer 真做（这是画布原生的强项，无需预混色近似）。</summary>
    void Layer(SKCanvas c, SKRect r, float alpha, Action<SKCanvas> draw)
    {
        int save = c.SaveLayer(new SKPaint { Color = new SKColor(0, 0, 0, (byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255)) });
        draw(c);
        c.RestoreToCount(save);
    }

    /// <summary>
    /// 逐边边框：先裁到外圆角轮廓，再逐边画条带。虚线/点线用 PathEffect；
    /// double 画外内两条（中间不涂色，露出底下背景）；groove/ridge 用同色明暗两条近似
    /// ——Skia 没有 CSS 的立体边框语义。
    /// </summary>
    void Border(SKCanvas c, SKRect r, Radii rad, Side top, Side right, Side bottom, Side left)
    {
        if (top.Style == BStyle.None && right.Style == BStyle.None &&
            bottom.Style == BStyle.None && left.Style == BStyle.None) return;

        int save = c.Save();
        using (var clip = new SKPath())
        {
            clip.AddRoundRect(RRect(r, rad));
            c.ClipPath(clip, SKClipOperation.Intersect, true);
        }
        Edge(c, r, top, 0);
        Edge(c, r, right, 1);
        Edge(c, r, bottom, 2);
        Edge(c, r, left, 3);
        c.RestoreToCount(save);
    }

    void Box(SKCanvas c, SKRect r, SKColor? fill, Side side, Radii rad = default)
    {
        if (fill is not null) Fill(c, r, fill.Value, rad);
        Border(c, r, rad, side, side, side, side);
    }

    void Edge(SKCanvas c, SKRect r, Side s, int side)   // 0=top 1=right 2=bottom 3=left
    {
        if (s.Style == BStyle.None || s.W <= 0f) return;
        float w = P(s.W);
        bool vert = side is 1 or 3;
        bool outerFirst = side is 0 or 1;
        SKRect strip = vert
            ? new SKRect(outerFirst ? r.Right - w : r.Left, r.Top, outerFirst ? r.Right : r.Left + w, r.Bottom)
            : new SKRect(r.Left, outerFirst ? r.Top : r.Bottom - w, r.Right, outerFirst ? r.Top + w : r.Bottom);

        switch (s.Style)
        {
            case BStyle.Solid:
                Fill(c, strip, s.Color);
                break;
            case BStyle.Double:
                {
                    float t = w / 3f;
                    SKRect a = vert
                        ? new SKRect(outerFirst ? r.Right - w : r.Left, r.Top, outerFirst ? r.Right - w + t : r.Left + t, r.Bottom)
                        : new SKRect(r.Left, outerFirst ? r.Top : r.Bottom - w, r.Right, outerFirst ? r.Top + t : r.Bottom - w + t);
                    SKRect b = vert
                        ? new SKRect(outerFirst ? r.Right - t : r.Left + t, r.Top, outerFirst ? r.Right : r.Left + w - t, r.Bottom)
                        : new SKRect(r.Left, outerFirst ? r.Top + w - t : r.Bottom - t, r.Right, outerFirst ? r.Top + w : r.Bottom);
                    Fill(c, a, s.Color);
                    Fill(c, b, s.Color);
                    break;
                }
            case BStyle.Dashed or BStyle.Dotted:
                Dash(c, strip, w, s, vert, outerFirst, s.Style == BStyle.Dotted);
                break;
            case BStyle.Groove or BStyle.Ridge:
                {
                    float half = w / 2;
                    SKRect a = vert
                        ? new SKRect(outerFirst ? r.Right - half : r.Left, r.Top, outerFirst ? r.Right : r.Left + half, r.Bottom)
                        : new SKRect(r.Left, outerFirst ? r.Top : r.Bottom - half, r.Right, outerFirst ? r.Top + half : r.Bottom);
                    SKRect b = vert
                        ? new SKRect(outerFirst ? r.Right - w : r.Left + half, r.Top, outerFirst ? r.Right - half : r.Left + w, r.Bottom)
                        : new SKRect(r.Left, outerFirst ? r.Top + half : r.Bottom - w, r.Right, outerFirst ? r.Top + w : r.Bottom - half);
                    // groove：外暗内亮；ridge：外亮内暗。上下与左右的明暗分配同 CSS 口径。
                    bool outerDark = s.Style == BStyle.Groove;
                    Fill(c, a, outerDark ? Darken(s.Color) : Lighten(s.Color));
                    Fill(c, b, outerDark ? Lighten(s.Color) : Darken(s.Color));
                    break;
                }
        }
    }

    void Dash(SKCanvas c, SKRect strip, float w, Side s, bool vert, bool outerFirst, bool dotted)
    {
        float on = dotted ? w : w * 3;
        float off = dotted ? w : w * 2;
        using var p = new SKPaint
        {
            Color = s.Color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = w,
            PathEffect = SKPathEffect.CreateDash(new[] { on, off }, 0),
        };
        using var path = new SKPath();
        float mid = vert ? (outerFirst ? strip.Right - w / 2 : strip.Left + w / 2)
                         : (outerFirst ? strip.Top + w / 2 : strip.Bottom - w / 2);
        if (vert) { path.MoveTo(mid, strip.Top); path.LineTo(mid, strip.Bottom); }
        else { path.MoveTo(strip.Left, mid); path.LineTo(strip.Right, mid); }
        c.DrawPath(path, p);
    }

    static SKColor Darken(SKColor c, float k = 0.66f) =>
        new((byte)(c.Red * k), (byte)(c.Green * k), (byte)(c.Blue * k), c.Alpha);

    static SKColor Lighten(SKColor c, float k = 1.6f) =>
        new((byte)Math.Min(255, c.Red * k), (byte)Math.Min(255, c.Green * k), (byte)Math.Min(255, c.Blue * k), c.Alpha);

    // ---------- 渐变 ----------
    /// <summary>CSS linear-gradient 角度语义（0deg 向上、90deg 向右），按盒子对角投影。</summary>
    SKShader LinGrad(SKRect r, double angleCss, SKColor[] cols, float[]? stops = null)
    {
        double a = angleCss * Math.PI / 180;
        double dx = Math.Sin(a), dy = -Math.Cos(a);
        double len = Math.Abs(r.Width * Math.Sin(a)) + Math.Abs(r.Height * Math.Cos(a));
        double cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        return SKShader.CreateLinearGradient(
            new SKPoint((float)(cx - dx * len / 2), (float)(cy - dy * len / 2)),
            new SKPoint((float)(cx + dx * len / 2), (float)(cy + dy * len / 2)),
            cols, stops, SKShaderTileMode.Clamp);
    }

    SKShader RadGrad(SKRect r, float atX, float atY, SKColor[] cols, float[]? stops = null)
    {
        var at = new SKPoint(r.Left + atX * r.Width, r.Top + atY * r.Height);
        float rad = (float)Math.Sqrt(r.Width * r.Width + r.Height * r.Height) / 2;
        return SKShader.CreateRadialGradient(at, rad, cols, stops, SKShaderTileMode.Clamp);
    }

    // ---------- 阴影 ----------
    void ShadowOuter(SKCanvas c, SKRect r, float dx, float dy, float blurPx, SKColor color, Radii rad = default)
    {
        using var p = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, P(blurPx) / 2f),
        };
        c.DrawRoundRect(RRect(new SKRect(r.Left + P(dx), r.Top + P(dy), r.Right + P(dx), r.Bottom + P(dy)), rad), p);
    }

    void ShadowHard(SKCanvas c, SKRect r, float dx, float dy, SKColor color, Radii rad = default) =>
        Fill(c, new SKRect(r.Left + P(dx), r.Top + P(dy), r.Right + P(dx), r.Bottom + P(dy)), color, rad);

    /// <summary>
    /// inset：裁进盒子，再用「外扩矩形 XOR 盒形」的偶奇路径加模糊，从内边缘渗出阴影。
    /// 必须在盒子背景铺好之后调用——它画在内容之上，不像真 inset 那样被背景垫在下面。
    /// </summary>
    void ShadowInset(SKCanvas c, SKRect r, float dx, float dy, float blurPx, SKColor color, Radii rad = default)
    {
        int save = c.Save();
        using (var inner = new SKPath())
        {
            inner.AddRoundRect(RRect(r, rad));
            c.ClipPath(inner, SKClipOperation.Intersect, true);
        }
        using (var ring = new SKPath())
        {
            float pad = P(Math.Max(Math.Abs(dx), Math.Abs(dy)) + blurPx * 3);
            ring.AddRect(new SKRect(r.Left - pad, r.Top - pad, r.Right + pad, r.Bottom + pad));
            ring.AddRoundRect(RRect(r, rad));
            ring.FillType = SKPathFillType.EvenOdd;
            using var p = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, P(blurPx) / 2f),
            };
            int s2 = c.Save();
            c.Translate(P(dx), P(dy));
            c.DrawPath(ring, p);
            c.RestoreToCount(s2);
        }
        c.RestoreToCount(save);
    }

    // ---------- 图片 ----------
    void Img(SKCanvas c, string fileName, SKRect dest, Radii rad = default)
    {
        var path = Path.Combine(_ctx.AssetsDir, fileName);
        using var bmp = SKBitmap.Decode(path);
        if (bmp is null) { Fail(c, dest, "image"); return; }
        int save = c.Save();
        if (rad.Max > 0)
        {
            using var clip = new SKPath();
            clip.AddRoundRect(RRect(dest, rad));
            c.ClipPath(clip, SKClipOperation.Intersect, true);
        }
        using var img = SKImage.FromBitmap(bmp);
        c.DrawImage(img, dest);
        c.RestoreToCount(save);
    }

    /// <summary>把矩形按 CSS 定位语义取整：x/y/w/h 全用 pt。</summary>
    static SKRect Rect(float x, float y, float w, float h) => new(x, y, x + w, y + h);

    // ---------- 占位约定 ----------
    /// <summary>库做不到的特性：灰块 + 红 ✗ + 一行说明，版面流照常占位（与其它适配器同一约定）。</summary>
    void Fail(SKCanvas c, SKRect r, string note)
    {
        Fill(c, r, GrayBg);
        float s = Math.Min(r.Width, r.Height) * 0.22f;
        using var p = new SKPaint
        {
            Color = DarkRed,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, s / 4f),
            StrokeCap = SKStrokeCap.Round,
        };
        float cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        c.DrawLine(cx - s, cy - s, cx + s, cy + s, p);
        c.DrawLine(cx + s, cy - s, cx - s, cy + s, p);
        if (note.Length > 0 && r.Width > P(70))
        {
            using var tp = new SKPaint { Color = DarkRed, IsAntialias = true };
            var cf = CjkFace(false);
            NoteGlyphs(cf, "× " + note);
            c.DrawText("× " + note, r.Left + P(4), r.Bottom - P(4), Font(cf, P(7)), tp);
        }
    }

    // ---------- 页框（三页共用的页眉/页脚）----------
    void Frame(SKCanvas c, string tag, int pageNo)
    {
        var brand = new Style { Size = 13, Bold = true, Color = Slate, Spacing = 1 };
        var tagS = new Style { Size = 10, Color = Tag };
        Text(c, Segs(("PDFBENCH · VISUAL CHALLENGE", brand)), ML, MT + P(13));
        Text(c, Segs((tag, tagS)), ML, MT + P(13), Align.Right, CW);
        using (var ln = new SKPaint { Color = Slate, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = P(2) })
            c.DrawLine(ML, MT + P(21), ML + CW, MT + P(21), ln);

        // .ftr { bottom:6mm; border-top:1px; padding-top:1.5mm } 行盒高由 16px 页码胶囊决定
        float line = PH - MM(6f) - P(16) - MM(1.5f);
        float top = line + MM(1.5f);
        using (var fl = new SKPaint { Color = FtrLn, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = P(1) })
            c.DrawLine(ML, line, ML + CW, line, fl);
        var foot = new Style { Size = 9, Color = Tag };
        Text(c, Segs(("PdfBench · challenge.html · generated by spec v0.1", foot)), ML, top + P(11.5f));
        var pill = Rect(ML + CW - P(34), top, P(34), P(16));
        StrokeBorder(c, pill, Rad(8), 1, Slate);   // r=高/2 的胶囊：条带覆边会漏掉两侧半圆，改中心线描边
        Text(c, Segs(($"{pageNo} / 3", new Style { Size = 9, Bold = true, Color = Slate })),
            pill.Left, pill.Top + P(11.5f), Align.Center, pill.Width);
    }

    /// <summary>h2 小节标题：左侧 4px 蓝色竖条 + 文字，返回下一段 y。</summary>
    float H2(SKCanvas c, string text, float y)
    {
        var box = Rect(ML, y, CW, P(14));
        Border(c, box, Radii.Zero, Side.NoBorder, Side.NoBorder, Side.NoBorder, Side.Solid(4, Accent));
        Text(c, Segs((text, new Style { Size = 14, Bold = true, Color = H2Ink })), ML + P(6), y + P(11));
        return y + P(14) + MM(2);   // h2 margin-bottom 2mm
    }
}
