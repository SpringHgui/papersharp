using System.Text;
using SkiaSharp;
using PaperSharp.Css;

namespace PaperSharp.Layout;

/// <summary>
/// 文本折行。Latin 按空格分词，CJK 每字可断；受 white-space / word-break / letter-spacing 影响。
/// 折行后的每一行是最终要绘制的字符串（含 text-transform 结果，transform 在样式层已施加）。
/// </summary>
public static class TextMeasurer
{
    /// <summary>把文本折成若干行。maxWidth&lt;=0 时返回单空行。</summary>
    public static string[] Wrap(string text, float maxWidth, SKFont font, ComputedStyle s)
        => Wrap(text, maxWidth, font, s, null, 0, out _);

    /// <summary>带浮动环绕的折行：每行按全局 y 查询左右缩进，lefts 记录每行左缩进（无浮动时为 null）。
    /// text-wrap: balance 时二次折行逼近等长行（有浮动环绕时跳过）。</summary>
    public static string[] Wrap(string text, float maxWidth, SKFont font, ComputedStyle s,
        FloatContext? fc, float topY, out float[]? lefts)
    {
        var lines = WrapRaw(text, maxWidth, font, s, fc, topY, out lefts);
        if (s.TextWrap == "balance" && fc == null && maxWidth > 0 && lines.Length >= 2
            && s.WhiteSpace is not ("nowrap" or "pre"))
        {
            float total = (lines.Length - 1) * TokenWidth(" ", font, s.LetterSpacing);
            foreach (var ln in lines) total += LineWidth(ln, font, s.LetterSpacing);
            // 二分最小的“行长上限”W：贪心折行仍保持原行数（=最少行数），使各行尽量均匀
            float lo = total / lines.Length - 0.5f, hi = maxWidth;
            for (int iter = 0; iter < 8 && hi - lo > 1f; iter++)
            {
                float mid = (lo + hi) / 2;
                if (WrapRaw(text, mid, font, s, fc, topY, out _).Length <= lines.Length) hi = mid;
                else lo = mid;
            }
            var rebal = WrapRaw(text, hi, font, s, fc, topY, out var rl);
            if (rebal.Length <= lines.Length) { lines = rebal; lefts = rl; }
        }
        return lines;
    }

    static string[] WrapRaw(string text, float maxWidth, SKFont font, ComputedStyle s,
        FloatContext? fc, float topY, out float[]? lefts)
    {
        var lines = new List<string>();
        var leftList = new List<float>();
        float ls = s.LetterSpacing;
        string ws = s.WhiteSpace;
        float lineHeight = s.LineHeightPx();
        float y = topY;

        text = Normalize(text, ws);

        // text-indent 只作用于**块的首行**：首行左缩进 += indent、首行可用宽 -= indent
        // （负值即悬挂缩进，列表 marker 就靠它让出内容盒外的落点）。
        float indent = s.TextIndent;
        bool first = true;
        (float, float) Query() => fc != null ? fc.Insets(y, lineHeight) : (0, 0f);
        void TakeLine(string line)
        {
            var (l, _) = Query();
            lines.Add(line);
            leftList.Add(first ? l + indent : l);
            first = false;
            y += lineHeight;
        }

        // nowrap / pre：不按宽度折行，仅按显式换行切
        bool noWrap = ws == "nowrap" || ws == "pre";
        if (noWrap || maxWidth <= 0)
        {
            foreach (var para in text.Split('\n')) TakeLine(para);
            if (lines.Count == 0) TakeLine("");
            return Finish(lines, leftList, fc, out lefts);
        }

        var paras = text.Split('\n');
        for (int pi = 0; pi < paras.Length; pi++)
        {
            var para = paras[pi];
            if (para.Length == 0) { TakeLine(""); continue; }
            WrapParagraph(para, maxWidth, font, ls, s.WordBreak, s.OverflowWrap, TakeLine, Query, pi == 0 ? indent : 0f);
        }
        if (lines.Count == 0) TakeLine("");
        return Finish(lines, leftList, fc, out lefts);
    }

    static string[] Finish(List<string> lines, List<float> lefts, FloatContext? fc, out float[]? leftArr)
    {
        // 有浮动环绕或首行 text-indent（含负的悬挂缩进）时才下发逐行左缩进；全 0 保持 null（绘制端按 0 处理）
        leftArr = lefts.Any(v => MathF.Abs(v) > 0.01f) ? lefts.ToArray() : null;
        return lines.ToArray();
    }

