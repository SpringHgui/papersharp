using SkiaSharp;

namespace PdfBench.Runner.Adapters;

/// <summary>
/// 页 3 —— 效果、图像与布局。
///
/// challenge.html 第 198-307 行：阴影四例 / 渐变四例 / 半透明叠加 / 三张图 / flex 五组 /
/// 8 行数据表 / 超长 URL 断行。Skia 没有 CSS、没有布局，全部盒模型、flex 比例、表格列宽
/// 都是这里手算的 pt 坐标（这是基准要量化的代价）。阴影用模糊/实底/inset 原语，
/// 渐变用 LinGrad/RadGrad 着色器，整块 opacity 用 SKCanvas.SaveLayer 真合成。
/// </summary>
public sealed partial class SkiaSharpAdapter
{
    /// <summary>本段共享的字号/内边距（challenge.html 的 .cell / table / .lbl 口径，CSS px）。</summary>
    private readonly struct Cfg
    {
        public float Gap { get; init; }     // .row gap 4mm（pt）
        public float Pad { get; init; }     // .cell padding 2.5mm（pt）
        public float LhPx { get; init; }    // .cell 行高（CSS px）
        public Style Cell { get; init; }    // .cell 字样式
        public Style Th { get; init; }      // th：8.5px 粗体白字
        public Style Td { get; init; }      // td：8.5px 常规
        public Style TdB { get; init; }     // td 加粗（编号列 / tfoot）
        public Style Lbl { get; init; }     // .lbl：8px 灰
    }

    partial void DrawPage3(SKCanvas c)
    {
        var K = new Cfg
        {
            Gap = MM(4), Pad = MM(2.5f), LhPx = 11.25f,
            Cell = new Style { Size = 9, Color = Ink },
            Th = new Style { Size = 8.5f, Bold = true, Color = SKColors.White },
            Td = new Style { Size = 8.5f, Color = Ink },
            TdB = new Style { Size = 8.5f, Bold = true, Color = Ink },
            Lbl = new Style { Size = 8, Color = LblC },
        };

        Frame(c, "Page 3 — Effects, Images, Layout", 3);

        // 版心纵向节奏按 challenge.html 的 margin 手算（h1 20px/3mm、h2 3.5mm-14px-2mm）。
        // 首块起点 = 页眉线（MT+P(21)）+ hdr margin-bottom 4mm。
        float y = MT + P(41);
        y = Para(c, Segs(("效果、图像与布局", new Style { Size = 20, Bold = true, Color = Slate })),
            ML, y, CW, 24);                                  // h1
        y += MM(3);                                          // h1 margin-bottom 3mm

        y = Shadows(c, K, y);
        y = Gradients(c, K, y);
        y = Images(c, K, y);
        y = Flex(c, K, y);
        y = Table(c, K, y);
    }

    /// <summary>
    /// h2 小节标题（CSS 口径版）：上 3.5mm + 14px 行 + 下 2mm，左侧 4px 蓝条。
    /// 核心层的 H2 把 3.5mm 当成了 3.5pt 且不含上边距，页 3 要贴着页脚排，间距必须精确，
    /// 所以这里按 CSS 重算一份（画法与核心层一致）。
    /// </summary>
    private float H2b(SKCanvas c, string text, float y)
    {
        y += MM(2.8f);   // margin-top 3.5mm（页 3 收 0.7mm，为的是末行不压到页脚线）
        var box = Rect(ML, y, CW, P(14));
        Border(c, box, Radii.Zero, Side.NoBorder, Side.NoBorder, Side.NoBorder, Side.Solid(4, Accent));
        Text(c, Segs((text, new Style { Size = 14, Bold = true, Color = H2Ink })), ML + P(6), y + P(11));
        return y + P(14) + MM(2);                            // margin-bottom 2mm
    }

