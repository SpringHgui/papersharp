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
    public float AlignDY;   // vertical-align 生效后的基线下移量（文本 run；原子盒由 PositionInline 处理）
}

/// <summary>一行：若干 frag + 行高/基线/宽度。Left 为对齐后的 x 偏移（相对内容盒）。
/// Inset/Avail：环绕浮动时该行的左缩进与可用宽（无浮动时 0/-1 表示全宽）。</summary>
public sealed class LineBox
{
    public List<InlineFrag> Frags = new();
    public float Width, Height, Baseline, Left;
    public float Inset;
    public float Avail = -1;
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
        return lines;
    }

    /// <summary>
    /// inline 序列的 min-content 宽：把可用宽压到 0 再走**同一条**折行代码——每个断行机会都断开、
    /// 行首空格吞掉、行尾空格裁掉，取最宽的一行。另起一套 token 口径必与真实折行分叉
    /// （CJK 逐字、break-all、keep-all、white-space、原子盒全都要重新判一遍，且判不准）。
    /// </summary>
    public static float MinContentWidth(List<InlineAtom> atoms)
    {
        float w = 0;
        foreach (var ln in BreakRaw(atoms, 0, null, 0, null)) w = Math.Max(w, ln.Width);
        return w;
    }

    /// <summary>纯文本块（没有 Atoms、走 TextContent 老路径）的 min-content：最宽的可断行段。</summary>
    public static float MinContentOfText(string text, ComputedStyle style)
    {
        float w = 0;
        foreach (var tok in Tokenize(text, style.WhiteSpace, style.WordBreak))
            if (tok != " " && tok != "\n") w = Math.Max(w, TextWidth(tok, style));
        return w;
    }

    static List<LineBox> BreakRaw(List<InlineAtom> atoms, float maxWidth, FloatContext? fc, float topY, ComputedStyle? strut)
    {
        var lines = new List<LineBox>();
        float yCursor = topY;
        var cur = NewLine(atoms.Count > 0 ? atoms[0].Style : null);
        float curW = 0, avail = cur.Avail;

        LineBox NewLine(ComputedStyle? firstStyle)
        {
            var ln = new LineBox();
            if (fc != null)
            {
                float est = firstStyle != null ? firstStyle.FontSize * firstStyle.LineHeightFactor : 16;
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

        foreach (var a in atoms)
        {
            if (a.IsBreak)
            {
                if (cur.Frags.Count > 0) Roll(a.Style);
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
            foreach (var tok in Tokenize(a.Text!, a.Style.WhiteSpace, a.Style.WordBreak))
            {
                if (tok == "\n") // preserve 模式下的强制换行
                {
                    Roll(a.Style); continue;
                }
                if (tok == " " && cur.Frags.Count == 0) continue; // 行首空格吞掉
                float tw = TextWidth(tok, a.Style);
                bool isSpace = tok == " ";
                var (pl0, pr0) = a.HDeco(maxWidth);
                bool sameRun = IsRunTail(cur, a.Style);
                float pl = sameRun ? 0 : pl0;
                if (curW + pl + tw + pr0 > avail && cur.Frags.Count > 0 && !(isSpace && string.IsNullOrWhiteSpace(LastText(cur))))
                {
                    TrimTrailingSpace(cur, ref curW);
                    Roll(a.Style);
                    if (isSpace) continue; // 行首空格吞掉
                    sameRun = false; pl = pl0;
                }
                // 合并到同一样式的尾 frag：同一元素在同一行上是一个片段，padding 只在两端各算一次
                var last = cur.Frags.Count > 0 ? cur.Frags[^1] : null;
                if (sameRun && last != null)
                {
                    float grow = tw + pr0 - last.PadRight;
                    last.Text += tok; last.Width += grow; curW += grow; last.PadRight = pr0;
                }
                else
                {
                    var (asc, desc) = FontManager.TextExtents(a.Style.FontFamily, a.Style.FontWeight, a.Style.FontSize);
                    AddFrag(new InlineFrag
                    {
                        Style = a.Style, Text = tok, Width = pl + tw + pr0, PadLeft = pl, PadRight = pr0,
                        FontSize = a.Style.FontSize, Ascent = asc, Descent = desc,
                    });
                }
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
            float lh = s.FontSize * s.LineHeightFactor;    // 与 MeasureBlock 那条折行同一口径
            var (asc, _) = BoxOf(s);
            return (lh - asc) + Math.Max(0, b.ContentHeight - ws.Length * lh) + deco;
        }
        var kids = InFlowChildren(b);
        if (kids.Count == 0) return null;
        var last = kids[^1];
        return (BaselineToBottom(last) ?? 0f) + last.Style.MarginBottom + deco;
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
        => f.Box != null ? (f.Ascent, f.Descent) : BoxOf(f.Ascent, f.Descent, f.FontSize * f.Style.LineHeightFactor);

    static (float Asc, float Desc) BoxOf(ComputedStyle s)
    {
        var (a, d) = FontManager.TextExtents(s.FontFamily, s.FontWeight, s.FontSize);
        return BoxOf(a, d, s.FontSize * s.LineHeightFactor);
    }

    static (float Asc, float Desc) BoxOf(float asc, float desc, float lineHeight)
    {
        float lead = lineHeight - (asc + desc);
        float top = (float)Math.Floor(lead / 2);
        return (asc + top, desc + lead - top);
    }

    static void FinalizeLine(LineBox ln, ComputedStyle? strut)
    {
        float maxAsc = 0, maxDesc = 0, w = 0;
        foreach (var f in ln.Frags)
        {
            var (a, d) = BoxOf(f);
            if (a > maxAsc) maxAsc = a;
            maxDesc = Math.Max(maxDesc, d);
            w += f.Width;
        }
        // strut（块自身的字体与行高）恒在每一行里：小图独占一行时行高也得不低于它
        if (strut != null)
        {
            var (a, d) = BoxOf(strut);
            maxAsc = Math.Max(maxAsc, a);
            maxDesc = Math.Max(maxDesc, d);
        }
        ln.Baseline = maxAsc > 0 ? maxAsc : strut?.FontSize ?? 14;
        ln.Height = maxAsc + maxDesc;
        ln.Width = w;

        // 文本 run 的 vertical-align（top/bottom 对齐行盒，super/sub 惯例偏移，length/% 相对基线上移）
        foreach (var f in ln.Frags)
        {
            if (f.Text == null) continue;
            var va = f.Style.VerticalAlign;
            var (fAsc, fDesc) = BoxOf(f);
            float dy;
            switch (va)
            {
                // top/bottom：把该片段自己的行高盒上/下边贴到行盒对应边上（相对基线换算成下移量）
                case "top": dy = fAsc - ln.Baseline; break;
                case "bottom": dy = (ln.Height - ln.Baseline) - fDesc; break;
                case "super": dy = -0.35f * f.FontSize; break;
                case "sub": dy = 0.25f * f.FontSize; break;
                default:
                    float len = va.EndsWith('%')
                        ? ln.Height * CssValueParser.ParseFloat(va[..^1]) / 100f
                        : CssValueParser.ToPixels(va, f.FontSize);
                    dy = float.IsNaN(len) ? 0 : -len; // 正值上移；"baseline"/"middle" 等关键字解析为 0
                    break;
            }
            f.AlignDY = dy;
        }

        ReorderForRtl(ln);
    }

    /// <summary>RTL 基底方向：把行内 frag 按强方向分组并反转为视觉序（规则与 TextShaper.BidiRuns 一致），重排 X。</summary>
    static void ReorderForRtl(LineBox ln)
    {
        if (ln.Frags.Count < 2) return;
        var firstText = ln.Frags.FirstOrDefault(f => f.Text != null);
        if (firstText == null || firstText.Style.Direction != "rtl") return;

        var groups = new List<List<InlineFrag>>();
        var cur = new List<InlineFrag>();
        int curDir = 0;
        foreach (var f in ln.Frags)
        {
            int dir = f.Text != null ? TextShaper.StrongDir(f.Text) : 0; // 原子盒按中性处理
            if (curDir == 0) { cur.Add(f); if (dir != 0) curDir = dir; }
            else if (dir == 0 || dir == curDir) cur.Add(f);
            else { groups.Add(cur); cur = new List<InlineFrag> { f }; curDir = dir; }
        }
        if (cur.Count > 0) groups.Add(cur);
        if (groups.Count < 2) return;

        groups.Reverse();
        float x = 0;
        foreach (var g in groups)
            foreach (var f in g) { f.X = x; x += f.Width; }
    }

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
    static IEnumerable<string> Tokenize(string s, string whiteSpace, string wordBreak)
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
            else sb.Append(g);
        }
        Flush();
        return tokens;
    }
}
