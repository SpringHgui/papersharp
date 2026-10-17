using System.Diagnostics;
using System.Reflection;

namespace PdfBench.Runner;

public static class Program
{
    public static int Main(string[] args)
    {
        var specDir = FindSpecDir();
        if (specDir is null)
        {
            Console.Error.WriteLine("找不到 spec/challenge.html（请从仓库内运行）");
            return 2;
        }
        var outDir = Path.Combine(Path.GetDirectoryName(specDir)!, "out");
        Directory.CreateDirectory(outDir);

        // 临时调试子命令：kernprobe — 系统字体的字距从哪来（Skia API / 原始表 / HarfBuzz）
        if (args.Length >= 1 && args[0].Equals("kernprobe", StringComparison.OrdinalIgnoreCase))
            return KernProbe.Run(args);

        // 子命令（规模扫描）：psworker — 常驻 stdio worker，供 scale/sweep.py 做 warm 循环
        if (args.Length >= 1 && args[0].Equals("psworker", StringComparison.OrdinalIgnoreCase))
            return ScaleWorker.Run();

        // 子命令（性能定位）：stageprobe <html> [iters] — 主链路逐项计时 + 录制趟 A/B
        if (args.Length >= 1 && args[0].Equals("stageprobe", StringComparison.OrdinalIgnoreCase))
            return StageProbe.Run(args);

        // 子命令（性能定位）：docsplit <html> [iters] — buildDoc 拆成 HTML解析/CSS解析/层叠匹配
        if (args.Length >= 1 && args[0].Equals("docsplit", StringComparison.OrdinalIgnoreCase))
            return StageProbe.RunDocSplit(args);

        // 子命令（性能定位）：cascadesplit <html> [iters] — 层叠本体 vs 逐属性读的归因
        if (args.Length >= 1 && args[0].Equals("cascadesplit", StringComparison.OrdinalIgnoreCase))
            return StageProbe.RunCascadeSplit(args);

        var all = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IPdfAdapter).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .OrderBy(t => t.Name)
            .Select(t => (IPdfAdapter)Activator.CreateInstance(t)!)
            .ToList();

        var ctx = new AdapterContext(specDir, outDir);

