using SkiaSharp;
using PaperSharp.Css;

namespace PaperSharp.Layout;

/// <summary>
/// 布局引擎：把 ComputedStyle 树转成 BlockBox 树，做两件事——
///   Measure(box, availWidth)：算每个盒子的 ContentWidth / ContentHeight（支持 block 流 + flex）
///   Position(box, x, y)：给定左上角，递归分配绝对坐标（支持 block 堆叠 + flex 的 justify/align）
/// PNG 走 Measure+Position+遍历；PDF 走 Measure+Paginator（分页时整块用 Position 定位子树）。
/// </summary>
public static class LayoutBuilder
{
    /// <summary>建盒子树，跳过 display:none。</summary>
    public static BlockBox ToBoxTree(ComputedStyle style, BlockBox? parent = null)
    {
        var box = new BlockBox
        {
            Style = style,
            Parent = parent,
            Typeface = FontManager.Get(style.FontFamily, style.FontWeight),
        };
        if (box.IsReplaced)
        {
            if (box.Style.Tag == "svg")
            {
                box.SvgPicture = ImageLoader.LoadSvg(box.Style.SvgMarkup);
                if (box.SvgPicture != null)
                {
                    box.NaturalW = box.SvgPicture.CullRect.Width;
                    box.NaturalH = box.SvgPicture.CullRect.Height;
                    // svg 无显式尺寸：回退 CSS 默认替换元素尺寸
                    if (box.NaturalW <= 0) box.NaturalW = 300;
                    if (box.NaturalH <= 0) box.NaturalH = 150;
                }
            }
            else
            {
                box.Image = ImageLoader.Load(style.ImageSrc);
                if (box.Image != null) { box.NaturalW = box.Image.Width; box.NaturalH = box.Image.Height; }
            }
        }
        // 纯 inline 块：把有序片段摊平成原子序列（inline-block/img 各自建盒），不走块级 Children 路径
        if (style.IsInlineFC)
        {
            box.Atoms = InlineLayout.BuildAtoms(style, c => ToBoxTree(c, box));
            return box;
        }
        // flex/grid 容器直接放文本：按 DOM 顺序把「文本片段」包成匿名纯文本盒、与元素子盒交错建盒，
        // 否则文本会被丢弃或错序（.prefix 的 "+86"、.oauth 的「badge→文字」、Pro 行的「icon→文字→限时」）。
        // 连续文本片段合并成一个匿名项（CSS：一段连续文本 = 一个匿名 flex/grid 子项，中间不插 gap）。
        if ((ComputedStyle.IsFlexDisplay(style.Display) || ComputedStyle.IsGridDisplay(style.Display)) && !string.IsNullOrEmpty(style.TextContent))
        {
            string? run = null;
            void FlushRun()
            {
                if (run != null) { box.Children.Add(ToBoxTree(AnonTextItem(style, run), box)); run = null; }
            }
            foreach (var piece in style.InlinePieces)
            {
                if (piece.Child != null)
                {
                    FlushRun();
                    if (piece.Child.Display != "none") box.Children.Add(ToBoxTree(piece.Child, box));
                }
                else if (!string.IsNullOrWhiteSpace(piece.Text))
                    run = run == null ? piece.Text : run + piece.Text;
            }
            FlushRun();
            return box;
        }
        foreach (var child in style.Children)
        {
            if (child.Display == "none") continue;
            box.Children.Add(ToBoxTree(child, box));
        }
        return box;
    }

    /// <summary>
    /// flex/grid 容器的匿名文本子项：克隆容器样式承载文字，但清成"纯文本盒"——
    /// 去掉从容器克隆来的盒模型装饰（否则 .prefix 的 border-right 会在文字旁多画一条线、padding 内缩文字），
    /// 并把 height:100% 等继承尺寸归 auto，令其收缩到文字高、随 align-items 与兄弟图标同高居中。
    /// </summary>
    static ComputedStyle AnonTextItem(ComputedStyle owner, string text)
    {
        var anon = owner.CloneShallow();
        anon.Children = new();
        anon.InlinePieces = new();
        anon.Before = anon.After = null;
        anon.Display = "block";
        var t = text.Trim();
        anon.TextContent = owner.TextTransform is "uppercase" or "lowercase" or "capitalize"
            ? StyleResolver.ApplyTransform(t, owner.TextTransform) : t;
        anon.Width = anon.Height = anon.MinWidth = anon.MaxWidth = anon.MinHeight = anon.MaxHeight = default;
        // 清 margin：既清声明尺寸（default 即 auto），也清"作者写了 auto"的标志位，
        // 否则匿名文本项会继承容器的 auto 边距被主轴居中，整体右移。
        anon.MarginTopCss = anon.MarginRightCss = anon.MarginBottomCss = anon.MarginLeftCss = CssSize.Abs(0);
        anon.MarginTopAuto = anon.MarginRightAuto = anon.MarginBottomAuto = anon.MarginLeftAuto = false;
        anon.PaddingTopCss = anon.PaddingRightCss = anon.PaddingBottomCss = anon.PaddingLeftCss = default;
        anon.MarginTop = anon.MarginRight = anon.MarginBottom = anon.MarginLeft = 0;
        anon.PaddingTop = anon.PaddingRight = anon.PaddingBottom = anon.PaddingLeft = 0;
        // 出流标志同样要清：容器自己 position:absolute 时，克隆来的定位属性会把匿名文本项也变成
        // 「绝对定位的 flex 子项」——它被逐出 flex 线（行高只剩图标的 12），文字再按 right:0 单独
        // 定位到行尾（pricing-top 的 .more 实测整块错位 70px）。
        anon.Position = "static";
        anon.OffsetTop = anon.OffsetRight = anon.OffsetBottom = anon.OffsetLeft = default;
        anon.BorderWidthTop = anon.BorderWidthRight = anon.BorderWidthBottom = anon.BorderWidthLeft = 0;
        anon.BorderRadiusTopLeft = anon.BorderRadiusTopRight = anon.BorderRadiusBottomRight = anon.BorderRadiusBottomLeft = 0;
        anon.BorderRadiusTopLeftY = anon.BorderRadiusTopRightY = anon.BorderRadiusBottomRightY = anon.BorderRadiusBottomLeftY = 0;
        anon.BorderRadiusPercent = null;
        anon.BackgroundColor = SKColor.Empty;
        anon.Background = null;
        anon.Shadows = null;
        return anon;
    }

    // ── 测量 ────────────────────────────────────────────────

    /// <summary>
    /// 父容器把"可用宽度"（= 父内容宽）和"包含块确定高度"（无则 NaN）传进来。
    /// width% 相对可用宽解析、height% 相对包含块确定高解析；auto 则按盒模型填充。
    /// </summary>
    public static void Measure(BlockBox box, float availWidth, float containingHeight = float.NaN)
        => Measure(box, availWidth, containingHeight, null, 0);

    /// <param name="fc">浮动上下文（块流经；flex/grid/table 子树重置为 null）。</param>
    /// <param name="outerTopY">本盒 margin-box 顶在测量期全局坐标系中的 y。</param>
    static void Measure(BlockBox box, float availWidth, float containingHeight, FloatContext? fc, float outerTopY)
    {
        // margin/padding 的 % 相对包含块宽（= availWidth）解析，必须在读任何盒边距之前完成。
        box.Style.ResolveBoxSizes(availWidth);
        ResetEdgeMargins(box);
        if (box.IsReplaced) { MeasureImage(box, availWidth); return; }
        if (box.Style.Display == "table") { MeasureTable(box, availWidth); return; }
        box.ContentWidth = ResolveWidth(box.Style.Width, availWidth, box.Style);
        float contentTopY = outerTopY + box.Style.MarginTop + box.Style.BorderWidthTop + box.Style.PaddingTop;
        MeasureHeight(box, containingHeight, fc, contentTopY);
    }

    /// <summary>flex 子项：强制内容宽 = 分配到的主轴尺寸，再测高。</summary>
    static void MeasureWithContentWidth(BlockBox box, float contentWidth, float containingHeight)
    {
        if (box.IsReplaced) { MeasureImage(box, contentWidth, availIsOwnContent: true); return; }
        box.Style.ResolveBoxSizes(contentWidth);   // 走 Measure 主入口才做的声明值→float 折算，这里补上（否则子项 padding/margin 恒 0）
        ResetEdgeMargins(box);
        box.ContentWidth = contentWidth;
        MeasureHeight(box, containingHeight, null, float.NaN);
    }

    /// <summary>测量一个子盒：absolute 走出流收缩适配（shrink-to-fit），其余按块/flex 正常测。</summary>
    static void MeasureChild(BlockBox c, float availWidth, float childContainingHeight,
        FloatContext? fc = null, float outerTopY = 0)
    {
        if (c.Style.Position == "absolute") { MeasureOutOfFlow(c, availWidth); return; }
        Measure(c, availWidth, childContainingHeight, fc, outerTopY);
    }

    /// <summary>出流（absolute）：width:auto 时按内容收缩，不填满包含块。</summary>
    static void MeasureOutOfFlow(BlockBox c, float availWidth)
    {
        var s = c.Style;
        s.ResolveBoxSizes(availWidth);
        ResetEdgeMargins(c);
        if (c.IsReplaced) { MeasureImage(c, availWidth); return; }
        if (s.Width.IsAuto)
        {
            float deco = s.MarginLeft + s.MarginRight + s.BorderWidthLeft + s.BorderWidthRight + s.PaddingLeft + s.PaddingRight;
            float nat = IntrinsicContentWidth(c);
            c.ContentWidth = Math.Max(0, Math.Min(nat, availWidth - deco));
            MeasureHeight(c, float.NaN, null, float.NaN);
        }
        else Measure(c, availWidth, float.NaN);
    }

    /// <summary>已知 ContentWidth 后算 ContentHeight：解析自身 height%（相对 containingHeight），否则按内容/flex 求自然高。</summary>
    static void MeasureHeight(BlockBox box, float containingHeight, FloatContext? fc, float contentTopY)
    {
        var s = box.Style;
        float bb = s.BoxSizing == "border-box"
            ? s.PaddingTop + s.PaddingBottom + s.BorderWidthTop + s.BorderWidthBottom : 0;
        float hRes = s.Height.Resolve(containingHeight);
        float? myDefH = float.IsNaN(hRes) ? null : Math.Max(0, hRes - bb);
        float naturalH = ComputedStyle.IsFlexDisplay(s.Display)
            ? MeasureFlex(box, myDefH)
            : ComputedStyle.IsGridDisplay(s.Display)
            ? MeasureGrid(box, myDefH)
            : MeasureBlock(box, myDefH ?? float.NaN, fc, contentTopY);
        box.ContentHeight = ClampSize(myDefH ?? naturalH, s.MinHeight, s.MaxHeight, containingHeight, bb);
        // 单行文本控件：浏览器把整行文字在内容盒里垂直居中（<input> 定高时文字贴顶会一眼看穿），
        // 而不是按普通块那样从内容盒顶部起排。复用 table-cell 的 ValignDY 通道。
        if (s.Tag == "input" && box.WrappedLines is { Length: > 0 } inLines)
        {
            float free = box.ContentHeight - inLines.Length * s.LineHeightPx();
            if (free > 0) box.ValignDY = free / 2;
        }
    }

    static float ResolveWidth(CssSize w, float availWidth, ComputedStyle s)
    {
        float decoH = s.PaddingLeft + s.PaddingRight + s.BorderWidthLeft + s.BorderWidthRight;
        float bb = s.BoxSizing == "border-box" ? decoH : 0;
        float cw = w.IsAuto
            ? Math.Max(0, availWidth - s.MarginLeft - s.MarginRight - decoH)
            : Math.Max(0, w.Resolve(availWidth) - bb);
        return ClampSize(cw, s.MinWidth, s.MaxWidth, availWidth, bb);
    }

    /// <summary>把尺寸夹到 [min,max]（各自 % 相对 base 解析；auto/none→NaN 跳过）。deco>0 表示 min/max 是 border-box，需换成 content。</summary>
    static float ClampSize(float v, CssSize min, CssSize max, float base_, float deco = 0)
    {
        var mn = min.Resolve(base_);
        if (!float.IsNaN(mn)) v = Math.Max(v, mn - deco);
        var mx = max.Resolve(base_);
        if (!float.IsNaN(mx)) v = Math.Min(v, mx - deco);
        return Math.Max(0, v);
    }

    /// <summary>替换元素（img / 内联 svg）：按 CSS 尺寸（含 width%）+ 自然尺寸定 content 盒，保持纵横比。
    /// <paramref name="availIsOwnContent"/>：调用方传的不是包含块可用宽而是**已分配给本盒的内容宽**
    /// （flex 子项走这条），此时自身 margin 不能再从可用空间里扣——扣了会把 16px 图标压成 8px。</summary>
    static void MeasureImage(BlockBox box, float availWidth, bool availIsOwnContent = false)
    {
        var s = box.Style;
        float decoW = s.BorderWidthLeft + s.BorderWidthRight + s.PaddingLeft + s.PaddingRight
            + (availIsOwnContent ? 0 : s.MarginLeft + s.MarginRight);
        float availContent = Math.Max(0, availWidth - decoW);
        float nw = box.NaturalW, nh = box.NaturalH;
        float ratio = nw > 0 && nh > 0 ? nh / nw : 1f;

        float wRes = s.Width.Resolve(availWidth);   // 绝对或 % of 包含块宽
        float hRes = s.Height.Resolve(float.NaN);   // img 的 height% 无可靠基准 → 视为 auto
        bool hasW = !float.IsNaN(wRes), hasH = !float.IsNaN(hRes);

        float w, h;
        if (hasW && hasH) { w = wRes; h = hRes; }
        else if (hasW) { w = wRes; h = w * ratio; }
        else if (hasH) { h = hRes; w = nh > 0 ? h / ratio : h; }
        else { w = nw; h = nh; if (w > availContent && w > 0) { h *= availContent / w; w = availContent; } }

        box.ContentWidth = Math.Max(0, w);
        box.ContentHeight = Math.Max(0, h);
    }

    // ── 表格 ────────────────────────────────────────────────

