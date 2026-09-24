using PaperSharp.Css;

namespace PaperSharp.Layout;

/// <summary>
/// 一个绘制单元：盒子装饰（背景+边框）和/或其文本行。
/// 分页时一个跨页容器会被拆成多个 DrawItem（每页一个装饰带 + 落在该页的文本行）。
/// </summary>
public sealed class DrawItem
{
    public BlockBox Box = null!;
    public float X, Y, W, H;          // border-box 矩形（页内绝对坐标）
    public bool DrawDecor = true;     // 是否画背景+边框
    public int FirstLine, LineCount;  // 文本：从第几行起、画几行
    public float TextLeft, TextTop, TextWidth; // 文本内容区起点与宽度
    public bool HasText => LineCount > 0 && Box.WrappedLines != null;
}

public sealed class Page
{
    public DrawItem? Backdrop;        // 整页 body 背景
    public List<DrawItem> Items = new();
    public List<DrawItem> AbsItems = new(); // absolute 子树，画在 Items 之上
}

/// <summary>
/// 块级分页器。把 body 的直接子块按可用页高回流到多页：
/// - 放得下 → 当前页整块放置
/// - 当前页放不下但整页放得下 → 换页整块放置（简单孤行控制）
/// - 比整页还高 → 碎片化：文本按行拆、容器按子块递归，跨页处重复容器装饰带
/// </summary>
public static class Paginator
{
    public static List<Page> Paginate(BlockBox body, PageSpec spec)
    {
        var s = body.Style;
        // 内容区 = @page 页边距 + body 自身 border/padding（无 @page 时页边距为 0，等价旧行为）
        float padL = spec.MarginLeft + s.BorderWidthLeft + s.PaddingLeft;
        float padR = spec.MarginRight + s.BorderWidthRight + s.PaddingRight;
        float padT = spec.MarginTop + s.BorderWidthTop + s.PaddingTop;
        float padB = spec.MarginBottom + s.BorderWidthBottom + s.PaddingBottom;

        var ctx = new Ctx
        {
            Body = body,
            PageW = spec.PageW,
            PageH = spec.PageH,
            ContentTop = padT,
            ContentBottom = spec.PageH - padB,
            ContentLeft = padL,
            ContentW = spec.PageW - padL - padR,
            // body 背景/边框画在页边距框（margin box）内
            BoxX = spec.MarginLeft,
            BoxY = spec.MarginTop,
            BoxW = spec.PageW - spec.MarginLeft - spec.MarginRight,
            BoxH = spec.PageH - spec.MarginTop - spec.MarginBottom,
        };
        ctx.UsableH = ctx.ContentBottom - ctx.ContentTop;
        ctx.NewPage();

        foreach (var child in body.Children)
        {
            LayoutBuilder.Measure(child, ctx.ContentW);
            ctx.Place(child, ctx.ContentW, ctx.ContentLeft);
        }
        return ctx.Pages;
    }

    sealed class Ctx
    {
        public BlockBox Body = null!;
        public float PageW, PageH, ContentTop, ContentBottom, ContentLeft, ContentW, UsableH;
        public float BoxX, BoxY, BoxW, BoxH;   // body 背景矩形（页边距框）
        public List<Page> Pages = new();
        public Page Cur = null!;
        public float CurY;
        /// <summary>当前流里上一个落位的流内块盒，用于相邻兄弟 margin 坍缩（见 LayoutBuilder.Collapsible）。</summary>
        BlockBox? _flowPrev;

        public void NewPage()
        {
            Cur = new Page
            {
                Backdrop = new DrawItem { Box = Body, X = BoxX, Y = BoxY, W = BoxW, H = BoxH, LineCount = 0 },
            };
            Pages.Add(Cur);
            CurY = ContentTop;
            _flowPrev = null;   // 换页即断开相邻关系：上一页那块的下 margin 不参与本页首块的坍缩
        }

