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
    public static BlockBox ToBoxTree(ComputedStyle style)
    {
        var box = new BlockBox
        {
            Style = style,
            Typeface = FontManager.Get(style.FontFamily, style.FontWeight),
        };
        if (box.IsReplaced)
        {
            box.Image = ImageLoader.Load(style.ImageSrc);
            if (box.Image != null) { box.NaturalW = box.Image.Width; box.NaturalH = box.Image.Height; }
        }
        // 纯 inline 块：把有序片段摊平成原子序列（inline-block/img 各自建盒），不走块级 Children 路径
        if (style.IsInlineFC)
        {
            box.Atoms = InlineLayout.BuildAtoms(style, c => ToBoxTree(c));
            return box;
        }
        foreach (var child in style.Children)
        {
            if (child.Display == "none") continue;
            box.Children.Add(ToBoxTree(child));
        }
        return box;
    }

    // ── 测量 ────────────────────────────────────────────────

    /// <summary>
    /// 父容器把"可用宽度"（= 父内容宽）和"包含块确定高度"（无则 NaN）传进来。
    /// width% 相对可用宽解析、height% 相对包含块确定高解析；auto 则按盒模型填充。
    /// </summary>
    public static void Measure(BlockBox box, float availWidth, float containingHeight = float.NaN)
    {
        // margin/padding 的 % 相对包含块宽（= availWidth）解析，必须在读任何盒边距之前完成。
        box.Style.ResolveBoxSizes(availWidth);
        if (box.IsReplaced) { MeasureImage(box, availWidth); return; }
        if (box.Style.Display == "table") { MeasureTable(box, availWidth); return; }
        box.ContentWidth = ResolveWidth(box.Style.Width, availWidth, box.Style);
        MeasureHeight(box, containingHeight);
    }

    /// <summary>flex 子项：强制内容宽 = 分配到的主轴尺寸，再测高。</summary>
    static void MeasureWithContentWidth(BlockBox box, float contentWidth, float containingHeight)
    {
        if (box.IsReplaced) { MeasureImage(box, contentWidth); return; }
        box.ContentWidth = contentWidth;
        MeasureHeight(box, containingHeight);
    }

    /// <summary>测量一个子盒：absolute 走出流收缩适配（shrink-to-fit），其余按块/flex 正常测。</summary>
    static void MeasureChild(BlockBox c, float availWidth, float childContainingHeight)
    {
        if (c.Style.Position == "absolute") { MeasureOutOfFlow(c, availWidth); return; }
        Measure(c, availWidth, childContainingHeight);
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
            MeasureHeight(c, float.NaN);
        }
        else Measure(c, availWidth, float.NaN);
    }

    /// <summary>已知 ContentWidth 后算 ContentHeight：解析自身 height%（相对 containingHeight），否则按内容/flex 求自然高。</summary>
    static void MeasureHeight(BlockBox box, float containingHeight)
    {
        var s = box.Style;
        float bb = s.BoxSizing == "border-box"
            ? s.PaddingTop + s.PaddingBottom + s.BorderWidthTop + s.BorderWidthBottom : 0;
        float hRes = s.Height.Resolve(containingHeight);
        float? myDefH = float.IsNaN(hRes) ? null : Math.Max(0, hRes - bb);
        float naturalH = s.Display == "flex"
            ? MeasureFlex(box, myDefH)
            : s.Display == "grid"
            ? MeasureGrid(box, myDefH)
            : MeasureBlock(box, myDefH ?? float.NaN);
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
        // 回填：格撑满行高（背景/边框铺满整行）
        for (int r = 0; r < grid.Count; r++)
            foreach (var info in grid[r])
            {
                if (info.RowSpan == 1)
                    info.Cell.ContentHeight = Math.Max(0, rowH[r] - CellVDeco(info.Cell));
                else
                {
                    float spanH = 0;
                    for (int dr = 0; dr < info.RowSpan && r + dr < grid.Count; dr++) spanH += rowH[r + dr];
                    spanH += spacing * (Math.Min(info.RowSpan, grid.Count - r) - 1);
                    info.Cell.ContentHeight = Math.Max(0, spanH - CellVDeco(info.Cell));
                }
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
        if (!w.IsPercent) return 0;
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

    static float MeasureBlock(BlockBox box, float childContainingHeight)
    {
        var s = box.Style;
        if (box.Atoms != null) return MeasureInline(box);
        float h = 0;
        if (!string.IsNullOrEmpty(s.TextContent))
        {
            using var font = new SKFont(box.Typeface, s.FontSize);
            box.WrappedLines = TextMeasurer.Wrap(s.TextContent, box.ContentWidth, font, s);
            if (s.TextOverflow == "ellipsis" && s.WhiteSpace == "nowrap" && !s.Width.IsAuto)
                box.WrappedLines = TruncateEllipsis(box.WrappedLines, box.ContentWidth, font, s.LetterSpacing);
            h += box.WrappedLines.Length * s.FontSize * s.LineHeightFactor;
        }
        foreach (var c in box.Children)
        {
            MeasureChild(c, box.ContentWidth, childContainingHeight);
            if (c.Style.Position == "absolute") continue; // 出流：不计入块高
            h += c.TotalHeight;
        }
        return h;
    }

    /// <summary>inline 块：先测原子盒（inline-block/img），再折行，行高累加为内容高。</summary>
    static float MeasureInline(BlockBox box)
    {
        foreach (var a in box.Atoms!)
        {
            if (a.Box == null) continue;
            // inline-block / 行内 img：auto 宽按内容收缩（同出流逻辑），显式宽则正常测
            MeasureOutOfFlow(a.Box, box.ContentWidth);
        }
        box.Lines = InlineLayout.BreakIntoLines(box.Atoms!, box.ContentWidth);
        float h = 0;
        foreach (var ln in box.Lines) h += ln.Height;
        return h;
    }

    /// <summary>text-overflow:ellipsis —— 单行超宽时截断并加省略号。</summary>
    static string[] TruncateEllipsis(string[] lines, float maxWidth, SKFont font, float ls)
    {
        if (lines.Length == 0 || maxWidth <= 0) return lines;
        var line = lines[0];
        if (TextMeasurer.LineWidth(line, font, ls) <= maxWidth) return lines;
        const string ell = "…";
        float ellW = TextMeasurer.LineWidth(ell, font, ls);
        int keep = line.Length;
        while (keep > 0 && TextMeasurer.LineWidth(line[..keep], font, ls) + ellW > maxWidth) keep--;
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
                    MeasureWithContentWidth(c, box.ContentWidth, childCH);
                    c.ContentHeight = lineFinal[i];
                    lineCrossMax = Math.Max(lineCrossMax, c.TotalWidth);
                }
            }

            line.MainSize = isRow
                ? gap * (line.ItemIndices.Count - 1) + lineFinal.Sum()
                : lineFinal.Sum() + gap * (line.ItemIndices.Count - 1);
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

    /// <summary>子项主轴 base：flex-basis（%相对主轴可用）> 显式 width/height > 内在尺寸。</summary>
    static float FlexBase(BlockBox c, bool isRow, float mainAvail, float? containerDefH)
    {
        var s = c.Style;
        if (!s.FlexBasis.IsAuto)
        {
            float r = s.FlexBasis.Resolve(mainAvail);
            if (!float.IsNaN(r)) return r;
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
        if (s.Width.IsAbsolute) return s.Width.Absolute;
        if (s.FlexBasis.IsAbsolute) return s.FlexBasis.Absolute;
        if (c.IsReplaced) return c.NaturalW;
        // inline FC：单行内在宽 = 各原子内在宽之和（文本 run 用字体度量，原子盒递归收缩）
        if (c.Atoms != null)
        {
            float w = 0;
            foreach (var a in c.Atoms)
            {
                if (a.IsBreak) continue;
                if (a.Box != null) w += MainDeco(a.Box, true) + (a.Box.IsReplaced ? a.Box.NaturalW : IntrinsicContentWidth(a.Box));
                else if (!string.IsNullOrEmpty(a.Text))
                {
                    using var f = new SKFont(FontManager.Get(a.Style.FontFamily, a.Style.FontWeight), a.Style.FontSize);
                    w += TextMeasurer.NaturalWidth(a.Text!, f, a.Style);
                }
            }
            return w;
        }
        if (!string.IsNullOrEmpty(s.TextContent))
        {
            using var font = new SKFont(c.Typeface, s.FontSize);
            return TextMeasurer.NaturalWidth(s.TextContent, font, s);
        }
        float ww = 0;
        foreach (var g in c.Children) ww += IntrinsicContentWidth(g) + MainDeco(g, true);
        return ww;
    }

    // ── Grid ────────────────────────────────────────────────

    /// <summary>
    /// Grid 测量：解析轨道定义 → 按 grid-column/row 放置子项（无显式位置则自动放置）→
    /// 计算轨道宽/高（固定尺寸或 auto）→ 按轨道尺寸测子项 → 返回网格总高。
    /// </summary>
    static float MeasureGrid(BlockBox box, float? myDefH)
    {
        var s = box.Style;
        float colGap = s.ColumnGap.Resolve(box.ContentWidth);
        float rowGap = s.RowGap.Resolve(box.ContentWidth);
        if (float.IsNaN(colGap)) colGap = 0;
        if (float.IsNaN(rowGap)) rowGap = 0;

        // 解析轨道定义
        var colTracks = ParseGridTracks(s.GridTemplateColumns, box.ContentWidth);
        var rowTracks = ParseGridTracks(s.GridTemplateRows, float.NaN);

        // 如果未定义轨道，按子项数量自动创建列/行
        if (colTracks.Count == 0)
        {
            int n = box.Children.Count;
            if (n > 0)
            {
                float autoW = box.ContentWidth / n - colGap * (n - 1) / n;
                for (int i = 0; i < n; i++) colTracks.Add(autoW);
            }
        }
        if (rowTracks.Count == 0)
        {
            // 行数由子项放置决定，先假设每子项一行
            rowTracks.Add(float.NaN); // auto 高度，后面根据内容定
        }

        // 放置子项到网格
        var placements = new List<(int childIdx, int col, int row, int colSpan, int rowSpan)>();
        var occupied = new HashSet<(int col, int row)>();
        int autoCol = 0, autoRow = 0;

        for (int i = 0; i < box.Children.Count; i++)
        {
            var cs = box.Children[i].Style;
            int col, row, colSpan, rowSpan;

            if (cs.GridColumnStart > 0 && cs.GridRowStart > 0)
            {
                col = cs.GridColumnStart - 1;
                row = cs.GridRowStart - 1;
                colSpan = cs.GridColumnEnd > 0 ? cs.GridColumnEnd - cs.GridColumnStart : cs.GridColumnSpan;
                rowSpan = cs.GridRowEnd > 0 ? cs.GridRowEnd - cs.GridRowStart : cs.GridRowSpan;
            }
            else
            {
                // 自动放置：找下一个空位
                col = autoCol;
                row = autoRow;
                colSpan = cs.GridColumnSpan;
                rowSpan = cs.GridRowSpan;

                // 确保不超出轨道数，必要时扩展
                while (col + colSpan > colTracks.Count)
                    colTracks.Add(float.NaN); // auto
                while (row + rowSpan > rowTracks.Count)
                    rowTracks.Add(float.NaN);
            }

            // 标记占用
            for (int dc = 0; dc < colSpan; dc++)
                for (int dr = 0; dr < rowSpan; dr++)
                    occupied.Add((col + dc, row + dr));

            placements.Add((i, col, row, colSpan, rowSpan));

            // 推进自动放置位置
            autoCol = col + colSpan;
            if (autoCol >= colTracks.Count)
            {
                autoCol = 0;
                autoRow++;
            }
        }

        // 确保轨道数足够
        while (rowTracks.Count <= (placements.Count > 0 ? placements.Max(p => p.row + p.rowSpan) : 0))
            rowTracks.Add(float.NaN);

        // 计算列宽：固定尺寸直接用，auto 按内容收缩
        float[] colWidths = new float[colTracks.Count];
        for (int c = 0; c < colTracks.Count; c++)
        {
            if (!float.IsNaN(colTracks[c]))
                colWidths[c] = colTracks[c];
            else
            {
                // auto：找该列所有子项的最大内容宽
                float maxW = 0;
                foreach (var p in placements)
                {
                    if (p.col == c && p.colSpan == 1)
                    {
                        var child = box.Children[p.childIdx];
                        MeasureChild(child, box.ContentWidth, myDefH ?? float.NaN);
                        maxW = Math.Max(maxW, child.TotalWidth);
                    }
                }
                colWidths[c] = maxW;
            }
        }

        // 测量所有子项
        foreach (var p in placements)
        {
            var child = box.Children[p.childIdx];
            float cellW = 0;
            for (int dc = 0; dc < p.colSpan; dc++)
                cellW += colWidths[p.col + dc] + (dc > 0 ? colGap : 0);
            MeasureWithContentWidth(child, cellW, myDefH ?? float.NaN);
        }

        // 计算行高：固定尺寸直接用，auto 按内容最大高度
        float[] rowHeights = new float[rowTracks.Count];
        for (int r = 0; r < rowTracks.Count; r++)
        {
            if (!float.IsNaN(rowTracks[r]))
                rowHeights[r] = rowTracks[r];
            else
            {
                float maxH = 0;
                foreach (var p in placements)
                {
                    if (p.row == r && p.rowSpan == 1)
                    {
                        var child = box.Children[p.childIdx];
                        maxH = Math.Max(maxH, child.TotalHeight);
                    }
                }
                rowHeights[r] = maxH;
            }
        }

        // 存储网格信息供定位用
        box.GridInfo = new GridInfo
        {
            ColWidths = colWidths,
            RowHeights = rowHeights,
            ColGap = colGap,
            RowGap = rowGap,
            Placements = placements
        };

        // 返回网格总高
        float totalH = 0;
        for (int r = 0; r < rowHeights.Length; r++)
            totalH += rowHeights[r] + (r > 0 ? rowGap : 0);
        return totalH;
    }

    static List<float> ParseGridTracks(string def, float availSize)
    {
        var tracks = new List<float>();
        if (string.IsNullOrWhiteSpace(def)) return tracks;

        var parts = def.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part == "auto")
                tracks.Add(float.NaN); // auto 标记
            else
            {
                float px = CssValueParser.ToPixels(part, availSize);
                tracks.Add(float.IsNaN(px) ? float.NaN : px);
            }
        }
        return tracks;
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
        float contentY = by + s.BorderWidthTop + s.PaddingTop;
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
        else if (s.Display == "flex")
        {
            PositionFlex(box, contentX, contentY, childCb);
        }
        else if (s.Display == "grid")
        {
            PositionGrid(box, contentX, contentY, childCb);
        }
        else if (box.Lines != null)
        {
            PositionInline(box, contentX, contentY, childCb);
        }
        else
        {
            // 块级子元素布局：处理浮动
            float cy = contentY;
            var leftFloats = new List<(float top, float bottom, float right)>();
            var rightFloats = new List<(float top, float bottom, float left)>();

            foreach (var c in box.Children)
            {
                if (c.Style.Position == "absolute") { PlaceAbsolute(c, childCb); continue; }

                // 处理 clear
                if (c.Style.Clear != "none")
                {
                    if (c.Style.Clear == "left" || c.Style.Clear == "both")
                        cy = Math.Max(cy, leftFloats.Where(f => f.bottom > cy).Select(f => f.bottom).DefaultIfEmpty(cy).Max());
                    if (c.Style.Clear == "right" || c.Style.Clear == "both")
                        cy = Math.Max(cy, rightFloats.Where(f => f.bottom > cy).Select(f => f.bottom).DefaultIfEmpty(cy).Max());
                }

                // 浮动元素
                if (c.Style.Float != "none")
                {
                    PositionRec(c, contentX, cy, childCb);
                    float cTop = cy;
                    float cBottom = cy + c.TotalHeight;

                    if (c.Style.Float == "left")
                    {
                        leftFloats.Add((cTop, cBottom, c.BorderX + c.TotalWidth - c.Style.MarginRight));
                        // 浮动元素不参与后续流式布局的 cy 推进
                    }
                    else if (c.Style.Float == "right")
                    {
                        float floatX = contentX + box.ContentWidth - c.TotalWidth;
                        PositionRec(c, floatX, cy, childCb);
                        rightFloats.Add((cTop, cBottom, c.BorderX - c.Style.MarginLeft));
                    }
                    continue;
                }

                // 非浮动元素：计算可用 x 和宽度
                float availX = contentX;
                float availW = box.ContentWidth;

                // 检查左侧浮动
                var activeLeft = leftFloats.Where(f => f.top < cy + c.TotalHeight && f.bottom > cy).ToList();
                if (activeLeft.Any())
                {
                    float maxRight = activeLeft.Max(f => f.right);
                    availX = Math.Max(availX, maxRight);
                    availW -= (availX - contentX);
                }

                // 检查右侧浮动
                var activeRight = rightFloats.Where(f => f.top < cy + c.TotalHeight && f.bottom > cy).ToList();
                if (activeRight.Any())
                {
                    float minLeft = activeRight.Min(f => f.left);
                    availW -= (contentX + box.ContentWidth - minLeft);
                }

                PositionRec(c, availX, cy, childCb);
                cy += c.TotalHeight;
            }
        }
    }

    /// <summary>inline 块：按 text-align 给每行算左偏移；原子盒按基线定位（底对齐基线）。</summary>
    static void PositionInline(BlockBox box, float contentX, float contentY, CB cb)
    {
        var s = box.Style;
        float y = contentY;
        foreach (var ln in box.Lines!)
        {
            ln.Left = s.TextAlignment switch
            {
                CssTextAlign.Center => Math.Max(0, (box.ContentWidth - ln.Width) / 2),
                CssTextAlign.Right => Math.Max(0, box.ContentWidth - ln.Width),
                _ => 0,
            };
            float x = contentX + ln.Left;
            foreach (var f in ln.Frags)
                if (f.Box != null)
                    PositionRec(f.Box, x + f.X, y + ln.Baseline - f.Ascent, cb);
            y += ln.Height;
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

        // align-content 目前简化为 flex-start（默认）
        crossCursor = 0;
        crossBetween = gap;

        // 逐行定位
        float crossPos = crossCursor;
        foreach (var line in box.FlexLines)
        {
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
                    "center" => Math.Max(0, (line.CrossSize - cf) / 2),
                    "flex-end" or "end" => Math.Max(0, line.CrossSize - cf),
                    _ => 0, // flex-start / stretch
                };

                if (isRow)
                    PositionRec(c, contentX + cursor, contentY + crossPos + crossOff, cb);
                else
                    PositionRec(c, contentX + crossPos + crossOff, contentY + cursor, cb);

                cursor += (isRow ? c.TotalWidth : c.TotalHeight) + between;
            }

            crossPos += line.CrossSize + crossBetween;
        }

        PlaceAbsChildren(box, cb);
    }

    static void PlaceAbsChildren(BlockBox box, CB cb)
    {
        foreach (var c in box.Children)
            if (c.Style.Position == "absolute") PlaceAbsolute(c, cb);
    }
}
