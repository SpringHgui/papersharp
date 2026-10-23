using SkiaSharp;

namespace PdfBench.Runner.Adapters;

/// <summary>
/// 第 1 页：排版与文字（challenge.html 第 36-118 行）。
/// Skia 无 HTML/CSS/布局/列表语义，整页按 CSS 盒换算成绝对坐标逐块手画。
/// 列表 marker（disc/circle/square/decimal/lower-alpha/upper-roman）全部自绘自编号，
/// 行内代码芯片先量文字宽再铺圆角底色+边框再叠字，text-shadow 用偏移重画近似。
/// </summary>
public sealed partial class SkiaSharpAdapter
{
    // 常用样式（px 字号，Kind/Color 由核心层命名色给出）
    static readonly Style P105 = new() { Size = 10.5f };
    static readonly Style PLbl = new() { Size = 8, Color = LblC };
    static readonly Style PList = new() { Size = 10 };

    partial void DrawPage1(SKCanvas c)
    {
        Frame(c, "Page 1 — Typography & Text", 1);

        float y = MT + P(21) + MM(4);   // 页眉底线 + .hdr margin-bottom 4mm

        // ---- h1 ----
        H1(c, "排版与文字 Rendering Test", ref y);

        // ---- 1.1 层级与字重 ----
        y = H2(c, "1.1 层级与字重", y);
        // .row[data-f=headings] align-items:flex-end：左 H3、右说明
        float h3Base = y + P(11 * 0.8f);
        Text(c, Segs(("H3 subtitle", new Style { Size = 11, Bold = true, Color = Tag })), ML, h3Base);
        Text(c, Segs(("h3 → h1 右对齐抬升", new Style { Size = 9, Color = LblC })), ML, h3Base, Align.Right, CW);
        y += P(11 * 1.35f) + P(2);

        // weights：含 small-caps 假缩放（A）
        y = Para(c, Segs(
            ("字重：", P105),
            ("粗体 bold", P105 with { Bold = true }), ("、", P105),
            ("斜体 italic", P105 with { Italic = true }), ("、", P105),
            ("粗斜体", P105 with { Bold = true, Italic = true }), ("、", P105),
            ("细体 light 300", P105 with { Kind = Fam.Light }), ("、", P105),
            ("下划线", P105 with { Underline = true }), ("、", P105),
            ("删除线", P105 with { Strike = true }), ("、", P105),
            ("Small Caps 效果", P105 with { SmallCaps = true }), ("。", P105)),
            ML, y, CW, 10.5f * 1.35f);
        y += MM(2.5f);

        // inline-code-chip：手排单行，量宽→铺芯片→叠字
        ChipLine(c, ref y);

        // ---- 1.2 中西混排与行高 ----
        y = H2(c, "1.2 中西混排与行高", y);
        y = Para(c, Segs(("中文段落混合 English words 与数字 0123456789，标点，以及括号（半角）与全角符号。 " +
            "中英文基线对齐是渲染引擎的常见短板，The quick brown fox 应跳 over the lazy dog 而不断行错位。", P105)),
            ML, y, CW, 10.5f * 1.8f);
        y += MM(2.5f);

        // 三档行高 + Cream 背景
        foreach (var (mult, txt) in new (float, string)[]
        {
            (1.0f, "密集行距示例 The quick brown fox jumps over the lazy dog 中文密集行示例，行与行可能粘连。"),
            (1.4f, "正常行距示例 The quick brown fox jumps over the lazy dog 中文正常行距示例，阅读舒适。"),
            (2.2f, "宽松行距示例 The quick brown fox 中文宽松行距示例。"),
        })
        {
            Text(c, Segs(($"line-height: {mult}", PLbl)), ML, y + P(8 * 0.8f));
            y += P(8 * 1.35f) + P(1);
            y = Para(c, Segs((txt, P105)), ML, y, CW, 10.5f * mult, bg: Cream);
            y += (mult < 2.1f ? MM(1f) : 0f);
        }

        // letter-spacing：三行字号 12，字距 0/2/5
        y += MM(2f);
        foreach (var (sp, txt) in new (float, string)[]
            { (0f, "normal: tracking 字距"), (2f, "wide 2px: tracking 字距"), (5f, "wider 5px: tracking 字距") })
        {
            var ls = new Style { Size = 12, Spacing = sp };
            Text(c, Segs((txt, ls)), ML, y + P(12 * 0.8f));
            y += P(12 * 1.35f);
        }

        // text-indent：2em = 21px
        y = Para(c, Segs(("首行缩进两个字符：这是段落首行缩进的验证文本，缩进应恰好为 2 个全角字符宽度且仅作用于首行。", P105)),
            ML, y, CW, 10.5f * 1.35f, indentPx: 21);
        y += MM(2.5f);

        // ---- 1.3 文字效果与对齐 ----
        y = H2(c, "1.3 文字效果与对齐", y);
        // text-shadow 2px 2px 0 #f5b7b1：偏移重画近似（A）
        var sh = new Style { Size = 16, Bold = true, Color = H2Ink };
        float shBase = y + P(16 * 0.8f);
        Text(c, Segs(("阴影文字 Text Shadow", sh with { Color = C("f5b7b1") })), ML + P(2), shBase + P(2));
        Text(c, Segs(("阴影文字 Text Shadow", sh)), ML, shBase);
        y += P(16 * 1.35f) + MM(2.5f);

        // 五色文字（纯色无渐变）
        y = Para(c, Segs(
            ("红", P105 with { Color = Red }), (" ", P105),
            ("绿", P105 with { Color = Green }), (" ", P105),
            ("蓝", P105 with { Color = Blue }), (" ", P105),
            ("橙", P105 with { Color = Orange }), (" ", P105),
            ("紫", P105 with { Color = Purple }), (" —— 五色文字，纯色无渐变。", P105)),
            ML, y, CW, 10.5f * 1.35f);
        y += MM(2.5f);

        // 四格对齐：tint 底 + padding，含 justify 长文
        AlignRow(c, ref y);

        // ---- 1.4 列表与嵌套 ----
        y = H2(c, "1.4 列表与嵌套", y);
        float gap = MM(4f);
        float colW = (CW - 2 * gap) / 3;
        float[] cx3 = { ML, ML + colW + gap, ML + 2 * (colW + gap) };
        float ca = y, cb = y, cc = y;   // 三列各自独立游标，同顶对齐
        ListColDisc(c, cx3[0], colW, ref ca);
        ListColOrdered(c, cx3[1], colW, ref cb);
        ListColCircle(c, cx3[2], colW, ref cc);
    }

