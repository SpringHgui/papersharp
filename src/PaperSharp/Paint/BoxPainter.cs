using SkiaSharp;
using PaperSharp.Css;

namespace PaperSharp.Paint;

/// <summary>
/// 盒子装饰绘制：box-shadow → 背景（纯色/渐变，含圆角）→ 边框（含圆角）。
/// 统一处理 opacity，PNG 与 PDF 两条路径共用。
/// </summary>
public static class BoxPainter
{
    public static void PaintDecor(SKCanvas canvas, SKRect r, ComputedStyle s, bool backgroundClippedToText = false)
    {
        float opacity = s.Opacity <= 0 ? 0 : s.Opacity >= 1 ? 1 : s.Opacity;
        if (opacity <= 0.001f) return;

        bool rounded = HasRadius(s);
        var radii = CornerRadii(r, s);
        bool sq = rounded && s.CornerShape == "squircle";

        // 外阴影在背景之下、内阴影在背景之上（CSS 的层叠次序），多层时第一条画在最上层 → 全部倒序铺
        if (s.Shadows is { Count: > 0 } layers)
        {
            for (int i = layers.Count - 1; i >= 0; i--)
                if (!layers[i].Inset) PaintShadow(canvas, r, s, layers[i], radii, rounded, sq, opacity);
        }

        // 背景（background-clip:text 时不铺满盒矩形——改由文字承载，见 Painter）
        bool hasBg = !backgroundClippedToText && (!s.BackgroundColor.Equals(SKColor.Empty) || s.Background != null);
        if (hasBg)
        {
            using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            SKShader? shader = s.Background?.BuildShader(r, opacity);
            if (shader != null) p.Shader = shader;
            else p.Color = s.BackgroundColor.WithAlpha((byte)Math.Round(s.BackgroundColor.Alpha * opacity));
            DrawRR(canvas, r, radii, rounded, sq, p);
            shader?.Dispose();
        }

        // 内阴影：铺在背景之上、边框之下
        if (s.Shadows is { Count: > 0 } ins)
        {
            // CSS：inset 阴影的形状与裁切都走 **padding box**（外阴影才贴着 border box）。
            var pr = new SKRect(r.Left + s.BorderWidthLeft, r.Top + s.BorderWidthTop,
                                r.Right - s.BorderWidthRight, r.Bottom - s.BorderWidthBottom);
            float bw = Math.Max(Math.Max(s.BorderWidthTop, s.BorderWidthRight), Math.Max(s.BorderWidthBottom, s.BorderWidthLeft));
            var pradii = ScaleRadii(radii, bw);
            bool inRound = rounded && AnyRadius(pradii);
            for (int i = ins.Count - 1; i >= 0; i--)
                if (ins[i].Inset) PaintInsetShadow(canvas, pr, ins[i], pradii, inRound, inRound && sq, opacity);
        }

        // 边框
        PaintBorder(canvas, r, s, radii, rounded, sq, opacity);
    }

    /// <summary>inset box-shadow：把"盒形之外"的一圈涂成阴影色，再裁进盒内 —— 等价于内边缘往里的投影。</summary>
    static void PaintInsetShadow(SKCanvas canvas, SKRect r, BoxShadow sh, SKPoint[] radii, bool rounded, bool sq, float opacity)
    {
        float sigma = sh.Blur / 2f;
        var hole = new SKRect(r.Left + sh.OffsetX + sh.Spread, r.Top + sh.OffsetY + sh.Spread,
                              r.Right + sh.OffsetX - sh.Spread, r.Bottom + sh.OffsetY - sh.Spread);

        // 环形不用布尔运算：一条路径里先描外框再描内孔，EvenOdd 填充天然得到"外框减内孔"。
        // 外框要远大于盒子，模糊后的外边缘才不会漏进可见区（可见区只有内边缘那一圈）。
        float pad = Math.Max(r.Width, r.Height);
        using var ring = new SKPath { FillType = SKPathFillType.EvenOdd };
        ring.AddRect(new SKRect(r.Left - pad, r.Top - pad, r.Right + pad, r.Bottom + pad));
        if (sq) ring.AddPath(MakeSquirclePath(hole, radii));
        else if (rounded) ring.AddRoundRect(MakeRR(hole, radii));
        else ring.AddRect(hole);

        using var clip = BoxOutline(r, radii, rounded, sq);
        using var p = new SKPaint
        {
            Color = sh.Color.WithAlpha((byte)Math.Round(sh.Color.Alpha * opacity)),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        if (sigma > 0.3f) p.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma);
        int save = canvas.Save();
        canvas.ClipPath(clip, antialias: true);
        canvas.DrawPath(ring, p);
        canvas.RestoreToCount(save);
    }

