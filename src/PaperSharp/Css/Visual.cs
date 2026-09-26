using SkiaSharp;

namespace PaperSharp.Css;

/// <summary>盒阴影（单层）：offset-x / offset-y / blur / spread / 颜色 / inset。</summary>
public sealed class BoxShadow
{
    public float OffsetX, OffsetY, Blur, Spread;
    public SKColor Color;
    /// <summary>inset：阴影画在盒内（裁剪到 padding 盒），外阴影画在盒外。</summary>
    public bool Inset;
}

/// <summary>CSS filter 子集：blur() 与 drop-shadow()，可链式。</summary>
public sealed class CssFilter
{
    public float Blur;
    public float ShadowX, ShadowY, ShadowBlur;
    public SKColor ShadowColor;
    public bool HasShadow;

    public SKImageFilter? Build()
    {
        SKImageFilter? f = null;
        if (Blur > 0.01f) f = SKImageFilter.CreateBlur(Blur / 2f, Blur / 2f);
        if (HasShadow)
        {
            var ds = SKImageFilter.CreateDropShadow(ShadowX, ShadowY, ShadowBlur / 2f, ShadowBlur / 2f, ShadowColor);
            f = f == null ? ds : SKImageFilter.CreateCompose(ds, f);   // 先模糊再投影（compose(outer, inner)）
        }
        return f;
    }
}

/// <summary>
/// 渐变背景（linear / radial）。角度用 CSS 约定：0deg=朝上、90deg=朝右。
/// Colors/Stops 一一对应，Stops 为 null 时均匀分布。
/// </summary>
public sealed class GradientSpec
{
    public bool Radial;
    public bool Conic;                  // conic-gradient（中心固定 50% 50%）
    public float AngleDeg;              // linear 用：CSS 约定 0deg=朝上；conic 用：from 起始角
    public SKColor[] Colors = Array.Empty<SKColor>();
    public float[]? Stops;              // 0..1（% 或内插），可空；NaN=该停止位用 StopsPx
    public float[]? StopsPx;            // px 停止位（BuildShader 期按渐变线长折算），可空

    /// <summary>在给定矩形上构造 SKShader（opacity 预乘进各色 alpha）。调用方负责 Dispose。</summary>
    public SKShader? BuildShader(SKRect r, float opacity = 1f)
    {
        if (Colors.Length < 2) return null;
        var pos = ResolvePositions(r);
        var cols = opacity >= 0.999f
            ? Colors
            : System.Array.ConvertAll(Colors, c => c.WithAlpha((byte)Math.Round(c.Alpha * Clamp01(opacity))));

        if (Radial)
        {
            var center = new SKPoint(r.MidX, r.MidY);
            float radius = Math.Max(r.Width, r.Height) / 2f;
            if (radius <= 0) return null;
            return SKShader.CreateRadialGradient(center, radius, cols, pos, SKShaderTileMode.Clamp);
        }

        if (Conic)
        {
            var c = new SKPoint(r.MidX, r.MidY);
            float a = AngleDeg - 90;   // CSS 0deg=正上方 → Skia 起角自 +x 轴
            return SKShader.CreateSweepGradient(c, cols, pos, SKShaderTileMode.Clamp, a, a + 360);
        }

        // linear：按角度求起止点（把角度转成方向向量，端点取矩形在该方向上的投影极值）
        double rad = (AngleDeg - 90) * Math.PI / 180.0;   // CSS 0deg=up → 数学上 -90
        float dirX = (float)Math.Cos(rad), dirY = (float)Math.Sin(rad);
        float cx = r.MidX, cy = r.MidY;
        // 半投影长度：矩形在 dir 方向上的支撑距离
        float proj = Math.Abs(dirX) * r.Width / 2f + Math.Abs(dirY) * r.Height / 2f;
        var start = new SKPoint(cx - dirX * proj, cy - dirY * proj);
        var end = new SKPoint(cx + dirX * proj, cy + dirY * proj);
        return SKShader.CreateLinearGradient(start, end, cols, pos, SKShaderTileMode.Clamp);
    }

    /// <summary>合并 %/px 停止位：px 按渐变线全长（linear=2×投影，radial=半径）折算为 0..1 并夹取。</summary>
    float[] ResolvePositions(SKRect r)
    {
        int n = Colors.Length;
        if (Stops == null) return EvenPositions(n);
        var pos = (float[])Stops.Clone();
        if (StopsPx != null)
        {
            double rad = (AngleDeg - 90) * Math.PI / 180.0;
            float lineLen = Radial
                ? Math.Max(r.Width, r.Height) / 2f
                : 2f * (Math.Abs((float)Math.Cos(rad)) * r.Width / 2f + Math.Abs((float)Math.Sin(rad)) * r.Height / 2f);
            for (int i = 0; i < pos.Length && i < StopsPx.Length; i++)
                if (float.IsNaN(pos[i]) && !float.IsNaN(StopsPx[i]))
                    pos[i] = lineLen > 0 ? Math.Clamp(StopsPx[i] / lineLen, 0f, 1f) : 0f;
        }
        for (int i = 0; i < pos.Length; i++)
            if (float.IsNaN(pos[i])) pos[i] = (float)i / (n - 1);
        // SKShader 要求单调不减
        for (int i = 1; i < pos.Length; i++)
            if (pos[i] < pos[i - 1]) pos[i] = pos[i - 1];
        return pos;
    }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    static float[] EvenPositions(int n)
    {
        var p = new float[n];
        for (int i = 0; i < n; i++) p[i] = n == 1 ? 0 : (float)i / (n - 1);
        return p;
    }
}