    // ======================= 3.1 阴影 box-shadow =======================
    private float Shadows(SKCanvas c, Cfg K, float y)
    {
        y = H2b(c, "3.1 阴影 box-shadow", y);
        float bh = MM(16);
        // 4 个固定 36mm 盒子：gap 4mm + 各自 margin-left 4/8/8/8mm → 超版心 2mm，按 flex-shrink 等比收缩
        var (xs, ws) = FlexBasis(ML, CW,
            new[] { MM(36), MM(36), MM(36), MM(36) }, K.Gap,
            new[] { MM(4), MM(8), MM(8), MM(8) });
        var cell = K.Cell;

        // 1) 外阴影 模糊：box-shadow:2px 2px 5px rgba(0,0,0,.45) —— Skia 有高斯模糊，原生口径
        var r0 = Rect(xs[0], y, ws[0], bh);
        ShadowOuter(c, r0, 2, 2, 5, A(SKColors.Black, 0.45f));
        Fill(c, r0, Tint);
        Para(c, Segs(("外阴影 模糊", cell)), r0.Left + K.Pad, r0.Top + K.Pad, r0.Width - 2 * K.Pad, K.LhPx);

        // 2) 硬阴影 无模糊：box-shadow:5px 5px 0 #f1c40f
        var r1 = Rect(xs[1], y, ws[1], bh);
        ShadowHard(c, r1, 5, 5, Yellow);
        Box(c, r1, SKColors.White, Side.Solid(1, Yellow));
        Para(c, Segs(("硬阴影 无模糊", cell)), r1.Left + K.Pad, r1.Top + K.Pad, r1.Width - 2 * K.Pad, K.LhPx);

        // 3) 内阴影 inset：先铺背景，再在背景之上渗 inset（核心层约定）
        var r2 = Rect(xs[2], y, ws[2], bh);
        Fill(c, r2, Tint);
        ShadowInset(c, r2, 3, 3, 5, A(SKColors.Black, 0.5f));
        Para(c, Segs(("内阴影 inset", cell)), r2.Left + K.Pad, r2.Top + K.Pad, r2.Width - 2 * K.Pad, K.LhPx);

        // 4) 三重阴影叠加：CSS 列表从外到内逐层画（模糊投影 → 2px spread 描边 → inset 实底带）
        var r3 = Rect(xs[3], y, ws[3], bh);
        ShadowOuter(c, r3, 3, 3, 6, A(Slate, 0.6f));                       // 3px 3px 6px rgba(44,62,80,.6)
        ShadowHard(c, Inflate(r3, 2), 0, 0, Blue);                          // 0 0 0 2px #2980b9（外扩描边环）
        Fill(c, r3, SKColors.White);
        Fill(c, new SKRect(r3.Left, r3.Bottom - P(4), r3.Right, r3.Bottom), // inset 0 -4px 0（零模糊=实底带）
            C("e8f6f3"));
        Para(c, Segs(("三重阴影叠加", cell)), r3.Left + K.Pad, r3.Top + K.Pad, r3.Width - 2 * K.Pad, K.LhPx);

        return y + bh;
    }

