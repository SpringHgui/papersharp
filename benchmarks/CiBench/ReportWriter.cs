using System.Globalization;
using System.Net;
using System.Text;

namespace CiBench;

public static class ReportWriter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Palette = ["#6c8cff", "#f0a35e", "#5ec9a5", "#d679c8", "#e8c46a"];

    const string IronPdfHint =
        "可选引擎 IronPdf 未参赛：它是商业库，需到 ironpdf.com 领取免费 30 天试用授权" +
        "（无需信用卡，key 发到邮箱），设置环境变量 IRONPDF_LICENSE_KEY 后重跑；CI 里配成同名 Repository Secret。" +
        "默认四家（PaperSharp / PuppeteerSharp / Microsoft.Playwright / iText 7 pdfHTML）都不需要任何 key，" +
        "所以常规报告不受影响。无授权时 IronPdf 会抛 LicensingException: Production License Required，" +
        "基准绝不拿试用/水印产物凑数。";

    const string ITextLicenseHint =
        "授权提醒：iText 7 Core + pdfHTML 是 AGPLv3 / 商业双授权，免费可用但带 copyleft 义务。" +
        "本仓库只把它当基准工具的被测依赖；若要在闭源产品分发或网络服务（SaaS）中使用，" +
        "须遵守 AGPL 开源义务或购买商业授权。不想引入该依赖时用 -p:IncludeIText=false 重编译即可移除；" +
        "同时它是解析型布局引擎，CSS 支持面比 Chromium 窄，本报告的耗时数字不代表视觉还原度。";

    static bool NeedsIronPdfHint(BenchReport r) => r.Engines.Any(e =>
        e.Name == "ironpdf" && e.Status == "skipped" &&
        (e.SkipReason ?? "").Contains("IRONPDF_LICENSE_KEY", StringComparison.OrdinalIgnoreCase));

    static bool NeedsITextHint(BenchReport r) => r.Engines.Any(e => e.Name == "itext" && e.IsOk);

    public static void WriteAll(BenchOptions o, BenchReport r)
    {
        Directory.CreateDirectory(o.OutDir);
        // 权威最终版：BenchRunner 的增量 results.json 只是崩溃兜底，这里覆盖成完整报告数据
        Save("results.json", JsonIo.Serialize(r));
        Save("report.html", BuildHtml(r));
        Save("results.csv", BuildCsv(r));
        Save("summary.md", BuildMarkdown(r));

        // 某个产物被编辑器/Preview 独占打开时不要让整轮报告失败：重试后仍占用就跳过并明确告警，
        // 其余文件照常刷新。关掉占用者后执行 --report-only 即可补齐。
        void Save(string name, string content)
        {
            string path = Path.Combine(o.OutDir, name);
            for (int i = 0; i < 4; i++)
            {
                try { File.WriteAllText(path, content, new UTF8Encoding(false)); return; }
                catch (Exception ex) when (i < 3 && (ex is IOException or UnauthorizedAccessException))
                {
                    Thread.Sleep(200);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"[warn] {name} 被其他进程占用，本次跳过写入（{ex.GetType().Name}）；" +
                                            "关闭打开该文件的程序后执行 --report-only 可刷新");
                    return;
                }
            }
        }
    }

    // ---------------- HTML ----------------

    static string BuildHtml(BenchReport r)
    {
        var engines = r.Engines;
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine($"<title>CiBench · {E(r.GeneratedAtUtc[..10])} PDF 性能基准</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css);
        sb.AppendLine("</style></head><body><div class=\"wrap\">");

        sb.AppendLine("<header>");
        sb.AppendLine($"<h1>CiBench · {engines.Count} 家 PDF 生成性能基准</h1>");
        sb.AppendLine($"<p class=\"sub\">{E(string.Join(" · ", engines.Select(x => x.Display)))} &nbsp;|&nbsp; 生成于 {E(r.GeneratedAtUtc)} &nbsp;|&nbsp; " +
            $"重复 {r.Config.Repeats} 次 · 并发档 [{string.Join(", ", r.Config.Batches)}] · 固定并发 {r.Config.Concurrency} " +
            $"· {(r.Config.Quick ? "quick" : "full")}</p>");
        sb.AppendLine($"<p class=\"env\">{E(r.Env.Os)} · {E(r.Env.Arch)} · {r.Env.LogicalCores} 核 · .NET {E(r.Env.Dotnet)} " +
            $"· git {E(r.Env.GitRev)} · 语料 {r.Config.HtmlBytes} B (sha {E(r.Config.HtmlSha256)})</p>");
        sb.AppendLine("</header>");

        // 概览卡片
        sb.AppendLine("<section class=\"cards\">");
        for (int i = 0; i < engines.Count; i++)
        {
            var e = engines[i];
            string badge = e.Status switch
            {
                "ok" => "<span class=\"badge ok\">OK</span>",
                "skipped" => "<span class=\"badge skip\">SKIPPED</span>",
                _ => "<span class=\"badge fail\">FAILED</span>",
            };
            string color = Palette[i % Palette.Length];
            sb.AppendLine($"<div class=\"card\" style=\"--accent:{color}\">");
            sb.AppendLine($"  <div class=\"card-h\"><span class=\"dot\"></span><b>{E(e.Display)}</b>{badge}</div>");
            sb.AppendLine($"  <div class=\"card-sub\">{E(e.RenderingEngine)} · v{E(e.Version)}</div>");
            if (!string.IsNullOrEmpty(e.Warning))
                sb.AppendLine($"  <div class=\"warn\">{E(e.Warning)}</div>");
            if (e.IsOk)
            {
                double ratio = (e.Warm?.MeanMs ?? 0) > 0 ? (e.Cold?.MeanMs ?? 0) / e.Warm!.MeanMs : double.NaN;
                sb.AppendLine($"  <div class=\"kv\"><span>冷启动均值</span><strong>{V(e.Cold?.MeanMs, "0")} ms</strong></div>");
                sb.AppendLine($"  <div class=\"kv\"><span>热启动均值</span><strong>{V(e.Warm?.MeanMs, "0")} ms</strong></div>");
                sb.AppendLine($"  <div class=\"kv\"><span>冷 / 热加速比</span><strong>{V(ratio, "0.0")}×</strong></div>");
                sb.AppendLine($"  <div class=\"kv\"><span>冷启动 RSS 峰值</span><strong>{V(e.Cold?.RssPeakMbMean, "0")} MB</strong></div>");
            }
            else
            {
                sb.AppendLine($"  <div class=\"reason\">{E(e.SkipReason ?? e.Error ?? "无可用样本")}</div>");
            }
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</section>");

        // 图表
        sb.AppendLine("<h2>性能图表</h2>");
        sb.AppendLine("<section class=\"charts\">");
        // 冷热量级差一个数量级，画在同一张图里热启动柱会被压扁，拆成两张各自独立定标
        sb.Append(EngineBarBlock(r, $"冷启动 · 导出一张 PDF 平均耗时（{r.Config.Repeats} 次取平均）", "ms",
            e => e.Cold?.MeanMs));
        sb.Append(EngineBarBlock(r, $"热启动 · 导出一张 PDF 平均耗时（{r.Config.Repeats} 次取平均）", "ms",
            e => e.Warm?.MeanMs));
        sb.Append(ChartBlock(r, "并发批次 · 平均每张耗时（总墙钟 / 文档数）", "ms/doc",
            BatchCats(r), e => BatchVals(r, e, b => b.AvgMsPerDoc)));
        sb.Append(ChartBlock(r, "并发批次 · 进程树 RSS 峰值", "MB",
            BatchCats(r), e => BatchVals(r, e, b => b.RssPeakMb)));
        sb.Append(ChartBlock(r, "并发批次 · 吞吐", "docs/s",
            BatchCats(r), e => BatchVals(r, e, b => b.ThroughputDps)));
        sb.Append(ChartBlock(r, "并发批次 · 单张延迟 P95", "ms",
            BatchCats(r), e => BatchVals(r, e, b => b.LatencyP95Ms)));
        sb.Append(ChartBlock(r, "并发批次 · 托管内存分配总量", "MB",
            BatchCats(r), e => BatchVals(r, e, b => b.AllocMbTotal)));
        sb.Append(ChartBlock(r, "并发批次 · 批次总墙钟", "s",
            BatchCats(r), e => BatchVals(r, e, b => b.WallMs / 1000.0)));
        sb.AppendLine("</section>");

        // 核心指标表
        sb.AppendLine("<h2>全指标明细</h2>");
        sb.AppendLine("<div class=\"table-wrap\"><table>");
        sb.Append("<thead><tr><th>指标</th><th>单位</th>");
        foreach (var e in engines) sb.Append($"<th>{E(e.Display)}</th>");
        sb.AppendLine("</tr></thead><tbody>");
        Row(sb, engines, "冷启动 · 平均耗时", "ms", e => e.Cold?.MeanMs, "0.0");
        Row(sb, engines, "冷启动 · P50", "ms", e => e.Cold?.P50Ms, "0.0");
        Row(sb, engines, "冷启动 · P95", "ms", e => e.Cold?.P95Ms, "0.0");
        Row(sb, engines, "冷启动 · 标准差", "ms", e => e.Cold?.StdMs, "0.0");
        Row(sb, engines, "冷启动 · 最慢一次", "ms", e => e.Cold?.MaxMs, "0.0");
        Row(sb, engines, "冷启动 · 成功 / 重复", "count", e => e.Cold?.Success, "0");
        Row(sb, engines, "冷启动 · 进程树 RSS 峰值均值", "MB", e => e.Cold?.RssPeakMbMean, "0");
        Row(sb, engines, "冷启动 · 产物体积", "KB", e => e.Cold?.SizeKbMean, "0.0");
        Row(sb, engines, "热启动 · 平均耗时", "ms", e => e.Warm?.MeanMs, "0.0");
        Row(sb, engines, "热启动 · P50", "ms", e => e.Warm?.P50Ms, "0.0");
        Row(sb, engines, "热启动 · P95", "ms", e => e.Warm?.P95Ms, "0.0");
        Row(sb, engines, "热启动 · 标准差", "ms", e => e.Warm?.StdMs, "0.0");
        Row(sb, engines, "热启动 · 托管分配 / 张", "KB", e => e.Warm?.AllocKbPerDoc, "0.0");
        Row(sb, engines, "热启动 · 进程树 RSS 峰值", "MB", e => e.Warm?.RssPeakMb, "0");
        Row(sb, engines, "热启动 · GC gen0", "count", e => e.Warm?.Gen0, "0");
        Row(sb, engines, "热启动 · GC gen1", "count", e => e.Warm?.Gen1, "0");
        Row(sb, engines, "热启动 · GC gen2", "count", e => e.Warm?.Gen2, "0");
        Row(sb, engines, "热启动 · 产物体积", "KB", e => e.Warm?.SizeKb, "0.0");
        sb.AppendLine("</tbody></table></div>");

        foreach (int n in r.Config.Batches)
        {
            sb.AppendLine($"<h3>并发批次 · {n} 张（固定并发 {r.Config.Concurrency}）</h3>");
            sb.AppendLine("<div class=\"table-wrap\"><table>");
            sb.Append("<thead><tr><th>指标</th><th>单位</th>");
            foreach (var e in engines) sb.Append($"<th>{E(e.Display)}</th>");
            sb.AppendLine("</tr></thead><tbody>");
            Func<EngineResult, BatchResult?> get = e => e.Batches.FirstOrDefault(b => b.Documents == n);
            Row(sb, engines, "批次总墙钟", "s", e => get(e)?.WallMs / 1000.0, "0.00");
            Row(sb, engines, "平均耗时 / 张", "ms", e => get(e)?.AvgMsPerDoc, "0.0");
            Row(sb, engines, "吞吐", "docs/s", e => get(e)?.ThroughputDps, "0.00");
            Row(sb, engines, "单张延迟 P50", "ms", e => get(e)?.LatencyP50Ms, "0.0");
            Row(sb, engines, "单张延迟 P95", "ms", e => get(e)?.LatencyP95Ms, "0.0");
            Row(sb, engines, "单张延迟最大", "ms", e => get(e)?.LatencyMaxMs, "0.0");
            Row(sb, engines, "进程树 RSS 峰值", "MB", e => get(e)?.RssPeakMb, "0");
            Row(sb, engines, "进程树 RSS 结束值", "MB", e => get(e)?.RssEndMb, "0");
            Row(sb, engines, "托管内存分配总量", "MB", e => get(e)?.AllocMbTotal, "0.0");
            Row(sb, engines, "GC (gen0)", "count", e => get(e)?.Gen0, "0");
            Row(sb, engines, "成功 / 总数", "count", e => get(e)?.Success, "0");
            Row(sb, engines, "失败数", "count", e => get(e)?.Failed, "0");
            Row(sb, engines, "平均产物体积", "KB", e => get(e)?.SizeKbAvg, "0.0");
            sb.AppendLine("</tbody></table></div>");
        }

        // 状态与口径
        sb.AppendLine("<h2>状态与口径</h2>");
        sb.AppendLine("<div class=\"table-wrap\"><table><thead><tr><th>引擎</th><th>状态</th><th>渲染内核</th><th>版本</th><th>说明</th></tr></thead><tbody>");
        foreach (var e in engines)
        {
            string why = e.Status == "ok" ? (e.Warning ?? "—") : (e.SkipReason ?? e.Error ?? "");
            sb.AppendLine($"<tr><td>{E(e.Display)}</td><td>{E(e.Status)}</td><td>{E(e.RenderingEngine)}</td>" +
                          $"<td>{E(e.Version)}</td><td class=\"why\">{E(why)}</td></tr>");
        }
        sb.AppendLine("</tbody></table></div>");
        sb.AppendLine("<ol class=\"notes\">");
        foreach (var n in r.Notes) sb.AppendLine($"<li>{E(n)}</li>");
        if (NeedsITextHint(r))
            sb.AppendLine($"<li class=\"hint\">{E(ITextLicenseHint)}</li>");
        if (NeedsIronPdfHint(r))
            sb.AppendLine($"<li class=\"hint\">{E(IronPdfHint)}</li>");
        sb.AppendLine("</ol>");
        sb.AppendLine("<footer>CiBench · 自包含报告，无外部脚本 / 字体 / 网络依赖</footer>");
        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>单指标跨引擎：每家一根柱、颜色与概览卡片一致，y 轴按本指标独立定标。</summary>
    static string EngineBarBlock(BenchReport r, string title, string unit, Func<EngineResult, double?> get)
    {
        string[] cats = r.Engines.Select(e => e.Display).ToArray();
        double?[] values = r.Engines.Select(e => e.IsOk ? get(e) : (double?)null).ToArray();
        string[] colors = Enumerable.Range(0, r.Engines.Count)
            .Select(i => Palette[i % Palette.Length]).ToArray();
        var series = new List<ChartSeries> { new("平均耗时", Palette[0], values, colors) };
        return $"<div class=\"chart-card\">{Charts.GroupedBars(title, unit, cats, series)}</div>";
    }

    static string ChartBlock(BenchReport r, string title, string unit, string[] cats,
        Func<EngineResult, double?[]> values)
    {
        var series = new List<ChartSeries>();
        for (int i = 0; i < r.Engines.Count; i++)
        {
            var e = r.Engines[i];
            series.Add(new ChartSeries(e.Display, Palette[i % Palette.Length], e.IsOk ? values(e) : [.. cats.Select(_ => (double?)null)]));
        }
        return $"<div class=\"chart-card\">{Charts.GroupedBars(title, unit, cats, series)}</div>";
    }

    static string[] BatchCats(BenchReport r) => r.Config.Batches.Select(n => $"{n} 张").ToArray();

    static double?[] BatchVals(BenchReport r, EngineResult e, Func<BatchResult, double> get) =>
        r.Config.Batches.Select(n =>
        {
            var b = e.Batches.FirstOrDefault(x => x.Documents == n);
            return b is null ? (double?)null : get(b);
        }).ToArray();

    static void Row(StringBuilder sb, IReadOnlyList<EngineResult> engines, string label, string unit,
        Func<EngineResult, double?> get, string fmt)
    {
        sb.Append($"<tr><td>{E(label)}</td><td class=\"unit\">{E(unit)}</td>");
        foreach (var e in engines)
            sb.Append($"<td>{V(get(e), fmt)}</td>");
        sb.AppendLine("</tr>");
    }

    static string V(double? v, string fmt) =>
        v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)
            ? "<span class=\"na\">N/A</span>"
            : v.Value.ToString(fmt, Inv);

    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    const string Css = """
        :root { color-scheme: dark; --bg:#0d1117; --panel:#161b22; --line:#30363d; --text:#e6edf3; --muted:#9aa4b2; }
        * { box-sizing:border-box; }
        body { margin:0; background:var(--bg); color:var(--text);
               font-family:-apple-system,BlinkMacSystemFont,"Segoe UI","Microsoft YaHei",sans-serif;
               font-size:14px; line-height:1.6; }
        .wrap { max-width:1180px; margin:0 auto; padding:32px 24px 64px; }
        header { border-bottom:1px solid var(--line); padding-bottom:20px; margin-bottom:28px; }
        h1 { font-size:26px; margin:0 0 8px; font-weight:600; }
        h2 { font-size:19px; margin:36px 0 16px; font-weight:600; }
        h3 { font-size:15px; margin:26px 0 10px; font-weight:600; color:var(--muted); }
        .sub { color:var(--muted); margin:0 0 6px; }
        .env { color:#7d8590; font-size:12px; margin:0; font-family:ui-monospace,Consolas,monospace; }
        .cards { display:grid; grid-template-columns:repeat(auto-fit,minmax(240px,1fr)); gap:16px; }
        .card { background:var(--panel); border:1px solid var(--line); border-left:3px solid var(--accent);
                border-radius:10px; padding:16px 18px; }
        .card-h { display:flex; align-items:center; gap:9px; font-size:16px; }
        .dot { width:9px; height:9px; border-radius:50%; background:var(--accent); display:inline-block; }
        .badge { margin-left:auto; font-size:10px; padding:2px 8px; border-radius:99px; letter-spacing:.5px; }
        .badge.ok { background:#123c2c; color:#5ec9a5; }
        .badge.skip { background:#3a3320; color:#e8c46a; }
        .badge.fail { background:#3d2020; color:#f08a8a; }
        .card-sub { color:var(--muted); font-size:12px; margin:6px 0 14px; }
        .kv { display:flex; justify-content:space-between; padding:4px 0; border-top:1px solid #21262d; }
        .kv span { color:var(--muted); }
        .kv strong { font-variant-numeric:tabular-nums; }
        .reason { color:#e8c46a; font-size:12px; line-height:1.6; overflow-wrap:anywhere; word-break:break-word; }
        .warn { color:#e8c46a; font-size:12px; margin:-4px 0 10px; line-height:1.6; overflow-wrap:anywhere; }
        .charts { display:grid; grid-template-columns:repeat(auto-fit,minmax(440px,1fr)); gap:16px; }
        .chart-card { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:12px; }
        .chart { display:block; }
        .chart-title { fill:#e6edf3; font-size:14px; font-weight:500; }
        .chart-unit { fill:#7d8590; font-size:11px; }
        .axis { fill:#8b949e; font-size:11px; }
        .cat { fill:#c9d1d9; font-size:12px; }
        .legend { fill:#c9d1d9; font-size:12px; }
        .value { fill:#e6edf3; font-size:11px; font-variant-numeric:tabular-nums; }
        .na-bar { fill:#30363d; }
        .na-label { fill:#7d8590; font-size:10px; }
        .grid { stroke:#262d36; stroke-width:1; }
        .axis-line { stroke:#484f58; stroke-width:1; }
        .table-wrap { overflow-x:auto; border:1px solid var(--line); border-radius:10px; margin-bottom:14px; }
        table { border-collapse:collapse; width:100%; font-size:13px; }
        th, td { padding:8px 12px; border-bottom:1px solid #21262d; text-align:right; white-space:nowrap; }
        th:first-child, td:first-child { text-align:left; }
        thead th { background:#1c2128; color:var(--muted); font-weight:500; position:sticky; top:0; }
        tbody tr:hover { background:#1c2128; }
        td { font-variant-numeric:tabular-nums; }
        .unit { color:#7d8590; text-align:left !important; }
        .na { color:#6e7681; }
        .why { text-align:left !important; white-space:normal; color:#d2a8ff; max-width:420px; }
        .notes { color:var(--muted); font-size:13px; padding-left:20px; }
        .notes li { margin:6px 0; }
        .notes li.hint { color:#e8c46a; }
        footer { margin-top:40px; padding-top:16px; border-top:1px solid var(--line); color:#6e7681; font-size:12px; }
        """;

    // ---------------- CSV ----------------

    static string BuildCsv(BenchReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("engine,phase,batch,metric,value,unit,status");
        foreach (var e in r.Engines)
        {
            string p = e.Status;
            void M(string phase, string batch, string metric, double? value, string unit) =>
                sb.AppendLine($"{Csv(e.Name)},{phase},{batch},{metric},{(value is null || double.IsNaN(value.Value) ? "" : value.Value.ToString("0.###", Inv))},{unit},{p}");

            M("cold", "", "mean_ms", e.Cold?.MeanMs, "ms");
            M("cold", "", "p50_ms", e.Cold?.P50Ms, "ms");
            M("cold", "", "p95_ms", e.Cold?.P95Ms, "ms");
            M("cold", "", "std_ms", e.Cold?.StdMs, "ms");
            M("cold", "", "max_ms", e.Cold?.MaxMs, "ms");
            M("cold", "", "success", e.Cold?.Success, "count");
            M("cold", "", "rss_peak_mb_mean", e.Cold?.RssPeakMbMean, "MB");
            M("cold", "", "size_kb_mean", e.Cold?.SizeKbMean, "KB");
            M("warm", "", "mean_ms", e.Warm?.MeanMs, "ms");
            M("warm", "", "p50_ms", e.Warm?.P50Ms, "ms");
            M("warm", "", "p95_ms", e.Warm?.P95Ms, "ms");
            M("warm", "", "std_ms", e.Warm?.StdMs, "ms");
            M("warm", "", "alloc_kb_per_doc", e.Warm?.AllocKbPerDoc, "KB");
            M("warm", "", "rss_peak_mb", e.Warm?.RssPeakMb, "MB");
            M("warm", "", "size_kb", e.Warm?.SizeKb, "KB");
            // 每档的指标 schema 对所有引擎保持一致：skipped/failed 的引擎也留下空值行，
            // status 列如实标记，避免长表里「整档消失」被误读成漏跑。
            void EmitBatch(string n, BatchResult? b)
            {
                M("batch", n, "wall_ms", b?.WallMs, "ms");
                M("batch", n, "avg_ms_per_doc", b?.AvgMsPerDoc, "ms");
                M("batch", n, "throughput_dps", b?.ThroughputDps, "docs/s");
                M("batch", n, "latency_p50_ms", b?.LatencyP50Ms, "ms");
                M("batch", n, "latency_p95_ms", b?.LatencyP95Ms, "ms");
                M("batch", n, "latency_max_ms", b?.LatencyMaxMs, "ms");
                M("batch", n, "rss_peak_mb", b?.RssPeakMb, "MB");
                M("batch", n, "rss_end_mb", b?.RssEndMb, "MB");
                M("batch", n, "alloc_mb_total", b?.AllocMbTotal, "MB");
                M("batch", n, "gen0", b?.Gen0, "count");
                M("batch", n, "success", b?.Success, "count");
                M("batch", n, "failed", b?.Failed, "count");
                M("batch", n, "size_kb_avg", b?.SizeKbAvg, "KB");
            }

            if (e.Batches.Count > 0)
                foreach (var b in e.Batches) EmitBatch(b.Documents.ToString(Inv), b);
            else
                foreach (int n in r.Config.Batches) EmitBatch(n.ToString(Inv), null);
        }
        return sb.ToString();
    }

    static string Csv(string s) => s.Contains(',') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ---------------- Markdown ----------------

    static string BuildMarkdown(BenchReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## CiBench · {r.Engines.Count} 家 PDF 生成性能基准");
        sb.AppendLine();
        sb.AppendLine($"`{r.GeneratedAtUtc}` · 重复 {r.Config.Repeats} 次 · 并发档 {string.Join("/", r.Config.Batches)} · " +
                      $"固定并发 {r.Config.Concurrency} · .NET {r.Env.Dotnet} · {r.Env.LogicalCores} 核");
        sb.AppendLine();
        sb.AppendLine("| 引擎 | 状态 | 冷启动均值 (ms) | 热启动均值 (ms) | 冷/热 | 冷启动 RSS (MB) |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var e in r.Engines)
        {
            double ratio = (e.Warm?.MeanMs ?? 0) > 0 ? (e.Cold?.MeanMs ?? 0) / e.Warm!.MeanMs : double.NaN;
            string ratioText = double.IsNaN(ratio) ? "—" : Md(ratio, "0.0") + "×";
            sb.AppendLine($"| {e.Display} | {e.Status} | {Md(e.Cold?.MeanMs)} | {Md(e.Warm?.MeanMs)} | " +
                          $"{ratioText} | {Md(e.Cold?.RssPeakMbMean)} |");
        }
        sb.AppendLine();
        foreach (int n in r.Config.Batches)
        {
            sb.AppendLine($"### 并发 {n} 张");
            sb.AppendLine();
            sb.AppendLine("| 引擎 | 总墙钟 (s) | 平均/张 (ms) | 吞吐 (docs/s) | P95 (ms) | RSS 峰值 (MB) | 成功 |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            foreach (var e in r.Engines)
            {
                var b = e.Batches.FirstOrDefault(x => x.Documents == n);
                sb.AppendLine($"| {e.Display} | {Md(b?.WallMs / 1000.0, "0.00")} | {Md(b?.AvgMsPerDoc)} | " +
                              $"{Md(b?.ThroughputDps, "0.00")} | {Md(b?.LatencyP95Ms)} | {Md(b?.RssPeakMb)} | " +
                              $"{(b is null ? "—" : $"{b.Success}/{n}")} |");
            }
            sb.AppendLine();
        }
        foreach (var e in r.Engines.Where(x => !x.IsOk))
            sb.AppendLine($"> **{e.Display}** 未参与：`{e.SkipReason ?? e.Error}`");
        foreach (var e in r.Engines.Where(x => x.IsOk && !string.IsNullOrEmpty(x.Warning)))
            sb.AppendLine($"> **{e.Display}** 提醒：{e.Warning}");
        if (NeedsITextHint(r)) sb.AppendLine($"> ⚠️ {ITextLicenseHint}");
        if (NeedsIronPdfHint(r)) sb.AppendLine($"> {IronPdfHint}");
        sb.AppendLine();
        sb.AppendLine("口径：冷启动=每次全新子进程；热启动=常驻进程预热后连续单张；批次=固定并发 worker 消费 N 张队列，" +
                      "平均耗时=总墙钟/N；RSS=进程树峰值（含 Chrome 子进程）。");
        return sb.ToString();
    }

    static string Md(double? v, string fmt = "0.0") =>
        v is null || double.IsNaN(v.Value) ? "—" : v.Value.ToString(fmt, Inv);
}

public static class SummaryWriter
{
    /// <summary>把 summary.md 追加到 GitHub Actions 的 job summary，CI 页面上直接可见。</summary>
    public static void AppendToGithubStepSummary(BenchOptions o)
    {
        var target = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            string md = File.ReadAllText(Path.Combine(o.OutDir, "summary.md"));
            File.AppendAllText(target, "\n\n" + md, new UTF8Encoding(false));
            Console.WriteLine("已写入 GITHUB_STEP_SUMMARY");
        }
        catch (Exception ex) { Console.Error.WriteLine($"写 job summary 失败: {ex.Message}"); }
    }
}
