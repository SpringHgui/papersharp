using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using PaperSharp.Css;
using SkiaSharp;
using PaperSharp;
using PaperSharp.Css;
using PaperSharp.Layout;
using PaperSharp.Paint;
using PaperSharp.Pdf;

namespace PdfBench.Runner;

/// <summary>
/// 阶段探针：把 <see cref="Renderer.RenderPdf"/> 的主链路逐项拆开计时，回答「51.6 ms/页 花在哪」。
///
/// 为什么不在 src 里加计时器：Renderer.cs 正被在途改动占着（工作树 48 个 src 文件未提交），
/// 在这里按同一顺序复刻一遍，零冲突，而且能顺手塞进「假想的替代实现」做 A/B。
/// 复刻的正确性靠两件事钉住：① 末行 total 与 LAT 里 RenderPdf 的实测中位对齐；
/// ② 阶段顺序与 Renderer.cs:19-70 逐行同构（含 RenderScope / PdfGlyphUsage 的作用域进出）。
///
/// 关键的一对比试：<b>录制趟</b>现在画在 <c>SKSurface</c>（真光栅、结果丢掉）上；
/// 这里同时量一份画在 <c>SKPictureRecorder</c>（只记显示列表、不光栅）上的成本。
/// 字形记账发生在托管侧的绘制点（PdfGlyphUsage 的钩子），与画布是否光栅无关，
/// 所以只要 recorder 版显著便宜，第一刀就是换画布而不是重写记账。
/// </summary>
public static class StageProbe
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: stageprobe <html路径> [iters=7] [pageSize=A4]");
            return 2;
        }
        var path = Path.GetFullPath(args[1]);
        int iters = args.Length >= 3 ? int.Parse(args[2]) : 7;
        var html = File.ReadAllText(path);
        var cssPath = Path.ChangeExtension(path, ".css");
        var css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";
        var baseDir = Path.GetDirectoryName(path);

        var acc = new Dictionary<string, List<double>>();
        void Add(string k, double ms) { if (!acc.ContainsKey(k)) acc[k] = new(); acc[k].Add(ms); }
        int pageCount = 0; long bytes = 0;

        for (int it = 0; it < iters; it++)
        {
            var sw = Stopwatch.StartNew();
            double prev = 0;
            // 每段计时 = 累计秒表打点差，避免「拿当前读数当段耗时」的错
            void Mark(string k)
            {
                var now = sw.Elapsed.TotalMilliseconds;
                Add(k, now - prev);
                prev = now;
            }

            using var scope = RenderScope.Begin(baseDir);
            FontFaceParser.ParseAndRegister(css); Mark("01 prepare");

            var spec = ParsePage(html, css, 595.28f * PageSpec.PxPerPt, 841.89f * PageSpec.PxPerPt);
            Mark("02 atpage");

            var computed = StyleResolver.BuildDocument(html, css);
            Mark("03 buildDoc");

            var body = LayoutBuilder.ToBoxTree(computed);
            Mark("04 toBoxTree");

            LayoutBuilder.Measure(body, spec.ContentW);
            Mark("05 measure");

            var pages = Paginator.Paginate(body, spec);
            Mark("06 paginate");
            pageCount = pages.Count;

            var (outlines, links) = PdfSemantic.Collect(pages);
            Mark("07 semantic");

            int pw = Math.Max(1, (int)Math.Ceiling(spec.PdfPageW)), ph = Math.Max(1, (int)Math.Ceiling(spec.PdfPageH));

            // ① 现状：录制趟画进真实光栅 surface。
            //    **记账作用域必须一直开着到真绘结束**——子集 face 是挂在 PdfGlyphUsage.Current 上的，
            //    早版把 Encode 放在 using 外面，量出来的「真绘趟」其实是整字体直嵌（A30 产物 23MB），
            //    结论完全错。这里与 Renderer.cs:46-69 的作用域严格同构。
            byte[] raw;
            using (var usage = PdfGlyphUsage.Begin())
            {
                using (var scratch = SKSurface.Create(new SKImageInfo(pw, ph)))
                    for (int i = 0; i < pages.Count; i++)
                    {
                        scratch.Canvas.Clear(SKColors.White);
                        scratch.Canvas.Save();
                        scratch.Canvas.Scale(PageSpec.PtPerPx, PageSpec.PtPerPx);
                        PagePainter.PaintPage(scratch.Canvas, pages[i], spec.PageW, spec.PageH);
                        MarginBoxRenderer.Render(scratch.Canvas, spec, i + 1, pages.Count);
                        scratch.Canvas.Restore();
                    }
                Mark("08 recordpass(RASTER)");
                usage.BuildSubsets();
                Mark("09 buildsubsets");

                // ③ 真绘趟 = SKDocument PDF 输出（子集生效，含 Skia 侧嵌入与 deflate）
                raw = Encode(pages, spec, pw, ph);
                Mark("10 realencode(PDF)");
                bytes = raw.LongLength;

                var enh = PdfEnhancer.Enhance(raw, outlines, links, spec.PdfPageH, PageSpec.PtPerPx);
                Mark("11 enhance");
            }

            // ② 备选：同一套绘制画进 picture recorder（不光栅）。单独一趟、独立记账，
            //    免得两本账互相污染（PdfGlyphUsage 每线程只有一个 Current）。
            using (var usage2 = PdfGlyphUsage.Begin())
            {
                var rec = new SKPictureRecorder();
                for (int i = 0; i < pages.Count; i++)
                {
                    var canvas = rec.BeginRecording(new SKRect(0, 0, pw, ph));
                    canvas.Clear(SKColors.White);
                    canvas.Save();
                    canvas.Scale(PageSpec.PtPerPx, PageSpec.PtPerPx);
                    PagePainter.PaintPage(canvas, pages[i], spec.PageW, spec.PageH);
                    MarginBoxRenderer.Render(canvas, spec, i + 1, pages.Count);
                    canvas.Restore();
                    rec.EndRecording()?.Dispose();
                }
                rec.Dispose();
                Mark("08b recordpass(RECORDER)");
                usage2.BuildSubsets();
                Mark("09b buildsubsets(rec)");
            }
            Add("99 total(含两版录制趟)", sw.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine($"# {Path.GetFileName(path)}  pages={pageCount} bytes={bytes / 1024}KB iters={iters}");
        foreach (var (k, v) in acc.OrderBy(p => p.Key))
        {
            var med = Median(v);
            Console.WriteLine($"{k,-24} med={med,9:F2} ms  min={v.Min(),9:F2}  max={v.Max(),9:F2}" +
                              (pageCount > 0 ? $"   ({med / pageCount,7:F2} ms/页)" : ""));
        }
        return 0;
    }

    /// <summary>
    /// buildDoc 分解：用「全量 / 剥掉 &lt;style&gt; / 只有样式 / 空文档」四点差值，
    /// 把 610 ms 的 BuildDocument 拆成 HTML 解析、CSS 解析、层叠匹配三块。
    /// 只看公开入口 StyleResolver.BuildDocument，不进引擎内部，改起来才有着力点。
    /// </summary>
    public static int RunDocSplit(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: docsplit <html路径> [iters=7]");
            return 2;
        }
        var path = Path.GetFullPath(args[1]);
        int iters = args.Length >= 3 ? int.Parse(args[2]) : 7;
        var html = File.ReadAllText(path);
        var stripped = Regex.Replace(html, @"<style[^>]*>.*?</style>", "",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var styles = string.Join("\n", Regex.Matches(html, @"<style[^>]*>(.*?)</style>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline).Select(m => m.Groups[1].Value));
        var styleOnly = "<!doctype html><html><head><style>" + styles + "</style></head><body>x</body></html>";
        var empty = "<!doctype html><html><head></head><body></body></html>";
        int elems = Regex.Matches(html, "<[a-zA-Z]").Count;

        double Time(string doc)
        {
            var ts = new List<double>();
            for (int i = 0; i < iters; i++)
            {
                using var scope = RenderScope.Begin(Path.GetDirectoryName(path));
                var sw = Stopwatch.StartNew();
                StyleResolver.BuildDocument(doc, "");
                sw.Stop();
                ts.Add(sw.Elapsed.TotalMilliseconds);
            }
            ts.Sort();
            return ts[ts.Count / 2];
        }

        double tEmpty = Time(empty), tStyle = Time(styleOnly), tNoStyle = Time(stripped), tFull = Time(html);

        // 再切一刀：477 ms 的「HTML 解析」到底是 AngleSharp 的 tokenizer/tree-builder，
        // 还是我们把它的 DOM 翻成 ComputedStyle 树的那趟遍历。前者要换解析器，后者改遍历就够。
        double tAng = 0;
        {
            var ts = new List<double>();
            var parser = new AngleSharp.Html.Parser.HtmlParser();
            for (int i = 0; i < iters; i++)
            {
                var sw = Stopwatch.StartNew();
                var d = parser.ParseDocument(html);
                sw.Stop();
                ts.Add(sw.Elapsed.TotalMilliseconds);
            }
            ts.Sort();
            tAng = ts[ts.Count / 2];
        }

        Console.WriteLine($"# {Path.GetFileName(path)}  起始标签 {elems:N0} 个，iters={iters}");
        Console.WriteLine($"空文档            {tEmpty,9:F2} ms   （AngleSharp 骨架 + 引擎初始化）");
        Console.WriteLine($"只有 <style>      {tStyle,9:F2} ms   （CSS 文本解析：{tStyle - tEmpty:F2}）");
        Console.WriteLine($"剥掉 <style>      {tNoStyle,9:F2} ms   （HTML 解析：{tNoStyle - tEmpty:F2}）");
        Console.WriteLine($"全量              {tFull,9:F2} ms");
        Console.WriteLine($"→ HTML 解析 ≈ {tNoStyle - tEmpty:F1} ms | CSS 解析 ≈ {tStyle - tEmpty:F1} ms" +
                          $" | 层叠匹配 ≈ {tFull - tNoStyle - (tStyle - tEmpty):F1} ms");
        double htmlParse = tNoStyle - tEmpty;
        double ourWalk = htmlParse - tAng;
        double walkPct = htmlParse > 0 ? 100.0 * ourWalk / htmlParse : 0;
        Console.WriteLine($"纯 AngleSharp ParseDocument = {tAng:F2} ms" +
                          $"  → 我们的 DOM 遍历 ≈ {ourWalk:F1} ms（占「HTML 解析」的 {walkPct:F0}%）");
        return 0;
    }

    /// <summary>
    /// 层叠归因：每元素 110 µs 里，「AngleSharp 跑一轮层叠」与「我们随后按名字读 ~130 个属性」各占多少。
    /// 这决定修法是便宜的（一次层叠→一张表，读走字典）还是昂贵的（自己接管层叠、拆 AngleSharp.Css）。
    /// 用引擎同一份 UA 表（StyleResolver.UaStylesheet 是 public const），同一份文档、同一套样式来源。
    /// </summary>
    public static int RunCascadeSplit(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: cascadesplit <html路径> [iters=5]");
            return 2;
        }
        var path = Path.GetFullPath(args[1]);
        int iters = args.Length >= 3 ? int.Parse(args[2]) : 5;
        var html = File.ReadAllText(path);
        var styles = string.Join("\n", Regex.Matches(html, @"<style[^>]*>(.*?)</style>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline).Select(m => m.Groups[1].Value));
        var body = Regex.Match(html, @"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var inner = body.Success ? body.Groups[1].Value : html;
        var fullHtml = "<!doctype html><html><head><meta charset='utf-8'>" +
                       $"<style>{StyleResolver.UaStylesheet}</style><style>{styles}</style>" +
                       $"</head><body>{inner}</body></html>";

        var device = new AngleSharp.Css.DefaultRenderDevice
        { FontSize = 16, ViewPortWidth = 794, ViewPortHeight = 1024 };
        var ctx = AngleSharp.BrowsingContext.New(
            AngleSharp.Configuration.Default.WithCss().WithRenderDevice(device));
        using var doc = ctx.OpenAsync(r => r.Content(fullHtml)).GetAwaiter().GetResult();
        var win = doc.DefaultView!;
        // 必须吃引擎那层缓存：AngleSharp 裸扩展方法对每元素每个祖先各枚举一次全表
        // （O(深度×文档)），量出来 1322 µs/元素，会把结论整个带偏。EagerStyleCollection 才是实际路径。
        var collected = new EagerStyleCollection(win.GetStyleCollection());
        var elems = doc.QuerySelectorAll("*");
        Console.WriteLine($"# {Path.GetFileName(path)}  元素 {elems.Length:N0} 个，iters={iters}");

        double Med(Func<double> f)
        {
            var t = new List<double>();
            for (int i = 0; i < iters; i++) t.Add(f());
            t.Sort();
            return t[t.Count / 2];
        }

        // A. 只跑层叠，不读任何属性
        double tA = Med(() =>
        {
            var sw = Stopwatch.StartNew();
            foreach (var el in elems) { try { var d = collected.ComputeDeclarations(el, null); GC.KeepAlive(d); } catch { } }
            return sw.Elapsed.TotalMilliseconds;
        });

        // B. 层叠 + 按名字读 N 个属性（N = 引擎实际读的量级）
        string[] props = Props;
        double tB = Med(() =>
        {
            var sw = Stopwatch.StartNew();
            long sink = 0;
            foreach (var el in elems)
            {
                ICssStyleDeclaration? d;
                try { d = collected.ComputeDeclarations(el, null); } catch { continue; }   // 引擎侧同样兜异常（radial-gradient 等）
                for (int i = 0; i < props.Length; i++) sink += d.GetPropertyValue(props[i]).Length;
            }
            GC.KeepAlive(sink);
            return sw.Elapsed.TotalMilliseconds;
        });

        // C. 只读属性（复用一次层叠结果，看读操作本身的单价）
        double tC = Med(() =>
        {
            var sw = Stopwatch.StartNew();
            long sink = 0;
            foreach (var el in elems)
            {
                ICssStyleDeclaration? d;
                try { d = collected.ComputeDeclarations(el, null); } catch { continue; }
                for (int i = 0; i < 8; i++) sink += d.GetPropertyValue("margin-top").Length;
            }
            GC.KeepAlive(sink);
            return sw.Elapsed.TotalMilliseconds;
        });

        int n = elems.Length;
        Console.WriteLine($"A 只层叠            {tA,9:F1} ms  = {tA * 1000 / n,7:F1} µs/元素");
        Console.WriteLine($"B 层叠+读{props.Length}属性 {tB,9:F1} ms  = {tB * 1000 / n,7:F1} µs/元素");
        Console.WriteLine($"C 层叠+读8次同名属性   {tC,9:F1} ms  = {tC * 1000 / n,7:F1} µs/元素");
        Console.WriteLine($"→ 属性读净增 ≈ {(tB - tA) * 1000 / n:F1} µs/元素" +
                          $"（占 B 的 {100.0 * (tB - tA) / tB:F0}%）；层叠+值折算本体占 {100.0 * tA / tB:F0}%");
        Console.WriteLine("   （对照：整份 BuildDocument ≈ 510 ms = 132 µs/元素，其中 Walk 前的准备 ≈ 0）");
        return 0;
    }

    /// <summary>引擎 ReadStyle 实际会问到的属性名（量级对齐用，不求逐字一致）。</summary>
    static readonly string[] Props = new[]
    {
        "display","position","top","right","bottom","left","float","clear","z-index",
        "width","height","min-width","min-height","max-width","max-height","aspect-ratio",
        "margin-top","margin-right","margin-bottom","margin-left",
        "padding-top","padding-right","padding-bottom","padding-left",
        "border-top-width","border-right-width","border-bottom-width","border-left-width",
        "border-top-style","border-right-style","border-bottom-style","border-left-style",
        "border-top-color","border-right-color","border-bottom-color","border-left-color",
        "border-top-left-radius","border-top-right-radius","border-bottom-right-radius","border-bottom-left-radius",
        "background-color","background-image","background-position","background-size","background-repeat",
        "color","font-family","font-size","font-weight","font-style","font-variant","font-stretch",
        "line-height","letter-spacing","word-spacing","text-align","text-indent","text-transform",
        "text-decoration-line","text-decoration-color","text-decoration-style","text-shadow",
        "white-space","word-break","overflow-wrap","vertical-align","visibility","opacity",
        "overflow-x","overflow-y","transform","transform-origin","box-shadow","box-sizing",
        "list-style-type","list-style-position","list-style-image","counter-increment","counter-reset",
        "flex-direction","flex-wrap","flex-grow","flex-shrink","flex-basis","justify-content",
        "align-items","align-self","align-content","order","gap","row-gap","column-gap",
        "grid-template-columns","grid-template-rows","grid-column","grid-row","object-fit","object-position",
        "cursor","quotes","content","speak","appearance","direction","unicode-bidi","writing-mode",
        "text-overflow","hyphens","tab-size","filter","backdrop-filter","mix-blend-mode",
    };

    static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }

    static byte[] Encode(List<Page> pages, PageSpec spec, int pw, int ph)
    {
        using var ms = new MemoryStream();
        var meta = new SKDocumentPdfMetadata
        {
            Title = "stageprobe", Creator = "PaperSharp", Producer = "PaperSharp · SkiaSharp",
            RasterDpi = 72, EncodingQuality = 90,
        };
        using (var doc = SKDocument.CreatePdf(ms, meta))
        {
            for (int i = 0; i < pages.Count; i++)
            {
                using var canvas = doc.BeginPage(spec.PdfPageW, spec.PdfPageH);
                canvas.Scale(PageSpec.PtPerPx, PageSpec.PtPerPx);
                PagePainter.PaintPage(canvas, pages[i], spec.PageW, spec.PageH);
                MarginBoxRenderer.Render(canvas, spec, i + 1, pages.Count);
                doc.EndPage();
            }
            doc.Close();
        }
        return ms.ToArray();
    }

    // 与 Renderer.ParsePage 同构（含 html 内嵌 <style> 合并）
    static PageSpec ParsePage(string html, string css, float defW, float defH)
    {
        var ms = Regex.Matches(html, @"<style[^>]*>(.*?)</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (ms.Count == 0) return AtPageParser.Parse(css, defW, defH);
        var sb = new StringBuilder(css);
        foreach (Match m in ms) sb.Append('\n').Append(m.Groups[1].Value);
        return AtPageParser.Parse(sb.ToString(), defW, defH);
    }
}