        public void Place(BlockBox box, float availW, float x)
        {
            var s = box.Style;
            // break-before: page → 强制换页
            if (s.BreakBefore == "page" && CurY > ContentTop + 0.5f)
                NewPage();

            // 相邻兄弟的共用 margin 只算一次。游标必须和 MeasureBlock/PositionRec 吃同一个值，
            // 否则「量出来按坍缩、翻页按不坍缩」会把多段样例凭空挤出一页（long 实测 2 → 3）。
            // 传盒而非 style：AbsorbMargin 比的是 EffTop（含首子逃逸上来的那截），见其注释。
            CurY -= LayoutBuilder.AbsorbMargin(_flowPrev, box);

            // 流高同样走 LayoutBuilder 的坍缩口径：首子顶边距被父吸收时不该占页内空间，
            // 按 TotalHeight 算会让分页版比长图版凭空多出一截、进而多切一页。
            float h = LayoutBuilder.FlowHeight(box);
            float remaining = ContentBottom - CurY;
            if (h <= remaining + 0.5f)
            {
                EmitWhole(box, x, CurY);
                CurY += h;
            }
            // 当前页放不下、但整页放得下：只有「没有可拆分的内部结构」才整块挪到下一页。
            // 能拆的（有多个子块或多行文本）就地起、页底断——Chrome printToPDF 打印 long 就是这个结果
            // （.card 在页中被拆成两段，共 2 页）；一律整块挪会把 2 页变成 3 页，还把上一页留成空页。
            else if (h <= UsableH + 0.5f && !Splittable(box))
            {
                NewPage();
                EmitWhole(box, x, CurY);
                CurY += h;
            }
            // break-inside: avoid → 不碎片化，整块放到下一页
            else if (s.BreakInside == "avoid")
            {
                NewPage();
                EmitWhole(box, x, CurY);
                CurY += Math.Min(h, UsableH);
            }
            // 替换元素（img）不可拆分：放不下也只能整块放，超出页底裁掉。
            // 含原子盒的 inline 块同理整块放置。
            else if (box.IsReplaced || (box.IsInlineFC && box.HasInlineAtomic))
            {
                NewPage();
                EmitWhole(box, x, CurY);
                CurY += Math.Min(h, UsableH);
            }
            // overflow 裁剪盒：内容超出自身 padding box 即被裁掉，绝不跨页续。
            // 放在表格分支之前——裁剪盒一旦碎片化，祖先与后代的坐标就不在同一页里，
            // BlockBox.OverflowClip 沿父链求交的前提就破了（表格若被裁，整表落一页）。
            else if (s.ClipsOverflow)
            {
                NewPage();
                EmitWhole(box, x, CurY);
                CurY += Math.Min(h, UsableH);
            }
            // 表格跨页续表：按行拆分，重复 thead/tfoot
            else if (s.Display == "table")
            {
                FlowTable(box, x);
            }
            else
            {
                Fragment(box, availW, x);
            }

            // 记下本盒作为下一个兄弟的「流内前驱」；行内级/浮动不参与坍缩，直接把链断掉。
            _flowPrev = LayoutBuilder.Collapsible(s) ? box : null;

            // break-after: page → 元素放完后强制换页
            if (s.BreakAfter == "page")
                NewPage();
        }

        /// <summary>
    /// 本盒内部有没有可下刀的接缝：至少一个子块，或至少两行文本。
    /// 没有接缝（单行文本、单个不可分单元）的盒子拆不动，只能整块挪页；有拆得动的接缝就让它碎，
    /// 具体断点交给 Fragment/FlowText（后者按 orphans/widows 收口）。
    /// </summary>
    static bool Splittable(BlockBox box)
        => box.Children.Exists(c => c.Style.Position != "absolute")
        || (box.WrappedLines?.Length ?? box.Lines?.Count ?? 0) >= 2;

    void EmitWhole(BlockBox box, float x, float y)
        {
            // 复用布局引擎定位整棵子树（block 堆叠 + flex 的 justify/align 都在 Position 里），
            // 分页这里只负责把定位好的子树遍历成 DrawItem。
            LayoutBuilder.Position(box, x, y);
            EmitWalk(box);
        }

        void EmitWalk(BlockBox box) => EmitInto(Cur.Items, box);

        void EmitInto(List<DrawItem> list, BlockBox box)
        {
            var s = box.Style;
            float bw = s.BorderWidthLeft + s.PaddingLeft + box.ContentWidth + s.PaddingRight + s.BorderWidthRight;
            float bh = s.BorderWidthTop + s.PaddingTop + box.ContentHeight + s.PaddingBottom + s.BorderWidthBottom;
            int lines = box.Lines?.Count ?? box.WrappedLines?.Length ?? 0;
            float textX = box.BorderX + s.BorderWidthLeft + s.PaddingLeft;
            float textY = box.BorderY + s.BorderWidthTop + s.PaddingTop + box.ValignDY;

            list.Add(new DrawItem
            {
                Box = box, X = box.BorderX, Y = box.BorderY, W = bw, H = bh,
                DrawDecor = true,
                FirstLine = 0, LineCount = lines,
                TextLeft = textX, TextTop = textY, TextWidth = box.ContentWidth,
            });

            // inline FC：原子盒（inline-block/行内 img）不是 Children，需各自作为绘制单元画出（已绝对定位）
            if (box.Lines != null)
                foreach (var ln in box.Lines)
                    foreach (var f in ln.Frags)
                        if (f.Box != null) EmitInto(list, f.Box);

            foreach (var ch in box.Children)
            {
                if (ch.Style.Position == "absolute") EmitInto(Cur.AbsItems, ch);
                else EmitInto(list, ch);
            }
        }

