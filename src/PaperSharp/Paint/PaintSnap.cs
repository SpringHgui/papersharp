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
///
/// <para>
/// **但文字只折 y、不折 x**（<see cref="BeginText"/>，2026-09-30 探针 <c>assets/_probe-phase.html</c>）：
/// Blink 对字形走次像素定位（subpixel text positioning），x 留在分数的排版位置上。探针里 10 行
/// 几何逐盒 0.0px 全等，把文字的绝对 x 小数部从 0.1 扫到 0.9，浏览器侧落位相对引擎侧连续地走
/// −0.8 → +1.0 设备像素（= 绝对 x 的小数部 × 2），而 dy 恒为 0。跟着盒一起折 x，就是
/// table / m1 / report / pct 那一类「盒全对、字却整条错一格」的重影。
/// </para>
/// </summary>
static class PaintSnap
{
    /// <summary>开关：<c>PS_SNAP=0</c> 关掉（A/B 取证用），默认开。</summary>
    public static readonly bool Enabled =
        System.Environment.GetEnvironmentVariable("PS_SNAP") != "0";

    /// <summary>当前画布上还压着的折整量（逐层 Begin/End 严格配对）。渲染会在不同线程上
    /// 并发跑，所以按线程记账（同 <c>PdfGlyphUsage</c> 那条教训）。</summary>
    [System.ThreadStatic] static List<(float Dx, float Dy)>? _applied;

    /// <summary>把本盒的绘制原点折到整 CSS px，返回 canvas.Save() 的句柄（-1 = 未折）。</summary>
    public static int Begin(SKCanvas canvas, BlockBox box)
    {
        if (!Enabled) return -1;
        return BeginAt(canvas, box.BorderX, box.BorderY);
    }

    /// <summary>同上，但由调用方给出「本盒在文档里的绝对落笔原点」（分页路径按页内坐标记账）。</summary>
    /// <remarks>折的是**文档绝对位置**，不是相对页内容盒原点——2026-09-30 的 A/B 否证了后者：
    /// 浏览器侧 m4 的 body 自己就带分数 margin（bodyY=17.40625），Blink 从文档零点折。
    /// 引擎若改折相对位，mm 边距页整幅错一个相位（m1 可见重影 21.97%→30.30%）。</remarks>
    public static int BeginAt(SKCanvas canvas, float x, float y)
    {
        if (!Enabled) return -1;
        var (ax, ay) = Accumulated();
        // 绝对口径：本盒最终落在 round(自己的绝对位置) 那一格，与祖先折了多少无关（P0-4 的
        // _probe-snap2 嵌套链探针：k=4 的 120+0.9 浏览器给设备行 242，逐层各自折会给 240）。
        float dx = Round(x) - x - ax, dy = Round(y) - y - ay;
        if (dx == 0 && dy == 0) return -1;
        int save = canvas.Save();
        canvas.Translate(dx, dy);
        (_applied ??= new List<(float, float)>()).Add((dx + ax, dy + ay));
        return save;
    }

    public static void End(SKCanvas canvas, int save)
    {
        if (save < 0) return;
        canvas.Restore();
        if (_applied is { Count: > 0 } s) s.RemoveAt(s.Count - 1);
    }

    /// <summary>
    /// 文字段：把本层以上累计的**横向**折整退回去（纵向留着——浏览器只折 y）。
    /// 用法与 <see cref="Begin"/> 成对：<c>int t = BeginText(c); …画字…; EndText(c, t);</c>
    /// </summary>
    public static int BeginText(SKCanvas canvas)
    {
        if (!Enabled) return -1;
        float sum = Accumulated().Dx;
        if (sum == 0) return -1;
        int save = canvas.Save();
        canvas.Translate(-sum, 0);
        return save;
    }

    /// <summary>
    /// 逐行文字：<b>仅 table-cell</b> 用——浏览器把这一行的绝对 y 折到整 CSS px（向下取整），
    /// 与所在盒折到哪一格无关。普通块走 <see cref="Begin"/> 的 round(盒) + 行内分数，不要调本方法。
    /// 用法与 <see cref="EndLine"/> 成对，包住一行里的阴影/字形/装饰线。
    /// </summary>
    /// <remarks>为什么不是「跟着盒一起折」也不是「四舍五入」——2026-10-01 量 challenge 末尾那张表
    /// （行距 19.53125px，格顶相位从 .28 扫到 .00，八行）：浏览器侧每行的**基线**恒落在
    /// floor(绝对基线位)（3137 / 3156 / 3176 / 3195 / 3215 / 3234 / 3254 / 3273），
    /// 而引擎按 round(格顶)+分数内偏移画，恒得 x.766。于是 frac(格顶)≥.5 的那四行整条字低 1 CSS px
    /// ＝对拍里的 2 设备像素位移带；按四舍五入算也会得 x.766±0.23，与浏览器差半格、同样错开。
    /// 同一张表里行的**底色/边框**落在 floor(格顶)（3142→[3142,3143)），即盒也走下取整，
    /// 那是另一条口径（见台账 P0-4），本方法只动文字。
    /// <para>
    /// **为什么只对 table-cell 开**（2026-10-01 反证）：把本方法无差别挂到所有行，challenge 的
    /// 5 条单元格位移带确实清零，但普通块（p / li / div / h1 / span / ol）新长出 17 条带，
    /// 整页 1x 可见重影 11.21% → 15.34%。也就是说浏览器对普通块的基线**没有**再折一次——
    /// 它信盒的 round 原点、行内偏移留分数；只有单元格这条链（盒自己走下取整）需要单独折。
    /// </para>
    /// </remarks>
    public static int BeginLine(SKCanvas canvas, float baselineY)
    {
        if (!Enabled || !LineEnabled) return -1;
        float ay = Accumulated().Dy;
        float d = MathF.Floor(baselineY) - baselineY - ay;
        if (d == 0) return -1;
        int save = canvas.Save();
        canvas.Translate(0, d);
        return save;
    }

    public static void EndLine(SKCanvas canvas, int save)
    {
        if (save >= 0) canvas.Restore();
    }

    public static void EndText(SKCanvas canvas, int save)
    {
        if (save >= 0) canvas.Restore();
    }

    /// <summary>逐行折整的开关：<c>PS_LINESNAP=0</c> 关掉（A/B 取证用），默认开。</summary>
    public static readonly bool LineEnabled =
        System.Environment.GetEnvironmentVariable("PS_LINESNAP") != "0";

    /// <summary>画布当前累计的折整平移（栈里存的是「到该层为止的累计值」，所以取栈顶即全量）。</summary>
    static (float Dx, float Dy) Accumulated()
        => _applied is { Count: > 0 } s ? s[s.Count - 1] : (0f, 0f);

    /// <summary>Blink 的 <c>RoundToInt</c>：四舍五入、.5 远离零（正数 = floor(x+0.5)）。</summary>
    static float Round(float v) => MathF.Round(v, MidpointRounding.AwayFromZero);
}
