namespace CiBench;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        try
        {
            // 内部子命令：由编排进程 spawn，参数里含路径，必须优先于常规选项解析
            if (args.Length > 0 && args[0].Equals("cold-one", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 4) { Console.Error.WriteLine("用法: cold-one <engine> <html> <outPdf>"); return 2; }
                return await WorkerRunner.ColdOneAsync(args[1], args[2], args[3]);
            }

            if (args.Length > 0 && args[0].Equals("worker-one", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 3) { Console.Error.WriteLine("用法: worker-one <engine> <jsonOut> [options]"); return 2; }
                var childOpts = BenchOptions.Parse(args.Skip(3).ToArray());
                ApplyRuntime(childOpts);
                return await WorkerRunner.WorkerOneAsync(args[1], args[2], childOpts);
            }

            var options = BenchOptions.Parse(args);
            ApplyRuntime(options);

            if (options.ReportOnly)
            {
                string existing = Path.Combine(options.OutDir, "results.json");
                if (!File.Exists(existing)) { Console.Error.WriteLine($"找不到 {existing}"); return 2; }
                var previous = JsonIo.Deserialize<BenchReport>(File.ReadAllText(existing));
                if (previous is null) { Console.Error.WriteLine($"{existing} 解析失败"); return 2; }
                ReportWriter.WriteAll(options, previous);
                SummaryWriter.AppendToGithubStepSummary(options);
                Console.WriteLine($"已用 {existing} 重新生成报告（未重跑测量）");
                return 0;
            }

            Console.WriteLine($"CiBench · repeats={options.Repeats}, batches=[{string.Join(',', options.Batches)}], " +
                $"concurrency={options.Concurrency}, quick={options.Quick}");
            Console.WriteLine($"语料: {options.HtmlPath}");
            Console.WriteLine($"产物: {options.OutDir}");

            var report = await BenchRunner.RunAsync(options);
            ReportWriter.WriteAll(options, report);
            SummaryWriter.AppendToGithubStepSummary(options);

            Console.WriteLine();
            Console.WriteLine("结果：");
            Console.WriteLine($"  {Path.Combine(options.OutDir, "report.html")}");
            Console.WriteLine($"  {Path.Combine(options.OutDir, "results.json")}");
            Console.WriteLine($"  {Path.Combine(options.OutDir, "results.csv")}");
            Console.WriteLine($"  {Path.Combine(options.OutDir, "summary.md")}");

            int exit = Gate.Evaluate(options, report);
            Console.WriteLine(exit == 0 ? "CiBench: PASS" : $"CiBench: 退出码 {exit}");
            return exit;
        }
        catch (HelpRequestedException)
        {
            Console.WriteLine(BenchOptions.HelpText);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CiBench 失败: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    /// <summary>把只影响引擎可用性的开关写进环境变量，spawn 出的 cold-one 子进程自动继承。</summary>
    static void ApplyRuntime(BenchOptions o)
    {
        if (o.AllowUnlicensedIronPdf)
            Environment.SetEnvironmentVariable("CIBENCH_ALLOW_UNLICENSED_IRONPDF", "1");
    }
}