    /// <summary>
    /// 表格测量：穿透 row-group 收集行 → 按 colspan/rowspan 建网格 → 列宽用「单跨单元格 max-content」定基准、
    /// 多跨不足则均摊 → 显式 width 或超出可用宽时按比例缩放列 → 逐格测高、行高取格最大值、格撑满行高。
    /// 结果（列宽/行高/单元格几何）存回 box 供 PositionTable 复用。
    /// </summary>
    static void MeasureTable(BlockBox box, float availWidth)
    {
        var s = box.Style;
        bool collapse = s.BorderCollapse == "collapse";
        float spacingX = collapse ? 0 : s.BorderSpacing;
        float spacingY = collapse ? 0 : s.BorderSpacingV;

        // 识别 table-caption 子盒（<caption>），从表格子元素中分离出来单独测量
        BlockBox? caption = null;
        foreach (var ch in box.Children)
            if (ch.Style.Display == "table-caption") { caption = ch; break; }
        box.TCaption = caption;
        box.TCaptionBottom = s.CaptionSide == "bottom";
        if (caption != null)
            Measure(caption, availWidth - s.PaddingLeft - s.PaddingRight - s.BorderWidthLeft - s.BorderWidthRight);

        var rowBoxes = new List<List<BlockBox>>();
        var rowGroupTypes = new List<string>(); // 每行所属的行组类型
        CollectRows(box, rowBoxes, rowGroupTypes);
        var rowBoxList = new List<BlockBox>();
        CollectRowBoxes(box, rowBoxList);

        // 建网格：处理 colspan + rowspan，用 occupied 数组跟踪被上行 rowspan 占据的列
        int numCols = 0;
        int numRows = rowBoxes.Count;
        var grid = new List<List<TCellInfo>>();
        // occupied[r] = 到第 r 行为止仍被 rowspan 占据的列集合（值=剩余跨行数）
        var occupied = new Dictionary<int, int>[numRows];
        for (int i = 0; i < numRows; i++) occupied[i] = new Dictionary<int, int>();

        for (int r = 0; r < numRows; r++)
        {
            // 从上一行继承尚未到期的 rowspan 占用
            if (r > 0)
                foreach (var kv in occupied[r - 1])
                    if (kv.Value > 1) occupied[r][kv.Key] = kv.Value - 1;

            var infos = new List<TCellInfo>();
            int c = 0;
            foreach (var cell in rowBoxes[r])
            {
                if (cell.Style.Display != "table-cell") continue;
                // 跳过被上行 rowspan 占据的列
                while (occupied[r].ContainsKey(c)) c++;
                int span = Math.Max(1, cell.Style.Colspan);
                int rspan = Math.Max(1, cell.Style.Rowspan);
                infos.Add(new TCellInfo { Cell = cell, Col = c, Span = span, Row = r, RowSpan = rspan });
                // 标记被此 rowspan 占据的列（对后续行）
                for (int dr = 1; dr < rspan && r + dr < numRows; dr++)
                    for (int dc = 0; dc < span; dc++)
                        occupied[r + dr][c + dc] = rspan - dr;
                c += span;
            }
            numCols = Math.Max(numCols, c);
            grid.Add(infos);
        }
        if (numCols == 0 || grid.Count == 0) { box.ContentWidth = 0; box.ContentHeight = 0; return; }

        // 先解析各单元格的 margin/padding（含 %），否则下面 CellHDeco 读到未解析的 0 → 列宽偏窄致折行
        foreach (var infos in grid)
            foreach (var info in infos)
                info.Cell.Style.ResolveBoxSizes(availWidth);

        // collapse：内部只保留 top+left 边框，右/底仅末列/末行保留，避免共享边双线
        if (collapse) ApplyCollapseBorders(grid, numCols);

        // 外层网格线被单元格边框「骑跨」：Chrome 实测 collapse 表的格区相对表格框四边各内缩该线宽的一半
        // （1px 边框 → 行盒 (24,137) 1236 变 (24.5,137.5) 1235）。列宽分配先扣掉这一圈。
        float insetL = 0, insetR = 0, insetT = 0, insetB = 0;
        if (collapse)
            for (int r = 0; r < grid.Count; r++)
                foreach (var info in grid[r])
                {
                    var b = info.Cell.Style;
                    if (info.Col == 0) insetL = Math.Max(insetL, b.BorderWidthLeft / 2);
                    if (info.Col + info.Span >= numCols) insetR = Math.Max(insetR, b.BorderWidthRight / 2);
                    if (r == 0) insetT = Math.Max(insetT, b.BorderWidthTop / 2);
                    if (r + info.RowSpan >= grid.Count) insetB = Math.Max(insetB, b.BorderWidthBottom / 2);
                }

        // 横向装饰：separate 是「左右各一个 outer spacing + 列间 n−1 个」，collapse 只吃半框
        float decoX = collapse ? insetL + insetR : spacingX * (numCols + 1);
        float decoY = collapse ? 0 : spacingY;   // 纵向：separate 首尾各加一个 spacing，collapse 靠末行裁剪

        // 计算目标网格宽（用于解析百分比列宽）：基准沿用「表格内容框扣掉列缝」，不扣首尾外圈缝
        float padBorder = s.PaddingLeft + s.PaddingRight + s.BorderWidthLeft + s.BorderWidthRight;
        float wResTbl = s.Width.Resolve(availWidth);
        float pctDecoX = collapse ? insetL + insetR : spacingX * (numCols - 1);
        float targetGridForPct = !float.IsNaN(wResTbl)
            ? Math.Max(0, wResTbl - padBorder - pctDecoX)
            : Math.Max(0, availWidth - s.MarginLeft - s.MarginRight - padBorder - pctDecoX);

        // 列宽（单元格 outer 盒宽）；+1px 余量避免「内容宽恰好等于内在宽」触发亚像素折行。
        // 写了 width（px/%/calc）的列是 CSS 2.1 §17.5.2.2 的 special column：就用声明值，
        // 内容再长也不参与撑宽（_tbl 实测：40% 列放着 46 个汉字仍是 319.6=40%，剩下 60% 全归 auto 列）。
        // 此前把声明值当"下限"与内在宽取大，等于让内容反过来吃掉比例列（m3 实测差 337px）。
        float[] colW = new float[numCols];
        bool[] colSpec = new bool[numCols];
        // 可用宽不够时浏览器不是把各列按 max-content 等比压，而是让每列在 [min-content, max-content]
        // 之间按同一因子收缩（CSS 2.1 §17.5.2.2 / Blink AutoTableLayout::ComputeColumnWidths）。
        // 等比压会把「一个长词」的列压到折行、把「一堆短词」的列压得比下限还窄，m1 实测每列差 6px、牵连 400 盒。
        float[] colMin = new float[numCols];
        for (int r = 0; r < grid.Count; r++)
        {
            foreach (var info in grid[r])
            {
                if (info.Span == 1)
                {
                    float intrinsic = IntrinsicContentWidth(info.Cell) + CellHDeco(info.Cell, collapse);
                    float spec = SpecifiedCellWidth(info.Cell, targetGridForPct, collapse);
                    if (spec > 0) { colW[info.Col] = Math.Max(colW[info.Col], spec); colMin[info.Col] = colW[info.Col]; colSpec[info.Col] = true; }
                    else
                    {
                        colW[info.Col] = Math.Max(colW[info.Col], intrinsic);
                        colMin[info.Col] = Math.Max(colMin[info.Col], Math.Min(intrinsic, MinContentWidth(info.Cell) + CellHDeco(info.Cell, collapse)));
                    }
                }
            }
        }
        // 多跨：不足则把差额均摊到所跨各列（定宽列不参与，摊给同组的 auto 列）
        foreach (var infos in grid)
            foreach (var info in infos)
                if (info.Span > 1)
                {
                    float intrinsic = IntrinsicContentWidth(info.Cell) + CellHDeco(info.Cell, collapse) + spacingX * (info.Span - 1) + 1;
                    float pctW = SpecifiedCellWidth(info.Cell, targetGridForPct, collapse);
                    float need = Math.Max(intrinsic, pctW);
                    float have = 0; for (int j = info.Col; j < info.Col + info.Span; j++) have += colW[j];
                    float deficit = need - have;
                    if (deficit > 0)
                    {
                        var free = SpreadTargets(info, numCols, colSpec);
                        float add = deficit / free.Count;
                        foreach (int j in free) colW[j] += add;
                    }
                    // 下限同样要抬：合并格里的长词也是硬底，否则收缩阶段会把所跨各列压到比它还能折。
                    float needMin = Math.Min(intrinsic, MinContentWidth(info.Cell) + CellHDeco(info.Cell, collapse) + spacingX * (info.Span - 1));
                    float haveMin = 0; for (int j = info.Col; j < info.Col + info.Span; j++) haveMin += colMin[j];
                    if (needMin - haveMin > 0)
                    {
                        var free = SpreadTargets(info, numCols, colSpec);
                        float add = (needMin - haveMin) / free.Count;
                        foreach (int j in free) colMin[j] = Math.Min(colMin[j] + add, colW[j]);
                    }
                }

        // 目标总宽：显式 width 优先；否则自然宽夹到可用宽
        for (int j = 0; j < numCols; j++) colMin[j] = Math.Min(colMin[j], colW[j]);   // 下限永不高于上限（多跨摊分可能先抬 w 再抬 min）
        float gridW = 0; for (int j = 0; j < numCols; j++) gridW += colW[j];
        float gapW = decoX;   // 列缝：separate 的 spacing（含首尾外圈）/ collapse 的骑跨半框
        // 声明 width 是表格**内容框**宽（列缝算在内），所以可用列宽 = width − 缝；auto 时自然宽夹到可用宽。
        float targetTotal = !float.IsNaN(wResTbl) ? wResTbl + padBorder
            : Math.Min(gridW + gapW + padBorder, Math.Max(0, availWidth - s.MarginLeft - s.MarginRight));
        float targetGrid = Math.Max(0, targetTotal - padBorder - gapW);
        if (gridW > 0.5f && Math.Abs(targetGrid - gridW) > 0.5f)
        {
            // 有 special 列时它们钉死不动，差额只由 auto 列吸收（_tbl 实测 case1/case4）。
            float specSum = 0, autoMx = 0, autoMin = 0;
            int autoN = 0;
            for (int j = 0; j < numCols; j++)
            {
                if (colSpec[j]) specSum += colW[j];
                else { autoMx += colW[j]; autoMin += colMin[j]; autoN++; }
            }
            float left = targetGrid - specSum;
            if (autoMx > 0.5f && left > 0.5f)
            {
                if (left >= autoMx)
                {
                    // 宽裕：按比例撑开。Blink 的列权重就是该列当前的 preferred 宽
                    // （ColumnWidthWeight 末尾 `return max(1, effective_width)`），
                    // 等分富余会把窄列喂成宽列（table 样例实测 col2 反多出 87px）。
                    float k = left / autoMx;
                    for (int j = 0; j < numCols; j++) if (!colSpec[j]) colW[j] *= k;
                }
                else if (left > autoMin)
                {
                    float f = (left - autoMin) / (autoMx - autoMin);
                    for (int j = 0; j < numCols; j++) if (!colSpec[j]) colW[j] = colMin[j] + (colW[j] - colMin[j]) * f;
                }
                else
                {
                    // 连 min-content 都喂不满：各列停在下限，表格照例溢出（浏览器同样溢出）。
                    for (int j = 0; j < numCols; j++) if (!colSpec[j]) colW[j] = colMin[j];
                }
                gridW = 0; for (int j = 0; j < numCols; j++) gridW += colW[j];
            }
            else if (autoMx <= 0.5f && left > 0.5f)
            {
                for (int j = 0; j < numCols; j++) colW[j] = targetGrid / numCols;
                gridW = targetGrid;
            }
            else if (autoMx <= 0.5f)
            {
                float scale = targetGrid / gridW;
                for (int j = 0; j < numCols; j++) colW[j] *= scale;
                gridW = targetGrid;
            }
            // left <= 0：special 列已吃满目标宽，auto 列保持内在宽让表格溢出（浏览器同样溢出）
        }
        box.ContentWidth = gridW + gapW;

        // 逐格测量 + 行高
        float[] rowH = new float[grid.Count];
        float totalH = 0;
        for (int r = 0; r < grid.Count; r++)
        {
            float rh = 0;
            foreach (var info in grid[r])
            {
                var cell = info.Cell;
                ZeroCellMargins(cell);
                cell.Style.Width = CssSize.Auto;   // 单元格 width 只是列宽线索，测量期以列宽为准
                float cellOuterW = spacingX * (info.Span - 1);
                for (int j = info.Col; j < info.Col + info.Span; j++) cellOuterW += colW[j];
                Measure(cell, cellOuterW);
                // 跨行格不计入本行高：它自己的上下两条 collapse 边框线（:676 只对未跨到末行的格子清过底边）
                // 会让它比单行格净高 1px，全记在本行就等于把整行撑高 1px（m1 实测 row6 39.3125 vs 浏览器 38.3125）。
                if (info.RowSpan > 1) continue;
                rh = Math.Max(rh, cell.ContentHeight + CellVDeco(cell));
            }
            rowH[r] = rh;
            totalH += rh;
        }
        // rowspan 高度分摊：若跨行单元格内容高于所跨行高之和，将差额均摊到所跨行
        for (int r = 0; r < grid.Count; r++)
            foreach (var info in grid[r])
                if (info.RowSpan > 1)
                {
                    float need = info.Cell.ContentHeight + CellVDeco(info.Cell);
                    float have = 0;
                    for (int dr = 0; dr < info.RowSpan && r + dr < grid.Count; dr++) have += rowH[r + dr];
                    have += spacingY * (Math.Min(info.RowSpan, grid.Count - r) - 1);
                    float deficit = need - have;
                    if (deficit > 0)
                    {
                        float add = deficit / Math.Min(info.RowSpan, grid.Count - r);
                        for (int dr = 0; dr < info.RowSpan && r + dr < grid.Count; dr++) rowH[r + dr] += add;
                    }
                }
        totalH = 0;
        for (int r = 0; r < grid.Count; r++) totalH += rowH[r];
        // 回填：格撑满行高（背景/边框铺满整行），并按 vertical-align 记内容偏移
        float clipLastRow = collapse ? insetT + insetB : 0f;   // 末行外框线被骑跨掉的净高（Chrome 实测 dh −1.0）
        for (int r = 0; r < grid.Count; r++)
            foreach (var info in grid[r])
            {
                float full;
                if (info.RowSpan == 1)
                    full = rowH[r];
                else
                {
                    full = spacingY * (Math.Min(info.RowSpan, grid.Count - r) - 1);
                    for (int dr = 0; dr < info.RowSpan && r + dr < grid.Count; dr++) full += rowH[r + dr];
                }
                full = Math.Max(0, full - CellVDeco(info.Cell));
                if (r + info.RowSpan >= grid.Count) full = Math.Max(0, full - clipLastRow);
                float free = Math.Max(0, full - info.Cell.ContentHeight);
                info.Cell.ContentHeight = full;
                info.Cell.ValignDY = info.Cell.Style.VerticalAlign switch
                {
                    "bottom" => free,
                    "middle" => free / 2,
                    _ => 0,   // top / baseline / length 近似按顶部
                };
            }
        totalH += spacingY * (grid.Count - 1) + 2 * decoY;
        // caption 高度加到表格总高
        if (caption != null)
            totalH += caption.TotalHeight;
        box.ContentHeight = totalH;

        // 识别 thead/tfoot 行并计算其总高（供跨页续表头/尾用）
        float headerH = 0, footerH = 0;
        for (int r = 0; r < rowGroupTypes.Count; r++)
        {
            if (rowGroupTypes[r] == "table-header-group")
            {
                box.THeaderRows.Add(r);
                headerH += rowH[r] + (r < grid.Count - 1 ? spacingY : 0);
            }
            else if (rowGroupTypes[r] == "table-footer-group")
            {
                box.TFooterRows.Add(r);
                footerH += rowH[r] + (r < grid.Count - 1 ? spacingY : 0);
            }
        }
        box.THeaderHeight = headerH;
        box.TFooterHeight = footerH;

        // 存回供定位
        box.TRows = grid; box.TColW = colW; box.TRowH = rowH; box.TSpacing = spacingX; box.TSpacingY = spacingY;
        box.TNumCols = numCols;
        box.TInsetL = insetL; box.TInsetR = insetR; box.TInsetT = insetT; box.TInsetB = insetB;
        box.TRowBoxes = rowBoxList;
    }