    // ---------- 页 1 私有辅助 ----------

    void H1(SKCanvas c, string text, ref float y)
    {
        Text(c, Segs((text, new Style { Size = 20, Bold = true, Color = Slate })), ML, y + P(20 * 0.8f));
        y += P(20 * 1.35f) + MM(3f);
    }

    /// <summary>行内代码芯片单行手排：量宽→铺圆角底色+边框→叠文字。</summary>
    void ChipLine(SKCanvas c, ref float y)
    {
        float bl = y + P(10.5f * 0.82f);
        float x = ML;
        const string lead = "行内代码芯片：", mid = " 与 ", tail = " 混排于行文中。";
        Text(c, Segs((lead, P105)), x, bl); x += WidthOf(P105, lead);
        x += Chip(c, "PdfGenerator.Render()", C("f3e5f5"), C("8e24aa"), C("ce93d8"), x, bl);
        Text(c, Segs((mid, P105)), x, bl); x += WidthOf(P105, mid);
        x += Chip(c, "spec.json", C("e3f2fd"), C("1565c0"), null, x, bl);
        Text(c, Segs((tail, P105)), x, bl);
        y += P(10.5f * 1.35f) + MM(2.5f);
    }

    float Chip(SKCanvas c, string text, SKColor bg, SKColor fg, SKColor? border, float x, float baseline)
    {
        var st = new Style { Size = 9.5f, Kind = Fam.Mono, Color = fg };
        float tw = WidthOf(st, text);
        float padX = P(4f);
        var r = Rect(x, baseline - P(9.5f * 0.95f), tw + 2 * padX, P(9.5f * 1.3f));
        Fill(c, r, bg, Rad(3f));
        if (border is not null)
        {
            var b = Side.Solid(1, border.Value);
            Border(c, r, Rad(3f), b, b, b, b);
        }
        Text(c, Segs((text, st)), x + padX, baseline);
        return tw + 2 * padX;
    }

