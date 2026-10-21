using iText.IO.Font;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Colorspace;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Pdf.Function;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Layout;
using iText.Layout.Properties;
using iText.Layout.Renderer;

namespace PdfBench.Runner.Adapters;

/// <summary>
/// iText 7 / itext7 8.0.2 手工还原适配器：
/// Layout Document + Table/Paragraph 流式排版；几何图形/渐变/阴影/文字阴影用自定义 IRenderer(CanvasBox) 自绘。
/// </summary>
public sealed class IText7Adapter : IPdfAdapter
{
    public string Name => "itext7";

    // ---------- 单位换算 ----------
    const float MMPT = 2.834645f;
    static float Mm(float mm) => mm * MMPT;
    static float Px(float px) => px * 0.75f;
    static string FontPath(string name) => System.IO.Path.Combine(@"C:\Windows\Fonts", name);

    // ---------- 颜色 ----------
    static Color C(string hex)
    {
        hex = hex.TrimStart('#');
        return new DeviceRgb(
            Convert.ToInt32(hex.Substring(0, 2), 16),
            Convert.ToInt32(hex.Substring(2, 2), 16),
            Convert.ToInt32(hex.Substring(4, 2), 16));
    }
    static DeviceRgb Crgb(string hex) => (DeviceRgb)C(hex);
    static float[] Rgb(DeviceRgb c) => c.GetColorValue();

    static bool HasCjk(string s)
    {
        foreach (var ch in s)
            if (ch >= 0x2E80 && ch <= 0x9FFF || ch >= 0xF900 && ch <= 0xFAFF || ch >= 0xFF00 && ch <= 0xFFEF)
                return true;
        return false;
    }

    sealed class Fonts
    {
        public PdfFont Segoe = null!, SegoeB = null!, SegoeI = null!, Consolas = null!;
        public PdfFont Yahei = null!, YaheiB = null!, YaheiL = null!, YaheiI = null!;
        public PdfFont Sym = null!; // Segoe UI Symbol：✗ 等符号（msyh 缺字）

        static PdfFont Load(string file, string enc) =>
            PdfFontFactory.CreateFont(file, enc, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);

        public static Fonts Create()
        {
            var f = new Fonts();
            f.Segoe = Load(FontPath("segoeui.ttf"), PdfEncodings.WINANSI);
            f.SegoeB = Load(FontPath("segoeuib.ttf"), PdfEncodings.WINANSI);
            f.SegoeI = Load(FontPath("segoeuii.ttf"), PdfEncodings.WINANSI);
            f.Consolas = Load(FontPath("consola.ttf"), PdfEncodings.WINANSI);
            f.Yahei = Load(FontPath("msyh.ttc,0"), PdfEncodings.IDENTITY_H);
            f.YaheiB = Load(FontPath("msyhbd.ttc,0"), PdfEncodings.IDENTITY_H);
            f.YaheiL = Load(FontPath("msyhl.ttc,0"), PdfEncodings.IDENTITY_H);
            f.YaheiI = f.Yahei; // 雅黑无独立斜体文件
            f.Sym = Load(FontPath("seguisym.ttf"), PdfEncodings.IDENTITY_H);
            return f;
        }

        public PdfFont Body(string s) => HasCjk(s) ? Yahei : Segoe;
        public PdfFont Bold(string s) => HasCjk(s) ? YaheiB : SegoeB;
    }

    // ---------- 自绘盒：base 布局完成后在自己的占用区域上执行 Post 回调 ----------
    sealed class CanvasBox : Div
    {
        public readonly PdfDocument Pdf;
        public readonly Action<PdfCanvas, Rectangle> Post;
        public CanvasBox(PdfDocument pdf, Action<PdfCanvas, Rectangle> post) { Pdf = pdf; Post = post; }
        protected override IRenderer MakeNewRenderer() => new CanvasBoxRenderer(this);
    }

    sealed class CanvasBoxRenderer : DivRenderer
    {
        readonly CanvasBox box;
        public CanvasBoxRenderer(CanvasBox b) : base(b) { box = b; }
        public override IRenderer GetNextRenderer() => new CanvasBoxRenderer(box);
        public override LayoutResult Layout(LayoutContext layoutContext)
        {
            var res = base.Layout(layoutContext);
            if (res.GetStatus() == LayoutResult.NOTHING) return res;
            var area = GetOccupiedArea();
            var canvas = new PdfCanvas(box.Pdf, area.GetPageNumber()); // 追加内容流 => 绘制在已有内容之上
            box.Post(canvas, area.GetBBox());
            return res;
        }
    }

    // ---------- canvas 小工具 ----------
    static void DrawText(PdfCanvas c, PdfFont font, float size, Color color, float x, float baseline, string text)
    {
        c.SaveState();
        c.SetFillColor(color);
        c.BeginText();
        c.SetFontAndSize(font, size);
        c.SetTextMatrix(x, baseline);
        c.ShowText(text);
        c.EndText();
        c.RestoreState();
    }

    static void FillRect(PdfCanvas c, Rectangle r, Color color, float opacity = 1f)
    {
        c.SaveState();
        if (opacity < 1f)
            c.SetExtGState(new PdfExtGState().SetFillOpacity(opacity));
        c.SetFillColor(color);
        c.Rectangle(r.GetX(), r.GetY(), r.GetWidth(), r.GetHeight());
        c.Fill();
        c.RestoreState();
    }

    static void StrokeRect(PdfCanvas c, Rectangle r, Color color, float width)
    {
        c.SaveState();
        c.SetLineWidth(width);
        c.SetStrokeColor(color);
        c.Rectangle(r.GetX() + width / 2, r.GetY() + width / 2, r.GetWidth() - width, r.GetHeight() - width);
        c.Stroke();
        c.RestoreState();
    }

    /// <summary>4 段贝塞尔近似画椭圆/圆（k=0.5523）。</summary>
    static void EllipsePath(PdfCanvas c, double cx, double cy, double rx, double ry)
    {
        const double k = 0.5523;
        c.MoveTo(cx + rx, cy);
        c.CurveTo(cx + rx, cy + k * ry, cx + k * rx, cy + ry, cx, cy + ry);
        c.CurveTo(cx - k * rx, cy + ry, cx - rx, cy + k * ry, cx - rx, cy);
        c.CurveTo(cx - rx, cy - k * ry, cx - k * rx, cy - ry, cx, cy - ry);
        c.CurveTo(cx + k * rx, cy - ry, cx + rx, cy - k * ry, cx + rx, cy);
    }

    static void FillEllipse(PdfCanvas c, Rectangle r, Color fill, Color? stroke, float strokeW)
    {
        c.SaveState();
        EllipsePath(c, r.GetX() + r.GetWidth() / 2, r.GetY() + r.GetHeight() / 2, r.GetWidth() / 2, r.GetHeight() / 2);
        c.SetLineWidth(strokeW);
        c.SetFillColor(fill);
        c.SetStrokeColor(stroke ?? fill);
        c.ClosePathFillStroke();
        c.RestoreState();
    }

