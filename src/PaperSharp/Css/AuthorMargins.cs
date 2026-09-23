using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace PaperSharp.Css;

/// <summary>
/// 作者 margin 声明的微级联。AngleSharp 把内置 UA 表当普通样式源参与特异度比较（实测 h4 的
/// margin-block-start 会同时污染 GetComputedStyle 与 GetDeclarations），作者通用的
/// `* { margin:0 }` 重置因此输给 UA 的 `h4` 规则——浏览器里作者源恒压 UA。
/// 作者 CSS 真声明了 margin 时由本类按 特异度+源顺序+@media+内联style 自行裁决；
/// 没声明（返回 false）则退回 AngleSharp 的读取，UA 默认值照旧生效。
/// </summary>
sealed class AuthorMargins
{
    sealed class Rule
    {
        public string Selector = "";
        public int Spec;
        public float MaxW = float.MaxValue, MinW = float.MinValue;
        public string?[] Val = new string?[4];   // top/right/bottom/left；null=该规则未声明此边
        public int Order;                        // 源顺序平票裁决
    }

    readonly List<Rule> _any = new();                                   // 通配选择器（*、html *）
    readonly Dictionary<string, List<Rule>> _byTag = new();
    readonly Dictionary<string, List<Rule>> _byClass = new();
    readonly Dictionary<string, List<Rule>> _byId = new();
    int _count;

    static readonly Regex MarginDecl = new(@"(^|[{;])\s*(margin(-(top|right|bottom|left))?)\s*:", RegexOptions.IgnoreCase);

    /// <summary>解析作者 CSS。返回 null 表示整份没有 margin 声明（零接管，走老路）。</summary>
    public static AuthorMargins? Parse(string css)
    {
        if (!Regex.IsMatch(css, @"margin", RegexOptions.IgnoreCase)) return null;
        var am = new AuthorMargins();
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var stack = new Stack<(float maxW, float minW, bool skip)>();
        int i = 0;
        while (i < css.Length)
        {
            char c = css[i];
            if (c == '{')
            {
                string header = css[(LastBoundary(css, i - 1) + 1)..i].Trim();
                int end = MatchBrace(css, i);
                if (header.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
                {
                    bool ok = TryMediaWidths(header, out float mw, out float mn);
                    stack.Push(ok ? (mw, mn, false) : (float.MaxValue, float.MinValue, true));
                    i++;                                   // 继续扫 @media 内部
                    continue;
                }
                if (!header.StartsWith('@'))               // 普通规则（@keyframes/@font-face 等整块跳过）
                {
                    var (mw, mn, skip) = stack.Count > 0 ? stack.Peek() : (float.MaxValue, float.MinValue, false);
                    if (!skip) am.AddRules(header, css[(i + 1)..end], mw, mn);
                }
                i = end + 1;                               // 规则块原子消费，含其配对的 '}'
                continue;
            }
            if (c == '}' && stack.Count > 0) stack.Pop();  // 只可能是 @media 的收尾（规则块的 '}' 已被跳过）
            i++;
        }
        return am._count > 0 ? am : null;
    }

    void AddRules(string header, string body, float maxW, float minW)
    {
        if (header.Contains("::")) return;   // 伪元素不适用（其 margin 走各自路径）
        string[] decls = body.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawSel in header.Split(','))
        {
            string sel = rawSel.Trim();
            if (sel.Length == 0) continue;
            Rule? r = null;
            foreach (string d in decls)
            {
                int colon = d.IndexOf(':');
                if (colon <= 0) continue;
                string prop = d[..colon].Trim().ToLowerInvariant();
                string val = d[(colon + 1)..].Trim();
                if (val.Length == 0 || val.Contains("var(")) continue;
                r ??= new Rule { Selector = sel, Spec = Specificity(sel), MaxW = maxW, MinW = minW, Order = _count++ };
                switch (prop)
                {
                    case "margin": ExpandShorthand(val, r.Val); break;
                    case "margin-top": r.Val[0] = val; break;
                    case "margin-right": r.Val[1] = val; break;
                    case "margin-bottom": r.Val[2] = val; break;
                    case "margin-left": r.Val[3] = val; break;
                    default: continue;
                }
            }
            if (r == null) continue;
            // 索引桶 = 选择器最后一个简单段的主 token；查询时只对候选跑 Matches() 全量核对
            string last = r.Selector;
            foreach (char sep in new[] { ' ', '>', '+', '~' })
            {
                int k = last.LastIndexOf(sep);
                if (k >= 0) last = last[(k + 1)..];
            }
            last = last.Trim();
            int cut = last.IndexOfAny(new[] { ':', '[' });
            if (cut >= 0) last = last[..cut];
            if (last.Length == 0 || last == "*") _any.Add(r);
            else if (last[0] == '.') { Add(_byClass, last[1..].Replace(".", " "), r); }
            else if (last[0] == '#') Add(_byId, last[1..], r);
            else Add(_byTag, last.ToLowerInvariant(), r);
        }
    }

