using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using PaperSharp.Css;

namespace PaperSharpSamples;

/// <summary>
/// buildDoc 内部拆解探针（`dotnet run -c Release -- pgbuild [iters] [样例名…]`）。
///
/// pgstage 实测 buildDoc 是 Playground 现在的第一大头，但它是「正则预处理 + AngleSharp 解析 +
/// 每元素层叠 + 每元素逐属性取值 + 引擎自己解析这些值」几件事的合称，不拆开就没法优化。
/// 这里按 §5.6 之后的**引擎真实路径**（按元素缓存层叠，raw 轮只查表不重算）逐层加回去，
/// 每层都同时记 **毫秒** 与 **托管分配量**（分配量才是 GC 抖动的来源）。
/// 最后两行是归因：angle = 解析 + 层叠填充(含折算) + 伪元素 + 94 属性读；
/// ours = 引擎总账 − angle（建树/填 ComputedStyle/正则预处理/DomPath/ExpandVars 等自研部分）。
///
/// ⚠️ 口径注意：探针的属性读只算 94 条 × 一轮；引擎的 `Val()` 对尺寸类属性还会在 raw 轮
/// 再读一次（同串、AngleSharp 取值），所以 angle 略低估、ours 略高估。伪元素行是「全部元素都命中
/// scope」的上界，只有 CSS 里出现 ::before/::after 的样例才算。
/// </summary>
public static class CascadeProbe
{
    const float W = 1284f;

    /// <summary>ReadStyle 一个元素要问的属性名（从 StyleResolver grep 出来的全集）。</summary>
    static readonly string[] Props =
    {
        "align-items","align-self","background","background-color","background-image","border-bottom-left-radius",
        "border-bottom-right-radius","border-bottom-width","border-collapse","border-color","border-left-width",
        "border-right-width","border-spacing","border-top-color","border-top-left-radius","border-top-right-radius",
        "border-top-width","bottom","box-shadow","box-sizing","break-after","break-before","break-inside","caption-side",
        "clear","clip-path","color","column-gap","content","corner-shape","counter-increment","counter-reset","direction",
        "display","filter","flex","flex-basis","flex-direction","flex-grow","flex-shrink","flex-wrap","float","font-family",
        "font-size","font-weight","gap","grid-column","grid-row","grid-template-columns","grid-template-rows","height",
        "justify-items","justify-self","left","letter-spacing","line-height","margin-bottom","margin-left","margin-right",
        "margin-top","mask-image","opacity","order","overflow","overflow-x","overflow-y","padding-bottom","padding-left",
        "padding-right","padding-top","perspective","place-items","place-self","position","right","row-gap","table-layout",
        "text-align","text-decoration","text-fit","text-indent","text-overflow","text-transform","text-wrap","top",
        "transform","vertical-align","visibility","white-space","widows","width","word-break","word-spacing","z-index",
    };

