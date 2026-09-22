using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Paint;

/// <summary>
/// 遍历布局树画到 SKCanvas。先画在流内容（跳过 absolute），再单独画 absolute 子树（盖在上层）。
/// </summary>
public static class Painter
{
    public static void Paint(SKCanvas canvas, BlockBox root)
    {
        PaintFlow(canvas, root);
        PaintAbsolute(canvas, root);
    }

    // 在流：画自己 + 递归非 absolute 子
    static void PaintFlow(SKCanvas canvas, BlockBox box)
    {
        PaintSelf(canvas, box);
        foreach (var child in box.Children)
            if (child.Style.Position != "absolute")
                PaintFlow(canvas, child);
    }

    // absolute：收集全树里的 absolute 盒，整棵子树画在最上层
    static void PaintAbsolute(SKCanvas canvas, BlockBox box)
    {
        foreach (var child in box.Children)
        {
            if (child.Style.Position == "absolute") PaintFull(canvas, child);
            else PaintAbsolute(canvas, child);
        }
    }

    static void PaintFull(SKCanvas canvas, BlockBox box)
    {
        PaintSelf(canvas, box);
        foreach (var child in box.Children) PaintFull(canvas, child);
    }

    static void PaintSelf(SKCanvas canvas, BlockBox box)
    {
        var s = box.Style;
        float bw = s.BorderWidthLeft + s.PaddingLeft + box.ContentWidth + s.PaddingRight + s.BorderWidthRight;
        float bh = s.BorderWidthTop + s.PaddingTop + box.ContentHeight + s.PaddingBottom + s.BorderWidthBottom;
        var borderRect = new SKRect(box.BorderX, box.BorderY, box.BorderX + bw, box.BorderY + bh);

        BoxPainter.PaintDecor(canvas, borderRect, s);

        float contentX = box.BorderX + s.BorderWidthLeft + s.PaddingLeft;
        float contentY = box.BorderY + s.BorderWidthTop + s.PaddingTop;

        if (box.Image != null)
        {
            var dst = new SKRect(contentX, contentY, contentX + box.ContentWidth, contentY + box.ContentHeight);
            byte a = (byte)Math.Round((s.Opacity <= 0 ? 0 : s.Opacity >= 1 ? 1 : s.Opacity) * 255);
            using var ip = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = new SKColor(255, 255, 255, a) };
            canvas.DrawImage(box.Image, dst, ip);
        }

        if (box.WrappedLines != null)
            TextPainter.DrawLines(canvas, s, box.Typeface, box.WrappedLines, 0, box.WrappedLines.Length,
                contentX, contentY, box.ContentWidth);
        else if (box.Lines != null)
        {
            TextPainter.DrawInlineLines(canvas, box, contentX, contentY, 0, box.Lines.Count);
            PaintInlineAtoms(canvas, box);   // inline-block / 行内 img 各自成盒，按已定位坐标画整棵子树
        }
    }

    /// <summary>inline FC 里的原子盒（inline-block/行内 img）已在 PositionInline 里定位到绝对坐标，这里整棵画出。</summary>
    static void PaintInlineAtoms(SKCanvas canvas, BlockBox box)
    {
        if (box.Lines == null) return;
        foreach (var ln in box.Lines)
            foreach (var f in ln.Frags)
                if (f.Box != null) PaintFull(canvas, f.Box);
    }
}