        // 子命令：single <adapter> — 只生成一次即退出（供 coldstart 父进程 spawn 自身计时用）
        if (args.Length > 0 && args[0].Equals("single", StringComparison.OrdinalIgnoreCase))
        {
            var one = args.Length >= 2
                ? all.Find(a => a.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase))
                : null;
            if (one is null)
            {
                Console.Error.WriteLine("用法: single <adapter>。可用: " + string.Join(", ", all.Select(a => a.Name)));
                return 2;
            }
            one.Generate(ctx, Path.Combine(outDir, one.Name + ".pdf"));
            return 0;
        }

        // 子命令（调试）：png [scale] [html路径] — 走 PNG 光栅通路（Painter.cs）出整篇长图，绕开 PDF 与 WinRT 光栅器
        if (args.Length >= 1 && args[0].Equals("png", StringComparison.OrdinalIgnoreCase))
        {
            float scale = args.Length >= 2 ? float.Parse(args[1]) : 2f;
            string htmlPath = args.Length >= 3 ? args[2] : Path.Combine(specDir, "challenge.html");
            var dir = Path.GetDirectoryName(Path.GetFullPath(htmlPath))!;
            var cssPath = Path.ChangeExtension(htmlPath, ".css");
            var css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";
            byte[] png = global::PaperSharp.Renderer.RenderPng(
                File.ReadAllText(htmlPath), css, 794f, baseDir: dir, scale: scale);
            var name = Path.GetFileNameWithoutExtension(htmlPath) + "_png.png";
            File.WriteAllBytes(Path.Combine(outDir, name), png);
            Console.Error.WriteLine($"wrote {name} {png.Length}");
            return 0;
        }

        // 子命令（调试）：cells [html文件名] — 用系统 Chrome 量出每个 feature 选择器在它所在页内的矩形，
        // 写 out/cells.json，供 fidelity/cells.py 做逐特性的像素对账（哪些格子还和 Chrome 不一样）。
        if (args.Length >= 1 && args[0].Equals("cells", StringComparison.OrdinalIgnoreCase))
        {
            return CellsAsync(specDir, outDir, args.Length >= 2 ? args[1] : "challenge.html").GetAwaiter().GetResult();
        }

        // 子命令（调试）：rects [domPath前缀] [html文件名] — 打印引擎本轮布局出的 border-box（body 原点，CSS px）。
        // 与浏览器里 getBoundingClientRect() 同一坐标系，所以能直接和无头 Chrome 的自测脚本对表。
        if (args.Length >= 1 && args[0].Equals("rects", StringComparison.OrdinalIgnoreCase))
        {
            var filter = args.Length >= 2 ? args[1] : null;
            var htmlPath = Path.Combine(specDir, args.Length >= 3 ? args[2] : "challenge.html");
            var cssPath = Path.ChangeExtension(htmlPath, ".css");
            var css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";
            var boxes = global::PaperSharp.Renderer.RenderRasterWithBoxes(
                File.ReadAllText(htmlPath), css, 794f, baseDir: Path.GetDirectoryName(Path.GetFullPath(htmlPath)), scale: 1f).Boxes;
            foreach (var b in boxes)
                if (filter is null || b.DomPath.StartsWith(filter, StringComparison.Ordinal))
                    Console.WriteLine($"{b.DomPath} <{b.Tag}> x={b.X:F2} y={b.Y:F2} w={b.W:F2} h={b.H:F2}");
            return 0;
        }

        // 子命令（调试）：probe [selectorSubstr] — 打印 PaperSharp 层叠后的关键盒属性，定位几何失真用
        if (args.Length >= 1 && args[0].Equals("probe", StringComparison.OrdinalIgnoreCase))
        {
            var filter = args.Length >= 2 ? args[1] : null;
            var html = File.ReadAllText(Path.Combine(specDir, args.Length >= 3 ? args[2] : "challenge.html"));
            var root = global::PaperSharp.Css.StyleResolver.BuildDocument(html, "", viewportWidth: 794f, domPath: true);
            static string Sz(global::PaperSharp.Css.CssSize s) => s.Kind switch
            {
                global::PaperSharp.Css.CssSizeKind.Absolute => $"{s.Value:F1}px",
                global::PaperSharp.Css.CssSizeKind.Percent => $"{s.Value:F1}%",
                global::PaperSharp.Css.CssSizeKind.Calc => $"{s.Value:F1}%+{s.Offset:F1}",
                _ => "auto",
            };
            static string Rad(float ax, float ay, float[]? pct, int i)
            {
                var sb = new System.Text.StringBuilder($"{ax:F0}/{ay:F0}");
                if (pct != null && (pct[i] > 0 || pct[i + 1] > 0)) sb.Append($"%({pct[i] * 100:F0},{pct[i + 1] * 100:F0})");
                return sb.ToString();
            }
            void WalkStyle(global::PaperSharp.Css.ComputedStyle s, int depth)
            {
                var path = s.DomPath ?? s.Tag;
                if (filter is null || path.StartsWith(filter, StringComparison.Ordinal))
                    Console.WriteLine($"{path} | bs={s.BoxSizing} disp={s.Display} w={Sz(s.Width)} h={Sz(s.Height)}" +
                        $" pad={Sz(s.PaddingTopCss)},{Sz(s.PaddingRightCss)},{Sz(s.PaddingBottomCss)},{Sz(s.PaddingLeftCss)}" +
                        $" mar={Sz(s.MarginTopCss)},{Sz(s.MarginBottomCss)} bw={s.BorderWidthLeft:F1}" +
                        $" fs={s.FontSize:F2} lh={s.LineHeightPx():F2} f={s.LineHeightFactor:F4} num={s.LineHeightNumeric} abs={s.LineHeightAbsPx:F2}" +
                        $" st={s.BorderStyleTop}/{s.BorderStyleRight}/{s.BorderStyleBottom}/{s.BorderStyleLeft}" +
                        $" ti={s.TextIndent:F1} ts={(s.TextShadow is { } tsL ? tsL.Count : 0)}" +
                        $" sh={(s.Shadows is { } shL ? string.Join("|", shL.Select(x => $"{x.OffsetX},{x.OffsetY},{x.Blur},{x.Spread},{x.Inset}")) : "-")}" +
                        $" lst={s.ListStyleType}{(s.ListStyleAuthorSet ? "*" : "")}{(s.ListStyleInside ? "/in" : "")}+{s.ListStartOffset}" +
                        $" ra={Rad(s.BorderRadiusTopLeft, s.BorderRadiusTopLeftY, s.BorderRadiusPercent, 0)}" +
                        $",{Rad(s.BorderRadiusTopRight, s.BorderRadiusTopRightY, s.BorderRadiusPercent, 2)}" +
                        $",{Rad(s.BorderRadiusBottomRight, s.BorderRadiusBottomRightY, s.BorderRadiusPercent, 4)}" +
                        $",{Rad(s.BorderRadiusBottomLeft, s.BorderRadiusBottomLeftY, s.BorderRadiusPercent, 6)}" +
                        $" ip={s.InlinePieces.Count} fc={s.IsInlineFC} txt=[{(s.TextContent is { Length: > 0 } t ? t[..Math.Min(8, t.Length)] : "")}]");
                foreach (var c in s.Children) WalkStyle(c, depth + 1);
            }
            WalkStyle(root, 0);
            return 0;
        }

        // 子命令（内部）：measure-one <adapter> <rowPath> — 独立子进程测量单个 adapter，供 measure 调用
        if (args.Length >= 3 && args[0].Equals("measure-one", StringComparison.OrdinalIgnoreCase))
        {
            var one = all.Find(a => a.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase));
            if (one is null) return 2;
            return MeasureRunner.RunOne(one, ctx, outDir, args[2]);
        }

        // 子命令：measure [adapter...] — 全指标性能测量，写 out/results.csv
        if (args.Length > 0 && args[0].Equals("measure", StringComparison.OrdinalIgnoreCase))
        {
            var rest = args.Skip(1).ToArray();
            var targets = rest.Length == 0
                ? all
                : all.Where(a => rest.Any(s => a.Name.Equals(s, StringComparison.OrdinalIgnoreCase))).ToList();
            if (targets.Count == 0)
            {
                Console.Error.WriteLine("无匹配适配器。可用: " + string.Join(", ", all.Select(a => a.Name)));
                return 2;
            }
            return MeasureRunner.Run(targets, ctx, outDir);
        }

        var selected = args.Length == 0
            ? all
            : all.Where(a => args.Any(s => a.Name.Equals(s, StringComparison.OrdinalIgnoreCase))).ToList();

        if (selected.Count == 0)
        {
            Console.Error.WriteLine("无匹配适配器。可用: " + string.Join(", ", all.Select(a => a.Name)));
            return 2;
        }

        int fail = 0;
        Console.WriteLine($"{"adapter",-18} {"status",-10} {"time_ms",9} {"size_kb",9}");
        foreach (var a in selected)
        {
            var path = Path.Combine(outDir, a.Name + ".pdf");
            var sw = Stopwatch.StartNew();
            try
            {
                a.Generate(ctx, path);
                sw.Stop();
                var kb = new FileInfo(path).Length / 1024.0;
                Console.WriteLine($"{a.Name,-18} {"OK",-10} {sw.Elapsed.TotalMilliseconds,9:F0} {kb,9:F1}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                fail++;
                Console.WriteLine($"{a.Name,-18} {"FAIL",-10} {sw.Elapsed.TotalMilliseconds,9:F0}   {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 用系统 Chrome 量出 spec/features.json 里每个特性的矩形（相对它所在 .page 容器），写 out/cells.json。
    /// 视口按 A4 的 794 CSS px 宽、print 媒体，量出来的格子才能和 truth_pN / ps_pN 页图对齐。
    /// </summary>
    private static async Task<int> CellsAsync(string specDir, string outDir, string htmlName)
    {
        var chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        if (!File.Exists(chrome))
        {
            Console.Error.WriteLine("未找到系统 Chrome");
            return 2;
        }
        var features = File.ReadAllText(Path.Combine(specDir, "features.json"));
        using var browser = await PuppeteerSharp.Puppeteer.LaunchAsync(new PuppeteerSharp.LaunchOptions
        {
            Headless = true,
            ExecutablePath = chrome,
        });
        using var page = await browser.NewPageAsync();
        await page.SetViewportAsync(new PuppeteerSharp.ViewPortOptions { Width = 794, Height = 1123, DeviceScaleFactor = 1 });
        await page.EmulateMediaTypeAsync(PuppeteerSharp.Media.MediaType.Print);
        await page.GoToAsync(new Uri(Path.Combine(specDir, htmlName)).AbsoluteUri,
            new PuppeteerSharp.NavigationOptions { WaitUntil = new[] { PuppeteerSharp.WaitUntilNavigation.Networkidle0 } });
        const string Js = @"(featuresJson) => {
  const feats = JSON.parse(featuresJson).features;
  const pages = [...document.querySelectorAll('.page')];
  const out = [];
  for (const f of feats)
    for (const sel of String(f.selector).split(','))
      for (const el of document.querySelectorAll(sel.trim())) {
        const pg = el.closest('.page');
        if (!pg) continue;
        const r = el.getBoundingClientRect(), b = pg.getBoundingClientRect();
        out.push({ id: f.id, group: f.group, page: pages.indexOf(pg) + 1,
                   x: r.left - b.left, y: r.top - b.top, w: r.width, h: r.height });
      }
  return JSON.stringify(out);
}";
        var dumped = await page.EvaluateFunctionAsync<string>("(" + Js + ")", features);
        var target = Path.Combine(outDir, "cells.json");
        File.WriteAllText(target, dumped ?? "[]");
        Console.Error.WriteLine($"wrote cells.json ({(dumped ?? "").Length} chars)");
        await browser.CloseAsync();
        return 0;
    }

    private static string? FindSpecDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "benchmarks", "PdfBench", "spec");
            if (File.Exists(Path.Combine(cand, "challenge.html"))) return cand;
            if (dir.Name == "PdfBench" && File.Exists(Path.Combine(dir.FullName, "spec", "challenge.html")))
                return Path.Combine(dir.FullName, "spec");
            dir = dir.Parent;
        }
        return null;
    }
}
