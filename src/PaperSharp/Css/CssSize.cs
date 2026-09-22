namespace PaperSharp.Css;

public enum CssSizeKind { Auto, Absolute, Percent, Calc }

/// <summary>
/// 一个尺寸值：auto / 绝对长度（px/pt/em/rem，已折算成布局单位）/ 百分比 / calc()（线性式 p% + c）。
/// 百分比必须在布局期按"包含块尺寸"解析（width% 相对包含块内容宽、height% 相对其内容高），
/// 所以这里只存原始百分比数，解析推迟到 Measure 调 <see cref="Resolve"/>。
/// calc() 折算后同样保持线性形式：Value=百分比系数（如 50 表示 50%），Offset=绝对像素项。
/// </summary>
public readonly struct CssSize
{
    public readonly CssSizeKind Kind;
    public readonly float Value;   // Absolute→布局单位；Percent/Calc→百分比数；Calc→另加 Offset
    public readonly float Offset;  // Calc 的绝对像素项（em/rem/px 已折算）

    CssSize(CssSizeKind k, float v, float off = 0) { Kind = k; Value = v; Offset = off; }

    public static CssSize Auto => default;                       // Kind=Auto
    public static CssSize Abs(float px) => new(CssSizeKind.Absolute, px);
    public static CssSize Pct(float p) => new(CssSizeKind.Percent, p);
    public static CssSize Calc(float p, float off) => new(CssSizeKind.Calc, p, off);

    public bool IsAuto => Kind == CssSizeKind.Auto;
    public bool IsAbsolute => Kind == CssSizeKind.Absolute;
    public bool IsPercent => Kind == CssSizeKind.Percent;
    /// <summary>不依赖包含块的定值（纯绝对，或 calc 里百分比项为 0）。</summary>
    public bool IsFixed => Kind == CssSizeKind.Absolute || (Kind == CssSizeKind.Calc && Value == 0);
    public float Absolute => Kind switch
    {
        CssSizeKind.Absolute => Value,
        CssSizeKind.Calc => Offset,   // 仅当 IsFixed 有意义
        _ => float.NaN,
    };

    /// <summary>解析成布局单位。auto → NaN；percent/calc 且 base 为 NaN → NaN（无法解析）。</summary>
    public float Resolve(float basePx) => Kind switch
    {
        CssSizeKind.Absolute => Value,
        CssSizeKind.Percent => float.IsNaN(basePx) ? float.NaN : Value / 100f * basePx,
        CssSizeKind.Calc => float.IsNaN(basePx) ? float.NaN : Value / 100f * basePx + Offset,
        _ => float.NaN,
    };

    /// <summary>
    /// margin / padding 边解析：绝对→值；百分比/calc→按基准（包含块宽）折算，基准未知→只留绝对项；auto→0。
    /// 与 <see cref="Resolve"/> 的区别是把"无法解析/auto"落到 0 而非 NaN（盒边距语义）。
    /// </summary>
    public float ResolveEdge(float basePx) => Kind switch
    {
        CssSizeKind.Absolute => Value,
        CssSizeKind.Percent => float.IsNaN(basePx) ? 0 : Value / 100f * basePx,
        CssSizeKind.Calc => (float.IsNaN(basePx) ? 0 : Value / 100f * basePx) + Offset,
        _ => 0,
    };
}
