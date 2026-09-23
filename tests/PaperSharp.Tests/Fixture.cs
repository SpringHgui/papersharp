using PaperSharp;

namespace PaperSharp.Tests;

/// <summary>
/// 测试公共设施：加载样例、渲染、定位 goldens 目录、UPDATE_GOLDENS=1 再生成基准。
/// </summary>
public static class Fixture
{
    // 与 PaperSharpSamples/Program.cs 一致的渲染参数
    public const float PngWidth = 794f;
    public const float PngScale = 2f;

    public static readonly string[] Samples =
        { "invoice", "long", "flex", "img", "report", "pct", "polish", "inline", "table", "m1", "m2", "m3", "m4", "m5", "m7" };

    public static readonly string AssetsDir = Path.Combine(AppContext.BaseDirectory, "assets");

    /// <summary>goldens 放测试项目源码目录（可提交进仓库），从 bin 输出向上找 csproj 定位。</summary>
    public static readonly string GoldensDir = LocateProjectDir() + "goldens";

    public static readonly bool UpdateGoldens = Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1";

    static string LocateProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaperSharp.Tests.csproj")))
            dir = dir.Parent;
        return dir?.FullName + Path.DirectorySeparatorChar
            ?? throw new InvalidOperationException("找不到 PaperSharp.Tests.csproj，无法定位 goldens 目录");
    }

    public static (string Html, string Css) ReadSample(string name)
        => (File.ReadAllText(Path.Combine(AssetsDir, name + ".html")),
            File.ReadAllText(Path.Combine(AssetsDir, name + ".css")));

    public static byte[] RenderPdf(string name)
    {
        var (html, css) = ReadSample(name);
        return Renderer.RenderPdf(html, css, baseDir: AssetsDir);
    }

    public static byte[] RenderPng(string name)
    {
        var (html, css) = ReadSample(name);
        return Renderer.RenderPng(html, css, PngWidth, AssetsDir, PngScale);
    }

    /// <summary>
    /// 比对字节 golden。UPDATE_GOLDENS=1 时只写基准不断言（供再生成脚本用）。
    /// 基准缺失一律判失败——防止误删 golden 后"自动重生"掩盖回归。
    /// </summary>
    public static bool MatchGolden(string relativePath, byte[] actual, Func<byte[], byte[], bool>? comparer = null)
    {
        comparer ??= (a, b) => a.AsSpan().SequenceEqual(b);
        var path = Path.Combine(GoldensDir, relativePath);
        if (UpdateGoldens)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, actual);
            return true;
        }
        return File.Exists(path) && comparer(actual, File.ReadAllBytes(path));
    }
}