    /// <summary>表格定位：把网格按列宽/行高铺开，逐格 PositionRec；行盒撑满整行宽以让行背景可见。支持 rowspan 跨行定位 + caption。</summary>
    static void PositionTable(BlockBox box, float contentX, float contentY, CB cb)
    {
        if (box.TRows == null || box.TColW == null || box.TRowH == null) return;
        float spacingX = box.TSpacing, spacingY = box.TSpacingY;
        float gridW = box.ContentWidth;
        int numRows = box.TRows.Count;
        // collapse：格区相对表格内容框被外圈网格线骑跨，铺在 [insetL, gridW−insetR] × [insetT, 底−insetB]
        float originX = contentX + box.TInsetL + spacingX;
        float clipLast = box.TInsetT + box.TInsetB;   // 末行矩形比行高矮这么多（顶/底各被半框吃掉一处）

        // caption 定位：top 在网格上方，bottom 在网格下方
        float gridStartY = contentY + box.TInsetT + spacingY;
        if (box.TCaption != null)
        {
            if (!box.TCaptionBottom)
            {
                PositionRec(box.TCaption, contentX, contentY, cb);
                gridStartY = contentY + box.TCaption.TotalHeight + box.TInsetT + spacingY;
            }
        }

        // 预计算每行顶边 y 坐标
        float[] rowY = new float[numRows];
        float y = gridStartY;
        for (int r = 0; r < numRows; r++)
        {
            rowY[r] = y;
            y += box.TRowH[r] + spacingY;
        }

        // 行盒几何：横跨整表宽、纵占行高
        for (int r = 0; r < numRows; r++)
        {
            var rowBox = box.TRowBoxes != null && r < box.TRowBoxes.Count ? box.TRowBoxes[r] : null;
            if (rowBox != null)
            {
                rowBox.BorderX = originX; rowBox.BorderY = rowY[r];
                rowBox.ContentWidth = gridW - box.TInsetL - box.TInsetR - 2 * spacingX;
                rowBox.ContentHeight = Math.Max(0, box.TRowH[r] - (r == numRows - 1 ? clipLast : 0));
            }
        }

        // direction:rtl 的表列序镜像（Chrome 实测首列贴右缘）
        bool rtl = box.Style.Direction == "rtl";
        for (int r = 0; r < numRows; r++)
        {
            foreach (var info in box.TRows[r])
                PositionRec(info.Cell, TableCellX(box, info.Col, info.Span, rtl, originX), rowY[r], cb);
        }
        // bottom caption 定位在网格下方
        if (box.TCaption != null && box.TCaptionBottom)
        {
            float captionY = gridStartY;
            for (int r = 0; r < numRows; r++) captionY += box.TRowH[r] + spacingY;
            PositionRec(box.TCaption, contentX, captionY, cb);
        }
    }

    /// <summary>跨页续表：定位指定行子集（thead + 部分 body + tfoot）到给定 Y 起点。返回占用的总高度。</summary>
    public static float PositionTableRows(BlockBox box, float contentX, float startY, List<int> rowIndices)
    {
        if (box.TRows == null || box.TColW == null || box.TRowH == null || rowIndices.Count == 0) return 0;
        float spacingX = box.TSpacing, spacingY = box.TSpacingY;
        float originX = contentX + box.TInsetL + spacingX;
        float rowRectW = box.ContentWidth - box.TInsetL - box.TInsetR - 2 * spacingX;
        float y = startY + box.TInsetT + spacingY;

        foreach (var r in rowIndices)
        {
            if (r >= box.TRows.Count) continue;
            // 行盒几何
            var rowBox = box.TRowBoxes != null && r < box.TRowBoxes.Count ? box.TRowBoxes[r] : null;
            if (rowBox != null)
            {
                rowBox.BorderX = originX; rowBox.BorderY = y;
                rowBox.ContentWidth = rowRectW; rowBox.ContentHeight = box.TRowH[r];
            }
            // 逐格定位
            bool rtl = box.Style.Direction == "rtl";
            foreach (var info in box.TRows[r])
                PositionRec(info.Cell, TableCellX(box, info.Col, info.Span, rtl, originX), y, null);
            y += box.TRowH[r] + spacingY;
        }
        return y - startY - spacingY; // 减去末尾多余的 spacing
    }

    /// <summary>
    /// 第 col 列、跨 span 列的单元格左边缘 x。列位置一律由列号算前缀和得出（不再沿行游标推进），
    /// 这样 direction:rtl 的镜像才有落脚点：以整网格右缘为轴翻转，首列贴右、次列往左。
    /// </summary>
    static float TableCellX(BlockBox box, int col, int span, bool rtl, float originX)
    {
        var colW = box.TColW!;
        float sx = box.TSpacing;
        float Left(int j)
        {
            float p = originX;
            for (int k = 0; k < j && k < colW.Length; k++) p += colW[k] + sx;
            return p;
        }
        float l = Left(col), w = sx * (span - 1);
        for (int k = col; k < col + span && k < colW.Length; k++) w += colW[k];
        if (!rtl) return l;
        int n = colW.Length;
        return originX + (Left(n - 1) + colW[n - 1] - (l + w));
    }

    static void CollectRows(BlockBox table, List<List<BlockBox>> rows, List<string> groupTypes)
    {
        foreach (var ch in table.Children)
        {
            var d = ch.Style.Display;
            if (d == "table-row") { rows.Add(ch.Children); groupTypes.Add("table-row-group"); }
            else if (d is "table-row-group" or "table-header-group" or "table-footer-group")
                CollectRowsFromGroup(ch, d, rows, groupTypes);
        }
    }

    static void CollectRowsFromGroup(BlockBox group, string groupType, List<List<BlockBox>> rows, List<string> groupTypes)
    {
        foreach (var ch in group.Children)
        {
            var d = ch.Style.Display;
            if (d == "table-row") { rows.Add(ch.Children); groupTypes.Add(groupType); }
        }
    }

    static void CollectRowBoxes(BlockBox table, List<BlockBox> rows)
    {
        foreach (var ch in table.Children)
        {
            var d = ch.Style.Display;
            if (d == "table-row") rows.Add(ch);
            else if (d is "table-row-group" or "table-header-group" or "table-footer-group") CollectRowBoxes(ch, rows);
        }
    }

    static void ApplyCollapseBorders(List<List<TCellInfo>> grid, int numCols)
    {
        int lastRow = grid.Count - 1;
        for (int r = 0; r < grid.Count; r++)
            foreach (var info in grid[r])
            {
                var b = info.Cell.Style;
                if (info.Col + info.Span < numCols) b.BorderWidthRight = 0;
                // rowspan 单元格仅在其跨度的最后一行才清除底边框
                if (r + info.RowSpan - 1 < lastRow) b.BorderWidthBottom = 0;
            }
    }

    /// <summary>
    /// 单元格横向装饰。collapse 表里每条网格线由相邻两格**共享**（Chrome 把折叠框线居中放在格线上），
    /// 所以每格只应计入半条边框；按整条计会让每列宽 +1px，并经自动表格的比例分配摊到全表
    /// （m1 金额列实测：Chrome 内容 65.97 + 装饰 17 = 82.97，引擎 65.98 + 18 = 83.98，
    /// 多出的 1.01px 抬高 Σmax → k 从 2.2954 掉到 2.2909 → 该表 51 行 × 7 列共 291 个盒各偏 0.2~2.4px）。
    /// separate 表每格边框各自独立，仍按整条计。
    /// </summary>
    static float CellHDeco(BlockBox c, bool collapse) => c.Style.PaddingLeft + c.Style.PaddingRight
        + (collapse ? (c.Style.BorderWidthLeft + c.Style.BorderWidthRight) / 2
                    : c.Style.BorderWidthLeft + c.Style.BorderWidthRight);
    static float CellVDeco(BlockBox c) => c.Style.PaddingTop + c.Style.PaddingBottom + c.Style.BorderWidthTop + c.Style.BorderWidthBottom;

    /// <summary>多跨格的差额该摊给谁：所跨列里的 auto 列；整组都是 special 列时只能全摊（没有别的出口）。</summary>
    static List<int> SpreadTargets(TCellInfo info, int numCols, bool[] colSpec)
    {
        var free = new List<int>();
        for (int j = info.Col; j < info.Col + info.Span && j < numCols; j++) if (!colSpec[j]) free.Add(j);
        if (free.Count == 0)
            for (int j = info.Col; j < info.Col + info.Span && j < numCols; j++) free.Add(j);
        return free;
    }

    /// <summary>解析单元格 width:% 为 outer 盒宽（含 deco）；非百分比返回 0。</summary>
    static float ResolvePctCellWidth(BlockBox cell, float targetGridW, bool collapse)
    {
        var w = cell.Style.Width;
        if (!w.IsPercent && w.Kind != CssSizeKind.Calc) return 0;
        float contentW = w.Resolve(targetGridW);
        if (float.IsNaN(contentW) || contentW <= 0) return 0;
        return contentW + CellHDeco(cell, collapse);
    }

    /// <summary>
    /// 单元格的**声明**宽（border-box 口径，即列 outer 宽）：% 与 calc 相对目标网格宽，px/em 直接用；
    /// box-sizing:border-box 时声明值本身已含装饰。auto 或非法值返回 0（交回内在宽决定）。
    /// </summary>
    static float SpecifiedCellWidth(BlockBox cell, float targetGridW, bool collapse)
    {
        var w = cell.Style.Width;
        if (w.IsAuto) return 0;
        float contentW = w.Kind is CssSizeKind.Absolute or CssSizeKind.Calc
            ? w.Resolve(float.NaN)            // px/em/calc 的绝对项：与基准无关
            : w.Resolve(targetGridW);         // %
        if (float.IsNaN(contentW) || contentW <= 0) return 0;
        // 百分比单元格宽是**列的外宽**，不再叠 padding+border：Blink 的 auto 表布局把 x% 直接分配给列，
        // 内容盒 = x% − deco。实测 m3 `.va-table{width:100%} .va-top{width:40%}`（padding 6 / border 1，
        // content-box）：浏览器 td rect = 487.6（= 40% × 1219），引擎按 contentW+13 报 500.6，
        // 多出的 13px 从另外三个 auto 列里扣走（3.7+4.0+5.3），四格齐漂。
        // 绝对值（px/em/calc）仍是 content-box 语义，要叠 deco。
        return cell.Style.BoxSizing == "border-box" || w.Kind == CssSizeKind.Percent
            ? contentW : contentW + CellHDeco(cell, collapse);
    }

    static void ZeroCellMargins(BlockBox c)
    {
        var s = c.Style;
        s.MarginLeftCss = s.MarginRightCss = s.MarginTopCss = s.MarginBottomCss = CssSize.Auto;
        s.MarginLeft = s.MarginRight = s.MarginTop = s.MarginBottom = 0;
    }

    // ── margin 坍缩（CSS 2.1 §8.3.1）─────────────────────────────────────
    //
    // 此前引擎完全不坍缩：两个相邻 <p>（各 margin 16px）之间留 32px，浏览器只留 16px。
    // 单看一块不多，一页十几个块就累积成几十 px 的系统性下偏——Playground 的盒级 Δ 表在
    // invoice/table 样例上量到的逐级漂移主要来自这里。
    //
    // 兄弟档与父子档（父在这一侧无 border/padding 时首/尾子的边距逃逸到父外）都已实现，
    // 但两者共用同一个「边距组」概念：一组成员（prev 的 EffBottom、本盒的 MarginTop、
    // 逃逸上来的首子 EffTop）取 max 后整体落在组的最外侧。只算兄弟那一档、拿 MarginTop
    // 去坍，等于把逃逸的那截与兄弟那截相加（m3 实测 18+22=40，浏览器 max=22）。

    /// <summary>
    /// 该盒是否参与兄弟间 margin 坍缩。条件按浏览器实测（headless Edge + getBoundingClientRect，
    /// 逐条钉在 MarginCollapsingTests 里）而非凭记忆：只有「不在同一个流里」才阻断——
    /// 行内级盒（inline-block/flex/grid）、浮动、脱流定位。曾被误当作阻断条件、实测照样坍的：
    /// overflow:hidden、display:flow-root、position:relative、带 clear 但没被顶开、中间夹 display:none。
    /// 若把这些也判成不坍，等于在本来正确的地方重新制造 12~30px 的下偏。
    /// </summary>
    internal static bool Collapsible(ComputedStyle s)
        => !ComputedStyle.IsInlineLevel(s.Display) && s.Float == "none";

    /// <summary>
    /// 该盒是否建立独立格式化上下文（CSS 2.1 §9.4.1）：只有 BFC 根才把浮动子算进自动高度。
    /// flex/grid 容器不在此列（其子会被块化、float 失效），故不必列出。
    /// </summary>
    static bool ContainsFloats(ComputedStyle s) =>
        s.ClipsOverflow || s.Display is "flow-root" or "inline-block" or "inline-flex" or "inline-grid"
                            or "table-cell" or "table-caption" or "caption"
        || s.Float != "none" || s.Position is "absolute" or "fixed";

    /// <summary>CSS 相邻 margin 坍缩值：同正取大、同负取更负、异号相加。</summary>
    static float CollapseMargins(float a, float b)
        => a >= 0 && b >= 0 ? Math.Max(a, b) : a <= 0 && b <= 0 ? Math.Min(a, b) : a + b;

    /// <summary>
    /// 本盒相对上一个流内兄弟应「吃掉」的像素数（0 = 不坍缩）。
    /// Measure、Position、分页游标三处必须算同一个值，否则父高、子位、页数会各说各话。
    ///
    /// 比的是 prev 的 EffBottom 与 next 的 <b>EffTop</b>（不是 Style.MarginTop）：next 顶边无
    /// border/padding 时，首子的顶边距会逃逸上来与 next 自己的顶边距并成一整组，浏览器拿这一整组
    /// 去和 prev 的下边距坍缩。只看 MarginTop 会让两组边距叠加（m3 实测 h1↔section 间距 18+22=40，
    /// 浏览器 max(18,22)=22，一处算错整篇 49 个盒齐刷刷下移 18px）。
    ///
    /// EffTop 要等 next 自己测完才落定，但 next 进 Measure 前 ResetEdgeMargins 已把它置成
    /// Style.MarginTop——所以未测盒调用本函数得到的是「只按自身 margin」的下界，可安心用作 y 提示。
    /// </summary>
    internal static float AbsorbMargin(BlockBox? prev, BlockBox next)
        => prev == null || !Collapsible(prev.Style) || !Collapsible(next.Style) ? 0f
         : prev.EffBottom + next.EffTop - CollapseMargins(prev.EffBottom, next.EffTop);