    /// <summary>white-space 归一化：normal/nowrap 折叠空白；pre/pre-wrap/pre-line 保留空白与换行。
    /// keepLeadingSpace：段首空格是否保留——浏览器只在**行首**吞掉折叠后的那个空格，
    /// 量一个行内片段的内在宽时它前面还有兄弟（<c>li{content:" " counter}</c> 的「布局 ·1」），吞掉就少算一个空格。
    /// keepTrailingSpace：段尾空格同理——只有整行的最后一片段的尾空格会被吞，
    /// 中间片段（<c>含 &lt;span&gt;高亮&lt;/span&gt;</c>）的那个空格浏览器照算，内在宽少算它就差出一个空格宽，
    /// auto 表格列据此定宽时整列窄一截、内容被迫折行（table 样例实测 277.1 对 282.4）。</summary>
    static string Normalize(string text, string ws, bool keepLeadingSpace = false, bool keepTrailingSpace = false)
    {
        text = text.Replace("\r\n", "\n").Replace('\t', ' ');
        bool preserve = ws == "pre" || ws == "pre-wrap" || ws == "pre-line";
        if (ws == "pre" || ws == "pre-wrap") return text;   // 原样保留空格与换行
        if (ws == "pre-line")                               // 折叠空格但保留换行
            return CollapseSpacesKeepNewlines(text);
        // normal / nowrap：折叠所有空白（含换行→空格）
        return CollapseAll(text, keepLeadingSpace, keepTrailingSpace);
    }

    static string CollapseAll(string s, bool keepLeadingSpace = false, bool keepTrailingSpace = false)
    {
        var sb = new StringBuilder(s.Length);
        bool lastSpace = false;
        foreach (var c in s)
        {
            if (c == ' ' || c == '\n' || c == '\t')
            {
                if (!lastSpace && (sb.Length > 0 || keepLeadingSpace)) sb.Append(' ');
                lastSpace = true;
            }
            else { sb.Append(c); lastSpace = false; }
        }
        var r = sb.ToString();
        return keepTrailingSpace ? r : r.TrimEnd();
    }

    static string CollapseSpacesKeepNewlines(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastSpace = false;
        foreach (var c in s)
        {
            if (c == '\n') { sb.Append('\n'); lastSpace = false; }
            else if (c == ' ' || c == '\t') { if (!lastSpace) sb.Append(' '); lastSpace = true; }
            else { sb.Append(c); lastSpace = false; }
        }
        return sb.ToString();
    }

    static void WrapParagraph(string para, float maxWidth, SKFont font, float ls, string wordBreak,
        string overflowWrap, Action<string> take, Func<(float left, float right)> query, float firstIndent)
    {
        var current = new StringBuilder();
        float currentWidth = 0;
        var (inL, inR) = query();
        float lineMax = Math.Max(1, maxWidth - inL - inR - firstIndent);
        void EmitLine(string line)
        {
            take(line);
            (inL, inR) = query();
            lineMax = Math.Max(1, maxWidth - inL - inR);
        }

        var toks = Tokenize(para, wordBreak);
        // overflow-wrap 只在词宽过整行时生效：Chrome 先照常用词边界把词挪到新行，新行仍放不下才断词。
        bool maySplit = overflowWrap is "break-word" or "anywhere";
        for (int i = 0; i < toks.Count; i++)
        {
            string token = toks[i];
            float tokenWidth = TokenWidth(token, font, ls);
            // 只有当前行已有非空白内容时才换行；单独的空格允许溢出，避免留白行
            if (currentWidth + tokenWidth > lineMax && current.Length > 0 && !string.IsNullOrWhiteSpace(current.ToString()))
            {
                EmitLine(current.ToString().TrimEnd());
                current.Clear();
                currentWidth = 0;
                if (token == " ") continue; // 换行处的空格吞掉
            }
            if (maySplit && token != " " && tokenWidth > lineMax)
            {
                string rest = token;
                while (TokenWidth(rest, font, ls) > lineMax)
                {
                    int cut = LongestFit(rest, lineMax, font, ls);
                    if (cut <= 0) break;
                    EmitLine(rest.Substring(0, cut));
                    rest = rest.Substring(cut);
                }
                if (rest.Length == 0) continue;
                token = rest;
                tokenWidth = TokenWidth(token, font, ls);
            }
            current.Append(token);
            currentWidth += tokenWidth;
        }
        if (current.Length > 0) EmitLine(current.ToString().TrimEnd());
    }

