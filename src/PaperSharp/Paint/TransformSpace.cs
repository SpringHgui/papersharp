using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Paint;

/// <summary>
/// CSS <c>transform</c> 的绘制期落地：把「未变换的页面坐标」映到「变换后的视觉坐标」。
///
/// 三条铁律（都是 Chrome 实测口径，改这里前先读）：
/// ① <b>transform 不参与布局</b>——盒矩形、兄弟排布、包含块一律按未变换的几何算，只有绘制时套矩阵。
///    所以本类只在 Paint 层出现，Layout 层不感知。
/// ② 每个盒的矩阵都在**自己的页面坐标**里定义（原点 = 边框盒左上 + transform-origin），
///    祖先链逐层后乘：<c>p' = p·M_self·M_parent·…</c>（Skia 行向量约定，postConcat = 先 this 再 other）。
///    于是每条绘制语句各自算一遍累计矩阵即可，不需要沿递归下传变换栈——与 OverflowClip 同一套设计。
/// ③ 溢出裁剪矩形同样按未变换坐标写、被同一个矩阵带走，所以「圆角卡片旋转后裁剪跟着转」天然成立。
///
/// 已知不做：3D 函数（解析层直接判整条作废）、<c>backdrop-filter</c> 与 transform 同盒
/// （毛玻璃取的是设备坐标的身后像素，变换后该源矩形对不上）、SVG 导出的 matrix 换算待复量。
/// </summary>
public static class TransformSpace
{
    /// <summary>边框盒（不含 margin）——与 Painter/PagePainter 里那份算法逐字一致。</summary>
    public static SKRect BorderRect(BlockBox b)
    {
        var s = b.Style;
        return new SKRect(b.BorderX, b.BorderY,
            b.BorderX + s.BorderWidthLeft + s.PaddingLeft + b.ContentWidth + s.PaddingRight + s.BorderWidthRight,
            b.BorderY + s.BorderWidthTop + s.PaddingTop + b.ContentHeight + s.PaddingBottom + s.BorderWidthBottom);
    }

    /// <summary>
    /// 本盒自己的变换矩阵；无变换返回 null。
    /// 先按书写顺序把函数乘成 F，再套原点：<c>p' = (p − o)·F + o</c> ⇒ 平移项 <c>t = o − o·F</c>。
    /// 矩阵乘法自己实现（行向量约定），不借 Skia 的 PostConcat——它的 this/other 方向在不同版本里
    /// 踩过一次：写出来 e/f 恰好是反的，旋转后的方块整个飞出画布，肉眼就是「元素凭空消失」。
    /// </summary>
    public static SKMatrix? Self(ComputedStyle s, SKRect r)
    {
        if (s.Transform is not { Count: > 0 } fns) return null;
        float ox = r.Left + s.TransformOriginX + s.TransformOriginXPct / 100f * r.Width;
        float oy = r.Top + s.TransformOriginY + s.TransformOriginYPct / 100f * r.Height;

        var f = SKMatrix.Identity;
        foreach (var fn in fns) f = Mul(f, Function(fn, s.FontSize, r));

        // p' = (p − o)·F + o = p·L + t_F + o − o·L ——  F 自带的平移 t_F 必须留着：
        // 漏掉它时 rotate/scale/skew 全对（t_F=0），而 translate 整条被吞成 0（实测 translateX(-50%) 不动）。
        var m = f;
        m.TransX = f.TransX + ox - (ox * f.ScaleX + oy * f.SkewX);
        m.TransY = f.TransY + oy - (ox * f.SkewY + oy * f.ScaleY);
        return m;
    }

    /// <summary>行向量约定的 3×3 乘法：返回「先套 a 再套 b」。</summary>
    static SKMatrix Mul(SKMatrix a, SKMatrix b)
    {
        var m = SKMatrix.Identity;
        m.ScaleX = a.ScaleX * b.ScaleX + a.SkewY * b.SkewX;
        m.SkewY = a.ScaleX * b.SkewY + a.SkewY * b.ScaleY;
        m.SkewX = a.SkewX * b.ScaleX + a.ScaleY * b.SkewX;
        m.ScaleY = a.SkewX * b.SkewY + a.ScaleY * b.ScaleY;
        m.TransX = a.TransX * b.ScaleX + a.TransY * b.SkewX + b.TransX;
        m.TransY = a.TransX * b.SkewY + a.TransY * b.ScaleY + b.TransY;
        return m;
    }

