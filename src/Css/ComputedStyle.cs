using SkiaSharp;

namespace PaperSharp.Css;

/// <summary>
/// 一个元素"计算后的样式 + 结构信息"。由 StyleResolver 从 AngleSharp 的
/// IComputedStyle 转换而来，同时保留 DOM 树父子关系供布局遍历。
/// </summary>
public sealed class ComputedStyle
{
    // 结构
    public string Tag = "";
    public ComputedStyle? Parent;
    public List<ComputedStyle> Children = new();
    /// <summary>该元素直接文本子节点拼起来（不含后代元素的文本）。</summary>
    public string? TextContent;
    /// <summary>&lt;img&gt; 的 src（文件路径 / data URI / URL）。</summary>
    public string? ImageSrc;

    /// <summary>有序行内片段（文本 / 行内子元素），仅用于 inline 格式化上下文，保留 DOM 顺序。</summary>
    public List<InlinePiece> InlinePieces = new();
    /// <summary>该块是否是纯 inline 格式化上下文（非 flex/table 块，含文本或行内元素，且无块级子元素）。</summary>
    public bool IsInlineFC;

    /// <summary>display 是否行内级（inline / inline-block）。</summary>
    public static bool IsInlineLevel(string display) => display == "inline" || display == "inline-block";

    // 布局开关
    public string Display = "block"; // block | flex | none | inline | inline-block | table | table-row | table-cell | ...
    public string BoxSizing = "content-box"; // content-box | border-box

    // 表格（Display 为 table 家族时生效）
    public string BorderCollapse = "separate"; // separate | collapse
    public float BorderSpacing;                // separate 时单元格间距（横纵同值，px）
    public int Colspan = 1;                    // <td>/<th> 的 colspan

    // Flexbox（Display == "flex" 时生效；子项用 FlexGrow/Shrink/Basis/AlignSelf）
    public string FlexDirection = "row";   // row | column（暂不支持 reverse）
    public string JustifyContent = "flex-start"; // flex-start|center|flex-end|space-between|space-around|space-evenly
    public string AlignItems = "stretch";  // stretch|flex-start|center|flex-end
    public float FlexGrow = 0f;
    public float FlexShrink = 1f;
    public CssSize FlexBasis;              // auto | 绝对 | 百分比
    public CssSize Gap;                    // 主轴/交叉轴统一间距（auto|绝对|百分比，百分比按内容宽解析）
    public string AlignSelf = "auto";      // auto|stretch|flex-start|center|flex-end

    // 盒模型（已解析 px，供布局/绘制直接消费）
    public float MarginTop, MarginRight, MarginBottom, MarginLeft;
    public float PaddingTop, PaddingRight, PaddingBottom, PaddingLeft;
    public float BorderWidthTop, BorderWidthRight, BorderWidthBottom, BorderWidthLeft;
    public SKColor BorderColor = SKColors.Black;

    // margin / padding 的**声明值**（可能是 %）；布局期用 ResolveBoxSizes 按包含块宽折算进上面的 float。
    // border-width 无百分比（CSS 不接受），故只有 float。
    public CssSize MarginTopCss, MarginRightCss, MarginBottomCss, MarginLeftCss;
    public CssSize PaddingTopCss, PaddingRightCss, PaddingBottomCss, PaddingLeftCss;

    /// <summary>
    /// 把 margin/padding 的声明值按包含块行内尺寸（宽）解析成 px，写入对应的 float 字段。
    /// CSS 规定 margin/padding 的百分比横竖都相对包含块**宽**，故统一用 <paramref name="containingBlockWidth"/>。
    /// auto / 无法解析（父宽未知）→ 0。布局期 Measure 拿到 availWidth 后调用一次（幂等）。
    /// </summary>
    public void ResolveBoxSizes(float containingBlockWidth)
    {
        MarginTop = MarginTopCss.ResolveEdge(containingBlockWidth);
        MarginRight = MarginRightCss.ResolveEdge(containingBlockWidth);
        MarginBottom = MarginBottomCss.ResolveEdge(containingBlockWidth);
        MarginLeft = MarginLeftCss.ResolveEdge(containingBlockWidth);
        PaddingTop = PaddingTopCss.ResolveEdge(containingBlockWidth);
        PaddingRight = PaddingRightCss.ResolveEdge(containingBlockWidth);
        PaddingBottom = PaddingBottomCss.ResolveEdge(containingBlockWidth);
        PaddingLeft = PaddingLeftCss.ResolveEdge(containingBlockWidth);
    }

    // 尺寸（auto | 绝对 | 百分比）
    public CssSize Width;
    public CssSize Height;
    public CssSize MinWidth, MaxWidth, MinHeight, MaxHeight;

    // 定位
    public string Position = "static";   // static | relative | absolute
    public CssSize OffsetTop, OffsetRight, OffsetBottom, OffsetLeft; // % 相对包含块（Position 期解析）

    // 视觉
    public SKColor Color = SKColors.Black;
    public SKColor BackgroundColor = SKColor.Empty;
    public GradientSpec? Background;     // background-image: linear/radial-gradient(...)
    public BoxShadow? Shadow;
    public float BorderRadiusTopLeft, BorderRadiusTopRight, BorderRadiusBottomRight, BorderRadiusBottomLeft;
    public float Opacity = 1f;

    // 文本
    public string FontFamily = "sans-serif";
    public float FontSize = 14f;
    public int FontWeight = 400;
    public float LineHeightFactor = 1.4f;
    public CssTextAlign TextAlignment = CssTextAlign.Left;
    public string TextDecoration = "none";   // none | underline | line-through | overline
    public float LetterSpacing;              // px
    public string WhiteSpace = "normal";     // normal | nowrap | pre | pre-wrap | pre-line
    public string TextTransform = "none";    // none | uppercase | lowercase | capitalize
    public string TextOverflow = "clip";     // clip | ellipsis
    public string WordBreak = "normal";      // normal | break-all | keep-all
}

public enum CssTextAlign { Left, Center, Right }

/// <summary>inline 序列里的一段：文本（Owner 的样式）或一个行内子元素（引用其 ComputedStyle）。</summary>
public sealed class InlinePiece
{
    public string? Text;
    public ComputedStyle? Child;
    public bool IsBreak;   // <br>
    public static InlinePiece OfText(string t) => new() { Text = t };
    public static InlinePiece OfChild(ComputedStyle c) => new() { Child = c };
    public static readonly InlinePiece Break = new() { IsBreak = true };
}
