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

        public void NewPage()
        {
            Cur = new Page
            {
                Backdrop = new DrawItem { Box = Body, X = BoxX, Y = BoxY, W = BoxW, H = BoxH, LineCount = 0 },
            };
            Pages.Add(Cur);
            CurY = ContentTop;
        }

        public void Place(BlockBox box, float availW, float x)
        {
            float h = box.TotalHeight;
            float remaining = ContentBottom - CurY;
            if (h <= remaining + 0.5f)
            {
                EmitWhole(box, x, CurY);
                CurY += h;
                return;
            }
            if (h <= UsableH + 0.5f)
            {
                NewPage();
                EmitWhole(box, x, CurY);
                CurY += h;
                return;
            }
            // 替换元素（img）不可拆分：放不下也只能整块放，超出页底裁掉。
            // 含原子盒的 inline 块、以及表格（MVP 不跨页断行）同理整块放置。
            if (box.IsReplaced || (box.IsInlineFC && box.HasInlineAtomic) || box.Style.Display == "table")
            {
                NewPage();
                EmitWhole(box, x, CurY);
                CurY += Math.Min(h, UsableH);
                return;
            }
            Fragment(box, availW, x);
        }

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
            float textY = box.BorderY + s.BorderWidthTop + s.PaddingTop;

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
            float headTop = CurY + s.MarginTop;

            // 内容从 headTop + 上边框/内边距 开始
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
                Cur.Items.Add(new DrawItem
                {
                    Box = box, X = innerX, Y = CurY, W = box.ContentWidth, H = count * lineHeight,
                    DrawDecor = false, // 装饰由 Fragment 的带负责
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
            var lines = box.Lines!;
            int i = 0;
            float y = CurY;
            while (i < lines.Count)
            {
                float remaining = ContentBottom - y, acc = 0;
                int count = 0;
                while (i + count < lines.Count && (acc + lines[i + count].Height <= remaining + 0.5f || count == 0))
                {
                    // 首行即便高于页也至少放一行（避免死循环）
                    if (count > 0 && acc + lines[i + count].Height > remaining + 0.5f) break;
                    acc += lines[i + count].Height; count++;
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

        static DrawItem Band(BlockBox box, float x, float y, float w, float h) =>
            new DrawItem { Box = box, X = x, Y = y, W = w, H = h, DrawDecor = true, LineCount = 0 };
    }
}