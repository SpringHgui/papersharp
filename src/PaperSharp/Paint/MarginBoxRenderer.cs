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

            var typeface = FontManager.Get(box.FontFamily, box.FontWeight, box.FontStyle);
            using var paint = TextPainter.NewTextPaint(typeface, box.FontSize, box.FontStyle, box.Color, box.FontWeight);

            bool isTop = box.Position.StartsWith("top");
            float centerY = isTop ? spec.MarginTop / 2f : spec.PageH - spec.MarginBottom / 2f;
            float baseline = centerY + box.FontSize * 0.35f;

            // 宽度与落笔必须同口径：字距对既进居中/贴右的算式（否则整串偏半个配对量），也决定配对之后
            // 每个字形的落点（否则页眉页脚那段字与正文同串的排布不一致）。
            // 注：这里刻意不走 TextPainter.MeasureWidth —— 边距盒的串多是中文+计数器，MeasureWidth 会
            // 拐进 TextShaper.WidthOf 的按字面切段分支，量出的宽与旧口径不同，m1/pct/report 三张的
            // 页脚位置会跟着动（实测 45 红 vs 41 红）。那一刀要不要落，见台账 P2-9 末段。
            // 与正文同一条宽度公式（MeasureWidth：hmtx + 字距 + 度量旗；复杂/回退串再走 WidthOf 切段）
            float w = TextPainter.MeasureWidth(text, paint, 0f);
            float x = box.Position switch
            {
                var p when p.EndsWith("left") => spanLeft,
                var p when p.EndsWith("right") => spanRight - w,
                _ => spanLeft + (spanW - w) / 2f, // center
            };

            Pdf.PdfGlyphUsage.DrawKerned(canvas, text, x, baseline, paint);
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