    // ======================= 3.2 渐变与透明 =======================
    private float Gradients(SKCanvas c, Cfg K, float y)
    {
        y = H2b(c, "3.2 渐变与透明", y);

        // ---- 渐变一行：4 个 flex:1，gap 4mm + margin-left 3mm ----
        float gh = MM(16);
        var (gx, gw) = FlexGrow(ML, CW, 4, K.Gap, new[] { 0f, MM(3), MM(3), MM(3) });

        var g1 = Rect(gx[0], y, gw, gh);
        FillShader(c, g1, LinGrad(g1, 45, new[] { Red, Yellow, Green }, new[] { 0f, 0.5f, 1f }));

        var g2 = Rect(gx[1], y, gw, gh);
        FillShader(c, g2, LinGrad(g2, 90, new[] { Slate, Tint }));

        var g3 = Rect(gx[2], y, gw, gh);
        FillShader(c, g3, RadGrad(g3, 0.3f, 0.4f, new[] { SKColors.White, Blue }, new[] { 0.1f, 0.9f }));

        var g4 = Rect(gx[3], y, gw, gh);   // 带 alpha 的色标：直接给半透明色，画布在渐变里真混合
        FillShader(c, g4, LinGrad(g4, 180, new[] { A(Red, 0.8f), A(Blue, 0.2f) }));
        Text(c, Segs(("渐变+alpha", new Style { Size = 8, Color = SKColors.White })),
            g4.Left, g4.Top + P(9.7f));           // span 无 padding：8px 行盒 10.8，基线 = 顶 + 半前导 + ascent

        y += gh + MM(2);                    // .row margin-top 2mm

        // ---- 透明叠加一行：三块 rgba 方块 + 说明 + opacity:0.4 整块 + 行内半透明高亮 ----
        float rowH = MM(26), sq = MM(22);
        float[] m = { 0f, MM(3), MM(4), MM(4) };             // 子项 margin-left
        float[] w = { MM(46), 0f, MM(30), MM(34) };          // w[1] = 说明文字宽（实测）
        w[1] = WidthOf(K.Lbl, "半透明三色叠加（重叠区混色）");
        float[] xs = new float[4];
        xs[0] = ML + m[0];
        for (int i = 1; i < 4; i++) xs[i] = xs[i - 1] + w[i - 1] + m[i] + K.Gap;

        // 三块半透明：DOM 顺序叠加 A(...)，重叠区由 Skia 真合成
        Fill(c, Rect(xs[0], y, sq, sq), A(Red, 0.55f));
        Fill(c, Rect(xs[0] + MM(9), y, sq, sq), A(Green, 0.55f));
        Fill(c, Rect(xs[0] + MM(4.5f), y + MM(8), sq, sq), A(Blue, 0.55f));

        TextMid(c, Segs(("半透明三色叠加（重叠区混色）", K.Lbl)), xs[1], y + rowH / 2, w[1]);

        var rOp = Rect(xs[2], y + (rowH - MM(18)) / 2, MM(30), MM(18));
        Layer(c, rOp, 0.4f, cv =>                 // opacity:0.4 → saveLayer 真合成（画布原生强项）
        {
            Fill(cv, rOp, Blue);
            Para(cv, Segs(("opacity 0.4 整块淡出", new Style { Size = 9, Color = SKColors.White })),
                rOp.Left + K.Pad, rOp.Top + K.Pad, rOp.Width - 2 * K.Pad, K.LhPx);
        });

        var rHi = Rect(xs[3], y + (rowH - MM(18)) / 2, MM(34), MM(18));
        Box(c, rHi, C("f8f9f9"), Side.Solid(1, C("d5d8dc")));
        ParaInlineBg(c, Segs(("行内半透明背景高亮（文字保持全不透明）", new Style { Size = 9, Color = Ink })),
            rHi.Left + K.Pad, rHi.Top + K.Pad, rHi.Width - 2 * K.Pad, K.LhPx, A(Yellow, 0.4f));

        return y + rowH;
    }

    // ======================= 3.3 图像 =======================
    private float Images(SKCanvas c, Cfg K, float y)
    {
        y = H2b(c, "3.3 图像", y);

        float lblH = P(13) + P(1);              // .lbl 行盒（8px，雅黑 ascent 超 em）+ margin-bottom 1px
        var lbl = K.Lbl;
        float h1 = lblH + P(100);               // JPEG 150×100
        float h2 = lblH + P(60) + 2 * MM(2);    // PNG 120×60 带 2mm 深色内衬
        float h3 = lblH + P(110);               // 圆角图 110×110
        float rowH = Math.Max(h1, Math.Max(h2, h3));
        float yA = y + (rowH - h1) / 2, yB = y + (rowH - h2) / 2, yC = y + (rowH - h3) / 2;

        // 三列 x：固定尺寸盒 + gap 4mm（第 2/3 列各自 margin-left 5mm）；若超出版心则整体等比缩放图片
        float[] basis = { P(150), P(120) + 2 * MM(2), P(110) };
        float scale = Math.Min(CW / (basis.Sum() + 2 * (K.Gap + MM(5))), 1f);

        // 列 1：JPEG 400×267 缩放显示
        float xA = ML;
        DrawLbl(c, "JPEG 400×267 缩放显示", xA, yA, lbl);
        ImgHi(c, "photo.jpg", Rect(xA, yA + lblH, P(150) * scale, P(100) * scale));

        // 列 2：PNG alpha —— 必须先铺深色底再叠图，透明通道才验证得出来
        float xB = xA + P(150) * scale + K.Gap + MM(5);
        DrawLbl(c, "PNG alpha 透明通道（深色底验证）", xB, yB, lbl);
        var dark = Rect(xB, yB + lblH, (P(120) + 2 * MM(2)) * scale, (P(60) + 2 * MM(2)) * scale);
        Fill(c, dark, H2Ink);
        ImgHi(c, "logo-alpha.png", Shrink(dark, MM(2) * scale));   // padding:2mm → 图落在内缩后的盒里

        // 列 3：圆角(50%)+4px 边框复合
        float xC = xB + (P(120) + 2 * MM(2)) * scale + K.Gap + MM(5);
        DrawLbl(c, "图片圆角+边框复合", xC, yC, lbl);
        float dia = P(110) * scale;                     // * { box-sizing:border-box } → 110px 含 4px 边框
        var circ = Rect(xC, yC + lblH, dia, dia);       // 这就是边框盒
        ImgHi(c, "photo.jpg", Shrink(circ, P(4) * scale), Rad(50));   // 图缩到 content 盒（102px）再裁成正圆
        Ring(c, Shrink(circ, P(2) * scale), P(4) * scale, Green);     // 环心线 104px，宽 4px → 铺满 102..110px

        return y + rowH;
    }

