using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Paint;

/// <summary>
/// 把分页器产出的 Page（一组 DrawItem）画到 SKCanvas。装饰带 + 部分文本行按 DrawItem 给的矩形绘制。
/// 先画在流 Items，再画 AbsItems（absolute 子树盖顶）。
/// </summary>
public static class PagePainter
{
    public static void PaintPage(SKCanvas canvas, Page page, float pageW, float pageH)
    {
        canvas.Clear(SKColors.White);
        if (page.Backdrop != null) PaintItem(canvas, page.Backdrop);
        foreach (var it in page.Items) PaintItem(canvas, it);
        foreach (var it in page.AbsItems) PaintItem(canvas, it);
    }

    static void PaintItem(SKCanvas canvas, DrawItem it)
    {
        var s = it.Box.Style;

        if (it.DrawDecor)
        {
            var r = new SKRect(it.X, it.Y, it.X + it.W, it.Y + it.H);
            BoxPainter.PaintDecor(canvas, r, s);

            if (it.Box.Image != null)
            {
                float cx = it.X + s.BorderWidthLeft + s.PaddingLeft;
                float cy = it.Y + s.BorderWidthTop + s.PaddingTop;
                var dst = new SKRect(cx, cy, cx + it.Box.ContentWidth, cy + it.Box.ContentHeight);
                byte a = (byte)Math.Round((s.Opacity <= 0 ? 0 : s.Opacity >= 1 ? 1 : s.Opacity) * 255);
                using var ip = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = new SKColor(255, 255, 255, a) };
                canvas.DrawImage(it.Box.Image, dst, ip);
            }
        }

        if (it.HasText)
            TextPainter.DrawLines(canvas, s, it.Box.Typeface, it.Box.WrappedLines!,
                it.FirstLine, it.LineCount, it.TextLeft, it.TextTop, it.TextWidth);
        else if (it.Box.Lines != null && it.LineCount > 0)
            TextPainter.DrawInlineLines(canvas, it.Box, it.TextLeft, it.TextTop, it.FirstLine, it.LineCount);
    }
}
