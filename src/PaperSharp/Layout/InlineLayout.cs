using SkiaSharp;
using PaperSharp.Css;

namespace PaperSharp.Layout;

/// <summary>inline 序列里的一个原子：文本 run / 换行 &lt;br&gt; / 原子盒（inline-block、行内 img）。</summary>
public sealed class InlineAtom
{
    public ComputedStyle Style = null!;   // 文本 run 的样式（字体/色/背景/装饰/字距）
    public string? Text;
    public BlockBox? Box;                 // 原子盒（inline-block / img）
    public bool IsBreak;
    public bool HostText;                 // 文字来自建立 IFC 的块自身（其 padding 已在块内容盒里，勿重复计）

    /// <summary>这个原素的横向外扩 = 行内元素自己的 padding+border；块自身的文字不算（已在内容盒原点里）。
    /// 内在宽与真实折行必须走同一个口子，否则两条路对行宽的判断会分叉。</summary>
    public (float Left, float Right) HDeco(float cbW) => HostText ? (0f, 0f) : InlineLayout.HDeco(Style, cbW);
}

/// <summary>折行后落在某行上的一段：文本（带样式）或原子盒。X 是相对行内容起点的偏移。
/// 文本片段的 X/Width 含元素自身的横向 padding+border（PadLeft/PadRight 是其左右两半），
/// 与浏览器行内盒的 padding box 同口径；文字画在 X+PadLeft。</summary>
public sealed class InlineFrag
{
    public ComputedStyle Style = null!;
    public string? Text;
    public BlockBox? Box;
    public float X, Width, FontSize, Ascent, Descent;
    public float PadLeft, PadRight;   // 仅文本片段：盒相对文字的左右外扩（padding+border）
    public bool HostText;             // 块自身文字：底色已由块盒铺过，绘制端不得再按行补背景（见 InlineAtom.HostText）
    public float AlignDY;   // vertical-align 生效后的基线下移量（文本 run 与原子盒同一约定，见 AlignShift）
}

/// <summary>一行：若干 frag + 行高/基线/宽度。Left 为对齐后的 x 偏移（相对内容盒）。
/// Inset/Avail：环绕浮动时该行的左缩进与可用宽（无浮动时 0/-1 表示全宽）。
/// ForcedBreak：这一行是被 &lt;br&gt;/源码换行断开的，不是排满自动折的——CSS 视同段末行，不拉伸。</summary>
public sealed class LineBox
{
    public List<InlineFrag> Frags = new();
    public float Width, Height, Baseline, Left;
    public float Inset;
    public float Avail = -1;
    public bool ForcedBreak;
}
/// <summary>
/// inline 格式化上下文：把块内的有序片段摊平成原子序列，再按宽度折成行盒。
/// 纯文本块不走这里（沿用 WrappedLines 老路径），只有混排行内内容才触发。
/// </summary>
public static class InlineLayout
{
    /// <summary>从某块的 InlinePieces 递归摊平成原子序列。factory 用于给 inline-block/img 建盒。
    /// <paramref name="hostLevel"/> 只在最外层为真：那层的文字属于建立 IFC 的块**自身**，
    /// 块自己的 padding 已经算进内容盒原点，绝不能再当行内元素的横向外扩加第二遍。</summary>
    public static List<InlineAtom> BuildAtoms(ComputedStyle style, Func<ComputedStyle, BlockBox> factory, bool hostLevel = true)
    {
        var list = new List<InlineAtom>();
        foreach (var piece in style.InlinePieces)
        {
            if (piece.IsBreak) { list.Add(new InlineAtom { Style = style, IsBreak = true, HostText = hostLevel }); continue; }
            if (piece.Text != null)
            {
                string t = ApplyTransform(piece.Text, style.TextTransform);
                if (!string.IsNullOrEmpty(t)) list.Add(new InlineAtom { Style = style, Text = t, HostText = hostLevel });
                continue;
            }
            var c = piece.Child;
            if (c == null || c.Display == "none") continue;
            if (c.Display is "inline-block" or "inline-flex" or "inline-grid" || c.Tag is "img" or "svg")
                list.Add(new InlineAtom { Style = c, Box = factory(c) });
            else
                list.AddRange(BuildAtoms(c, factory, false));   // 普通 inline：递归摊平
        }
        return list;
    }

    /// <summary>按 maxWidth 把原子序列折成行盒。fontCache 复用 SKFont 度量。</summary>
    public static List<LineBox> BreakIntoLines(List<InlineAtom> atoms, float maxWidth)
        => BreakIntoLines(atoms, maxWidth, null, 0, null);