    // ======================= 3.4 Flex 布局 =======================
    private float Flex(SKCanvas c, Cfg K, float y)
    {
        y = H2b(c, "3.4 Flex 布局", y);

        float top = y;
        float colGap = K.Gap;                        // .row gap 4mm
        float lW = (CW - colGap) * 2 / 3, rW = (CW - colGap) / 3;   // flex:2 / flex:1 手算
        float lx = ML, rx = ML + lW + colGap;
        float ch = P(K.LhPx) + 2 * K.Pad;            // 标准 .cell 高

        // A) justify-content:space-between：首贴左、末贴右、中间居中（三个自然宽盒的落点手算）
        float fy = top;
        string[] sa = { "space-between 左", "中", "右" };
        SKColor[] ca = { Red, Blue, Green };
        float[] sw = sa.Select(t => WidthOf(K.Cell, t) + 2 * K.Pad + P(2)).ToArray();   // +P(2)：留出不换行的余量
        float[] sxa = { lx, lx + (lW - sw[1]) / 2, lx + lW - sw[2] };
        for (int i = 0; i < 3; i++)
        {
            var r = Rect(sxa[i], fy, sw[i], ch);
            Fill(c, r, ca[i]);
            Para(c, Segs((sa[i], new Style { Size = 9, Color = SKColors.White })),
                r.Left + K.Pad, r.Top + K.Pad, r.Width - 2 * K.Pad, K.LhPx);
        }
        fy += ch + MM(2);

        // B) flex-start + margin-left:auto（第二个顶到最右）
        string[] sb = { "flex-start", "margin-left:auto 顶右" };
        SKColor[] cb = { Purple, DOrange };
        float[] bw = sb.Select(t => WidthOf(K.Cell, t) + 2 * K.Pad + P(2)).ToArray();
        for (int i = 0; i < 2; i++)
        {
            var r = Rect(i == 0 ? lx : lx + lW - bw[1], fy, bw[i], ch);
            Fill(c, r, cb[i]);
            Para(c, Segs((sb[i], new Style { Size = 9, Color = SKColors.White })),
                r.Left + K.Pad, r.Top + K.Pad, r.Width - 2 * K.Pad, K.LhPx);
        }
        fy += ch + MM(2);

        // C) flex-wrap:wrap + gap:2px：两个 47% 放得下，第三个换行（row-gap 同为 2px）
        float ww = 0.47f * lW, gp = P(2);
        string[] sc = { "wrap A", "wrap B", "wrap C 换行" };
        SKColor[] cc = { C("d6eaf8"), C("fadbd8"), C("d5f5e3") };
        for (int i = 0; i < 3; i++)
        {
            float xw = i % 2 == 0 ? lx : lx + ww + gp;
            float yw = i < 2 ? fy : fy + ch + gp;
            var r = Rect(xw, yw, ww, ch);
            Fill(c, r, cc[i]);
            Para(c, Segs((sc[i], K.Cell)), r.Left + K.Pad, r.Top + K.Pad, r.Width - 2 * K.Pad, K.LhPx);
        }
        fy += 2 * ch + gp + MM(2);

        // D) 嵌套 flex:1 / flex:1，右列内再套 flex:1 + flex:2（比例全部手算）
        float bd = P(1);
        float nestInner = 2 * P(K.Lbl.Size * 1.2f) + MM(2) + MM(4);
        float nestH = nestInner + 2 * bd;
        Border(c, Rect(lx, fy, lW, nestH), Radii.Zero,
            Side.Solid(1, FtrLn), Side.Solid(1, FtrLn), Side.Solid(1, FtrLn), Side.Solid(1, FtrLn));
        float ncw = (lW - 2 * bd) / 2;              // flex:1 等分
        float nInX = lx + bd, nInY = fy + bd;
        var nest = new Style { Size = 8, Color = Ink };      // 只声明 font-size:8px，颜色继承 body
        Fill(c, Rect(nInX, nInY, ncw, nestInner), Cream);
        Para(c, Segs(("嵌套：flex:1 等分列左", nest)), nInX + MM(2), nInY + MM(2), ncw - 2 * MM(2), P(K.Lbl.Size * 1.2f));
        float nR = nInX + ncw;
        Fill(c, Rect(nR, nInY, ncw, nestInner), C("eafaf1"));
        float barY = Para(c, Segs(("右列，列内再套", nest)), nR + MM(2), nInY + MM(2), ncw - 2 * MM(2), P(K.Lbl.Size * 1.2f)) + MM(1);
        float barW = ncw - 2 * MM(2), barH = MM(4);
        Fill(c, Rect(nR + MM(2), barY, barW / 3, barH), C("aed6f1"));    // flex:1
        Fill(c, Rect(nR + MM(2) + barW / 3, barY, barW * 2 / 3, barH), C("f5b7b1")); // flex:2
        fy += nestH + MM(2);

        // E) 右列：align-items:center + justify-content:center（交叉轴居中），拉伸到左列等高
        float colH = fy - MM(2) - top;
        var rr = Rect(rx, top, rW, colH);
        Fill(c, rr, C("f4ecf7"));
        Border(c, rr, Radii.Zero, Side.Dashed(1, Purple), Side.Dashed(1, Purple), Side.Dashed(1, Purple), Side.Dashed(1, Purple));
        var pb = Rect(rr.Left + rr.Width * 0.2f, rr.Top + (rr.Height - MM(14)) / 2, rr.Width * 0.6f, MM(14));
        Fill(c, pb, Purple);
        // 这个 div 既没有 .cell 类也没写 font-size → 继承 body 默认 16px；
        // align-items:center + justify-content:center → 两个轴都居中，基线要自己算。
        TextMid(c, Segs(("交叉轴居中", new Style { Size = 16, Color = SKColors.White })),
            pb.Left, pb.MidY, pb.Width, Align.Center);

        return fy - MM(2);
    }