    // ── 父↔首/尾子 margin 坍缩（CSS 2.1 §8.3.1）────────────────────────
    //
    // 只做兄弟坍缩时欠下的另一半：父在这一侧没有 border/padding、且不成 BFC 时，
    // 首子的顶边距与父的顶边距合并成一个、整体挪到父外面（尾子的底边距同理逃出父高）。
    // 实测（headless Edge + getBoundingClientRect）：无 padding 的 body 里 h1 在浏览器是 y=0
    // （那 21px 穿到了 body 外面），引擎此前记 21，整篇文档跟着下移——
    // m3/m5/m7/m8/m4 的级联位移全在这一条上。

    /// <summary>
    /// 复位坍缩相关字段：不参与父子坍缩的盒（flex/grid/table/替换/出流）一律 = 自身 margin，
    /// 参与的和块流在 MeasureBlock 末尾覆写。复位是必须的——分页会对同一棵树反复 Measure，
    /// 留着上一次的 Eff/Edge 会让兄弟间距在第二页起算错；表格/替换盒根本不走 MeasureHeight，
    /// 不在这里初始化就会被 AbsorbMargin 当成 0（实测 display:table 的兄弟间距 70 → 58）。
    /// </summary>
    static void ResetEdgeMargins(BlockBox box)
    {
        box.EffTop = box.Style.MarginTop; box.EffBottom = box.Style.MarginBottom;
        box.EdgeFirst = box.EdgeLast = null;
        box.EdgeCollapseTop = box.EdgeCollapseBottom = false;
    }

    /// <summary>父 p 能否在这一侧与它的首/尾流内块子坍缩 margin。</summary>
    static bool CollapsesAtEdge(BlockBox p, BlockBox c, bool top)
    {
        var s = p.Style;
        // flex/grid 子项与表格单元格各自建立格式化上下文：内部 margin 不与子坍缩
        // （img 样例实测 .col 是 flex 子项，浏览器与引擎都把它的内容高算到 p 的底边距为止）。
        var gp = p.Parent?.Style;
        if (gp != null && (ComputedStyle.IsFlexDisplay(gp.Display) || ComputedStyle.IsGridDisplay(gp.Display)
                           || gp.Display is "table-cell")) return false;
        return (s.Display is "block" or "list-item") && !s.ClipsOverflow && s.Float == "none" && s.Position != "absolute"
            && string.IsNullOrEmpty(s.TextContent) && Collapsible(c.Style)
            && (top ? s.BorderWidthTop + s.PaddingTop : s.BorderWidthBottom + s.PaddingBottom) == 0
            && (top || s.Height.IsAuto);   // 父高被写死时，尾子的底边距留在父内
    }

    /// <summary>本盒在父内容流里实际占掉的纵向高：顶/底 margin 被父吸收、或从孙辈逃逸到本盒外的修正都在这一处。</summary>
    internal static float FlowHeight(BlockBox c)
    {
        var p = c.Parent;
        bool top = p?.EdgeFirst == c && p.EdgeCollapseTop, bot = p?.EdgeLast == c && p.EdgeCollapseBottom;
        return c.TotalHeight
             - (top ? c.Style.MarginTop : 0f) + (top ? 0f : c.EscapeTop)
             // ⚠️ 只扣本盒自己声明的下 margin：EscapeBottom 那道「逃出去的 margin」在本盒
             // 自己的 MeasureBlock 里已经按同一条规则从末子身上扣过一遍（不再含在 ContentHeight 内），
             // 这里再扣一次就是重复扣减——实测 m3 的 section[10] TotalHeight=193.78（m 0/8、border 185.78）、
             // EscapeBottom=8，旧式扣 16 → 贡献 177.78，body 高停在 1817.39，比末子 border 底边矮整 8px
             // （浏览器两者重合于 1824.88）。
             - (bot ? c.Style.MarginBottom : 0f) + (bot ? 0f : c.EscapeBottom);
    }

    /// <summary>本盒边框顶相对父流游标的偏移（顶边距被父吃掉时不再把它往下推）。</summary>
    internal static float FlowInset(BlockBox c)
    {
        var p = c.Parent;
        return p != null && p.EdgeFirst == c && p.EdgeCollapseTop ? 0f : c.Style.MarginTop + c.EscapeTop;
    }

    static float MeasureBlock(BlockBox box, float childContainingHeight, FloatContext? ambient, float contentTopY)
    {
        var s = box.Style;
        if (float.IsNaN(contentTopY)) ambient = null;   // 无全局 y（flex 子树等）：环绕失效，退化为旧行为
        if (box.Atoms != null) return MeasureInline(box, ambient, contentTopY);

        bool hasFloats = false;
        foreach (var c in box.Children)
            if (c.Style.Float != "none" && c.Style.Position == "static") { hasFloats = true; break; }
        var fc = hasFloats ? new FloatContext { Parent = ambient } : ambient;

        float h = 0;
        // 两端的流内子先认出来（只看子序与样式，不看测量结果）：FlowHeight/FlowInset 在下面的循环里就要用，
        // Position 与分页复用同一份结论，三处才不会各说各话。
        foreach (var c in box.Children)
        {
            if (c.Style.Position == "absolute" || c.Style.Float != "none") continue;
            box.EdgeFirst ??= c;
            box.EdgeLast = c;
        }
        box.EdgeCollapseTop = box.EdgeFirst != null && CollapsesAtEdge(box, box.EdgeFirst, top: true);
        box.EdgeCollapseBottom = box.EdgeLast != null && CollapsesAtEdge(box, box.EdgeLast, top: false);
        if (!string.IsNullOrEmpty(s.TextContent))
        {
            using var font = new SKFont(box.Typeface, s.FontSize);
            box.WrappedLines = TextMeasurer.Wrap(s.TextContent, box.ContentWidth, font, s, ambient, contentTopY, out var lefts);
            box.LineLefts = lefts;
            if (s.TextOverflow == "ellipsis" && s.WhiteSpace == "nowrap" && !s.Width.IsAuto)
                box.WrappedLines = TruncateEllipsis(box.WrappedLines, box.ContentWidth, font, s.LetterSpacing);
            h += box.WrappedLines.Length * s.LineHeightPx();

            // text-fit: scale-down —— 高度定死且文本溢出时逐步缩字号重排（下限 6px）
            if (s.TextFit == "scale-down" && !float.IsNaN(childContainingHeight) && childContainingHeight > 0)
                for (int iter = 0; iter < 10 && h > childContainingHeight && s.FontSize > 6f; iter++)
                {
                    s.FontSize = Math.Max(6f, s.FontSize * childContainingHeight / h * 0.98f);
                    using var fitFont = new SKFont(box.Typeface, s.FontSize);
                    box.WrappedLines = TextMeasurer.Wrap(s.TextContent, box.ContentWidth, fitFont, s, ambient, contentTopY, out var fl);
                    box.LineLefts = fl;
                    h = box.WrappedLines.Length * s.LineHeightPx();
                }
        }

        List<(int ChildIndex, float X, float YRel)>? placements = null;
        BlockBox? flowPrev = null;   // 上一个推进了流 cursor 的流内兄弟（坍缩只看它）
        for (int i = 0; i < box.Children.Count; i++)
        {
            var c = box.Children[i];
            var cs = c.Style;
            if (cs.Position == "absolute") { MeasureChild(c, box.ContentWidth, childContainingHeight); continue; }

            // clear：把流 cursor 推到同侧浮动区之下。被 clear 顶开之后两侧不再相邻，坍缩链断掉。
            // ⚠️ 落点是本盒的 **border box 顶**（CSS 2.1 §9.5.2：clearance 补足到浮动底边，自身 margin-top
            // 被吸收而非叠加）。原先按 margin box 比，.clear-both{margin-top:10px} 实测多下坠 10px。
            if (cs.Clear != "none" && fc != null)
            {
                float yAbs = contentTopY + h;
                float borderTop = yAbs + cs.MarginTop;
                float pushed = fc.ClearTo(cs.Clear is "left" or "both", cs.Clear is "right" or "both", borderTop);
                if (pushed > borderTop) { h = pushed - cs.MarginTop - contentTopY; flowPrev = null; }
            }

            if (cs.Float != "none" && cs.Position == "static")
            {
                float childOuterY = contentTopY + h;
                if (cs.Width.IsAuto) MeasureOutOfFlow(c, box.ContentWidth);   // 浮动：shrink-to-fit
                else Measure(c, box.ContentWidth, float.NaN, null, childOuterY);
                var (x, top) = fc!.Place(cs.Float == "left", box.ContentWidth, childOuterY, c.TotalWidth, c.TotalHeight);
                (placements ??= new()).Add((i, x, top - contentTopY));
                continue;   // 浮动不推进流 cursor
            }

            if (cs.Width.IsAuto && cs.Display is "inline-flex" or "inline-grid" or "inline-block")
            { MeasureOutOfFlow(c, box.ContentWidth); h += c.TotalHeight; flowPrev = null; continue; }   // 行内级盒：收缩适配，且打断两侧块盒的坍缩链

            cs.ResolveBoxSizes(box.ContentWidth);   // 先解析，AbsorbMargin 读到的才是已折算的 margin
            // 测量期还拿不到本盒的 EffTop（要等它自己测完），先用「只按自身 margin」的下界估个 y 传给浮动查询；
            // 游标推进必须用测完后的真值，否则首子边距逃逸的父盒会把它和 prev 的下边距叠加而非坍缩。
            float hint = flowPrev == null || !Collapsible(flowPrev.Style) || !Collapsible(cs) ? 0f
                       : flowPrev.EffBottom + cs.MarginTop - CollapseMargins(flowPrev.EffBottom, cs.MarginTop);
            Measure(c, box.ContentWidth, childContainingHeight, fc, contentTopY + h - hint);
            h += FlowHeight(c) - AbsorbMargin(flowPrev, c);
            flowPrev = c;
        }

        // 对外边距：与首/尾子合并后的结果，供父盒（以及父盒的父）继续往上传。
        box.EffTop = box.EdgeCollapseTop ? CollapseMargins(s.MarginTop, box.EdgeFirst!.EffTop) : s.MarginTop;
        box.EffBottom = box.EdgeCollapseBottom ? CollapseMargins(s.MarginBottom, box.EdgeLast!.EffBottom) : s.MarginBottom;

        // BFC 根要「包含浮动」：自动高度的盒一旦成为独立格式化上下文（裁剪溢出 / flow-root / inline-block /
        // 浮动 / 绝对定位），其浮动子就参与撑高。Chrome 实测 .calc-pair{overflow:hidden} 高 88.8 = 两个浮动
        // pane 的底边；引擎原先按流内子算高 → 0，下面整页上抬 75.9px。
        if (hasFloats && ContainsFloats(s) && !float.IsNaN(contentTopY))
            foreach (var g in fc!.Own)
                h = Math.Max(h, g.Bottom - contentTopY);

        if (placements != null)
        {
            box.FloatPlacements = placements.ToArray();
            box.FloatBands = fc!.BandsRel(contentTopY);
        }
        return h;
    }

    /// <summary>inline 块：先测原子盒（inline-block/img），再折行（可环绕 ambient 浮动），行高累加为内容高。</summary>
    static float MeasureInline(BlockBox box, FloatContext? fc, float contentTopY)
    {
        foreach (var a in box.Atoms!)
        {
            if (a.Box == null) continue;
            // inline-block / 行内 img：auto 宽按内容收缩（同出流逻辑），显式宽则正常测
            MeasureOutOfFlow(a.Box, box.ContentWidth);
        }
        box.Lines = InlineLayout.BreakIntoLines(box.Atoms!, box.ContentWidth, fc, contentTopY, box.Style);
        float h = 0;
        foreach (var ln in box.Lines) h += ln.Height;
        return h;
    }

    /// <summary>text-overflow:ellipsis —— 单行超宽时截断并加省略号（按字素边界切，不拆代理对/组合符）。</summary>
    static string[] TruncateEllipsis(string[] lines, float maxWidth, SKFont font, float ls)
    {
        if (lines.Length == 0 || maxWidth <= 0) return lines;
        var line = lines[0];
        if (TextMeasurer.LineWidth(line, font, ls) <= maxWidth) return lines;
        const string ell = "…";
        float ellW = TextMeasurer.LineWidth(ell, font, ls);
        var sb = new System.Text.StringBuilder();
        int keep = 0;
        foreach (var g in TextShaper.Graphemes(line))
        {
            sb.Append(g);
            if (TextMeasurer.LineWidth(sb.ToString(), font, ls) + ellW > maxWidth) break;
            keep = sb.Length;
        }
        return new[] { line[..keep] + ell };
    }

    static readonly bool FlexDbg = System.Environment.GetEnvironmentVariable("PS_FLEXDBG") == "1";

