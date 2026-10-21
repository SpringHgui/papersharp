using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using PaperSharp;

// 扫描样例资产：按 Playground「同宽档」口径出 PDF，逐页光栅化，找出没有任何可见内容的空白页。
// dotnet run --project tools/BlankScan -- [名字过滤...]
// dotnet run --project tools/BlankScan -- file:<某个.html 路径>   （直读单个文件复现）

var root = FindRepoRoot() ?? throw new Exception("找不到仓库根");
var assets = Path.Combine(root, "src", "PaperSharpSamples", "assets");
var filter = args.ToArray();

// Playground 默认：宽 1284、纸型 SameWidth（BuildPdfCss 追加这一行 @page）
const string PdfCssTail = "\n@page{size:1284px 841.89px}";

if (args.Length == 1 && args[0].StartsWith("file:", StringComparison.OrdinalIgnoreCase))
{
    var p = Path.GetFullPath(args[0][5..]);
    var cssSide = Path.ChangeExtension(p, ".css");
    var (name1, pdf1) = (Path.GetFileNameWithoutExtension(p),
        Renderer.RenderPdf(File.ReadAllText(p), (File.Exists(cssSide) ? File.ReadAllText(cssSide) : "") + PdfCssTail, PageSize.A4, baseDir: Path.GetDirectoryName(p)!));
    Report(name1, pdf1);
    return;
}

void Report(string name, byte[] pdf)
{
    using var ms = new MemoryStream(pdf);
    using var book = UglyToad.PdfPig.PdfDocument.Open(pdf);
    int pages = book.NumberOfPages;
    var blank = new List<int>();
    for (int i = 0; i < pages; i++)
        if (IsBlankPage(ms, i)) blank.Add(i + 1);
    Console.WriteLine(blank.Count > 0
        ? $"{name,-22} {pages,5}  !! 空白页: {string.Join(",", blank)}"
        : $"{name,-22} {pages,5}  -");
}

Console.WriteLine($"{"sample",-22} {"pages",5}  blank_pages");
foreach (var htmlPath in Directory.EnumerateFiles(assets, "*.html").OrderBy(Path.GetFileName))
{
    var name = Path.GetFileNameWithoutExtension(htmlPath);
    if (name.StartsWith('_')) continue;
    if (filter.Length > 0 && !filter.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;

    var html = File.ReadAllText(htmlPath);
    var cssPath = Path.ChangeExtension(htmlPath, ".css");
    var css = (File.Exists(cssPath) ? File.ReadAllText(cssPath) : "") + PdfCssTail;

    byte[] pdf;
    try { pdf = Renderer.RenderPdf(html, css, PageSize.A4, baseDir: assets); }
    catch (Exception ex) { Console.WriteLine($"{name,-22}  ERR {ex.Message.Split('\n')[0]}"); continue; }
    Report(name, pdf);
}

static bool IsBlankPage(MemoryStream ms, int pageIndex)
{
    // 低分辨率光栅：只看有没有任何一个非白像素。
    using var bmp = Conversion.ToImage(ms, pageIndex, leaveOpen: true, options: new RenderOptions(24));
    int w = bmp.Width, h = bmp.Height;
    IntPtr pixels = bmp.GetPixels();
    int bpp = bmp.BytesPerPixel;
    unsafe
    {
        var p = (byte*)pixels;
        for (int y = 0; y < h; y++)
        {
            var row = p + (long)y * bmp.RowBytes;
            for (int x = 0; x < w; x++)
            {
                var px = row + x * bpp;
                // BGRA/RGBA 通用：任一分量偏离白 >8 即算有内容
                if (px[0] < 248 || px[1] < 248 || px[2] < 248) return false;
            }
        }
    }
    return true;
}

static string? FindRepoRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "PaperSharp.slnx"))) return d.FullName;
    return null;
}
