using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Paint;

/// <summary>
/// 渲染 @page 边距盒（页眉/页脚）。每页调用一次，把 counter(page)/counter(pages)
/// 替换成实际页码/总页数后，画到页边距带里（top-* 在上边距，bottom-* 在下边距）。
/// </summary>
public static class MarginBoxRenderer
{
    public static void Render(SKCanvas canvas, PageSpec spec, int pageIndex, int total)
    {
        if (spec.Boxes.Count == 0) return;

        float spanLeft = spec.MarginLeft;
        float spanRight = spec.PageW - spec.MarginRight;
        float spanW = spanRight - spanLeft;

        foreach (var box in spec.Boxes)
        {
            string text = BuildText(box, pageIndex, total);
            if (string.IsNullOrEmpty(text)) continue;

            var typeface = FontManager.Get(box.FontFamily, box.FontWeight);
            using var paint = new SKPaint
            {
                Color = box.Color,
                TextSize = box.FontSize,
                Typeface = typeface,
                IsAntialias = true,
                SubpixelText = true,
                Style = SKPaintStyle.Fill,
            };

            bool isTop = box.Position.StartsWith("top");
            float centerY = isTop ? spec.MarginTop / 2f : spec.PageH - spec.MarginBottom / 2f;
            float baseline = centerY + box.FontSize * 0.35f;

            float w = paint.MeasureText(text);
            float x = box.Position switch
            {
                var p when p.EndsWith("left") => spanLeft,
                var p when p.EndsWith("right") => spanRight - w,
                _ => spanLeft + (spanW - w) / 2f, // center
            };

            Pdf.PdfGlyphUsage.DrawText(canvas, text, x, baseline, paint);
        }
    }

    static string BuildText(MarginBoxSpec box, int pageIndex, int total)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in box.Content)
        {
            switch (part.Kind)
            {
                case ContentPartKind.Literal: sb.Append(part.Text); break;
                case ContentPartKind.PageNumber: sb.Append(CounterStyle.Format(pageIndex, part.CounterStyle)); break;
                case ContentPartKind.TotalPages: sb.Append(CounterStyle.Format(total, part.CounterStyle)); break;
            }
        }
        return sb.ToString();
    }
}
