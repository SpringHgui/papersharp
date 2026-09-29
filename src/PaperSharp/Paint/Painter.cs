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

    /// <summary>
    /// 画一个盒：先按祖先的 overflow 裁剪包裹（祖先开了 overflow 且本盒整颗落在裁剪框外则直接不画），
    /// 再画自身。子盒各自再算一遍自己的祖先裁剪链，所以这里不必把裁剪矩形沿递归传下去。
    /// </summary>
    static void PaintSelf(SKCanvas canvas, SKSurface? surface, BlockBox box)
    {
        // transform：只罩本盒自身这一笔。子盒由 PaintFull 各自再走一遍 PaintSelf，
        // 那时算的是「自己 + 全部祖先」的累计矩阵，所以这里不能把变换留在画布上往下传，
        // 否则祖先会被乘两次（TransformSpace 的头注释记着这条）。
        int tf = TransformSpace.Apply(canvas, box);
        int snap = PaintSnap.Begin(canvas, box);
        PaintSelfClipped(canvas, surface, box);
        PaintSnap.End(canvas, snap);
        TransformSpace.Restore(canvas, tf);
    }

    static void PaintSelfClipped(SKCanvas canvas, SKSurface? surface, BlockBox box)
    {
        var clip = box.OverflowClip();
        if (clip == null) { PaintBox(canvas, surface, box); return; }
        if (clip.Value.Rect.Width <= 0 || clip.Value.Rect.Height <= 0) return;   // 求交为空 → 完全不可见
        int save = VisualEffects.ClipOverflow(canvas, clip.Value.Rect, clip.Value.RadiusOwner);
        PaintBox(canvas, surface, box);
        canvas.Restore();
    }

    static void PaintBox(SKCanvas canvas, SKSurface? surface, BlockBox box)
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

        // 原生表单控件：checkbox/radio 作者样式通常无框，按浏览器观感自绘一个方框/圆框。
        if (s.Tag == "input" && s.InputType is "checkbox" or "radio")
            PaintNativeControl(canvas, s, borderRect);

        int imgClip = box.Image != null || box.SvgPicture != null
            ? BoxPainter.ClipReplacedContent(canvas, borderRect,
                new SKRect(contentX, contentY, contentX + box.ContentWidth, contentY + box.ContentHeight), s)
            : -1;
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
        if (imgClip >= 0) canvas.RestoreToCount(imgClip);

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

    /// <summary>自绘原生 checkbox/radio：白底灰框，选中态填主题蓝并画勾/圆点。尺寸取盒大小。</summary>
    static void PaintNativeControl(SKCanvas canvas, ComputedStyle s, SKRect r)
    {
        var accent = new SKColor(0x1a, 0x5c, 0xf0);   // 缺省强调色（未解析 accent-color）
        float rad = s.InputType == "radio" ? r.Width / 2 : Math.Min(4, r.Width / 4);
        using var bg = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
        using var border = new SKPaint { IsAntialias = true, Color = new SKColor(0xc4, 0xc9, 0xd0), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        canvas.DrawRoundRect(r, rad, rad, bg);
        canvas.DrawRoundRect(r, rad, rad, border);
        if (!s.InputChecked) return;
        using var fill = new SKPaint { IsAntialias = true, Color = accent, Style = SKPaintStyle.Fill };
        canvas.DrawRoundRect(r, rad, rad, fill);
        if (s.InputType == "radio")
        {
            var dot = new SKRect(r.MidX - r.Width / 6, r.MidY - r.Height / 6, r.MidX + r.Width / 6, r.MidY + r.Height / 6);
            using var dotp = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
            canvas.DrawOval(dot, dotp);
        }
        else
        {
            using var check = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, r.Width / 8), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            var p = new SKPath();
            p.MoveTo(r.Left + r.Width * 0.26f, r.Top + r.Height * 0.52f);
            p.LineTo(r.Left + r.Width * 0.44f, r.Top + r.Height * 0.68f);
            p.LineTo(r.Left + r.Width * 0.74f, r.Top + r.Height * 0.34f);
            canvas.DrawPath(p, check);
        }
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