    static float MeasureFlex(BlockBox box, float? myDefH)
    {
        var s = box.Style;
        bool isRow = s.FlexDirection.StartsWith("row");
        bool isReverse = s.FlexDirection.EndsWith("reverse");
        bool isWrap = s.FlexWrap != "nowrap";
        bool isWrapReverse = s.FlexWrap == "wrap-reverse";
        float gap = ResolveGap(s, box.ContentWidth);
        float childCH = myDefH ?? float.NaN;

        // 先测所有子项（含 absolute，供其自身尺寸用），再把出流的排除出 flex 线
        foreach (var c in box.Children) MeasureChild(c, box.ContentWidth, childCH);
        var kids = box.Children.FindAll(c => c.Style.Position != "absolute");
        int n = kids.Count;
        if (n == 0) return 0;

        // 主轴可用（row→内容宽；column→仅确定高才可分配）；交叉轴可用（row→高；column→内容宽）
        float mainAvail = isRow ? box.ContentWidth : (myDefH ?? float.NaN);
        float crossAvail = isRow ? (myDefH ?? float.NaN) : box.ContentWidth;

        float[] baseMain = new float[n];
        float[] decoMain = new float[n];
        for (int i = 0; i < n; i++)
        {
            baseMain[i] = FlexBase(kids[i], isRow, mainAvail, myDefH);
            decoMain[i] = MainDeco(kids[i], isRow);
        }

        // 分行逻辑
        var lines = new List<FlexLine>();
        if (isWrap && !float.IsNaN(mainAvail))
        {
            // 换行模式：按可用主轴空间分组
            var currentLine = new FlexLine();
            float lineUsed = 0;
            for (int i = 0; i < n; i++)
            {
                int idx = isReverse ? n - 1 - i : i;
                float itemMain = baseMain[idx] + decoMain[idx];
                float newUsed = lineUsed + (currentLine.ItemIndices.Count > 0 ? gap : 0) + itemMain;

                if (currentLine.ItemIndices.Count > 0 && newUsed > mainAvail + 0.5f)
                {
                    lines.Add(currentLine);
                    currentLine = new FlexLine();
                    lineUsed = 0;
                }

                currentLine.ItemIndices.Add(idx);
                lineUsed += (currentLine.ItemIndices.Count > 1 ? gap : 0) + itemMain;
            }
            if (currentLine.ItemIndices.Count > 0) lines.Add(currentLine);
            if (isWrapReverse) lines.Reverse();
        }
        else
        {
            // 不换行：所有项在一行
            var line = new FlexLine();
            for (int i = 0; i < n; i++) line.ItemIndices.Add(isReverse ? n - 1 - i : i);
            lines.Add(line);
        }

        // 对每行进行 flex grow/shrink 分配
        foreach (var line in lines)
        {
            float[] lineBase = line.ItemIndices.Select(i => baseMain[i]).ToArray();
            float[] lineDeco = line.ItemIndices.Select(i => decoMain[i]).ToArray();
            float[] lineFinal = (float[])lineBase.Clone();

            float lineMainAvail = isRow ? box.ContentWidth : (myDefH ?? float.NaN);
            if (!float.IsNaN(lineMainAvail))
            {
                float used = gap * (line.ItemIndices.Count - 1);
                for (int i = 0; i < line.ItemIndices.Count; i++) used += lineBase[i] + lineDeco[i];
                float free = lineMainAvail - used;
                if (FlexDbg) Console.Error.WriteLine($"[flex] {s.Tag} avail={lineMainAvail} used={used} free={free} base=[{string.Join(",", lineBase)}]");

                if (free > 0.5f)
                {
                    float totalGrow = 0;
                    for (int i = 0; i < line.ItemIndices.Count; i++)
                        totalGrow += kids[line.ItemIndices[i]].Style.FlexGrow;
                    if (totalGrow > 0)
                        for (int i = 0; i < line.ItemIndices.Count; i++)
                            lineFinal[i] = lineBase[i] + free * kids[line.ItemIndices[i]].Style.FlexGrow / totalGrow;
                }
                else if (free < -0.5f)
                {
                    float sw = 0;
                    for (int i = 0; i < line.ItemIndices.Count; i++)
                        sw += kids[line.ItemIndices[i]].Style.FlexShrink * Math.Max(1, lineBase[i]);
                    if (sw > 0)
                        for (int i = 0; i < line.ItemIndices.Count; i++)
                            lineFinal[i] = Math.Max(0, lineBase[i] + free * kids[line.ItemIndices[i]].Style.FlexShrink * Math.Max(1, lineBase[i]) / sw);
                }
            }

            // 夹到 min/max
            for (int i = 0; i < line.ItemIndices.Count; i++)
            {
                var ms = kids[line.ItemIndices[i]].Style;
                lineFinal[i] = isRow
                    ? ClampSize(lineFinal[i], ms.MinWidth, ms.MaxWidth, lineMainAvail)
                    : ClampSize(lineFinal[i], ms.MinHeight, ms.MaxHeight, myDefH ?? float.NaN);
            }

            // 测量交叉轴尺寸
            bool stretchDefault = s.AlignItems == "stretch";
            float lineCrossMax = 0;
            for (int i = 0; i < line.ItemIndices.Count; i++)
            {
                var c = kids[line.ItemIndices[i]];
                if (isRow)
                {
                    MeasureWithContentWidth(c, lineFinal[i], childCH);
                    bool stretch = c.Style.AlignSelf == "stretch" || (c.Style.AlignSelf == "auto" && stretchDefault);
                    if (stretch && !float.IsNaN(crossAvail) && lines.Count == 1 && c.Style.Height.IsAuto)
                        c.ContentHeight = Math.Max(0, crossAvail - MainDeco(c, false));
                    lineCrossMax = Math.Max(lineCrossMax, c.TotalHeight);
                }
                else
                {
                    // column 的交叉轴是宽：显式宽 > stretch 撑满 > 内容收缩。
                    // 旧代码无条件把子项宽拉成容器宽，既吞掉 width:100px，也让 align-items:center 无从居中。
                    bool stretch = c.Style.AlignSelf == "stretch" || (c.Style.AlignSelf == "auto" && stretchDefault);
                    if (!stretch && c.Style.Width.IsAuto)
                    {
                        float decoW = MainDeco(c, isRow: true);   // isRow 分支即"水平方向装饰"：左右 margin/border/padding
                        MeasureWithContentWidth(c, Math.Max(0, Math.Min(IntrinsicContentWidth(c), box.ContentWidth - decoW)), childCH);
                    }
                    else MeasureWithContentWidth(c, ResolveWidth(c.Style.Width, box.ContentWidth, c.Style), childCH);
                    c.ContentHeight = lineFinal[i];
                    lineCrossMax = Math.Max(lineCrossMax, c.TotalWidth);
                }
            }

            // align-items:stretch 且行容器交叉轴为 auto 高（crossAvail=NaN）：上面 :1052 那条只在
            // 容器有确定高度时才拉伸，漏掉了更常见的「auto 高行容器」。此处补第二趟——把未显式定高的
            // stretch 子项纵向撑到该行最高子项（lineCrossMax），margin-box 齐平。flex 的 .n-left、
            // pct 的 .col-b 都是这样被浏览器拉到同行最高，而引擎此前停在内容高（差 43/19.5px）。
            // 只增不减（stretch 绝不把比线高的子项压短），故 lineCrossMax 本身不受影响。
            if (isRow && float.IsNaN(crossAvail))
                for (int i = 0; i < line.ItemIndices.Count; i++)
                {
                    var c = kids[line.ItemIndices[i]];
                    bool stretch = c.Style.AlignSelf == "stretch" || (c.Style.AlignSelf == "auto" && stretchDefault);
                    if (!stretch || !c.Style.Height.IsAuto) continue;
                    float deco = MainDeco(c, false);
                    float target = ClampSize(lineCrossMax - deco, c.Style.MinHeight, c.Style.MaxHeight, float.NaN, deco);
                    if (target > c.ContentHeight) c.ContentHeight = target;
                }

            // 线主轴尺寸 = 分配后的内容尺寸 + 各子项主轴装饰（margin/border/padding），
            // 否则 column 容器自然高漏掉子项 margin → 内容溢出盒高、网格行重叠。
            line.MainSize = gap * (line.ItemIndices.Count - 1) + lineFinal.Sum() + lineDeco.Sum();
            line.CrossSize = lineCrossMax;
        }

        box.FlexLines = lines;

        // 返回交叉轴总尺寸
        if (isRow)
        {
            float totalCross = gap * (lines.Count - 1);
            foreach (var line in lines) totalCross += line.CrossSize;
            return totalCross;
        }
        else
        {
            float totalMain = gap * (lines.Count - 1);
            foreach (var line in lines) totalMain += line.MainSize;
            return totalMain;
        }
    }

    /// <summary>子项主轴 base（内容尺寸）：flex-basis（%相对主轴可用；border-box 时含自身 padding/border）
    /// > 显式 width/height（同样按 box-sizing 决定声明值含不含装饰）> 内在尺寸。</summary>
    static float FlexBase(BlockBox c, bool isRow, float mainAvail, float? containerDefH)
    {
        var s = c.Style;
        if (!s.FlexBasis.IsAuto)
        {
            float r = s.FlexBasis.Resolve(mainAvail);
            if (!float.IsNaN(r))
                return s.BoxSizing == "border-box" ? r - MainBorderPadding(c, isRow) : r;
        }
        float dim = isRow ? s.Width.Resolve(mainAvail) : s.Height.Resolve(containerDefH ?? float.NaN);
        if (!float.IsNaN(dim))
            return s.BoxSizing == "border-box" ? Math.Max(0, dim - MainBorderPadding(c, isRow)) : dim;
        return isRow ? IntrinsicContentWidth(c) : c.ContentHeight;
    }

    /// <summary>gap 百分比按容器内容宽解析；auto/无法解析 → 0。</summary>
    static float ResolveGap(ComputedStyle s, float contentWidth)
    {
        var g = s.Gap.Resolve(contentWidth);
        return float.IsNaN(g) || g < 0 ? 0 : g;
    }

    static float MainDeco(BlockBox c, bool isRow) => isRow
        ? c.Style.MarginLeft + c.Style.MarginRight + c.Style.BorderWidthLeft + c.Style.BorderWidthRight + c.Style.PaddingLeft + c.Style.PaddingRight
        : c.Style.MarginTop + c.Style.MarginBottom + c.Style.BorderWidthTop + c.Style.BorderWidthBottom + c.Style.PaddingTop + c.Style.PaddingBottom;

    /// <summary>主轴上的 padding+border（不含 margin）。box-sizing:border-box 的声明尺寸换内容尺寸用它——
    /// margin 由 <see cref="MainDeco"/> 在分配外单独加，扣进尺寸里等于扣两遍。</summary>
    static float MainBorderPadding(BlockBox c, bool isRow) => isRow
        ? c.Style.BorderWidthLeft + c.Style.BorderWidthRight + c.Style.PaddingLeft + c.Style.PaddingRight
        : c.Style.BorderWidthTop + c.Style.BorderWidthBottom + c.Style.PaddingTop + c.Style.PaddingBottom;

    /// <summary>内在宽的两个口径：max-content（不折行）与 min-content（每个断行机会都断）。</summary>
    enum Intrinsic { Max, Min }

    /// <summary>max-content 宽：完全不折行时需要的宽。</summary>
    static float IntrinsicContentWidth(BlockBox c) => IntrinsicWidth(c, Intrinsic.Max);

    /// <summary>
    /// min-content 宽：在每一个断行机会都断开后最宽的那一行。表格 auto 列在可用宽不够时
    /// 浏览器不是把各列按 max-content 等比压，而是在 [min,max] 之间按同一因子收缩
    /// （CSS 2.1 §17.5.2.2 / Blink AutoTableLayout::ComputeColumnWidths）——等比压会把「一个长词」
    /// 的列压到折行、把「一堆短词」的列压得比它还矮。
    /// </summary>
    static float MinContentWidth(BlockBox c) => IntrinsicWidth(c, Intrinsic.Min);

    /// <summary>
    /// 内在宽：min 与 max 只差在**叶子**（文字/原子序列怎么量），容器层的组合规则一模一样
    /// （沿主轴并排求和、块级子项取最宽、固定宽/替换元素照抄）。因此共用一趟遍历、
    /// 分支只写一处——两棵平行的树迟早在一侧补漏时分叉。
    /// </summary>
    static float IntrinsicWidth(BlockBox c, Intrinsic kind)
    {
        var s = c.Style;
        if (s.Width.IsFixed) return s.Width.Absolute;
        if (s.FlexBasis.IsFixed) return s.FlexBasis.Absolute;
        if (c.IsReplaced) return c.NaturalW;
        // inline FC：max = 各原子内在宽之和（不折行）；min = 压到零宽走一遍真实折行取最宽行
        if (c.Atoms != null)
        {
            if (kind == Intrinsic.Min) return InlineLayout.MinContentWidth(c.Atoms);
            // max-content 只对「软换行机会」不折行求和；`<br>` 是硬换行，须在那里断段、段间取 max
            // （沿用「换行只由软断点决定」把 IsBreak 直接 continue 掉，会把 <br> 两侧行宽相加：
            //  pricing-foot 的 .copy 实测 299.4 + 194.0 = 493.4 撑爆整列）。
            float w = 0, seg = 0;
            for (int ai = 0; ai < c.Atoms.Count; ai++)
            {
                var a = c.Atoms[ai];
                if (a.IsBreak) { w = Math.Max(w, seg); seg = 0; continue; }
                // 原子盒一律递归取内在宽：替换元素在里面先认 CSS 显式宽（width:120px 的图就是 120），
                // 没有显式宽才回退自然宽。直接读 NaturalW 会把缩放图撑回原尺寸，整条 flex 行跟着错位。
                if (a.Box != null) seg += MainDeco(a.Box, true) + IntrinsicWidth(a.Box, kind);
                else if (!string.IsNullOrEmpty(a.Text))
                {
                    using var f = new SKFont(FontManager.Get(a.Style.FontFamily, a.Style.FontWeight), a.Style.FontSize);
                    // 行内元素自己的横向 padding/border 是它的盒的一部分，折行（BreakRaw）已按此推进，
                    // 内在宽漏掉就会与真实折行分叉。cbW 传 0：此处是 max-content 通道，无包含块宽可依，
                    // 而 inline padding 的百分比本就相对包含块，留给 BreakRaw 那条路折算。
                    var (ipl, ipr) = a.HDeco(0);
                    // seg>0：前面已有兄弟片段，本片段段首那个空格不在行首，浏览器不会吞（m8 的
                    // li::after{content:" " counter(tag)} 被吞掉后徽标少 4.4px，挤成两行、整卡高 55 vs 29.5）。
                    // 尾空格同理：max-content 是一条不断行的直线，只有**行末**那一个尾空格会被吞，
                    // 所以除序列末尾之外一律保留（「含 <span>高亮</span>」中间的那个空格）。
                    bool atLineEnd = true;
                    for (int j = ai + 1; j < c.Atoms.Count; j++)
                    {
                        var n = c.Atoms[j];
                        if (n.IsBreak) { atLineEnd = true; break; }
                        if (n.Box != null || !string.IsNullOrEmpty(n.Text)) { atLineEnd = false; break; }
                    }
                    seg += TextMeasurer.NaturalWidth(a.Text!, f, a.Style,
                        keepLeadingSpace: seg > 0, keepTrailingSpace: !atLineEnd) + ipl + ipr;
                }
            }
            return Math.Max(w, seg);
        }
        // flex/grid 容器里直接文本会被包成匿名子项（见 ToBoxTree 的 IsFlexDisplay 分支），与元素子项并排占主轴
        // → 内在宽走 Children 那条路即可。只有在**没有子盒**承载文字时（content:"…" 伪元素盒）才须另加 TextOf，
        // 否则同一段文字计两遍：login-page 的 .suffix/.prefix/.oauth 各凭空宽出一个文字宽（最坏 +102px）。
        // （试过无条件删掉这里的 TextOf 项：pricing-top 均值 20.6→42.8，.menu 的链接直接塌成 19.1 宽并折行
        //  ——伪元素盒的文字只从这里进，Children 那条路没有它，勿再无条件去掉。）
        if (ComputedStyle.IsFlexDisplay(s.Display) || ComputedStyle.IsGridDisplay(s.Display))
        {
            // 只有沿主轴并排的子项才「求和」：row flex / grid 的 max-content 宽 = 各项相加；
            // column flex 子项纵向堆叠，内在宽仍是各项之最——求和会把每一列都撑成整行之和。
            bool alongMain = ComputedStyle.IsGridDisplay(s.Display) || s.FlexDirection.StartsWith("row");
            float both = 0;
            if (!string.IsNullOrEmpty(s.TextContent) && c.Children.Count == 0)
                both += TextOf(s.TextContent!, c, s, kind);
            int items = 0;
            foreach (var g in c.Children)
            {
                float w = IntrinsicWidth(g, kind) + MainDeco(g, true);
                both = alongMain ? both + w : Math.Max(both, w);
                items++;
            }
            // 主轴 gap 也要算进内在宽：max-content 是一条直线，n 个并排项之间有 n-1 段间距。
            // 漏算会让上层 flex 容器以为这一项很窄，把宽度分给它时压到不足内容宽，
            // 整行项目被迫收缩换行（pricing-top 的 .menu 内在 312 而实际需 482，六个链接全塌，dx 133px）。
            if (alongMain && items > 1 && ComputedStyle.IsFlexDisplay(s.Display))
                both += ResolveGap(s, 0) * (items - 1);
            return both;
        }
        if (!string.IsNullOrEmpty(s.TextContent)) return TextOf(s.TextContent!, c, s, kind);
        // 块级子项各占一行：收缩宽（max-content）= 最宽的那个，不是全部相加。
        // 相加会让「图 + 说明文字」这类 flex 项凭空宽出一截，后面的兄弟被整条推歪（Playground img 样例 216px）。
        float ww = 0;
        foreach (var g in c.Children) ww = Math.Max(ww, IntrinsicWidth(g, kind) + MainDeco(g, true));
        return ww;
    }

