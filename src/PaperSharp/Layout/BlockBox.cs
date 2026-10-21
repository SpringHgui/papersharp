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
    /// <summary><c>line-height: normal</c> 且各行掺的回退字体不同时，逐行的 (基线以上, 基线以下)。
    /// 浏览器让用了回退字体的那一行按回退字体的度量长高（见 <see cref="TextShaper.LineExtents"/>），
    /// 于是同一个块里可能出现 15/19 混排。null = 各行等高，走 <see cref="ComputedStyle.LineHeightPx"/> 老路径。
    /// 只有 <see cref="WrappedLines"/> 那条纯文本通路会填（IFC 通路逐行记在 <see cref="Lines"/> 里）。</summary>
    public (float Asc, float Desc)[]? LineBoxes;
    /// <summary>纯文本第 i 行的行盒高。</summary>
    public float LineHeightAt(int i) => LineBoxes != null ? LineBoxes[i].Asc + LineBoxes[i].Desc : Style.LineHeightPx();
    /// <summary>纯文本首行基线距内容盒顶的距离。</summary>
    public float FirstLineAscent() => LineBoxes != null && LineBoxes.Length > 0
        ? LineBoxes[0].Asc : InlineLayout.FirstBaselineOffset(Style);
    /// <summary>纯文本第 i 行基线以下的高度。</summary>
    public float LineDescentAt(int i) => LineBoxes != null
        ? LineBoxes[i].Desc : Style.LineHeightPx() - InlineLayout.FirstBaselineOffset(Style);
    /// <summary>全部 WrappedLines 占掉的流高。</summary>
    public float TextFlowHeight()
    {
        int n = WrappedLines?.Length ?? 0;
        if (n == 0) return 0;
        if (LineBoxes == null) return n * Style.LineHeightPx();
        float h = 0;
        for (int i = 0; i < n; i++) h += LineHeightAt(i);
        return h;
    }
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
    /// <summary>与 TRows 同序：每行所属的**行组盒**（thead/tbody/tfoot）；裸 tr 直接挂 table 下时为 null。
    /// 行组自己的 background/border 要铺在它那一段行带上，而且必须画在行背景之下
    /// （CSS 表格绘制顺序：table 底 → 行组底 → 行底 → 格底）。缺了它，thead{background} 整条不落地。</summary>
    public List<BlockBox?>? TRowGroupBoxes;
    public float[]? TColW;
    public float[]? TRowH;
    public float TSpacing;
    public float TSpacingY;               // separate 时纵向间距：border-spacing 允许「横 纵」两值
    /// <summary>collapse 表的外圈网格线被单元格边框骑跨：格区相对表格内容框四边各内缩半个最宽共享边框。</summary>
    public float TInsetL, TInsetR, TInsetT, TInsetB;
    public int TNumCols;
    public BlockBox? TCaption;          // table-caption 子盒（若有）
    public bool TCaptionBottom;         // caption-side: bottom
    public List<int> THeaderRows = new(); // thead 行索引（在 TRows 中的下标）
    public List<int> TFooterRows = new(); // tfoot 行索引
    public float THeaderHeight;          // thead 总高（含 spacing）
    public float TFooterHeight;          // tfoot 总高（含 spacing）
    /// <summary>collapse 表里本格**内容布局**用的四条边宽 = 相邻两条折叠网格线各占半条
    /// （Blink 把整条线骑跨在两格边界中线上）。只影响内容内缩与内容宽，**不影响描边**——
    /// 描边仍按 <c>Style</c> 的整条由单格描一次（把线拆成两半各自抗锯齿会让线变软，
    /// 边框占比高的样例反而回退，见 <c>LayoutBuilder.ApplyCollapseBorders</c>）。
    /// 纵向不折半（格底/行高随之差 1px，整张表行线上下错位，实测更差），所以只有 L/R 两个访问器。
    /// 非折叠格 <see cref="CellEdgeSet"/> 为假，回落到 Style。</summary>
    public bool CellEdgeSet;
    public float CellBwL, CellBwR;
    public float EdgeL => CellEdgeSet ? CellBwL : Style.BorderWidthLeft;
    public float EdgeR => CellEdgeSet ? CellBwR : Style.BorderWidthRight;

    /// <summary>
    /// 折叠表里本格所描的那几条线要<b>骑跨</b>在边框盒的边上：Blink 把共享网格线的中心摆在两格边界中线上，
    /// 所以描边矩形 = 边框盒沿「自己拥有的线」方向各外扩半条（被清零的内部边不扩，那条由邻居描）。
    /// 传进 <c>BoxPainter.PaintBorder</c> 时宽度仍用整条，于是斜接梯形的内外缘正好对称压在中线两侧。
    /// 背景不跟扩 —— 背景铺的是边框盒本身。
    /// </summary>
    public SkiaSharp.SKRect CollapsedBorderRect(SkiaSharp.SKRect r) => CellEdgeSet
        ? new SkiaSharp.SKRect(r.Left - Style.BorderWidthLeft / 2, r.Top - Style.BorderWidthTop / 2,
                               r.Right + Style.BorderWidthRight / 2, r.Bottom + Style.BorderWidthBottom / 2)
        : r;
    // 纵向**不**折半：试过，行高/格底随之变 1px，整张表的行线上下错位，像素门反而回退
    //（m1 1x 可见重影 21.02% → 26.44%）。共享线的纵向骑跨要连着行高分配一起改，另立 P1-17。
    public float EdgeT => Style.BorderWidthTop;
    public float EdgeB => Style.BorderWidthBottom;

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

    /// <summary>边框盒总宽。折叠表格的格子用 <c>Edge*</c>（每边半条共享线）而不是 <c>Style.BorderWidth*</c>：
    /// 内容宽是按 Edge 口径从列宽里扣出来的，这里若换回整条，最后一列就会凭空多出 1px。</summary>
    public float TotalWidth =>
        Style.MarginLeft + EdgeL + Style.PaddingLeft
        + ContentWidth
        + Style.PaddingRight + EdgeR + Style.MarginRight;

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
