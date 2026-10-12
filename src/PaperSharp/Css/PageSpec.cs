using System.Globalization;
using System.Text;
using SkiaSharp;

namespace PaperSharp.Css;

public enum ContentPartKind { Literal, PageNumber, TotalPages }

/// <summary>@page 边距盒（如 @bottom-center）content 值的一段：字面串 / counter(page) / counter(pages)。</summary>
public sealed class ContentPart
{
    public ContentPartKind Kind;
    public string? Text;
    public string CounterStyle = "decimal"; // decimal, cjk-ideographic, trad-chinese-informal, etc.
}

/// <summary>一个 @page 边距盒（页眉/页脚）。</summary>
public sealed class MarginBoxSpec
{
    public string Position = "";   // top-left / top-center / top-right / bottom-left / bottom-center / bottom-right
    public List<ContentPart> Content = new();
    public float FontSize = 10f;
    public string FontFamily = "sans-serif";
    public int FontWeight = 400;
    public string FontStyle = "normal";   // font-style：normal | italic | oblique
    public SKColor Color = SKColors.Gray;
}

/// <summary>
/// 解析后的 @page 规则：页面尺寸 + 页边距 + 页眉页脚边距盒。
/// **本类的数字一律是 CSS px（96dpi）= 引擎的布局单位**，与盒模型同源；
/// 出纸时才乘 <see cref="PtPerPx"/> 折算成 PDF 的 pt（72dpi）设备单位。
/// </summary>
public sealed class PageSpec
{
    /// <summary>1pt 等于多少 CSS px（96/72）。@page 里的 pt/in/mm 先进布局、再随画布整体折算。</summary>
    public const float PxPerPt = 96f / 72f;
    /// <summary>1 CSS px 等于多少 pt：PDF 设备换算系数。</summary>
    public const float PtPerPx = 72f / 96f;

    public float PageW = 595.28f * PxPerPt;   // 默认 A4
    public float PageH = 841.89f * PxPerPt;
    public float MarginLeft, MarginTop, MarginRight, MarginBottom;
    public List<MarginBoxSpec> Boxes = new();
    public bool HasRule;

    public float ContentW => PageW - MarginLeft - MarginRight;
    public float ContentH => PageH - MarginTop - MarginBottom;
    /// <summary>PDF 页宽（pt）：布局 px × 0.75。</summary>
    public float PdfPageW => PageW * PtPerPx;
    /// <summary>PDF 页高（pt）：边注/书签的 PDF 坐标也以此为准。</summary>
    public float PdfPageH => PageH * PtPerPx;

}