    /// <summary>块自身直接文本的内在宽：max = 整串自然宽；min = 最宽的可断行段。</summary>
    static float TextOf(string text, BlockBox c, ComputedStyle s, Intrinsic kind)
    {
        if (kind == Intrinsic.Min) return InlineLayout.MinContentOfText(text, s);
        using var font = new SKFont(c.Typeface, s.FontSize);
        return TextMeasurer.NaturalWidth(text, font, s);
    }

    // ── Grid ────────────────────────────────────────────────

    /// <summary>一条网格轨道：Fr&gt;0 为弹性轨道；Min=NaN 为 auto（内容定尺）；否则固定/minmax 下界。</summary>
    sealed class GridTrack
    {
        public float Min = float.NaN, Max = float.NaN;
        public float Fr;
    }

    /// <summary>
    /// Grid 测量：解析轨道（固定/%/auto/fr/minmax/repeat）→ 放置子项（显式或自动，含占用避让）→
    /// 列定尺（auto 按内容、多跨均摊、fr 分配剩余）→ 按格宽测子项 → 行定尺 → 单跨项纵向 stretch。
    /// </summary>
    static float MeasureGrid(BlockBox box, float? myDefH)
    {
        var s = box.Style;
        float colGap = Resolve(s.ColumnGap, box.ContentWidth);
        float rowGap = Resolve(s.RowGap, box.ContentWidth);

        var colTracks = ParseGridTracks(s.GridTemplateColumns, box.ContentWidth, colGap);
        var rowAvail = myDefH ?? float.NaN;
        var rowTracks = ParseGridTracks(s.GridTemplateRows, rowAvail, rowGap);
        // 容器高不确定时 fr 行无剩余空间可分：退化为 auto
        if (float.IsNaN(rowAvail))
            foreach (var t in rowTracks)
                if (t.Fr > 0) { t.Fr = 0; t.Min = float.NaN; }

        if (colTracks.Count == 0)
            colTracks.Add(new GridTrack { Min = box.ContentWidth, Max = box.ContentWidth }); // 无模板：单列占满（CSS 语义）
        if (rowTracks.Count == 0) rowTracks.Add(new GridTrack());
        // 模板显式写出的轨道条数：浏览器里空轨道照样占位（grid-template-rows:120px×4 只放 3 项仍高 510），
        // 只有自动放置撑出来的隐式轨道才可裁掉——裁错会把整页高度凭空缩掉一截（_gsel 实测差 260px）。
        int explicitCols = colTracks.Count, explicitRows = rowTracks.Count;

        // ── 放置 ──
        var placements = new List<(int childIdx, int col, int row, int colSpan, int rowSpan)>();
        var occupied = new HashSet<(int col, int row)>();
        int autoCol = 0, autoRow = 0;

        for (int i = 0; i < box.Children.Count; i++)
        {
            var cs = box.Children[i].Style;
            if (cs.Position == "absolute") { MeasureOutOfFlow(box.Children[i], box.ContentWidth); continue; }

            // 跨线数由 ParseGridPlacement 按线号算好（end - start）；这里不能再减一次起始线，
            // 否则 span 恒多 1：`grid-column:2/7` 在 6 列网格里拿 col=1、span=6，下面的自动放置
            // 扫描永远凑不出 c==col 且放得下 → MeasureGrid 死循环（fontvs 卡死）。
            int colSpan = Math.Max(1, cs.GridColumnSpan), rowSpan = Math.Max(1, cs.GridRowSpan);
            int col = cs.GridColumnStart > 0 ? cs.GridColumnStart - 1 : -1;
            int row = cs.GridRowStart > 0 ? cs.GridRowStart - 1 : -1;

            if (col < 0 || row < 0)
            {
                // 自动放置（CSS §8.5 简化）：定轴锁死、只扫不定的那一轴，隐式轨道交给下面的补轨兜住。
                // 旧写法把两轴混在一个扫描里，遇到「定轴 + 所跨轨道超出模板」就永远回不到定轴值上——
                // 那个形状本来只由 span 多算 1 触发，但 `grid-column:5/8` 这类真·超界写法一样会死循环。
                int r = autoRow, c = autoCol;
                if (col >= 0)
                {
                    c = col;
                    while (Intersects(occupied, c, r, colSpan, rowSpan)) r++;
                }
                else if (row >= 0)
                {
                    r = row;
                    while (Intersects(occupied, c, r, colSpan, rowSpan)) c++;
                }
                else
                {
                    while (true)
                    {
                        if (c + colSpan > colTracks.Count) { c = 0; r++; }
                        if (!Intersects(occupied, c, r, colSpan, rowSpan)) break;
                        c++;
                    }
                }
                col = c; row = r;
            }

            while (col + colSpan > colTracks.Count) colTracks.Add(new GridTrack());
            while (row + rowSpan > rowTracks.Count) rowTracks.Add(new GridTrack());
            for (int dc = 0; dc < colSpan; dc++)
                for (int dr = 0; dr < rowSpan; dr++)
                    occupied.Add((col + dc, row + dr));
            placements.Add((i, col, row, colSpan, rowSpan));

            autoCol = col + colSpan;
            if (autoCol >= colTracks.Count) { autoCol = 0; autoRow = row + 1; }
        }

        // 裁掉尾部无子项的隐式轨道（显式模板轨道一律保留）
        TrimTracks(colTracks, placements, p => (p.col, p.colSpan), explicitCols);
        TrimTracks(rowTracks, placements, p => (p.row, p.rowSpan), explicitRows);

        // ── 列定尺 ──
        float[] colW = SizeGridTracks(colTracks, box.ContentWidth, colGap,
            placements.Select(p => (p.col, p.colSpan, MeasureNeedWidth(box.Children[p.childIdx], box.ContentWidth),
                                    MeasureNeedMinWidth(box.Children[p.childIdx]))).ToArray());

        // ── 按格宽复测子项 ──
        // 自对齐先裁决一次：非 stretch 的那一轴不铺满轨道，测量就得按 fit-content 收，
        // 定位期再拿同一份结果算偏移（grid 默认 normal ≡ 两轴都 stretch）。
        var selfCross = new string[placements.Count];
        var selfMain = new string[placements.Count];
        for (int i = 0; i < placements.Count; i++)
        {
            var child = box.Children[placements[i].childIdx];
            selfCross[i] = GridSelfAlign(child.Style.AlignSelf, s.AlignItems);
            selfMain[i] = GridSelfAlign(child.Style.JustifySelf, s.JustifyItems);
        }
        for (int i = 0; i < placements.Count; i++)
        {
            var p = placements[i];
            var child = box.Children[p.childIdx];
            float cellW = SumSpan(colW, p.col, p.colSpan, colGap);
            child.Style.ResolveBoxSizes(cellW);
            // 轨道宽是子项 margin box 的可用区；折算成内容宽再测（auto=拉伸减去装饰，固定宽用声明值）
            float w = ResolveWidth(child.Style.Width, cellW, child.Style);
            if (selfMain[i] != "stretch" && child.Style.Width.IsAuto)
            {
                float decoW = child.Style.MarginLeft + child.Style.MarginRight
                    + child.Style.BorderWidthLeft + child.Style.BorderWidthRight
                    + child.Style.PaddingLeft + child.Style.PaddingRight;
                w = Math.Min(IntrinsicContentWidth(child), Math.Max(0, cellW - decoW));
            }
            MeasureWithContentWidth(child, w, myDefH ?? float.NaN);
        }

        // ── 行定尺 ──
        float[] rowH = SizeGridTracks(rowTracks, rowAvail, rowGap,
            // 行轴自动最小要的是子项 min-content **高**（本引擎未计算）→ 地板传 0 = 维持旧行为。
            placements.Select(p => (p.row, p.rowSpan, box.Children[p.childIdx].TotalHeight, 0f)).ToArray());

        // 块轴的容器确定高：height 优先，其次 min-height 折算到 content 盒（% 无基准则忽略）。
        float vbDeco = s.BoxSizing == "border-box"
            ? s.PaddingTop + s.PaddingBottom + s.BorderWidthTop + s.BorderWidthBottom : 0;
        float minHRaw = s.MinHeight.Resolve(float.NaN);
        float blockTarget = Math.Max(myDefH ?? 0, float.IsNaN(minHRaw) ? 0 : Math.Max(0, minHRaw - vbDeco));
        float rowsSum = 0;
        for (int r = 0; r < rowH.Length; r++) rowsSum += rowH[r] + (r > 0 ? rowGap : 0);
        float rowExtra = blockTarget - rowsSum;
        float rowLead = 0;
        if (rowExtra > 0.5f)
        {
            // align-content 未写/normal ≡ stretch：富余空间平分给 auto 行轨道（固定/fr 轨不涨）。
            // 不先把轨道撑起来，行内的 align-items:center 就没有可居中的余量（pricing-mid 的 .r
            // {min-height:105px;align-items:center} 实测整行格子差 41.5px）。
            var autoRows = new List<int>();
            for (int r = 0; r < rowH.Length; r++)
                if (rowTracks[r].Fr == 0 && float.IsNaN(rowTracks[r].Min) && float.IsNaN(rowTracks[r].Max))
                    autoRows.Add(r);
            if (autoRows.Count > 0 && s.AlignContent is "" or "normal" or "stretch")
                foreach (int r in autoRows) rowH[r] += rowExtra / autoRows.Count;
            else
                rowLead = s.AlignContent switch { "center" => rowExtra / 2, "end" or "flex-end" => rowExtra, _ => 0 };
        }

        // 未显式定高且交叉轴 stretch 的子项纵向拉伸到所跨行总高（grid 默认 align-self:stretch）；
        // start/center/end 保自然高，偏移留给 PositionGrid。
        for (int i = 0; i < placements.Count; i++)
        {
            var p = placements[i];
            var child = box.Children[p.childIdx];
            if (selfCross[i] != "stretch" || !child.Style.Height.IsAuto) continue;
            float decoH = child.Style.MarginTop + child.Style.MarginBottom
                + child.Style.BorderWidthTop + child.Style.BorderWidthBottom
                + child.Style.PaddingTop + child.Style.PaddingBottom;
            child.ContentHeight = Math.Max(child.ContentHeight, Math.Max(0, SumSpan(rowH, p.row, p.rowSpan, rowGap) - decoH));
        }

        box.GridInfo = new GridInfo
        {
            ColWidths = colW,
            RowHeights = rowH,
            ColGap = colGap,
            RowGap = rowGap,
            RowLead = rowExtra > 0.5f ? rowLead : 0,
            Placements = placements,
            SelfCross = selfCross,
            SelfMain = selfMain
        };

        float totalH = 0;
        for (int r = 0; r < rowH.Length; r++) totalH += rowH[r] + (r > 0 ? rowGap : 0);
        return totalH;
    }

    static float Resolve(CssSize v, float base_)
    {
        var r = v.Resolve(base_);
        return float.IsNaN(r) || r < 0 ? 0 : r;
    }

    /// <summary>列轴内容需求 = max-content 内在宽（auto 宽直接测会填满容器，不能用 TotalWidth）。</summary>
    static float MeasureNeedWidth(BlockBox child, float avail)
    {
        var s = child.Style;
        float w = s.Width.IsFixed ? s.Width.Absolute : IntrinsicContentWidth(child);
        return w + s.MarginLeft + s.MarginRight + s.BorderWidthLeft + s.BorderWidthRight
            + s.PaddingLeft + s.PaddingRight;
    }

    /// <summary>
    /// 网格子项的「自动最小尺寸」贡献（CSS Grid §12.6.2 + Sizing §9.4.1）：min-width:auto 且
    /// overflow 为 visible 时，轨道底不得小于它的 min-content。两个豁免缺一不可：写了具体
    /// min-width 用它；overflow 非 visible（裁剪容器可合法压到内容以下）则无内容地板——
    /// pricing 的 .plan-card{overflow:hidden} 就是靠这条不立底。
    /// </summary>
    static float MeasureNeedMinWidth(BlockBox child)
    {
        var s = child.Style;
        float deco = s.MarginLeft + s.MarginRight + s.BorderWidthLeft + s.BorderWidthRight
            + s.PaddingLeft + s.PaddingRight;
        if (!s.MinWidth.IsAuto)
        {
            float m = s.MinWidth.Resolve(float.NaN);
            return float.IsNaN(m) ? deco : Math.Max(0, m) + deco;
        }
        if (s.ClipsOverflow) return 0;
        return MinContentWidth(child) + deco;
    }

    static bool Intersects(HashSet<(int, int)> occ, int col, int row, int colSpan, int rowSpan)
    {
        for (int dc = 0; dc < colSpan; dc++)
            for (int dr = 0; dr < rowSpan; dr++)
                if (occ.Contains((col + dc, row + dr))) return true;
        return false;
    }

    static void TrimTracks<T>(List<GridTrack> tracks, List<T> placements, Func<T, (int start, int span)> start, int keep)
    {
        if (placements.Count == 0 || tracks.Count <= 1) return;
        int maxUsed = Math.Max(keep, placements.Max(p => { var (s0, sp) = start(p); return s0 + sp; }));
        while (tracks.Count > maxUsed && tracks.Count > 1) tracks.RemoveAt(tracks.Count - 1);
    }

    static float SumSpan(float[] sizes, int start, int span, float gap)
    {
        float v = 0;
        for (int i = start; i < start + span && i < sizes.Length; i++) v += sizes[i] + (i > start ? gap : 0);
        return v;
    }

