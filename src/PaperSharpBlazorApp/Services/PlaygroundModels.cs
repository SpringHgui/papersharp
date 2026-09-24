namespace PaperSharpBlazorApp.Services;

/// <summary>
/// 一个元素在某一侧（引擎 / 浏览器）的 border-box。
/// 两侧都归一到「&lt;body&gt; 边框盒左上」为原点、CSS px 为单位，故同名字段可直接相减。
/// </summary>
public sealed class DomRect
{
    /// <summary>DOM 身份串（body/div[1]/p[2]）——两侧按同一条 nth-of-type 口径数出来的对位键。</summary>
    public string DomPath { get; set; } = "";
    public string Tag { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
}

/// <summary>一次渲染的产物：图片落地句柄 + 引擎侧的几何事实。</summary>
public sealed record PlaygroundRender(
    string Token,
    /// <summary>PNG 像素宽高。</summary>
    int PixelWidth,
    int PixelHeight,
    /// <summary>PNG 应显示成的 CSS px 宽高（= 像素 ÷ 生效 scale），叠加时 1 CSS px 才等于引擎的 1 px。</summary>
    float CssWidth,
    float CssHeight,
    /// <summary>排版宽（= 画布宽 − 左右页边距）。浏览器 iframe 必须用同一个宽，否则比的不是同一次排版。</summary>
    float ContentWidth,
    /// <summary>body 边框盒在 PNG 里的原点；叠加时把 PNG 反向挪这么多，两侧内容原点才重合。</summary>
    float BodyX,
    float BodyY,
    int BoxCount,
    double ElapsedMs,
    long Bytes);

/// <summary>差异类别。顺序即严重度：ok &lt; drift &lt; 单边缺盒。</summary>
public enum DiffKind { Ok, Drift, EngineOnly, BrowserOnly }

public sealed record DiffRow(DomRect? Engine, DomRect? Browser, float Dx, float Dy, float Dw, float Dh, DiffKind Kind)
{
    /// <summary>四个分量里最坏的那个绝对值——排序与「是否算漂移」都看它。</summary>
    public float MaxDelta { get; init; }
    public string DomPath => (Engine ?? Browser)!.DomPath;
    public string Tag => (Engine ?? Browser)!.Tag;
}

public sealed record DiffSummary(
    int Total, int Ok, int Drift, int EngineOnly, int BrowserOnly,
    float WorstDelta, double MeanDriftPx, int Returned,
    /// <summary>判定「算差异」的容差（CSS px）。低于它的不计入，避免亚像素取整噪声淹没真问题。</summary>
    float TolerancePx);

public sealed record DiffResult(IReadOnlyList<DiffRow> Rows, DiffSummary Summary);
