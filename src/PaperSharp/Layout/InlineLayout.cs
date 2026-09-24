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
}

/// <summary>折行后落在某行上的一段：文本（带样式）或原子盒。X 是相对行内容起点的偏移。</summary>
public sealed class InlineFrag
{
    public ComputedStyle Style = null!;
    public string? Text;
    public BlockBox? Box;
    public float X, Width, FontSize, Ascent, Descent;
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
    /// <summary>从某块的 InlinePieces 递归摊平成原子序列。factory 用于给 inline-block/img 建盒。</summary>
    public static List<InlineAtom> BuildAtoms(ComputedStyle style, Func<ComputedStyle, BlockBox> factory)
    {
        var list = new List<InlineAtom>();
        foreach (var piece in style.InlinePieces)
        {
            if (piece.IsBreak) { list.Add(new InlineAtom { Style = style, IsBreak = true }); continue; }
            if (piece.Text != null)
            {
                string t = ApplyTransform(piece.Text, style.TextTransform);
                if (!string.IsNullOrEmpty(t)) list.Add(new InlineAtom { Style = style, Text = t });
                continue;
            }
            var c = piece.Child;
            if (c == null || c.Display == "none") continue;
            if (c.Display is "inline-block" or "inline-flex" or "inline-grid" || c.Tag is "img" or "svg")
                list.Add(new InlineAtom { Style = c, Box = factory(c) });
            else
                list.AddRange(BuildAtoms(c, factory));   // 普通 inline：递归摊平
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
                AddFrag(new InlineFrag { Style = a.Style, Box = a.Box, Width = bw, FontSize = a.Style.FontSize, Ascent = a.Box.TotalHeight, Descent = 0 });
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
                if (curW + tw > avail && cur.Frags.Count > 0 && !(isSpace && string.IsNullOrWhiteSpace(LastText(cur))))
                {
                    TrimTrailingSpace(cur, ref curW);
                    Roll(a.Style);
                    if (isSpace) continue; // 行首空格吞掉
                }
                // 合并到同一样式的尾 frag
                var last = cur.Frags.Count > 0 ? cur.Frags[^1] : null;
                if (last != null && last.Text != null && ReferenceEquals(last.Style, a.Style))
                { last.Text += tok; last.Width += tw; curW += tw; }
                else
                    AddFrag(new InlineFrag { Style = a.Style, Text = tok, Width = tw, FontSize = a.Style.FontSize, Ascent = a.Style.FontSize, Descent = a.Style.FontSize * 0.22f });
            }
        }
        if (cur.Frags.Count > 0) { TrimTrailingSpace(cur, ref curW); FinalizeLine(cur, strut ?? (atoms.Count > 0 ? atoms[0].Style : null)); lines.Add(cur); }
        else if (lines.Count == 0) { var e = NewLine(null); FinalizeLine(e, strut ?? (atoms.Count > 0 ? atoms[0].Style : null)); lines.Add(e); }
        return lines;
    }

    /// <summary>
    /// 行盒在基线下方留多高：CSS 行盒模型 = 字体 descent + 半 leading（行高富余对半分）。
    /// 纯文本行里行高本就占主导（结果与旧值一致）；只有「高图独占一行」时这截缝才显形——
    /// 浏览器里图片下方那道 ~5px 空隙就是它，缺了它图后面的兄弟全部上移。
    /// descent 用 0.22em，与 BreakRaw 里文本 frag 的口径一致。
    /// </summary>
    static float StrutBelow(ComputedStyle? s)
    {
        if (s == null) return 3f;   // 无 strut 可依：退回旧的经验值
        float descent = s.FontSize * 0.22f;
        float lh = s.FontSize * s.LineHeightFactor;
        return descent + Math.Max(0, (lh - (s.FontSize + descent)) / 2);
    }

    static void FinalizeLine(LineBox ln, ComputedStyle? strut)
    {
        float maxBase = 0, maxH = 0, w = 0;
        bool baseByReplaced = false;
        foreach (var f in ln.Frags)
        {
            if (f.Ascent > maxBase) { maxBase = f.Ascent; baseByReplaced = f.Box is { IsReplaced: true }; }
            maxH = Math.Max(maxH, f.Ascent + f.Descent);
            maxH = Math.Max(maxH, f.FontSize * f.Style.LineHeightFactor);
            w += f.Width;
        }
        // strut（块自身的字体与行高）恒在每一行里：小图独占一行时行高也得不低于它
        if (strut != null) maxH = Math.Max(maxH, strut.FontSize * strut.LineHeightFactor);
        ln.Baseline = maxBase > 0 ? maxBase : strut?.FontSize ?? 14;
        // 基线下方留多高，分两种：
        // · 替换元素（img/svg）的底边就压在基线上 → 行盒还得再加一截 strut descent，
        //   这就是浏览器里「图片下方那道缝」（探针实测 15px/1.6 → 6.2px）。
        // · inline-block 的基线落在其内部文字上，盒底本就在基线上方，沿用旧经验值 3。
        ln.Height = Math.Max(maxH, ln.Baseline + (baseByReplaced ? StrutBelow(strut) : 3f));
        ln.Width = w;

        // 文本 run 的 vertical-align（top/bottom 对齐行盒，super/sub 惯例偏移，length/% 相对基线上移）
        foreach (var f in ln.Frags)
        {
            if (f.Text == null) continue;
            var va = f.Style.VerticalAlign;
            float dy;
            switch (va)
            {
                case "top": dy = f.Ascent - ln.Baseline; break;
                case "bottom": dy = ln.Height - ln.Baseline; break;
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

    static void TrimTrailingSpace(LineBox ln, ref float curW)
    {
        if (ln.Frags.Count == 0) return;
        var last = ln.Frags[^1];
        if (last.Text == null) return;
        string t = last.Text.TrimEnd();
        if (t.Length != last.Text.Length)
        {
            float removed = TextWidth(last.Text[^1..], last.Style);
            last.Text = t; last.Width = TextWidth(t, last.Style);
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
