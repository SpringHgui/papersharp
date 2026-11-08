using System.Text.Json;
using System.Text.Json.Serialization;

namespace CiBench;

public sealed class BenchReport
{
    public string SchemaVersion => "1";
    public string GeneratedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public EnvInfo Env { get; set; } = new();
    public ConfigSnapshot Config { get; set; } = new();
    public List<EngineResult> Engines { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

public sealed class EnvInfo
{
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
    public string Dotnet { get; set; } = "";
    public string Cpu { get; set; } = "";
    public int LogicalCores { get; set; }
    public string GitRev { get; set; } = "";
    public string? ChromePath { get; set; }
    public string? ChromeVersion { get; set; }
    public bool IronPdfLicensed { get; set; }
}

public sealed class ConfigSnapshot
{
    public int Repeats { get; set; }
    public int[] Batches { get; set; } = [];
    public int Concurrency { get; set; }
    public bool Quick { get; set; }
    public string HtmlPath { get; set; } = "";
    public string HtmlSha256 { get; set; } = "";
    public long HtmlBytes { get; set; }
}

public sealed class EngineResult
{
    public string Name { get; set; } = "";
    public string Display { get; set; } = "";
    public string Status { get; set; } = "pending";   // ok | skipped | failed
    public string? SkipReason { get; set; }
    /// <summary>参与但结果需打折的提醒（如 IronPdf 试用模式）。</summary>
    public string? Warning { get; set; }
    public string Version { get; set; } = "";
    public string RenderingEngine { get; set; } = "";
    public ColdResult? Cold { get; set; }
    public WarmResult? Warm { get; set; }
    public List<BatchResult> Batches { get; set; } = [];
    public string? Error { get; set; }
    public bool IsOk => Status == "ok";
}

/// <summary>冷启动：每次全新子进程导出一张 PDF，父进程计墙钟，repeats 次取平均。</summary>
public sealed class ColdResult
{
    public int Repeats { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
    public double MeanMs { get; set; }
    public double MinMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double MaxMs { get; set; }
    public double StdMs { get; set; }
    public double RssPeakMbMean { get; set; }
    public double SizeKbMean { get; set; }
    public string? FirstError { get; set; }
}

/// <summary>热启动：同一常驻进程预热后连续导出单张，repeats 次逐次计时取平均。</summary>
public sealed class WarmResult
{
    public int Repeats { get; set; }
    public int Success { get; set; }
    public double MeanMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double StdMs { get; set; }
    public double AllocKbPerDoc { get; set; }
    public double RssPeakMb { get; set; }
    public double SizeKb { get; set; }
    public int Gen0 { get; set; }
    public int Gen1 { get; set; }
    public int Gen2 { get; set; }
    public string? FirstError { get; set; }
}

/// <summary>并发批次：热态下以固定并发 worker 消费 N 张文档，测总墙钟与进程树内存。</summary>
public sealed class BatchResult
{
    public int Documents { get; set; }
    public int Concurrency { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
    public double WallMs { get; set; }
    public double AvgMsPerDoc { get; set; }
    public double ThroughputDps { get; set; }
    public double LatencyP50Ms { get; set; }
    public double LatencyP95Ms { get; set; }
    public double LatencyMaxMs { get; set; }
    public double RssPeakMb { get; set; }
    public double RssEndMb { get; set; }
    public double AllocMbTotal { get; set; }
    public int Gen0 { get; set; }
    public int Gen1 { get; set; }
    public int Gen2 { get; set; }
    public double SizeKbAvg { get; set; }
    public string? FirstError { get; set; }
}

public static class JsonIo
{
    static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // skipped 引擎 / 空样本会产出 NaN；默认 strict 模式会让整个 results.json 写不出来
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, WriteOpts);
    public static T? Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, ReadOpts);

    public static EnvInfo CollectEnv(string htmlPath, bool ironPdfLicensed)
    {
        var chrome = ChromeLocator.TryFind();
        return new EnvInfo
        {
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Dotnet = Environment.Version.ToString(),
            Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? ReadLinuxCpu() ?? "unknown",
            LogicalCores = Environment.ProcessorCount,
            GitRev = GitRev(),
            ChromePath = chrome.Path,
            ChromeVersion = chrome.Version,
            IronPdfLicensed = ironPdfLicensed,
        };
    }

    static string? ReadLinuxCpu()
    {
        try
        {
            if (!File.Exists("/proc/cpuinfo")) return null;
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
                if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    return line.Split(':', 2)[1].Trim();
        }
        catch { }
        return null;
    }

    static string GitRev()
    {
        try
        {
            var root = RepoPaths.Root;
            var head = Path.Combine(root, ".git", "HEAD");
            if (!File.Exists(head)) return "";
            var content = File.ReadAllText(head).Trim();
            if (!content.StartsWith("ref:", StringComparison.Ordinal)) return content[..Math.Min(12, content.Length)];
            var refPath = Path.Combine(root, ".git", content[4..].Trim().Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(refPath) ? File.ReadAllText(refPath).Trim()[..Math.Min(12, File.ReadAllText(refPath).Trim().Length)] : "";
        }
        catch { return ""; }
    }
}