    /// <summary>词内断行点：能放进 <paramref name="width"/> 的最长前缀长度（按字素簇，不吃 surrogate 半边）。
    /// 一个字素都放不下时仍返回第一个字素——Chrome 也至少塞一个字符进去，同时保证折行一定前进。</summary>
    static int LongestFit(string word, float width, SKFont font, float ls)
    {
        int len = 0, best = 0;
        foreach (var g in TextShaper.Graphemes(word))
        {
            int n = len + g.Length;
            if (best > 0 && TokenWidth(word.Substring(0, n), font, ls) > width) break;
            len = n;
            best = n;
        }
        return best;
    }

    static List<string> Tokenize(string s, string wordBreak)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        // 词内 '-' 后是断点（UAX#14 的 BA 类，Blink 照做：窄盒实测 "margin-top" → "margin-" / "top"），
        // `word-break: keep-all` 关掉。
        bool hyphBreak = wordBreak != "keep-all";
        foreach (var g in TextShaper.Graphemes(s)) // 字素簇：break-all 也不拆散 surrogate 对
        {
            if (g == " ")
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                tokens.Add(" ");
            }
            else if (wordBreak == "break-all")
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                tokens.Add(g);
            }
            else if (IsCjkChar(g[0]) && wordBreak != "keep-all")
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                tokens.Add(g);
            }
            else if (hyphBreak && g.Length == 1 && g[0] == '-' && sb.Length > 0 && IsWordChar(sb[^1]))
            {
                // 词中的 '-'：连同它前面的那段一起收尾，断点落在它之后（"border-radius" → "border-" + "radius"）
                sb.Append(g);
                tokens.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(g);
            }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    /// <summary>连字符前是「词字符」才算词内断点：ASCII 字母数字（"calc(2rem + 100px)" 的 '-' 前是空格，不算）。</summary>
    static bool IsWordChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    static bool IsCjk(char c) => IsCjkChar(c);

    /// <summary>是否 CJK / 全角码位（可在任意位置断行）。InlineLayout 也复用。</summary>
    public static bool IsCjkChar(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF)   // CJK Unified Ideographs
        || (c >= 0x3400 && c <= 0x4DBF) // CJK Ext A
        || (c >= 0x3000 && c <= 0x303F) // CJK 标点
        || (c >= 0xFF00 && c <= 0xFFEF) // 全角
        || (c >= 0x3040 && c <= 0x30FF) // 假名
        || (c >= 0xAC00 && c <= 0xD7AF) // 韩文
        || (c >= 0xF900 && c <= 0xFAFF); // CJK 兼容表意文字

    /// <summary>整串宽度（含 letter-spacing 与字体字距对）。空串→0。复杂文字/缺字形走 TextShaper 通路。</summary>
    public static float LineWidth(string s, SKFont font, float letterSpacing)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        float mono = TextShaper.MonoGridWidth(s, font.Typeface, font.Size, letterSpacing);
        if (mono >= 0) return mono;
        if (TextShaper.NeedsSpecial(s, font.Typeface))
            return TextShaper.WidthOf(s, font.Typeface, font.Size, letterSpacing);
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        return paint.MeasureText(s) + KernTable.Of(s, font.Typeface, font.Size)
            + letterSpacing * Math.Max(0, s.Length - 1);
    }

    static float TokenWidth(string token, SKFont font, float ls)
    {
        float mono = TextShaper.MonoGridWidth(token, font.Typeface, font.Size, ls);
        if (mono >= 0) return mono;
        if (TextShaper.NeedsSpecial(token, font.Typeface))
            return TextShaper.WidthOf(token, font.Typeface, font.Size, ls);
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        return paint.MeasureText(token) + KernTable.Of(token, font.Typeface, font.Size) + ls * Math.Max(0, token.Length - 1);
    }

    /// <summary>文本不换行时的自然宽度（取多行中最长的一行）。flex 主轴 base 用。</summary>
    public static float NaturalWidth(string text, SKFont font, ComputedStyle s, bool keepLeadingSpace = false, bool keepTrailingSpace = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        text = Normalize(text, s.WhiteSpace, keepLeadingSpace, keepTrailingSpace);
        float max = 0;
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            float mono = TextShaper.MonoGridWidth(line, font.Typeface!, font.Size, s.LetterSpacing);
            float w = mono >= 0 ? mono
                : TextShaper.NeedsSpecial(line, font.Typeface!)
                ? TextShaper.WidthOf(line, font.Typeface!, font.Size, s.LetterSpacing)
                : paint.MeasureText(line) + KernTable.Of(line, font.Typeface, font.Size)
                    + s.LetterSpacing * Math.Max(0, line.Length - 1);
            max = Math.Max(max, w);
        }
        return max;
    }
}
