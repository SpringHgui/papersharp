using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Layout;
using PaperSharp.Paint;

namespace PaperSharp;

public enum PageSize { A4, Letter }

/// <summary>
/// 顶层入口：把一段 HTML + 一段 CSS 渲染成 PDF 或 PNG 字节数组。
/// PDF 走分页（Paginator + PagePainter），PNG 走单页长图（Position + Painter）。
/// baseDir 用于解析 &lt;img&gt; 的相对路径（一般传 HTML 所在目录）。
/// </summary>
public static class Renderer
{
    public static byte[] RenderPdf(string html, string css, PageSize pageSize = PageSize.A4, string? baseDir = null)
    {
        Prepare(css, baseDir);
        var (defW, defH) = GetPageSize(pageSize);
        var spec = AtPageParser.Parse(css, defW, defH);   // 有 @page 则覆盖默认尺寸/边距
        var body = BuildMeasuredBody(html, css, spec.ContentW);
        var pages = Paginator.Paginate(body, spec);

        using var ms = new MemoryStream();
        var meta = new SKDocumentPdfMetadata
        {
            Title = "PaperSharp",
            Producer = "PaperSharp · SkiaSharp",
            RasterDpi = 72,
            EncodingQuality = 90,
        };
        using (var pdfDoc = SKDocument.CreatePdf(ms, meta))
        {
            for (int i = 0; i < pages.Count; i++)
            {
                using var canvas = pdfDoc.BeginPage(spec.PageW, spec.PageH);
                PagePainter.PaintPage(canvas, pages[i], spec.PageW, spec.PageH);
                MarginBoxRenderer.Render(canvas, spec, i + 1, pages.Count); // 页眉/页脚 + 页码
                pdfDoc.EndPage();
            }
            pdfDoc.Close();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// 渲染单页长图 PNG。<paramref name="scale"/> 是设备缩放因子（retina 导出）：布局仍按 CSS px 计算，
    /// 但画布放大 scale 倍后光栅，文字/几何以更高像素渲染 → 清晰。scale=1 保持旧行为（~96 DPI，偏糊）。
    /// </summary>
    public static byte[] RenderPng(string html, string css, float width, string? baseDir = null, float scale = 1f)
    {
        Prepare(css, baseDir);
        var (defW, defH) = GetPageSize(PageSize.A4);
        var spec = AtPageParser.Parse(css, defW, defH);
        // PNG 预览也按 @page 边距留白，视觉更接近成品页（页眉页脚不画，因为长图不分页）
        float mL = spec.MarginLeft, mR = spec.MarginRight, mT = spec.MarginTop, mB = spec.MarginBottom;

        var computed = StyleResolver.BuildDocument(html, css);
        var root = LayoutBuilder.ToBoxTree(computed);
        LayoutBuilder.Measure(root, Math.Max(1, width - mL - mR));
        LayoutBuilder.Position(root, mL, mT);
        float layoutH = root.TotalHeight + mT + mB;
        if (scale < 1f) scale = 1f;

        int pw = Math.Max(1, (int)Math.Round(width * scale));
        int ph = Math.Max(100, (int)Math.Ceiling(layoutH * scale));
        using var surface = SKSurface.Create(new SKImageInfo(pw, ph));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        if (scale != 1f) canvas.Scale(scale);   // 之后 Painter 仍用 CSS px 坐标，被整体放大到设备像素
        Painter.Paint(canvas, root);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static void Prepare(string css, string? baseDir)
    {
        ImageLoader.BaseDir = baseDir;
        FontManager.BaseDir = baseDir;
        FontFaceParser.ParseAndRegister(css);   // 登记 @font-face 自定义字体
    }

    static BlockBox BuildMeasuredBody(string html, string css, float width)
    {
        var computed = StyleResolver.BuildDocument(html, css);
        var body = LayoutBuilder.ToBoxTree(computed);
        LayoutBuilder.Measure(body, width);
        return body;
    }

    static (float, float) GetPageSize(PageSize size) => size switch
    {
        PageSize.A4 => (595.28f, 841.89f),
        PageSize.Letter => (612f, 792f),
        _ => (595.28f, 841.89f),
    };
}