    // ======================= 3.5 数据表格 + 超长 URL 断行 =======================
    private float Table(SKCanvas c, Cfg K, float y)
    {
        y = H2b(c, "3.5 数据表格", y) + MM(0.4f);

        // 列宽 12% / 40% / 20% / 28%，border-collapse:collapse（共边，不做加倍处理，差 1px 级）
        float[] cw = { 0.12f * CW, 0.40f * CW, 0.20f * CW, 0.28f * CW };
        float[] cx = new float[5];
        cx[0] = ML;
        for (int i = 1; i < 5; i++) cx[i] = cx[i - 1] + cw[i - 1];

        float padV = MM(1.2f), padVb = MM(1), padH = MM(2);
        float lh = 11.2f;                                  // 8.5px 行盒（对齐 truth 的 14.6pt 行距）
        float thH = P(lh) + 2 * padV, trH = P(lh) + 2 * padVb;
        float tTop = y;

        string[] heads = { "编号 Code", "名称 Name", "数量 Qty", "金额 Amount (¥)" };
        Fill(c, Rect(ML, y, CW, thH), Slate);
        for (int i = 0; i < 4; i++)
            Cell(c, heads[i], Rect(cx[i], y, cw[i], thH), K.Th, padV, padH, i >= 2 ? Align.Right : Align.Left, lh);
        y += thH;

        string[][] body =
        {
            new[] { "A-001", "工业级渲染管线 Industrial pipeline", "12", "1,280.00" },
            new[] { "A-002", "矢量字体子集 Vector subset", "3", "96.50" },
            new[] { "B-014", "分页引擎 Pagination engine", "1", "2,400.00" },
            new[] { "B-015", "阴影合成器 Shadow compositor", "8", "640.00" },
            new[] { "C-101", "渐变缓存 Gradient cache", "24", "1,152.00" },
            new[] { "C-102", "Alpha 通道混合 Alpha blending", "6", "306.00" },
            new[] { "D-200", "CJK 换行分析 Line-break CJK", "2", "780.00" },
        };
        for (int ri = 0; ri < body.Length; ri++)
        {
            if (ri % 2 == 1) Fill(c, Rect(ML, y, CW, trH), C("f8f9f9"));   // 斑马纹
            for (int ci = 0; ci < 4; ci++)
                Cell(c, body[ri][ci], Rect(cx[ci], y, cw[ci], trH), ci == 0 ? K.TdB : K.Td,
                    padVb, padH, ci >= 2 ? Align.Right : Align.Left, lh);
            y += trH;
        }

        // tfoot：colspan=2 合计行
        Fill(c, Rect(ML, y, CW, trH), C("eaeded"));
        Cell(c, "合计 Total (7 行明细)", Rect(cx[0], y, cw[0] + cw[1], trH), K.TdB, padVb, padH, Align.Left, lh);
        Cell(c, "56", Rect(cx[2], y, cw[2], trH), K.TdB, padVb, padH, Align.Right, lh);
        Cell(c, "¥ 6,654.50", Rect(cx[3], y, cw[3], trH), K.TdB, padVb, padH, Align.Right, lh);
        float tBottom = y + trH;

        // 网格线：thead 用 #5d6d7e，tbody/tfoot 用 #d5d8dc（1px 实底条带，等价 collapse 后共边）
        var hc = C("5d6d7e"); var bc = C("d5d8dc");
        float ln = P(1);
        for (int ri = 0; ri <= 7; ri++)   // 7 行明细 + tfoot 的行间横线
            Fill(c, new SKRect(ML, tTop + thH + ri * trH - ln / 2, ML + CW, tTop + thH + ri * trH + ln / 2), bc);
        Fill(c, new SKRect(ML, tTop, ML + CW, tTop + ln), hc);                                  // 表头外框
        Fill(c, new SKRect(ML, tTop + thH - ln / 2, ML + CW, tTop + thH + ln / 2), hc);         // thead/tbody 分隔
        Fill(c, new SKRect(ML, tBottom - ln, ML + CW, tBottom), bc);                             // 末行下边
        for (int ci = 1; ci < 4; ci++)
        {
            Fill(c, new SKRect(cx[ci] - ln / 2, tTop, cx[ci] + ln / 2, tTop + thH), hc);        // thead 竖线
            Fill(c, new SKRect(cx[ci] - ln / 2, tTop + thH, cx[ci] + ln / 2, tBottom), bc);     // 数据区竖线
        }
        Fill(c, new SKRect(ML, tTop, ML + ln, tTop + thH), hc);            // 外框左右：表头段深色
        Fill(c, new SKRect(ML + CW - ln, tTop, ML + CW, tTop + thH), hc);
        Fill(c, new SKRect(ML, tTop + thH, ML + ln, tBottom), bc);          // 数据段浅色
        Fill(c, new SKRect(ML + CW - ln, tTop + thH, ML + CW, tBottom), bc);

        // ---- word-break:break-all 超长 URL ----
        y = tBottom + MM(1.2f);
        var wb = new Style { Size = 9, Color = Ink };
        string url = "超长URL自动断行验证：https://example.org/very/long/path/文件与单词混排abcdefghij无空格也可断开以适配窄容器supercalifragilistic 末行。";
        float pv = MM(1.5f), bl = P(3);
        float innerW = CW - bl - 2 * MM(1.5f);
        var lines = WrapLines(Flatten(Segs((url, wb)), true), innerW);   // breakAll：逐字可断
        float txtH = lines.Sum(l => P(LineBoxPx(l.Atoms, 11.25f)));
        var box = Rect(ML, y, CW, txtH + 2 * pv);
        Fill(c, box, Cream);
        Fill(c, new SKRect(box.Left, box.Top, box.Left + bl, box.Bottom), DOrange);
        float ty = box.Top + pv;
        foreach (var l in lines) ty += DrawLine(c, l, box.Left + bl + MM(1.5f), ty, 11.25f, Align.Left, innerW);

        return ty;
    }

