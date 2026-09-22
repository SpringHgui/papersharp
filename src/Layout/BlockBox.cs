using SkiaSharp;
using PaperSharp.Css;

namespace PaperSharp.Layout;

/// <summary>
/// 布局盒。demo 里所有元素都当 block 处理（inline 元素会被当作 block，
/// 因此 &lt;p&gt;text &lt;span&gt;inline&lt;/span&gt;&lt;/p&gt; 这种混排会分行显示）。
/// </summary>
public sealed class BlockBox
{
    public ComputedStyle Style = null!;
    public List<BlockBox> Children = new();

    /// <summary>本元素直属文本（如果有），已经通过 TextMeasurer.Wrap 折行。</summary>
    public string[]? WrappedLines;
    public SKTypeface Typeface = null!;

    /// <summary>inline 格式化上下文：原子序列（Measure 前）与折好的行盒（Measure 后）。非 inline 块为 null。</summary>
    public List<InlineAtom>? Atoms;
    public List<LineBox>? Lines;
    public bool IsInlineFC => Atoms != null;
    /// <summary>inline FC 里是否含原子盒（inline-block / 行内 img）；含则不可按行拆分，整块放置。</summary>
    public bool HasInlineAtomic => Lines != null && Lines.Any(l => l.Frags.Any(f => f.Box != null));

    /// <summary>替换元素（&lt;img&gt;）解码后的图像 + 自然尺寸。</summary>
    public SKImage? Image;
    public float NaturalW;
    public float NaturalH;
    public bool IsReplaced => Style.Tag == "img";

    /// <summary>表格布局中间结果（仅 display:table 盒使用），供 Position 复用，避免二次计算。</summary>
    public List<List<TCellInfo>>? TRows;
    public List<BlockBox>? TRowBoxes;   // 与 TRows 同序的行盒（table-row），定位时按下标取，免每行重遍历
    public float[]? TColW;
    public float[]? TRowH;
    public float TSpacing;
    public int TNumCols;
    public BlockBox? TCaption;          // table-caption 子盒（若有）
    public bool TCaptionBottom;         // caption-side: bottom
    public List<int> THeaderRows = new(); // thead 行索引（在 TRows 中的下标）
    public List<int> TFooterRows = new(); // tfoot 行索引
    public float THeaderHeight;          // thead 总高（含 spacing）
    public float TFooterHeight;          // tfoot 总高（含 spacing）

    // 测量结果
    public float ContentWidth;
    public float ContentHeight;

    // 绝对定位：border-box 左上角（相对于根 body）
    public float BorderX;
    public float BorderY;

    public float TotalWidth =>
        Style.MarginLeft + Style.BorderWidthLeft + Style.PaddingLeft
        + ContentWidth
        + Style.PaddingRight + Style.BorderWidthRight + Style.MarginRight;

    public float TotalHeight =>
        Style.MarginTop + Style.BorderWidthTop + Style.PaddingTop
        + ContentHeight
        + Style.PaddingBottom + Style.BorderWidthBottom + Style.MarginBottom;
}

/// <summary>表格网格里一个单元格：盒子 + 起始列/行 + 跨列/行数。</summary>
public sealed class TCellInfo
{
    public BlockBox Cell = null!;
    public int Col, Span;
    public int Row, RowSpan;
}
