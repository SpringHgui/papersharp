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

        bool rounded = s.BorderRadiusTopLeft > 0 || s.BorderRadiusTopRight > 0
                    || s.BorderRadiusBottomRight > 0 || s.BorderRadiusBottomLeft > 0;
        var radii = ClampRadii(r, s);
        bool sq = rounded && s.CornerShape == "squircle";

        if (s.Shadow != null) PaintShadow(canvas, r, s, radii, rounded, sq, opacity);

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

        // 边框
        PaintBorder(canvas, r, s, radii, rounded, sq, opacity);
    }

    static void PaintShadow(SKCanvas canvas, SKRect r, ComputedStyle s, SKPoint[] radii, bool rounded, bool sq, float opacity)
    {
        var sh = s.Shadow!;
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
            using var p = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
            Side(true, r.Top, r.Bottom, r.Left, r.Right, t, s.BorderStyleTop, s.BorderColorTop);
            Side(true, r.Bottom - b, r.Bottom, r.Left, r.Right, b, s.BorderStyleBottom, s.BorderColorBottom);
            Side(false, r.Top, r.Bottom, r.Left, r.Left + l, l, s.BorderStyleLeft, s.BorderColorLeft);
            Side(false, r.Top, r.Bottom, r.Right - rt, r.Right, rt, s.BorderStyleRight, s.BorderColorRight);
            return;

            // 条带左上角恒为 (a0, band0)：横边沿 x 铺满 [a0,a1]、厚 w；竖边沿 y 铺满 [band0,band1]、厚 w。
            // 虚线描条带中线（横边 y=band0+w/2，竖边 x=a0+w/2）。
            void Side(bool horizontal, float band0, float band1, float a0, float a1, float w, string style, SKColor raw)
            {
                if (w <= 0) return;
                var col = raw.WithAlpha((byte)Math.Round(raw.Alpha * opacity));
                var eff = DashEffect(style, w);
                if (eff == null)
                {
                    p.Color = col;
                    if (horizontal) canvas.DrawRect(a0, band0, a1 - a0, w, p);
                    else canvas.DrawRect(a0, band0, w, band1 - band0, p);
                    return;
                }
                using var sp = new SKPaint
                {
                    Color = col, Style = SKPaintStyle.Stroke, StrokeWidth = w,
                    IsAntialias = true, PathEffect = eff,
                    StrokeCap = style == "dotted" ? SKStrokeCap.Round : SKStrokeCap.Butt,
                };
                if (horizontal) canvas.DrawLine(a0, band0 + w / 2, a1, band0 + w / 2, sp);
                else canvas.DrawLine(a0 + w / 2, band0, a0 + w / 2, band1, sp);
            }
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

    /// <summary>dashed/dotted 的虚线段（浏览器近似：dash≈3×宽、gap≈2×宽；点距≈宽），其余样式返回 null=实线。</summary>
    static SKPathEffect? DashEffect(string style, float w)
        => style == "dashed" ? SKPathEffect.CreateDash(new[] { 3 * w, 2 * w }, 0)
         : style == "dotted" ? SKPathEffect.CreateDash(new[] { 0.01f, w }, 0)
         : null;

    /// <summary>圆角盒四边只能共用一条描边路径：混合样式时 dashed 优先，其次 dotted。</summary>
    public static string DominantDash(ComputedStyle s)
    {
        if (s.BorderStyleTop == "dashed" || s.BorderStyleRight == "dashed" || s.BorderStyleBottom == "dashed" || s.BorderStyleLeft == "dashed") return "dashed";
        if (s.BorderStyleTop == "dotted" || s.BorderStyleRight == "dotted" || s.BorderStyleBottom == "dotted" || s.BorderStyleLeft == "dotted") return "dotted";
        return "solid";
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

    /// <summary>取四角半径并夹到不超过盒子短边的一半。</summary>
    static SKPoint[] ClampRadii(SKRect r, ComputedStyle s)
    {
        float max = Math.Min(r.Width, r.Height) / 2f;
        float F(float v) => v > max ? max : v;
        return new[]
        {
            new SKPoint(F(s.BorderRadiusTopLeft), F(s.BorderRadiusTopLeft)),
            new SKPoint(F(s.BorderRadiusTopRight), F(s.BorderRadiusTopRight)),
            new SKPoint(F(s.BorderRadiusBottomRight), F(s.BorderRadiusBottomRight)),
            new SKPoint(F(s.BorderRadiusBottomLeft), F(s.BorderRadiusBottomLeft)),
        };
    }

    static SKPoint[] ScaleRadii(SKPoint[] radii, float shrink)
    {
        var res = new SKPoint[radii.Length];
        for (int i = 0; i < radii.Length; i++)
            res[i] = new SKPoint(Math.Max(0, radii[i].X - shrink), Math.Max(0, radii[i].Y - shrink));
        return res;
    }
}
