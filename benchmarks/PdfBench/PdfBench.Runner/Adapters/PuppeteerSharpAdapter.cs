using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace PdfBench.Runner.Adapters;

public sealed class PuppeteerSharpAdapter : HtmlAdapterBase
{
    public override string Name => "puppeteer";

    public override void Generate(AdapterContext ctx, string outPdfPath)
    {
        GenerateAsync(ctx, outPdfPath).GetAwaiter().GetResult();
    }

    private static async Task GenerateAsync(AdapterContext ctx, string outPdfPath)
    {
        // 复用系统 Chrome，避免下载捆绑 Chromium。
        var chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        if (!File.Exists(chrome))
            throw new FileNotFoundException("未找到系统 Chrome，请安装或改用 BundledBrowser 下载。", chrome);

        using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            ExecutablePath = chrome,
        });
        using var page = await browser.NewPageAsync();
        await page.GoToAsync(FileUrl(ctx), new NavigationOptions
        {
            WaitUntil = new[] { WaitUntilNavigation.Networkidle0 },
            Timeout = 60000,
        });
        await page.PdfAsync(outPdfPath, new PdfOptions
        {
            Format = PaperFormat.A4,
            PrintBackground = true,
            MarginOptions = new MarginOptions { Top = "0", Bottom = "0", Left = "0", Right = "0" },
            PreferCSSPageSize = true,
        });
        await browser.CloseAsync();
    }
}