    // ======================= 本页私有辅助（核心层没有的） =======================

    /// <summary>单元格：内边距 + 单行/多行文本 + 左/右对齐（表格全靠这里手排）。</summary>
    private float Cell(SKCanvas c, string text, SKRect box, Style s, float padV, float padH, Align al, float lhPx)
    {
        float innerW = box.Width - 2 * padH;
        return Para(c, Segs((text, s)), box.Left + padH, box.Top + padV, innerW, lhPx, al);
    }

    /// <summary>行内半透明背景高亮：按每个行盒的实测文字宽铺 A(色) 底，文字仍全不透明。</summary>
    private float ParaInlineBg(SKCanvas c, IReadOnlyList<Run> runs, float x, float y, float maxW,
                               float lineHeightPx, SKColor bg)
    {
        foreach (var l in WrapLines(Flatten(runs, false), maxW))
        {
            float h = P(LineBoxPx(l.Atoms, lineHeightPx));
            Fill(c, new SKRect(x, y, x + Math.Min(l.Width, maxW), y + h), bg);   // 只盖住文字宽度
            y += DrawLine(c, l, x, y, lineHeightPx, Align.Left, maxW);
        }
        return y;
    }

    /// <summary>单行文字在给定垂直中心线上居中的基线（Skia 只吃基线，CSS 的垂直居中要自己算）。</summary>
    private void TextMid(SKCanvas c, IReadOnlyList<Run> runs, float x, float cy, float boxW, Align al = Align.Left)
    {
        var s = runs[0].S;
        var f = Font(Tf(s.Kind, s.Bold, s.Italic), P(s.Size));
        f.GetFontMetrics(out var m);
        Text(c, runs, x, cy + (-m.Ascent - m.Descent) / 2, al, boxW);
    }

