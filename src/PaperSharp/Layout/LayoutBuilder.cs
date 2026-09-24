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
        anon.BorderWidthTop = anon.BorderWidthRight = anon.BorderWidthBottom = anon.BorderWidthLeft = 0;
        anon.BorderRadiusTopLeft = anon.BorderRadiusTopRight = anon.BorderRadiusBottomRight = anon.BorderRadiusBottomLeft = 0;
        anon.BackgroundColor = SKColor.Empty;
        anon.Background = null;
        anon.Shadow = null;
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
        if (box.IsReplaced) { MeasureImage(box, availWidth); return; }
        if (box.Style.Display == "table") { MeasureTable(box, availWidth); return; }
        box.ContentWidth = ResolveWidth(box.Style.Width, availWidth, box.Style);
        float contentTopY = outerTopY + box.Style.MarginTop + box.Style.BorderWidthTop + box.Style.PaddingTop;
        MeasureHeight(box, containingHeight, fc, contentTopY);
    }

    /// <summary>flex 子项：强制内容宽 = 分配到的主轴尺寸，再测高。</summary>
    static void MeasureWithContentWidth(BlockBox box, float contentWidth, float containingHeight)
    {
        if (box.IsReplaced) { MeasureImage(box, contentWidth); return; }
        box.Style.ResolveBoxSizes(contentWidth);   // 走 Measure 主入口才做的声明值→float 折算，这里补上（否则子项 padding/margin 恒 0）
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

    /// <summary>替换元素（img）：按 CSS 尺寸（含 width%）+ 自然尺寸定 content 盒，保持纵横比。</summary>
    static void MeasureImage(BlockBox box, float availWidth)
    {
        var s = box.Style;
        float decoW = s.MarginLeft + s.MarginRight + s.BorderWidthLeft + s.BorderWidthRight + s.PaddingLeft + s.PaddingRight;
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
        float spacing = collapse ? 0 : s.BorderSpacing;

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

        // 计算目标网格宽（用于解析百分比列宽）
        float padBorder = s.PaddingLeft + s.PaddingRight + s.BorderWidthLeft + s.BorderWidthRight;
        float wResTbl = s.Width.Resolve(availWidth);
        float targetGridForPct = !float.IsNaN(wResTbl)
            ? Math.Max(0, wResTbl - padBorder - spacing * (numCols - 1))
            : Math.Max(0, availWidth - s.MarginLeft - s.MarginRight - padBorder - spacing * (numCols - 1));

        // 列宽（单元格 outer 盒宽）；+1px 余量避免「内容宽恰好等于内在宽」触发亚像素折行
        // 百分比 width 优先：解析为占目标网格宽的比例，作为列宽下限
        float[] colW = new float[numCols];
        for (int r = 0; r < grid.Count; r++)
        {
            foreach (var info in grid[r])
            {
                if (info.Span == 1)
                {
                    float intrinsic = IntrinsicContentWidth(info.Cell) + CellHDeco(info.Cell) + 1;
                    float pctW = ResolvePctCellWidth(info.Cell, targetGridForPct);
                    colW[info.Col] = Math.Max(colW[info.Col], Math.Max(intrinsic, pctW));
                }
            }
        }
        // 多跨：不足则把差额均摊到所跨各列（含百分比宽度）
        foreach (var infos in grid)
            foreach (var info in infos)
                if (info.Span > 1)
                {
                    float intrinsic = IntrinsicContentWidth(info.Cell) + CellHDeco(info.Cell) + spacing * (info.Span - 1) + 1;
                    float pctW = ResolvePctCellWidth(info.Cell, targetGridForPct);
                    float need = Math.Max(intrinsic, pctW);
                    float have = 0; for (int j = info.Col; j < info.Col + info.Span; j++) have += colW[j];
                    float deficit = need - have;
                    if (deficit > 0) { float add = deficit / info.Span; for (int j = info.Col; j < info.Col + info.Span; j++) colW[j] += add; }
                }

        // 目标总宽：显式 width 优先；否则自然宽夹到可用宽
        float gridW = 0; for (int j = 0; j < numCols; j++) gridW += colW[j];
        float gapW = spacing * (numCols - 1);
        float targetTotal = !float.IsNaN(wResTbl) ? wResTbl + padBorder + gapW
            : Math.Min(gridW + gapW + padBorder, Math.Max(0, availWidth - s.MarginLeft - s.MarginRight));
        float targetGrid = Math.Max(0, targetTotal - padBorder - gapW);
        if (gridW > 0.5f && Math.Abs(targetGrid - gridW) > 0.5f)
        {
            float scale = targetGrid / gridW;
            for (int j = 0; j < numCols; j++) colW[j] *= scale;
            gridW = targetGrid;
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
                float cellOuterW = spacing * (info.Span - 1);
                for (int j = info.Col; j < info.Col + info.Span; j++) cellOuterW += colW[j];
                Measure(cell, cellOuterW);
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
                    have += spacing * (Math.Min(info.RowSpan, grid.Count - r) - 1);
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
        for (int r = 0; r < grid.Count; r++)
            foreach (var info in grid[r])
            {
                float full;
                if (info.RowSpan == 1)
                    full = rowH[r];
                else
                {
                    full = spacing * (Math.Min(info.RowSpan, grid.Count - r) - 1);
                    for (int dr = 0; dr < info.RowSpan && r + dr < grid.Count; dr++) full += rowH[r + dr];
                }
                full = Math.Max(0, full - CellVDeco(info.Cell));
                float free = Math.Max(0, full - info.Cell.ContentHeight);
                info.Cell.ContentHeight = full;
                info.Cell.ValignDY = info.Cell.Style.VerticalAlign switch
                {
                    "bottom" => free,
                    "middle" => free / 2,
                    _ => 0,   // top / baseline / length 近似按顶部
                };
            }
        totalH += spacing * (grid.Count - 1);
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
                headerH += rowH[r] + (r < grid.Count - 1 ? spacing : 0);
            }
            else if (rowGroupTypes[r] == "table-footer-group")
            {
                box.TFooterRows.Add(r);
                footerH += rowH[r] + (r < grid.Count - 1 ? spacing : 0);
            }
        }
        box.THeaderHeight = headerH;
        box.TFooterHeight = footerH;

        // 存回供定位
        box.TRows = grid; box.TColW = colW; box.TRowH = rowH; box.TSpacing = spacing; box.TNumCols = numCols;
        box.TRowBoxes = rowBoxList;
    }

    /// <summary>表格定位：把网格按列宽/行高铺开，逐格 PositionRec；行盒撑满整行宽以让行背景可见。支持 rowspan 跨行定位 + caption。</summary>
    static void PositionTable(BlockBox box, float contentX, float contentY, CB cb)
    {
        if (box.TRows == null || box.TColW == null || box.TRowH == null) return;
        float spacing = box.TSpacing;
        float gridW = box.ContentWidth;
        int numRows = box.TRows.Count;

        // caption 定位：top 在网格上方，bottom 在网格下方
        float gridStartY = contentY;
        if (box.TCaption != null)
        {
            if (!box.TCaptionBottom)
            {
                PositionRec(box.TCaption, contentX, contentY, cb);
                gridStartY = contentY + box.TCaption.TotalHeight;
            }
        }

        // 预计算每行顶边 y 坐标
        float[] rowY = new float[numRows];
        float y = gridStartY;
        for (int r = 0; r < numRows; r++)
        {
            rowY[r] = y;
            y += box.TRowH[r] + spacing;
        }

        // 行盒几何：横跨整表宽、纵占行高
        for (int r = 0; r < numRows; r++)
        {
            var rowBox = box.TRowBoxes != null && r < box.TRowBoxes.Count ? box.TRowBoxes[r] : null;
            if (rowBox != null)
            {
                rowBox.BorderX = contentX; rowBox.BorderY = rowY[r];
                rowBox.ContentWidth = gridW; rowBox.ContentHeight = box.TRowH[r];
            }
        }

        // 跟踪被 rowspan 占据的列（值=剩余跨行数）
        var rowspanOccupied = new Dictionary<int, int>();
        for (int r = 0; r < numRows; r++)
        {
            // 递减并移除已到期的 rowspan 占用
            var expired = new List<int>();
            foreach (var kv in rowspanOccupied)
            {
                if (kv.Value <= 1) expired.Add(kv.Key);
                else rowspanOccupied[kv.Key] = kv.Value - 1;
            }
            foreach (var k in expired) rowspanOccupied.Remove(k);

            // 按列顺序定位本行单元格，跳过被 rowspan 占据的列
            float x = contentX;
            int colCursor = 0;
            foreach (var info in box.TRows[r])
            {
                // 跳过被 rowspan 占据的列，累加其宽度
                while (colCursor < info.Col)
                {
                    x += box.TColW[colCursor];
                    if (colCursor < info.Col) x += spacing;
                    colCursor++;
                }
                float cellOuterW = spacing * (info.Span - 1);
                for (int j = info.Col; j < info.Col + info.Span; j++) cellOuterW += box.TColW[j];
                PositionRec(info.Cell, x, rowY[r], cb);
                // 标记 rowspan 占用
                if (info.RowSpan > 1)
                    for (int dc = 0; dc < info.Span; dc++)
                        rowspanOccupied[info.Col + dc] = info.RowSpan;
                x += cellOuterW + spacing;
                colCursor = info.Col + info.Span;
            }
        }
        // bottom caption 定位在网格下方
        if (box.TCaption != null && box.TCaptionBottom)
        {
            float captionY = gridStartY;
            for (int r = 0; r < numRows; r++) captionY += box.TRowH[r] + spacing;
            PositionRec(box.TCaption, contentX, captionY, cb);
        }
    }

    /// <summary>跨页续表：定位指定行子集（thead + 部分 body + tfoot）到给定 Y 起点。返回占用的总高度。</summary>
    public static float PositionTableRows(BlockBox box, float contentX, float startY, List<int> rowIndices)
    {
        if (box.TRows == null || box.TColW == null || box.TRowH == null || rowIndices.Count == 0) return 0;
        float spacing = box.TSpacing;
        float gridW = box.ContentWidth;
        float y = startY;

        foreach (var r in rowIndices)
        {
            if (r >= box.TRows.Count) continue;
            // 行盒几何
            var rowBox = box.TRowBoxes != null && r < box.TRowBoxes.Count ? box.TRowBoxes[r] : null;
            if (rowBox != null)
            {
                rowBox.BorderX = contentX; rowBox.BorderY = y;
                rowBox.ContentWidth = gridW; rowBox.ContentHeight = box.TRowH[r];
            }
            // 逐格定位
            float x = contentX;
            int colCursor = 0;
            foreach (var info in box.TRows[r])
            {
                while (colCursor < info.Col)
                {
                    x += box.TColW[colCursor] + spacing;
                    colCursor++;
                }
                float cellOuterW = spacing * (info.Span - 1);
                for (int j = info.Col; j < info.Col + info.Span; j++) cellOuterW += box.TColW[j];
                PositionRec(info.Cell, x, y, null);
                x += cellOuterW + spacing;
                colCursor = info.Col + info.Span;
            }
            y += box.TRowH[r] + spacing;
        }
        return y - startY - spacing; // 减去末尾多余的 spacing
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

    static float CellHDeco(BlockBox c) => c.Style.PaddingLeft + c.Style.PaddingRight + c.Style.BorderWidthLeft + c.Style.BorderWidthRight;
    static float CellVDeco(BlockBox c) => c.Style.PaddingTop + c.Style.PaddingBottom + c.Style.BorderWidthTop + c.Style.BorderWidthBottom;

    /// <summary>解析单元格 width:% 为 outer 盒宽（含 deco）；非百分比返回 0。</summary>
    static float ResolvePctCellWidth(BlockBox cell, float targetGridW)
    {
        var w = cell.Style.Width;
        if (!w.IsPercent && w.Kind != CssSizeKind.Calc) return 0;
        float contentW = w.Resolve(targetGridW);
        if (float.IsNaN(contentW) || contentW <= 0) return 0;
        return contentW + CellHDeco(cell);
    }

    static void ZeroCellMargins(BlockBox c)
    {
        var s = c.Style;
        s.MarginLeftCss = s.MarginRightCss = s.MarginTopCss = s.MarginBottomCss = CssSize.Auto;
        s.MarginLeft = s.MarginRight = s.MarginTop = s.MarginBottom = 0;
    }

    // ── 相邻兄弟 margin 坍缩（CSS 2.1 §8.3.1）────────────────────────────
    //
    // 此前引擎完全不坍缩：两个相邻 <p>（各 margin 16px）之间留 32px，浏览器只留 16px。
    // 单看一块不多，一页十几个块就累积成几十 px 的系统性下偏——Playground 的盒级 Δ 表在
    // invoice/table 样例上量到的逐级漂移主要来自这里。
    //
    // 只做「相邻兄弟」这一档。父子坍缩要判父盒有无 border/padding，实测两侧本就不坍缩
    // （padded body 里 h1 引擎与浏览器同为 y=45），没有证据不动。

    /// <summary>
    /// 该盒是否参与兄弟间 margin 坍缩。条件按浏览器实测（headless Edge + getBoundingClientRect，
    /// 逐条钉在 MarginCollapsingTests 里）而非凭记忆：只有「不在同一个流里」才阻断——
    /// 行内级盒（inline-block/flex/grid）、浮动、脱流定位。曾被误当作阻断条件、实测照样坍的：
    /// overflow:hidden、display:flow-root、position:relative、带 clear 但没被顶开、中间夹 display:none。
    /// 若把这些也判成不坍，等于在本来正确的地方重新制造 12~30px 的下偏。
    /// </summary>
    internal static bool Collapsible(ComputedStyle s)
        => !ComputedStyle.IsInlineLevel(s.Display) && s.Float == "none";

    /// <summary>CSS 相邻 margin 坍缩值：同正取大、同负取更负、异号相加。</summary>
    static float CollapseMargins(float a, float b)
        => a >= 0 && b >= 0 ? Math.Max(a, b) : a <= 0 && b <= 0 ? Math.Min(a, b) : a + b;

    /// <summary>
    /// 本盒相对上一个流内兄弟应「吃掉」的像素数（0 = 不坍缩）。
    /// Measure、Position、分页游标三处必须算同一个值，否则父高、子位、页数会各说各话。
    /// </summary>
    internal static float AbsorbMargin(BlockBox? prev, ComputedStyle next)
        => prev == null || !Collapsible(prev.Style) || !Collapsible(next) ? 0f
         : prev.Style.MarginBottom + next.MarginTop - CollapseMargins(prev.Style.MarginBottom, next.MarginTop);

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
        if (!string.IsNullOrEmpty(s.TextContent))
        {
            using var font = new SKFont(box.Typeface, s.FontSize);
            box.WrappedLines = TextMeasurer.Wrap(s.TextContent, box.ContentWidth, font, s, ambient, contentTopY, out var lefts);
            box.LineLefts = lefts;
            if (s.TextOverflow == "ellipsis" && s.WhiteSpace == "nowrap" && !s.Width.IsAuto)
                box.WrappedLines = TruncateEllipsis(box.WrappedLines, box.ContentWidth, font, s.LetterSpacing);
            h += box.WrappedLines.Length * s.FontSize * s.LineHeightFactor;

            // text-fit: scale-down —— 高度定死且文本溢出时逐步缩字号重排（下限 6px）
            if (s.TextFit == "scale-down" && !float.IsNaN(childContainingHeight) && childContainingHeight > 0)
                for (int iter = 0; iter < 10 && h > childContainingHeight && s.FontSize > 6f; iter++)
                {
                    s.FontSize = Math.Max(6f, s.FontSize * childContainingHeight / h * 0.98f);
                    using var fitFont = new SKFont(box.Typeface, s.FontSize);
                    box.WrappedLines = TextMeasurer.Wrap(s.TextContent, box.ContentWidth, fitFont, s, ambient, contentTopY, out var fl);
                    box.LineLefts = fl;
                    h = box.WrappedLines.Length * s.FontSize * s.LineHeightFactor;
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
            if (cs.Clear != "none" && fc != null)
            {
                float yAbs = contentTopY + h;
                float pushed = fc.ClearTo(cs.Clear is "left" or "both", cs.Clear is "right" or "both", yAbs);
                if (pushed > yAbs) { h = pushed - contentTopY; flowPrev = null; }
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
            float absorb = AbsorbMargin(flowPrev, cs);
            Measure(c, box.ContentWidth, childContainingHeight, fc, contentTopY + h - absorb);
            h += c.TotalHeight - absorb;
            flowPrev = c;
        }

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

    /// <summary>子项主轴 base：flex-basis（%相对主轴可用；border-box 时含自身 padding/border）> 显式 width/height > 内在尺寸。</summary>
    static float FlexBase(BlockBox c, bool isRow, float mainAvail, float? containerDefH)
    {
        var s = c.Style;
        if (!s.FlexBasis.IsAuto)
        {
            float r = s.FlexBasis.Resolve(mainAvail);
            if (!float.IsNaN(r))
                return s.BoxSizing == "border-box" ? r - MainDeco(c, isRow) : r;
        }
        float dim = isRow ? s.Width.Resolve(mainAvail) : s.Height.Resolve(containerDefH ?? float.NaN);
        if (!float.IsNaN(dim)) return dim;
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

    static float IntrinsicContentWidth(BlockBox c)
    {
        var s = c.Style;
        if (s.Width.IsFixed) return s.Width.Absolute;
        if (s.FlexBasis.IsFixed) return s.FlexBasis.Absolute;
        if (c.IsReplaced) return c.NaturalW;
        // inline FC：单行内在宽 = 各原子内在宽之和（文本 run 用字体度量，原子盒递归收缩）
        if (c.Atoms != null)
        {
            float w = 0;
            foreach (var a in c.Atoms)
            {
                if (a.IsBreak) continue;
                // 原子盒一律递归取内在宽：替换元素在里面先认 CSS 显式宽（width:120px 的图就是 120），
                // 没有显式宽才回退自然宽。直接读 NaturalW 会把缩放图撑回原尺寸，整条 flex 行跟着错位。
                if (a.Box != null) w += MainDeco(a.Box, true) + IntrinsicContentWidth(a.Box);
                else if (!string.IsNullOrEmpty(a.Text))
                {
                    using var f = new SKFont(FontManager.Get(a.Style.FontFamily, a.Style.FontWeight), a.Style.FontSize);
                    w += TextMeasurer.NaturalWidth(a.Text!, f, a.Style);
                }
            }
            return w;
        }
        // flex/grid 容器里直接文本会被包成匿名子项，与元素子项并排占主轴 → 内在宽须"文本 + 各子项"求和；
        // 普通块则文本与块级子项各占独立行，取二者较大即可（沿用旧的"文本优先短路"）。
        if (ComputedStyle.IsFlexDisplay(s.Display) || ComputedStyle.IsGridDisplay(s.Display))
        {
            // 只有沿主轴并排的子项才「求和」：row flex / grid 的 max-content 宽 = 各项相加；
            // column flex 子项纵向堆叠，内在宽仍是各项之最——求和会把每一列都撑成整行之和。
            bool alongMain = ComputedStyle.IsGridDisplay(s.Display) || s.FlexDirection.StartsWith("row");
            float both = 0;
            if (!string.IsNullOrEmpty(s.TextContent))
            {
                using var fnt = new SKFont(c.Typeface, s.FontSize);
                both += TextMeasurer.NaturalWidth(s.TextContent, fnt, s);
            }
            foreach (var g in c.Children)
            {
                float w = IntrinsicContentWidth(g) + MainDeco(g, true);
                both = alongMain ? both + w : Math.Max(both, w);
            }
            return both;
        }
        if (!string.IsNullOrEmpty(s.TextContent))
        {
            using var font = new SKFont(c.Typeface, s.FontSize);
            return TextMeasurer.NaturalWidth(s.TextContent, font, s);
        }
        // 块级子项各占一行：收缩宽（max-content）= 最宽的那个，不是全部相加。
        // 相加会让「图 + 说明文字」这类 flex 项凭空宽出一截，后面的兄弟被整条推歪（Playground img 样例 216px）。
        float ww = 0;
        foreach (var g in c.Children) ww = Math.Max(ww, IntrinsicContentWidth(g) + MainDeco(g, true));
        return ww;
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

        // ── 放置 ──
        var placements = new List<(int childIdx, int col, int row, int colSpan, int rowSpan)>();
        var occupied = new HashSet<(int col, int row)>();
        int autoCol = 0, autoRow = 0;

        for (int i = 0; i < box.Children.Count; i++)
        {
            var cs = box.Children[i].Style;
            if (cs.Position == "absolute") { MeasureOutOfFlow(box.Children[i], box.ContentWidth); continue; }

            int colSpan = Math.Max(1, cs.GridColumnSpan), rowSpan = Math.Max(1, cs.GridRowSpan);
            int col = cs.GridColumnStart > 0 ? cs.GridColumnStart - 1 : -1;
            int row = cs.GridRowStart > 0 ? cs.GridRowStart - 1 : -1;
            if (cs.GridColumnEnd > cs.GridColumnStart && col >= 0) colSpan = cs.GridColumnEnd - col;
            if (cs.GridRowEnd > cs.GridRowStart && row >= 0) rowSpan = cs.GridRowEnd - row;

            if (col < 0 || row < 0)
            {
                // 自动放置：行优先扫描第一个空位
                int r = autoRow, c = autoCol;
                while (true)
                {
                    if (c + colSpan > colTracks.Count) { c = 0; r++; }
                    bool fits = (col < 0 || c == col) && (row < 0 || r == row) && !Intersects(occupied, c, r, colSpan, rowSpan);
                    if (fits) break;
                    c++;
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

        // 裁掉尾部无子项的多余轨道
        TrimTracks(colTracks, placements, p => (p.col, p.colSpan));
        TrimTracks(rowTracks, placements, p => (p.row, p.rowSpan));

        // ── 列定尺 ──
        float[] colW = SizeGridTracks(colTracks, box.ContentWidth, colGap,
            placements.Select(p => (p.col, p.colSpan, MeasureNeedWidth(box.Children[p.childIdx], box.ContentWidth))).ToArray());

        // ── 按格宽复测子项 ──
        foreach (var p in placements)
        {
            var child = box.Children[p.childIdx];
            float cellW = SumSpan(colW, p.col, p.colSpan, colGap);
            child.Style.ResolveBoxSizes(cellW);
            // 轨道宽是子项 margin box 的可用区；折算成内容宽再测（auto=拉伸减去装饰，固定宽用声明值）
            MeasureWithContentWidth(child, ResolveWidth(child.Style.Width, cellW, child.Style), myDefH ?? float.NaN);
        }

        // ── 行定尺 ──
        float[] rowH = SizeGridTracks(rowTracks, rowAvail, rowGap,
            placements.Select(p => (p.row, p.rowSpan, box.Children[p.childIdx].TotalHeight)).ToArray());

        // 未显式定高的子项纵向 stretch 到所跨行总高（grid 默认 align-self:stretch）
        foreach (var p in placements)
        {
            var child = box.Children[p.childIdx];
            if (child.Style.Height.IsAuto)
            {
                float decoH = child.Style.MarginTop + child.Style.MarginBottom
                    + child.Style.BorderWidthTop + child.Style.BorderWidthBottom
                    + child.Style.PaddingTop + child.Style.PaddingBottom;
                child.ContentHeight = Math.Max(child.ContentHeight, Math.Max(0, SumSpan(rowH, p.row, p.rowSpan, rowGap) - decoH));
            }
        }

        box.GridInfo = new GridInfo
        {
            ColWidths = colW,
            RowHeights = rowH,
            ColGap = colGap,
            RowGap = rowGap,
            Placements = placements
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

    static bool Intersects(HashSet<(int, int)> occ, int col, int row, int colSpan, int rowSpan)
    {
        for (int dc = 0; dc < colSpan; dc++)
            for (int dr = 0; dr < rowSpan; dr++)
                if (occ.Contains((col + dc, row + dr))) return true;
        return false;
    }

    static void TrimTracks<T>(List<GridTrack> tracks, List<T> placements, Func<T, (int start, int span)> start)
    {
        if (placements.Count == 0 || tracks.Count <= 1) return;
        int maxUsed = placements.Max(p => { var (s0, sp) = start(p); return s0 + sp; });
        while (tracks.Count > maxUsed && tracks.Count > 1) tracks.RemoveAt(tracks.Count - 1);
    }

    static float SumSpan(float[] sizes, int start, int span, float gap)
    {
        float v = 0;
        for (int i = start; i < start + span && i < sizes.Length; i++) v += sizes[i] + (i > start ? gap : 0);
        return v;
    }

    /// <summary>轨道定尺：固定值直接用；auto 按单跨内容取大；多跨不足均摊；fr 按剩余空间比例分配。</summary>
    static float[] SizeGridTracks(List<GridTrack> tracks, float avail, float gap, (int start, int span, float need)[] items)
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
        // 多跨不足：均摊给跨内轨道
        foreach (var it in items.Where(i => i.span > 1))
        {
            float sum = SumSpan(size, it.start, it.span, gap);
            float shortAmt = it.need - sum;
            if (shortAmt > 0.5f)
                for (int t = it.start; t < it.start + it.span && t < n; t++)
                    size[t] += shortAmt / it.span;
        }
        // fr 分配剩余
        float sumFr = 0;
        foreach (var tr in tracks) sumFr += tr.Fr;
        if (sumFr > 0 && !float.IsNaN(avail))
        {
            float used = 0;
            for (int t = 0; t < n; t++) used += size[t];
            used += gap * Math.Max(0, n - 1);
            float free = avail - used;
            if (free > 0)
                for (int t = 0; t < n; t++)
                    if (tracks[t].Fr > 0) size[t] = free * tracks[t].Fr / sumFr;
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

    static void PositionGrid(BlockBox box, float contentX, float contentY, CB cb)
    {
        if (box.GridInfo == null) return;

        var gi = box.GridInfo;
        float cy = contentY;

        for (int r = 0; r < gi.RowHeights.Length; r++)
        {
            float cx = contentX;
            for (int c = 0; c < gi.ColWidths.Length; c++)
            {
                // 找该格的所有子项
                foreach (var p in gi.Placements)
                {
                    if (p.col == c && p.row == r)
                    {
                        var child = box.Children[p.childIdx];
                        if (child.Style.Position == "absolute") { PlaceAbsolute(child, cb); continue; }

                        // 计算单元格位置
                        float cellX = cx;
                        float cellY = cy;

                        // 子项在单元格内默认左上对齐
                        child.BorderX = cellX + child.Style.MarginLeft;
                        child.BorderY = cellY + child.Style.MarginTop;

                        // 递归定位子树
                        PositionRec(child, child.BorderX, child.BorderY, cb);
                    }
                }
                cx += gi.ColWidths[c] + gi.ColGap;
            }
            cy += gi.RowHeights[r] + gi.RowGap;
        }
    }

    // ── 定位 ────────────────────────────────────────────────

    /// <summary>包含块：最近定位祖先的 padding box；absolute 后代据此定位，relative 偏移 % 也按它解析。</summary>
    readonly struct CB
    {
        public readonly float Left, Top, Width, Height;
        public CB(float l, float t, float w, float h) { Left = l; Top = t; Width = w; Height = h; }
    }

    /// <summary>给定 margin-box 左上角 (x,y)，递归设置全子树的 BorderX/BorderY（含 relative/absolute）。</summary>
    public static void Position(BlockBox box, float x, float y) => PositionRec(box, x, y, null);

    static void PositionRec(BlockBox box, float x, float y, CB? cb)
    {
        var s = box.Style;
        float bx = x + s.MarginLeft, by = y + s.MarginTop;

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
            contentY += box.WrappedLines.Length * s.FontSize * s.LineHeightFactor;

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
                    float yRel = cy - contentY;
                    foreach (var b in bands)
                        if ((b.IsLeft && wantL || !b.IsLeft && wantR) && b.BottomRel > yRel)
                            yRel = Math.Max(yRel, b.BottomRel);
                    cy = contentY + yRel;
                    if (cy != before) flowPrev = null;   // 被顶开即不再与上一块相邻
                }

                if (cs.Float != "none" && cs.Position == "static")
                {
                    var p = placements?.FirstOrDefault(pl => pl.ChildIndex == i);
                    if (p.HasValue)
                        PositionRec(c, contentX + p.Value.X - cs.MarginLeft, contentY + p.Value.YRel - cs.MarginTop, childCb);
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
                float absorb = shrinkInline ? 0f : AbsorbMargin(flowPrev, cs);
                PositionRec(c, contentX + xoff, cy - absorb, childCb);
                cy += c.TotalHeight - absorb;
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
                if (f.Box != null)
                    PositionRec(f.Box, x + f.X, AtomicTop(f, y, ln), cb);
            y += ln.Height;
        }
    }

    /// <summary>原子盒（inline-block/img）按 vertical-align 求行内顶边 y。</summary>
    static float AtomicTop(InlineFrag f, float lineY, LineBox ln)
    {
        float boxH = f.Box!.TotalHeight;
        var va = f.Style.VerticalAlign;
        switch (va)
        {
            case "top": return lineY;
            case "bottom": return lineY + ln.Height - boxH;
            case "middle": return lineY + ln.Baseline + 0.25f * f.FontSize - boxH / 2;
            case "super": return lineY + ln.Baseline - boxH - 0.35f * f.FontSize;
            case "sub": return lineY + ln.Baseline - boxH + 0.25f * f.FontSize;
            default:
                float len = va.EndsWith('%')
                    ? ln.Height * CssValueParser.ParseFloat(va[..^1]) / 100f
                    : CssValueParser.ToPixels(va, f.FontSize);
                return lineY + ln.Baseline - boxH - (float.IsNaN(len) ? 0 : len);
        }
    }

    /// <summary>absolute 子盒：按包含块 + 偏移定位（left 优先，其次 right 反推；auto 落到包含块原点）。</summary>
    static void PlaceAbsolute(BlockBox c, CB cb)
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

        float mx = left != null ? cb.Left + left.Value
               : right != null ? cb.Left + cb.Width - right.Value - c.TotalWidth
               : cb.Left;
        float my = top != null ? cb.Top + top.Value
               : bottom != null ? cb.Top + cb.Height - bottom.Value - c.TotalHeight
               : cb.Top;

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
        if (n == 0 || box.FlexLines == null) { PlaceAbsChildren(box, cb); return; }

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

            float cursor, between;
            switch (s.JustifyContent)
            {
                case "center": cursor = lineFree / 2; between = gap; break;
                case "flex-end": case "end": cursor = lineFree; between = gap; break;
                case "space-between": cursor = 0; between = line.ItemIndices.Count > 1 ? gap + lineFree / (line.ItemIndices.Count - 1) : gap; break;
                case "space-around": between = line.ItemIndices.Count > 0 ? gap + lineFree / line.ItemIndices.Count : gap; cursor = between / 2; break;
                case "space-evenly": between = gap + lineFree / (line.ItemIndices.Count + 1); cursor = between; break;
                default: cursor = 0; between = gap; break;
            }

            // 定位该行的每个子项
            foreach (int idx in line.ItemIndices)
            {
                var c = kids[idx];
                float cf = isRow ? c.TotalHeight : c.TotalWidth;
                string align = c.Style.AlignSelf == "auto" ? s.AlignItems : c.Style.AlignSelf;
                float crossOff = align switch
                {
                    "center" => Math.Max(0, (lineCross - cf) / 2),
                    "flex-end" or "end" => Math.Max(0, lineCross - cf),
                    _ => 0, // flex-start / stretch
                };

                if (isRow)
                {
                    float mainPos = cursor;
                    if (c.Style.MarginLeftAuto && c.Style.MarginRightAuto)
                        mainPos = Math.Max(0, (mainSize - c.TotalWidth) / 2);   // 主轴 auto 边距吸收自由空间 → 居中，覆盖 justify-content
                    PositionRec(c, contentX + mainPos, contentY + crossPos + crossOff, cb);
                }
                else
                {
                    float mainPos = cursor;
                    if (c.Style.MarginTopAuto && c.Style.MarginBottomAuto)
                        mainPos = Math.Max(0, (mainSize - c.TotalHeight) / 2);
                    PositionRec(c, contentX + crossPos + crossOff, contentY + mainPos, cb);
                }

                cursor += (isRow ? c.TotalWidth : c.TotalHeight) + between;
            }

            crossPos += lineCross + crossBetween;
        }

        PlaceAbsChildren(box, cb);
    }

    static void PlaceAbsChildren(BlockBox box, CB cb)
    {
        foreach (var c in box.Children)
            if (c.Style.Position == "absolute") PlaceAbsolute(c, cb);
    }
}
