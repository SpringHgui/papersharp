using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PdfBench.Runner.Adapters;

/// <summary>
/// QuestPDF (2025.7.0) adapter recreating spec/challenge.html (3 A4 pages) block by block.
/// Feature mapping (vs. data-f in challenge.html):
///   ✅ native: solid borders (uniform / single-side), uniform & per-corner radius,
///     linear gradients, box-shadow blur & hard (Shadow API), weights/underline/strike/
///     small-caps(OpenType)/letter-spacing/line-height/first-line-indent/justify, images
///     (jpeg, png-alpha), table (header/zebra/colspan), pill & circle radius (incl.
///     native image clipping via CornerRadius), flex via Row/Column, currentColor
///     border, inline code chips (injected element).
///   🟡 approximated via dynamic .Svg() (the sanctioned custom-draw API; .Canvas throws
///     since 2024.3): dashed/dotted/double borders, per-side heterogeneous borders &
///     colors, groove/ridge (flat 2-tone), true ellipse, dashed rounded box, border
///     triangles, linear/radial gradients (QuestPDF's own angle is unit-square
///     distorted), rgba overlap blend, text-shadow (offset copy), whole-block opacity,
///     overflow clip radius.
///   ❌ placeholder (#DDDDDD + red note): inset box-shadow; word-break:break-all
///     mid-token (QuestPDF breaks long tokens only at boundaries).
/// </summary>
public sealed class QuestPdfAdapter : IPdfAdapter
{
    public string Name => "questpdf";

    private static readonly string[] Fam = { "Segoe UI", "Microsoft YaHei" };

    // ---- unit / color helpers -------------------------------------------------
    private static float F(float px) => px * 0.75f;            // CSS px -> pt
    private static float Mm(float mm) => mm * 72f / 25.4f;     // CSS mm -> pt
    private static Color C(string hex) => Color.FromHex(hex);
    private static Color CA(string hex, float alpha) => Color.FromHex(hex).WithAlpha(alpha);
    private static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Hex(Color c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
    private static string SvgDoc(Size s, string body) =>
        $"<svg xmlns='http://www.w3.org/2000/svg' width='{N((float)s.Width)}' height='{N((float)s.Height)}'>{body}</svg>";

    /// <summary>CSS linear-gradient faithful emulation (QuestPDF's own angle is unit-square distorted).
    /// stops: (offset 0..1, #hex color, opacity 0..1).</summary>
    private static string GradSvg(Size s, double angleCss, (double off, string col, double op)[] stops)
    {
        double a = angleCss * Math.PI / 180, w = s.Width, h = s.Height;
        double dx = Math.Sin(a), dy = -Math.Cos(a);
        double len = Math.Abs(w * Math.Sin(a)) + Math.Abs(h * Math.Cos(a));
        double x1 = w / 2 - dx * len / 2, y1 = h / 2 - dy * len / 2;
        double x2 = w / 2 + dx * len / 2, y2 = h / 2 + dy * len / 2;
        var defs = string.Concat(stops.Select(st =>
            $"<stop offset='{N((float)st.off)}' stop-color='{st.col}' stop-opacity='{N((float)st.op)}'/>"));
        return SvgDoc(s,
            $"<defs><linearGradient id='lg' gradientUnits='userSpaceOnUse' x1='{N((float)x1)}' y1='{N((float)y1)}' x2='{N((float)x2)}' y2='{N((float)y2)}'>{defs}</linearGradient></defs>" +
            $"<rect width='{N((float)w)}' height='{N((float)h)}' fill='url(#lg)'/>");
    }

    // ---- entry point ------------------------------------------------------------
    public void Generate(AdapterContext ctx, string outPdfPath)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        Document.Create(doc =>
        {
            Page1(doc, ctx);
            Page2(doc, ctx);
            Page3(doc, ctx);
        }).GeneratePdf(outPdfPath);
    }

    // ==========================================================================
    //  page frame (hdr / ftr shared by all pages)
    // ==========================================================================
    private static void Frame(IDocumentContainer container, string tag, int pageNo, Action<ColumnDescriptor> content)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(0);
            page.DefaultTextStyle(t => t.FontFamily(Fam).FontSize(F(10.5f)).FontColor(C("#222222")));

            page.Header()
                .PaddingTop(Mm(12)).PaddingHorizontal(Mm(14)).PaddingBottom(Mm(2))
                .Column(h =>
                {
                    h.Item().Row(r =>
                    {
                        r.RelativeItem(1).Text(t =>
                            t.Span("PDFBENCH · VISUAL CHALLENGE")
                             .FontColor(C("#34495e")).FontSize(F(13)).Bold().LetterSpacing(1f / 13f));
                        r.AutoItem().AlignBottom().Text(t =>
                            t.Span(tag).FontColor(C("#7f8c8d")).FontSize(F(10)));
                    });
                    h.Item().PaddingTop(Mm(2)).LineHorizontal(F(2)).LineColor(C("#34495e"));
                });

            page.Content()
                .PaddingHorizontal(Mm(14)).PaddingTop(Mm(2)).PaddingBottom(Mm(2))
                .Column(c => content(c));

