using SkiaSharp;
using PaperSharp.Css;

namespace PaperSharp.Paint;

/// <summary>
/// 单盒视觉隔离层：clip-path 裁剪 + mix-blend-mode/filter 经 SaveLayer + mask-image 经 DstIn。
/// 两个画师（PNG 遍历 / PDF DrawItem）共用；作用域为盒自身的装饰与文本（子树不并入，简化）。
/// 注意「子树不并入」只针对本隔离层：祖先 <c>overflow</c> 的裁剪走 <see cref="ClipOverflow"/>，
/// 由绘制侧在每个盒/条目外各包一层，因此同样罩得住子盒。
/// </summary>
public static class VisualEffects
{
    public static bool HasAny(ComputedStyle s) =>
        s.ClipPath != null || s.MaskImage != null || s.Filter != null || s.MixBlendMode != "normal";

    public static Scope Begin(SKCanvas canvas, ComputedStyle s, SKRect rect)
    {
        var sc = new Scope { Canvas = canvas };
        if (!HasAny(s)) return sc;

        canvas.Save();
        sc.Saved = true;
        if (s.ClipPath != null)
        {
            var clip = CssValueParser.BuildClipPath(s.ClipPath, rect);
            if (clip != null) { canvas.ClipPath(clip); clip.Dispose(); }
        }
        bool needLayer = s.Filter != null || s.MaskImage != null || s.MixBlendMode != "normal";
        if (needLayer)
        {
            using var p = new SKPaint();
            if (s.MixBlendMode != "normal") p.BlendMode = MapBlend(s.MixBlendMode);
            SKImageFilter? filter = null;
            if (s.Filter != null) filter = s.Filter.Build();
            p.ImageFilter = filter;
            // 图层边界：盒矩形外扩滤镜影响范围（模糊/阴影），避免被裁掉
            float pad = 40f;
            if (s.Filter != null) pad = Math.Max(pad, s.Filter.Blur * 2 + Math.Max(Math.Abs(s.Filter.ShadowX), Math.Abs(s.Filter.ShadowY)) + s.Filter.ShadowBlur);
            var lb = new SKRect(rect.Left - pad, rect.Top - pad, rect.Right + pad, rect.Bottom + pad);
            canvas.SaveLayer(lb, p);
            // Skia 的 Modulate（=PD multiply）会把图层透明区乘掉背景：先铺白底，
            // 白×底色=底色，等效 CSS multiply 的"透明源不影响目标"语义
            if (s.MixBlendMode == "multiply")
            {
                using var wp = new SKPaint { Color = SKColors.White };
                canvas.DrawRect(lb, wp);
            }
            filter?.Dispose();
            sc.LayerOpened = true;
        }
        sc.Mask = s.MaskImage;
        sc.Rect = rect;
        return sc;
    }

    /// <summary>结束隔离层：先在本层内画 DstIn 遮罩，再逐帧 Restore（SaveLayer 也占一个保存帧）。</summary>
    public static void End(in Scope sc)
    {
        if (!sc.Saved) return;
        if (sc.LayerOpened && sc.Mask != null)
        {
            using var shader = sc.Mask.BuildShader(sc.Rect);
            if (shader != null)
            {
                using var mp = new SKPaint { Shader = shader, BlendMode = SKBlendMode.DstIn };
                sc.Canvas.DrawRect(sc.Rect, mp);
            }
        }
        if (sc.LayerOpened) sc.Canvas.Restore();
        sc.Canvas.Restore();
    }

