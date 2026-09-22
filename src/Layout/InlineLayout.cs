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
}

/// <summary>一行：若干 frag + 行高/基线/宽度。Left 为对齐后的 x 偏移（相对内容盒）。</summary>
public sealed class LineBox
{
    public List<InlineFrag> Frags = new();
    public float Width, Height, Baseline, Left;
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
            if (c.Display == "inline-block" || c.Tag == "img")
                list.Add(new InlineAtom { Style = c, Box = factory(c) });
            else
                list.AddRange(BuildAtoms(c, factory));   // 普通 inline：递归摊平
        }
        return list;
    }

    /// <summary>按 maxWidth 把原子序列折成行盒。fontCache 复用 SKFont 度量。</summary>
    public static List<LineBox> BreakIntoLines(List<InlineAtom> atoms, float maxWidth)
    {
        var lines = new List<LineBox>();
        var cur = new LineBox();
        float curW = 0;

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
                if (cur.Frags.Count > 0) { FinalizeLine(cur); lines.Add(cur); cur = new LineBox(); curW = 0; }
                else { var e = new LineBox(); FinalizeLine(e); lines.Add(e); } // <br> 于行首 → 空行
                continue;
            }
            if (a.Box != null)
            {
                float bw = a.Box.TotalWidth;
                if (curW + bw > maxWidth && cur.Frags.Count > 0) { FinalizeLine(cur); lines.Add(cur); cur = new LineBox(); curW = 0; }
                AddFrag(new InlineFrag { Style = a.Style, Box = a.Box, Width = bw, FontSize = a.Style.FontSize, Ascent = a.Box.TotalHeight, Descent = 0 });
                continue;
            }
            // 文本：按 token 折行
            foreach (var tok in Tokenize(a.Text!, a.Style.WhiteSpace, a.Style.WordBreak))
            {
                if (tok == "\n") // preserve 模式下的强制换行
                {
                    FinalizeLine(cur); lines.Add(cur); cur = new LineBox(); curW = 0; continue;
                }
                if (tok == " " && cur.Frags.Count == 0) continue; // 行首空格吞掉
                float tw = TextWidth(tok, a.Style);
                bool isSpace = tok == " ";
                if (curW + tw > maxWidth && cur.Frags.Count > 0 && !(isSpace && string.IsNullOrWhiteSpace(LastText(cur))))
                {
                    TrimTrailingSpace(cur, ref curW);
                    FinalizeLine(cur); lines.Add(cur); cur = new LineBox(); curW = 0;
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
        if (cur.Frags.Count > 0) { TrimTrailingSpace(cur, ref curW); FinalizeLine(cur); lines.Add(cur); }
        else if (lines.Count == 0) { var e = new LineBox(); FinalizeLine(e); lines.Add(e); }
        return lines;
    }

    static void FinalizeLine(LineBox ln)
    {
        float maxBase = 0, maxH = 0, w = 0;
        foreach (var f in ln.Frags)
        {
            maxBase = Math.Max(maxBase, f.Ascent);
            maxH = Math.Max(maxH, f.Ascent + f.Descent);
            maxH = Math.Max(maxH, f.FontSize * f.Style.LineHeightFactor);
            w += f.Width;
        }
        ln.Baseline = maxBase > 0 ? maxBase : 14;
        ln.Height = Math.Max(maxH, ln.Baseline + 3);
        ln.Width = w;
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
        static bool IsWs(char c) => c is ' ' or '\n' or '\t' or '\r' or '\f';

        foreach (var c in s)
        {
            if (preserve)
            {
                if (c == '\n') { Flush(); tokens.Add("\n"); }
                else sb.Append(c);
                continue;
            }
            if (IsWs(c))
            {
                Flush();
                if (tokens.Count == 0 || tokens[^1] != " ") tokens.Add(" "); // 折叠连续空白为单个空格
            }
            else if (wordBreak == "break-all" || (TextMeasurer.IsCjkChar(c) && wordBreak != "keep-all"))
            {
                Flush();
                tokens.Add(c.ToString());
            }
            else sb.Append(c);
        }
        Flush();
        return tokens;
    }
}