    /// <summary>盒形轮廓（圆角 / squircle / 直角矩形）。</summary>
    static SKPath BoxOutline(SKRect r, SKPoint[] radii, bool rounded, bool sq)
    {
        if (sq) return MakeSquirclePath(r, radii);
        var path = new SKPath();
        if (rounded) path.AddRoundRect(MakeRR(r, radii));
        else path.AddRect(r);
        return path;
    }

    /// <summary>四角半径（TL/TR/BR/BL，每角 x/y 两轴）：border-radius 的百分比按本盒边框盒的宽高在此折算。
    /// 相邻半径之和超过边长时由 SKRoundRect / squircle 自己等比收缩（CSS 规则）。</summary>
    public static SKPoint[] CornerRadii(SKRect r, ComputedStyle s)
    {
        float[]? p = s.BorderRadiusPercent;
        var abs = new[]
        {
            new SKPoint(s.BorderRadiusTopLeft, s.BorderRadiusTopLeftY),
            new SKPoint(s.BorderRadiusTopRight, s.BorderRadiusTopRightY),
            new SKPoint(s.BorderRadiusBottomRight, s.BorderRadiusBottomRightY),
            new SKPoint(s.BorderRadiusBottomLeft, s.BorderRadiusBottomLeftY),
        };
        if (p == null) return abs;
        float w = Math.Max(0, r.Width), h = Math.Max(0, r.Height);
        for (int i = 0; i < 4; i++)
        {
            float px = p[i * 2], py = p[i * 2 + 1];
            if (px > 0) abs[i].X += px * w;
            if (py > 0) abs[i].Y += py * h;
        }
        return abs;
    }

    /// <summary>盒上是否写过任何 border-radius（含只在百分比里的角）。</summary>
    public static bool HasRadius(ComputedStyle s) => s.BorderRadiusPercent != null
        || s.BorderRadiusTopLeft > 0 || s.BorderRadiusTopRight > 0 || s.BorderRadiusBottomRight > 0 || s.BorderRadiusBottomLeft > 0
        || s.BorderRadiusTopLeftY > 0 || s.BorderRadiusTopRightY > 0 || s.BorderRadiusBottomRightY > 0 || s.BorderRadiusBottomLeftY > 0;

    /// <summary>图片/SVG 这类替换内容要裁进**内容盒的圆角**：CSS 口径是角半径逐轴减去它外面的边框与内边距
    /// （下限 0），所以 12px 圆角 + 3px 边框的图，可见圆角是 9px。返回 canvas.Save() 的层号，调用方 Restore。</summary>
    public static int ClipReplacedContent(SKCanvas canvas, SKRect r, SKRect dst, ComputedStyle s)
    {
        int save = canvas.Save();
        if (!HasRadius(s)) return save;
        var rad = CornerRadii(r, s);
        float hl = s.BorderWidthLeft + s.PaddingLeft, hr = s.BorderWidthRight + s.PaddingRight;
        float vt = s.BorderWidthTop + s.PaddingTop, vb = s.BorderWidthBottom + s.PaddingBottom;
        static SKPoint Shrink(SKPoint p, float hx, float vy)
            => new(Math.Max(0, p.X - hx), Math.Max(0, p.Y - vy));
        var c = new[]
        {
            Shrink(rad[0], hl, vt), Shrink(rad[1], hr, vt), Shrink(rad[2], hr, vb), Shrink(rad[3], hl, vb),
        };
        if (c.All(p => p.X <= 0 && p.Y <= 0)) return save;
        using var outline = BoxOutline(dst, c, true, s.CornerShape == "squircle");
        canvas.ClipPath(outline, antialias: true);
        return save;
    }

