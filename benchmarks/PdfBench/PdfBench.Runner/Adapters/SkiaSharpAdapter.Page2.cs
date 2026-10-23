using SkiaSharp;

namespace PdfBench.Runner.Adapters;

/// <summary>
/// 页 2 —— 边框家族与圆角：逐块手抄 challenge.html 第 120-196 行。
/// .row 是 display:flex; gap:4mm；2.1 的 .cell 无 flex:1（内容宽打包、左起），
/// 2.2/2.3 的等分格按 (CW - 总gap) / 格数 手算；align-items:center 的行自己算垂直居中。
/// 圆角盒上的虚线/整圆/整椭圆/胶囊用「中心线描边」近似（共享 Border 的逐边条带
/// 只适合直角盒），overflow:hidden 子元素背景用真裁剪层。
/// </summary>
public sealed partial class SkiaSharpAdapter
{
    // 9px 正文 / 8px 标注；行高 normal 按 1.35 取（与基准其它手抄选手同口径）
    static readonly Style S9 = new() { Size = 9 };
    static readonly Style S9R = new() { Size = 9, Color = DarkRed };   // currentColor 格
    static readonly Style S9W = new() { Size = 9, Bold = true, Color = SKColors.White };
    static readonly Style S8 = new() { Size = 8, Color = LblC };
    const float LH9 = 12.15f;                                          // 9px × 1.35（CSS px）

