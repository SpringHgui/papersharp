using System.Diagnostics;
using System.Text.Json;
using iText.Kernel.Pdf;

namespace PdfBench.Runner;

/// <summary>
/// 规模扫描（scale/sweep.py）用的常驻 worker：stdin 一行一个 JSON job，stdout 一行一个 JSON 结果。
///
/// 为什么不是「一进程一调」：驱动要让**所有引擎走同一条测量路径**才敢横向比。
/// 常驻 = warm 口径（JIT、字体缓存、Skia 上下文只付一次）；cold 口径由驱动重启本进程得到，
/// 不需要 worker 自己实现生命周期开关。
///
/// 渲染入口与 Adapters/PaperSharpAdapter.cs 逐项一致（RenderPdf + PageSize.A4 + baseDir），
/// 换入口=换口径，历史数字作废。
/// </summary>
public static class ScaleWorker
{
    record Job(int Id, string Cmd, string? Engine, string? Html, string? Out);
    record Result(int Id, bool Ok, double? Ms, long? Bytes, int? Pages, string? Err);

    public static int Run()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = false };
        Console.SetOut(stdout);

        string ver;
        try { ver = typeof(global::PaperSharp.Renderer).Assembly.GetName().Version?.ToString() ?? "?"; }
        catch { ver = "?"; }

        while (true)
        {
            var line = Console.In.ReadLine();
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            Job? job;
            try
            {
                job = JsonSerializer.Deserialize<Job>(line, new JsonSerializerOptions
                { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Emit(new Result(-9, false, null, null, null, "bad json: " + ex.Message));
                continue;
            }
            if (job is null) continue;

            if (job.Cmd == "quit") break;
            if (job.Cmd == "stat")
            {
                Console.Error.WriteLine($"papersharp clr {Environment.Version} asm {ver}");
                Console.Out.Flush();
                Emit(new Result(job.Id, true, null, null, null, null));
                continue;
            }
            if (job.Cmd != "gen")
            {
                Emit(new Result(job.Id, false, null, null, null, $"未知 cmd {job.Cmd}"));
                continue;
            }

            try
            {
                var html = File.ReadAllText(job.Html!);
                var baseDir = Path.GetDirectoryName(Path.GetFullPath(job.Html!));
                var sw = Stopwatch.StartNew();
                var pdf = global::PaperSharp.Renderer.RenderPdf(html, "", global::PaperSharp.PageSize.A4, baseDir: baseDir);
                sw.Stop();
                File.WriteAllBytes(job.Out!, pdf);
                int pages;
                try
                {
                    using var doc = new PdfDocument(new PdfReader(job.Out!));
                    pages = doc.GetNumberOfPages();
                }
                catch { pages = -1; }
                Emit(new Result(job.Id, true, sw.Elapsed.TotalMilliseconds, pdf.LongLength, pages, null));
            }
            catch (Exception ex)
            {
                Emit(new Result(job.Id, false, null, null, null,
                    $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}"));
            }
        }
        return 0;
    }

    static void Emit(Result r)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(r, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.Out.Flush();
    }
}
