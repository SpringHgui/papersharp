using System.Diagnostics;

namespace PdfBench.Runner.Adapters;

public sealed class WkhtmltopdfAdapter : HtmlAdapterBase
{
    public override string Name => "wkhtmltopdf";

    public override void Generate(AdapterContext ctx, string outPdfPath)
    {
        var exe = Locate();
        if (exe is null)
            throw new InvalidOperationException("wkhtmltopdf.exe 未安装或不在 PATH / 常见安装目录。");

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        // 关闭页眉页脚，A4，边距 0（文档自带 @page）。
        psi.ArgumentList.Add("--page-size"); psi.ArgumentList.Add("A4");
        psi.ArgumentList.Add("--margin-top"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--margin-bottom"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--margin-left"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--margin-right"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--disable-smart-shrinking");
        psi.ArgumentList.Add("--encoding"); psi.ArgumentList.Add("utf-8");
        psi.ArgumentList.Add("--enable-local-file-access");
        psi.ArgumentList.Add(FileUrl(ctx));
        psi.ArgumentList.Add(outPdfPath);

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(120000);
        if (p.ExitCode != 0 || !File.Exists(outPdfPath))
            throw new InvalidOperationException($"wkhtmltopdf 退出码 {p.ExitCode}: {err.Split('\n').FirstOrDefault()}");
    }

    private static string? Locate()
    {
        var candidates = new[]
        {
            @"C:\Program Files\wkhtmltopdf\bin\wkhtmltopdf.exe",
            @"C:\Program Files (x86)\wkhtmltopdf\bin\wkhtmltopdf.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"wkhtmltox\wkhtmltox\bin\wkhtmltopdf.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        var path = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, "wkhtmltopdf.exe"))
            .FirstOrDefault(File.Exists);
        return path;
    }
}