        void Fragment(BlockBox box, float availW, float x)
        {
            var s = box.Style;
            float bw = box.TotalWidth - s.MarginLeft - s.MarginRight;
            float bx = x + s.MarginLeft;
            float innerX = bx + s.BorderWidthLeft + s.PaddingLeft;
            int startPage = Pages.Count - 1;
            // 内容从 headTop + 上边框/内边距 开始（headTop 走 FlowInset：顶边距被父吸收时不再往下推）
            float headTop = CurY + LayoutBuilder.FlowInset(box);
            CurY = headTop + s.BorderWidthTop + s.PaddingTop;

            if (box.WrappedLines != null)
                FlowText(box, innerX);
            else if (box.Lines != null)
                FlowInline(box, innerX);

            foreach (var ch in box.Children)
            {
                LayoutBuilder.Measure(ch, box.ContentWidth);
                Place(ch, box.ContentWidth, innerX);
            }

            float tailBottom = CurY + s.PaddingBottom + s.BorderWidthBottom;
            int endPage = Pages.Count - 1;

            // 在各页最前面插入容器装饰带（画在内容之下）
            Pages[startPage].Items.Insert(0, Band(box, bx, headTop, bw, ContentBottom - headTop));
            if (endPage != startPage)
            {
                for (int i = startPage + 1; i < endPage; i++)
                    Pages[i].Items.Insert(0, Band(box, bx, ContentTop, bw, ContentBottom - ContentTop));
                Pages[endPage].Items.Insert(0, Band(box, bx, ContentTop, bw, Math.Min(tailBottom, ContentBottom) - ContentTop));
            }
            CurY = tailBottom;
        }

        void FlowText(BlockBox box, float innerX)
        {
            var s = box.Style;
            float lineHeight = s.FontSize * s.LineHeightFactor;
            var lines = box.WrappedLines!;
            int i = 0;
            while (i < lines.Length)
            {
                float remaining = ContentBottom - CurY;
                int fit = Math.Max(1, (int)Math.Floor(remaining / lineHeight));
                int count = Math.Min(fit, lines.Length - i);

                // 孤行寡行控制：换页前检查
                if (count < lines.Length - i)
                {
                    // Orphans：当前页底部至少 s.Orphans 行
                    if (count < s.Orphans)
                        count = lines.Length - i; // 不够就全放当前页（可能溢出）
                    else
                    {
                        // Widows：下一页顶部至少 s.Widows 行
                        int remainingLines = lines.Length - i - count;
                        if (remainingLines < s.Widows && count > s.Orphans)
                            count = Math.Max(s.Orphans, lines.Length - i - s.Widows);
                    }
                }

                Cur.Items.Add(new DrawItem
                {
                    Box = box, X = innerX, Y = CurY, W = box.ContentWidth, H = count * lineHeight,
                    DrawDecor = false,
                    FirstLine = i, LineCount = count,
                    TextLeft = innerX, TextTop = CurY, TextWidth = box.ContentWidth,
                });
                i += count;
                CurY += count * lineHeight;
                if (i < lines.Length) NewPage();
            }
        }

        /// <summary>纯文本 inline 块按行盒拆分跨页：逐行累加，放不下当前行则换页（行高可变，含原子盒的块不走这里）。</summary>
        void FlowInline(BlockBox box, float innerX)
        {
            var s = box.Style;
            var lines = box.Lines!;
            int i = 0;
            float y = CurY;
            while (i < lines.Count)
            {
                float remaining = ContentBottom - y, acc = 0;
                int count = 0;
                while (i + count < lines.Count && (acc + lines[i + count].Height <= remaining + 0.5f || count == 0))
                {
                    if (count > 0 && acc + lines[i + count].Height > remaining + 0.5f) break;
                    acc += lines[i + count].Height; count++;
                }

                if (i + count < lines.Count)
                {
                    if (count < s.Orphans)
                    {
                        while (i + count < lines.Count)
                        { acc += lines[i + count].Height; count++; }
                    }
                    else
                    {
                        int remainingLines = lines.Count - i - count;
                        if (remainingLines < s.Widows && count > s.Orphans)
                        {
                            while (count > s.Orphans && lines.Count - i - count < s.Widows)
                            { count--; acc -= lines[i + count].Height; }
                        }
                    }
                }

                Cur.Items.Add(new DrawItem
                {
                    Box = box, X = innerX, Y = y, W = box.ContentWidth, H = acc,
                    DrawDecor = false,
                    FirstLine = i, LineCount = count,
                    TextLeft = innerX, TextTop = y, TextWidth = box.ContentWidth,
                });
                i += count; y += acc;
                if (i < lines.Count) { NewPage(); y = CurY; }
            }
            CurY = y;
        }

