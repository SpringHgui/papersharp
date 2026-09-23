using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Paint;

/// <summary>
/// 遍历布局树画到 SKCanvas。先画在流内容（跳过 absolute），再单独画 absolute 子树（盖在上层）。
/// </summary>
public static class Painter
{
    public static void Paint(SKCanvas canvas, BlockBox root, SKSurface? surface = null)
    {
        PaintFlow(canvas, surface, root);
        PaintAbsolute(canvas, surface, root);
    }

    // 在流：画自己 + 递归非 absolute 子
    static void PaintFlow(SKCanvas canvas, SKSurface? surface, BlockBox box)
    {
        PaintSelf(canvas, surface, box);
        foreach (var child in box.Children)
            if (child.Style.Position != "absolute")
                PaintFlow(canvas, surface, child);
    }

    // absolute：收集全树里的 absolute 盒，按 z-index 排序后画在最上层
    static void PaintAbsolute(SKCanvas canvas, SKSurface? surface, BlockBox box)
    {
        var absBoxes = new List<BlockBox>();
        CollectAbsolute(box, absBoxes);

        // 按 z-index 排序（无显式 z-index 的排前面，按文档顺序）
        absBoxes.Sort((a, b) =>
        {
            bool aHas = a.Style.HasZIndex;
            bool bHas = b.Style.HasZIndex;
            if (!aHas && !bHas) return 0; // 保持文档顺序
            if (!aHas) return -1;
            if (!bHas) return 1;
            return a.Style.ZIndex.CompareTo(b.Style.ZIndex);
        });

        foreach (var abs in absBoxes)
            PaintFull(canvas, surface, abs);
    }

    static void CollectAbsolute(BlockBox box, List<BlockBox> result)
    {
        foreach (var child in box.Children)
        {
            if (child.Style.Position == "absolute") result.Add(child);
            else CollectAbsolute(child, result);
        }
    }

    static void PaintFull(SKCanvas canvas, SKSurface? surface, BlockBox box)
    {
        PaintSelf(canvas, surface, box);
        foreach (var child in box.Children) PaintFull(canvas, surface, child);
    }

    static void PaintSelf(SKCanvas canvas, SKSurface? surface, BlockBox box)
    {
        var s = box.Style;
        float bw = s.BorderWidthLeft + s.PaddingLeft + box.ContentWidth + s.PaddingRight + s.BorderWidthRight;
        float bh = s.BorderWidthTop + s.PaddingTop + box.ContentHeight + s.PaddingBottom + s.BorderWidthBottom;
        var borderRect = new SKRect(box.BorderX, box.BorderY, box.BorderX + bw, box.BorderY + bh);

        // backdrop-filter：先模糊盒背后已绘像素（仅光栅/PNG 路径），再画盒自身（半透明背景叠在毛玻璃上）
        VisualEffects.ApplyBackdrop(canvas, surface, s, borderRect);

        var scope = VisualEffects.Begin(canvas, s, borderRect);

        // background-clip:text：背景不铺盒，改由字形承载（渐变文字）。solid 背景走 fillOverride，渐变走 fillShader。
        bool clipText = s.BackgroundClip == "text" && (s.Background != null || !s.BackgroundColor.Equals(SKColor.Empty));
        float op = s.Opacity <= 0 ? 0 : s.Opacity >= 1 ? 1 : s.Opacity;
        SKShader? fillShader = clipText ? s.Background?.BuildShader(borderRect, op) : null;
        SKColor? fillOverride = clipText && fillShader == null ? s.BackgroundColor : null;

        BoxPainter.PaintDecor(canvas, borderRect, s, backgroundClippedToText: clipText);

        float contentX = box.BorderX + s.BorderWidthLeft + s.PaddingLeft;
        float contentY = box.BorderY + s.BorderWidthTop + s.PaddingTop + box.ValignDY;

        if (box.Image != null)
        {
            var dst = new SKRect(contentX, contentY, contentX + box.ContentWidth, contentY + box.ContentHeight);
            byte a = (byte)Math.Round((s.Opacity <= 0 ? 0 : s.Opacity >= 1 ? 1 : s.Opacity) * 255);
            using var ip = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = new SKColor(255, 255, 255, a) };
            canvas.DrawImage(box.Image, dst, ip);
        }
        else if (box.SvgPicture != null)
        {
            // 内联 SVG：矢量直绘（PDF 中保持矢量），按内容盒缩放
            var dst = new SKRect(contentX, contentY, contentX + box.ContentWidth, contentY + box.ContentHeight);
            canvas.Save();
            canvas.Translate(dst.Left, dst.Top);
            float pw = box.SvgPicture.CullRect.Width, ph = box.SvgPicture.CullRect.Height;
            if (pw > 0 && ph > 0) canvas.Scale(dst.Width / pw, dst.Height / ph);
            byte a = (byte)Math.Round((s.Opacity <= 0 ? 0 : s.Opacity >= 1 ? 1 : s.Opacity) * 255);
            using var sp = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, a) };
            canvas.DrawPicture(box.SvgPicture, sp);
            canvas.Restore();
        }

        if (box.WrappedLines != null)
            TextPainter.DrawLines(canvas, s, box.Typeface, box.WrappedLines, 0, box.WrappedLines.Length,
                contentX, contentY, box.ContentWidth, box.LineLefts, fillShader, fillOverride);
        else if (box.Lines != null)
        {
            TextPainter.DrawInlineLines(canvas, box, contentX, contentY, 0, box.Lines.Count, fillShader, fillOverride);
            PaintInlineAtoms(canvas, surface, box);   // inline-block / 行内 img 各自成盒，按已定位坐标画整棵子树
        }
        fillShader?.Dispose();
        VisualEffects.End(scope);
    }

    /// <summary>inline FC 里的原子盒（inline-block/行内 img）已在 PositionInline 里定位到绝对坐标，这里整棵画出。</summary>
    static void PaintInlineAtoms(SKCanvas canvas, SKSurface? surface, BlockBox box)
    {
        if (box.Lines == null) return;
        foreach (var ln in box.Lines)
            foreach (var f in ln.Frags)
                if (f.Box != null) PaintFull(canvas, surface, f.Box);
    }
}
