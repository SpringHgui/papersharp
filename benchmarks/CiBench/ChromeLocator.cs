using System.Diagnostics;

namespace CiBench;

/// <summary>
/// 供 PuppeteerSharp 复用的浏览器可执行文件。优先环境变量，再按平台探测常见安装位置；
/// 都找不到时返回 null，交给 PuppeteerSharp 的 BrowserFetcher 自行下载。
/// 版本信息只读 PE 文件元数据，绝不执行 chrome --version —— Windows 上该命令不会打印版本后退出，
/// 而是拉起一个完整浏览器常驻，既卡住基准又污染进程树 RSS。
/// </summary>
public static class ChromeLocator
{
    static readonly Lazy<(string? Path, string? Version)> Cached = new(Probe);

    /// <summary>只探测路径，供每个测量子进程调用（零进程启动、零延迟）。</summary>
    public static string? TryFindPath() => ProbePath();

    public static (string? Path, string? Version) TryFind() => Cached.Value;

    static (string? Path, string? Version) Probe()
    {
        string? path = ProbePath();
        return (path, path is null ? null : ReadFileVersion(path));
    }

    static string? ProbePath()
    {
        var env = Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        return Candidates().FirstOrDefault(File.Exists);
    }

    static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files\Google\Chrome\Application\chrome.exe";
            yield return @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe");
            yield return @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            yield return @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/bin/google-chrome";
            yield return "/usr/bin/google-chrome-stable";
            yield return "/usr/bin/chromium";
            yield return "/usr/bin/chromium-browser";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        }
    }

    static string? ReadFileVersion(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                return string.IsNullOrWhiteSpace(v.ProductVersion) ? v.FileVersion : v.ProductVersion;
            }
            // Linux/macOS 上读文件版本无意义，留给 env 记录路径即可
            return null;
        }
        catch { return null; }
    }
}
