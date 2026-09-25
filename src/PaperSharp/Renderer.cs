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
    public static byte[] RenderPdf(string html, string css, PageSize pageSize = PageSize.A4, string? baseDir = null, double? animationTime = null, bool precomputed = false)
    {
        using var scope = RenderScope.Begin(baseDir);
        Prepare(css);
        var (defW, defH) = GetPageSize(pageSize);
        var spec = AtPageParser.Parse(css, defW, defH);   // 有 @page 则覆盖默认尺寸/边距
        var (body, docInfo) = BuildMeasuredBody(html, css, spec.ContentW, animationTime, precomputed);
        var pages = Paginator.Paginate(body, spec);
        var (outlines, links) = Pdf.PdfSemantic.Collect(pages);   // M5：书签 + 链接注释

        using var ms = new MemoryStream();
        var meta = new SKDocumentPdfMetadata
        {
            Title = docInfo?.Title ?? "PaperSharp",
            Author = docInfo?.Author,
            Subject = docInfo?.Description,
            Keywords = docInfo?.Keywords,
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
    public static byte[] RenderPng(string html, string css, float width, string? baseDir = null, float scale = 1f, double? animationTime = null, bool precomputed = false)
        => RenderImage(html, css, width, SKEncodedImageFormat.Png, 100, baseDir, scale, animationTime, precomputed);

    /// <summary>光栅输出（PNG / JPEG / WebP）。JPEG 不支持透明，画布本就是白底无影响。</summary>
    public static byte[] RenderImage(string html, string css, float width, SKEncodedImageFormat format,
        int quality = 90, string? baseDir = null, float scale = 1f, double? animationTime = null, bool precomputed = false)
    {
        var (surface, _, _, _) = RasterLayout(html, css, width, baseDir, ref scale, animationTime, precomputed);
        using (surface)
        {
            using var image = surface.Snapshot();
            return Encode(image, format, quality);
        }
    }

    /// <summary>
    /// PNG 走自研并行编码器（pgenc 实测同尺寸或更小；提速随像素量增长，m1 这类 20Mpx 级 20× 上下、
    /// 22 样例语料合计 19×，见 PERF_REPORT §6.7），其余格式交回 Skia。
    /// 关掉 <see cref="UseFastPng"/> 即整体退回 <c>SKImage.Encode</c>——两条路解码后像素逐字节相同
    /// （goldcheck 对 22 个样例逐像素对账过），所以这个开关只影响速度与体积，不影响画面，可随时 A/B。
    /// 例外：<c>RenderIco</c> 的条目图与 <c>SvgWriter</c> 内嵌的位图仍直接用 Skia——那两处尺寸固定在
    /// 一两万像素，并行分块收益为零，换实现只会无谓地翻 ICO/SVG 基准。
    /// WASM 宿主（<c>PaperSharp.Wasm.Api</c> 静态构造）会把它设为 false：收益本体是并行，
    /// 单线程 + Mono 解释器下逐字节托管滤波反而比 Skia 的 native zlib 慢 1.3–1.5×（PERF_REPORT §6.8）。
    /// </summary>
    public static bool UseFastPng { get; set; } = true;

    static byte[] Encode(SKImage image, SKEncodedImageFormat format, int quality)
    {
        if (UseFastPng && format == SKEncodedImageFormat.Png && Imaging.PngEncoder.Supports(image))
            return Imaging.PngEncoder.Encode(image);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>
    /// 光栅图 + 本轮布局算出的每个真实元素的 border-box 矩形。
    /// 坐标系以 <c>&lt;body&gt;</c> 边框盒左上为原点、单位 CSS px —— 与浏览器里
    /// <c>getBoundingClientRect()</c> 减去 body 矩形的结果同一坐标系，可逐元素直接求差。
    /// </summary>
    public static RasterOutput RenderRasterWithBoxes(string html, string css, float width,
        SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 100,
        string? baseDir = null, float scale = 1f)
    {
        float s = scale;
        // domPath:true 是唯一与生产渲染不同的开关；其余流程与 RenderPng 完全一致，
        // 保证「比对时看到的图」与「用户实际导出的图」出自同一套代码。
        var (surface, layoutH, root, spec) = RasterLayout(html, css, width, baseDir, ref s, null, precomputed: false, domPath: true);
        byte[] bytes;
        int pxW, pxH;
        using (surface)
        {
            using var image = surface.Snapshot();
            (pxW, pxH) = (image.Width, image.Height);
            bytes = Encode(image, format, quality);
        }
        var boxes = new List<BoxRect>();
        CollectBoxes(root, root.BorderX, root.BorderY, boxes);
        // 纯行内元素（span/em/a/b…）引擎不单独成盒，其文字折在父块行盒里。不补这一步，
        // 比对工具会把网页里每一个 <span> 都报成「引擎没渲染」，真问题淹死在噪音里。
        CollectInlineBoxes(root, root.BorderX, root.BorderY, boxes,
            new HashSet<string>(boxes.Select(b => b.DomPath), StringComparer.Ordinal));
        return new RasterOutput(bytes, boxes, layoutH)
        {
            PageWidth = width, PixelWidth = pxW, PixelHeight = pxH, Scale = s,
            MarginLeft = spec.MarginLeft, MarginTop = spec.MarginTop,
            MarginRight = spec.MarginRight, MarginBottom = spec.MarginBottom,
            BodyX = root.BorderX, BodyY = root.BorderY,
        };
    }

    /// <summary>深度优先收集有 DOM 身份（非匿名）的盒；坐标归一到 body 边框盒原点。</summary>
    static void CollectBoxes(BlockBox box, float originX, float originY, List<BoxRect> into)
    {
        // 表格行组（thead/tbody/tfoot）在引擎里是透传容器：表格算法直接摆 tr 与单元格，
        // 行组盒自己从不被定位，几何恒为 0。把它当真实盒送去比对，就会报出
        // 「浏览器 1235 宽 vs 引擎 0 宽」这种最吓人的假偏差——比对口径错了，数值再准也没用。
        // 不 emit 但继续递归：里面的 tr/td 是有真实几何的。
        bool passThrough = box.Style.Display is "table-row-group" or "table-header-group" or "table-footer-group";
        if (!passThrough && box.Style.DomPath is { Length: > 0 } domPath)
            into.Add(new BoxRect(domPath, box.Style.Tag,
                box.BorderX - originX, box.BorderY - originY,
                box.TotalWidth - box.Style.MarginLeft - box.Style.MarginRight,
                box.TotalHeight - box.Style.MarginTop - box.Style.MarginBottom));
        foreach (var c in box.Children) CollectBoxes(c, originX, originY, into);
        // inline 格式化上下文的子盒藏在原子序列里，不在 Children 上，同样要收
        if (box.Atoms != null)
            foreach (var a in box.Atoms)
                if (a.Box != null) CollectBoxes(a.Box, originX, originY, into);
    }

    /// <summary>
    /// 给「引擎按行内片段排版、因而没有盒」的元素补出矩形。
    /// 落位复用 <see cref="TextPainter.DrawInlineLines"/> 那一套（行盒左端 + 片段 X + 基线），
    /// 但矩形取**片段自己的文字盒**（InlineFrag.Ascent+Descent）而非整行行高盒：CSS 里 line-height
    /// 只撑行盒、不改行内元素自身的盒，此前按 字号×行高 报会让每个 span/a/code 虚高 4~6px
    /// （inline / m1 的 code / m5 / m9 / pricing-* 的 dy+dh 系统性偏差都来自这一处）。
    /// 残差仍在：引擎文字盒取 1em+0.22em，Chrome 取字体表的 1.068em（16px 雅黑 17.09），口径差 ~2.4px。
    /// 一个行内元素跨多行时取所有片段的并集，与浏览器 getBoundingClientRect() 的语义一致；
    /// 嵌套行内（b 包 i）时片段矩形同时并给每一级祖先，因为 CSS 里 b 的盒本就包含 i 的内容。
    /// 已经有盒的身份一律跳过，绝不覆盖盒级结果。
    /// </summary>
    static void CollectInlineBoxes(BlockBox box, float originX, float originY, List<BoxRect> into, HashSet<string> boxed)
    {
        foreach (var c in box.Children) CollectInlineBoxes(c, originX, originY, into, boxed);
        if (box.Atoms != null)
            foreach (var a in box.Atoms)
                if (a.Box != null) CollectInlineBoxes(a.Box, originX, originY, into, boxed);

        if (box.Lines == null) return;   // 非 inline 格式化上下文：内容要么已成盒，要么走 WrappedLines 纯文本路径
        var s = box.Style;
        float y = box.BorderY + s.BorderWidthTop + s.PaddingTop + box.ValignDY;
        float contentX = box.BorderX + s.BorderWidthLeft + s.PaddingLeft;
        var union = new Dictionary<string, (string Tag, float X, float Y, float R, float B)>(StringComparer.Ordinal);

        void Add(ComputedStyle? st, float x, float top, float w, float h)
        {
            for (; st != null && !ReferenceEquals(st, s); st = st.Parent)
            {
                if (st.DomPath is not { Length: > 0 } path || boxed.Contains(path)) continue;
                if (union.TryGetValue(path, out var prev))
                    union[path] = (prev.Tag, Math.Min(prev.X, x), Math.Min(prev.Y, top), Math.Max(prev.R, x + w), Math.Max(prev.B, top + h));
                else union[path] = (st.Tag, x, top, x + w, top + h);
            }
        }

        foreach (var ln in box.Lines)
        {
            float lineLeft = contentX + ln.Left, baseline = y + ln.Baseline;
            foreach (var f in ln.Frags)
            {
                if (f.Text == null) continue;
                // 行内盒报的是**字体内容区**（基线上方的 ascent + 下方的 descent），不是 字号×行高：
                // CSS 里 line-height 只撑行盒、不改 <span>/<a>/<code> 自己的盒，Chrome 的
                // getBoundingClientRect() 量的正是内容区。按行高报会让每个行内盒虚高 ~4px、
                // 顶边上移 ~3px（inline / m1 的 code / m5 / m9 / pricing-* 同型偏差都来自这一处）。
                // Ascent/Descent 是 InlineLayout 按**实际字面**取的真实字体度量，与行盒同一口径；
                // Width 已含该元素自己的横向 padding+border（Chrome 的行内盒矩形含 padding）。
                // 纵向必须把 f.AlignDY 也算进来：Chrome 的 getBoundingClientRect() 里，行内盒跟着
                // vertical-align 挪后的那条基线走（<sup> 抬 6.33px 时盒一起抬），而 AlignDY 此前只有
                // 绘制端（TextPainter/SvgWriter）在读 → 字挪了、盒没挪。实测 m3 的 sup/sub 因此各差
                // 5.9/4.7px，且作者在 CSS 里显式写 vertical-align:super 也照样不动（判别探针 _probe-va）。
                Add(f.Style, lineLeft + f.X, baseline + f.AlignDY - f.Ascent, f.Width, f.Ascent + f.Descent);
            }
            y += ln.Height;
        }
        foreach (var (path, u) in union)
            into.Add(new BoxRect(path, u.Tag, u.X - originX, u.Y - originY, u.R - u.X, u.B - u.Y));
    }

    /// <summary>
    /// ICO 输出：Vista+ 支持 PNG 压缩的图标条目。取文档左上 width×width 方形区缩放到 size×size，
    /// 打包 ICONDIR(6B) + ICONDIRENTRY(16B) + PNG。size 建议 16/32/48/64/128/256。
    /// </summary>
    public static byte[] RenderIco(string html, string css, int size, float width, string? baseDir = null, bool precomputed = false)
    {
        float scale = 1f;
        var (surface, _, _, _) = RasterLayout(html, css, width, baseDir, ref scale, null, precomputed);
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
    public static byte[] RenderSvg(string html, string css, float width, string? baseDir = null, double? animationTime = null, bool precomputed = false)
    {
        using var scope = RenderScope.Begin(baseDir);
        Prepare(css);
        var (defW, defH) = GetPageSize(PageSize.A4);
        var spec = AtPageParser.Parse(css, defW, defH);
        float mL = spec.MarginLeft, mR = spec.MarginRight, mT = spec.MarginTop, mB = spec.MarginBottom;

        var computed = StyleResolver.BuildDocument(html, css, animationTime, precomputed, width);
        var root = LayoutBuilder.ToBoxTree(computed);
        LayoutBuilder.Measure(root, Math.Max(1, width - mL - mR));
        LayoutBuilder.Position(root, mL, mT);
        float layoutH = root.TotalHeight + mT + mB;
        return SvgWriter.Write(root, width, layoutH);
    }

    /// <summary>
    /// 共享的 PNG 光栅流程：布局 → 定位 → 建 surface 并绘制。
    /// 返回 surface、布局高、定位完成的盒树根、以及 @page 解析结果
    /// （后两者供比对工具读矩形与页边距，省掉第二轮布局）。
    /// <paramref name="domPath"/> 只在比对/调试入口打开，生产渲染恒 false。
    /// </summary>
    static (SKSurface, float, BlockBox, PageSpec) RasterLayout(string html, string css, float width, string? baseDir, ref float scale,
        double? animationTime = null, bool precomputed = false, bool domPath = false)
    {
        using var scope = RenderScope.Begin(baseDir);
        Prepare(css);
        var (defW, defH) = GetPageSize(PageSize.A4);
        var spec = AtPageParser.Parse(css, defW, defH);
        // 预览也按 @page 边距留白，视觉更接近成品页（页眉页脚不画，因为长图不分页）
        float mL = spec.MarginLeft, mR = spec.MarginRight, mT = spec.MarginTop, mB = spec.MarginBottom;

        var computed = StyleResolver.BuildDocument(html, css, animationTime, precomputed, width, domPath);
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
        Painter.Paint(canvas, root, surface);
        return (surface, layoutH, root, spec);
    }

    static void Prepare(string css)
        => FontFaceParser.ParseAndRegister(css);   // 登记 @font-face 自定义字体（落在当前 RenderScope）

    static (BlockBox body, Css.DocumentInfo? docInfo) BuildMeasuredBody(string html, string css, float width, double? animationTime = null, bool precomputed = false)
    {
        var computed = StyleResolver.BuildDocument(html, css, animationTime, precomputed, width);
        var body = LayoutBuilder.ToBoxTree(computed);
        LayoutBuilder.Measure(body, width);
        return (body, computed.Document);
    }

    static (float, float) GetPageSize(PageSize size) => size switch
    {
        PageSize.A4 => (595.28f, 841.89f),
        PageSize.Letter => (612f, 792f),
        _ => (595.28f, 841.89f),
    };
}

/// <summary>
/// 一个真实 DOM 元素在引擎里排出来的 border-box。
/// 原点 = <c>&lt;body&gt;</c> 边框盒左上角，单位 CSS px —— 与浏览器
/// <c>getBoundingClientRect()</c> 减去 body 矩形后同一坐标系，两边可逐元素直接求差。
/// </summary>
/// <param name="DomPath">DOM 身份串（<c>body/div[1]/p[2]</c>）。浏览器侧按同一条 nth-of-type 规则数出同一个串。</param>
public readonly record struct BoxRect(string DomPath, string Tag, float X, float Y, float W, float H);

/// <summary><see cref="Renderer.RenderRasterWithBoxes"/> 的返回：光栅字节 + 同一轮布局产出的盒矩形。</summary>
/// <param name="LayoutHeight">内容总高（CSS px，含 @page 边距），即 scale=1 时 PNG 的高度。</param>
public sealed record RasterOutput(byte[] Bytes, IReadOnlyList<BoxRect> Boxes, float LayoutHeight)
{
    /// <summary>PNG 画布宽（CSS px），等于调用方传入的 width。</summary>
    public float PageWidth { get; init; }
    /// <summary>PNG 实际像素宽高（= 画布 CSS px × 生效后的 scale）。</summary>
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    /// <summary>生效的设备缩放（传入值小于 1 时会被抬到 1）。像素 ÷ 它 = 图上 1 CSS px。</summary>
    public float Scale { get; init; } = 1f;
    /// <summary>@page 页边距（长图也按它留白）。无 @page 时全 0。</summary>
    public float MarginLeft { get; init; }
    public float MarginTop { get; init; }
    public float MarginRight { get; init; }
    public float MarginBottom { get; init; }
    /// <summary>
    /// body 边框盒在 PNG 里的原点（= 页边距 + body 自身 margin）。
    /// <see cref="Boxes"/> 的坐标已减去它，所以叠加时要把 PNG 反向挪这个量才对得上。
    /// </summary>
    public float BodyX { get; init; }
    public float BodyY { get; init; }
    /// <summary>排版宽度（= PageWidth − 左右页边距），浏览器侧 iframe 要用同一个宽才可比。</summary>
    public float ContentWidth => PageWidth - MarginLeft - MarginRight;
}
