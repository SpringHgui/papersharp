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
        var (outlines, links) = Pdf.PdfSemantic.Collect(pages);   // M5：书签 + 链接注释

        using var ms = new MemoryStream();
        var meta = new SKDocumentPdfMetadata
        {
            Title = StyleResolver.LastTitle ?? "PaperSharp",
            Author = StyleResolver.LastAuthor,
            Subject = StyleResolver.LastDescription,
            Keywords = StyleResolver.LastKeywords,
            Creator = "PaperSharp",
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
        // Skia 不出 outline/Annots：用 PDF 增量更新在尾部补书签与链接注释（失败自动原样返回）
        return Pdf.PdfEnhancer.Enhance(ms.ToArray(), outlines, links, spec.PageH);
    }

    /// <summary>
    /// 渲染单页长图 PNG。<paramref name="scale"/> 是设备缩放因子（retina 导出）：布局仍按 CSS px 计算，
    /// 但画布放大 scale 倍后光栅，文字/几何以更高像素渲染 → 清晰。scale=1 保持旧行为（~96 DPI，偏糊）。
    /// </summary>
    public static byte[] RenderPng(string html, string css, float width, string? baseDir = null, float scale = 1f)
        => RenderImage(html, css, width, SKEncodedImageFormat.Png, 100, baseDir, scale);

    /// <summary>光栅输出（PNG / JPEG / WebP）。JPEG 不支持透明，画布本就是白底无影响。</summary>
    public static byte[] RenderImage(string html, string css, float width, SKEncodedImageFormat format,
        int quality = 90, string? baseDir = null, float scale = 1f)
    {
        var (surface, _) = RasterLayout(html, css, width, baseDir, ref scale);
        using (surface)
        {
            using var image = surface.Snapshot();
            using var data = image.Encode(format, quality);
            return data.ToArray();
        }
    }

    /// <summary>
    /// ICO 输出：Vista+ 支持 PNG 压缩的图标条目。取文档左上 width×width 方形区缩放到 size×size，
    /// 打包 ICONDIR(6B) + ICONDIRENTRY(16B) + PNG。size 建议 16/32/48/64/128/256。
    /// </summary>
    public static byte[] RenderIco(string html, string css, int size, float width, string? baseDir = null)
    {
        float scale = 1f;
        var (surface, _) = RasterLayout(html, css, width, baseDir, ref scale);
        byte[] png;
        using (surface)
        {
            using var full = surface.Snapshot();
            int side = Math.Min(full.Width, full.Height);
            using var target = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var p = new SKPaint { FilterQuality = SKFilterQuality.High })
                target.Canvas.DrawImage(full, new SKRect(0, 0, side, side), new SKRect(0, 0, size, size), p);
            using var icon = target.Snapshot();
            using var data = icon.Encode(SKEncodedImageFormat.Png, 100);
            png = data.ToArray();
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)1);      // 保留 / 类型=图标 / 条目数
        bw.Write((byte)(size >= 256 ? 0 : size));                            // 宽（0=256）
        bw.Write((byte)(size >= 256 ? 0 : size));                            // 高
        bw.Write((byte)0); bw.Write((byte)0);                                // 调色板数 / 保留
        bw.Write((ushort)1); bw.Write((ushort)32);                           // 平面 / 位深
        bw.Write((uint)png.Length); bw.Write((uint)22);                      // 数据长 / 偏移
        bw.Write(png);
        return ms.ToArray();
    }

    /// <summary>SVG 输出：把布局树序列化为矢量 SVG（浏览器/编辑器可打开，文字可选中）。</summary>
    public static byte[] RenderSvg(string html, string css, float width, string? baseDir = null)
    {
        Prepare(css, baseDir);
        var (defW, defH) = GetPageSize(PageSize.A4);
        var spec = AtPageParser.Parse(css, defW, defH);
        float mL = spec.MarginLeft, mR = spec.MarginRight, mT = spec.MarginTop, mB = spec.MarginBottom;

        var computed = StyleResolver.BuildDocument(html, css);
        var root = LayoutBuilder.ToBoxTree(computed);
        LayoutBuilder.Measure(root, Math.Max(1, width - mL - mR));
        LayoutBuilder.Position(root, mL, mT);
        float layoutH = root.TotalHeight + mT + mB;
        return SvgWriter.Write(root, width, layoutH);
    }

    /// <summary>共享的 PNG 光栅流程：布局 → 定位 → 建 surface 并绘制。返回 surface 与布局高。</summary>
    static (SKSurface, float) RasterLayout(string html, string css, float width, string? baseDir, ref float scale)
    {
        Prepare(css, baseDir);
        var (defW, defH) = GetPageSize(PageSize.A4);
        var spec = AtPageParser.Parse(css, defW, defH);
        // 预览也按 @page 边距留白，视觉更接近成品页（页眉页脚不画，因为长图不分页）
        float mL = spec.MarginLeft, mR = spec.MarginRight, mT = spec.MarginTop, mB = spec.MarginBottom;

        var computed = StyleResolver.BuildDocument(html, css);
        var root = LayoutBuilder.ToBoxTree(computed);
        LayoutBuilder.Measure(root, Math.Max(1, width - mL - mR));
        LayoutBuilder.Position(root, mL, mT);
        float layoutH = root.TotalHeight + mT + mB;
        if (scale < 1f) scale = 1f;

        int pw = Math.Max(1, (int)Math.Round(width * scale));
        int ph = Math.Max(100, (int)Math.Ceiling(layoutH * scale));
        var surface = SKSurface.Create(new SKImageInfo(pw, ph));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        if (scale != 1f) canvas.Scale(scale);   // 之后 Painter 仍用 CSS px 坐标，被整体放大到设备像素
        Painter.Paint(canvas, root);
        return (surface, layoutH);
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
