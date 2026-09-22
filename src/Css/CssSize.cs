namespace PaperSharp.Css;

public enum CssSizeKind { Auto, Absolute, Percent }

/// <summary>
/// 一个尺寸值：auto / 绝对长度（px/pt/em/rem，已折算成布局单位）/ 百分比。
/// 百分比必须在布局期按"包含块尺寸"解析（width% 相对包含块内容宽、height% 相对其内容高），
/// 所以这里只存原始百分比数，解析推迟到 Measure 调 <see cref="Resolve"/>。
/// </summary>
public readonly struct CssSize
{
    public readonly CssSizeKind Kind;
    public readonly float Value;   // Absolute→布局单位；Percent→百分比数（如 50 表示 50%）

    CssSize(CssSizeKind k, float v) { Kind = k; Value = v; }

    public static CssSize Auto => default;                       // Kind=Auto
    public static CssSize Abs(float px) => new(CssSizeKind.Absolute, px);
    public static CssSize Pct(float p) => new(CssSizeKind.Percent, p);

    public bool IsAuto => Kind == CssSizeKind.Auto;
    public bool IsAbsolute => Kind == CssSizeKind.Absolute;
    public bool IsPercent => Kind == CssSizeKind.Percent;
    public float Absolute => Value;   // 仅当 IsAbsolute 有意义

    /// <summary>解析成布局单位。auto → NaN；percent 且 base 为 NaN → NaN（无法解析）。</summary>
    public float Resolve(float basePx) => Kind switch
    {
        CssSizeKind.Absolute => Value,
        CssSizeKind.Percent => float.IsNaN(basePx) ? float.NaN : Value / 100f * basePx,
        _ => float.NaN,
    };

    /// <summary>
    /// margin / padding 边解析：绝对→值；百分比→按基准（包含块宽）折算，基准未知→0；auto→0。
    /// 与 <see cref="Resolve"/> 的区别是把"无法解析/auto"落到 0 而非 NaN（盒边距语义）。
    /// </summary>
    public float ResolveEdge(float basePx) => Kind switch
    {
        CssSizeKind.Absolute => Value,
        CssSizeKind.Percent => float.IsNaN(basePx) ? 0 : Value / 100f * basePx,
        _ => 0,
    };
}
