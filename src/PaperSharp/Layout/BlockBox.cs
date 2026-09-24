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

    /// <summary>
    /// 父盒，由 <see cref="LayoutBuilder.ToBoxTree"/> 建树时写入。行内原子盒（inline-block/行内 img）
    /// 与匿名盒同样挂真实宿主，所以沿此链可还原元素的祖先关系——溢出裁剪（<see cref="OverflowClip"/>）就靠它。
    /// </summary>
    public BlockBox? Parent;

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
    /// <summary>内联 &lt;svg&gt; 解析后的矢量图（PDF 中保持矢量），自然尺寸取 CullRect。</summary>
    public SKPicture? SvgPicture;
    public float NaturalW;
    public float NaturalH;
    public bool IsReplaced => Style.Tag is "img" or "svg";

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

    /// <summary>Flex 布局中间结果（仅 display:flex 盒使用）：分行信息，供 Position 复用。</summary>
    public List<FlexLine>? FlexLines;

    /// <summary>Grid 布局中间结果（仅 display:grid 盒使用）：轨道尺寸和子项放置信息。</summary>
    public GridInfo? GridInfo;

    /// <summary>WrappedLines 每行的左缩进（环绕浮动用；无浮动时为 null）。</summary>
    public float[]? LineLefts;

    /// <summary>table-cell vertical-align：内容（文本/子盒）相对单元格内容盒顶部的额外下移。</summary>
    public float ValignDY;

    /// <summary>块容器内浮动子盒的测量期放置结果：X/YRel 相对本盒内容盒左上（供 Position 直接取用）。</summary>
    public (int ChildIndex, float X, float YRel)[]? FloatPlacements;
    /// <summary>本容器（含祖先链）浮动带，y 相对本盒内容顶，供定位期复现 clear。</summary>
    public (bool IsLeft, float TopRel, float BottomRel)[]? FloatBands;

    // 测量结果
    public float ContentWidth;
    public float ContentHeight;

    /// <summary>
    /// 与首/尾流内子坍缩 margin 后，本盒对外呈现的上/下外边距（不参与坍缩时 = 自身 margin）。
    /// 父盒据此决定「子里边占多少流高」，父的父据此决定兄弟间距。
    /// </summary>
    public float EffTop, EffBottom;
    /// <summary>EffTop/EffBottom 里来自后代、已经不在本盒内占位的那一段（逃逸到本盒外面）。</summary>
    public float EscapeTop => EffTop - Style.MarginTop;
    public float EscapeBottom => EffBottom - Style.MarginBottom;

    /// <summary>MeasureBlock 算好的两端坍缩结论，Position 与分页直接复用——三处必须同一口径。</summary>
    public BlockBox? EdgeFirst, EdgeLast;
    public bool EdgeCollapseTop, EdgeCollapseBottom;

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

    /// <summary>padding box（= CSS 的溢出裁剪盒）：定位后的 border-box 内缩四边 border-width。未定位时为退化矩形。</summary>
    public SKRect PaddingBox()
    {
        var s = Style;
        return new SKRect(
            BorderX + s.BorderWidthLeft,
            BorderY + s.BorderWidthTop,
            BorderX + s.BorderWidthLeft + s.PaddingLeft + ContentWidth + s.PaddingRight,
            BorderY + s.BorderWidthTop + s.PaddingTop + ContentHeight + s.PaddingBottom);
    }

    /// <summary>
    /// 本盒的内容该被裁到哪个矩形：沿 <see cref="Parent"/> 链把每个开了
    /// <c>overflow</c> 裁剪的祖先的 padding box 求交；无裁剪祖先返回 null（不裁）。
    /// 圆角取**最内层**裁剪祖先（再往外的祖先只按矩形求交，够覆盖常见用法）。
    /// 坐标系跟随调用方：长图 = 全文坐标，分页 = 该页坐标（Position 每页重跑）。
    /// 前提：裁剪盒自身不参与跨页碎片化（见 <c>Paginator.Place</c> 的 ClipsOverflow 分支），
    /// 否则碎片化的外层祖先与后代不会落在同一页坐标系里。
    /// </summary>
    public (SKRect Rect, ComputedStyle RadiusOwner)? OverflowClip()
    {
        SKRect? acc = null;
        ComputedStyle? owner = null;
        for (var a = Parent; a != null; a = a.Parent)
        {
            if (!a.Style.ClipsOverflow) continue;
            var pad = a.PaddingBox();
            if (acc == null) { acc = pad; owner = a.Style; }
            else acc = new SKRect(
                Math.Max(pad.Left, acc.Value.Left), Math.Max(pad.Top, acc.Value.Top),
                Math.Min(pad.Right, acc.Value.Right), Math.Min(pad.Bottom, acc.Value.Bottom));
        }
        return acc == null ? null : (acc.Value, owner!);
    }
}

/// <summary>表格网格里一个单元格：盒子 + 起始列/行 + 跨列/行数。</summary>
public sealed class TCellInfo
{
    public BlockBox Cell = null!;
    public int Col, Span;
    public int Row, RowSpan;
}

/// <summary>Flex 布局中的一行：包含该行的子项索引、行主轴尺寸、行交叉轴尺寸。</summary>
public sealed class FlexLine
{
    public List<int> ItemIndices = new(); // 该行子项在 kids 列表中的索引
    public float MainSize;                // 该行主轴尺寸（行宽或列高）
    public float CrossSize;               // 该行交叉轴尺寸（行高或列宽）
}

/// <summary>Grid 布局信息：轨道尺寸、间距和子项放置位置。</summary>
public sealed class GridInfo
{
    public float[] ColWidths = Array.Empty<float>();
    public float[] RowHeights = Array.Empty<float>();
    public float ColGap;
    public float RowGap;
    /// <summary>align-content 为 start/center/end 时，行块在容器块轴确定高里的起始留白（stretch 时为 0）。</summary>
    public float RowLead;
    public List<(int childIdx, int col, int row, int colSpan, int rowSpan)> Placements = new();
    /// <summary>
    /// 与 <see cref="Placements"/> 同序的**已裁决**自对齐（start|center|end|stretch）。
    /// 测量期就要按它决定「拉伸还是收缩到 fit-content」，定位期按同一份算偏移——
    /// 两处各判一次必然分叉（margin 坍缩的三处同算是同一课）。
    /// </summary>
    public string[] SelfCross = System.Array.Empty<string>();
    public string[] SelfMain = System.Array.Empty<string>();
}