            page.Footer()
                .PaddingHorizontal(Mm(14)).PaddingBottom(Mm(4))
                .Column(f =>
                {
                    f.Item().LineHorizontal(0.75f).LineColor(C("#bdc3c7"));
                    f.Item().PaddingTop(Mm(1.5f)).Row(r =>
                    {
                        r.RelativeItem(1).AlignMiddle().Text(t =>
                        {
                            t.AlignCenter();
                            t.Span("PdfBench · challenge.html · generated by spec v0.1")
                             .FontColor(C("#7f8c8d")).FontSize(F(9));
                        });
                        r.AutoItem().Width(F(34)).Height(F(16))
                            .Border(F(1), C("#34495e")).CornerRadius(F(8))
                            .AlignMiddle().Text(t =>
                            {
                                t.AlignCenter();
                                t.Span($"{pageNo} / 3").FontColor(C("#34495e")).FontSize(F(9)).Bold();
                            });
                    });
                });
        });
    }

    // shared typography blocks ----------------------------------------------------
    private static void H1(ColumnDescriptor col, string text) =>
        col.Item().PaddingBottom(Mm(3)).Text(t =>
            t.Span(text).FontSize(F(20)).Bold().FontColor(C("#34495e")));

    private static void H2(ColumnDescriptor col, string text) =>
        col.Item().PaddingTop(Mm(3.5f)).PaddingBottom(Mm(2))
            .BorderLeft(F(4)).BorderColor(C("#3498db")).PaddingLeft(F(6))
            .Text(t => t.Span(text).FontSize(F(14)).Bold().FontColor(C("#2c3e50")));

    private static void Lbl(ColumnDescriptor col, string text) =>
        col.Item().PaddingBottom(0.75f).Text(t =>
            t.Span(text).FontSize(F(8)).FontColor(C("#95a5a6")));

    // ==========================================================================
    //  Svg border drawing (dashed / dotted / double / per-side heterogeneous)
    // ==========================================================================
    private enum Bs { Solid, Dashed, Dotted, Double }

    private static string Edge(string x1, string y1, string x2, string y2, float w, Bs st, Color col)
    {
        var s = Hex(col);
        if (st == Bs.Double)
        {
            var t = w / 3f;
            string L(string a, string b, string c, string d) =>
                $"<line x1='{a}' y1='{b}' x2='{c}' y2='{d}' stroke='{s}' stroke-width='{N(t)}'/>";
            if (x1 == x2) // vertical edge
            {
                float x = float.Parse(x1, CultureInfo.InvariantCulture);
                return L(N(x - w / 2 + t / 2), y1, N(x - w / 2 + t / 2), y2) +
                       L(N(x + w / 2 - t / 2), y1, N(x + w / 2 - t / 2), y2);
            }
            float y = float.Parse(y1, CultureInfo.InvariantCulture);
            return L(x1, N(y - w / 2 + t / 2), x2, N(y - w / 2 + t / 2)) +
                   L(x1, N(y + w / 2 - t / 2), x2, N(y + w / 2 - t / 2));
        }
        var dash = st switch
        {
            Bs.Dashed => $" stroke-dasharray='{N(w * 3f)}, {N(w * 2.4f)}'",
            Bs.Dotted => $" stroke-dasharray='0.01, {N(w * 1.7f)}' stroke-linecap='round'",
            _ => ""
        };
        return $"<line x1='{x1}' y1='{y1}' x2='{x2}' y2='{y2}' stroke='{s}' stroke-width='{N(w)}'{dash}/>";
    }

    private static string BorderBody(float w, float h, float[] bw, Bs[] st, Color[] col, float radius)
    {
        bool uniform = bw[0] == bw[1] && bw[1] == bw[2] && bw[2] == bw[3]
                       && st[0] == st[1] && st[1] == st[2] && st[2] == st[3]
                       && col[0].Hex == col[1].Hex && col[1].Hex == col[2].Hex && col[2].Hex == col[3].Hex;
        if (uniform && radius > 0)
        {
            var dash = st[0] switch
            {
                Bs.Dashed => $" stroke-dasharray='{N(bw[0] * 3f)}, {N(bw[0] * 2.4f)}'",
                Bs.Dotted => $" stroke-dasharray='0.01, {N(bw[0] * 1.7f)}' stroke-linecap='round'",
                _ => ""
            };
            var t = bw[0];
            return $"<rect x='{N(t / 2)}' y='{N(t / 2)}' width='{N(w - t)}' height='{N(h - t)}' rx='{N(radius)}' ry='{N(radius)}' fill='none' stroke='{Hex(col[0])}' stroke-width='{N(t)}'{dash}/>";
        }
        return
            Edge("0", N(bw[0] / 2), N(w), N(bw[0] / 2), bw[0], st[0], col[0]) +
            Edge(N(w - bw[1] / 2), "0", N(w - bw[1] / 2), N(h), bw[1], st[1], col[1]) +
            Edge("0", N(h - bw[2] / 2), N(w), N(h - bw[2] / 2), bw[2], st[2], col[2]) +
            Edge(N(bw[3] / 2), "0", N(bw[3] / 2), N(h), bw[3], st[3], col[3]);
    }

    /// <summary>Box whose border is drawn via dynamic Svg; border space reserved with padding.</summary>
    private static void SvgBox(IContainer item, float[] bwPx, Bs[] st, Color[] col, float radiusPx,
        Color? bg, float padT, float padR, float padB, float padL, Action<IContainer> content)
    {
        var bw = new[] { F(bwPx[0]), F(bwPx[1]), F(bwPx[2]), F(bwPx[3]) };
        item.Layers(l =>
        {
            var p = l.PrimaryLayer();
            if (bg.HasValue) p = p.Background(bg.Value);
            content(p
                .PaddingTop(bw[0] + padT).PaddingRight(bw[1] + padR)
                .PaddingBottom(bw[2] + padB).PaddingLeft(bw[3] + padL));
            l.Layer().Svg(size =>
                SvgDoc(size, BorderBody((float)size.Width, (float)size.Height, bw, st, col, F(radiusPx))));
        });
    }

    private static void SvgBox(IContainer item, float bwPx, Bs st, Color col, float radiusPx,
        Color? bg, float padT, float padR, float padB, float padL, Action<IContainer> content) =>
        SvgBox(item, new[] { bwPx, bwPx, bwPx, bwPx }, new[] { st, st, st, st }, new[] { col, col, col, col },
            radiusPx, bg, padT, padR, padB, padL, content);

    // ==========================================================================
    //  PAGE 1 — typography & text
    // ==========================================================================
    private static void Page1(IDocumentContainer doc, AdapterContext ctx)
    {
        Frame(doc, "Page 1 — Typography & Text", 1, col =>
        {
            H1(col, "排版与文字 Rendering Test");

            // ---- 1.1 headings & weights ----------------------------------------
            H2(col, "1.1 层级与字重");
            col.Item().Row(r =>
            {
                r.AutoItem().Text(t => t.Span("H3 subtitle").FontSize(F(11)).Bold().FontColor(C("#7f8c8d")));
                r.RelativeItem(1).AlignRight().AlignBottom().Text(t =>
                    t.Span("h3 → h1 右对齐抬升").FontSize(F(9)).FontColor(C("#95a5a6")));
            });
            col.Item().PaddingTop(Mm(1)).Text(t =>
            {
                t.Span("字重：");
                t.Span("粗体 bold").Bold();
                t.Span("、");
                t.Span("斜体 italic").Italic(true);
                t.Span("、");
                t.Span("粗斜体").Bold().Italic(true);
                t.Span("、");
                t.Span("细体 light 300").Light();
                t.Span("、");
                t.Span("下划线").Underline(true);
                t.Span("、");
                t.Span("删除线").Strikethrough(true);
                t.Span("、");
                t.Span("Small Caps 效果").EnableFontFeature(FontFeatures.SmallCapitals);
                t.Span("。");
            });
            col.Item().PaddingTop(Mm(2.5f)).Text(t =>
            {
                t.Span("行内代码芯片：");
                CodeChip(t, "PdfGenerator.Render()", "#f3e5f5", "#8e24aa", "#ce93d8");
                t.Span(" 与 ");
                CodeChip(t, "spec.json", "#e3f2fd", "#1565c0", null);
                t.Span(" 混排于行文中。");
            });

            // ---- 1.2 cjk / line-height / letter-spacing / indent ----------------
            H2(col, "1.2 中西混排与行高");
            col.Item().Text(t =>
            {
                t.DefaultTextStyle(x => x.LineHeight(1.8f));
                t.Span("中文段落混合 English words 与数字 0123456789，标点，以及括号（半角）与全角符号。 中英文基线对齐是渲染引擎的常见短板，The quick brown fox 应跳 over the lazy dog 而不断行错位。");
            });
            Lbl(col, "line-height: 1.0");
            LineDemo(col, 1.0f, "密集行距示例 The quick brown fox jumps over the lazy dog 中文密集行示例，行与行可能粘连。", true);
            Lbl(col, "line-height: 1.4");
            LineDemo(col, 1.4f, "正常行距示例 The quick brown fox jumps over the lazy dog 中文正常行距示例，阅读舒适。", true);
            Lbl(col, "line-height: 2.2");
            LineDemo(col, 2.2f, "宽松行距示例 The quick brown fox 中文宽松行距示例。", false);

            col.Item().PaddingTop(Mm(2)).Column(ls =>
            {
                ls.Item().Text(t => { t.DefaultTextStyle(x => x.FontSize(F(12))); t.Span("normal: tracking 字距"); });
                ls.Item().Text(t => { t.DefaultTextStyle(x => x.FontSize(F(12)).LetterSpacing(2f / 12f)); t.Span("wide 2px: tracking 字距"); });
                ls.Item().Text(t => { t.DefaultTextStyle(x => x.FontSize(F(12)).LetterSpacing(5f / 12f)); t.Span("wider 5px: tracking 字距"); });
            });
            col.Item().PaddingTop(Mm(2.5f)).Text(t =>
            {
                t.ParagraphFirstLineIndentation(F(21), Unit.Point); // 2em of 10.5px
                t.Span("首行缩进两个字符：这是段落首行缩进的验证文本，缩进应恰好为 2 个全角字符宽度且仅作用于首行。");
            });

            // ---- 1.3 text effects & alignment ------------------------------------
            H2(col, "1.3 文字效果与对齐");
            col.Item().Layers(l =>
            {
                l.PrimaryLayer().TranslateX(F(2)).TranslateY(F(2))
                    .Text(t => t.Span("阴影文字 Text Shadow").FontSize(F(16)).Bold().FontColor(C("#f5b7b1")));
                l.Layer().Text(t => t.Span("阴影文字 Text Shadow").FontSize(F(16)).Bold().FontColor(C("#2c3e50")));
            });
            col.Item().PaddingTop(Mm(2.5f)).Text(t =>
            {
                t.Span("红").FontColor(C("#e74c3c"));
                t.Span(" ");
                t.Span("绿").FontColor(C("#27ae60"));
                t.Span(" ");
                t.Span("蓝").FontColor(C("#2980b9"));
                t.Span(" ");
                t.Span("橙").FontColor(C("#f39c12"));
                t.Span(" ");
                t.Span("紫").FontColor(C("#8e44ad"));
                t.Span(" —— 五色文字，纯色无渐变。");
            });
            col.Item().PaddingTop(Mm(2.5f)).Row(r =>
            {
                TintCell(r.RelativeItem(1), "左对齐 left 文", 0);
                TintCell(r.RelativeItem(1), "居中 center 文", 1);
                TintCell(r.RelativeItem(1), "右对齐 right 文", 2);
                TintCell(r.RelativeItem(1.6f), "两端对齐 justify：This paragraph is justified so that both left and right edges form clean lines across its full width 无论中英文。", 3);
            });

            // ---- 1.4 lists ----------------------------------------------------------
            H2(col, "1.4 列表与嵌套");
            col.Item().Row(r =>
            {
                r.RelativeItem(1).Column(c1 =>
                {
                    Lbl(c1, "unordered disc，marker 着色");
                    c1.Item().Column(u =>
                    {
                        Li(u, "•", C("#e74c3c"), 0, t =>
                        {
                            t.Span("项目一：").FontColor(C("#e74c3c"));
                            t.Span("无序列表 disc 符号").FontColor(C("#2c3e50"));
                        });
                        Li(u, "•", C("#2c3e50"), 0, t => t.Span("项目二：Marker 颜色独立于文字").FontColor(C("#2c3e50")));
                        Li(u, "•", C("#2c3e50"), 0, t => t.Span("项目三：三级嵌套入口").FontColor(C("#2c3e50")));
                        Li(u, "◦", C("#2c3e50"), F(11), t => t.Span("嵌套层 A").FontColor(C("#2c3e50")));
                        Li(u, "▪", C("#2c3e50"), F(22), t => t.Span("方块 square 层").FontColor(C("#2c3e50")));
                    });
                });
                r.RelativeItem(1).PaddingLeft(Mm(4)).Column(c2 =>
                {
                    Lbl(c2, "ordered decimal / lower-alpha / upper-roman");
                    c2.Item().Column(o =>
                    {
                        Li(o, "1.", C("#2c3e50"), 0, t => t.Span("十进制 ordered").FontColor(C("#2c3e50")));
                        Li(o, "2.", C("#2c3e50"), 0, t => t.Span("第二项").FontColor(C("#2c3e50")));
                        Li(o, "a.", C("#2c3e50"), F(11), t => t.Span("lower-alpha 子项").FontColor(C("#2c3e50")));
                        Li(o, "b.", C("#2c3e50"), F(11), t => t.Span("子项二").FontColor(C("#2c3e50")));
                        Li(o, "I.", C("#2c3e50"), 0, t => t.Span("upper-roman 独立编号").FontColor(C("#2c3e50")));
                        Li(o, "II.", C("#2c3e50"), 0, t => t.Span("应为 II").FontColor(C("#2c3e50")));
                    });
                });
                r.RelativeItem(1).PaddingLeft(Mm(4)).Column(c3 =>
                {
                    Lbl(c3, "自定义 marker：circle + 无标记");
                    c3.Item().Column(u =>
                    {
                        Li(u, "◦", C("#2c3e50"), 0, t => t.Span("circle 空心圈").FontColor(C("#2c3e50")));
                        Li(u, null, C("#2c3e50"), 0, t => t.Span("none：无标记但有缩进对齐").FontColor(C("#2c3e50")));
                        Li(u, "◦", C("#2c3e50"), 0, t => t.Span("circle 第二项").FontColor(C("#2c3e50")));
                    });
                });
            });
        });
    }

    private static void CodeChip(TextDescriptor t, string code, string bg, string fg, string? border)
    {
        t.Element(e =>
        {
            var box = e.Background(C(bg)).CornerRadius(F(3)).PaddingHorizontal(F(4));
            if (border != null) box = box.Border(F(1), C(border));
            box.Text(x => x.Span(code).FontFamily("Consolas", "Microsoft YaHei").FontSize(F(9.5f)).FontColor(C(fg)));
        }, TextInjectedElementAlignment.Middle);
    }

    private static void LineDemo(ColumnDescriptor col, float lineHeight, string text, bool marginBottom)
    {
        col.Item().Background(C("#fdf6ec")).Text(t =>
        {
            t.DefaultTextStyle(x => x.LineHeight(lineHeight));
            t.Span(text);
        });
        if (marginBottom) col.Item().PaddingBottom(Mm(1));
    }

    private static void TintCell(IContainer item, string text, int align)
    {
        item.Background(C("#ecf0f1")).Padding(Mm(2.5f)).Text(t =>
        {
            switch (align)
            {
                case 1: t.AlignCenter(); break;
                case 2: t.AlignRight(); break;
                case 3: t.Justify(); break;
                default: t.AlignLeft(); break;
            }
            t.DefaultTextStyle(x => x.FontSize(F(9)));
            t.Span(text);
        });
    }

    /// <summary>Manual list item: [indent][marker][text] (QuestPDF has no styled markers).</summary>
    private static void Li(ColumnDescriptor col, string? marker, Color markerColor, float extraIndent, Action<TextDescriptor> text)
    {
        col.Item().Row(r =>
        {
            if (extraIndent > 0) r.ConstantItem(extraIndent, Unit.Point);
            if (marker != null)
                r.ConstantItem(F(13), Unit.Point).Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(F(10)));
                    t.Span(marker).FontColor(markerColor);
                });
            r.RelativeItem(1).Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(F(10)));
                text(t);
            });
        });
    }

    // ==========================================================================
    //  PAGE 2 — borders & corners
    // ==========================================================================
    private static void Page2(IDocumentContainer doc, AdapterContext ctx)
    {
        Frame(doc, "Page 2 — Borders & Corners", 2, col =>
        {
            H1(col, "边框家族与圆角");

            // ---- 2.1 four styles x three widths -----------------------------------
            H2(col, "2.1 四种线型 × 三种宽度");
            var cp = Mm(2.5f);
            Action<IContainer> Cell9(string text) => c =>
                c.Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9))); t.Span(text); });

            col.Item().Row(r =>
            {
                foreach (var (w, txt) in new[] { (2f, "solid 2px"), (4f, "solid 4px"), (8f, "solid 8px") })
                    r.AutoItem().PaddingRight(Mm(4)).Border(F(w), C("#e74c3c")).Padding(cp)
                        .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9))); t.Span(txt); });
            });
            col.Item().PaddingTop(Mm(1.5f)).Row(r =>
            {
                foreach (var (w, txt) in new[] { (2f, "dashed 2px"), (4f, "dashed 4px"), (8f, "dashed 4px+") })
                    SvgBox(r.AutoItem().PaddingRight(Mm(4)), w, Bs.Dashed, C("#2980b9"), 0, null, cp, cp, cp, cp, Cell9(txt));
            });
            col.Item().PaddingTop(Mm(1.5f)).Row(r =>
            {
                SvgBox(r.AutoItem().PaddingRight(Mm(4)), 2, Bs.Dotted, C("#27ae60"), 0, null, cp, cp, cp, cp, Cell9("dotted 2px"));
                SvgBox(r.AutoItem().PaddingRight(Mm(4)), 4, Bs.Dotted, C("#27ae60"), 0, null, cp, cp, cp, cp, Cell9("dotted 4px"));
                SvgBox(r.AutoItem().PaddingRight(Mm(4)), 8, Bs.Double, C("#8e44ad"), 0, null, cp, cp, cp, cp, Cell9("double 8px"));
            });
            col.Item().PaddingTop(Mm(1.5f)).Row(r =>
            {
                SvgBox(r.AutoItem().PaddingRight(Mm(4)), 4, Bs.Double, C("#8e44ad"), 0, null, cp, cp, cp, cp, Cell9("double 4px"));
                // groove: top/left dark, bottom/right light (flat 2-tone approximation)
                SvgBox(r.AutoItem().PaddingRight(Mm(4)), new[] { 4f, 4f, 4f, 4f }, new[] { Bs.Solid, Bs.Solid, Bs.Solid, Bs.Solid },
                    new[] { C("#a04000"), C("#eb984e"), C("#eb984e"), C("#a04000") },
                    0, null, cp, cp, cp, cp, Cell9("groove 4px"));
                // ridge: top/left light, bottom/right dark
                SvgBox(r.AutoItem().PaddingRight(Mm(4)), new[] { 4f, 4f, 4f, 4f }, new[] { Bs.Solid, Bs.Solid, Bs.Solid, Bs.Solid },
                    new[] { C("#76d7c4"), C("#0e6655"), C("#0e6655"), C("#76d7c4") },
                    0, null, cp, cp, cp, cp, Cell9("ridge 4px"));
            });

            // ---- 2.2 per-side heterogeneous -----------------------------------------
            H2(col, "2.2 逐边异构");
            col.Item().Row(r =>
            {
                SvgBox(r.RelativeItem(1), new[] { 3f, 3f, 3f, 4f },
                    new[] { Bs.Solid, Bs.Dashed, Bs.Dotted, Bs.Double },
                    new[] { C("#2980b9"), C("#e74c3c"), C("#27ae60"), C("#f39c12") },
                    0, null, Mm(5), Mm(3), Mm(5), Mm(3), Cell9("四边四线型：上实/右虚/下点/左双"));

                SvgBox(r.RelativeItem(1).PaddingLeft(Mm(4)), new[] { 3f, 3f, 3f, 3f },
                    new[] { Bs.Solid, Bs.Solid, Bs.Solid, Bs.Solid },
                    new[] { C("#e74c3c"), C("#2980b9"), C("#27ae60"), C("#f39c12") },
                    0, null, Mm(5), Mm(3), Mm(5), Mm(3), Cell9("四边同线型异色：上红/右蓝/下绿/左橙"));

                r.RelativeItem(1).PaddingLeft(Mm(4)).Border(F(2), C("#c0392b")).PaddingTop(Mm(5)).PaddingBottom(Mm(5)).PaddingHorizontal(Mm(3))
                    .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9)).FontColor(C("#c0392b"))); t.Span("currentColor 边框：边色随字色变红"); });
            });
            col.Item().PaddingTop(Mm(1.5f)).Row(r =>
            {
                r.RelativeItem(1).Background(C("#ecf0f1")).BorderBottom(F(3)).BorderColor(C("#8e44ad"))
                    .PaddingTop(Mm(2)).PaddingBottom(Mm(2)).PaddingHorizontal(Mm(3))
                    .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9))); t.Span("仅底边：文章式分隔线"); });
                r.RelativeItem(1).PaddingLeft(Mm(4)).Background(C("#ecf0f1")).BorderLeft(F(5)).BorderColor(C("#27ae60"))
                    .PaddingTop(Mm(2)).PaddingBottom(Mm(2)).PaddingHorizontal(Mm(3))
                    .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9))); t.Span("仅左边：引文强调条"); });
                r.RelativeItem(1).PaddingLeft(Mm(4)).BorderTop(F(2)).BorderBottom(F(2)).BorderColor(C("#2980b9"))
                    .PaddingTop(Mm(2)).PaddingBottom(Mm(2)).PaddingHorizontal(Mm(3))
                    .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9))); t.Span("上下边：band 样式"); });
            });

            // ---- 2.3 radius -------------------------------------------------------------
            H2(col, "2.3 圆角");
            col.Item().AlignMiddle().Row(r =>
            {
                RadiusBox(r, "r=0", 0, 0, 0, 0);
                RadiusBox(r, "4px", F(4), F(4), F(4), F(4));
                RadiusBox(r, "10px", F(10), F(10), F(10), F(10));
                RadiusBox(r, "8/2 对角", F(8), F(2), F(8), F(2));
                RadiusBox(r, "四角异值", F(14), F(2), F(14), F(2));
            });
            col.Item().PaddingTop(Mm(2)).AlignMiddle().Row(r =>
            {
                // 50% circle (native radius = w/2)
                r.AutoItem().Width(Mm(18)).Height(Mm(18))
                    .Background(C("#fadbd8")).Border(F(2), C("#c0392b")).CornerRadius(Mm(9));
                r.AutoItem().PaddingHorizontal(Mm(2))
                    .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(8)).FontColor(C("#95a5a6"))); t.Span("50% 正圆"); });
                // 50% ellipse (true ellipse -> Svg; radius API can only produce a pill)
                r.AutoItem().Width(Mm(11)).Height(Mm(18)).Svg(size =>
                    SvgDoc(size, $"<ellipse cx='{N((float)size.Width / 2)}' cy='{N((float)size.Height / 2)}' rx='{N((float)size.Width / 2 - 0.75f)}' ry='{N((float)size.Height / 2 - 0.75f)}' fill='{Hex(C("#d5f5e3"))}' stroke='{Hex(C("#27ae60"))}' stroke-width='1.5'/>"));
                r.AutoItem().PaddingHorizontal(Mm(2))
                    .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(8)).FontColor(C("#95a5a6"))); t.Span("50% 椭圆"); });
                // pill (native radius = h/2)
                r.AutoItem().Width(Mm(34)).Height(Mm(10))
                    .Background(C("#fdebd0")).Border(F(3), C("#d35400")).CornerRadius(Mm(5))
                    .AlignMiddle().Text(t =>
                    {
                        t.AlignCenter();
                        t.DefaultTextStyle(x => x.FontSize(F(9)));
                        t.Span("胶囊 pill r=高度/2");
                    });
                // radius + dashed compound (Svg)
                SvgBox(r.AutoItem().PaddingLeft(Mm(4)).Width(Mm(26)).Height(Mm(18)), 3, Bs.Dashed, C("#8e44ad"), 10, C("#d6eaf8"), 0, 0, 0, 0, _ => { });
                r.AutoItem().PaddingLeft(Mm(2))
                    .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(8)).FontColor(C("#95a5a6"))); t.Span("圆角+虚线复合"); });
            });
            col.Item().PaddingTop(Mm(2)).Row(r =>
            {
                // thick border + big radius + bg + padding compound (all native)
                r.RelativeItem(1).Border(F(6), C("#34495e")).CornerRadius(F(14)).Background(C("#f8f9f9")).Padding(Mm(3))
                    .Text(t =>
                    {
                        t.DefaultTextStyle(x => x.FontSize(F(9)));
                        t.Span("厚边框(6px)+大圆角(14px)+内边距+背景色 复合盒：边框与背景的圆角必须同心，内边距把内容从边框推开。");
                    });
                // overflow:hidden clip over rounded corners: header gets matching inner radius
                r.RelativeItem(1).PaddingLeft(Mm(4)).Border(F(2), C("#27ae60")).CornerRadius(F(10)).Column(b =>
                {
                    b.Item().Background(C("#27ae60")).CornerRadiusTopLeft(F(8)).CornerRadiusTopRight(F(8))
                        .PaddingTop(Mm(2)).PaddingBottom(Mm(2)).PaddingHorizontal(Mm(3))
                        .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9)).FontColor(C("#ffffff")).Bold()); t.Span("裁切过头圆角"); });
                    b.Item().PaddingTop(Mm(2)).PaddingBottom(Mm(2)).PaddingHorizontal(Mm(3))
                        .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9))); t.Span("overflow:hidden 子元素背景填满左上圆角"); });
                });
            });

            // ---- 2.4 border-as-shape (CSS border triangles -> Svg polygons) --------------
            H2(col, "2.4 边框即图形");
            col.Item().AlignMiddle().Row(r =>
            {
                r.AutoItem().Width(Mm(28)).Height(Mm(18)).Svg(size =>
                    SvgDoc(size, $"<polygon points='0,{N((float)size.Height)} {N((float)size.Width / 2)},0 {N((float)size.Width)},{N((float)size.Height)}' fill='{Hex(C("#e74c3c"))}'/>"));
                r.AutoItem().PaddingLeft(Mm(3)).Text(t =>
                { t.DefaultTextStyle(x => x.FontSize(F(8)).FontColor(C("#95a5a6"))); t.Span("border 三角形（等腰）"); });
                r.AutoItem().PaddingLeft(Mm(3)).Width(Mm(16)).Height(Mm(12)).Svg(size =>
                    SvgDoc(size, $"<polygon points='0,0 0,{N((float)size.Height)} {N((float)size.Width)},{N((float)size.Height)}' fill='{Hex(C("#f39c12"))}'/>"));
                r.AutoItem().PaddingLeft(Mm(3)).Text(t =>
                { t.DefaultTextStyle(x => x.FontSize(F(8)).FontColor(C("#95a5a6"))); t.Span("直角三角形（右向）"); });
                r.AutoItem().PaddingLeft(Mm(3)).Width(Mm(28)).Height(Mm(18)).Svg(size =>
                    SvgDoc(size, $"<polygon points='0,0 {N((float)size.Width)},0 {N((float)size.Width / 2)},{N((float)size.Height)}' fill='{Hex(C("#2980b9"))}'/>"));
                r.AutoItem().PaddingLeft(Mm(3)).Text(t =>
                { t.DefaultTextStyle(x => x.FontSize(F(8)).FontColor(C("#95a5a6"))); t.Span("倒三角"); });
            });
        });
    }

    private static void RadiusBox(RowDescriptor r, string label, float tl, float tr, float br, float bl)
    {
        r.AutoItem().PaddingRight(Mm(4)).Width(Mm(26)).Height(Mm(18))
            .Background(C("#d6eaf8")).Border(F(2), C("#2980b9"))
            .CornerRadiusTopLeft(tl).CornerRadiusTopRight(tr)
            .CornerRadiusBottomRight(br).CornerRadiusBottomLeft(bl)
            .Padding(Mm(2.5f))
            .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9))); t.Span(label); });
    }

    // ==========================================================================
    //  PAGE 3 — effects, images, layout
    // ==========================================================================
    private static void Page3(IDocumentContainer doc, AdapterContext ctx)
    {
        var photo = Path.Combine(ctx.AssetsDir, "photo.jpg");
        var logo = Path.Combine(ctx.AssetsDir, "logo-alpha.png");

        Frame(doc, "Page 3 — Effects, Images, Layout", 3, col =>
        {
            H1(col, "效果、图像与布局");

            // ---- 3.1 box-shadow (native Shadow API) ------------------------------------
                        H2(col, "3.1 阴影 box-shadow");
            col.Item().PaddingTop(Mm(2)).AlignMiddle().Row(r =>
            {
                r.AutoItem().PaddingLeft(Mm(4)).Width(Mm(36)).Height(Mm(16))
                    .Background(C("#ecf0f1"))
                    .Shadow(new BoxShadowStyle { OffsetX = F(2), OffsetY = F(2), Blur = F(5), Color = CA("#000000", 0.45f) })
                    .Padding(Mm(2.5f)).Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9))); t.Span("外阴影 模糊"); });
                r.AutoItem().PaddingLeft(Mm(8)).Width(Mm(36)).Height(Mm(16))
                    .Background(C("#ffffff")).Border(F(1), C("#f1c40f"))
                    .Shadow(new BoxShadowStyle { OffsetX = F(5), OffsetY = F(5), Blur = 0, Color = C("#f1c40f") })
                    .Padding(Mm(2.5f)).Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9))); t.Span("硬阴影 无模糊"); });
                // ❌ inset box-shadow: no native support -> placeholder
                r.AutoItem().PaddingLeft(Mm(8)).Width(Mm(36)).Height(Mm(16))
                    .Background(C("#dddddd")).Padding(Mm(2.5f)).Column(ic =>
                    {
                        ic.Item().Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9))); t.Span("内阴影 inset"); });
                        ic.Item().Text(t => { t.DefaultTextStyle(x => x.FontSize(F(8)).FontColor(C("#e74c3c"))); t.Span("✗ box-shadow (inset) 不支持"); });
                    });
                // 🟡 triple shadow: ring as border + outer blur + bottom inset bar drawn
                r.AutoItem().PaddingLeft(Mm(8)).Width(Mm(36)).Height(Mm(16))
                    .Border(F(2), C("#2980b9"))
                    .Shadow(new BoxShadowStyle { OffsetX = F(3), OffsetY = F(3), Blur = F(6), Color = CA("#2c3e50", 0.6f) })
                    .Background(C("#ffffff")).Layers(l =>
                    {
                        l.PrimaryLayer().PaddingLeft(Mm(2.5f)).PaddingTop(Mm(2.5f))
                            .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9))); t.Span("三重阴影叠加"); });
                        l.Layer().ExtendHorizontal().AlignBottom().Height(F(4)).Background(C("#e8f6f3"));
                    });
            });

            // ---- 3.2 gradients & transparency ---------------------------------------------
                        H2(col, "3.2 渐变与透明");
            col.Item().AlignMiddle().Row(r =>
            {
                r.RelativeItem(1).Height(Mm(16)).Svg(size => GradSvg(size, 45,
                    new[] { (0.0, "#e74c3c", 1.0), (0.5, "#f1c40f", 1.0), (1.0, "#27ae60", 1.0) }));
                r.RelativeItem(1).PaddingLeft(Mm(3)).Height(Mm(16)).Svg(size => GradSvg(size, 90,
                    new[] { (0.0, "#34495e", 1.0), (1.0, "#ecf0f1", 1.0) }));
                r.RelativeItem(1).PaddingLeft(Mm(3)).Height(Mm(16)).Svg(size =>
                    SvgDoc(size,
                        $"<defs><radialGradient id='rg' cx='30%' cy='40%' r='75%'><stop offset='10%' stop-color='#ffffff'/><stop offset='90%' stop-color='#2980b9'/></radialGradient></defs>" +
                        $"<rect width='{N((float)size.Width)}' height='{N((float)size.Height)}' fill='url(#rg)'/>"));
                r.RelativeItem(1).PaddingLeft(Mm(3)).Height(Mm(16)).Layers(l =>
                {
                    l.PrimaryLayer().Svg(size => GradSvg(size, 180,
                        new[] { (0.0, "#e74c3c", 0.8), (1.0, "#2980b9", 0.2) }));
                    l.Layer().Padding(2).Text(t =>
                    {
                        t.DefaultTextStyle(x => x.FontSize(F(8)).FontColor(C("#ffffff")));
                        t.Span("渐变+alpha");
                    });
                });
            });
            col.Item().PaddingTop(Mm(2)).AlignMiddle().Row(r =>
            {
                r.AutoItem().Width(Mm(46)).Height(Mm(26)).Svg(size =>
                    SvgDoc(size,
                        $"<rect x='0' y='0' width='{N(Mm(22))}' height='{N(Mm(22))}' fill='{Hex(C("#e74c3c"))}' fill-opacity='0.55'/>" +
                        $"<rect x='{N(Mm(9))}' y='0' width='{N(Mm(22))}' height='{N(Mm(22))}' fill='{Hex(C("#27ae60"))}' fill-opacity='0.55'/>" +
                        $"<rect x='{N(Mm(4.5f))}' y='{N(Mm(8))}' width='{N(Mm(22))}' height='{N(Mm(22))}' fill='{Hex(C("#2980b9"))}' fill-opacity='0.55'/>"));
                r.AutoItem().PaddingHorizontal(Mm(3)).Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(F(8)).FontColor(C("#95a5a6")));
                    t.Span("半透明三色叠加（重叠区混色）");
                });
                // 🟡 whole-block opacity approximated: alpha applied to bg + text
                r.AutoItem().Width(Mm(30)).Height(Mm(18)).Background(CA("#2980b9", 0.4f)).Padding(Mm(2.5f))
                    .Text(t => { t.DefaultTextStyle(x => x.FontSize(F(9)).FontColor(CA("#ffffff", 0.6f))); t.Span("opacity 0.4 整块淡出"); });
                r.AutoItem().PaddingLeft(Mm(4)).Width(Mm(34)).Height(Mm(18)).Background(C("#f8f9f9")).Border(F(1), C("#d5d8dc"))
                    .Padding(Mm(2.5f))
                    .Text(t =>
                    {
                        t.DefaultTextStyle(x => x.FontSize(F(9)));
                        t.Span("行内半透明背景高亮（文字保持全不透明）").BackgroundColor(CA("#f1c40f", 0.4f));
                    });
            });


            // ---- 3.3 images ---------------------------------------------------------------------
                        H2(col, "3.3 图像");
            col.Item().AlignMiddle().Row(r =>
            {
                r.AutoItem().Column(a =>
                {
                    Lbl(a, "JPEG 400×267 缩放显示");
                    a.Item().Width(F(150)).Height(F(100)).Image(photo);
                });
                r.AutoItem().PaddingLeft(Mm(5)).Column(b =>
                {
                    Lbl(b, "PNG alpha 透明通道（深色底验证）");
                    b.Item().Background(C("#2c3e50")).Padding(Mm(2)).Width(F(120))
                        .Image(logo);
                });
                r.AutoItem().PaddingLeft(Mm(5)).Column(d =>
                {
                    Lbl(d, "图片圆角+边框复合");
                    // native: circular clip via corner radius = side/2 + rounded border
                    d.Item().Width(F(118)).Height(F(118)).Border(F(4), C("#27ae60")).CornerRadius(F(59))
                        .Image(photo).FitUnproportionally();
                });
            });


            // ---- 3.4 flex ----------------------------------------------------------------------------
                        H2(col, "3.4 Flex 布局");
            col.Item().Row(r =>
            {
                r.RelativeItem(2).Column(c =>
                {
                    c.Item().Row(x =>
                    {
                        FlexCell(x.AutoItem(), "#e74c3c", "space-between 左", null);
                        FlexCell(x.RelativeItem(1).AlignCenter(), "#2980b9", "中", null);
                        FlexCell(x.AutoItem(), "#27ae60", "右", null);
                    });
                    c.Item().PaddingTop(Mm(2)).Row(x =>
                    {
                        FlexCell(x.AutoItem(), "#8e44ad", "flex-start", null);
                        FlexCell(x.RelativeItem(1).AlignRight(), "#d35400", "margin-left:auto 顶右", null);
                    });
                    c.Item().PaddingTop(Mm(2)).Column(x =>
                    {
                        x.Item().Row(w =>
                        {
                            FlexCell(w.RelativeItem(47), "#d6eaf8", "wrap A", C("#222222"));
                            w.RelativeItem(3);
                            FlexCell(w.RelativeItem(47), "#fadbd8", "wrap B", C("#222222"));
                            w.RelativeItem(3);
                        });
                        x.Item().PaddingTop(F(2)).Row(w =>
                        {
                            FlexCell(w.RelativeItem(47), "#d5f5e3", "wrap C 换行", C("#222222"));
                            w.RelativeItem(53);
                        });
                    });
                    c.Item().PaddingTop(Mm(2)).Border(F(1), C("#bdc3c7")).Row(x =>
                    {
                        x.RelativeItem(1).Background(C("#fdf6ec")).Padding(Mm(2))
                            .Text(t => { t.DefaultTextStyle(x2 => x2.FontSize(F(8))); t.Span("嵌套：flex:1 等分列左"); });
                        x.RelativeItem(1).Background(C("#eafaf1")).Padding(Mm(2)).Column(cc =>
                        {
                            cc.Item().Text(t => { t.DefaultTextStyle(x2 => x2.FontSize(F(8))); t.Span("右列，列内再套"); });
                            cc.Item().PaddingTop(Mm(1)).Row(bars =>
                            {
                                bars.RelativeItem(1).Height(Mm(4)).Background(C("#aed6f1"));
                                bars.RelativeItem(2).Height(Mm(4)).Background(C("#f5b7b1"));
                            });
                        });
                    });
                });
                // dashed purple container with cross-axis centered block (Svg border)
                SvgBox(r.RelativeItem(1).PaddingLeft(Mm(4)), new[] { 1f, 1f, 1f, 1f }, new[] { Bs.Dashed, Bs.Dashed, Bs.Dashed, Bs.Dashed },
                    new[] { C("#8e44ad"), C("#8e44ad"), C("#8e44ad"), C("#8e44ad") }, 0, C("#f4ecf7"),
                    0, 0, 0, 0, box =>
                    box.AlignMiddle().Row(inner =>
                    {
                        inner.RelativeItem(20);
                        inner.RelativeItem(60).Height(Mm(14)).Background(C("#8e44ad")).AlignMiddle()
                            .Text(t => { t.AlignCenter(); t.DefaultTextStyle(x => x.FontSize(F(10.5f)).FontColor(C("#ffffff"))); t.Span("交叉轴居中"); });
                        inner.RelativeItem(20);
                    }));
            });


            // ---- 3.5 table ------------------------------------------------------------------------------
                        H2(col, "3.5 数据表格");
            col.Item().PaddingTop(Mm(1)).BorderTop(0.75f).BorderColor(C("#5d6d7e"))
                .BorderLeft(0.75f).BorderBottom(0.75f).BorderColor(C("#d5d8dc"))
                .Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(12);
                        c.RelativeColumn(40);
                        c.RelativeColumn(20);
                        c.RelativeColumn(28);
                    });
                    t.Header(h =>
                    {
                        HeadCell(h, "编号 Code", false);
                        HeadCell(h, "名称 Name", false);
                        HeadCell(h, "数量 Qty", true);
                        HeadCell(h, "金额 Amount (¥)", true);
                    });
                    var rows = new[]
                    {
                        ("A-001", "工业级渲染管线 Industrial pipeline", "12", "1,280.00"),
                        ("A-002", "矢量字体子集 Vector subset", "3", "96.50"),
                        ("B-014", "分页引擎 Pagination engine", "1", "2,400.00"),
                        ("B-015", "阴影合成器 Shadow compositor", "8", "640.00"),
                        ("C-101", "渐变缓存 Gradient cache", "24", "1,152.00"),
                        ("C-102", "Alpha 通道混合 Alpha blending", "6", "306.00"),
                        ("D-200", "CJK 换行分析 Line-break CJK", "2", "780.00"),
                    };
                    for (var i = 0; i < rows.Length; i++)
                    {
                        var striped = i % 2 == 1;
                        BodyCell(t, rows[i].Item1, striped, true, false);
                        BodyCell(t, rows[i].Item2, striped, false, false);
                        BodyCell(t, rows[i].Item3, striped, false, true);
                        BodyCell(t, rows[i].Item4, striped, false, true);
                    }
                    FootCell(t, "合计 Total (7 行明细)", 2, false);
                    FootCell(t, "56", 1, true);
                    FootCell(t, "¥ 6,654.50", 1, true);
                });

            // ---- word-break bar (🟡: long tokens only break at boundaries) --------------------------------
            col.Item().PaddingTop(Mm(2)).Background(C("#fdf6ec")).BorderLeft(F(3)).BorderColor(C("#d35400"))
                .Padding(Mm(1.5f)).PaddingLeft(Mm(1.5f) + F(3))
                .Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(F(9)));
                    t.Span("超长URL自动断行验证：https://example.org/very/long/path/文件与单词混排abcdefghij无空格也可断开以适配窄容器supercalifragilistic 末行。");
                });
        });
    }

    private static void FlexCell(IContainer item, string bg, string text, Color? fg)
    {
        item.Background(C(bg)).Padding(Mm(2.5f))
            .Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(F(9)).FontColor(fg ?? C("#ffffff")));
                t.Span(text);
            });
    }

    private static void HeadCell(TableCellDescriptor h, string text, bool right)
    {
        h.Cell().Background(C("#34495e"))
            .BorderBottom(0.75f).BorderRight(0.75f).BorderColor(C("#5d6d7e"))
            .PaddingVertical(Mm(1.2f)).PaddingHorizontal(Mm(2))
            .Text(t =>
            {
                if (right) t.AlignRight(); else t.AlignLeft();
                t.DefaultTextStyle(x => x.FontSize(F(8.5f)).Bold().FontColor(C("#ffffff")));
                t.Span(text);
            });
    }

    private static void BodyCell(TableDescriptor t, string text, bool striped, bool bold, bool right)
    {
        IContainer cell = t.Cell();
        if (striped) cell = cell.Background(C("#f8f9f9"));
        cell.BorderBottom(0.75f).BorderRight(0.75f).BorderColor(C("#d5d8dc"))
            .PaddingVertical(Mm(0.8f)).PaddingHorizontal(Mm(2))
            .Text(x =>
            {
                if (right) x.AlignRight(); else x.AlignLeft();
                x.DefaultTextStyle(v => v.FontSize(F(8.5f)));
                if (bold) x.Span(text).Bold(); else x.Span(text);
            });
    }

    private static void FootCell(TableDescriptor t, string text, uint span, bool right)
    {
        var cell = t.Cell().ColumnSpan(span).Background(C("#eaeded"));
        cell.BorderBottom(0.75f).BorderRight(0.75f).BorderColor(C("#d5d8dc"))
            .PaddingVertical(Mm(0.8f)).PaddingHorizontal(Mm(2))
            .Text(x =>
            {
                if (right) x.AlignRight(); else x.AlignLeft();
                x.DefaultTextStyle(v => v.FontSize(F(8.5f)).Bold());
                x.Span(text);
            });
    }
}