    /// <summary>带浮动环绕的折行：每行起笔时按该行 y 查询左右缩进，可用宽相应收窄（fc=null 时与旧行为一致）。
    /// strut = 建立该 IFC 的块自身样式（字体/行高决定行盒在基线下方留多高），null 时退回首个片段的样式。</summary>
    public static List<LineBox> BreakIntoLines(List<InlineAtom> atoms, float maxWidth, FloatContext? fc, float topY, ComputedStyle? strut = null)
    {
        var lines = BreakRaw(atoms, maxWidth, fc, topY, strut);
        // text-wrap: balance：按“总宽/最少行数”目标宽二次折行，逼近各行等长（有浮动环绕时跳过）
        if (fc == null && atoms.Count > 0 && atoms[0].Style.TextWrap == "balance"
            && maxWidth > 0 && lines.Count >= 2)
        {
            float total = lines.Sum(l => l.Width) + (lines.Count - 1) * TextWidth(" ", atoms[0].Style);
            // 二分最小的“行长上限”W：折行仍保持原行数（=最少行数），各行尽量均匀
            float lo = total / lines.Count - 0.5f, hi = maxWidth;
            for (int iter = 0; iter < 8 && hi - lo > 1f; iter++)
            {
                float mid = (lo + hi) / 2;
                if (BreakRaw(atoms, mid, null, topY, strut).Count <= lines.Count) hi = mid;
                else lo = mid;
            }
            var rebal = BreakRaw(atoms, hi, null, topY, strut);
            if (rebal.Count <= lines.Count) lines = rebal;
        }
        ApplyJustify(lines, atoms, maxWidth, strut);
        return lines;
    }

    /// <summary>
    /// text-align:justify / text-align-last:justify：把每行剩余空间平分给行内空隙。
    /// 段末行（以及被 &lt;br&gt;/源码换行断开的行）不拉伸，除非 text-align-last:justify。
    /// </summary>
    static void ApplyJustify(List<LineBox> lines, List<InlineAtom> atoms, float maxWidth, ComputedStyle? strut)
    {
        var aln = strut ?? (atoms.Count > 0 ? atoms[0].Style : null);
        if (aln == null || lines.Count == 0) return;
        bool any = aln.TextAlignment == CssTextAlign.Justify || aln.TextAlignLast == "justify";
        if (!any) return;
        for (int i = 0; i < lines.Count; i++)
        {
            bool isLast = i == lines.Count - 1 || lines[i].ForcedBreak;
            if (isLast ? aln.TextAlignLast != "justify" : aln.TextAlignment != CssTextAlign.Justify) continue;
            float avail = lines[i].Avail > 0 ? lines[i].Avail : maxWidth;
            JustifyLine(lines[i], avail);
        }
    }

