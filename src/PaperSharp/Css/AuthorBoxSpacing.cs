using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace PaperSharp.Css;

/// <summary>
/// 作者 margin / padding 声明的微级联。AngleSharp 把内置 UA 表当普通样式源参与特异度比较（实测 h4 的
/// margin-block-start 会同时污染 GetComputedStyle 与 GetDeclarations），作者通用的
/// `* { margin:0; padding:0 }` 重置因此输给 UA 的 `h4`/`ul` 规则——浏览器里作者源恒压 UA。
/// 作者 CSS 真声明了这些属性时由本类按 特异度+源顺序+@media+内联style 自行裁决；
/// 没声明（返回 false）则退回 AngleSharp 的读取，UA 默认值照旧生效。
///
/// margin 与 padding 必须走同一套：本引擎自己的 UA 表（StyleResolver.UaStylesheet）里写着
/// `ul, ol { margin:1em 0; padding-left:40px }`，只裁 margin 时作者的 `* { padding:0 }` 在引擎侧
/// 仍输给特异度更高的 `ul`，而浏览器侧那份是 @layer（未分层作者源恒压）——两侧就此分叉
/// （pricing-top/page 实测整棵子树横移）。
/// </summary>
sealed class AuthorBoxSpacing
{
    /// <summary>Val 下标：0-3 = margin top/right/bottom/left，4-7 = padding 同序。</summary>
    const int PadBase = 4;

    sealed class Rule
    {
        public string Selector = "";
        public int Spec;
        public float MaxW = float.MaxValue, MinW = float.MinValue;
        public string?[] Val = new string?[8];   // null=该规则未声明此边/此属性
        public int Order;                        // 源顺序平票裁决
    }

    readonly List<Rule> _any = new();                                   // 通配选择器（*、html *）
    readonly Dictionary<string, List<Rule>> _byTag = new();
    readonly Dictionary<string, List<Rule>> _byClass = new();
    readonly Dictionary<string, List<Rule>> _byId = new();
    int _count;

    static readonly Regex SpacingDecl = new(@"(^|[{;])\s*((margin|padding)(-(top|right|bottom|left))?)\s*:", RegexOptions.IgnoreCase);

    /// <summary>解析作者 CSS。返回 null 表示整份没有 margin/padding 声明（零接管，走老路）。</summary>
    public static AuthorBoxSpacing? Parse(string css)
    {
        if (!Regex.IsMatch(css, @"margin|padding", RegexOptions.IgnoreCase)) return null;
        var am = new AuthorBoxSpacing();
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
        if (header.Contains("::")) return;   // 伪元素不适用（其 margin/padding 走各自路径）
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
                    case "margin": ExpandShorthand(val, r.Val, 0); break;
                    case "margin-top": r.Val[0] = val; break;
                    case "margin-right": r.Val[1] = val; break;
                    case "margin-bottom": r.Val[2] = val; break;
                    case "margin-left": r.Val[3] = val; break;
                    case "padding": ExpandShorthand(val, r.Val, PadBase); break;
                    case "padding-top": r.Val[4] = val; break;
                    case "padding-right": r.Val[5] = val; break;
                    case "padding-bottom": r.Val[6] = val; break;
                    case "padding-left": r.Val[7] = val; break;
                    default: continue;
                }
            }
            if (r == null) continue;
            // 索引桶 = 末段简单选择器的**每个**主 token（tag / .class / #id）；一个都不剩才归通配。
            // 只认第一个 token 会把 "h2.sec" 整串当标签名建桶（查询按 el.TagName="h2" 取），
            // 于是 `.band h2.sec{margin-bottom:44px}` 永不被扫到，反让 `*{margin:0}` 独赢 → 整页下坠。
            string last = r.Selector;
            foreach (char sep in new[] { ' ', '>', '+', '~' })
            {
                int k = last.LastIndexOf(sep);
                if (k >= 0) last = last[(k + 1)..];
            }
            last = last.Trim();
            int cut = last.IndexOfAny(new[] { ':', '[' });
            if (cut >= 0) last = last[..cut];
            bool bucketed = false;
            var tagM = Regex.Match(last, @"^[a-zA-Z][\w-]*");
            if (tagM.Success) { Add(_byTag, tagM.Value.ToLowerInvariant(), r); bucketed = true; }
            foreach (Match cm in Regex.Matches(last, @"\.[\w-]+")) { Add(_byClass, cm.Value[1..], r); bucketed = true; }
            foreach (Match im in Regex.Matches(last, @"#[\w-]+")) { Add(_byId, im.Value[1..], r); bucketed = true; }
            if (!bucketed) _any.Add(r);
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

    /// <summary>取元素的 margin/padding 声明值（作者源级联）。prop ∈ (margin|padding)-(top|right|bottom|left)。</summary>
    public bool TryGet(IElement el, float viewportW, string prop, out string value)
    {
        int idx = prop switch
        {
            "margin-top" => 0, "margin-right" => 1, "margin-bottom" => 2, "margin-left" => 3,
            "padding-top" => 4, "padding-right" => 5, "padding-bottom" => 6, "padding-left" => 7,
            _ => -1,
        };
        value = "";
        if (idx < 0) return false;

        // 内联 style 最优先
        string inline = el.GetAttribute("style") ?? "";
        if (SpacingDecl.IsMatch(inline))
        {
            string[] v8 = new string[8];
            bool any = false;
            foreach (string d in inline.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = d.IndexOf(':');
                if (colon <= 0) continue;
                string p = d[..colon].Trim().ToLowerInvariant(), val = d[(colon + 1)..].Trim();
                if (val.Contains("var(")) continue;
                int baseIdx = p.StartsWith("padding") ? PadBase : 0;
                if (p == "margin" || p == "padding") { ExpandShorthand(val, v8, baseIdx); any = true; }
                else if (p == prop) { value = val; return true; }
            }
            if (any) { value = v8[idx]; return v8[idx].Length > 0; }
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

    static void ExpandShorthand(string val, string?[] into, int at)
    {
        string[] p = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (t, r, b, l) = p.Length switch
        {
            1 => (p[0], p[0], p[0], p[0]),
            2 => (p[0], p[1], p[0], p[1]),
            3 => (p[0], p[1], p[2], p[1]),
            _ => (p[0], p[1], p[2], p[3]),
        };
        into[at] = t; into[at + 1] = r; into[at + 2] = b; into[at + 3] = l;
    }

    /// <summary>
    /// 特异度 (a,b,c) 压成 a·1000²+b·1000+c。类型选择器要先剥掉 #id/.类/:伪/[属性] 再数剩余词，
    /// 否则 "h2.sec" 的 h2 会被后面的 . 吞掉（漏算 c → 与 ".sec .sec" 打平，源顺序反而错判赢家）。
    /// </summary>
    static int Specificity(string sel)
    {
        int id = Regex.Matches(sel, @"#[\w-]+").Count;
        int cls = Regex.Matches(sel, @"(\.[\w-]+|\[[^\]]+\]|:(?!:)[\w-]+(\([^)]*\))?)").Count;
        string bare = Regex.Replace(sel, @"#[\w-]+|\.[\w-]+|\[[^\]]+\]|::?[\w-]+(\([^)]*\))?", " ");
        int tag = Regex.Matches(bare, @"[a-zA-Z][\w-]*").Count;   // '*' 不以字母开头，天然不计数
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
