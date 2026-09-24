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
/// 这里逐层加回去，每层都同时记 **毫秒** 与 **托管分配量**（分配量才是 GC 抖动的来源：
/// 实测同一份 m1 的层叠第二遍比第一遍慢 2.6 倍，不是代码变慢，是垃圾把 GC 拖进来的）。
///
/// 另外看「同一批元素再算一遍是否变快」：快了就说明 AngleSharp 内部有可蹭的缓存。
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

            Row(ci, n, "buildDoc(engine)", Med(() => { StyleResolver.BuildDocument(htmlRaw, css, null, false, W, domPath: true); return 0.0; }, iters),
                "引擎真实入口");
            Row(ci, n, "angle-parse", Med(() => { using var d = Open(fullHtml); return 0.0; }, iters), "HTML+UA+作者 CSS 解析");

            using var doc = Open(fullHtml);
            var win = doc.DefaultView!;
            var els = doc.QuerySelectorAll("*").ToArray();
            Row(ci, n, "elements", (els.Length, 0.0), "含 head/style 等不成盒元素");

            Row(ci, n, "cascade-pass1", Med(() => { foreach (var e in els) win.GetComputedStyle(e, null); return 0.0; }, iters), "每元素一次层叠");
            Row(ci, n, "cascade-pass2", Med(() => { foreach (var e in els) win.GetComputedStyle(e, null); return 0.0; }, iters), "再算一遍：快=有缓存可蹭");
            Row(ci, n, "declarations", Med(() => { var sc = win.GetStyleCollection(); foreach (var e in els) sc.GetDeclarations(e, null); return 0.0; }, iters),
                "引擎的第二轮层叠（% / calc 兜底）");

            var cache = els.Select(e => win.GetComputedStyle(e, null)!).ToArray();
            Row(ci, n, $"props-{Props.Length}x-once", Med(() =>
            {
                foreach (var cs in cache) foreach (var p in Props) cs.GetPropertyValue(p);
                return 0.0;
            }, iters), "只读属性值，不再层叠");
            Row(ci, n, $"props+ParseSize", Med(() =>
            {
                foreach (var cs in cache) foreach (var p in Props) CssValueParser.ParseSize(cs.GetPropertyValue(p), 16f);
                return 0.0;
            }, iters), "再叠上引擎的值解析：看解析占多少");
            Row(ci, n, "props+TrimLower", Med(() =>
            {
                foreach (var cs in cache) foreach (var p in Props) { var v = cs.GetPropertyValue(p); _ = v.Trim().ToLowerInvariant(); }
                return 0.0;
            }, iters), "引擎里 display/text-align 等每次都 Trim+ToLower 的分配代价");
        }
    }

    static IBrowsingContext NewContext() => BrowsingContext.New(
        Configuration.Default.WithCss().WithRenderDevice(new DefaultRenderDevice
        { FontSize = 16, ViewPortWidth = (int)W, ViewPortHeight = 1024 }));

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