    static void Add(Dictionary<string, List<Rule>> map, string keySpace, Rule r)
    {
        foreach (string k in keySpace.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!map.TryGetValue(k, out var list)) map[k] = list = new List<Rule>();
            if (!list.Contains(r)) list.Add(r);
        }
    }

    /// <summary>取元素的 margin 声明值（作者源级联）。prop ∈ margin-top/right/bottom/left。</summary>
    public bool TryGet(IElement el, float viewportW, string prop, out string value)
    {
        int idx = prop switch { "margin-top" => 0, "margin-right" => 1, "margin-bottom" => 2, "margin-left" => 3, _ => -1 };
        value = "";
        if (idx < 0) return false;

        // 内联 style 最优先
        string inline = el.GetAttribute("style") ?? "";
        if (Regex.IsMatch(inline, @"(^|;)\s*margin(-\w+)?\s*:", RegexOptions.IgnoreCase))
        {
            string[] v4 = new string[4];
            bool any = false;
            foreach (string d in inline.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = d.IndexOf(':');
                if (colon <= 0) continue;
                string p = d[..colon].Trim().ToLowerInvariant(), val = d[(colon + 1)..].Trim();
                if (val.Contains("var(")) continue;
                if (p == "margin") { ExpandShorthand(val, v4); any = true; }
                else if (p == prop) { value = val; return true; }
            }
            if (any) { value = v4[idx]; return v4[idx].Length > 0; }
        }

        Rule best = null!;
        int bestSpec = -1, bestOrder = -1;
        void Scan(List<Rule>? list)
        {
            if (list == null) return;
            foreach (var r in list)
            {
                if (r.Val[idx] == null || r.MaxW < viewportW || r.MinW > viewportW) continue;
                if (r.Spec < bestSpec || (r.Spec == bestSpec && r.Order < bestOrder)) continue;   // 已被更优者压制
                bool m;
                try { m = el.Matches(r.Selector); } catch { continue; }
                if (!m) continue;
                best = r; bestSpec = r.Spec; bestOrder = r.Order;
            }
        }
        Scan(_any);
        Scan(_byTag.TryGetValue(el.TagName.ToLowerInvariant(), out var t) ? t : null);
        string? id = el.Id;
        if (!string.IsNullOrEmpty(id)) Scan(_byId.TryGetValue(id, out var i) ? i : null);
        foreach (string cls in el.ClassList) Scan(_byClass.TryGetValue(cls, out var c) ? c : null);

        if (best == null!) return false;
        value = best.Val[idx]!;
        return true;
    }

    static void ExpandShorthand(string val, string?[] into)
    {
        string[] p = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (t, r, b, l) = p.Length switch
        {
            1 => (p[0], p[0], p[0], p[0]),
            2 => (p[0], p[1], p[0], p[1]),
            3 => (p[0], p[1], p[2], p[1]),
            _ => (p[0], p[1], p[2], p[3]),
        };
        into[0] = t; into[1] = r; into[2] = b; into[3] = l;
    }

    static int Specificity(string sel)
    {
        int id = Regex.Matches(sel, @"#[\w-]+").Count;
        int cls = Regex.Matches(sel, @"(\.[\w-]+|\[[^\]]+\]|:(?!:)[\w-]+(\([^)]*\))?)").Count;
        int tag = Regex.Matches(sel, @"(?<![\w.-])[a-z][\w-]*(?![\w-]*\s*[.#\[:])", RegexOptions.IgnoreCase).Count;
        return id * 1_000_000 + cls * 1_000 + tag;
    }

    static bool TryMediaWidths(string header, out float maxW, out float minW)
    {
        maxW = float.MaxValue; minW = float.MinValue;
        bool found = false;
        foreach (Match m in Regex.Matches(header, @"\(\s*(max|min)-width\s*:\s*([\d.]+)px\s*\)", RegexOptions.IgnoreCase))
        {
            float v = float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (m.Groups[1].Value.Equals("max", StringComparison.OrdinalIgnoreCase)) maxW = Math.Min(maxW, v);
            else minW = Math.Max(minW, v);
            found = true;
        }
        // 只带 width 条件的 @media 才接管；含其他条件（orientation/print 等）交回 AngleSharp
        string rest = Regex.Replace(header, @"\(\s*(max|min)-width\s*:\s*[\d.]+px\s*\)", "", RegexOptions.IgnoreCase);
        return found && !Regex.IsMatch(rest, @"(orientation|aspect-ratio|prefers-|color|print|screen)", RegexOptions.IgnoreCase);
    }

    // '{' 也须算边界：否则 @media 块内第一条规则的 header 会把 "@media … {" 整段吞进来，被误判成再嵌一层媒体。
    static int LastBoundary(string s, int before) => before < 0 ? -1 : s.LastIndexOfAny(new[] { '}', ';', '{' }, before);

    static int MatchBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return s.Length;
    }
}
