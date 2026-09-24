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

    /// <summary>
    /// 一次扫描求出该元素八条边（margin t/r/b/l + padding t/r/b/l）的作者声明值；条目为 null 表示
    /// 作者没声明这一条，调用方退回 AngleSharp 的读取（UA 默认值照旧生效）。
    ///
    /// 刻意做成「一次算全八条」而不是「按属性问八次」：每次查询都要重跑一遍规则桶，桶里每条规则
    /// 都要 <c>el.Matches(selector)</c> 做一次真正的选择器匹配。一个元素 margin+padding 八条边问下来
    /// 就是 8×规则数 次匹配。现在一条规则每个元素最多匹配一次，八条边在同一趟里各自裁决。
    /// </summary>
    public string?[] Resolve(IElement el, float viewportW)
    {
        var v8 = new string?[8];

        // 内联 style 最优先：整条 longhand 压过简写，简写之间后写的覆盖先写的（与逐个查询时的语义一致）
        string inlineStyle = el.GetAttribute("style") ?? "";
        bool touchedPadding = false, touchedMargin = false;
        string?[] exact = new string?[8];
        string?[] short8 = new string?[8];
        if (inlineStyle.Length > 0 && SpacingDecl.IsMatch(inlineStyle))
        {
            foreach (string d in inlineStyle.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = d.IndexOf(':');
                if (colon <= 0) continue;
                string p = d[..colon].Trim().ToLowerInvariant(), val = d[(colon + 1)..].Trim();
                if (val.Contains("var(")) continue;
                switch (p)
                {
                    case "margin": ExpandShorthand(val, short8, 0); touchedMargin = true; break;
                    case "padding": ExpandShorthand(val, short8, PadBase); touchedPadding = true; break;
                    case "margin-top": exact[0] ??= val; break;
                    case "margin-right": exact[1] ??= val; break;
                    case "margin-bottom": exact[2] ??= val; break;
                    case "margin-left": exact[3] ??= val; break;
                    case "padding-top": exact[4] ??= val; break;
                    case "padding-right": exact[5] ??= val; break;
                    case "padding-bottom": exact[6] ??= val; break;
                    case "padding-left": exact[7] ??= val; break;
                }
            }
            for (int i = 0; i < 8; i++)
            {
                bool hit = (i < PadBase ? touchedMargin : touchedPadding) || exact[i] != null;
                if (!hit) continue;                       // 这一条内联没写 → 让规则桶继续裁决
                v8[i] = exact[i] ?? short8[i];
            }
        }

        // 未被内联覆盖的条目才进规则桶；八条全被内联裁决完就一次匹配都不做
        if (Open(v8)) ScanRules(el, viewportW, v8);
        return v8;
    }

    static bool Open(string?[] v8)
    {
        for (int i = 0; i < 8; i++) if (v8[i] is not { Length: > 0 }) return true;
        return false;
    }

    void ScanRules(IElement el, float viewportW, string?[] v8)
    {
        var best = new Rule?[8];
        var bestSpec = new int[8];
        var bestOrder = new int[8];
        Array.Fill(bestSpec, -1);
        // 八条边各自独立裁决：内联已定的不再参与，其余每条边各自挑「特异度最高、源顺序最后」的那条规则。
        var open = new bool[8];
        for (int i = 0; i < 8; i++) open[i] = v8[i] is not { Length: > 0 };

        void Scan(List<Rule>? list)
        {
            if (list == null) return;
            foreach (var r in list)
            {
                if (r.MaxW < viewportW || r.MinW > viewportW) continue;
                bool any = false;
                for (int i = 0; i < 8; i++)
                    if (open[i] && r.Val[i] != null && Better(r, i, best, bestSpec, bestOrder)) { any = true; break; }
                if (!any) continue;                       // 这条规则管不到任何还没裁决的边，省掉一次匹配
                if (!Matches(el, r.Selector)) continue;
                // 逐条边重新判定：本规则可能对某条边是更优者、对另一条边反而不如已胜出者，
                // 不能因为「有一条边更优」就把八条边一起覆盖了（invoice 实测因此少坍缩 8px）。
                for (int i = 0; i < 8; i++)
                    if (open[i] && r.Val[i] != null && Better(r, i, best, bestSpec, bestOrder))
                    { best[i] = r; bestSpec[i] = r.Spec; bestOrder[i] = r.Order; }
            }
        }

        Scan(_any);
        Scan(_byTag.TryGetValue(el.TagName.ToLowerInvariant(), out var t) ? t : null);
        if (!string.IsNullOrEmpty(el.Id)) Scan(_byId.TryGetValue(el.Id, out var i) ? i : null);
        foreach (string cls in el.ClassList) Scan(_byClass.TryGetValue(cls, out var c) ? c : null);

        for (int k = 0; k < 8; k++) if (best[k] is { } r && r.Val[k] is { } val) v8[k] = val;
    }

    /// <summary>该边尚未裁决，或本规则的特异度/源顺序更高。</summary>
    static bool Better(Rule r, int i, Rule?[] best, int[] spec, int[] order) =>
        best[i] == null || r.Spec > spec[i] || (r.Spec == spec[i] && r.Order > order[i]);

    /// <summary>作者写了奇形怪状的选择器时 AngleSharp 会抛；按「不匹配」处理，与逐条查询时一致。</summary>
    static bool Matches(IElement el, string selector)
    {
        try { return el.Matches(selector); } catch { return false; }
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
