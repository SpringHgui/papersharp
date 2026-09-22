using SkiaSharp;
using PaperSharp.Css;

namespace PaperSharp.Paint;

/// <summary>
/// 盒子装饰绘制：box-shadow → 背景（纯色/渐变，含圆角）→ 边框（含圆角）。
/// 统一处理 opacity，PNG 与 PDF 两条路径共用。
/// </summary>
public static class BoxPainter
{
    public static void PaintDecor(SKCanvas canvas, SKRect r, ComputedStyle s)
    {
        float opacity = s.Opacity <= 0 ? 0 : s.Opacity >= 1 ? 1 : s.Opacity;
        if (opacity <= 0.001f) return;

        bool rounded = s.BorderRadiusTopLeft > 0 || s.BorderRadiusTopRight > 0
                    || s.BorderRadiusBottomRight > 0 || s.BorderRadiusBottomLeft > 0;
        var radii = ClampRadii(r, s);

        if (s.Shadow != null) PaintShadow(canvas, r, s, radii, rounded, opacity);

        // 背景
        bool hasBg = !s.BackgroundColor.Equals(SKColor.Empty) || s.Background != null;
        if (hasBg)
        {
            using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            SKShader? shader = s.Background?.BuildShader(r, opacity);
            if (shader != null) p.Shader = shader;
            else p.Color = s.BackgroundColor.WithAlpha((byte)Math.Round(s.BackgroundColor.Alpha * opacity));
            DrawRR(canvas, r, radii, rounded, p);
            shader?.Dispose();
        }

        // 边框
        PaintBorder(canvas, r, s, radii, rounded, opacity);
    }

    static void PaintShadow(SKCanvas canvas, SKRect r, ComputedStyle s, SKPoint[] radii, bool rounded, float opacity)
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
        DrawRR(canvas, sr, radii, rounded, p);
    }

    static void PaintBorder(SKCanvas canvas, SKRect r, ComputedStyle s, SKPoint[] radii, bool rounded, float opacity)
    {
        float t = s.BorderWidthTop, rt = s.BorderWidthRight, b = s.BorderWidthBottom, l = s.BorderWidthLeft;
        if (t <= 0 && rt <= 0 && b <= 0 && l <= 0) return;
        var color = s.BorderColor.WithAlpha((byte)Math.Round(s.BorderColor.Alpha * opacity));

        if (!rounded)
        {
            using var p = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
            if (t > 0) canvas.DrawRect(r.Left, r.Top, r.Width, t, p);
            if (b > 0) canvas.DrawRect(r.Left, r.Bottom - b, r.Width, b, p);
            if (l > 0) canvas.DrawRect(r.Left, r.Top, l, r.Height, p);
            if (rt > 0) canvas.DrawRect(r.Right - rt, r.Top, rt, r.Height, p);
            return;
        }

        // 圆角边框：描一条内缩的圆角矩形（四边取最大宽度做近似）
        float w = Math.Max(t, Math.Max(rt, Math.Max(b, l)));
        using var sp = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = w, IsAntialias = true };
        var inset = new SKRect(r.Left + w / 2, r.Top + w / 2, r.Right - w / 2, r.Bottom - w / 2);
        var rr = MakeRR(inset, ScaleRadii(radii, w / 2));
        canvas.DrawRoundRect(rr, sp);
    }

    static void DrawRR(SKCanvas canvas, SKRect r, SKPoint[] radii, bool rounded, SKPaint p)
    {
        if (rounded) canvas.DrawRoundRect(MakeRR(r, radii), p);
        else canvas.DrawRect(r, p);
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
