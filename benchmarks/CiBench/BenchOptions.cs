using System.Globalization;

namespace CiBench;

/// <summary>CLI 参数。所有路径都解析成绝对路径，子进程可直接复用。</summary>
public sealed class BenchOptions
{
    public int Repeats { get; set; } = 20;
    public int[] Batches { get; set; } = [100, 200, 300];
    public int Concurrency { get; set; } = Math.Min(Environment.ProcessorCount, 8);
    public string OutDir { get; set; } = RepoPaths.DefaultOut;
    public string HtmlPath { get; set; } = RepoPaths.DefaultHtml;
    public string[] Engines { get; set; } = ["papersharp", "puppeteer", "playwright", "itext"];
    public bool Strict { get; set; }
    public string? BaselinePath { get; set; }
    public double MaxRegressionPct { get; set; } = 15;
    public string[] GateEngines { get; set; } = ["papersharp"];
    public bool Quick { get; set; }
    /// <summary>无 IRONPDF_LICENSE_KEY 时仍让 IronPdf 以试用模式参赛（报告会标注水印风险）。</summary>
    public bool AllowUnlicensedIronPdf { get; set; }
    /// <summary>不重跑测量，只用已有 out/results.json 重新生成报告（改版式/核对数据时用）。</summary>
    public bool ReportOnly { get; set; }

    /// <summary>透传给子进程的选项（不含编排层关心的 baseline / strict / out）。</summary>
    public string[] ToChildArgs()
    {
        return
        [
            "--repeats", Repeats.ToString(CultureInfo.InvariantCulture),
            "--batches", string.Join(',', Batches),
            "--concurrency", Concurrency.ToString(CultureInfo.InvariantCulture),
            "--html", HtmlPath,
            "--out", OutDir,
        ];
    }

    public static BenchOptions Parse(string[] args)
    {
        var o = new BenchOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next(string flag) => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{flag} 缺少参数值");

            switch (a.ToLowerInvariant())
            {
                case "--repeats": o.Repeats = Math.Max(1, int.Parse(Next(a), CultureInfo.InvariantCulture)); break;
                case "--batches": o.Batches = Next(a).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(int.Parse).Where(x => x > 0).Distinct().OrderBy(x => x).ToArray(); break;
                case "--concurrency": o.Concurrency = Math.Max(1, int.Parse(Next(a), CultureInfo.InvariantCulture)); break;
                case "--out": o.OutDir = Path.GetFullPath(Next(a)); break;
                case "--html": o.HtmlPath = Path.GetFullPath(Next(a)); break;
                case "--engines": o.Engines = Next(a).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => x.ToLowerInvariant()).ToArray(); break;
                case "--baseline": o.BaselinePath = Path.GetFullPath(Next(a)); break;
                case "--max-regression": o.MaxRegressionPct = double.Parse(Next(a), CultureInfo.InvariantCulture); break;
                case "--gate-engines": o.GateEngines = Next(a).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => x.ToLowerInvariant()).ToArray(); break;
                case "--strict": o.Strict = true; break;
                case "--quick": o.Quick = true; break;
                case "--allow-unlicensed-ironpdf": o.AllowUnlicensedIronPdf = true; break;
                case "--report-only": o.ReportOnly = true; break;
                case "--help" or "-h": throw new HelpRequestedException();
                default: throw new ArgumentException($"未知参数: {a}");
            }
        }

        if (o.Quick)
        {
            o.Repeats = 5;
            o.Batches = [100, 200];
            o.Concurrency = Math.Min(o.Concurrency, 4);
        }
        if (o.Batches.Length == 0) o.Batches = [100, 200, 300];
        if (o.Engines.Length == 0) o.Engines = ["papersharp", "puppeteer", "playwright", "itext"];
        return o;
    }

    public static string HelpText => """
        CiBench — PaperSharp / PuppeteerSharp / Microsoft.Playwright / iText 7 pdfHTML
                  四家可进 CI 的 PDF 性能基准

        用法:
          dotnet run --project benchmarks/CiBench/CiBench.csproj -- [options]
          CiBench.exe [options]

        选项:
          --repeats N          冷/热单张导出重复次数，取平均（默认 20）
          --batches A,B,C      并发批次的文档数（默认 100,200,300）
          --concurrency N      固定并行 worker 数（默认 min(CPU, 8)）
          --html PATH          语料 HTML（默认 spec/invoice.html）
          --out DIR            产物目录（默认 benchmarks/CiBench/out）
          --engines A,B        只跑指定引擎（默认 papersharp,puppeteer,playwright,itext；
                               可选 ironpdf 需 -p:IncludeIronPdf=true 重编译并配 license）
          --quick              快速档：5 次重复 + 100,200 批次，供 PR 使用
          --allow-unlicensed-ironpdf
                               仅对可选引擎 ironpdf 生效；无 license 时仍尝试测
                               （多数版本抛 LicensingException，此时如实记为 failed）
          --report-only         不重跑测量，用已有 out/results.json 重出报告
          --strict             任一引擎 skipped/failed 时退出码非 0
          --baseline PATH      与上次 results.json 对比，做回归门禁
          --max-regression P   允许的劣化百分比（默认 15）
          --gate-engines A,B   门禁只对这些引擎生效（默认 papersharp）
          -h / --help          显示本帮助

        内部子命令（由编排进程 spawn，勿手动调用）:
          cold-one <engine> <html> <outPdf>      全新进程导出一张 PDF
          worker-one <engine> <jsonOut> [opts]   热态测量（单张 + 并发批次）

        授权提醒:
          iText 7 pdfHTML 为 AGPLv3 / 商业双授权（免费可用但有 copyleft 义务），
          仅在本基准工具中作为被测依赖；闭源分发或网络服务使用需评估 AGPL 或购买商业授权。
          可用 -p:IncludeIText=false 重编译彻底移除该依赖。

        环境变量:
          PUPPETEER_EXECUTABLE_PATH  Chrome/Chromium 路径，PuppeteerSharp 与 Playwright 共用
          IRONPDF_LICENSE_KEY      仅可选引擎 IronPdf 使用；缺失时该库标记为 skipped
          CIBENCH_ROOT             仓库根目录（自动探测失败时用）
          CIBENCH_ALLOW_UNLICENSED_IRONPDF  置 1 等价于 --allow-unlicensed-ironpdf（供子进程继承）
        """;
}

public sealed class HelpRequestedException : Exception;

public static class RepoPaths
{
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CIBENCH_ROOT");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return Path.GetFullPath(env);
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "benchmarks", "CiBench", "CiBench.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }

    public static string DefaultHtml => Path.Combine(Root, "benchmarks", "CiBench", "spec", "invoice.html");
    public static string DefaultOut => Path.Combine(Root, "benchmarks", "CiBench", "out");
}