    void AlignRow(SKCanvas c, ref float y)
    {
        float gap = MM(4f), pad = MM(2.5f);
        float unit = (CW - 3 * gap) / 4.6f;
        float[] ws = { unit, unit, unit, unit * 1.6f };
        float[] xs = new float[4];
        xs[0] = ML;
        for (int i = 1; i < 4; i++) xs[i] = xs[i - 1] + ws[i - 1] + gap;
        string[] texts =
        {
            "左对齐 left 文", "居中 center 文", "右对齐 right 文",
            "两端对齐 justify：This paragraph is justified so that both left and right edges form clean lines across its full width 无论中英文。",
        };
        Align[] aligns = { Align.Left, Align.Center, Align.Right, Align.Justify };
        var cell = new Style { Size = 9 };
        float lh = 9 * 1.35f;

        float maxH = 0;
        for (int i = 0; i < 4; i++)
        {
            float innerW = ws[i] - 2 * pad;
            var runs = Segs((texts[i], cell));
            float h = ParaHeight(runs, innerW, lh);
            maxH = Math.Max(maxH, h);
        }
        float cellH = maxH + 2 * pad;
        for (int i = 0; i < 4; i++)
        {
            Fill(c, Rect(xs[i], y, ws[i], cellH), Tint);
            if (aligns[i] == Align.Justify)
                ParaJ(c, Segs((texts[i], cell)), xs[i] + pad, y + pad, ws[i] - 2 * pad, lh);
            else
                Para(c, Segs((texts[i], cell)), xs[i] + pad, y + pad, ws[i] - 2 * pad, lh, aligns[i]);
        }
        y += cellH + MM(2.5f);
    }

    /// <summary>两端对齐段落，但末行左对齐（Chrome 不拉伸最后一行；核心层 Para 会拉伸）。</summary>
    float ParaJ(SKCanvas c, IReadOnlyList<Run> runs, float x, float y, float maxW, float lineHeightPx)
    {
        var lines = WrapLines(Flatten(runs, false), maxW);
        for (int i = 0; i < lines.Count; i++)
        {
            var al = i == lines.Count - 1 ? Align.Left : Align.Justify;
            y += DrawLine(c, lines[i], x, y, lineHeightPx, al, maxW);
        }
        return y;
    }

    // ---------- 列表 ----------
    static readonly float ListStep = P(26f);   // 每级缩进（与 marker 右缘一致）

    void ListColDisc(SKCanvas c, float x0, float w, ref float iy)
    {
        Text(c, Segs(("unordered disc，marker 着色", PLbl)), x0, iy + P(8 * 0.8f));
        iy += P(8 * 1.35f) + P(2);
        float lh = P(10 * 1.35f);
        var ink = new Style { Size = 10 };

        Disc(c, x0 + P(12f), MarkerCy(iy), H2Ink);
        Text(c, Segs(("项目一：", ink with { Color = Red }), ("无序列表 disc 符号", ink with { Color = H2Ink })), x0 + ListStep, LineBase(iy));
        iy += lh;

        Disc(c, x0 + P(12f), MarkerCy(iy), ink.Color);
        Text(c, Segs(("项目二：Marker 颜色独立于文字", ink)), x0 + ListStep, LineBase(iy));
        iy += lh;

        Disc(c, x0 + P(12f), MarkerCy(iy), ink.Color);
        Text(c, Segs(("项目三：三级嵌套入口", ink)), x0 + ListStep, LineBase(iy));
        iy += lh;

        // 第 2 层 ul 的 UA 默认是 circle（空心），第 3 层才是 square
        Hollow(c, x0 + P(12f) + ListStep, MarkerCy(iy), ink.Color);
        Text(c, Segs(("嵌套层 A", ink)), x0 + ListStep + ListStep, LineBase(iy));
        iy += lh;

        Sq(c, x0 + P(12f) + 2 * ListStep, MarkerCy(iy), ink.Color);
        Text(c, Segs(("方块 square 层", ink)), x0 + ListStep + 2 * ListStep, LineBase(iy));
        iy += lh;
    }