/// <summary>
/// 手写 @page 解析器（CSS Paged Media 的实用子集）。直接从 CSS 文本里用花括号配对提取，
/// 不依赖 AngleSharp 的分页媒体支持（其对 @bottom-center / counter() 覆盖不全）。
/// 支持：size（关键字 / 两长度 / landscape）、margin 简写与 margin-*、嵌套 @top/bottom-*{content;font-*;color}。
/// 长度单位换算到 CSS px（本引擎的布局单位，见 <see cref="PageSpec"/>）：
/// px→×1，pt→×96/72，mm→×96/25.4，cm→×96/2.54，in→×96。出纸时再统一乘 0.75 折成 pt。
/// </summary>
public static class AtPageParser
{
    // 纸张标称尺寸（pt，ISO 216 / 常用美制），× PxPerPt 进布局单位。
    static readonly Dictionary<string, (float w, float h)> Sizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a2"] = (1190.55f, 1683.78f),
        ["a3"] = (841.89f, 1190.55f),
        ["a4"] = (595.28f, 841.89f),
        ["a5"] = (419.53f, 595.28f),
        ["letter"] = (612f, 792f),
        ["legal"] = (612f, 1008f),
    };


    public static PageSpec Parse(string css, float defaultW, float defaultH)
    {
        var spec = new PageSpec { PageW = defaultW, PageH = defaultH };
        var cleaned = StripComments(css);

        int idx = 0;
        while ((idx = cleaned.IndexOf("@page", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int braceOpen = cleaned.IndexOf('{', idx);
            if (braceOpen < 0) break;
            int braceClose = MatchBrace(cleaned, braceOpen);
            if (braceClose < 0) break;
            string body = cleaned[(braceOpen + 1)..braceClose];
            ApplyPageBody(spec, body);
            spec.HasRule = true;
            idx = braceClose + 1;
        }
        return spec;
    }

    static void ApplyPageBody(PageSpec spec, string body)
    {
        SplitBlock(body, out var decls, out var nested);
        foreach (var (prop, value) in decls)
        {
            switch (prop.ToLowerInvariant())
            {
                case "size": ApplySize(spec, value); break;
                case "margin": ApplyMarginShorthand(spec, value); break;
                case "margin-top": spec.MarginTop = ToMarginPx(value); break;
                case "margin-right": spec.MarginRight = ToMarginPx(value); break;
                case "margin-bottom": spec.MarginBottom = ToMarginPx(value); break;
                case "margin-left": spec.MarginLeft = ToMarginPx(value); break;
            }
        }
        foreach (var (name, inner) in nested)
        {
            var box = TryParseMarginBox(name, inner);
            if (box != null) spec.Boxes.Add(box);
        }
    }

    static void ApplySize(PageSpec spec, string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool landscape = false;
        foreach (var p in parts)
            if (p.Equals("landscape", StringComparison.OrdinalIgnoreCase)) landscape = true;

        foreach (var p in parts)
        {
            if (Sizes.TryGetValue(p, out var s))
            {
                spec.PageW = s.w * PageSpec.PxPerPt; spec.PageH = s.h * PageSpec.PxPerPt;
                if (landscape) (spec.PageW, spec.PageH) = (spec.PageH, spec.PageW);
                return;
            }
        }
        // 两个长度：宽 高
        var lens = parts.Where(p => !p.Equals("landscape", StringComparison.OrdinalIgnoreCase)
                                    && !p.Equals("portrait", StringComparison.OrdinalIgnoreCase)
                                    && !p.Equals("auto", StringComparison.OrdinalIgnoreCase)).ToList();
        if (lens.Count >= 2)
        {
            spec.PageW = ToCssPx(lens[0]);
            spec.PageH = ToCssPx(lens[1]);
            if (landscape) (spec.PageW, spec.PageH) = (spec.PageH, spec.PageW);
        }
    }

    static void ApplyMarginShorthand(PageSpec spec, string value)
    {
        var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float T(string s) => ToMarginPx(s);
        float v0 = p.Length > 0 ? T(p[0]) : 0;
        float v1 = p.Length > 1 ? T(p[1]) : v0;
        float v2 = p.Length > 2 ? T(p[2]) : v0;
        float v3 = p.Length > 3 ? T(p[3]) : v1;
        spec.MarginTop = v0; spec.MarginRight = v1; spec.MarginBottom = v2; spec.MarginLeft = v3;
    }

    static MarginBoxSpec? TryParseMarginBox(string atName, string body)
    {
        string name = atName.TrimStart('@').Trim().ToLowerInvariant();
        if (name is not ("top-left" or "top-center" or "top-right"
                      or "bottom-left" or "bottom-center" or "bottom-right"
                      or "left-top" or "left-middle" or "left-bottom"
                      or "right-top" or "right-middle" or "right-bottom"))
            return null;

        var box = new MarginBoxSpec { Position = name };
        SplitBlock(body, out var decls, out _);
        foreach (var (prop, value) in decls)
        {
            switch (prop.ToLowerInvariant())
            {
                case "content": box.Content = ParseContent(value); break;
                // @page 边距盒的字号同口径：round 到 1/64（见 CssValueParser.SnapRound）
                case "font-size": box.FontSize = CssValueParser.SnapRound(ToCssPx(value)); break;
                case "font-family": box.FontFamily = value.Split(',')[0].Trim().Trim('\'', '"'); break;
                case "font-weight": box.FontWeight = int.TryParse(value, out var w) ? w : (value.Trim().Equals("bold", StringComparison.OrdinalIgnoreCase) ? 700 : 400); break;
                case "font-style": box.FontStyle = CssValueParser.ParseFontStyle(value, box.FontStyle); break;
                case "color": box.Color = CssValueParser.ParseColor(value) ?? box.Color; break;
            }
        }
        return box;
    }

    /// <summary>解析 content 值："第 " counter(page) " 页" → [Literal("第 "), PageNumber, Literal(" 页")]。</summary>
    static List<ContentPart> ParseContent(string value)
    {
        var parts = new List<ContentPart>();
        value = value.Trim();
        if (value == "none" || value == "normal") return parts;

        int i = 0;
        while (i < value.Length)
        {
            char c = value[i];
            if (c == '"' || c == '\'')
            {
                var quote = c; i++;
                var sb = new StringBuilder();
                while (i < value.Length && value[i] != quote)
                {
                    if (value[i] == '\\' && i + 1 < value.Length) { sb.Append(value[i + 1]); i += 2; }
                    else sb.Append(value[i++]);
                }
                i++; // 跳过收尾引号
                parts.Add(new ContentPart { Kind = ContentPartKind.Literal, Text = sb.ToString() });
            }
            else if (value.Length - i >= 8 && value.AsSpan(i, 8).Equals("counter(", StringComparison.OrdinalIgnoreCase))
            {
                int close = value.IndexOf(')', i);
                if (close < 0) break;
                string arg = value[(i + 8)..close].Trim();
                // counter(page) 或 counter(page, cjk-ideographic)
                var args = arg.Split(',', StringSplitOptions.TrimEntries);
                string counterName = args[0].ToLowerInvariant();
                string style = args.Length > 1 ? args[1].ToLowerInvariant() : "decimal";
                parts.Add(new ContentPart
                {
                    Kind = counterName == "pages" ? ContentPartKind.TotalPages : ContentPartKind.PageNumber,
                    CounterStyle = style,
                });
                i = close + 1;
            }
            else i++;
        }
        return parts;
    }

    // ── 工具 ────────────────────────────────────────────────

    /// <summary>把带单位长度转成 CSS px（本引擎布局单位）。px→×1，pt/in/mm/cm/pc/q→按 96dpi 折算，无单位→按 px。</summary>
    public static float ToCssPx(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return 0;
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare)) return bare;

        int cut = value.Length;
        while (cut > 0 && char.IsLetter(value[cut - 1])) cut--;   // 剥掉结尾的单位字母
        if (cut == 0 || cut == value.Length) return 0;
        string num = value[..cut];
        string unit = value[cut..].ToLowerInvariant();
        if (!float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0;
        return CssValueParser.AbsoluteUnitFactor(unit) is { } f ? v * f : v;
    }

    /// <summary>@page 边距：换到 CSS px 后再折到 Blink 的 1/64 网格（<b>截断</b>，作者长度口径）。
    /// 为什么要紧（2026-09-30 取证，台账 P0-6）：毫米边距落在两个整数之间（16mm = 60.4724 → 浏览器
    /// 存 60.46875），边距同时决定**内容宽**与**整页落笔相位**，引擎不折就和浏览器差 0.0073px 宽——
    /// 页面上每条右对齐/靠 flex 分配的线都因此可能整体错 1 CSS px（report/pct 的残余位移带）。</summary>
    static float ToMarginPx(string value) => CssValueParser.Snap(ToCssPx(value));

    static string StripComments(string s)
    {
        if (!s.Contains("/*")) return s;
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
            }
            else sb.Append(s[i++]);
        }
        return sb.ToString();
    }

    static int MatchBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    /// <summary>把一个块体拆成顶层声明 (prop,value) 与嵌套 at-rule (name, body)。</summary>
    static void SplitBlock(string body, out List<(string prop, string value)> decls, out List<(string name, string inner)> nested)
    {
        decls = new List<(string, string)>();
        nested = new List<(string, string)>();
        int i = 0, n = body.Length;
        int stmtStart = 0;
        while (i < n)
        {
            char c = body[i];
            if (c == '@')
            {
                // 先收尾之前的声明片段
                FlushDecl(body.AsSpan(stmtStart, i - stmtStart), decls);
                int j = i; while (j < n && body[j] != '{') j++;
                if (j >= n) break;
                string name = body[i..j].Trim();
                int close = MatchBrace(body, j);
                if (close < 0) break;
                nested.Add((name, body[(j + 1)..close]));
                i = close + 1; stmtStart = i;
            }
            else if (c == ';')
            {
                FlushDecl(body.AsSpan(stmtStart, i - stmtStart), decls);
                i++; stmtStart = i;
            }
            else i++;
        }
        FlushDecl(body.AsSpan(stmtStart, n - stmtStart), decls);
    }

    static void FlushDecl(ReadOnlySpan<char> stmt, List<(string prop, string value)> decls)
    {
        string s = stmt.ToString().Trim();
        if (s.Length == 0) return;
        int colon = s.IndexOf(':');
        if (colon <= 0) return;
        decls.Add((s[..colon].Trim(), s[(colon + 1)..].Trim()));
    }
}
