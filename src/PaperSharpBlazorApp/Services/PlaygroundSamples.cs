using PaperSharp.Layout;

namespace PaperSharpBlazorApp.Services;

/// <summary>
/// Playground 的样例来源：PaperSharpSamples 的验收资产（与单测同一份，见其 csproj 的 LinkBase="assets"）。
/// 刻意不做进 wwwroot —— 那是公开静态目录，样例是开发期资产，只落到 bin 里当 baseDir 用。
/// </summary>
public sealed class PlaygroundSamples
{
    readonly ILogger<PlaygroundSamples> _log;

    /// <summary>资产目录；找不到为 null（此时下拉为空，相对路径图片也解析不了）。</summary>
    public string? AssetsDir { get; }

    /// <summary>下拉里的样例名 = 文件名去扩展名；下划线开头的是拼装碎片（_repad 等），不展示。</summary>
    public IReadOnlyList<string> Names => _names;
    IReadOnlyList<string> _names = Array.Empty<string>();

    public PlaygroundSamples(ILogger<PlaygroundSamples> log)
    {
        _log = log;
        // 候选顺序：链接进输出的 assets/ → 从 bin 往上找源码树里的 PaperSharpSamples/assets
        AssetsDir = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "assets"),
            FindUpwards(AppContext.BaseDirectory, Path.Combine("src", "PaperSharpSamples", "assets")),
        }.FirstOrDefault(d => d != null && Directory.Exists(d) && Directory.EnumerateFiles(d, "*.html").Any());

        if (AssetsDir == null) { _log.LogWarning("未找到 PaperSharpSamples/assets，Playground 样例列表为空"); return; }

        _names = Directory.EnumerateFiles(AssetsDir, "*.html")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is { Length: > 0 } && !n.StartsWith('_'))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 样例里的中文/自定义字体靠这里兜底（@font-face 走各自的 CSS，进程级目录是第二道）。
        var fonts = Path.Combine(AssetsDir, "fonts");
        if (Directory.Exists(fonts)) FontManager.RegisterFontDirectory(fonts);
        _log.LogInformation("Playground 样例资产：{Dir}（{Count} 个 html）", AssetsDir, _names.Count);
    }

    /// <summary>读样例正文；名字不在列表里（手搓 URL / 路径穿越）一律 null。</summary>
    public (string Html, string Css)? Read(string name)
    {
        if (AssetsDir == null || !Names.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        var html = File.ReadAllText(Path.Combine(AssetsDir, name + ".html"));
        // 与 PaperSharpSamples 的 RenderOne 同一口径：同名 .css 作为 css 参数传入（优先级低于文档内 <style>）。
        var cssPath = Path.Combine(AssetsDir, name + ".css");
        return (html, File.Exists(cssPath) ? File.ReadAllText(cssPath) : "");
    }

    static string? FindUpwards(string from, string tail)
    {
        for (var dir = new DirectoryInfo(from); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, tail);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