    static void PaintShadow(SKCanvas canvas, SKRect r, ComputedStyle s, BoxShadow sh, SKPoint[] radii, bool rounded, bool sq, float opacity)
    {
        float sigma = sh.Blur / 2f;
        var sr = new SKRect(r.Left + sh.OffsetX - sh.Spread, r.Top + sh.OffsetY - sh.Spread,
                            r.Right + sh.OffsetX + sh.Spread, r.Bottom + sh.OffsetY + sh.Spread);
        using var p = new SKPaint
        {
            Color = sh.Color.WithAlpha((byte)Math.Round(sh.Color.Alpha * opacity)),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        if (sigma > 0.3f) p.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma);
        DrawRR(canvas, sr, radii, rounded, sq, p);
    }

    static void PaintBorder(SKCanvas canvas, SKRect r, ComputedStyle s, SKPoint[] radii, bool rounded, bool sq, float opacity)
    {
        float t = s.BorderWidthTop, rt = s.BorderWidthRight, b = s.BorderWidthBottom, l = s.BorderWidthLeft;
        if (t <= 0 && rt <= 0 && b <= 0 && l <= 0) return;

        if (!rounded)
        {
            // CSS 边框是**斜接梯形**：每条边一个四边形，外缘占满整条边，内缘按相邻两边收缩，
            // 四角 45° 相接。只有这套几何才解释得通两件事：① 0×0 盒 + 三条透明边 = CSS 三角形；
            // ② groove/ridge/inset/outset 的明暗分界沿斜接线走（重叠矩形画法两头都做不到）。
            using var p = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
            for (int side = 0; side < 4; side++)
            {
                float w = side switch { 0 => t, 1 => rt, 2 => b, _ => l };
                string style = side switch { 0 => s.BorderStyleTop, 1 => s.BorderStyleRight, 2 => s.BorderStyleBottom, _ => s.BorderStyleLeft };
                SKColor raw = side switch { 0 => s.BorderColorTop, 1 => s.BorderColorRight, 2 => s.BorderColorBottom, _ => s.BorderColorLeft };
                if (w <= 0 || style is "none" or "hidden") continue;
                var col = raw.WithAlpha((byte)Math.Round(raw.Alpha * opacity));
                var e = EdgePts(side, r, t, rt, b, l);
                if (style is "dashed" or "dotted")
                {
                    // 虚线沿梯形中线走（斜接后中线两端自然内缩）
                    var m0 = Mid(e, 0);
                    var m1 = Mid(e, 1);
                    float len = MathF.Sqrt((m1.X - m0.X) * (m1.X - m0.X) + (m1.Y - m0.Y) * (m1.Y - m0.Y));
                    var (on, off) = DashPattern(style, w, len);
                    using var sp = new SKPaint
                    {
                        Color = col, Style = SKPaintStyle.Stroke, StrokeWidth = w,
                        IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { on, off }, 0),
                        StrokeCap = style == "dotted" ? SKStrokeCap.Round : SKStrokeCap.Butt,
                    };
                    canvas.DrawLine(m0, m1, sp);
                    continue;
                }
                foreach (var (f0, f1, c) in Stripes(style, col, lit: side is 0 or 3))
                {
                    using var path = BandPath(e, f0, f1);
                    p.Color = c;
                    canvas.DrawPath(path, p);
                }
            }
            return;
        }

