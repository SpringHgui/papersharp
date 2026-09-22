using SkiaSharp;
using PaperSharp.Css;

namespace PaperSharp.Paint;

/// <summary>
/// 单盒视觉隔离层：clip-path 裁剪 + mix-blend-mode/filter 经 SaveLayer + mask-image 经 DstIn。
/// 两个画师（PNG 遍历 / PDF DrawItem）共用；作用域为盒自身的装饰与文本（子树不并入，简化）。
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
}
