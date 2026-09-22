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
    /// 表格测量：穿透 row-group 收集行 → 按 colspan 建网格 → 列宽用「单跨单元格 max-content」定基准、
    /// 多跨不足则均摊 → 显式 width 或超出可用宽时按比例缩放列 → 逐格测高、行高取格最大值、格撑满行高。
    /// 结果（列宽/行高/单元格几何）存回 box 供 PositionTable 复用。不支持 rowspan（跨行）。
    /// </summary>
    static void MeasureTable(BlockBox box, float availWidth)
    {
        var s = box.Style;
        bool collapse = s.BorderCollapse == "collapse";
        float spacing = collapse ? 0 : s.BorderSpacing;

        var rowBoxes = new List<List<BlockBox>>();
        CollectRows(box, rowBoxes);
        // 一次性收集行盒（与 rowBoxes 同序、同数量），供 PositionTable 按下标取，避免每行重遍历整棵表树（O(行²)）
        var rowBoxList = new List<BlockBox>();
        CollectRowBoxes(box, rowBoxList);

        // 每行的 cell + span，并求列数
        var grid = new List<List<TCellInfo>>();
        int numCols = 0;
        foreach (var row in rowBoxes)
        {
            var infos = new List<TCellInfo>();
            int c = 0;
            foreach (var cell in row)
            {
                if (cell.Style.Display != "table-cell") continue;
                int span = Math.Max(1, cell.Style.Colspan);
                infos.Add(new TCellInfo { Cell = cell, Col = c, Span = span });
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

        // 列宽（单元格 outer 盒宽）；+1px 余量避免「内容宽恰好等于内在宽」触发亚像素折行
        float[] colW = new float[numCols];
        for (int r = 0; r < grid.Count; r++)
        {
            foreach (var info in grid[r])
                if (info.Span == 1)
                    colW[info.Col] = Math.Max(colW[info.Col], IntrinsicContentWidth(info.Cell) + CellHDeco(info.Cell) + 1);
        }
        // 多跨：不足则把差额均摊到所跨各列
        foreach (var infos in grid)
            foreach (var info in infos)
                if (info.Span > 1)
                {
                    float need = IntrinsicContentWidth(info.Cell) + CellHDeco(info.Cell) + spacing * (info.Span - 1) + 1;
                    float have = 0; for (int j = info.Col; j < info.Col + info.Span; j++) have += colW[j];
                    float deficit = need - have;
                    if (deficit > 0) { float add = deficit / info.Span; for (int j = info.Col; j < info.Col + info.Span; j++) colW[j] += add; }
                }

        // 目标总宽：显式 width 优先；否则自然宽夹到可用宽
        float padBorder = s.PaddingLeft + s.PaddingRight + s.BorderWidthLeft + s.BorderWidthRight;
        float gridW = 0; for (int j = 0; j < numCols; j++) gridW += colW[j];
        float gapW = spacing * (numCols - 1);
        float wRes = s.Width.Resolve(availWidth);
        float targetTotal = !float.IsNaN(wRes) ? wRes
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
            // 回填：格撑满行高（背景/边框铺满整行）
            foreach (var info in grid[r])
                info.Cell.ContentHeight = Math.Max(0, rh - CellVDeco(info.Cell));
            totalH += rh;
        }
        totalH += spacing * (grid.Count - 1);
        box.ContentHeight = totalH;

        // 存回供定位
        box.TRows = grid; box.TColW = colW; box.TRowH = rowH; box.TSpacing = spacing; box.TNumCols = numCols;
        box.TRowBoxes = rowBoxList;
    }

    /// <summary>表格定位：把网格按列宽/行高铺开，逐格 PositionRec；行盒撑满整行宽以让行背景可见。</summary>
    static void PositionTable(BlockBox box, float contentX, float contentY, CB cb)
    {
        if (box.TRows == null || box.TColW == null || box.TRowH == null) return;
        var s = box.Style;
        float spacing = box.TSpacing;
        float gridW = box.ContentWidth;

        // 行盒（table-row）几何：横跨整表宽、纵占行高，供行背景/边框绘制
        float y = contentY;
        for (int r = 0; r < box.TRows.Count; r++)
        {
            var rowBox = box.TRowBoxes != null && r < box.TRowBoxes.Count ? box.TRowBoxes[r] : null;
            if (rowBox != null)
            {
                rowBox.BorderX = contentX; rowBox.BorderY = y;
                rowBox.ContentWidth = gridW; rowBox.ContentHeight = box.TRowH[r];
            }
            float x = contentX;
            foreach (var info in box.TRows[r])
            {
                float cellOuterW = spacing * (info.Span - 1);
                for (int j = info.Col; j < info.Col + info.Span; j++) cellOuterW += box.TColW[j];
                PositionRec(info.Cell, x, y, cb);
                x += cellOuterW + spacing;
            }
            y += box.TRowH[r] + spacing;
        }
    }

    static void CollectRows(BlockBox table, List<List<BlockBox>> rows)
    {
        foreach (var ch in table.Children)
        {
            var d = ch.Style.Display;
            if (d == "table-row") rows.Add(ch.Children);
            else if (d is "table-row-group" or "table-header-group" or "table-footer-group") CollectRows(ch, rows);
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
                if (r < lastRow) b.BorderWidthBottom = 0;
            }
    }

    static float CellHDeco(BlockBox c) => c.Style.PaddingLeft + c.Style.PaddingRight + c.Style.BorderWidthLeft + c.Style.BorderWidthRight;
    static float CellVDeco(BlockBox c) => c.Style.PaddingTop + c.Style.PaddingBottom + c.Style.BorderWidthTop + c.Style.BorderWidthBottom;

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

        float[] finalMain = (float[])baseMain.Clone();
        if (!float.IsNaN(mainAvail))
        {
            float used = gap * (n - 1);
            for (int i = 0; i < n; i++) used += baseMain[i] + decoMain[i];
            float free = mainAvail - used;
            if (free > 0.5f)
            {
                float totalGrow = 0; for (int i = 0; i < n; i++) totalGrow += kids[i].Style.FlexGrow;
                if (totalGrow > 0)
                    for (int i = 0; i < n; i++) finalMain[i] = baseMain[i] + free * kids[i].Style.FlexGrow / totalGrow;
            }
            else if (free < -0.5f)
            {
                float sw = 0; for (int i = 0; i < n; i++) sw += kids[i].Style.FlexShrink * Math.Max(1, baseMain[i]);
                if (sw > 0)
                    for (int i = 0; i < n; i++)
                        finalMain[i] = Math.Max(0, baseMain[i] + free * kids[i].Style.FlexShrink * Math.Max(1, baseMain[i]) / sw);
            }
        }

        // 主轴尺寸夹到 min/max（ResolveWidth 只覆盖块路径，flex 项这里补上）
        for (int i = 0; i < n; i++)
        {
            var ms = kids[i].Style;
            finalMain[i] = isRow
                ? ClampSize(finalMain[i], ms.MinWidth, ms.MaxWidth, mainAvail)
                : ClampSize(finalMain[i], ms.MinHeight, ms.MaxHeight, myDefH ?? float.NaN);
        }

        bool stretchDefault = s.AlignItems == "stretch";
        float crossMax = 0;
        for (int i = 0; i < n; i++)
        {
            var c = kids[i];
            if (isRow)
            {
                MeasureWithContentWidth(c, finalMain[i], childCH); // 宽=主轴，高=内容
                bool stretch = c.Style.AlignSelf == "stretch" || (c.Style.AlignSelf == "auto" && stretchDefault);
                if (stretch && !float.IsNaN(crossAvail) && c.Style.Height.IsAuto)
                    c.ContentHeight = Math.Max(0, crossAvail - MainDeco(c, false));
                crossMax = Math.Max(crossMax, c.TotalHeight);
            }
            else
            {
                MeasureWithContentWidth(c, box.ContentWidth, childCH); // 宽=交叉(满)，高=内容
                c.ContentHeight = finalMain[i];                        // 主轴=高，覆盖为分配值
                crossMax = Math.Max(crossMax, c.TotalWidth);
            }
        }

        if (isRow) return crossMax;
        float sum = gap * (n - 1);
        for (int i = 0; i < n; i++) sum += finalMain[i] + decoMain[i];
        return sum;
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
        else if (box.Lines != null)
        {
            PositionInline(box, contentX, contentY, childCb);
        }
        else
        {
            float cy = contentY;
            foreach (var c in box.Children)
            {
                if (c.Style.Position == "absolute") { PlaceAbsolute(c, childCb); continue; }
                PositionRec(c, contentX, cy, childCb);
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
        float gap = ResolveGap(s, box.ContentWidth);
        var kids = box.Children.FindAll(c => c.Style.Position != "absolute");
        int n = kids.Count;
        if (n == 0) { PlaceAbsChildren(box, cb); return; }

        float mainSize = isRow ? box.ContentWidth : box.ContentHeight;
        float crossSize = isRow ? box.ContentHeight : box.ContentWidth;

        float used = gap * (n - 1);
        foreach (var c in kids) used += isRow ? c.TotalWidth : c.TotalHeight;
        float free = mainSize - used;

        float cursor, between;
        switch (s.JustifyContent)
        {
            case "center": cursor = free / 2; between = gap; break;
            case "flex-end": case "end": cursor = free; between = gap; break;
            case "space-between": cursor = 0; between = n > 1 ? gap + free / (n - 1) : gap; break;
            case "space-around": between = n > 0 ? gap + free / n : gap; cursor = between / 2; break;
            case "space-evenly": between = gap + free / (n + 1); cursor = between; break;
            default: cursor = 0; between = gap; break;
        }

        foreach (var c in kids)
        {
            float cf = isRow ? c.TotalHeight : c.TotalWidth;
            string align = c.Style.AlignSelf == "auto" ? s.AlignItems : c.Style.AlignSelf;
            float crossOff = align switch
            {
                "center" => Math.Max(0, (crossSize - cf) / 2),
                "flex-end" or "end" => Math.Max(0, crossSize - cf),
                _ => 0, // flex-start / stretch
            };

            if (isRow)
                PositionRec(c, contentX + cursor, contentY + crossOff, cb);
            else
                PositionRec(c, contentX + crossOff, contentY + cursor, cb);

            cursor += (isRow ? c.TotalWidth : c.TotalHeight) + between;
        }

        PlaceAbsChildren(box, cb);
    }

    static void PlaceAbsChildren(BlockBox box, CB cb)
    {
        foreach (var c in box.Children)
            if (c.Style.Position == "absolute") PlaceAbsolute(c, cb);
    }
}