        // 圆角边框：描一条内缩的圆角矩形（四边取最大宽度做近似）。整圈只有一条描边，
        // 颜色跟着最宽那条边走（ComputedStyle.BorderColor）。
        var color = s.BorderColor.WithAlpha((byte)Math.Round(s.BorderColor.Alpha * opacity));
        float w2 = Math.Max(t, Math.Max(rt, Math.Max(b, l)));
        using var stroke = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = w2, IsAntialias = true };
        string dom = DominantDash(s);
        stroke.PathEffect = DashEffect(dom, w2);
        if (dom == "dotted") stroke.StrokeCap = SKStrokeCap.Round;
        var inset = new SKRect(r.Left + w2 / 2, r.Top + w2 / 2, r.Right - w2 / 2, r.Bottom - w2 / 2);
        if (sq)
        {
            using var path = MakeSquirclePath(inset, ScaleRadii(radii, w2 / 2));
            canvas.DrawPath(path, stroke);
            return;
        }
        var rr = MakeRR(inset, ScaleRadii(radii, w2 / 2));
        canvas.DrawRoundRect(rr, stroke);
    }

    /// <summary>一条边的斜接四边形：外缘两点 o0/o1 + 对应的内缘两点 i0/i1（沿盒顺时针：上→右→下→左）。</summary>
    static (SKPoint o0, SKPoint o1, SKPoint i0, SKPoint i1) EdgePts(int side, SKRect r, float t, float rt, float b, float l)
        => side switch
        {
            0 => (new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left + l, r.Top + t), new(r.Right - rt, r.Top + t)),
            1 => (new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Right - rt, r.Top + t), new(r.Right - rt, r.Bottom - b)),
            2 => (new(r.Right, r.Bottom), new(r.Left, r.Bottom), new(r.Right - rt, r.Bottom - b), new(r.Left + l, r.Bottom - b)),
            _ => (new(r.Left, r.Bottom), new(r.Left, r.Top), new(r.Left + l, r.Bottom - b), new(r.Left + l, r.Top + t)),
        };

    static SKPoint Lerp(SKPoint a, SKPoint c, float f) => new(a.X + (c.X - a.X) * f, a.Y + (c.Y - a.Y) * f);

    /// <summary>该边带宽分数区间 [f0,f1) 的子梯形（f=0 外缘、1 内缘）。</summary>
    static SKPath BandPath((SKPoint o0, SKPoint o1, SKPoint i0, SKPoint i1) e, float f0, float f1)
    {
        var p = new SKPath();
        p.MoveTo(Lerp(e.o0, e.i0, f0));
        p.LineTo(Lerp(e.o1, e.i1, f0));
        p.LineTo(Lerp(e.o1, e.i1, f1));
        p.LineTo(Lerp(e.o0, e.i0, f1));
        p.Close();
        return p;
    }

    /// <summary>梯形中线在某一端的端点（虚线沿它走）。</summary>
    static SKPoint Mid((SKPoint o0, SKPoint o1, SKPoint i0, SKPoint i1) e, int end)
        => end == 0 ? Lerp(e.o0, e.i0, 0.5f) : Lerp(e.o1, e.i1, 0.5f);

    /// <summary>把一条边拆成若干条子带 + 各自的填色。<paramref name="lit"/> = 本边朝光源（上/左）。</summary>
    static IEnumerable<(float f0, float f1, SKColor col)> Stripes(string style, SKColor col, bool lit)
    {
        var dark = Shade(col, light: false);
        var light = Shade(col, light: true);
        return style switch
        {
            "double" => new[] { (0f, 1f / 3f, col), (2f / 3f, 1f, col) },
            "groove" => new[] { (0f, .5f, lit ? dark : light), (.5f, 1f, lit ? light : dark) },
            "ridge" => new[] { (0f, .5f, lit ? light : dark), (.5f, 1f, lit ? dark : light) },
            "inset" => new[] { (0f, 1f, lit ? dark : light) },
            "outset" => new[] { (0f, 1f, lit ? light : dark) },
            _ => new[] { (0f, 1f, col) },
        };
    }

    /// <summary>groove/ridge/inset/outset 的明暗色。Chrome 口径实测（2026-09-26 探针）：保持色相与饱和，
    /// 把明度（HSV 的 V，等价于最大通道）整体 ±84/255 —— 即按 (max±84)/max 等比缩放三通道，亮色撞到 255 就截断。</summary>
    static SKColor Shade(SKColor c, bool light)
    {
        int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
        if (max == 0) return c;
        int target = light ? Math.Min(255, max + 84) : Math.Max(0, max - 84);
        float k = target / (float)max;
        return new SKColor((byte)Math.Round(c.Red * k), (byte)Math.Round(c.Green * k), (byte)Math.Round(c.Blue * k), c.Alpha);
    }

    /// <summary>dashed / dotted 的虚线段。Chrome 口径实测（2026-09-26 探针，2px/4px/8px 三档）：
    /// dashed 段长 2×宽、dotted 圆点直径=宽；理想周期 3×宽 / 2×宽。</summary>
    static SKPathEffect? DashEffect(string style, float w)
        => style == "dashed" ? SKPathEffect.CreateDash(new[] { 2 * w, w }, 0)
         : style == "dotted" ? SKPathEffect.CreateDash(new[] { 0.01f, 2 * w }, 0)
         : null;

    /// <summary>直边虚线：Chrome 还会把 dashed 的周期按边长整数化——段长固定，取 ceil(内沿长/理想周期) 段
    /// 铺满整条边，空隙吸收余数（8px dashed 因此比理想周期短 1/3，不整数化就明显偏疏）。
    /// dotted 实测恒为 2×宽的中心距，不做整数化。</summary>
    static (float on, float off) DashPattern(string style, float w, float midLen)
    {
        if (style == "dotted") return (0.01f, 2 * w);
        float on = 2 * w;
        float len = midLen - w;   // 内沿长度：Chrome 用它算段数
        int n = len > 3 * w ? (int)Math.Ceiling(len / (3 * w)) : 1;
        return (on, Math.Max(0.01f, len / n - on));
    }

    /// <summary>圆角盒四边只能共用一条描边路径：混合样式时 dashed 优先，其次 dotted。</summary>
    public static string DominantDash(ComputedStyle s)
    {
        if (s.BorderStyleTop == "dashed" || s.BorderStyleRight == "dashed" || s.BorderStyleBottom == "dashed" || s.BorderStyleLeft == "dashed") return "dashed";
        if (s.BorderStyleTop == "dotted" || s.BorderStyleRight == "dotted" || s.BorderStyleBottom == "dotted" || s.BorderStyleLeft == "dotted") return "dotted";
        return "solid";
    }

    /// <summary>行内盒背景：只有底色没有边框/阴影，但圆角与盒级装饰同口径（squircle 也跟随）。</summary>
    public static void PaintInlineBackground(SKCanvas canvas, SKRect r, ComputedStyle s, SKColor color)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        bool rounded = HasRadius(s);
        using var p = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        DrawRR(canvas, r, CornerRadii(r, s), rounded, rounded && s.CornerShape == "squircle", p);
    }

    static void DrawRR(SKCanvas canvas, SKRect r, SKPoint[] radii, bool rounded, bool sq, SKPaint p)
    {
        if (sq)
        {
            using var path = MakeSquirclePath(r, radii);
            canvas.DrawPath(path, p);
        }
        else if (rounded) canvas.DrawRoundRect(MakeRR(r, radii), p);
        else canvas.DrawRect(r, p);
    }

    /// <summary>
    /// squircle 轮廓：四角画超椭圆 |x/a|^n + |y/b|^n = 1（n=4，连续曲率近似），每角 14 段采样。
    /// radii 顺序 TL/TR/BR/BL；相邻角半径在同一边上重叠时按 CSS 规则等比收缩。
    /// </summary>
    public static SKPath MakeSquirclePath(SKRect r, SKPoint[] radii)
    {
        var rads = (SKPoint[])radii.Clone();
        FitEdge(ref rads[0], ref rads[1], r.Width, xComp: true);   // 上边 TL.x+TR.x
        FitEdge(ref rads[3], ref rads[2], r.Width, xComp: true);   // 下边 BL.x+BR.x
        FitEdge(ref rads[0], ref rads[3], r.Height, xComp: false); // 左边 TL.y+BL.y
        FitEdge(ref rads[1], ref rads[2], r.Height, xComp: false); // 右边 TR.y+BR.y

        const double n = 4.0, e = 2.0 / n;   // 超椭圆指数（n=4 → sqrt）
        const int segs = 14;
        static float S(double v, double ee) => (float)Math.Pow(Math.Sin(v), ee);
        static float C(double v, double ee) => Math.Cos(v) <= 0 ? 0 : (float)Math.Pow(Math.Cos(v), ee);

        var path = new SKPath();
        path.MoveTo(r.Left + rads[0].X, r.Top);   // TL 顶边切点

        // TR：中心 (R-ax, T+by)，θ:0→π/2 从顶切点到右切点
        AddArc(path, r.Right - rads[1].X, r.Top + rads[1].Y, rads[1].X, rads[1].Y,
               θ => (S(θ, e) * rads[1].X, -C(θ, e) * rads[1].Y));
        path.LineTo(r.Right, r.Bottom - rads[2].Y);
        // BR：中心 (R-ax, B-by)，从右切点到底切点
        AddArc(path, r.Right - rads[2].X, r.Bottom - rads[2].Y, rads[2].X, rads[2].Y,
               θ => (C(θ, e) * rads[2].X, S(θ, e) * rads[2].Y));
        path.LineTo(r.Left + rads[3].X, r.Bottom);
        // BL：中心 (L+ax, B-by)，从底切点 (0,b) 到左切点 (-a,0)
        AddArc(path, r.Left + rads[3].X, r.Bottom - rads[3].Y, rads[3].X, rads[3].Y,
               θ => (-S(θ, e) * rads[3].X, C(θ, e) * rads[3].Y));
        path.LineTo(r.Left, r.Top + rads[0].Y);
        // TL：中心 (L+ax, T+by)，从左切点 (-a,0) 回顶切点 (0,-b)
        AddArc(path, r.Left + rads[0].X, r.Top + rads[0].Y, rads[0].X, rads[0].Y,
               θ => (-C(θ, e) * rads[0].X, -S(θ, e) * rads[0].Y));
        path.Close();
        return path;

        static void AddArc(SKPath path, float cx, float cy, float a, float b,
                           Func<double, (float, float)> offset)
        {
            if (a <= 0 && b <= 0) { path.LineTo(cx, cy); return; }
            for (int i = 1; i <= segs; i++)
            {
                double θ = Math.PI / 2 * i / segs;
                var (dx, dy) = offset(θ);
                path.LineTo(cx + dx, cy + dy);
            }
        }
    }

    static void FitEdge(ref SKPoint p1, ref SKPoint p2, float len, bool xComp)
    {
        float v1 = xComp ? p1.X : p1.Y, v2 = xComp ? p2.X : p2.Y;
        float sum = v1 + v2;
        if (sum <= len || sum <= 0) return;
        float k = len / sum;
        if (xComp) { p1.X *= k; p2.X *= k; } else { p1.Y *= k; p2.Y *= k; }
    }

    static SKRoundRect MakeRR(SKRect r, SKPoint[] radii)
    {
        var rr = new SKRoundRect();
        rr.SetRectRadii(r, radii);
        return rr;
    }

    /// <summary>inset 阴影的环形状：只要任一角有半径就走圆角（椭圆角的 y 分量也算）。</summary>
    static bool AnyRadius(SKPoint[] radii) => radii.Any(p => p.X > 0 || p.Y > 0);

    static SKPoint[] ScaleRadii(SKPoint[] radii, float shrink)
    {
        var res = new SKPoint[radii.Length];
        for (int i = 0; i < radii.Length; i++)
            res[i] = new SKPoint(Math.Max(0, radii[i].X - shrink), Math.Max(0, radii[i].Y - shrink));
        return res;
    }
}