    /// <summary>
    /// 拉伸一行：先按 <see cref="Tokenize"/> 把每个 frag 的文本拆回 token（词 / 单空格 / 单 CJK 字），
    /// 再给每个「扩展机会」平分剩余空间——空格本身是一个机会，CJK 字两侧的词界同样算机会
    /// （Chrome 对中文段落 justify 就是逐字拉开的，只按空格分会让整段中文一行都拉不动）。
    /// 每个 token 变成一个子 frag，Width 含它自己那份间隙，故栅格/SVG/PDF 各绘制端照旧按 f.X 落字，
    /// 行内背景与下划线也仍连成一片（Chrome 正是如此：空隙被拉宽但底色不断）。
    /// </summary>
    static void JustifyLine(LineBox ln, float avail)
    {
        if (ln.Frags.Count == 0) return;
        float extra = avail - ln.Width;
        if (extra <= 0.5f) return;

        var units = new List<(InlineFrag F, string Tok, float W, bool Space, bool Cjk, bool First, bool Last)>();
        foreach (var f in ln.Frags)
        {
            if (f.Text == null) { units.Add((f, f.Text!, f.Width, false, false, true, true)); continue; }
            int n = 0;
            foreach (var tok in Tokenize(f.Text, f.Style.WhiteSpace, f.Style.WordBreak))
            {
                bool first = n == 0; n++;
                units.Add((f, tok, TextWidth(tok, f.Style), tok == " ",
                    tok.Length == 1 && TextMeasurer.IsCjkChar(tok[0]), first, false));
            }
            if (n > 0) { var (F, T, W, S, C, Fi, _) = units[^1]; units[^1] = (F, T, W, S, C, Fi, true); }
        }

        int spots = 0;
        var gapBefore = new bool[units.Count];
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i].Space) { spots++; continue; }
            if (i == 0) continue;                       // 行首不留间隙
            if (units[i].Cjk || units[i - 1].Cjk) { gapBefore[i] = true; spots++; }
        }
        if (spots == 0) return;
        float add = extra / spots;

        var outFrags = new List<InlineFrag>(units.Count);
        float x = 0;
        for (int i = 0; i < units.Count; i++)
        {
            var (f, tok, w, space, _, first, last) = units[i];
            float pl = first ? f.PadLeft : 0f, pr = last ? f.PadRight : 0f;
            if (f.Box != null) { pl = 0; pr = 0; w = f.Width; tok = f.Text; }
            else w += (space ? add : 0f) + (gapBefore[i] ? add : 0f);
            outFrags.Add(new InlineFrag
            {
                Style = f.Style, Text = tok, Box = f.Box, FontSize = f.FontSize,
                Ascent = f.Ascent, Descent = f.Descent, HostText = f.HostText, AlignDY = f.AlignDY,
                PadLeft = pl, PadRight = pr, Width = pl + w + pr, X = x,
            });
            x += pl + w + pr;
        }
        ln.Frags = outFrags;
        ln.Width = x;
    }

    /// <summary>
    /// inline 序列的 min-content 宽：把可用宽压到 0 再走**同一条**折行代码——每个断行机会都断开、
    /// 行首空格吞掉、行尾空格裁掉，取最宽的一行。另起一套 token 口径必与真实折行分叉
    /// （CJK 逐字、break-all、keep-all、white-space、原子盒全都要重新判一遍，且判不准）。
    /// </summary>
    public static float MinContentWidth(List<InlineAtom> atoms)
    {
        float w = 0;
        // minContent=true：量最小内容宽时不许按 overflow-wrap 断词（break-word 的定义就是
        // 「只在不得不断时才断」，它不该让容器以为一个长词可以缩到一字符宽）；anywhere 才允许。
        foreach (var ln in BreakRaw(atoms, 0, null, 0, null, minContent: true)) w = Math.Max(w, ln.Width);
        return w;
    }

    /// <summary>overflow-wrap 是否允许在词内断开（white-space 得先允许折行）。</summary>
    static bool CanBreakInside(ComputedStyle s, bool minContent) =>
        s.OverflowWrap is "break-word" or "anywhere"
        && (s.OverflowWrap == "anywhere" || !minContent)
        && s.WhiteSpace is "normal" or "pre-wrap" or "pre-line" or "break-spaces";

    /// <summary>词内断行点：能放进 <paramref name="width"/> 的最长前缀长度（按字素簇，不拆散 surrogate 对）。
    /// 连一个字素都放不下时仍返回第一个字素——Chrome 也至少塞一个字符，同时保证折行一定前进。</summary>
    static int LongestFit(string word, float width, ComputedStyle s)
    {
        int len = 0, best = 0;
        foreach (var g in TextShaper.Graphemes(word))
        {
            int n = len + g.Length;
            if (best > 0 && TextWidth(word.Substring(0, n), s) > width) break;
            len = n;
            best = n;
        }
        return best;
    }

    /// <summary>纯文本块（没有 Atoms、走 TextContent 老路径）的 min-content：最宽的可断行段。</summary>
    public static float MinContentOfText(string text, ComputedStyle style)
    {
        float w = 0;
        foreach (var tok in Tokenize(text, style.WhiteSpace, style.WordBreak))
            if (tok != " " && tok != "\n") w = Math.Max(w, TextWidth(tok, style));
        return w;
    }

    static List<LineBox> BreakRaw(List<InlineAtom> atoms, float maxWidth, FloatContext? fc, float topY, ComputedStyle? strut, bool minContent = false)
    {
        var lines = new List<LineBox>();
        float yCursor = topY;
        var cur = NewLine(atoms.Count > 0 ? atoms[0].Style : null);
        // text-indent 只作用于本 IFC 的首行：左缩进 += indent、可用宽 -= indent（负值=悬挂缩进）
        float ti = strut?.TextIndent ?? 0;
        if (ti != 0) { cur.Inset += ti; cur.Avail = Math.Max(1, cur.Avail - ti); }
        float curW = 0, avail = cur.Avail;

        LineBox NewLine(ComputedStyle? firstStyle)
        {
            var ln = new LineBox();
            if (fc != null)
            {
                float est = firstStyle != null ? firstStyle.LineHeightPx() : 16;
                var (l, r) = fc.Insets(yCursor, est);
                ln.Inset = l;
                ln.Avail = Math.Max(1, maxWidth - l - r);
            }
            else ln.Avail = maxWidth;
            return ln;
        }

        void Roll(ComputedStyle? nextStyle)
        {
            FinalizeLine(cur, strut ?? nextStyle ?? (atoms.Count > 0 ? atoms[0].Style : null)); lines.Add(cur); yCursor += cur.Height;
            cur = NewLine(nextStyle); avail = cur.Avail; curW = 0;
        }

        void AddFrag(InlineFrag f)
        {
            f.X = curW;
            cur.Frags.Add(f);
            curW += f.Width;
        }

        /// <summary>把一个文本 token 落到当前行：与同一样式的尾 frag 合并（同一元素在同一行上是一个片段，
        /// padding 只在两端各算一次），否则另起新 frag。</summary>
        void Place(InlineAtom at, string tok, float tw, float padL, float padR, bool sameRun)
        {
            var last = cur.Frags.Count > 0 ? cur.Frags[^1] : null;
            if (sameRun && last != null)
            {
                float grow = tw + padR - last.PadRight;
                last.Text += tok; last.Width += grow; curW += grow; last.PadRight = padR;
                return;
            }
            var st = at.Style;
            var (asc, desc) = FontManager.TextExtents(st.FontFamily, st.FontWeight, st.FontSize);
            AddFrag(new InlineFrag
            {
                Style = st, Text = tok, Width = padL + tw + padR, PadLeft = padL, PadRight = padR,
                FontSize = st.FontSize, Ascent = asc, Descent = desc, HostText = at.HostText,
            });
        }

        foreach (var a in atoms)
        {
            if (a.IsBreak)
            {
                if (cur.Frags.Count > 0) { cur.ForcedBreak = true; Roll(a.Style); }
                else { var e = NewLine(a.Style); FinalizeLine(e, strut ?? a.Style); lines.Add(e); yCursor += e.Height; } // <br> 于行首 → 空行
                continue;
            }
            if (a.Box != null)
            {
                float bw = a.Box.TotalWidth;
                if (curW + bw > avail && cur.Frags.Count > 0) Roll(a.Style);
                var (aAsc, aDesc) = AtomicExtents(a.Box);
                AddFrag(new InlineFrag { Style = a.Style, Box = a.Box, Width = bw, FontSize = a.Style.FontSize, Ascent = aAsc, Descent = aDesc });
                continue;
            }
            // 文本：按 token 折行
            var toks = Tokenize(a.Text!, a.Style.WhiteSpace, a.Style.WordBreak);
            bool maySplit = CanBreakInside(a.Style, minContent);
            bool midWord = false;   // 上一个是被 overflow-wrap 切断的词的前半（本段不是 inline 盒的开头）
            for (int qi = 0; qi < toks.Count; qi++)
            {
                var tok = toks[qi];
                if (tok == "\n") // preserve 模式下的强制换行
                {
                    cur.ForcedBreak = true;
                    Roll(a.Style); midWord = false; continue;
                }
                if (tok == " " && cur.Frags.Count == 0) { midWord = false; continue; } // 行首空格吞掉
                float tw = TextWidth(tok, a.Style);
                bool isSpace = tok == " ";
                var (pl0, pr0) = a.HDeco(maxWidth);
                bool sameRun = IsRunTail(cur, a.Style);
                bool cont = midWord; midWord = false;
                float pl = sameRun || cont ? 0 : pl0;
                if (curW + pl + tw + pr0 > avail && cur.Frags.Count > 0 && !(isSpace && string.IsNullOrWhiteSpace(LastText(cur))))
                {
                    TrimTrailingSpace(cur, ref curW);
                    Roll(a.Style);
                    if (isSpace) continue; // 行首空格吞掉
                    sameRun = false;
                    if (!cont) pl = pl0;
                }
                // overflow-wrap：词宽过整行，正常词边界救不了它——从行首按可用宽切一刀，余下重新排队
                // （下一轮它仍放不下 → 走上面的 Roll 换行，再切，直到余部放得下）。
                if (maySplit && !isSpace && pl + tw + pr0 > avail)
                {
                    int cut = LongestFit(tok, avail - curW - pl, a.Style);
                    if (cut > 0 && cut < tok.Length)
                    {
                        string head = tok.Substring(0, cut);
                        Place(a, head, TextWidth(head, a.Style), pl, 0, sameRun);
                        toks[qi] = tok.Substring(cut);
                        midWord = true;
                        continue;
                    }
                }
                Place(a, tok, tw, pl, pr0, sameRun);
            }
        }
        if (cur.Frags.Count > 0) { TrimTrailingSpace(cur, ref curW); FinalizeLine(cur, strut ?? (atoms.Count > 0 ? atoms[0].Style : null)); lines.Add(cur); }
        else if (lines.Count == 0) { var e = NewLine(null); FinalizeLine(e, strut ?? (atoms.Count > 0 ? atoms[0].Style : null)); lines.Add(e); }
        return lines;
    }

    /// <summary>
    /// 原子盒（inline-block / 行内 img）对行盒的上下伸出量（= 基线把它切成的两段）。
    /// CSS 10.8.1：inline-block 的基线落在它**最后一个行盒**的基线上；没有行盒（替换元素、
    /// 空盒、纯块内容）才退到下外边距边。旧代码一律按「底边压基线」处理，于是每个 inline-block
    /// 都把它自己本该在基线下方的那截 descent 顶到了基线上方：行盒凭空增高、后面整篇下移
    /// （inline 样例实测每行 +3.2px）。
    /// </summary>
    static (float Asc, float Desc) AtomicExtents(BlockBox b)
    {
        float h = b.TotalHeight;   // 含 border+padding；行内块的纵向 margin 不参与行盒
        float below = Math.Clamp(BaselineToBottom(b) ?? 0f, 0, h);
        return (h - below, below);
    }

    /// <summary>
    /// 基线到盒「下边框外缘」的距离；null = 盒里既没有行盒也没有子盒（替换元素、空盒——调用方按
    /// 「基线落在下外边距边」处理，即距离 0）。行盒可能在三个地方：自己的 <see cref="BlockBox.Lines"/>
    /// （有行内片段）、<see cref="BlockBox.WrappedLines"/>（整盒只有一段文字、按块折的行），
    /// 或藏在<b>最后一个常规流子盒</b>里（子盒自己也没有行时，它的下外边距边就当基线——与浏览器一致）。
    /// </summary>
    static float? BaselineToBottom(BlockBox b)
    {
        var s = b.Style;
        float deco = s.PaddingBottom + s.BorderWidthBottom;
        if (b.Lines is { Count: > 0 } ls)
        {
            float ifc = 0;
            foreach (var l in ls) ifc += l.Height;
            return (ls[^1].Height - ls[^1].Baseline) + Math.Max(0, b.ContentHeight - ifc) + deco;
        }
        if (b.WrappedLines is { Length: > 0 } ws)
        {
            float lh = s.LineHeightPx();    // 与 MeasureBlock 那条折行同一口径
            var (asc, _) = BoxOf(s);
            return (lh - asc) + Math.Max(0, b.ContentHeight - ws.Length * lh) + deco;
        }
        var kids = InFlowChildren(b);
        if (kids.Count == 0) return null;
        var last = kids[^1];
        return (BaselineToBottom(last) ?? 0f) + last.Style.MarginBottom + deco;
    }

    /// <summary>
    /// 盒「上边框外缘」到其**第一条基线**的距离（flex/grid 的 align-items:baseline 用）；
    /// null = 盒里找不到基线（替换元素、空盒）——调用方按合成基线落在上外边距边处理，即距离 0。
    /// 与 <see cref="BaselineToBottom"/> 同一套行盒定位口径：纯文本块的首行基线 = leading/2 取整 + ascent。
    /// </summary>
    internal static float? BaselineToTop(BlockBox b)
    {
        var s = b.Style;
        float deco = s.BorderWidthTop + s.PaddingTop;
        if (b.Lines is { Count: > 0 } ls) return deco + ls[0].Baseline;
        if (b.WrappedLines is { Length: > 0 })
        {
            var (asc, _) = BoxOf(s);
            return deco + asc;
        }
        var kids = InFlowChildren(b);
        if (kids.Count == 0) return null;
        var cb = BaselineToTop(kids[0]);
        return cb == null ? null : deco + cb;
    }

    /// <summary>常规流子盒（浮动与绝对定位不参与「最后一个行盒」的认定）。</summary>
    static List<BlockBox> InFlowChildren(BlockBox b)
    {
        if (b.Children == null || b.Children.Count == 0) return new List<BlockBox>();
        var list = new List<BlockBox>(b.Children.Count);
        foreach (var c in b.Children)
            if (c.Style.Position == "static" && c.Style.Float == "none") list.Add(c);
        return list;
    }

    /// <summary>
    /// 一个片段对行盒上/下各伸出多高（CSS 行盒模型）：内容区（字体 ascent+descent，<b>各自已取整到整像素</b>）
    /// 压在基线上，行高的富余 leading 上下各半 → asc = A + ⌊leading/2⌋、desc = D + ⌈leading/2⌉。
    /// <b>奇数富余归下方</b>是浏览器口径（探针实测 15px/行高 24 的基线在 18 而非 18.5，负 leading 同理：
    /// 20px/行高 24 → ⌊−2.5⌋=−3）。单字体行里行盒恒等于 L；<b>混排</b>字体时各项的 (A−D) 不同，
    /// 行盒 = max(asc) + max(desc) 会比任何单项都高——浏览器里「一个 <code> 让整行高出 1~2px」正是它。
    /// 原子盒（inline-block / 行内 img）的上下两段由 <see cref="AtomicExtents"/> 给，不再套 leading。
    /// </summary>
    static (float Asc, float Desc) BoxOf(InlineFrag f)
        => f.Box != null ? (f.Ascent, f.Descent) : BoxOf(f.Ascent, f.Descent, f.Style.LineHeightPx());

    static (float Asc, float Desc) BoxOf(ComputedStyle s)
    {
        var (a, d) = FontManager.TextExtents(s.FontFamily, s.FontWeight, s.FontSize);
        return BoxOf(a, d, s.LineHeightPx());
    }

    /// <summary>纯文本块**首行基线**相对内容盒顶的距离，绘制侧必须与 <see cref="BaselineToTop"/> 取同一个数
    /// （ascent + ⌊leading/2⌋）。画成「内容顶 + 字号」时行距越大偏得越多：m3 里 40px 高、行高 2.2 的
    /// inline-block 文字实测比浏览器高 7px。</summary>
    internal static float FirstBaselineOffset(ComputedStyle s) => BoxOf(s).Asc;

    static (float Asc, float Desc) BoxOf(float asc, float desc, float lineHeight)
    {
        float lead = lineHeight - (asc + desc);
        float top = (float)Math.Floor(lead / 2);
        return (asc + top, desc + lead - top);
    }

    static void FinalizeLine(LineBox ln, ComputedStyle? strut)
    {
        // 行盒 = 行内每个盒**按 vertical-align 对齐之后**的上下伸出量的并集（CSS 2.1 §10.8）。
        // 除了 top/bottom，位移量都与行盒无关 → 一遍就能同时定出各片段的位移和撑开后的行盒；
        // 被抬起的片段因此会把基线顶下去（整行文字随之一起下移），被压下的片段只加高下半截。
        // Chrome 同口径：_probe-va5 里 14px/行高 21 的一行放个「上抬 18px」的 12px 片段，
        // 行盒 21→37、基线由盒顶第 16 变第 32，片段顶边落在盒顶 +1，逐位吻合。
        // top/bottom 反过来要贴行盒的边，先按基线对齐计入并集（免得整行只有一个贴图时被算成空行），
        // 再在并集定稿后贴一次边。外侧 marker 完全不撑行盒：见 ComputedStyle.IsListMarker。
        float asc = 0, desc = 0, w = 0;
        foreach (var f in ln.Frags)
        {
            if (f.Style.IsListMarker) { w += f.Width; continue; }
            var (fAsc, fDesc) = BoxOf(f);
            w += f.Width;
            if (f.Style.VerticalAlign is "top" or "bottom")
            {
                if (fAsc > asc) asc = fAsc;
                desc = Math.Max(desc, fDesc);
                continue;
            }
            float dy = f.AlignDY = AlignShift(f, ln, fAsc, fDesc);
            asc = Math.Max(asc, fAsc - dy);
            desc = Math.Max(desc, fDesc + dy);
        }
        // strut（块自身的字体与行高）恒在每一行里：小图独占一行时行高也得不低于它
        if (strut != null)
        {
            var (a, d) = BoxOf(strut);
            asc = Math.Max(asc, a);
            desc = Math.Max(desc, d);
        }
        ln.Baseline = asc > 0 ? asc : strut?.FontSize ?? 14;
        ln.Height = asc + desc;
        ln.Width = w;

        foreach (var f in ln.Frags)
        {
            if (f.Style.IsListMarker) continue;
            if (f.Style.VerticalAlign is not ("top" or "bottom")) continue;
            var (fAsc, fDesc) = BoxOf(f);
            f.AlignDY = AlignShift(f, ln, fAsc, fDesc);
        }

        ReorderForRtl(ln);
    }

    /// <summary>
    /// 一个片段按 vertical-align 相对基线的**下移量**（正 = 下）。文本 run 与原子盒共用：
    /// 绘制顶边一律是「行盒基线 + dy − 自己的上段」，上段对文本取 <see cref="BoxOf(InlineFrag)"/>
    /// 的行高盒、对原子盒取盒基线以上那截（<c>f.Ascent</c>，两者相加恒等于盒高），故同一条算式两边都成立。
    /// 基准一律是**父元素**（CSS 2.1 §10.8.1，见 <see cref="ParentStyle"/>）：
    /// text-top/text-bottom 贴父字体内容区的上下边，middle 把自身盒中点摆到「父基线 + 父 x-height/2」。
    /// Chrome 实测：super 上抬 = 父字号/3 + 1px、sub 下移 = 父字号/5 + 1px（只看**父**字号，
    /// 4 字体 × 3 行高 × 7 档父字号拟合残差 ≤0.006px）；% 的基准是**元素自己的 used line-height**
    /// （19.1875px 的 25% = 4.797、40px 的 25% = 10、normal 行高 16px 的 25% = 4），em 是自己的字号
    /// （故这两个值必须由声明文本换算，见 StyleResolver 里 vertical-align 取 Spec 的那段）。
    /// **嵌套行内要累加外层盒的位移**：行内盒挪的是自己的基线，子元素按 baseline 落在这条挪过的基线上，
    /// 不是回到整行的基线（探针 _probe-va9 p[5]：<span{va:super}> 里的 <i> 与 span 同顶边 109，
    /// 旧口径把它放回 114.67，比浏览器低 5.7px）。
    /// </summary>
    static float AlignShift(InlineFrag f, LineBox ln, float fAsc, float fDesc)
    {
        // 块自身的文字不按块的 vertical-align 位移：这条属性只作用于「行盒里的那个盒」，块级盒写了它
        // 也只被外层用作对齐（表格单元格走 ValignDY，另有 inline-block/行内块作为原子盒在此行里）。
        // Chrome 实测：<p style="vertical-align:super"> 与对照 <p> 行盒同高（_probe-va8）；td/th 的 UA
        // middle 若作用到单元格里每段文字，table 样例每一行都虚高 2.2px。
        if (f.HostText) return 0;
        float dy = OwnShift(f.Style, fAsc, fDesc, ln);
        for (var p = f.Style.Parent; p is { Display: "inline" }; p = p.Parent)
            if (p.VerticalAlign != "baseline")
            {
                var (pAsc, pDesc) = BoxOf(p);
                dy += OwnShift(p, pAsc, pDesc, ln);
            }
        return dy;
    }

    /// <summary>某个盒**自己**那条 vertical-align 的位移（外层行内盒的贡献由 AlignShift 逐层累加）。</summary>
    static float OwnShift(ComputedStyle st, float fAsc, float fDesc, LineBox ln)
    {
        var va = st.VerticalAlign;
        switch (va)
        {
            case "top": return fAsc - ln.Baseline;
            case "bottom": return (ln.Height - ln.Baseline) - fDesc;
            case "text-top": return fAsc - ParentFontBox(st).Asc;
            case "text-bottom": return ParentFontBox(st).Desc - fDesc;
            case "middle": return (fAsc - fDesc) / 2 - ParentXHeight(st) / 2;
            case "super": return -(ParentFontSize(st) / 3f + 1f);
            case "sub": return ParentFontSize(st) / 5f + 1f;
            default:
                float len = va.EndsWith('%')
                    ? st.LineHeightPx() * CssValueParser.ParseFloat(va[..^1]) / 100f
                    : CssValueParser.ToPixels(va, st.FontSize);
                return float.IsNaN(len) ? 0 : -len;   // 正值上移；"baseline" 等解析为 0
        }
    }

    /// <summary>RTL 基底方向：把行内 frag 按强方向分组并反转为视觉序（规则与 TextShaper.BidiRuns 一致），重排 X。
    /// 反转分两级：段序整体倒排（RTL 段最先出现在最右），且 RTL 段**内部**也倒排——阿拉伯语词序自右向左，
    /// LTR 段（拉丁词/数字）内部保持逻辑序。旧实现只做第一级，于是纯 RTL 段里的 inline-block 停在段首，
    /// 实测偏移 317px。</summary>
    static void ReorderForRtl(LineBox ln)
    {
        if (ln.Frags.Count < 2) return;
        if (ln.Frags[0].Style.Direction != "rtl") return;

        var groups = new List<(int Dir, List<InlineFrag> Frags)>();
        var cur = new List<InlineFrag>();
        int curDir = 0;
        foreach (var f in ln.Frags)
        {
            int dir = f.Text != null ? TextShaper.StrongDir(f.Text) : 0; // 原子盒按中性处理
            if (curDir == 0) { cur.Add(f); if (dir != 0) curDir = dir; }
            else if (dir == 0 || dir == curDir) cur.Add(f);
            else { groups.Add((curDir, cur)); cur = new List<InlineFrag> { f }; curDir = dir; }
        }
        if (cur.Count > 0) groups.Add((curDir, cur));

        groups.Reverse();
        float x = 0;
        foreach (var (dir, g) in groups)
        {
            if (dir > 0) g.Reverse();
            foreach (var f in g) { f.X = x; x += f.Width; }
        }
    }

    /// <summary>CSS 2.1 §10.8.1 的 sub/super/middle/text-top/text-bottom 都以**父元素**的字体为基准
    /// （ComputedStyle.Parent 在样式期已挂好）。</summary>
    static ComputedStyle ParentStyle(ComputedStyle st) => st.Parent ?? st;

    /// <summary>父字体内容区（ascent/descent，各自取整到整像素）在父字号下的上下高度。</summary>
    static (float Asc, float Desc) ParentFontBox(ComputedStyle st)
    {
        var p = ParentStyle(st);
        return FontManager.TextExtents(p.FontFamily, p.FontWeight, p.FontSize);
    }

    /// <summary>父字体的 x-height（px）——vertical-align:middle 把自身盒中点摆到「基线 + 它的一半」。</summary>
    static float ParentXHeight(ComputedStyle st)
    {
        var p = ParentStyle(st);
        return FontManager.XHeightPerEm(p.FontFamily, p.FontWeight) * p.FontSize;
    }

    static float ParentFontSize(ComputedStyle st)
        => st.Parent is { FontSize: > 0 } p ? p.FontSize : st.FontSize;

    static string LastText(LineBox ln)
    {
        for (int i = ln.Frags.Count - 1; i >= 0; i--)
            if (ln.Frags[i].Text != null) return ln.Frags[i].Text!;
        return "";
    }

    /// <summary>
    /// 行内元素的横向外扩（padding + border）。竖直 padding 对非替换行内不生效，故只取横向。
    /// inline 元素没有盒，从没走过 Measure 里的 ResolveBoxSizes，所以 padding 要拿**声明值**
    /// 按包含块宽（= 本 IFC 的 maxWidth）就地折算，否则 padding:0 4px 的 <code> 会量成 0。
    /// </summary>
    internal static (float Left, float Right) HDeco(ComputedStyle s, float cbW)
        => (s.BorderWidthLeft + s.PaddingLeftCss.ResolveEdge(cbW),
            s.BorderWidthRight + s.PaddingRightCss.ResolveEdge(cbW));

    /// <summary>本行末尾是否还是同一个 inline 元素的文字延续（决定还要不要再加一次 padding-left）。</summary>
    static bool IsRunTail(LineBox ln, ComputedStyle st)
        => ln.Frags.Count > 0 && ln.Frags[^1] is { Text: not null } f && ReferenceEquals(f.Style, st);

    static void TrimTrailingSpace(LineBox ln, ref float curW)
    {
        if (ln.Frags.Count == 0) return;
        var last = ln.Frags[^1];
        if (last.Text == null) return;
        string t = last.Text.TrimEnd();
        if (t.Length != last.Text.Length)
        {
            float removed = TextWidth(last.Text[^1..], last.Style);
            last.Text = t; last.Width = last.PadLeft + TextWidth(t, last.Style) + last.PadRight;
            curW -= removed;
        }
    }

    static float TextWidth(string s, ComputedStyle st)
    {
        using var font = new SKFont(FontManager.Get(st.FontFamily, st.FontWeight), st.FontSize);
        return TextMeasurer.LineWidth(s, font, st.LetterSpacing);
    }

    static string ApplyTransform(string text, string mode)
    {
        if (mode == "uppercase") return text.ToUpperInvariant();
        if (mode == "lowercase") return text.ToLowerInvariant();
        if (mode == "capitalize")
        {
            var sb = new System.Text.StringBuilder(text.Length);
            bool ws = true;
            foreach (var ch in text) { if (char.IsWhiteSpace(ch)) { ws = true; sb.Append(ch); } else { sb.Append(ws ? char.ToUpperInvariant(ch) : ch); ws = false; } }
            return sb.ToString();
        }
        return text;
    }

    /// <summary>把一段文本切成 token（词 / 单空格 / 单 CJK 字），受 white-space / word-break 影响。
    /// 非 preserve 下把连续空白（含换行/制表）折叠成单个 " " token；preserve 下 \n 作为强制换行 token。</summary>
    static List<string> Tokenize(string s, string whiteSpace, string wordBreak)
    {
        bool preserve = whiteSpace == "pre" || whiteSpace == "pre-wrap" || whiteSpace == "pre-line";
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();
        void Flush() { if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); } }
        static bool IsWs(string g) => g.Length == 1 && g[0] is ' ' or '\n' or '\t' or '\r' or '\f';

        foreach (var g in TextShaper.Graphemes(s)) // 字素簇为单位：surrogate 对（emoji/生僻字）不拆散
        {
            if (preserve)
            {
                if (g == "\n") { Flush(); tokens.Add("\n"); }
                else sb.Append(g);
                continue;
            }
            if (IsWs(g))
            {
                Flush();
                if (tokens.Count == 0 || tokens[^1] != " ") tokens.Add(" "); // 折叠连续空白为单个空格
            }
            else if (wordBreak == "break-all" || (TextMeasurer.IsCjkChar(g[0]) && wordBreak != "keep-all"))
            {
                Flush();
                tokens.Add(g);
            }
            else if (wordBreak != "keep-all" && g.Length == 1 && g[0] == '-' && sb.Length > 0
                     && sb[^1] is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                // 词内连字符后是断点（UAX#14 的 LD 类，Blink 照它折）："border-radius" → "border-" / "radius"。
                // 前面不是词字符的 '-'（"calc(18px - 0.5rem)"）不算，否则会把运算符当断点。
                sb.Append(g);
                Flush();
            }
            else sb.Append(g);
        }
        Flush();
        return tokens;
    }
}