    /// <summary>轨道定尺：固定值直接用；auto 按单跨内容取大；多跨不足均摊；fr 按剩余空间比例分配。</summary>
    static float[] SizeGridTracks(List<GridTrack> tracks, float avail, float gap, (int start, int span, float need, float minNeed)[] items)
    {
        int n = tracks.Count;
        float[] size = new float[n];
        for (int t = 0; t < n; t++)
        {
            var tr = tracks[t];
            if (tr.Fr > 0) continue;                                  // fr 轨道先占 0，后面分剩余
            if (!float.IsNaN(tr.Min)) size[t] = Math.Max(0, tr.Min);  // 固定 / minmax 下界
        }
        // auto 轨道：单跨内容取大
        for (int t = 0; t < n; t++)
        {
            if (tracks[t].Fr > 0 || !float.IsNaN(tracks[t].Min)) continue;
            foreach (var it in items.Where(i => i.start == t && i.span == 1))
                size[t] = Math.Max(size[t], it.need);
        }
        // 多跨不足：均摊给跨内轨道。⚠️ 只摊给**非 fr** 轨道——fr 轨道下面会被 free*Fr/sumFr 整体覆写，
        // 而 used 又把这笔临时值算进已占用，于是 free 凭空少掉一份、fr 轨集体缩水（m2 的
        // grid-template-columns:repeat(3,1fr) 实测 309/327 vs 浏览器 340，整幅右缘空转 39~93px）。
        foreach (var it in items.Where(i => i.span > 1))
        {
            float sum = SumSpan(size, it.start, it.span, gap);
            float shortAmt = it.need - sum;
            if (shortAmt <= 0.5f) continue;
            // 只数能留下这笔钱的下界轨道；全是 fr 时整笔作废（剩余空间天然会分给它们）
            int fixedN = 0;
            for (int t = it.start; t < it.start + it.span && t < n; t++)
                if (tracks[t].Fr == 0) fixedN++;
            if (fixedN == 0) continue;
            for (int t = it.start; t < it.start + it.span && t < n; t++)
                if (tracks[t].Fr == 0) size[t] += shortAmt / fixedN;
        }
        // fr 轨道：§12.6.2 自动最小地板 + §12.7.1「find the size of an fr」。
        // 尺寸 = fr × 假想 fr 值，其中假想值 = (容器 − 缝 − 非 fr 定尺 − 已冻结 fr 轨的地板) / 在算 fr 因子和；
        // 某轨地板高于它该分的份额时**冻结在地板上并剔除出分配**后重算（不是把地板再加回去——
        // 写成 size = 地板 + 份额·Fr/ΣFr 会让每条轨重复计入约 0.75×地板，实测把 pricing 整页撑溢出 88.9px）。
        // 地板全高于份额时 free 为负：轨道停在地板，网格照浏览器那样整体溢出（login-page 实测轨道
        // 640px/372px，640+372+48 = 1060 > 内容盒 1052，浏览器就是故意溢出 8px）。
        float sumFr = 0;
        foreach (var tr in tracks) sumFr += tr.Fr;
        if (sumFr > 0 && !float.IsNaN(avail))
        {
            var floor = new float[n];
            var frozen = new bool[n];
            for (int t = 0; t < n; t++)
            {
                if (tracks[t].Fr <= 0) continue;
                foreach (var it in items.Where(i => i.start == t && i.span == 1))
                    floor[t] = Math.Max(floor[t], it.minNeed);
            }
            for (int guard = 0; guard < n + 1; guard++)
            {
                float leftover = avail - gap * Math.Max(0, n - 1), sumFrA = 0;
                for (int t = 0; t < n; t++)
                {
                    if (tracks[t].Fr <= 0) leftover -= size[t];
                    else if (frozen[t]) leftover -= floor[t];
                    else sumFrA += tracks[t].Fr;
                }
                if (sumFrA <= 0)
                {
                    for (int t = 0; t < n; t++) if (tracks[t].Fr > 0) size[t] = floor[t];
                    break;
                }
                float hyp = leftover > 0 ? leftover / sumFrA : 0;
                bool kicked = false;
                for (int t = 0; t < n; t++)
                    if (tracks[t].Fr > 0 && !frozen[t] && floor[t] > hyp * tracks[t].Fr + 1e-3f)
                    { frozen[t] = true; kicked = true; }
                if (kicked) continue;
                for (int t = 0; t < n; t++)
                    if (tracks[t].Fr > 0) size[t] = frozen[t] ? floor[t] : hyp * tracks[t].Fr;
                break;
            }
        }
        return size;
    }

    /// <summary>解析 grid-template-* 值为轨道列表：auto | 长度/% | Nfr | minmax(a,b) | repeat(n|auto-fill|auto-fit, t...)</summary>
    static List<GridTrack> ParseGridTracks(string? def, float availSize, float gap)
    {
        var tracks = new List<GridTrack>();
        if (string.IsNullOrWhiteSpace(def)) return tracks;
        foreach (var tok in SplitTopLevel(def!))
            ExpandTrackToken(tok, availSize, gap, tracks);
        return tracks;
    }