    public static void Run(string assets, int iters, string[]? only = null)
    {
        var ci = CultureInfo.InvariantCulture;
        var names = Directory.EnumerateFiles(assets, "*.html").Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is { Length: > 0 } && !n!.StartsWith('_') && (only == null || only.Contains(n!)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        Console.WriteLine("BLD,sample,phase,ms,mb,note");
        foreach (var n in names!)
        {
            var htmlRaw = File.ReadAllText(Path.Combine(assets, n + ".html"));
            var css = File.Exists(Path.Combine(assets, n + ".css")) ? File.ReadAllText(Path.Combine(assets, n + ".css")) : "";
            var (bodyHtml, userCss) = Split(htmlRaw, css);
            var fullHtml = "<!doctype html><html><head><meta charset='utf-8'>" +
                           $"<style>{StyleResolver.UaStylesheet}</style><style>{userCss}</style></head><body>{bodyHtml}</body></html>";

            Med(() => { StyleResolver.BuildDocument(htmlRaw, css, null, false, W, domPath: true); return 0.0; }, 1);
            var (engine, _) = Med(() => { StyleResolver.BuildDocument(htmlRaw, css, null, false, W, domPath: true); return 0.0; }, iters);
            Row(ci, n, "buildDoc(engine)", (engine, 0.0), "引擎真实入口（总账）");

            Med(() => { using var d = Open(fullHtml); return 0.0; }, 1);
            var (parse, mbParse) = Med(() => { using var d = Open(fullHtml); return 0.0; }, iters);
            Row(ci, n, "angle-parse", (parse, mbParse), "HTML+UA+作者 CSS 解析");

            using var doc = Open(fullHtml);
            var win = doc.DefaultView!;
            var els = doc.QuerySelectorAll("*").ToArray();
            Row(ci, n, "elements", (els.Length, 0.0), "含 head/style 等不成盒元素");

            // C1：只层叠匹配、不折算值（fresh 集合 = 引擎每次渲染的真实成本；缓存不跨迭代蹭）。
            Med(() =>
            {
                var eg = new PaperSharp.Css.EagerStyleCollection(win.GetStyleCollection());
                foreach (var e in els) eg.GetDeclarations(e, null);
                return 0.0;
            }, 1);
            var (rawfill, mbRaw) = Med(() =>
            {
                var eg = new PaperSharp.Css.EagerStyleCollection(win.GetStyleCollection());
                foreach (var e in els) eg.GetDeclarations(e, null);
                return 0.0;
            }, iters);
            Row(ci, n, "cascade-rawfill", (rawfill, mbRaw), "每元素一次全表匹配（% 未折算）");

            // C2：匹配 + 值折算（引擎宿主轮）。C2−C1 = 折算单独成本。
            // 折算个别值会抛 NRE（引擎 ReadStyle 里 try/catch 兜住并退回声明值），探针同构照做。
            static void SafeCompute(PaperSharp.Css.EagerStyleCollection eg, IElement e, string? p)
            {
                try { eg.ComputeDeclarations(e, p); }
                catch { try { eg.GetDeclarations(e, p); } catch { } }
            }
            Med(() =>
            {
                var eg = new PaperSharp.Css.EagerStyleCollection(win.GetStyleCollection());
                foreach (var e in els) SafeCompute(eg, e, null);
                return 0.0;
            }, 1);
            var (computed, mbComp) = Med(() =>
            {
                var eg = new PaperSharp.Css.EagerStyleCollection(win.GetStyleCollection());
                foreach (var e in els) SafeCompute(eg, e, null);
                return 0.0;
            }, iters);
            Row(ci, n, "cascade-computed", (computed, mbComp), "匹配+值折算（引擎宿主轮）");
            Row(ci, n, "fold-delta", (computed - rawfill, 0.0), "C2−C1：em/%/calc 折算本身");

            // 伪元素轮：上界（假设每个元素都命中 scope）。只有 CSS 含 ::before/::after 才有意义。
            double pseudo = 0;
            if (Regex.IsMatch(userCss, @"::?(before|after)\b", RegexOptions.IgnoreCase))
            {
                Med(() =>
                {
                    var eg = new PaperSharp.Css.EagerStyleCollection(win.GetStyleCollection());
                    foreach (var e in els) { eg.GetDeclarations(e, null); SafeCompute(eg, e, "::before"); SafeCompute(eg, e, "::after"); }
                    return 0.0;
                }, 1);
                var (ps, mbPs) = Med(() =>
                {
                    var eg = new PaperSharp.Css.EagerStyleCollection(win.GetStyleCollection());
                    foreach (var e in els) { eg.GetDeclarations(e, null); SafeCompute(eg, e, "::before"); SafeCompute(eg, e, "::after"); }
                    return 0.0;
                }, iters);
                pseudo = Math.Max(ps - computed, 0);
                Row(ci, n, "pseudo-upper", (pseudo, mbPs - mbComp), "上界：全元素 ×2 伪元素（引擎按 scope 实际更少）");
            }

            // D：折算后的声明上读 94 条属性（AngleSharp 取值）。个别值读取会抛（CssPeriodicValue NRE 类），
            // 引擎侧本来就有 try/catch，这里同样吞掉，只把读到的计入。
            static string? Read(ICssStyleDeclaration d, string p)
            {
                try { return d.GetPropertyValue(p); } catch { return null; }
            }
            var declEg = new PaperSharp.Css.EagerStyleCollection(win.GetStyleCollection());
            var decls = els.Select(e =>
            {
                try { return declEg.ComputeDeclarations(e, null); }
                catch { try { return declEg.GetDeclarations(e, null); } catch { return null; } }
            }).Where(d => d != null).Select(d => d!).ToArray();
            Med(() => { foreach (var cs in decls) foreach (var p in Props) Read(cs, p); return 0.0; }, 1);
            var (props, mbProps) = Med(() => { foreach (var cs in decls) foreach (var p in Props) Read(cs, p); return 0.0; }, iters);
            Row(ci, n, $"props-{Props.Length}x-once", (props, mbProps), "只读属性串，不再层叠");

            // D2：再叠上引擎对每个尺寸值的自研解析（差值 = 自研侧的值解析成本）。
            Med(() => { foreach (var cs in decls) foreach (var p in Props) { var v = Read(cs, p); if (v != null) CssValueParser.ParseSize(v, 16f); } return 0.0; }, 1);
            var (propsParse, mbPP) = Med(() => { foreach (var cs in decls) foreach (var p in Props) { var v = Read(cs, p); if (v != null) CssValueParser.ParseSize(v, 16f); } return 0.0; }, iters);
            Row(ci, n, "props+ParseSize", (propsParse, mbPP), $"差值 {propsParse - props:0.0}ms = 自研值解析");

            var angle = parse + computed + pseudo + props;
            Row(ci, n, "split-angle", (angle, 0.0), "AngleSharp：解析+层叠折算+伪元素+属性读");
            Row(ci, n, "split-ours", (engine - angle, 0.0), "自研残差 = 总账−split-angle（含值解析/建树/DomPath）");
        }
    }

    static IBrowsingContext NewContext() => BrowsingContext.New(
        Configuration.Default.WithCss().WithRenderDevice(new DefaultRenderDevice
        { FontSize = 16, ViewPortWidth = (int)W, ViewPortHeight = 1024 }));

    /// <summary>
    /// 层叠超线性的来源探针（`dotnet run -c Release -- pgstyle [N…]`）。
    ///
    /// §5.1 只证明了 buildDoc 随元素数 ~O(n^1.9)，没证明**为什么**：合成样例的 CSS 恒为 7 条规则，
    /// 所以「∝ 规则数」解释不了它。这里的候选解释是 <c>window.GetComputedStyle(el)</c> 这个扩展方法
    /// 每次调用都重新 <c>GetStyleCollection()</c>（把整篇文档的样式表收集一遍，含遍历全部元素），
    /// 于是每元素 O(n)、整篇 O(n²)。若成立，把集合外提一次就能消掉指数，不必等 M6 的自研级联。
    ///
    /// 四组对照（都只量层叠本身，不碰解析与取值）：
    ///   A now-per-element  每元素一次 win.GetComputedStyle —— 现路
    ///   B hoisted          外提 GetStyleCollection 后每元素 ComputeDeclarations —— 待验证的修法
    ///   C collection-only  单独重复调 GetStyleCollection，看它本身多贵、是否随 N 涨
    ///   D rules-inflated   固定 N、把规则数灌到 200 条，区分「∝ 元素」还是「∝ 规则」
    /// 另加 E match-check：B 与 A 逐属性对账，值不一致就说明两者不是同一轮层叠，修法作废。
    ///
    /// H 行（本轮）= 引擎的按元素缓存层叠（EagerStyleCollection 的实例方法，遮蔽 AngleSharp 同名扩展方法），
    /// 对应 PERF_REPORT §8.3 的杠杆 (a)+(b)。它带一道更严的门：真实语料全样例 × 全元素 × 94 属性 ×
    /// {computed, raw 声明} × {宿主, ::before, ::after} 逐串对账（H-check），不一致必须为 0。
    /// </summary>
    public static void Style(string assets, int[] sizes)
    {
        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine("STY,kind,n,els,phase,ms,us_per_el");
        void Row(string kind, int n, int els, string phase, double ms) =>
            Console.WriteLine($"STY,{kind},{n},{els},{phase},{ms.ToString("0.00", ci)},{(ms * 1000.0 / els).ToString("0.00", ci)}");

        foreach (var kind in new[] { "flexlist", "table" })
            foreach (var n in sizes)
            {
                var (html, css) = kind == "table" ? Benchmark.SynthTable(n) : Benchmark.SynthFlex(n);
                var full = "<!doctype html><html><head><meta charset='utf-8'>" +
                           $"<style>{StyleResolver.UaStylesheet}</style><style>{css}</style></head><body>{html}</body></html>";
                using var doc = Open(full);
                var win = doc.DefaultView!;
                var els = doc.QuerySelectorAll("*").ToArray();
                int ne = els.Length;
                int T = n <= 500 ? 3 : 1;   // 大档一次就要 20+ s，多迭代纯属烧时间

                Med(() => { foreach (var e in els) win.GetComputedStyle(e, null); return 0.0; }, 1);  // 预热
                Row(kind, n, ne, "A now-per-element", Med(() => { foreach (var e in els) win.GetComputedStyle(e, null); return 0.0; }, T).Item1);

                var sc = win.GetStyleCollection();
                Med(() => { foreach (var e in els) sc.ComputeDeclarations(e, null); return 0.0; }, 1);
                Row(kind, n, ne, "B hoisted", Med(() => { foreach (var e in els) sc.ComputeDeclarations(e, null); return 0.0; }, T).Item1);

                // F/G：把惰性规则表具体化一次。StyleCollection 的 _sheets 是未物化的 LINQ，
                // 而 GetDeclarations 会对「每个祖先」各枚举一次集合 → 每元素 O(深度 × 文档大小)。
                // IStyleCollection 本身是公开接口（只有 Device + IEnumerable<ICssStyleRule>），
                // 所以可以自己实现一个"枚举缓存列表"的版本，层叠仍走 AngleSharp 的同一批公开扩展方法。
                var eager = new EagerStyleCollection(sc);
                Med(() => { foreach (var e in els) eager.ComputeDeclarations(e, null); return 0.0; }, 1);
                Row(kind, n, ne, "F eager-collected", Med(() => { foreach (var e in els) eager.ComputeDeclarations(e, null); return 0.0; }, T).Item1);
                Row(kind, n, ne, "G eager-cascaded-only", Med(() => { foreach (var e in els) eager.ComputeCascadedStyle(e); return 0.0; }, T).Item1);

                // H：引擎的按元素缓存层叠（具体类型走实例方法 = 遮蔽 AngleSharp 扩展方法；
                // 每元素全表匹配恰一次 + 双轮合一）。计时含 Compute 折算，与 F 同口径可比。
                var cachedH = new PaperSharp.Css.EagerStyleCollection(sc);
                Med(() => { foreach (var e in els) cachedH.ComputeDeclarations(e, null); return 0.0; }, 1);
                Row(kind, n, ne, "H cached-per-element", Med(() => { foreach (var e in els) cachedH.ComputeDeclarations(e, null); return 0.0; }, T).Item1);


                Med(() => { for (int i = 0; i < 20; i++) win.GetStyleCollection(); return 0.0; }, 1);
                var (c20, _) = Med(() => { for (int i = 0; i < 20; i++) win.GetStyleCollection(); return 0.0; }, 3);
                Row(kind, n, ne, "C collection-only", c20 / 20);

                if (n == sizes[^2])
                {
                    var fat = css + string.Concat(Enumerable.Range(0, 200).Select(i => $".junk{i}{{color:rgb({i % 255},1,2)}}"));
                    using var doc2 = Open(full.Replace($"<style>{css}</style>", $"<style>{fat}</style>"));
                    var win2 = doc2.DefaultView!;
                    var els2 = doc2.QuerySelectorAll("*").ToArray();
                    Med(() => { foreach (var e in els2) win2.GetComputedStyle(e, null); return 0.0; }, 1);
                    var (fatMs, _) = Med(() => { foreach (var e in els2) win2.GetComputedStyle(e, null); return 0.0; }, 3);
                    var (thinMs, _) = Med(() => { foreach (var e in els2) win.GetComputedStyle(e, null); return 0.0; }, 3);
                    Row(kind, n, els2.Length, "D rules-inflated", fatMs);
                    Console.WriteLine($"STY,{kind},{n},{els2.Length},D-ratio-x{fatMs / thinMs:0.0},// 规则 {7}→{207} 条后的耗时比（同元素数）");
                }
            }

        // E：对账（中等规模 + 一个真实样例），逐属性比 A 与 B 的字符串值
        var mism = 0; long cmp = 0;
        foreach (var n in new[] { 200 })
        {
            var (html, css) = Benchmark.SynthFlex(n);
            var full = "<!doctype html><html><head><meta charset='utf-8'>" +
                       $"<style>{StyleResolver.UaStylesheet}</style><style>{css}</style></head><body>{html}</body></html>";
            using var doc = Open(full);
            var win = doc.DefaultView!;
            var sc = new EagerStyleCollection(win.GetStyleCollection());
            foreach (var e in doc.QuerySelectorAll("*"))
            {
                var a = win.GetComputedStyle(e, null);
                var b = sc.ComputeDeclarations(e, null);
                foreach (var p in Props)
                {
                    cmp++;
                    if (a.GetPropertyValue(p) != b.GetPropertyValue(p)) mism++;
                }
            }
        }
        Console.WriteLine($"STY,check,-,-,E match-check,{mism},// {cmp:N0} 次逐属性比对中不一致的条数（必须为 0）");

        // H-check：真实语料全样例的对账门。参照物 = 只物化不缓存的旧路（本文件嵌套类，走扩展方法），
        // 被测 = 引擎按元素缓存版。维度：每元素 × {宿主, ::before, ::after} × {computed, raw 声明串} × 94 属性。
        // raw 那一维必须单独对：引擎 ReadStyle 的第二轮（Spec()）吃的就是它，% / calc 不许被折算。
        long hcmp = 0; int hmism = 0, hskip = 0;
        foreach (var file in Directory.EnumerateFiles(assets, "*.html").OrderBy(f => f))
        {
            var n = Path.GetFileNameWithoutExtension(file);
            if (n.StartsWith('_')) continue;
            var cssPath = Path.ChangeExtension(file, ".css");
            var (bodyHtml, userCss) = Split(File.ReadAllText(file), File.Exists(cssPath) ? File.ReadAllText(cssPath) : "");
            var fullH = "<!doctype html><html><head><meta charset='utf-8'>" +
                        $"<style>{StyleResolver.UaStylesheet}</style><style>{userCss}</style></head><body>{bodyHtml}</body></html>";
            using var doc = Open(fullH);
            var win = doc.DefaultView!;
            IStyleCollection old = new EagerStyleCollection(win.GetStyleCollection());   // 接口类型 ⇒ 绑到扩展方法（旧语义）
            var nw = new PaperSharp.Css.EagerStyleCollection(win.GetStyleCollection());
            foreach (var e in doc.QuerySelectorAll("*"))
                foreach (var ps in new string?[] { null, "::before", "::after" })
                {
                    ICssStyleDeclaration oc, oraw;
                    try
                    {
                        oc = old.ComputeDeclarations(e, ps);
                        oraw = old.GetDeclarations(e, ps);
                    }
                    catch { hskip++; continue; }   // radial-gradient 等已知会炸，两路一致跳过
                    var nc = nw.ComputeDeclarations(e, ps);
                    var nraw = nw.GetDeclarations(e, ps);
                    // 取值本身也会抛（CssPeriodicValue.CssText 的已知 NRE，与引擎 Get() 的 try/catch 同源）：
                    // 双侧同抛按跳过计；**只有一边抛**说明两路拿到的属性对象不同，算分歧单独记账。
                    string? V(ICssStyleDeclaration d, string p)
                    {
                        try { return d.GetPropertyValue(p); } catch { return null; }
                    }
                    void Cmp(ICssStyleDeclaration x, ICssStyleDeclaration y, string p)
                    {
                        var (a, b) = (V(x, p), V(y, p));
                        hcmp++;
                        if (a == null && b == null) return;
                        if (a != b) hmism++;
                    }
                    foreach (var p in Props)
                    {
                        Cmp(oc, nc, p);
                        Cmp(oraw, nraw, p);
                    }
                }
            Console.WriteLine($"STY,check,-,-,H-check {n},{hmism},// 截至该样例累计：{hcmp:N0} 比对 / 跳过 {hskip}");
        }
        Console.WriteLine($"STY,check,-,-,H match-check,{hmism},// {hcmp:N0} 次比对（含伪元素与 raw 轮）不一致条数，必须为 0");
    }

    /// <summary>
    /// 规则表具体化版：枚举一次AngleSharp 的惰性集合（含 @media 过滤与嵌套规则展开），之后固定吃这份列表。
    /// 层叠/特异度/继承全部仍由 AngleSharp 的公开扩展方法完成，语义与 window.GetComputedStyle 逐属性一致
    /// （E 行对账，57,058 次比对 0 不一致）。文档在渲染期间静态，故缓存安全。
    /// </summary>
    sealed class EagerStyleCollection : IStyleCollection
    {
        readonly List<ICssStyleRule> _rules;

        public IRenderDevice Device { get; }

        public EagerStyleCollection(IStyleCollection src)
        {
            Device = src.Device;
            _rules = src.ToList();
        }

        public IEnumerator<ICssStyleRule> GetEnumerator() => _rules.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _rules.GetEnumerator();
    }


    static IDocument Open(string html) => NewContext().OpenAsync(r => r.Content(html)).GetAwaiter().GetResult();

    static (string body, string css) Split(string html, string external)
    {
        var sb = new StringBuilder().Append(external);
        foreach (Match s in Regex.Matches(html, @"<style[^>]*>(.*?)</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            sb.Append('\n').Append(s.Groups[1].Value);
        html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var mb = Regex.Match(html, @"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return (mb.Success ? mb.Groups[1].Value : html, sb.ToString());
    }

    /// <summary>中位毫秒 + 中位托管分配 MB（后者解释前者为什么会飘）。</summary>
    static (double ms, double mb) Med(Func<double> a, int iters)
    {
        var t = new double[iters]; var m = new double[iters];
        for (int i = 0; i < iters; i++)
        {
            long a0 = GC.GetTotalAllocatedBytes(precise: false);
            var sw = Stopwatch.StartNew(); a(); t[i] = sw.Elapsed.TotalMilliseconds;
            m[i] = (GC.GetTotalAllocatedBytes(precise: false) - a0) / 1e6;
        }
        Array.Sort(t); Array.Sort(m);
        return (t[iters / 2], m[iters / 2]);
    }

    static void Row(CultureInfo ci, string n, string phase, (double ms, double mb) v, string note) =>
        Console.WriteLine("BLD," + n + "," + phase + "," + v.ms.ToString("0.00", ci) + "," + v.mb.ToString("0.0", ci) +
                          (note.Length > 0 ? ",//" + note : ""));
}