    partial void DrawPage2(SKCanvas c)
    {
        Frame(c, "Page 2 — Borders & Corners", 2);
        float gap = MM(4), pad = MM(2.5f), th = P(LH9);

        // ---------- h1 ----------
        float y = MT + P(21) + MM(4);
        y = Para(c, Segs(("边框家族与圆角", new Style { Size = 20, Bold = true, Color = Slate })),
                 ML, y, CW, 23);

        // ===== 2.1 四种线型 × 三种宽度（4 行 × 3 格，内容宽打包 + align-items:stretch）=====
        y = H2(c, "2.1 四种线型 × 三种宽度", y + MM(3.5f));
        (string txt, Side side)[][] styleRows =
        {
            new[] { ("solid 2px", Side.Solid(2, Red)), ("solid 4px", Side.Solid(4, Red)), ("solid 8px", Side.Solid(8, Red)) },
            new[] { ("dashed 2px", Side.Dashed(2, Blue)), ("dashed 4px", Side.Dashed(4, Blue)), ("dashed 4px+", Side.Dashed(8, Blue)) },
            new[] { ("dotted 2px", Side.Dotted(2, Green)), ("dotted 4px", Side.Dotted(4, Green)), ("double 8px", Side.Double(8, Purple)) },
            new[] { ("double 4px", Side.Double(4, Purple)), ("groove 4px", Side.Groove(4, DOrange)), ("ridge 4px", Side.Ridge(4, Teal)) },
        };
        foreach (var row in styleRows)
        {
            float maxB = row.Max(r => r.side.W);
            float h = th + 2 * pad + 2 * P(maxB);                     // 行高 = 最高格外框（stretch）
            float x = ML;
            foreach (var (txt, side) in row)
            {
                float bw = P(side.W);
                float w = WidthOf(S9, txt) + 2 * pad + 2 * bw;        // flex-basis:auto → 内容宽
                Box(c, Rect(x, y, w, h), null, side);
                Para(c, Segs((txt, S9)), x + bw + pad, y + bw + pad, w - 2 * (bw + pad), LH9);
                x += w + gap;
            }
            y += h + MM(1.5f);
        }

        // ===== 2.2 逐边异构（3 等分格，padding 5mm 3mm）=====
        y = H2(c, "2.2 逐边异构", y + MM(2));
        float sw = (CW - 2 * gap) / 3;
        float sh = th + 2 * MM(5) + 2 * P(3);
        float x0 = ML;
        var rA = Rect(x0, y, sw, sh);
        Border(c, rA, Radii.Zero, Side.Solid(3, Blue), Side.Dashed(3, Red), Side.Dotted(3, Green), Side.Double(4, Orange));
        Para(c, Segs(("四边四线型：上实/右虚/下点/左双", S9)), x0 + MM(3) + P(4), y + MM(5) + P(3),
             sw - 2 * MM(3) - P(7), LH9);
        x0 += sw + gap;
        Border(c, Rect(x0, y, sw, sh), Radii.Zero,
               Side.Solid(3, Red), Side.Solid(3, Blue), Side.Solid(3, Green), Side.Solid(3, Orange));
        Para(c, Segs(("四边同线型异色：上红/右蓝/下绿/左橙", S9)), x0 + MM(3) + P(3), y + MM(5) + P(3),
             sw - 2 * MM(3) - P(6), LH9);
        x0 += sw + gap;
        Border(c, Rect(x0, y, sw, sh), Radii.Zero,
               Side.Solid(2, DarkRed), Side.Solid(2, DarkRed), Side.Solid(2, DarkRed), Side.Solid(2, DarkRed)); // currentColor
        Para(c, Segs(("currentColor 边框：边色随字色变红", S9R)), x0 + MM(3) + P(2), y + MM(5) + P(2),
             sw - 2 * MM(3) - P(4), LH9);
        y += sh + MM(1.5f);

        // ---- 单边行（padding 2mm 3mm；行高按最高的上下边格 stretch）----
        float bh = th + 2 * MM(2) + 2 * P(2);
        x0 = ML;
        var r1 = Rect(x0, y, sw, bh);
        Fill(c, r1, Tint);
        Border(c, r1, Radii.Zero, Side.NoBorder, Side.NoBorder, Side.Solid(3, Purple), Side.NoBorder);
        Para(c, Segs(("仅底边：文章式分隔线", S9)), x0 + MM(3), y + MM(2), sw - 2 * MM(3), LH9);
        x0 += sw + gap;
        var r2 = Rect(x0, y, sw, bh);
        Fill(c, r2, Tint);
        Border(c, r2, Radii.Zero, Side.NoBorder, Side.NoBorder, Side.NoBorder, Side.Solid(5, Green));
        Para(c, Segs(("仅左边：引文强调条", S9)), x0 + MM(3) + P(5), y + MM(2), sw - 2 * MM(3) - P(5), LH9);
        x0 += sw + gap;
        var r3 = Rect(x0, y, sw, bh);
        Border(c, r3, Radii.Zero, Side.Solid(2, Blue), Side.NoBorder, Side.Solid(2, Blue), Side.NoBorder);
        Para(c, Segs(("上下边：band 样式", S9)), x0 + MM(3), y + MM(2) + P(2), sw - 2 * MM(3), LH9);
        y += bh;

        // ===== 2.3 圆角 =====
        y = H2(c, "2.3 圆角", y + MM(3.5f));
        // ---- 五格定尺寸圆角盒（26×18mm，align-items:center）----
        float bw26 = MM(26), bh18 = MM(18);
        (string txt, Radii rad)[] radCells =
        {
            ("r=0", Radii.Zero), ("4px", Rad(4)), ("10px", Rad(10)),
            ("8/2 对角", Rad(8, 2)), ("四角异值", Rad(14, 2, 14, 2)),
        };
        x0 = ML;
        foreach (var (txt, rad) in radCells)
        {
            var rc = Rect(x0, y, bw26, bh18);
            Fill(c, rc, C("d6eaf8"), rad);
            Border(c, rc, rad, Side.Solid(2, Blue), Side.Solid(2, Blue), Side.Solid(2, Blue), Side.Solid(2, Blue));
            Para(c, Segs((txt, S9)), x0 + P(2) + pad, y + P(2) + pad, bw26 - 2 * (P(2) + pad), LH9);
            x0 += bw26 + gap;
        }
        y += bh18 + MM(2);

        // ---- 正圆 / 椭圆 / 胶囊 / 圆角+虚线（align-items:center，行高 18mm）----
        
        x0 = ML;
        var circ = Rect(x0, y, bh18, bh18);                            // 18×18mm
        Fill(c, circ, C("fadbd8"), EllipseR(circ));
        StrokeBorder(c, circ, EllipseR(circ), 2, DarkRed);             // border-radius:50% → 正圆
        x0 += bh18 + gap + MM(2);
        Text(c, Segs(("50% 正圆", S8)), x0, LblBase(y, bh18));
        x0 += WidthOf(S8, "50% 正圆") + MM(2) + gap;
        var ell = Rect(x0, y, MM(11), bh18);                           // 11×18mm → 椭圆
        Fill(c, ell, C("d5f5e3"), EllipseR(ell));
        StrokeBorder(c, ell, EllipseR(ell), 2, Green);
        x0 += MM(11) + gap + MM(2);
        Text(c, Segs(("50% 椭圆", S8)), x0, LblBase(y, bh18));
        x0 += WidthOf(S8, "50% 椭圆") + MM(2) + gap;
        var pill = Rect(x0, y + (bh18 - MM(10)) / 2, MM(34), MM(10));  // 34×10mm r=5mm=高/2
        var pillRad = AllRad(MM(5));
        Fill(c, pill, C("fdebd0"), pillRad);
        StrokeBorder(c, pill, pillRad, 3, DOrange);
        Text(c, Segs(("胶囊 pill r=高度/2", S9)), pill.Left + P(3), (pill.Top + pill.Bottom) / 2 + P(9 * 0.35f),
             Align.Center, pill.Width - 2 * P(3));
        x0 += MM(34) + gap;
        var dashBox = Rect(x0, y, bw26, bh18);
        var dashRad = Rad(10);
        Fill(c, dashBox, C("d6eaf8"), dashRad);
        StrokeBorder(c, dashBox, dashRad, 3, Purple, dashed: true);    // 圆角+虚线：中心线描边近似
        x0 += bw26 + MM(2) + gap;
        Text(c, Segs(("圆角+虚线复合", S8)), x0, LblBase(y, bh18));
        y += bh18 + MM(2);

        // ---- 复合盒（两等分格：厚边+大圆角+内边距 / overflow:hidden 背景裁圆角）----
        float half = (CW - gap) / 2;
        float padA = MM(3), bA = P(6);
        float txtAH = BlockH(S9, "厚边框(6px)+大圆角(14px)+内边距+背景色 复合盒：边框与背景的圆角必须同心，内边距把内容从边框推开。",
                             half - 2 * padA - 2 * bA);
        float compH = txtAH + 2 * padA + 2 * bA;
        var comp = Rect(ML, y, half, compH);
        Box(c, comp, C("f8f9f9"), Side.Solid(6, Slate), Rad(14));      // 背景与边框同外圆角，条带覆边 → 同心
        Para(c, Segs(("厚边框(6px)+大圆角(14px)+内边距+背景色 复合盒：边框与背景的圆角必须同心，内边距把内容从边框推开。", S9)),
             ML + bA + padA, y + bA + padA, half - 2 * (bA + padA), LH9);

        float bB = P(2), padB = MM(3), padBV = MM(2);
        float headH = P(LH9) + 2 * padBV;
        string bodyTxt = "overflow:hidden 子元素背景填满左上圆角";
        float bodyH = BlockH(S9, bodyTxt, half - 2 * bB - 2 * padB) + 2 * padBV;
        float bx = ML + half + gap;
        var clipR = new SKRect(bx + bB, y + bB, bx + half - bB, y + compH - bB);
        int save = c.Save();
        using (var clip = new SKPath())
        {
            clip.AddRoundRect(RRect(clipR, Shrink(Rad(10), bB)));
            c.ClipPath(clip, SKClipOperation.Intersect, true);
        }
        Fill(c, new SKRect(bx, y, bx + half, y + headH), Green);       // 子元素背景涂满并被外圆角裁切
        Para(c, Segs(("裁切过头圆角", S9W)), bx + padB, y + padBV, half - 2 * padB, LH9);
        Para(c, Segs((bodyTxt, S9)), bx + padB, y + headH + padBV, half - 2 * padB, LH9);
        c.RestoreToCount(save);
        Border(c, Rect(bx, y, half, compH), Rad(10),
               Side.Solid(2, Green), Side.Solid(2, Green), Side.Solid(2, Green), Side.Solid(2, Green));
        y += compH + MM(2);

        // ===== 2.4 边框即图形（三角形：CSS 用零尺寸盒 + 透明边框拼出，Skia 等价 = 直接填路径）=====
        y = H2(c, "2.4 边框即图形", y + MM(3.5f));
        float t1w = MM(28), t1h = MM(18), t2w = MM(16), t2h = MM(12);
        x0 = ML;
        Tri(c, new SKPoint(x0, y + t1h), new SKPoint(x0 + t1w / 2, y), new SKPoint(x0 + t1w, y + t1h), Red);
        x0 += t1w + gap;
        Text(c, Segs(("border 三角形（等腰）", S8)), x0, LblBase(y, t1h));
        x0 += WidthOf(S8, "border 三角形（等腰）") + gap + MM(3);
        float t2y = y + (t1h - t2h) / 2;
        Tri(c, new SKPoint(x0, t2y), new SKPoint(x0, t2y + t2h), new SKPoint(x0 + t2w, t2y + t2h), Orange);
        x0 += t2w + gap;
        Text(c, Segs(("直角三角形（右向）", S8)), x0, LblBase(y, t1h));
        x0 += WidthOf(S8, "直角三角形（右向）") + gap + MM(3);
        Tri(c, new SKPoint(x0, y), new SKPoint(x0 + t1w, y), new SKPoint(x0 + t1w / 2, y + t1h), Blue);
        x0 += t1w + gap;
        Text(c, Segs(("倒三角", S8)), x0, LblBase(y, t1h));
        y += t1h;
    }