    /// <summary>把 CSS 的 flex 固定基准盒排成一行：gap + 各项 margin-left，超出则按 flex-shrink:1 等比收缩。</summary>
    private static (float[] xs, float[] ws) FlexBasis(float x0, float total, float[] basis, float gap, float[] marginLeft)
    {
        int n = basis.Length;
        float avail = total - gap * (n - 1) - marginLeft.Sum();
        float nat = basis.Sum();
        float k = nat > avail && nat > 0 ? avail / nat : 1f;
        var ws = basis.Select(w => w * k).ToArray();
        var xs = new float[n];
        float x = x0 + marginLeft[0];
        for (int i = 0; i < n; i++)
        {
            xs[i] = x;
            if (i + 1 < n) x += ws[i] + gap + marginLeft[i + 1];
        }
        return (xs, ws);
    }

    /// <summary>flex:1 等分：先扣掉 gap 与各项 margin-left，剩余平分（CSS grow 的口径）。</summary>
    private static (float[] xs, float w) FlexGrow(float x0, float total, int n, float gap, float[] marginLeft)
    {
        float w = (total - gap * (n - 1) - marginLeft.Sum()) / n;
        var xs = new float[n];
        float x = x0 + marginLeft[0];
        for (int i = 0; i < n; i++)
        {
            xs[i] = x;
            if (i + 1 < n) x += w + gap + marginLeft[i + 1];
        }
        return (xs, w);
    }

    /// <summary>CSS 盒的等距外扩（参数按 CSS px；边框、spread 阴影、图片 content 盒都靠它）。</summary>
    private static SKRect Inflate(SKRect r, float px)
    {
        float d = P(px);
        return new SKRect(r.Left - d, r.Top - d, r.Right + d, r.Bottom + d);
    }

    /// <summary>按 pt 内缩（padding 已是 pt 时用这个，避免和 Inflate 的 px 口径混起来）。</summary>
    private static SKRect Shrink(SKRect r, float pt) =>
        new(r.Left + pt, r.Top + pt, r.Right - pt, r.Bottom - pt);