    /// <summary>
    /// 把画布裁剪到某个 <c>overflow</c> 裁剪祖先的 padding box（由 <c>BlockBox.OverflowClip</c> 沿父链求交得出）。
    /// 与 <see cref="Begin"/> 的区别：Begin 是「盒自身 clip-path 的作用域」，这里罩的是整盒落笔（含子盒、
    /// backdrop、替换内容），由调用方在画每个盒/条目时各算一次并包裹，故不需要沿递归下传裁剪栈。
    /// 祖先开了 border-radius 时按圆角矩形裁（四角半径按比例缩到盒内，同 CSS 规则）；返回 Save 帧号，调用方负责 Restore。
    /// </summary>
    public static int ClipOverflow(SKCanvas canvas, SKRect box, ComputedStyle owner)
    {
        int save = canvas.Save();
        float tl = owner.BorderRadiusTopLeft, tr = owner.BorderRadiusTopRight,
              br = owner.BorderRadiusBottomRight, bl = owner.BorderRadiusBottomLeft;
        // CSS 规则：相邻两半径之和超过边长时，四角按同一比例缩小到刚好放下
        float f = 1f;
        if (tl + tr > 0) f = Math.Min(f, box.Width / (tl + tr));
        if (bl + br > 0) f = Math.Min(f, box.Width / (bl + br));
        if (tl + bl > 0) f = Math.Min(f, box.Height / (tl + bl));
        if (tr + br > 0) f = Math.Min(f, box.Height / (tr + br));
        if (f < 1f) { tl *= f; tr *= f; br *= f; bl *= f; }
        if (tl > 0.01f || tr > 0.01f || br > 0.01f || bl > 0.01f)
        {
            using var path = new SKPath();
            var rr = new SKRoundRect();
            rr.SetRectRadii(box, new[] { new SKPoint(tl, tl), new SKPoint(tr, tr), new SKPoint(br, br), new SKPoint(bl, bl) });
            path.AddRoundRect(rr);
            canvas.ClipPath(path);
        }
        else canvas.ClipRect(box);
        return save;
    }

    public struct Scope
    {
        public SKCanvas Canvas;
        public bool Saved;          // 是否 Save 过（无效果则 false）
        public bool LayerOpened;    // 是否 SaveLayer 过
        public GradientSpec? Mask;
        public SKRect Rect;
    }

    public static SKBlendMode MapBlend(string m) => m switch
    {
        "multiply" => SKBlendMode.Modulate,
        "screen" => SKBlendMode.Screen,
        "overlay" => SKBlendMode.Overlay,
        "darken" => SKBlendMode.Darken,
        "lighten" => SKBlendMode.Lighten,
        "color-dodge" => SKBlendMode.ColorDodge,
        "color-burn" => SKBlendMode.ColorBurn,
        "hard-light" => SKBlendMode.HardLight,
        "soft-light" => SKBlendMode.SoftLight,
        "difference" => SKBlendMode.Difference,
        "exclusion" => SKBlendMode.Exclusion,
        "hue" => SKBlendMode.Hue,
        "saturation" => SKBlendMode.Saturation,
        "color" => SKBlendMode.Color,
        "luminosity" => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver,
    };

    /// <summary>
    /// backdrop-filter：把盒背后【已绘】像素快照、套滤镜（模糊等）、裁剪回盒矩形，再让盒自身半透明背景叠其上。
    /// 仅光栅画布（PNG 路径，surface 非空）能快照 backdrop；PDF 矢量画布无 surface → 静默跳过（backdrop 是位图效果）。
    /// 在盒装饰绘制之前调用。cssRect 为 border-box（CSS 坐标，画布可能已 scale）。
    /// </summary>
    public static void ApplyBackdrop(SKCanvas canvas, SKSurface? surface, ComputedStyle s, SKRect cssRect)
    {
        if (surface == null || s.BackdropFilter == null) return;
        var filter = s.BackdropFilter.Build();
        if (filter == null) return;
        var m = canvas.TotalMatrix;
        var dev = m.MapRect(cssRect);
        using var snap = surface.Snapshot();   // 全画布已绘内容（设备像素）
        if (snap == null) { filter.Dispose(); return; }
        float sx = m.ScaleX > 0 ? m.ScaleX : 1f;
        canvas.Save();
        canvas.SetMatrix(SKMatrix.Identity);
        float tl = s.BorderRadiusTopLeft * sx, tr = s.BorderRadiusTopRight * sx,
              br = s.BorderRadiusBottomRight * sx, bl = s.BorderRadiusBottomLeft * sx;
        if (tl > 0 || tr > 0 || br > 0 || bl > 0)
        {
            using var path = new SKPath();
            var rr = new SKRoundRect();
            rr.SetRectRadii(dev, new[] { new SKPoint(tl, tl), new SKPoint(tr, tr), new SKPoint(br, br), new SKPoint(bl, bl) });
            path.AddRoundRect(rr);
            canvas.ClipPath(path);
        }
        else canvas.ClipRect(dev);
        using (var p = new SKPaint { IsAntialias = true, ImageFilter = filter })
            canvas.DrawImage(snap, 0, 0, p);   // 套模糊滤镜回绘，clip 限定在盒内 = backdrop 模糊
        canvas.Restore();
        filter.Dispose();
    }
}