    static void ExpandTrackToken(string tok, float availSize, float gap, List<GridTrack> outTracks)
    {
        tok = tok.Trim();
        if (tok.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase) && tok.EndsWith(")"))
        {
            var inside = tok[7..^1];
            var parts = SplitTopLevel(inside, ',');
            if (parts.Count < 2) return;
            int count;
            var countTok = parts[0].Trim().ToLowerInvariant();
            var inner = new List<GridTrack>();
            foreach (var t in SplitTopLevel(parts[1])) ExpandTrackToken(t, availSize, gap, inner);
            if (inner.Count == 0) return;
            if (countTok is "auto-fill" or "auto-fit")
            {
                float firstMin = inner[0].Fr == 0 && !float.IsNaN(inner[0].Min) ? inner[0].Min : 0;
                float stride = firstMin + gap;
                count = !float.IsNaN(availSize) && stride > 0
                    ? Math.Max(1, (int)Math.Floor((availSize + gap) / stride))
                    : 1;
                if (countTok == "auto-fit") { /* 空轨道折叠暂不实现，与 auto-fill 同 */ }
            }
            else if (!int.TryParse(countTok, out count) || count < 1) return;
            for (int r = 0; r < count; r++) outTracks.AddRange(inner.Select(t => new GridTrack { Min = t.Min, Max = t.Max, Fr = t.Fr }));
            return;
        }
        if (tok == "auto") { outTracks.Add(new GridTrack()); return; }
        if (tok.StartsWith("minmax(", StringComparison.OrdinalIgnoreCase) && tok.EndsWith(")"))
        {
            var args = SplitTopLevel(tok[7..^1], ',');
            if (args.Count != 2) return;
            var lo = ParseTrackSize(args[0], availSize);
            var hi = ParseTrackSize(args[1], availSize);
            outTracks.Add(new GridTrack { Min = lo.px, Max = hi.px, Fr = hi.fr });
            return;
        }
        var v = ParseTrackSize(tok, availSize);
        outTracks.Add(v.fr > 0 ? new GridTrack { Fr = v.fr } : new GridTrack { Min = v.px, Max = v.px });
    }

    /// <summary>单轨道尺寸：auto→(NaN,0)；Nfr→(NaN,fr)；长度/%→(px,0)；垃圾→(NaN,0) 视作 auto。</summary>
    static (float px, float fr) ParseTrackSize(string tok, float availSize)
    {
        tok = tok.Trim();
        if (tok.Length == 0 || tok.Equals("auto", StringComparison.OrdinalIgnoreCase)) return (float.NaN, 0);
        if (tok.EndsWith("fr", StringComparison.OrdinalIgnoreCase))
        {
            float f = ParseNum(tok[..^2]);
            return f > 0 ? (float.NaN, f) : (float.NaN, 0);
        }
        if (tok.Contains("calc(", StringComparison.OrdinalIgnoreCase))
        {
            // calc 轨道：折成线性式后按容器宽解析（% 相对 grid 内容宽）
            float r = CssValueParser.ParseSize(tok, availSize).Resolve(availSize);
            return !float.IsNaN(r) && r > 0 ? (r, 0) : (float.NaN, 0);
        }
        float px = CssValueParser.ToPixels(tok, availSize);
        return px > 0 || (px == 0 && tok is "0" or "0px" or "0%") ? (px, 0) : (float.NaN, 0);
    }

    static float ParseNum(string s) =>
        float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : float.NaN;

    /// <summary>按分隔符在括号外切分（默认空格）。</summary>
    static List<string> SplitTopLevel(string s, char sep = ' ')
    {
        var parts = new List<string>();
        int depth = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s)
        {
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            if (depth == 0 && (sep == ' ' ? char.IsWhiteSpace(ch) : ch == sep))
            {
                if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>
    /// 子项自对齐取值裁决：auto → 容器的 *-items 默认；flex-start/flex-end/left/right 是 start/end 的别名；
    /// normal/stretch/基线等一律按 stretch 处理（grid 里 normal≡stretch，基线对齐本引擎未实现，退化为顶端）。
    /// </summary>
    static string GridSelfAlign(string self, string containerDefault)
        => (self == "auto" || string.IsNullOrEmpty(self) ? containerDefault : self) switch
        {
            "start" or "flex-start" or "self-start" or "left" => "start",
            "end" or "flex-end" or "self-end" or "right" => "end",
            "center" => "center",
            _ => "stretch",
        };

    static void PositionGrid(BlockBox box, float contentX, float contentY, CB cb)
    {
        if (box.GridInfo == null) return;

        var gi = box.GridInfo;
        for (int i = 0; i < gi.Placements.Count; i++)
        {
            var (childIdx, col, row, colSpan, rowSpan) = gi.Placements[i];
            var child = box.Children[childIdx];
            if (child.Style.Position == "absolute") { PlaceAbsolute(child, cb); continue; }

            // 单元格 = 起始轨道左上角 + 所跨轨道总尺寸（含内部 gap）
            float cellX = contentX, cellY = contentY + gi.RowLead;
            for (int c = 0; c < col; c++) cellX += gi.ColWidths[c] + gi.ColGap;
            for (int r = 0; r < row; r++) cellY += gi.RowHeights[r] + gi.RowGap;
            float cellW = SumSpan(gi.ColWidths, col, colSpan, gi.ColGap);
            float cellH = SumSpan(gi.RowHeights, row, rowSpan, gi.RowGap);

            // 非 stretch 的那一轴在格内偏移（测量期已按 fit-content 收窄，这里只挪位）。
            // 传给 PositionRec 的是**外边距盒**原点——margin 由它自己加，这里再加一次就是双倍边距。
            float dx = SelfOffset(gi.SelfMain[i], cellW - child.TotalWidth);
            float dy = SelfOffset(gi.SelfCross[i], cellH - child.TotalHeight);
            PositionRec(child, cellX + dx, cellY + dy, cb);
        }
    }

    /// <summary>自对齐在某一轴上的偏移：stretch/start 贴左（上），end 贴右（下），center 居中；剩余空间为负时不缩位。</summary>
    static float SelfOffset(string align, float free)
        => align switch
        {
            "end" => Math.Max(0, free),
            "center" => Math.Max(0, free) / 2f,
            _ => 0f,
        };

    // ── 定位 ────────────────────────────────────────────────

    /// <summary>包含块：最近定位祖先的 padding box；absolute 后代据此定位，relative 偏移 % 也按它解析。</summary>
    readonly struct CB
    {
        public readonly float Left, Top, Width, Height;
        public CB(float l, float t, float w, float h) { Left = l; Top = t; Width = w; Height = h; }
    }

    /// <summary>给定 margin-box 左上角 (x,y)，递归设置全子树的 BorderX/BorderY（含 relative/absolute）。
    /// 顶边距被父吸收时（父↔首子坍缩）FlowInset 归 0，本子边框顶就落在游标上。</summary>
    public static void Position(BlockBox box, float x, float y) => PositionRec(box, x, y, null);

    static void PositionRec(BlockBox box, float x, float y, CB? cb)
    {
        var s = box.Style;
        float bx = x + s.MarginLeft, by = y + FlowInset(box);

        // relative：在流内位置基础上按偏移挪动（% 相对包含块尺寸）
        if (s.Position == "relative")
        {
            bx += Off(s.OffsetLeft, cb?.Width ?? 0);
            by += Off(s.OffsetTop, cb?.Height ?? 0);
        }

        box.BorderX = bx;
        box.BorderY = by;

        float contentX = bx + s.BorderWidthLeft + s.PaddingLeft;
        float contentY = by + s.BorderWidthTop + s.PaddingTop + box.ValignDY;
        if (box.WrappedLines != null)
            contentY += box.WrappedLines.Length * s.LineHeightPx();

        // 子代的包含块：本盒若定位（relative/absolute）则用本盒 padding box，否则继承上层；根盒兜底
        CB childCb = s.Position != "static" || cb == null
            ? new CB(bx + s.BorderWidthLeft, by + s.BorderWidthTop,
                     s.PaddingLeft + box.ContentWidth + s.PaddingRight,
                     s.PaddingTop + box.ContentHeight + s.PaddingBottom)
            : cb.Value;

        if (s.Display == "table")
        {
            PositionTable(box, contentX, contentY, childCb);
        }
        else if (ComputedStyle.IsFlexDisplay(s.Display))
        {
            PositionFlex(box, contentX, contentY, childCb);
        }
        else if (ComputedStyle.IsGridDisplay(s.Display))
        {
            PositionGrid(box, contentX, contentY, childCb);
        }
        else if (box.Lines != null)
        {
            PositionInline(box, contentX, contentY, childCb);
        }
        else
        {
            // 块级子元素布局。浮动位置在测量期已算好（FloatPlacements），此处按记录摆放并复现 clear 推进。
            // margin 坍缩的 absorb 必须与 MeasureBlock 里算的一模一样，否则父高与子位会互相错开。
            float cy = contentY;
            var placements = box.FloatPlacements;
            var bands = box.FloatBands;
            BlockBox? flowPrev = null;

            for (int i = 0; i < box.Children.Count; i++)
            {
                var c = box.Children[i];
                var cs = c.Style;
                if (cs.Position == "absolute") { PlaceAbsolute(c, childCb); continue; }

                if (cs.Clear != "none" && bands != null)
                {
                    float before = cy;
                    bool wantL = cs.Clear is "left" or "both", wantR = cs.Clear is "right" or "both";
                    // 与 MeasureBlock 同一判据：浮动底边对齐的是本盒 border box 顶，不是 margin box 顶。
                    float yRel = cy - contentY + cs.MarginTop;
                    foreach (var b in bands)
                        if ((b.IsLeft && wantL || !b.IsLeft && wantR) && b.BottomRel > yRel)
                            yRel = Math.Max(yRel, b.BottomRel);
                    cy = contentY + yRel - cs.MarginTop;
                    if (cy != before) flowPrev = null;   // 被顶开即不再与上一块相邻
                }

                if (cs.Float != "none" && cs.Position == "static")
                {
                    var p = placements?.FirstOrDefault(pl => pl.ChildIndex == i);
                    // Place 返回的就是 **margin box** 左上角，而 PositionRec 的入参同样是 margin box 原点
                    // （它内部再 +MarginLeft / +FlowInset 折出 border box）。再减一次 margin 等于把盒往左上
                    // 各拽回一个 margin：m2 的 .float-right{margin-left:15px} 实测贴右缘时少算 15px。
                    if (p.HasValue)
                        PositionRec(c, contentX + p.Value.X, contentY + p.Value.YRel, childCb);
                    else
                        PositionRec(c, contentX, cy, childCb);   // 无测量记录（旧回退路径）
                    continue;
                }

                // margin 的 auto：定宽盒在父内容宽内水平居中/靠右（如 .wrap 的 margin:0 auto），否则正常靠左。
                float xoff = 0;
                if (!cs.Width.IsAuto)
                {
                    float free = box.ContentWidth - c.TotalWidth;
                    if (free > 0)
                    {
                        bool la = cs.MarginLeftAuto, ra = cs.MarginRightAuto;
                        if (la && ra) xoff = free / 2;
                        else if (la) xoff = free;
                    }
                }
                else if (cs.Display is "inline-block" or "inline-flex" or "inline-grid")
                {
                    // 收缩适配的行内级盒：CSS 会把它包进继承 text-align 的匿名块，故按父 text-align 水平对齐
                    // （如 .hero{text-align:center} 里的 .chip 药丸应居中，而非贴左填满）。
                    float free = box.ContentWidth - c.TotalWidth;
                    if (free > 0)
                        xoff = box.Style.TextAlignment switch
                        {
                            CssTextAlign.Center => free / 2,
                            CssTextAlign.Right => free,
                            _ => 0,
                        };
                }
                // 收缩适配的行内级盒在 MeasureBlock 里走的是另一条分支（打断坍缩链、不吃 margin），此处必须同样处理。
                bool shrinkInline = cs.Width.IsAuto && cs.Display is "inline-flex" or "inline-grid" or "inline-block";
                float absorb = shrinkInline ? 0f : AbsorbMargin(flowPrev, c);
                PositionRec(c, contentX + xoff, cy - absorb, childCb);
                cy += (shrinkInline ? c.TotalHeight : FlowHeight(c)) - absorb;
                flowPrev = shrinkInline ? null : c;
            }
        }
    }

    /// <summary>inline 块：按 text-align 给每行算左偏移（含浮动环绕缩进）；原子盒按基线定位（底对齐基线）。</summary>
    static void PositionInline(BlockBox box, float contentX, float contentY, CB cb)
    {
        var s = box.Style;
        float y = contentY;
        foreach (var ln in box.Lines!)
        {
            float avail = ln.Avail > 0 ? ln.Avail : box.ContentWidth;
            float align = s.TextAlignment switch
            {
                CssTextAlign.Center => Math.Max(0, (avail - ln.Width) / 2),
                CssTextAlign.Right => Math.Max(0, avail - ln.Width),
                _ => 0,
            };
            ln.Left = ln.Inset + align;
            float x = contentX + ln.Left;
            foreach (var f in ln.Frags)
            {
                if (f.Box == null) continue;
                float sx = x + f.X, sy = AtomicTop(f, y, ln);
                // 行内上下文里的绝对定位子盒：静态位置就是它作为原子盒本应占的地方（上面已算好），
                // 一旦写了 left/right/top/bottom 再按包含块改判（PositionInline 走不到 :1757 那条块级分支）。
                if (f.Box.Style.Position == "absolute") PlaceAbsolute(f.Box, cb, sx, sy);
                else PositionRec(f.Box, sx, sy, cb);
            }
            y += ln.Height;
        }
    }

    /// <summary>原子盒（inline-block/img）按 vertical-align 求行内顶边 y。
    /// 基线类对齐（baseline/middle/super/sub/长度）都从「盒基线压行盒基线」推起：盒基线以下的部分
    /// 由 <c>f.Descent</c> 给出（inline-block 是末行基线以下那截，替换元素是 0），所以顶边 =
    /// 行盒基线 − 盒高 + 盒基线以下。</summary>
    static float AtomicTop(InlineFrag f, float lineY, LineBox ln)
    {
        float boxH = f.Box!.TotalHeight;
        float baseTop = lineY + ln.Baseline - boxH + f.Descent;   // vertical-align: baseline
        var va = f.Style.VerticalAlign;
        switch (va)
        {
            case "top": return lineY;
            case "bottom": return lineY + ln.Height - boxH;
            case "middle": return lineY + ln.Baseline - f.FontSize * FontManager.XHeightPerEm(f.Style.FontFamily, f.Style.FontWeight) / 2 - boxH / 2;
            case "super": return baseTop - 0.35f * f.FontSize;
            case "sub": return baseTop + 0.25f * f.FontSize;
            default:
                float len = va.EndsWith('%')
                    ? ln.Height * CssValueParser.ParseFloat(va[..^1]) / 100f
                    : CssValueParser.ToPixels(va, f.FontSize);
                return baseTop - (float.IsNaN(len) ? 0 : len);
        }
    }

    /// <summary>absolute 子盒：按包含块 + 偏移定位（left 优先，其次 right 反推；auto 落到静态位置）。</summary>
    static void PlaceAbsolute(BlockBox c, CB cb, float? staticX = null, float? staticY = null)
    {
        var s = c.Style;
        float? left = OffP(s.OffsetLeft, cb.Width), right = OffP(s.OffsetRight, cb.Width);
        float? top = OffP(s.OffsetTop, cb.Height), bottom = OffP(s.OffsetBottom, cb.Height);

        // 拉伸行为：如果同时设置了 left+right 且 width 为 auto，则水平拉伸
        if (left != null && right != null && s.Width.IsAuto)
        {
            float stretchW = cb.Width - left.Value - right.Value;
            float decoW = s.MarginLeft + s.MarginRight + s.BorderWidthLeft + s.BorderWidthRight + s.PaddingLeft + s.PaddingRight;
            float bb = s.BoxSizing == "border-box" ? decoW : 0;
            c.ContentWidth = Math.Max(0, stretchW - (s.BoxSizing == "border-box" ? decoW : 0));
        }

        // 垂直拉伸：同时设置了 top+bottom 且 height 为 auto
        if (top != null && bottom != null && s.Height.IsAuto)
        {
            float stretchH = cb.Height - top.Value - bottom.Value;
            float decoH = s.MarginTop + s.MarginBottom + s.BorderWidthTop + s.BorderWidthBottom + s.PaddingTop + s.PaddingBottom;
            c.ContentHeight = Math.Max(0, stretchH - (s.BoxSizing == "border-box" ? decoH : 0));
        }

        // 两轴都没写偏移时，落**静态位置**（调用方给的）而不是 CB 起点角：CSS Position §10.1 的
        // auto 语义就是「用它在常规流里本应占的位置」。staticX/staticY 缺省（块级子、grid 子）时
        // 才退回 CB 原点——那两种情形下静态位置本就等于起点角。
        float mx = left != null ? cb.Left + left.Value
               : right != null ? cb.Left + cb.Width - right.Value - c.TotalWidth
               : staticX ?? cb.Left;
        float my = top != null ? cb.Top + top.Value
               : bottom != null ? cb.Top + cb.Height - bottom.Value - c.TotalHeight
               : staticY ?? cb.Top;

        PositionRec(c, mx, my, cb);
    }

    static float Off(CssSize c, float base_) { var r = c.Resolve(base_); return float.IsNaN(r) ? 0 : r; }
    static float? OffP(CssSize c, float base_) { var r = c.Resolve(base_); return float.IsNaN(r) ? null : r; }

    static void PositionFlex(BlockBox box, float contentX, float contentY, CB cb)
    {
        var s = box.Style;
        bool isRow = s.FlexDirection.StartsWith("row");
        bool isReverse = s.FlexDirection.EndsWith("reverse");
        bool isWrapReverse = s.FlexWrap == "wrap-reverse";
        float gap = ResolveGap(s, box.ContentWidth);
        var kids = box.Children.FindAll(c => c.Style.Position != "absolute");
        int n = kids.Count;
        if (n == 0 || box.FlexLines == null) { PlaceAbsChildren(box, cb, contentX, contentY); return; }

        float mainSize = isRow ? box.ContentWidth : box.ContentHeight;
        float crossSize = isRow ? box.ContentHeight : box.ContentWidth;

        // 计算交叉轴起始位置和间距
        float totalCross = gap * (box.FlexLines.Count - 1);
        foreach (var line in box.FlexLines) totalCross += line.CrossSize;
        float crossFree = crossSize - totalCross;
        float crossCursor, crossBetween;

        // align-content 暂只支持 normal/stretch：单行容器有确定交叉尺寸时，线交叉尺寸取容器内交叉尺寸，
        // 否则行内自由空间无处安放，align-items 的 center/flex-end 只会在"最高子项"那条线里居中。
        bool stretchSingleLine = box.FlexLines.Count == 1 && crossFree > 0.5f;

        crossCursor = 0;
        crossBetween = gap;

        // 逐行定位
        float crossPos = crossCursor;
        foreach (var line in box.FlexLines)
        {
            float lineCross = stretchSingleLine ? crossSize : line.CrossSize;
            // 计算该行主轴起始位置和间距
            float lineMainUsed = gap * (line.ItemIndices.Count - 1);
            foreach (int idx in line.ItemIndices)
                lineMainUsed += isRow ? kids[idx].TotalWidth : kids[idx].TotalHeight;
            float lineFree = mainSize - lineMainUsed;

            // 主轴 auto 边距先吃掉全部自由空间（CSS Flexbox §9.1：一旦有 auto 边距，justify-content 失效）。
            // 单边 auto 把该子项推到行尾/行首，双边 auto 即旧的「居中」——所以居中只是它的一个特例。
            int autoEdges = 0;
            foreach (int idx in line.ItemIndices)
                autoEdges += isRow
                    ? (kids[idx].Style.MarginLeftAuto ? 1 : 0) + (kids[idx].Style.MarginRightAuto ? 1 : 0)
                    : (kids[idx].Style.MarginTopAuto ? 1 : 0) + (kids[idx].Style.MarginBottomAuto ? 1 : 0);
            float autoShare = autoEdges > 0 && lineFree > 0 ? lineFree / autoEdges : 0;

            float cursor, between;
            if (autoShare > 0) { cursor = 0; between = gap; }
            else switch (s.JustifyContent)
            {
                case "center": cursor = lineFree / 2; between = gap; break;
                case "flex-end": case "end": cursor = lineFree; between = gap; break;
                case "space-between": cursor = 0; between = line.ItemIndices.Count > 1 ? gap + lineFree / (line.ItemIndices.Count - 1) : gap; break;
                case "space-around": between = line.ItemIndices.Count > 0 ? gap + lineFree / line.ItemIndices.Count : gap; cursor = between / 2; break;
                case "space-evenly": between = gap + lineFree / (line.ItemIndices.Count + 1); cursor = between; break;
                default: cursor = 0; between = gap; break;
            }

            // 基线组：参与 baseline 对齐的子项共用一条基线，其位置取最深的基线（谁都不越出交叉轴起点）。
            float lineBaseline = 0;
            foreach (int idx in line.ItemIndices)
            {
                var ms = kids[idx].Style;
                if ((ms.AlignSelf == "auto" ? s.AlignItems : ms.AlignSelf) != "baseline") continue;
                lineBaseline = Math.Max(lineBaseline, InlineLayout.BaselineToTop(kids[idx]) ?? 0);
            }

            // 定位该行的每个子项。row/column-reverse 时 MeasureFlex 给的 ItemIndices 已经是反的，
            // 而镜像又会再翻一次 → 先按 DOM 序取，主轴坐标交给下面的镜像统一翻。
            List<int> order;
            if (isReverse) { order = new List<int>(line.ItemIndices); order.Reverse(); }
            else order = line.ItemIndices;
            foreach (int idx in order)
            {
                var c = kids[idx];
                float cf = isRow ? c.TotalHeight : c.TotalWidth;
                string align = c.Style.AlignSelf == "auto" ? s.AlignItems : c.Style.AlignSelf;
                float crossOff = align switch
                {
                    "center" => Math.Max(0, (lineCross - cf) / 2),
                    "flex-end" or "end" => Math.Max(0, lineCross - cf),
                    "baseline" => Math.Max(0, lineBaseline - (InlineLayout.BaselineToTop(c) ?? 0)),
                    _ => 0, // flex-start / stretch
                };

                if (isRow)
                {
                    float mainPos = cursor;
                    if (autoShare > 0)
                    {
                        if (c.Style.MarginLeftAuto) mainPos += autoShare;
                        cursor += autoShare * ((c.Style.MarginLeftAuto ? 1 : 0) + (c.Style.MarginRightAuto ? 1 : 0));
                    }
                    // row-reverse：主轴起点翻到容器右端（justify-content 的 flex-start 也跟着到右边），
                    // 顺序已由 MeasureFlex 反过，这里只镜像整行的主轴坐标。
                    if (isReverse) mainPos = mainSize - mainPos - c.TotalWidth;
                    PositionRec(c, contentX + mainPos, contentY + crossPos + crossOff, cb);
                }
                else
                {
                    float mainPos = cursor;
                    if (autoShare > 0)
                    {
                        if (c.Style.MarginTopAuto) mainPos += autoShare;
                        cursor += autoShare * ((c.Style.MarginTopAuto ? 1 : 0) + (c.Style.MarginBottomAuto ? 1 : 0));
                    }
                    if (isReverse) mainPos = mainSize - mainPos - c.TotalHeight;   // column-reverse 同理
                    PositionRec(c, contentX + crossPos + crossOff, contentY + mainPos, cb);
                }

                cursor += (isRow ? c.TotalWidth : c.TotalHeight) + between;
            }

            crossPos += lineCross + crossBetween;
        }

        PlaceAbsChildren(box, cb, contentX, contentY);
    }

    /// <summary>
    /// flex 容器的绝对定位子：静态位置按 CSS Flexbox §4 / Position §10.1 ——
    /// **「把它当成容器里唯一的 flex item」**，所以 justify-content / align-items（含子项 align-self）
    /// 两轴都要生效。此前一律按 CB 起点角放，pricing 的 <c>.switcher{align-items:center}</c> 里
    /// 那个只写 <c>right:0</c> 的 <c>a.more</c> 因此整幅上偏 (54−22.4)/2 = 15.8px（top/page 两样例共 26 盒受累）。
    /// 只在某轴两向偏移都为 auto 时才起作用（PlaceAbsolute 内部裁决）；写了 left/top 等仍按偏移。
    /// </summary>
    static void PlaceAbsChildren(BlockBox box, CB cb, float contentX, float contentY)
    {
        var s = box.Style;
        bool isRow = s.FlexDirection.StartsWith("row");
        foreach (var c in box.Children)
        {
            if (c.Style.Position != "absolute") continue;
            float mainAvail = isRow ? box.ContentWidth : box.ContentHeight;
            float crossAvail = isRow ? box.ContentHeight : box.ContentWidth;
            float mainSize = isRow ? c.TotalWidth : c.TotalHeight;
            float crossSize = isRow ? c.TotalHeight : c.TotalWidth;
            // 单 item 时 space-* 与 flex-start 等价（Blink 同解），故只认三种显式对齐
            float mainOff = s.JustifyContent switch
            {
                "center" => (mainAvail - mainSize) / 2,
                "flex-end" or "end" => mainAvail - mainSize,
                _ => 0,
            };
            float crossOff = GridSelfAlign(c.Style.AlignSelf, s.AlignItems) switch
            {
                "center" => (crossAvail - crossSize) / 2,
                "end" => crossAvail - crossSize,
                _ => 0,
            };
            PlaceAbsolute(c, cb,
                contentX + (isRow ? mainOff : crossOff),
                contentY + (isRow ? crossOff : mainOff));
        }
    }
}