    // ---------- 页 2 私有辅助（共享层缺的画布原语）----------

    /// <summary>行内 label（8px）在给定行框内垂直居中的基线 y。</summary>
    static float LblBase(float rowTop, float rowH) => rowTop + rowH / 2 + P(8 * 0.35f);

    /// <summary>四角同半径（pt 值，Rad() 只收 CSS px，mm 半径需要它）。</summary>
    static Radii AllRad(float pt) =>
        new(new SKPoint(pt, pt), new SKPoint(pt, pt), new SKPoint(pt, pt), new SKPoint(pt, pt));

    /// <summary>border-radius:50% 的椭圆半径（rx=宽/2，ry=高/2，SKRoundRect 原生支持）。</summary>
    static Radii EllipseR(SKRect r)
    {
        var p = new SKPoint(r.Width / 2, r.Height / 2);
        return new Radii(p, p, p, p);
    }

    static Radii Shrink(Radii rad, float k) =>
        new(Sub(rad.TL, k), Sub(rad.TR, k), Sub(rad.BR, k), Sub(rad.BL, k));

    static SKPoint Sub(SKPoint p, float k) => new(Math.Max(0, p.X - k), Math.Max(0, p.Y - k));

    /// <summary>
    /// 圆角盒边框的「中心线描边」：CSS 边框是外圆角到内圆角之间的环带，
    /// 中心线半径 = 外半径 - w/2，等宽描边即近似。共享 Border 的逐边条带只适合直角盒，
    /// 圆角虚线/整圆/椭圆/胶囊必须走这里。
    /// </summary>
    void StrokeBorder(SKCanvas c, SKRect r, Radii rad, float wPx, SKColor col, bool dashed = false)
    {
        float w = P(wPx), k = w / 2;
        var mid = new SKRect(r.Left + k, r.Top + k, r.Right - k, r.Bottom - k);
        using var p = new SKPaint
        {
            Color = col,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = w,
        };
        if (dashed) p.PathEffect = SKPathEffect.CreateDash(new[] { w * 3, w * 2 }, 0);
        c.DrawRoundRect(RRect(mid, Shrink(rad, k)), p);
    }

    /// <summary>零尺寸边框三角形 → 直接三顶点填充。</summary>
    void Tri(SKCanvas c, SKPoint a, SKPoint b, SKPoint d, SKColor col)
    {
        using var path = new SKPath();
        path.MoveTo(a);
        path.LineTo(b);
        path.LineTo(d);
        path.Close();
        using var p = new SKPaint { Color = col, IsAntialias = true };
        c.DrawPath(path, p);
    }

    /// <summary>预排版量高：同一断行器先量出文本块高度，盒子才能定高。</summary>
    float BlockH(Style s, string txt, float maxW)
    {
        var lines = WrapLines(Flatten(Segs((txt, s)), false), maxW);
        float h = 0;
        foreach (var l in lines) h += P(LineBoxPx(l.Atoms, LH9));
        return h;
    }
}
