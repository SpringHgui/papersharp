using SkiaSharp;
using PaperSharp.Layout;

namespace PaperSharp.Paint;

/// <summary>
/// Blink 的「绘制原点折到整 CSS px」口径（snap to pixel layout boundaries）。
///
/// 为什么需要它（2026-09-29 探针 <c>_probe-snap.html</c> 实测，见 docs/chrome-parity-todo.md P0-4）：
/// 布局里每个盒的绝对位置在 Blink 内部是 LayoutUnit（1/64 px）的分数值，但**交给绘制时先折成整
/// CSS px**（四舍五入、.5 远离零），再乘 deviceScaleFactor。探针里 20 行 top=40.1k+0.1k 的实心条
/// 与文本各自量到：浏览器侧落位只出现在**偶数设备行**（= 整 CSS px × 2），而引擎侧按分数设备行连续走。
/// 于是在 deviceScaleFactor=2 的 2x 光栅上，几乎每个盒、每行文字都与浏览器差 0~1 设备像素——
/// 这就是像素对拍里「把浏览器层上下挪 1 设备像素，差异从 100% 掉到 20%」的那个大头。
///
/// 只在**绘制**这一层折，不动布局：盒矩形、换行点、分页一律按分数坐标算，
/// 所以 <c>tools/sweep.sh</c> 的几何对拍与 <c>boxdiff</c> 不受影响。
/// </summary>
static class PaintSnap
{
    /// <summary>开关：<c>PS_SNAP=0</c> 关掉（A/B 取证用），默认开。</summary>
    public static readonly bool Enabled =
        System.Environment.GetEnvironmentVariable("PS_SNAP") != "0";

    /// <summary>把本盒的绘制原点折到整 CSS px，返回 canvas.Save() 的句柄（-1 = 未折）。</summary>
    public static int Begin(SKCanvas canvas, BlockBox box)
    {
        if (!Enabled) return -1;
        return BeginAt(canvas, box.BorderX, box.BorderY);
    }

    /// <summary>同上，但由调用方给出「本盒在文档里的绝对落笔原点」（分页路径按页内坐标记账）。</summary>
    public static int BeginAt(SKCanvas canvas, float x, float y)
    {
        float dx = Round(x) - x, dy = Round(y) - y;
        if (dx == 0 && dy == 0) return -1;
        int save = canvas.Save();
        canvas.Translate(dx, dy);
        return save;
    }

    public static void End(SKCanvas canvas, int save)
    {
        if (save >= 0) canvas.Restore();
    }

    /// <summary>Blink 的 <c>RoundToInt</c>：四舍五入、.5 远离零（正数 = floor(x+0.5)）。</summary>
    static float Round(float v) => MathF.Round(v, MidpointRounding.AwayFromZero);
}
