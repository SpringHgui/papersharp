using SkiaSharp;

namespace PaperSharp.Css;

/// <summary>盒阴影（单层）：offset-x / offset-y / blur / spread / 颜色。</summary>
public sealed class BoxShadow
{
    public float OffsetX, OffsetY, Blur, Spread;
    public SKColor Color;
}

/// <summary>
/// 渐变背景（linear / radial）。角度用 CSS 约定：0deg=朝上、90deg=朝右。
/// Colors/Stops 一一对应，Stops 为 null 时均匀分布。
/// </summary>
public sealed class GradientSpec
{
    public bool Radial;
    public float AngleDeg;              // linear 用
    public SKColor[] Colors = Array.Empty<SKColor>();
    public float[]? Stops;              // 0..1，可空

    /// <summary>在给定矩形上构造 SKShader（opacity 预乘进各色 alpha）。调用方负责 Dispose。</summary>
    public SKShader? BuildShader(SKRect r, float opacity = 1f)
    {
        if (Colors.Length < 2) return null;
        var pos = Stops ?? EvenPositions(Colors.Length);
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

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    static float[] EvenPositions(int n)
    {
        var p = new float[n];
        for (int i = 0; i < n; i++) p[i] = n == 1 ? 0 : (float)i / (n - 1);
        return p;
    }
}
