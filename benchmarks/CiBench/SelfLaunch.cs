using System.Diagnostics;
using System.Reflection;

namespace CiBench;

/// <summary>
/// 用「自己」spawn 子进程做隔离测量。dotnet CiBench.dll 启动时 Environment.ProcessPath
/// 指向 dotnet 宿主而不是本程序，所以要把入口 dll 补进参数，否则子进程会去跑 dotnet 的 CLI。
/// </summary>
public static class SelfLaunch
{
    public static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    public static ProcessStartInfo Make(string arguments)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法取得当前进程路径");
        string prefix = "";
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(dll)) throw new InvalidOperationException("单文件发布不适用 spawn 自身");
            prefix = Quote(dll) + " ";
        }
        return new ProcessStartInfo(exe, prefix + arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
    }

    /// <summary>带超时的子进程执行；超时一律 Kill 整棵进程树，避免残留 Chrome 把后续测量污染。</summary>
    public static async Task<ChildResult> RunAsync(string arguments, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(Make(arguments)) ?? throw new InvalidOperationException("子进程启动失败");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        bool timedOut = false;
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { p.Kill(entireProcessTree: true); } catch { }
            await p.WaitForExitAsync();
        }
        sw.Stop();
        return new ChildResult(
            ExitCode: p.ExitCode,
            StdOut: await outTask,
            StdErr: await errTask,
            WallMs: sw.Elapsed.TotalMilliseconds,
            TimedOut: timedOut);
    }
}

public sealed record ChildResult(int ExitCode, string StdOut, string StdErr, double WallMs, bool TimedOut)
{
    public bool Failed => TimedOut || ExitCode != 0;
    public string BriefError => (StdErr ?? "").Trim().Split('\n').FirstOrDefault(x => x.Trim().Length > 0)?.Trim()
        ?? $"exit={ExitCode}, timedOut={TimedOut}";
}