    static void FillPolygon(PdfCanvas c, Color color, params (double x, double y)[] pts)
    {
        c.SaveState();
        c.SetFillColor(color);
        c.MoveTo(pts[0].x, pts[0].y);
        for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i].x, pts[i].y);
        c.ClosePath();
        c.Fill();
        c.RestoreState();
    }

    // ---------- 渐变（条纹近似：PdfShading 在部分查看器/栅格化下渲染异常，用 64 段插值条纹稳定呈现） ----------
    // 注意：DeviceRgb.GetColorValue() 返回 0..1 归一分量；DeviceRgb(float,float,float) 亦按 0..1 解释，勿乘 255/取整。
    static DeviceRgb Mix(DeviceRgb a, DeviceRgb b, float t)
    {
        var ca = Rgb(a); var cb = Rgb(b);
        return new DeviceRgb(
            ca[0] + (cb[0] - ca[0]) * t,
            ca[1] + (cb[1] - ca[1]) * t,
            ca[2] + (cb[2] - ca[2]) * t);
    }

    static DeviceRgb SampleStops((float stop, DeviceRgb color)[] stops, float t)
    {
        for (int i = 0; i < stops.Length - 1; i++)
        {
            if (t <= stops[i + 1].stop)
            {
                float span = stops[i + 1].stop - stops[i].stop;
                return Mix(stops[i].color, stops[i + 1].color, span <= 0 ? 0 : (t - stops[i].stop) / span);
            }
        }
        return stops[^1].color;
    }

    /// <summary>多停靠线性渐变：angleDeg 为 CSS 语义（0=向上 90=向右 180=向下 45=右上）。旋转空间内画条纹。</summary>
    static void FillLinearGradient(PdfCanvas c, Rectangle r, double angleDeg, params (float stop, DeviceRgb color)[] stops)
    {
        const int N = 64;
        double rad = angleDeg * Math.PI / 180;
        double dx = Math.Sin(rad), dy = Math.Cos(rad); // PDF y 向上
        double cx = r.GetX() + r.GetWidth() / 2.0, cy = r.GetY() + r.GetHeight() / 2.0;
        double len = Math.Abs(r.GetWidth() * dx) + Math.Abs(r.GetHeight() * dy); // 渐变线长
        c.SaveState();
        c.Rectangle(r.GetX(), r.GetY(), r.GetWidth(), r.GetHeight());
        c.Clip();
        c.EndPath(); // W 后必须以 n 结束路径，否则后续填充与裁剪路径合并导致裁剪失效
        double half = len / 2;
        // 局部 y 方向需覆盖整条对角线
        double diag = Math.Sqrt(r.GetWidth() * r.GetWidth() + r.GetHeight() * r.GetHeight()) / 2.0;
        double sw = len / N;
        // 转到“渐变轴 = 局部 x 轴、原点 = 盒中心”的坐标系
        c.ConcatMatrix(dx, dy, -dy, dx, cx, cy);
        for (int i = 0; i < N; i++)
        {
            float t = (i + 0.5f) / N;
            c.SetFillColor(SampleStops(stops, t));
            c.Rectangle((float)(-half + i * sw), (float)-diag, (float)(sw + 0.3), (float)(2 * diag));
            c.Fill();
        }
        c.RestoreState();
    }

    static void FillRadialGradient(PdfCanvas c, Rectangle r, double fx, double fy, DeviceRgb inner, DeviceRgb outer)
    {
        const int N = 48;
        double cx = r.GetX() + r.GetWidth() * fx, cy = r.GetY() + r.GetHeight() * (1 - fy);
        // 半径取焦点到最远角，保证覆盖整个盒
        double maxR = 0;
        foreach (var corner in new[] { new[] { (double)r.GetX(), (double)r.GetY() },
                     new[] { (double)r.GetX() + r.GetWidth(), (double)r.GetY() },
                     new[] { (double)r.GetX(), (double)r.GetY() + r.GetHeight() },
                     new[] { (double)r.GetX() + r.GetWidth(), (double)r.GetY() + r.GetHeight() } })
            maxR = Math.Max(maxR, Math.Sqrt((corner[0] - cx) * (corner[0] - cx) + (corner[1] - cy) * (corner[1] - cy)));
        c.SaveState();
        c.Rectangle(r.GetX(), r.GetY(), r.GetWidth(), r.GetHeight());
        c.Clip();
        c.EndPath();
        // 从外到内画同心圆
        for (int i = N; i >= 1; i--)
        {
            float t = (float)i / N;
            c.SetFillColor(Mix(inner, outer, t));
            EllipsePath(c, cx, cy, maxR * t, maxR * t);
            c.Fill();
        }
        c.RestoreState();
    }

    static DeviceRgb BlendOnWhite(DeviceRgb c, float alpha)
    {
        var v = Rgb(c); // 0..1
        return new DeviceRgb(
            v[0] * alpha + 1f * (1 - alpha),
            v[1] * alpha + 1f * (1 - alpha),
            v[2] * alpha + 1f * (1 - alpha));
    }

    // ---------- 布局小工具 ----------
    readonly struct FlexCell
    {
        public readonly float Flex;
        public readonly IBlockElement El;
        public FlexCell(float flex, IBlockElement el) { Flex = flex; El = el; }
    }

    static readonly Color White = new DeviceRgb(255, 255, 255);
    static readonly Color Ink = C("#222222");
    const float ContentW = 182 * MMPT; // 210 - 2*14 mm

    static Cell PlainCell(float w, IBlockElement el, VerticalAlignment valign = VerticalAlignment.TOP) =>
        new Cell().SetWidth(w).SetPadding(0).SetBorder(Border.NO_BORDER).SetVerticalAlignment(valign).Add(el);

    static Table FlexRow(float gap, params FlexCell[] cells)
    {
        float sumF = cells.Sum(c => c.Flex);
        float total = ContentW - gap * (cells.Length - 1);
        var widths = cells.Select(c => total * c.Flex / sumF).ToArray();
        var cols = new List<UnitValue>();
        foreach (var w in widths) { cols.Add(UnitValue.CreatePointValue(w)); cols.Add(UnitValue.CreatePointValue(gap)); }
        cols.RemoveAt(cols.Count - 1);
        var t = new Table(cols.ToArray());
        t.SetWidth(total);
        for (int i = 0; i < cells.Length; i++)
        {
            t.AddCell(PlainCell(widths[i], cells[i].El));
            if (i < cells.Length - 1) t.AddCell(PlainCell(gap, new Div().SetMinHeight(0)));
        }
        return t;
    }

    static Table FixedRow(float gap, VerticalAlignment valign, params (float w, IBlockElement el)[] cells)
    {
        var cols = new List<UnitValue>();
        foreach (var (w, _) in cells) { cols.Add(UnitValue.CreatePointValue(w)); cols.Add(UnitValue.CreatePointValue(gap)); }
        cols.RemoveAt(cols.Count - 1);
        var t = new Table(cols.ToArray());
        t.SetWidth(cells.Sum(x => x.w) + gap * (cells.Length - 1));
        for (int i = 0; i < cells.Length; i++)
        {
            t.AddCell(PlainCell(cells[i].w, cells[i].el, valign));
            if (i < cells.Length - 1) t.AddCell(PlainCell(gap, new Div().SetMinHeight(0)));
        }
        return t;
    }

    // =====================================================================
    public void Generate(AdapterContext ctx, string outPdfPath)
    {
        var F = Fonts.Create();
        using var pdfDoc = new PdfDocument(new PdfWriter(outPdfPath));
        pdfDoc.SetDefaultPageSize(PageSize.A4);
        var doc = new Document(pdfDoc, PageSize.A4);
        doc.SetMargins(Mm(12), Mm(14), Mm(12), Mm(14));
        float W = ContentW, H = PageSize.A4.GetHeight();

        PageHeader(doc, F, "Page 1 — Typography & Text");
        BuildPage1(doc, F, pdfDoc);
        Footer(doc, F, W, H, 1);
        doc.Add(new AreaBreak());
        PageHeader(doc, F, "Page 2 — Borders & Corners");
        BuildPage2(doc, F, pdfDoc);
        Footer(doc, F, W, H, 2);
        doc.Add(new AreaBreak());
        PageHeader(doc, F, "Page 3 — Effects, Images, Layout");
        BuildPage3(doc, F, pdfDoc, ctx);
        Footer(doc, F, W, H, 3);
        doc.Close();
    }

    // ---------- 页眉 / 页脚 ----------
    static void PageHeader(Document doc, Fonts F, string tag)
    {
        var p = new Paragraph()
            .Add(new Text("PDFBENCH · VISUAL CHALLENGE").SetFont(F.SegoeB).SetFontSize(10).SetFontColor(C("#34495e")).SetCharacterSpacing(Px(1)))
            .Add(new Tab()).Add(new Text(tag).SetFont(F.Segoe).SetFontSize(7.5f).SetFontColor(C("#7f8c8d")));
        p.AddTabStops(new TabStop(ContentW, TabAlignment.RIGHT));
        p.SetMarginBottom(Mm(3)).SetPaddingBottom(Mm(2))
            .SetBorderBottom(new SolidBorder(C("#34495e"), Px(2)))
            .SetMultipliedLeading(1.1f);
        doc.Add(p);
    }

    static void Footer(Document doc, Fonts F, float W, float H, int page)
    {
        var left = new Text("PdfBench · challenge.html · generated by spec v0.1").SetFont(F.Segoe).SetFontSize(6.75f).SetFontColor(C("#7f8c8d"));
        var pillTxt = new Text(" " + page + " / 3 ").SetFont(F.SegoeB).SetFontSize(6.75f).SetFontColor(C("#34495e"))
            .SetBorder(new SolidBorder(C("#34495e"), 0.8f)).SetBorderRadius(new BorderRadius(Px(8)));
        var p = new Paragraph()
            .Add(left).Add(new Tab()).Add(pillTxt);
        p.AddTabStops(new TabStop(W, TabAlignment.RIGHT));
        p.SetBorderTop(new SolidBorder(C("#bdc3c7"), 0.75f)).SetPaddingTop(Mm(1.5f))
            .SetMultipliedLeading(1.5f);
        p.SetFixedPosition(page, 0, Mm(6), UnitValue.CreatePointValue(W));
        doc.Add(p);
    }

    // ---------- 标题 / 段落 ----------
    static Paragraph H1(Fonts F, string s) =>
        new Paragraph(s).SetFont(F.Bold(s)).SetFontSize(15).SetFontColor(C("#34495e")).SetMarginBottom(Mm(2)).SetMultipliedLeading(1.15f);

    static Paragraph H2(Fonts F, string s) =>
        new Paragraph(s).SetFont(F.Bold(s)).SetFontSize(10.5f).SetFontColor(C("#2c3e50"))
            .SetBorderLeft(new SolidBorder(C("#3498db"), 3)).SetPaddingLeft(Px(6))
            .SetMarginTop(Mm(2)).SetMarginBottom(Mm(1.5f)).SetMultipliedLeading(1.15f);

    static Paragraph Lbl(Fonts F, string s) =>
        new Paragraph(s).SetFont(F.Body(s)).SetFontSize(6).SetFontColor(C("#95a5a6")).SetMarginBottom(1).SetMultipliedLeading(1.1f);

    static Paragraph P(Fonts F, string s, float px = 10.5f) =>
        new Paragraph(s).SetFont(F.Body(s)).SetFontSize(Px(px)).SetMarginBottom(Mm(2.5f)).SetMultipliedLeading(1.15f);

    // =====================================================================
    // PAGE 1
    // =====================================================================
    void BuildPage1(Document doc, Fonts F, PdfDocument pdfDoc)
    {
        doc.Add(H1(F, "排版与文字 Rendering Test"));
        doc.Add(H2(F, "1.1 层级与字重"));

        var h3row = FixedRow(0, VerticalAlignment.BOTTOM,
            (ContentW / 2, new Paragraph("H3 subtitle").SetFont(F.SegoeB).SetFontSize(8.25f).SetFontColor(C("#7f8c8d")).SetMultipliedLeading(1.1f)),
            (ContentW / 2, new Paragraph("h3 → h1 右对齐抬升").SetFont(F.Yahei).SetFontSize(6.75f).SetFontColor(C("#95a5a6"))
                .SetTextAlignment(TextAlignment.RIGHT).SetMultipliedLeading(1.1f)));
        doc.Add(h3row.SetMarginBottom(Px(4)));

        // 字重
        var wp = new Paragraph().SetMultipliedLeading(1.3f).SetMarginBottom(Mm(2.5f));
        wp.SetFont(F.Yahei).SetFontSize(Px(10.5f));
        wp.Add(new Text("字重："));
        wp.Add(new Text("粗体 bold").SetFont(F.YaheiB));
        wp.Add(new Text("、"));
        wp.Add(new Text("斜体 italic").SetItalic());
        wp.Add(new Text("、"));
        wp.Add(new Text("粗斜体").SetFont(F.YaheiB).SetBold().SetItalic());
        wp.Add(new Text("、"));
        wp.Add(new Text("细体 light 300").SetFont(F.YaheiL));
        wp.Add(new Text("、"));
        wp.Add(new Text("下划线").SetUnderline());
        wp.Add(new Text("、"));
        wp.Add(new Text("删除线").SetLineThrough());
        wp.Add(new Text("、"));
        wp.Add(new Text("SMALL CAPS ").SetFont(F.Segoe).SetFontSize(Px(8.4f)));
        wp.Add(new Text("效果"));
        wp.Add(new Text("。"));
        doc.Add(wp);

        // 行内代码芯片
        var cp = new Paragraph().SetMultipliedLeading(1.7f).SetMarginBottom(Mm(2.5f));
        cp.SetFont(F.Yahei).SetFontSize(Px(10.5f));
        cp.Add(new Text("行内代码芯片： "));
        cp.Add(new Text(" PdfGenerator.Render() ")
            .SetFont(F.Consolas).SetFontSize(Px(9.5f)).SetFontColor(C("#8e24aa"))
            .SetBackgroundColor(C("#f3e5f5"))
            .SetBorder(new SolidBorder(C("#ce93d8"), 0.75f)).SetBorderRadius(new BorderRadius(Px(3))));
        cp.Add(new Text(" 与 "));
        cp.Add(new Text(" spec.json ")
            .SetFont(F.Consolas).SetFontSize(Px(9.5f)).SetFontColor(C("#1565c0"))
            .SetBackgroundColor(C("#e3f2fd"))
            .SetBorderRadius(new BorderRadius(Px(3))));
        cp.Add(new Text(" 混排于行文中。"));
        doc.Add(cp);

        doc.Add(H2(F, "1.2 中西混排与行高"));
        doc.Add(P(F, "中文段落混合 English words 与数字 0123456789，标点，以及括号（半角）与全角符号。 中英文基线对齐是渲染引擎的常见短板，The quick brown fox 应跳 over the lazy dog 而不断行错位。").SetMultipliedLeading(1.8f));

        doc.Add(Lbl(F, "line-height: 1.0"));
        doc.Add(P(F, "密集行距示例 The quick brown fox jumps over the lazy dog 中文密集行示例，行与行可能粘连。")
            .SetMultipliedLeading(1.0f).SetBackgroundColor(C("#fdf6ec")).SetMarginBottom(Mm(1)));
        doc.Add(Lbl(F, "line-height: 1.4"));
        doc.Add(P(F, "正常行距示例 The quick brown fox jumps over the lazy dog 中文正常行距示例，阅读舒适。")
            .SetMultipliedLeading(1.4f).SetBackgroundColor(C("#fdf6ec")).SetMarginBottom(Mm(1)));
        doc.Add(Lbl(F, "line-height: 2.2"));
        doc.Add(P(F, "宽松行距示例 The quick brown fox 中文宽松行距示例。")
            .SetMultipliedLeading(2.2f).SetBackgroundColor(C("#fdf6ec")).SetMarginBottom(0));

        doc.Add(new Paragraph("normal: tracking 字距").SetFont(F.Yahei).SetFontSize(9).SetMarginTop(Mm(2)).SetMultipliedLeading(1.35f));
        doc.Add(new Paragraph("wide 2px: tracking 字距").SetFont(F.Yahei).SetFontSize(9).SetCharacterSpacing(Px(2)).SetMultipliedLeading(1.35f));
        doc.Add(new Paragraph("wider 5px: tracking 字距").SetFont(F.Yahei).SetFontSize(9).SetCharacterSpacing(Px(5)).SetMarginBottom(Mm(2)).SetMultipliedLeading(1.35f));

        doc.Add(P(F, "首行缩进两个字符：这是段落首行缩进的验证文本，缩进应恰好为 2 个全角字符宽度且仅作用于首行。").SetFirstLineIndent(Px(21)));

        doc.Add(H2(F, "1.3 文字效果与对齐"));

        // 文字阴影：base 先画阴影色文字，Post 在其左上偏移处画主色文字
        var shadowBox = new CanvasBox(pdfDoc, (canvas, r) =>
        {
            float size = Px(16);
            float baseline = r.GetTop() - size * 1.08f;
            DrawText(canvas, F.YaheiB, size, C("#2c3e50"), r.GetX() - Px(2), baseline + Px(2), "阴影文字 Text Shadow");
        })
            .SetHeight(Px(16) * 1.45f).SetMarginBottom(Mm(1.5f));
        shadowBox.Add(new Paragraph("阴影文字 Text Shadow").SetFont(F.YaheiB).SetFontSize(Px(16))
            .SetFontColor(C("#f5b7b1")).SetMultipliedLeading(1.1f));
        doc.Add(shadowBox);

        var ct = new Paragraph().SetMultipliedLeading(1.3f).SetMarginBottom(Mm(2.5f));
        ct.SetFont(F.Yahei).SetFontSize(Px(10.5f));
        ct.Add(new Text("红").SetFontColor(C("#e74c3c")));
        ct.Add(new Text(" "));
        ct.Add(new Text("绿").SetFontColor(C("#27ae60")));
        ct.Add(new Text(" "));
        ct.Add(new Text("蓝").SetFontColor(C("#2980b9")));
        ct.Add(new Text(" "));
        ct.Add(new Text("橙").SetFontColor(C("#f39c12")));
        ct.Add(new Text(" "));
        ct.Add(new Text("紫").SetFontColor(C("#8e44ad")));
        ct.Add(new Text(" —— 五色文字，纯色无渐变。"));
        doc.Add(ct);

        var tint = C("#ecf0f1");
        doc.Add(FlexRow(Mm(4),
            new FlexCell(1, TintCell(F, "左对齐 left 文", tint, TextAlignment.LEFT)),
            new FlexCell(1, TintCell(F, "居中 center 文", tint, TextAlignment.CENTER)),
            new FlexCell(1, TintCell(F, "右对齐 right 文", tint, TextAlignment.RIGHT)),
            new FlexCell(1.6f, TintCell(F, "两端对齐 justify：This paragraph is justified so that both left and right edges form clean lines across its full width 无论中英文。", tint, TextAlignment.JUSTIFIED))));

        doc.Add(H2(F, "1.4 列表与嵌套"));

        var col1 = new Div();
        col1.Add(Lbl(F, "unordered disc，marker 着色"));
        static ListItem LI(Paragraph p) { var li = new ListItem(); li.Add(p); return li; }
        Paragraph LP(string s) => new Paragraph(s).SetFont(F.Yahei).SetFontSize(7.5f).SetMultipliedLeading(1.25f);
        var ul1 = new List().SetListSymbol("•").SetSymbolIndent(12).SetFont(F.Yahei).SetFontSize(7.5f);
        ul1.SetMarginLeft(16);
        var li1p = new Paragraph().SetFont(F.Yahei).SetFontSize(7.5f).SetMultipliedLeading(1.25f);
        li1p.Add(new Text("项目一：").SetFontColor(C("#e74c3c")));
        li1p.Add(new Text("无序列表 disc 符号"));
        ul1.Add(LI(li1p));
        ul1.Add(LI(LP("项目二：Marker 颜色独立于文字")));
        var li3 = LI(LP("项目三：三级嵌套入口"));
        var ul2 = new List().SetListSymbol("•").SetSymbolIndent(12).SetFont(F.Yahei).SetFontSize(7.5f).SetMarginLeft(14);
        var liA = LI(LP("嵌套层 A"));
        var ul3 = new List().SetListSymbol("▪").SetSymbolIndent(12).SetFont(F.Yahei).SetFontSize(7.5f).SetMarginLeft(14);
        ul3.Add(LI(LP("方块 square 层")));
        liA.Add(ul3);
        ul2.Add(liA);
        li3.Add(ul2);
        ul1.Add(li3);
        col1.Add(ul1);

        var col2 = new Div();
        col2.Add(Lbl(F, "ordered decimal / lower-alpha / upper-roman"));
        var ol1 = new List().SetListSymbol(ListNumberingType.DECIMAL).SetSymbolIndent(12).SetFont(F.Yahei).SetFontSize(7.5f).SetMarginLeft(16);
        ol1.Add(LI(LP("十进制 ordered")));
        var oli2 = LI(LP("第二项"));
        var olAlpha = new List().SetSymbolIndent(12).SetFont(F.Yahei).SetFontSize(7.5f).SetMarginLeft(14);
        var la = LI(LP("lower-alpha 子项"));
        la.SetListSymbol(new Text("a.").SetFont(F.Segoe).SetFontSize(7.5f));
        var lb = LI(LP("子项二"));
        lb.SetListSymbol(new Text("b.").SetFont(F.Segoe).SetFontSize(7.5f));
        olAlpha.Add(la); olAlpha.Add(lb);
        oli2.Add(olAlpha);
        ol1.Add(oli2);
        col2.Add(ol1);
        var ol2 = new List().SetListSymbol(ListNumberingType.ROMAN_UPPER).SetSymbolIndent(12).SetFont(F.Yahei).SetFontSize(7.5f).SetMarginLeft(16);
        ol2.Add(LI(LP("upper-roman 独立编号")));
        ol2.Add(LI(LP("应为 II")));
        col2.Add(ol2);

        var col3 = new Div();
        col3.Add(Lbl(F, "自定义 marker：circle + 无标记"));
        var ulc = new List().SetListSymbol("◦").SetSymbolIndent(12).SetFont(F.Yahei).SetFontSize(7.5f).SetMarginLeft(16);
        ulc.Add(LI(LP("circle 空心圈")));
        var noneLi = LI(LP("none：无标记但有缩进对齐"));
        noneLi.SetListSymbol(new Text(" ").SetFont(F.Segoe).SetFontSize(7.5f));
        ulc.Add(noneLi);
        ulc.Add(LI(LP("circle 第二项")));
        col3.Add(ulc);

        doc.Add(FlexRow(Mm(4), new FlexCell(1, col1), new FlexCell(1, col2), new FlexCell(1, col3)));
    }

    static Div TintCell(Fonts F, string text, Color bg, TextAlignment align) =>
        new Div().SetBackgroundColor(bg).SetPadding(Mm(2.5f))
            .Add(new Paragraph(text).SetFont(F.Body(text)).SetFontSize(Px(9)).SetTextAlignment(align).SetMultipliedLeading(1.25f));

    // =====================================================================
    // PAGE 2
    // =====================================================================
    void BuildPage2(Document doc, Fonts F, PdfDocument pdfDoc)
    {
        doc.Add(H1(F, "边框家族与圆角"));
        doc.Add(H2(F, "2.1 四种线型 × 三种宽度"));

        static Div BCell(Fonts F, Border b, string label) =>
            new Div().SetBorder(b).SetPadding(Mm(2.5f))
                .Add(new Paragraph(label).SetFont(F.Segoe).SetFontSize(Px(9)).SetMultipliedLeading(1.1f));

        float cw = Mm(30), gap = Mm(4);
        var red = C("#e74c3c"); var blue = C("#2980b9"); var green = C("#27ae60"); var purple = C("#8e44ad");
        void StyleRow(params Div[] rowCells)
        {
            doc.Add(FixedRow(gap, VerticalAlignment.TOP, rowCells.Select(d => (cw, (IBlockElement)d)).ToArray()).SetMarginBottom(Mm(1.5f)));
        }
        StyleRow(
            BCell(F, new SolidBorder(red, Px(2)), "solid 2px"),
            BCell(F, new SolidBorder(red, Px(4)), "solid 4px"),
            BCell(F, new SolidBorder(red, Px(8)), "solid 8px"));
        StyleRow(
            BCell(F, new DashedBorder(blue, Px(2)), "dashed 2px"),
            BCell(F, new DashedBorder(blue, Px(4)), "dashed 4px"),
            BCell(F, new DashedBorder(blue, Px(8)), "dashed 4px+"));
        StyleRow(
            BCell(F, new DottedBorder(green, Px(2)), "dotted 2px"),
            BCell(F, new DottedBorder(green, Px(4)), "dotted 4px"),
            BCell(F, new DoubleBorder(purple, Px(8)), "double 8px"));
        StyleRow(
            BCell(F, new DoubleBorder(purple, Px(4)), "double 4px"),
            BCell(F, new GrooveBorder(Crgb("#d35400"), Px(4)), "groove 4px"),
            BCell(F, new RidgeBorder(Crgb("#16a085"), Px(4)), "ridge 4px"));

        doc.Add(H2(F, "2.2 逐边异构"));
        var per1 = new Div()
            .SetBorderTop(new SolidBorder(blue, Px(3))).SetBorderRight(new DashedBorder(red, Px(3)))
            .SetBorderBottom(new DottedBorder(green, Px(3))).SetBorderLeft(new DoubleBorder(C("#f39c12"), Px(4)))
            .SetPaddingLeft(Mm(3)).SetPaddingRight(Mm(3)).SetPaddingTop(Mm(5)).SetPaddingBottom(Mm(5))
            .Add(new Paragraph("四边四线型：上实/右虚/下点/左双").SetFont(F.Yahei).SetFontSize(Px(9)).SetMultipliedLeading(1.1f));
        var per2 = new Div()
            .SetBorderTop(new SolidBorder(red, Px(3))).SetBorderRight(new SolidBorder(blue, Px(3)))
            .SetBorderBottom(new SolidBorder(green, Px(3))).SetBorderLeft(new SolidBorder(C("#f39c12"), Px(3)))
            .SetPaddingLeft(Mm(3)).SetPaddingRight(Mm(3)).SetPaddingTop(Mm(5)).SetPaddingBottom(Mm(5))
            .Add(new Paragraph("四边同线型异色：上红/右蓝/下绿/左橙").SetFont(F.Yahei).SetFontSize(Px(9)).SetMultipliedLeading(1.1f));
        var per3 = new Div()
            .SetBorder(new SolidBorder(C("#c0392b"), Px(2)))
            .SetPaddingLeft(Mm(3)).SetPaddingRight(Mm(3)).SetPaddingTop(Mm(5)).SetPaddingBottom(Mm(5))
            .Add(new Paragraph("currentColor 边框：边色随字色变红").SetFont(F.Yahei).SetFontSize(Px(9)).SetFontColor(C("#c0392b")).SetMultipliedLeading(1.1f));
        doc.Add(FlexRow(Mm(4), new FlexCell(1, per1), new FlexCell(1, per2), new FlexCell(1, per3)));

        var s1 = new Div().SetBackgroundColor(C("#ecf0f1")).SetBorderBottom(new SolidBorder(purple, Px(3)))
            .SetPaddingLeft(Mm(3)).SetPaddingRight(Mm(3)).SetPaddingTop(Mm(2)).SetPaddingBottom(Mm(2))
            .Add(new Paragraph("仅底边：文章式分隔线").SetFont(F.Yahei).SetFontSize(Px(9)).SetMultipliedLeading(1.1f));
        var s2 = new Div().SetBackgroundColor(C("#ecf0f1")).SetBorderLeft(new SolidBorder(green, Px(5)))
            .SetPaddingLeft(Mm(3)).SetPaddingRight(Mm(3)).SetPaddingTop(Mm(2)).SetPaddingBottom(Mm(2))
            .Add(new Paragraph("仅左边：引文强调条").SetFont(F.Yahei).SetFontSize(Px(9)).SetMultipliedLeading(1.1f));
        var s3 = new Div().SetBorderTop(new SolidBorder(blue, Px(2))).SetBorderBottom(new SolidBorder(blue, Px(2)))
            .SetPaddingLeft(Mm(3)).SetPaddingRight(Mm(3)).SetPaddingTop(Mm(2)).SetPaddingBottom(Mm(2))
            .Add(new Paragraph("上下边：band 样式").SetFont(F.Yahei).SetFontSize(Px(9)).SetMultipliedLeading(1.1f));
        doc.Add(FlexRow(Mm(4), new FlexCell(1, s1), new FlexCell(1, s2), new FlexCell(1, s3)).SetMarginTop(Mm(1.5f)));

        doc.Add(H2(F, "2.3 圆角"));

        static Div RadiusBox(Fonts F, (float, float, float, float) r, string label)
        {
            var d = new Div().SetWidth(Mm(26)).SetHeight(Mm(18))
                .SetBackgroundColor(C("#d6eaf8")).SetBorder(new SolidBorder(C("#2980b9"), Px(2)))
                .SetPaddingLeft(Mm(2.5f)).SetPaddingTop(Mm(2));
            d.SetBorderTopLeftRadius(new BorderRadius(r.Item1));
            d.SetBorderTopRightRadius(new BorderRadius(r.Item2));
            d.SetBorderBottomRightRadius(new BorderRadius(r.Item3));
            d.SetBorderBottomLeftRadius(new BorderRadius(r.Item4));
            d.Add(new Paragraph(label).SetFont(F.Body(label)).SetFontSize(Px(9)).SetMultipliedLeading(1.1f));
            return d;
        }
        doc.Add(FixedRow(gap, VerticalAlignment.MIDDLE,
            (Mm(26), RadiusBox(F, (0, 0, 0, 0), "r=0")),
            (Mm(26), RadiusBox(F, (Px(4), Px(4), Px(4), Px(4)), "4px")),
            (Mm(26), RadiusBox(F, (Px(10), Px(10), Px(10), Px(10)), "10px")),
            (Mm(26), RadiusBox(F, (Px(8), Px(2), Px(8), Px(2)), "8/2 对角")),
            (Mm(26), RadiusBox(F, (Px(14), Px(2), Px(14), Px(2)), "四角异值"))).SetMarginBottom(Mm(1.5f)));

        // 正圆 / 椭圆 / 胶囊 / 虚线圆角
        var circle = new CanvasBox(pdfDoc, (c, r) => FillEllipse(c, r, C("#fadbd8"), C("#c0392b"), Px(2)))
            .SetWidth(Mm(18)).SetHeight(Mm(18));
        var ellipse = new CanvasBox(pdfDoc, (c, r) => FillEllipse(c, r, C("#d5f5e3"), C("#27ae60"), Px(2)))
            .SetWidth(Mm(11)).SetHeight(Mm(18));
        var pill = new Div().SetWidth(Mm(34)).SetHeight(Mm(10))
            .SetBackgroundColor(C("#fdebd0")).SetBorder(new SolidBorder(C("#d35400"), Px(3)))
            .SetVerticalAlignment(VerticalAlignment.MIDDLE);
        pill.SetBorderRadius(new BorderRadius(Mm(5)));
        pill.Add(new Paragraph("胶囊 pill r=高度/2").SetFont(F.Yahei).SetFontSize(Px(9)).SetTextAlignment(TextAlignment.CENTER).SetMultipliedLeading(1f));
        var dashRadius = new Div().SetWidth(Mm(26)).SetHeight(Mm(18))
            .SetBackgroundColor(C("#d6eaf8")).SetBorder(new DashedBorder(purple, Px(3)));
        dashRadius.SetBorderRadius(new BorderRadius(Px(10)));
        doc.Add(FixedRow(Mm(2), VerticalAlignment.MIDDLE,
            (Mm(18), circle),
            (Mm(16), Lbl(F, "50% 正圆")),
            (Mm(11), ellipse),
            (Mm(16), Lbl(F, "50% 椭圆")),
            (Mm(34), pill),
            (Mm(26), dashRadius),
            (Mm(24), Lbl(F, "圆角+虚线复合"))).SetMarginBottom(Mm(1.5f)));

        // 复合盒
        var comp1 = new Div().SetBorder(new SolidBorder(C("#34495e"), Px(6)))
            .SetBackgroundColor(C("#f8f9f9")).SetPadding(Mm(3))
            .Add(new Paragraph("厚边框(6px)+大圆角(14px)+内边距+背景色 复合盒：边框与背景的圆角必须同心，内边距把内容从边框推开。")
                .SetFont(F.Yahei).SetFontSize(Px(9)).SetMultipliedLeading(1.25f));
        comp1.SetBorderRadius(new BorderRadius(Px(14)));
        var clipHead = new Div().SetBackgroundColor(C("#27ae60"))
            .SetPaddingLeft(Mm(3)).SetPaddingRight(Mm(3)).SetPaddingTop(Mm(2)).SetPaddingBottom(Mm(2))
            .Add(new Paragraph("裁切过头圆角").SetFont(F.YaheiB).SetFontSize(Px(9)).SetFontColor(White).SetMultipliedLeading(1.1f));
        clipHead.SetBorderTopLeftRadius(new BorderRadius(Px(8)));
        clipHead.SetBorderTopRightRadius(new BorderRadius(Px(8)));
        var comp2 = new Div().SetBorder(new SolidBorder(C("#27ae60"), Px(2)))
            .Add(clipHead)
            .Add(new Div().SetPaddingLeft(Mm(3)).SetPaddingRight(Mm(3)).SetPaddingTop(Mm(2)).SetPaddingBottom(Mm(2))
                .Add(new Paragraph("overflow:hidden 子元素背景填满左上圆角").SetFont(F.Yahei).SetFontSize(Px(9)).SetMultipliedLeading(1.1f)));
        comp2.SetBorderRadius(new BorderRadius(Px(10)));
        doc.Add(FlexRow(Mm(4), new FlexCell(1, comp1), new FlexCell(1, comp2)).SetMarginBottom(Mm(1.5f)));

        doc.Add(H2(F, "2.4 边框即图形"));

        var tri1 = new CanvasBox(pdfDoc, (c, r) => FillPolygon(c, red,
            (r.GetX(), r.GetY()), (r.GetX() + r.GetWidth(), r.GetY()), (r.GetX() + r.GetWidth() / 2, r.GetBottom() + r.GetHeight())))
            .SetWidth(Mm(28)).SetHeight(Mm(18));
        var tri2 = new CanvasBox(pdfDoc, (c, r) => FillPolygon(c, C("#f39c12"),
            (r.GetX(), r.GetBottom() + r.GetHeight()), (r.GetX(), r.GetY()), (r.GetX() + r.GetWidth(), r.GetY())))
            .SetWidth(Mm(16)).SetHeight(Mm(12));
        var tri3 = new CanvasBox(pdfDoc, (c, r) => FillPolygon(c, blue,
            (r.GetX(), r.GetBottom() + r.GetHeight()), (r.GetX() + r.GetWidth(), r.GetBottom() + r.GetHeight()), (r.GetX() + r.GetWidth() / 2, r.GetY())))
            .SetWidth(Mm(28)).SetHeight(Mm(18));
        doc.Add(FixedRow(Mm(3), VerticalAlignment.MIDDLE,
            (Mm(28), tri1),
            (Mm(42), Lbl(F, "border 三角形（等腰）")),
            (Mm(16), tri2),
            (Mm(42), Lbl(F, "直角三角形（右向）")),
            (Mm(28), tri3),
            (Mm(20), Lbl(F, "倒三角"))));
    }

    // =====================================================================
    // PAGE 3
    // =====================================================================
    void BuildPage3(Document doc, Fonts F, PdfDocument pdfDoc, AdapterContext ctx)
    {
        doc.Add(H1(F, "效果、图像与布局"));
        doc.Add(H2(F, "3.1 阴影 box-shadow"));

        // 盒1：高斯模糊阴影 —— iText 无模糊 → 占位灰块 + ✗
        var blur1 = new Div().SetWidth(Mm(36)).SetHeight(Mm(12))
            .SetBackgroundColor(C("#DDDDDD")).SetPaddingLeft(Mm(2.5f)).SetPaddingTop(Mm(2.5f))
            .Add(new Paragraph().SetFontSize(Px(9)).SetFontColor(C("#e74c3c")).SetMultipliedLeading(1.1f)
                .Add(new Text("✗").SetFont(F.Sym)).Add(new Text(" 外阴影 模糊").SetFont(F.Yahei)));
        // 盒2：硬阴影（偏移矩形可原生自绘）
        var hard = new CanvasBox(pdfDoc, (c, r) =>
        {
            float off = Px(5);
            var yc = C("#f1c40f");
            FillRect(c, new Rectangle(r.GetX() + off, r.GetY() - off, r.GetWidth(), r.GetHeight()), yc);
            FillRect(c, r, White);
            StrokeRect(c, r, yc, Px(1));
            DrawText(c, F.Yahei, Px(9), Ink, r.GetX() + Mm(2.5f), r.GetTop() - Mm(2.5f) - Px(9), "硬阴影 无模糊");
        }).SetWidth(Mm(36)).SetHeight(Mm(12));
        // 盒3：内阴影 inset —— 无模糊 → 占位
        var blur3 = new Div().SetWidth(Mm(36)).SetHeight(Mm(12))
            .SetBackgroundColor(C("#DDDDDD")).SetPaddingLeft(Mm(2.5f)).SetPaddingTop(Mm(2.5f))
            .Add(new Paragraph().SetFontSize(Px(9)).SetFontColor(C("#e74c3c")).SetMultipliedLeading(1.1f)
                .Add(new Text("✗").SetFont(F.Sym)).Add(new Text(" 内阴影 inset").SetFont(F.Yahei)));
        // 盒4：三重阴影（spread 环 + inset 底带可画；模糊层占位）
        var triple = new CanvasBox(pdfDoc, (c, r) =>
        {
            FillRect(c, r, White);
            FillRect(c, new Rectangle(r.GetX(), r.GetY(), r.GetWidth(), 4), C("#e8f6f3"));
            StrokeRect(c, r, C("#2980b9"), Px(2));
            DrawText(c, F.Yahei, Px(9), Ink, r.GetX() + Mm(2.5f), r.GetTop() - Mm(2.5f) - Px(9), "三重阴影叠加");
            DrawText(c, F.Sym, 5.5f, C("#e74c3c"), r.GetX() + Mm(2.5f), r.GetTop() - Mm(2.5f) - Px(9) - 9, "✗");
            DrawText(c, F.Yahei, 5.5f, C("#e74c3c"), r.GetX() + Mm(2.5f) + 5.8f, r.GetTop() - Mm(2.5f) - Px(9) - 9, " 模糊层");
        }).SetWidth(Mm(36)).SetHeight(Mm(12));
        doc.Add(FixedRow(Mm(6), VerticalAlignment.MIDDLE,
            (Mm(36), blur1), (Mm(36), hard), (Mm(36), blur3), (Mm(36), triple)).SetMarginLeft(Mm(4)).SetMarginBottom(Mm(1.5f)));

        doc.Add(H2(F, "3.2 渐变与透明"));

        var g1 = new CanvasBox(pdfDoc, (c, r) => FillLinearGradient(c, r, 45,
            (0, Crgb("#e74c3c")), (0.5f, Crgb("#f1c40f")), (1, Crgb("#27ae60")))).SetHeight(Mm(12));
        var g2 = new CanvasBox(pdfDoc, (c, r) => FillLinearGradient(c, r, 90,
            (0, Crgb("#34495e")), (1, Crgb("#ecf0f1")))).SetHeight(Mm(12));
        var g3 = new CanvasBox(pdfDoc, (c, r) => FillRadialGradient(c, r, 0.3, 0.4, Crgb("#ffffff"), Crgb("#2980b9"))).SetHeight(Mm(12));
        // 渐变+alpha：rgba 与白底合成后画（视觉等价）
        var g4 = new CanvasBox(pdfDoc, (c, r) =>
        {
            FillLinearGradient(c, r, 180, (0, BlendOnWhite(Crgb("#e74c3c"), 0.8f)), (1, BlendOnWhite(Crgb("#2980b9"), 0.2f)));
            DrawText(c, F.Yahei, Px(8), White, r.GetX() + 2, r.GetTop() - Px(8) - 3, "渐变+alpha");
        }).SetHeight(Mm(12));
        doc.Add(FlexRow(Mm(3), new FlexCell(1, g1), new FlexCell(1, g2), new FlexCell(1, g3), new FlexCell(1, g4)).SetMarginBottom(Mm(1.5f)));

        var venn = new CanvasBox(pdfDoc, (c, r) =>
        {
            float x = r.GetX(), top = r.GetBottom() + r.GetHeight();
            float s = Mm(18);
            FillRect(c, new Rectangle(x, top - s, s, s), C("#e74c3c"), 0.55f);
            FillRect(c, new Rectangle(x + Mm(7.5f), top - s, s, s), C("#27ae60"), 0.55f);
            FillRect(c, new Rectangle(x + Mm(3.75f), top - Mm(6.5f) - s, s, s), C("#2980b9"), 0.55f);
        }).SetWidth(Mm(46)).SetHeight(Mm(22));
        var opBox = new Div().SetWidth(Mm(30)).SetHeight(Mm(18))
            .SetBackgroundColor(C("#2980b9"), 0.4f)
            .SetPaddingLeft(Mm(2.5f)).SetPaddingTop(Mm(2.5f))
            .Add(new Paragraph("opacity 0.4 整块淡出").SetFont(F.Yahei).SetFontSize(Px(9)).SetFontColor(White, 0.4f).SetMultipliedLeading(1.1f));
        var hlTxt = new Text("行内半透明背景高亮（文字保持全不透明）")
            .SetFont(F.Yahei).SetFontSize(Px(9)).SetFontColor(Ink)
            .SetBackgroundColor(C("#f1c40f"), 0.4f);
        var hlBox = new Div().SetWidth(Mm(34))
            .SetBackgroundColor(C("#f8f9f9")).SetBorder(new SolidBorder(C("#d5d8dc"), Px(1)))
            .SetPaddingLeft(Mm(2)).SetPaddingRight(Mm(2)).SetPaddingTop(Mm(2)).SetPaddingBottom(Mm(2));
        hlBox.Add(new Paragraph().SetFont(F.Yahei).SetFontSize(Px(9)).SetMultipliedLeading(1.35f).Add(hlTxt));
        doc.Add(FixedRow(Mm(3), VerticalAlignment.MIDDLE,
            (Mm(46), venn),
            (Mm(52), Lbl(F, "半透明三色叠加（重叠区混色）")),
            (Mm(30), opBox),
            (Mm(34), hlBox)).SetMarginBottom(Mm(1.5f)));

        doc.Add(H2(F, "3.3 图像"));

        var photo = ImageDataFactory.Create(System.IO.Path.Combine(ctx.AssetsDir, "photo.jpg"));
        var logo = ImageDataFactory.Create(System.IO.Path.Combine(ctx.AssetsDir, "logo-alpha.png"));
        var imgCol1 = new Div();
        imgCol1.Add(Lbl(F, "JPEG 400×267 缩放显示"));
        imgCol1.Add(new Image(photo).ScaleAbsolute(Px(150), Px(100)));
        var imgCol2 = new Div();
        imgCol2.Add(Lbl(F, "PNG alpha 透明通道（深色底验证）"));
        imgCol2.Add(new Div().SetBackgroundColor(C("#2c3e50")).SetPadding(Mm(2))
            .Add(new Image(logo).ScaleAbsolute(Px(120), Px(60))));
        var circImg = new Image(photo).ScaleAbsolute(Px(96), Px(96));
        circImg.SetBorder(new SolidBorder(C("#27ae60"), Px(4)));
        circImg.SetBorderRadius(new BorderRadius(Px(48)));
        var imgCol3 = new Div();
        imgCol3.Add(Lbl(F, "图片圆角+边框复合"));
        imgCol3.Add(circImg);
        doc.Add(FixedRow(Mm(5), VerticalAlignment.MIDDLE,
            (Px(150), imgCol1), (Px(120) + Mm(4), imgCol2), (Px(96) + Px(8), imgCol3)).SetMarginBottom(Mm(1.5f)));

        doc.Add(H2(F, "3.4 Flex 布局"));
        doc.Add(BuildFlex(F, pdfDoc));

        doc.Add(H2(F, "3.5 数据表格"));
        doc.Add(BuildDataTable(F));

        doc.Add(new Paragraph("超长URL自动断行验证：https://example.org/very/long/path/文件与单词混排abcdefghij无空格也可断开以适配窄容器supercalifragilistic 末行。")
            .SetFont(F.Yahei).SetFontSize(Px(9)).SetBackgroundColor(C("#fdf6ec"))
            .SetBorderLeft(new SolidBorder(C("#d35400"), Px(3))).SetPadding(Mm(1.2f))
            .SetMarginTop(Mm(1.5f)).SetMultipliedLeading(1.2f));
    }

    static Div FlexChip(Fonts F, string text, Color bg, Color fg) =>
        new Div().SetBackgroundColor(bg).SetPadding(Mm(1.5f))
            .Add(new Paragraph(text).SetFont(F.Body(text)).SetFontSize(Px(9)).SetFontColor(fg).SetMultipliedLeading(1.1f));

    static Table BuildFlex(Fonts F, PdfDocument pdfDoc)
    {
        float W = ContentW;
        float rightW = W * 0.34f, gap = Mm(4), leftW = W - rightW - gap;
        var outer = new Table(new UnitValue[] { UnitValue.CreatePointValue(leftW), UnitValue.CreatePointValue(gap), UnitValue.CreatePointValue(rightW) });
        outer.SetWidth(W);

        var left = new Div();
        float lw = leftW;
        // 行1：space-between
        float midW = (lw - Mm(34) - Mm(12)) / 2;
        var r1 = new Table(new UnitValue[]
        {
            UnitValue.CreatePointValue(Mm(34)), UnitValue.CreatePointValue(midW),
            UnitValue.CreatePointValue(Mm(12)), UnitValue.CreatePointValue(midW), UnitValue.CreatePointValue(Mm(12))
        });
        r1.SetWidth(lw);
        r1.AddCell(PlainCell(Mm(34), FlexChip(F, "space-between 左", C("#e74c3c"), White)));
        r1.AddCell(PlainCell(midW, new Div()));
        r1.AddCell(PlainCell(Mm(12), FlexChip(F, "中", C("#2980b9"), White), VerticalAlignment.MIDDLE));
        r1.AddCell(PlainCell(midW, new Div()));
        r1.AddCell(PlainCell(Mm(12), FlexChip(F, "右", C("#27ae60"), White), VerticalAlignment.MIDDLE));
        left.Add(r1.SetMarginBottom(Mm(1.5f)));
        // 行2：margin-left:auto
        var r2 = new Table(new UnitValue[] { UnitValue.CreatePointValue(Mm(26)), UnitValue.CreatePointValue(lw - Mm(26) - Mm(42)), UnitValue.CreatePointValue(Mm(42)) });
        r2.SetWidth(lw);
        r2.AddCell(PlainCell(Mm(26), FlexChip(F, "flex-start", C("#8e44ad"), White)));
        r2.AddCell(PlainCell(lw - Mm(26) - Mm(42), new Div()));
        r2.AddCell(PlainCell(Mm(42), FlexChip(F, "margin-left:auto 顶右", C("#d35400"), White)));
        left.Add(r2.SetMarginBottom(Mm(1.5f)));
        // 行3：wrap 47%
        var r3 = new Table(new UnitValue[] { UnitValue.CreatePointValue(lw * 0.47f), UnitValue.CreatePointValue(lw * 0.03f), UnitValue.CreatePointValue(lw * 0.47f) });
        r3.SetWidth(lw);
        r3.AddCell(PlainCell(lw * 0.47f, FlexChip(F, "wrap A", C("#d6eaf8"), Ink)));
        r3.AddCell(PlainCell(lw * 0.03f, new Div()));
        r3.AddCell(PlainCell(lw * 0.47f, FlexChip(F, "wrap B", C("#fadbd8"), Ink)));
        r3.AddCell(PlainCell(lw * 0.47f, FlexChip(F, "wrap C 换行", C("#d5f5e3"), Ink)));
        r3.AddCell(PlainCell(lw * 0.03f, new Div()));
        r3.AddCell(PlainCell(lw * 0.47f, new Div()));
        left.Add(r3.SetMarginBottom(Mm(1.5f)));
        // 行4：嵌套等分列
        var r4 = new Table(new UnitValue[] { UnitValue.CreatePointValue(lw / 2), UnitValue.CreatePointValue(lw / 2) });
        r4.SetWidth(lw);
        r4.AddCell(new Cell().SetPadding(Mm(2)).SetBorder(Border.NO_BORDER).SetBackgroundColor(C("#fdf6ec"))
            .Add(new Paragraph("嵌套：flex:1 等分列左").SetFont(F.Yahei).SetFontSize(Px(8)).SetMultipliedLeading(1.05f)));
        var rc = new Div();
        rc.Add(new Paragraph("右列，列内再套").SetFont(F.Yahei).SetFontSize(Px(8)).SetMultipliedLeading(1.15f));
        var bars = new Table(new UnitValue[] { UnitValue.CreatePointValue(lw / 6), UnitValue.CreatePointValue(lw / 3) });
        bars.SetWidth(lw / 2 - Mm(4));
        bars.AddCell(new Cell().SetHeight(Mm(4)).SetPadding(0).SetBorder(Border.NO_BORDER).SetBackgroundColor(C("#aed6f1")));
        bars.AddCell(new Cell().SetHeight(Mm(4)).SetPadding(0).SetBorder(Border.NO_BORDER).SetBackgroundColor(C("#f5b7b1")));
        rc.Add(bars.SetMarginTop(Mm(1)));
        r4.AddCell(new Cell().SetPadding(Mm(2)).SetBorder(Border.NO_BORDER).SetBackgroundColor(C("#eafaf1")).Add(rc));
        left.Add(r4.SetBorder(new SolidBorder(C("#bdc3c7"), Px(1))));
        outer.AddCell(PlainCell(leftW, left, VerticalAlignment.TOP));
        outer.AddCell(PlainCell(gap, new Div()));

        // 右列：虚线盒 + 居中紫块
        var purple = new Div().SetWidth(rightW * 0.6f).SetHeight(Mm(14))
            .SetBackgroundColor(C("#8e44ad")).SetVerticalAlignment(VerticalAlignment.MIDDLE)
            .Add(new Paragraph("交叉轴居中").SetFont(F.Yahei).SetFontSize(Px(9)).SetFontColor(White)
                .SetTextAlignment(TextAlignment.CENTER).SetMultipliedLeading(1.1f));
        var rightBox = new Div().SetWidth(rightW).SetHeight(Mm(46))
            .SetBackgroundColor(C("#f4ecf7")).SetBorder(new DashedBorder(C("#8e44ad"), Px(1)))
            .SetVerticalAlignment(VerticalAlignment.MIDDLE).SetHorizontalAlignment(HorizontalAlignment.CENTER)
            .Add(purple);
        outer.AddCell(PlainCell(rightW, rightBox, VerticalAlignment.TOP));
        return outer;
    }

    static Table BuildDataTable(Fonts F)
    {
        var t = new Table(UnitValue.CreatePercentArray(new float[] { 12, 40, 20, 28 }));
        t.SetWidth(UnitValue.CreatePercentValue(100));
        var hdrB = new SolidBorder(C("#5d6d7e"), 0.75f);
        var cellB = new SolidBorder(C("#d5d8dc"), 0.75f);
        void Header(string s, TextAlignment al)
        {
            t.AddCell(new Cell().SetBackgroundColor(C("#34495e")).SetBorder(hdrB)
                .SetPaddingLeft(Mm(2)).SetPaddingRight(Mm(2)).SetPaddingTop(Mm(0.8f)).SetPaddingBottom(Mm(0.8f))
                .Add(new Paragraph(s).SetFont(F.Body(s)).SetFontSize(Px(8.5f)).SetFontColor(White).SetTextAlignment(al).SetMultipliedLeading(1.05f)));
        }
        Header("编号 Code", TextAlignment.LEFT);
        Header("名称 Name", TextAlignment.LEFT);
        Header("数量 Qty", TextAlignment.RIGHT);
        Header("金额 Amount (¥)", TextAlignment.RIGHT);

        string[][] rows =
        {
            new[] { "A-001", "工业级渲染管线 Industrial pipeline", "12", "1,280.00" },
            new[] { "A-002", "矢量字体子集 Vector subset", "3", "96.50" },
            new[] { "B-014", "分页引擎 Pagination engine", "1", "2,400.00" },
            new[] { "B-015", "阴影合成器 Shadow compositor", "8", "640.00" },
            new[] { "C-101", "渐变缓存 Gradient cache", "24", "1,152.00" },
            new[] { "C-102", "Alpha 通道混合 Alpha blending", "6", "306.00" },
            new[] { "D-200", "CJK 换行分析 Line-break CJK", "2", "780.00" },
        };
        for (int i = 0; i < rows.Length; i++)
        {
            Color? bg = i % 2 == 1 ? C("#f8f9f9") : null;
            for (int j = 0; j < 4; j++)
            {
                var txt = rows[i][j];
                var al = j >= 2 ? TextAlignment.RIGHT : TextAlignment.LEFT;
                var cell = new Cell().SetBorder(cellB)
                    .SetPaddingLeft(Mm(2)).SetPaddingRight(Mm(2)).SetPaddingTop(Mm(1)).SetPaddingBottom(Mm(1));
                if (bg != null) cell.SetBackgroundColor(bg);
                var f = j == 0 ? F.Bold(txt) : F.Body(txt);
                cell.Add(new Paragraph(txt).SetFont(f).SetFontSize(Px(8.5f)).SetTextAlignment(al).SetMultipliedLeading(1.15f));
                t.AddCell(cell);
            }
        }
        Color footBg = C("#eaeded");
        t.AddCell(new Cell(1, 2).SetBorder(cellB).SetBackgroundColor(footBg)
            .SetPaddingLeft(Mm(2)).SetPaddingRight(Mm(2)).SetPaddingTop(Mm(0.8f)).SetPaddingBottom(Mm(0.8f))
            .Add(new Paragraph("合计 Total (7 行明细)").SetFont(F.YaheiB).SetFontSize(Px(8.5f)).SetMultipliedLeading(1.05f)));
        t.AddCell(new Cell().SetBorder(cellB).SetBackgroundColor(footBg)
            .SetPaddingLeft(Mm(2)).SetPaddingRight(Mm(2)).SetPaddingTop(Mm(0.8f)).SetPaddingBottom(Mm(0.8f))
            .Add(new Paragraph("56").SetFont(F.SegoeB).SetFontSize(Px(8.5f)).SetTextAlignment(TextAlignment.RIGHT).SetMultipliedLeading(1.05f)));
        t.AddCell(new Cell().SetBorder(cellB).SetBackgroundColor(footBg)
            .SetPaddingLeft(Mm(2)).SetPaddingRight(Mm(2)).SetPaddingTop(Mm(0.8f)).SetPaddingBottom(Mm(0.8f))
            .Add(new Paragraph("¥ 6,654.50").SetFont(F.YaheiB).SetFontSize(Px(8.5f)).SetTextAlignment(TextAlignment.RIGHT).SetMultipliedLeading(1.05f)));
        return t;
    }
}