        /// <summary>表格跨页续表：按行拆分，每页重复 thead/tfoot，共享列宽。</summary>
        void FlowTable(BlockBox table, float x)
        {
            if (table.TRows == null || table.TColW == null || table.TRowH == null)
            {
                EmitWhole(table, x, CurY);
                CurY += table.TotalHeight;
                return;
            }

            var s = table.Style;
            float spacing = table.TSpacing;
            int numRows = table.TRows.Count;
            var headerRows = table.THeaderRows;
            var footerRows = table.TFooterRows;
            float headerH = table.THeaderHeight;
            float footerH = table.TFooterHeight;

            // 表格装饰（border/padding）
            float bw = table.TotalWidth - s.MarginLeft - s.MarginRight;
            float bx = x + s.MarginLeft;
            float innerX = bx + s.BorderWidthLeft + s.PaddingLeft;
            float topDeco = s.BorderWidthTop + s.PaddingTop;
            float bottomDeco = s.PaddingBottom + s.BorderWidthBottom;

            // 识别 body 行（非 header/footer）
            var bodyRows = new List<int>();
            var headerSet = new HashSet<int>(headerRows);
            var footerSet = new HashSet<int>(footerRows);
            for (int r = 0; r < numRows; r++)
                if (!headerSet.Contains(r) && !footerSet.Contains(r))
                    bodyRows.Add(r);

            int bodyIdx = 0;
            bool firstPage = true;

            while (bodyIdx < bodyRows.Count)
            {
                if (!firstPage) NewPage();
                firstPage = false;

                float y = CurY;
                // 首页加表格上装饰
                if (bodyIdx == 0) y += topDeco;

                // 计算可用高度
                float available = ContentBottom - y;
                // 非首页需要预留 thead 高度
                float usedH = 0;
                var pageRows = new List<int>();

                // thead 行
                foreach (var hr in headerRows) { pageRows.Add(hr); usedH += table.TRowH[hr] + spacing; }
                // tfoot 行（每页都要放）
                float footerWithSpacing = footerH;
                if (footerRows.Count > 0) footerWithSpacing = footerH + spacing;

                // body 行：尽量多放
                while (bodyIdx < bodyRows.Count)
                {
                    int r = bodyRows[bodyIdx];
                    float rowH = table.TRowH[r] + spacing;
                    // 检查是否能放下：已用 + 本行 + tfoot + 底装饰
                    float needed = usedH + rowH + footerWithSpacing + bottomDeco;
                    if (needed > available + 0.5f && pageRows.Count > headerRows.Count)
                        break; // 放不下且已有至少一个 body 行
                    pageRows.Add(r);
                    usedH += rowH;
                    bodyIdx++;
                }

                // tfoot 行
                foreach (var fr in footerRows) { pageRows.Add(fr); usedH += table.TRowH[fr] + spacing; }

                // 定位并绘制这些行
                float gridStartY = y;
                float totalUsed = LayoutBuilder.PositionTableRows(table, innerX, gridStartY, pageRows);

                // 绘制表格装饰带
                float bandTop = bodyIdx == bodyRows.Count + (bodyIdx == 0 ? 0 : 0) && !firstPage
                    ? ContentTop : (bodyIdx <= bodyRows.Count && firstPage ? CurY : ContentTop);
                float bandBottom = gridStartY + totalUsed + bottomDeco;
                if (bandBottom > ContentBottom) bandBottom = ContentBottom;

                // 表格装饰：首页从 CurY 开始，后续页从 ContentTop 开始
                float decoTop = firstPage ? CurY + s.MarginTop : ContentTop;
                Cur.Items.Insert(0, Band(table, bx, decoTop, bw, bandBottom - decoTop));

                // 绘制行内容
                foreach (var r in pageRows)
                    EmitTableRow(table, r, innerX);

                CurY = bandBottom;
                if (bodyIdx < bodyRows.Count) CurY = ContentBottom; // 当前页满
            }

            if (bodyRows.Count == 0)
            {
                // 只有 header/footer 的表格，整块放置
                EmitWhole(table, x, CurY);
                CurY += table.TotalHeight;
            }
        }

        void EmitTableRow(BlockBox table, int rowIdx, float innerX)
        {
            if (table.TRows == null || rowIdx >= table.TRows.Count) return;
            // 行盒背景
            var rowBox = table.TRowBoxes != null && rowIdx < table.TRowBoxes.Count ? table.TRowBoxes[rowIdx] : null;
            if (rowBox != null) EmitInto(Cur.Items, rowBox);
            // 各单元格
            foreach (var info in table.TRows[rowIdx])
                EmitInto(Cur.Items, info.Cell);
        }

        static DrawItem Band(BlockBox box, float x, float y, float w, float h) =>
            new DrawItem { Box = box, X = x, Y = y, W = w, H = h, DrawDecor = true, LineCount = 0 };
    }
}