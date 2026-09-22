using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Paint;

/// <summary>
/// 文本绘制：按行画，处理 text-align、letter-spacing（逐字排布）、text-decoration、opacity。
/// PNG 与 PDF 两条路径共用，避免逻辑漂移。
/// </summary>
public static class TextPainter
{
    /// <summary>
    /// 画 inline 行盒里的文本片段（原子盒由调用方另画）。以 (contentLeft,contentTop) 为内容原点，
    /// 画 lines[firstLine..firstLine+lineCount)。每段的样式（字体/色/背景/字距/装饰）取自 frag 自己的 ComputedStyle。
    /// </summary>
    public static void DrawInlineLines(SKCanvas canvas, BlockBox box,
        float contentLeft, float contentTop, int firstLine, int lineCount)
    {
        var lines = box.Lines;
        if (lines == null || lineCount <= 0) return;
        float y = contentTop;
        for (int i = 0; i < firstLine && i < lines.Count; i++) y += lines[i].Height;
        int end = Math.Min(lines.Count, firstLine + lineCount);
        for (int i = firstLine; i < end; i++)
        {
            var ln = lines[i];
            float lineLeft = contentLeft + ln.Left;
            float baseline = y + ln.Baseline;
            foreach (var f in ln.Frags)
                if (f.Text != null) DrawRun(canvas, f, lineLeft + f.X, baseline);
            y += ln.Height;
        }
    }

    static void DrawRun(SKCanvas canvas, InlineFrag f, float x, float baseline)
    {
        var s = f.Style;
        byte alpha = (byte)Math.Round(Clamp01(s.Opacity) * 255);

        // inline 背景：覆盖文字行高区间（用 frag 字号 + 一点降部），仅纯色
        if (!s.BackgroundColor.Equals(SKColor.Empty))
        {
            float top = baseline - f.FontSize;
            float bgH = f.FontSize * Math.Max(1f, s.LineHeightFactor);
            using var bp = new SKPaint
            {
                Color = s.BackgroundColor.WithAlpha((byte)Math.Round(s.BackgroundColor.Alpha * (float)alpha)),
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
            };
            canvas.DrawRect(x, top, f.Width, bgH, bp);
        }

        var typeface = FontManager.Get(s.FontFamily, s.FontWeight);
        var color = s.Color.WithAlpha(alpha);
        using var paint = new SKPaint
        {
            Color = color,
            TextSize = f.FontSize,
            Typeface = typeface,
            IsAntialias = true,
            SubpixelText = true,
            Style = SKPaintStyle.Fill,
        };
        DrawSpaced(canvas, f.Text!, x, baseline, paint, s.LetterSpacing);
        DrawDecoration(canvas, s, x, baseline, f.Width, color);
    }
    /// <summary>画 lines[first..first+count) 到 (left,top) 起的内容区，宽度 width。</summary>
    public static void DrawLines(SKCanvas canvas, ComputedStyle s, SKTypeface typeface,
        string[] lines, int first, int count, float left, float top, float width)
    {
        if (lines == null || count <= 0) return;
        byte alpha = (byte)Math.Round(Clamp01(s.Opacity) * 255);
        var color = s.Color.WithAlpha(alpha);

        using var paint = new SKPaint
        {
            Color = color,
            TextSize = s.FontSize,
            Typeface = typeface,
            IsAntialias = true,
            SubpixelText = true,
            Style = SKPaintStyle.Fill,
        };
        float ls = s.LetterSpacing;
        float lineHeight = s.FontSize * s.LineHeightFactor;
        float baseline = top + s.FontSize;

        for (int k = 0; k < count; k++)
        {
            int idx = first + k;
            if (idx >= lines.Length) break;
            var line = lines[idx];
            float textW = MeasureWidth(line, paint, ls);

            float x = left;
            if (s.TextAlignment == CssTextAlign.Center) x = left + (width - textW) / 2;
            else if (s.TextAlignment == CssTextAlign.Right) x = left + width - textW;

            DrawSpaced(canvas, line, x, baseline, paint, ls);
            DrawDecoration(canvas, s, x, baseline, textW, color);
            baseline += lineHeight;
        }
    }

    static void DrawSpaced(SKCanvas canvas, string line, float x, float y, SKPaint paint, float ls)
    {
        if (line.Length == 0) return;
        if (Math.Abs(ls) < 0.01f) { canvas.DrawText(line, x, y, paint); return; }
        float cx = x;
        foreach (var ch in line)
        {
            string g = ch.ToString();
            canvas.DrawText(g, cx, y, paint);
            cx += paint.MeasureText(g) + ls;
        }
    }

    static void DrawDecoration(SKCanvas canvas, ComputedStyle s, float x, float baseline, float width, SKColor color)
    {
        if (s.TextDecoration == "none" || width <= 0) return;
        float thickness = Math.Max(0.8f, s.FontSize / 12f);
        using var p = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        if (s.TextDecoration.Contains("underline"))
            canvas.DrawRect(x, baseline + s.FontSize * 0.14f, width, thickness, p);
        if (s.TextDecoration.Contains("line-through"))
            canvas.DrawRect(x, baseline - s.FontSize * 0.32f, width, thickness, p);
        if (s.TextDecoration.Contains("overline"))
            canvas.DrawRect(x, baseline - s.FontSize * 0.86f, width, thickness, p);
    }

    public static float MeasureWidth(string s, SKPaint paint, float letterSpacing)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        return paint.MeasureText(s) + letterSpacing * Math.Max(0, s.Length - 1);
    }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