    /// <summary>含祖先在内的累计变换。沿 <see cref="BlockBox.Parent"/> 上溯：先套自己，再套父、祖父…</summary>
    public static SKMatrix? Accumulated(BlockBox box)
    {
        SKMatrix? total = null;
        for (BlockBox? b = box; b != null; b = b.Parent)
        {
            var m = Self(b.Style, BorderRect(b));
            if (m == null) continue;
            total = total == null ? m : Mul(total.Value, m.Value);
        }
        return total;
    }

    /// <summary>把累计变换套到画布上；返回 canvas.Save() 的层号，无变换返回 -1（调用方照样传给 Restore，成对使用）。</summary>
    public static int Apply(SKCanvas canvas, BlockBox box)
    {
        var m = Accumulated(box);
        if (m == null) return -1;
        int save = canvas.Save();
        var mat = m.Value;                      // 局部一份：SKMatrix 要按 ref 传给 Concat
        canvas.Concat(ref mat);
        return save;
    }

    public static void Restore(SKCanvas canvas, int save)
    {
        if (save >= 0) canvas.RestoreToCount(save);
    }

    static SKMatrix Function(TransformFn f, float fontSize, SKRect r) => f.Name switch
    {
        "translate" => SKMatrix.CreateTranslation(Axis(f.Args[0], fontSize, r.Width),
            f.Args.Length > 1 ? Axis(f.Args[1], fontSize, r.Height) : 0f),
        "translatex" => SKMatrix.CreateTranslation(Axis(f.Args[0], fontSize, r.Width), 0f),
        "translatey" => SKMatrix.CreateTranslation(0f, Axis(f.Args[0], fontSize, r.Height)),
        // translate3d 取 x/y 丢 z；translateZ / scaleZ 无透视时是恒等
        "translate3d" => SKMatrix.CreateTranslation(Axis(f.Args[0], fontSize, r.Width), Axis(f.Args[1], fontSize, r.Height)),
        "translatez" or "scalez" => SKMatrix.Identity,
        "scale" => SKMatrix.CreateScale(TransformParser.Scalar(f.Args[0]),
            f.Args.Length > 1 ? TransformParser.Scalar(f.Args[1]) : TransformParser.Scalar(f.Args[0])),
        "scalex" => SKMatrix.CreateScale(TransformParser.Scalar(f.Args[0]), 1f),
        "scaley" => SKMatrix.CreateScale(1f, TransformParser.Scalar(f.Args[0])),
        "rotate" or "rotatez" => SKMatrix.CreateRotationDegrees(TransformParser.Degrees(f.Args[0])),
        "skew" => SKMatrix.CreateSkew(Tan(f.Args[0]), Tan(f.Args.Length > 1 ? f.Args[1] : "0deg")),
        "skewx" => SKMatrix.CreateSkew(Tan(f.Args[0]), 0f),
        "skewy" => SKMatrix.CreateSkew(0f, Tan(f.Args[0])),
        // CSS matrix(a,b,c,d,e,f) 与 Skia 的字段一一对应（都是行向量 [a b; c d] + (e,f) 平移）
        "matrix" => CssMatrix(TransformParser.Scalar(f.Args[0]), TransformParser.Scalar(f.Args[1]),
                              TransformParser.Scalar(f.Args[2]), TransformParser.Scalar(f.Args[3]),
                              TransformParser.Scalar(f.Args[4]), TransformParser.Scalar(f.Args[5])),
        _ => SKMatrix.Identity,
    };

    /// <summary>CSS matrix(a,b,c,d,e,f) → Skia 的 3×3（行向量：a=ScaleX b=SkewY c=SkewX d=ScaleY，e/f 平移）。</summary>
    static SKMatrix CssMatrix(float a, float b, float c, float d, float e, float f)
    {
        var m = SKMatrix.Identity;
        m.ScaleX = a; m.SkewY = b; m.TransX = e;
        m.SkewX = c; m.ScaleY = d; m.TransY = f;
        return m;
    }

    static float Tan(string angle) => MathF.Tan(TransformParser.Degrees(angle) * MathF.PI / 180f);

    /// <summary>translate 的一个轴：% 相对自身边框盒该轴长度，其余长度单位按字号折算。</summary>
    static float Axis(string raw, float fontSize, float boxLen) =>
        TransformParser.Percent(raw) is float pct ? pct / 100f * boxLen : TransformParser.LengthPx(raw, fontSize);
}
