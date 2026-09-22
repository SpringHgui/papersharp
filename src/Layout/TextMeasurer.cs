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
    {
        var lines = new List<string>();
        float ls = s.LetterSpacing;
        string ws = s.WhiteSpace;

        text = Normalize(text, ws);

        // nowrap / pre：不按宽度折行，仅按显式换行切
        bool noWrap = ws == "nowrap" || ws == "pre";
        if (noWrap || maxWidth <= 0)
        {
            foreach (var para in text.Split('\n')) lines.Add(para);
            if (lines.Count == 0) lines.Add("");
            return lines.ToArray();
        }

        foreach (var para in text.Split('\n'))
        {
            if (para.Length == 0) { lines.Add(""); continue; }
            WrapParagraph(para, maxWidth, font, ls, s.WordBreak, lines);
        }
        if (lines.Count == 0) lines.Add("");
        return lines.ToArray();
    }

    /// <summary>white-space 归一化：normal/nowrap 折叠空白；pre/pre-wrap/pre-line 保留空白与换行。</summary>
    static string Normalize(string text, string ws)
    {
        text = text.Replace("\r\n", "\n").Replace('\t', ' ');
        bool preserve = ws == "pre" || ws == "pre-wrap" || ws == "pre-line";
        if (ws == "pre" || ws == "pre-wrap") return text;   // 原样保留空格与换行
        if (ws == "pre-line")                               // 折叠空格但保留换行
            return CollapseSpacesKeepNewlines(text);
        // normal / nowrap：折叠所有空白（含换行→空格）
        return CollapseAll(text);
    }

    static string CollapseAll(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastSpace = false;
        foreach (var c in s)
        {
            if (c == ' ' || c == '\n' || c == '\t')
            {
                if (!lastSpace && sb.Length > 0) sb.Append(' ');
                lastSpace = true;
            }
            else { sb.Append(c); lastSpace = false; }
        }
        return sb.ToString().TrimEnd();
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

    static void WrapParagraph(string para, float maxWidth, SKFont font, float ls, string wordBreak, List<string> result)
    {
        var current = new StringBuilder();
        float currentWidth = 0;
        foreach (var token in Tokenize(para, wordBreak))
        {
            float tokenWidth = TokenWidth(token, font, ls);
            // 只有当前行已有非空白内容时才换行；单独的空格允许溢出，避免留白行
            if (currentWidth + tokenWidth > maxWidth && current.Length > 0 && !string.IsNullOrWhiteSpace(current.ToString()))
            {
                result.Add(current.ToString().TrimEnd());
                current.Clear();
                currentWidth = 0;
                if (token == " ") continue; // 换行处的空格吞掉
            }
            current.Append(token);
            currentWidth += tokenWidth;
        }
        if (current.Length > 0) result.Add(current.ToString().TrimEnd());
    }

    static IEnumerable<string> Tokenize(string s, string wordBreak)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c == ' ')
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                tokens.Add(" ");
            }
            else if (wordBreak == "break-all")
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                tokens.Add(c.ToString());
            }
            else if (IsCjk(c) && wordBreak != "keep-all")
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                tokens.Add(c.ToString());
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

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

    /// <summary>整串宽度（含字距）。空串→0。</summary>
    public static float LineWidth(string s, SKFont font, float letterSpacing)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        return paint.MeasureText(s) + letterSpacing * Math.Max(0, s.Length - 1);
    }

    static float TokenWidth(string token, SKFont font, float ls)
    {
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        return paint.MeasureText(token) + ls * Math.Max(0, token.Length - 1);
    }

    /// <summary>文本不换行时的自然宽度（取多行中最长的一行）。flex 主轴 base 用。</summary>
    public static float NaturalWidth(string text, SKFont font, ComputedStyle s)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        text = Normalize(text, s.WhiteSpace);
        float max = 0;
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        foreach (var line in text.Split('\n'))
            max = Math.Max(max, paint.MeasureText(line) + s.LetterSpacing * Math.Max(0, line.Length - 1));
        return max;
    }
}