    void ListColOrdered(SKCanvas c, float x0, float w, ref float iy)
    {
        Text(c, Segs(("ordered decimal / lower-alpha / upper-roman", PLbl)), x0, iy + P(8 * 0.8f));
        iy += P(8 * 1.35f) + P(2);
        float lh = P(10 * 1.35f);
        var ink = new Style { Size = 10 };
        var num = new Style { Size = 10 };
        var items = new (string marker, int lvl, string txt)[]
        {
            ("1.", 0, "十进制 ordered"), ("2.", 0, "第二项"),
            ("a.", 1, "lower-alpha 子项"), ("b.", 1, "子项二"),
            ("I.", 0, "upper-roman 独立编号"), ("II.", 0, "应为 II"),
        };
        foreach (var (marker, lvl, txt) in items)
        {
            float textX = x0 + ListStep + lvl * ListStep;
            float right = textX - P(4f);
            float mw = WidthOf(num, marker);
            Text(c, Segs((marker, num)), right - mw, LineBase(iy));
            Text(c, Segs((txt, ink)), textX, LineBase(iy));
            iy += lh;
        }
    }

    void ListColCircle(SKCanvas c, float x0, float w, ref float iy)
    {
        Text(c, Segs(("自定义 marker：circle + 无标记", PLbl)), x0, iy + P(8 * 0.8f));
        iy += P(8 * 1.35f) + P(2);
        float lh = P(10 * 1.35f);
        var ink = new Style { Size = 10 };

        Hollow(c, x0 + P(12f), MarkerCy(iy), ink.Color);
        Text(c, Segs(("circle 空心圈", ink)), x0 + ListStep, LineBase(iy));
        iy += lh;

        // list-style:none：无 marker，但保留缩进对齐
        Text(c, Segs(("none：无标记但有缩进对齐", ink)), x0 + ListStep, LineBase(iy));
        iy += lh;

        Hollow(c, x0 + P(12f), MarkerCy(iy), ink.Color);
        Text(c, Segs(("circle 第二项", ink)), x0 + ListStep, LineBase(iy));
        iy += lh;
    }

    static float LineBase(float top) => top + P(10 * 1.05f);
    static float MarkerCy(float top) => top + P(10 * 0.68f);

    void Disc(SKCanvas c, float cx, float cy, SKColor col)
    {
        using var p = new SKPaint { Color = col, IsAntialias = true };
        c.DrawCircle(cx, cy, P(1.7f), p);
    }

    void Hollow(SKCanvas c, float cx, float cy, SKColor col)
    {
        using var p = new SKPaint { Color = col, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = P(0.8f) };
        c.DrawCircle(cx, cy, P(2.3f), p);
    }

    void Sq(SKCanvas c, float cx, float cy, SKColor col)
    {
        float s = P(2f);
        Fill(c, Rect(cx - s, cy - s, 2 * s, 2 * s), col);
    }

    /// <summary>只量高不落笔：换行数 × 行盒高。</summary>
    float ParaHeight(IReadOnlyList<Run> runs, float maxW, float lineHeightPx)
    {
        var lines = WrapLines(Flatten(runs, false), maxW);
        float h = 0;
        foreach (var l in lines) h += P(LineBoxPx(l.Atoms, lineHeightPx));
        return h;
    }
}