    /// <summary>标签行：.lbl 只有行高与 1px 下边距，基线手算居中。</summary>
    private void DrawLbl(SKCanvas c, string text, float x, float top, Style s)
    {
        var f = Font(Tf(s.Kind, s.Bold, s.Italic), P(s.Size));
        f.GetFontMetrics(out var m);
        float boxH = P(LineBoxPx(new List<Atom> { new Atom(text, s) }, s.Size * 1.25f));
        Text(c, Segs((text, s)), x, top + boxH / 2 + (-m.Ascent - m.Descent) / 2);
    }

    /// <summary>
    /// 图片（无损通路版）。核心 Img 走 SKBitmap→SKImage 的不透明通路，Skia 的 PDF 后端会把这种
    /// 图重编码成 DCTDecode 且写死 /ColorTransform 0（数据其实是 YCbCr，被当成 RGB 读）——
    /// 400×267 的样张只落 1097 字节，渲出来满是 16px 方块且整体偏灰偏色。带 alpha 的位图反而走
    /// 无损 FlateDecode（样张里的 PNG logo 就是好的）。所以这里先把 JPEG 拷进一张 Premul 的
    /// RGBA8888 surface，逼它走无损通路，再按高质量采样绘制。
    /// </summary>
    private void ImgHi(SKCanvas c, string fileName, SKRect dest, Radii rad = default)
    {
        var path = Path.Combine(_ctx.AssetsDir, fileName);
        var cached = LoadAsset(path);
        if (cached is null) { Fail(c, dest, "image"); return; }
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
        DrawImgDest(c, cached, dest, rad, paint);
    }

    /// <summary>
    /// 样张解码缓存：一次 Generate 要跑两趟（记字趟 + 真绘趟），photo.jpg 还被三个盒子引用，
    /// 解码 + Premul 拷贝按文件缓存后只付一次。进程级、永不释放（就两三张样张）。
    /// </summary>
    static readonly Dictionary<string, SKImage?> _assets = new();
    static readonly object _assetLock = new();

    static SKImage? LoadAsset(string path)
    {
        lock (_assetLock)
        {
            if (_assets.TryGetValue(path, out var hit)) return hit;
            var img = DecodePremul(path);
            _assets[path] = img;
            return img;
        }
    }

    static SKImage? DecodePremul(string path)
    {
        using var src = SKBitmap.Decode(path);
        if (src is null) return null;

        var info = new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surf = SKSurface.Create(info);
        if (surf is null) return SKImage.FromBitmap(src);
        surf.Canvas.Clear(SKColors.Transparent);
        surf.Canvas.DrawBitmap(src, 0, 0);
        // 关键一步：把左上角一个像素的 alpha 改成 254（BlendMode.Src 直接覆写，肉眼完全看不出来）。
        // 只要图里存在非 255 的 alpha，Skia 的 PDF 后端就改走无损 FlateDecode 通路；
        // 全不透明时它会走有损 DCT 重编码，样张尺寸下糊成 16px 方块 + 偏灰。
        using (var poke = new SKPaint
        {
            Color = src.GetPixel(0, 0).WithAlpha(254),
            BlendMode = SKBlendMode.Src,
        })
            surf.Canvas.DrawRect(new SKRect(0, 0, 1, 1), poke);
        return surf.Snapshot();
    }

    /// <summary>把 SKImage 画到目标盒（可选圆角裁剪），供 ImgHi 用。</summary>
    private void DrawImgDest(SKCanvas c, SKImage img, SKRect dest, Radii rad, SKPaint paint)
    {
        int save = c.Save();
        if (rad.Max > 0)
        {
            using var clip = new SKPath();
            clip.AddRoundRect(RRect(dest, rad));
            c.ClipPath(clip, SKClipOperation.Intersect, true);
        }
        c.DrawImage(img, dest, paint);
        c.RestoreToCount(save);
    }

    /// <summary>椭圆描边环：border-radius:50% + 实线边框（核心 Border 只会画四条直边，圆角处会露馅）。</summary>
    private void Ring(SKCanvas c, SKRect centerLine, float strokePt, SKColor color)
    {
        using var p = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokePt,
            IsAntialias = true,
        };
        c.DrawOval(centerLine, p);
    }
}
