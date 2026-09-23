using AngleSharp.Dom;

namespace PaperSharp.Css;

/// <summary>
/// 展平快照（扩展端 capture.js 产物）专用的级联。
///
/// 展平器已经把浏览器算好的 used value 写成「一条规则一个类」的扁平表（body / .ps-N / .ps-N::before|after），
/// 里面没有特异度竞争、没有组合器、没有相对单位。这类输入继续走 AngleSharp 的逐元素层叠纯属白烧：
/// 实测 native 1.82 ms/元素（932 元素 1.7 s），WASM 下再放大数倍（扩展端 904 元素的快照引擎侧要 27 s）。
/// 这里换成「类名 → 声明表」的 O(1) 命中 + 可继承属性沿父链上溯。
///
/// 安全边界：TryBuild 只在整份 CSS 都落在上述形状时才成功；出现 at-rule、组合器、伪类、多选择器、
/// !important 等任一情况即返回 null，调用方整份回退 AngleSharp —— 宁可不加速，也不能改变结果。
/// </summary>
public sealed class FlatCascade
{
    /// <summary>
    /// 展平端「与父元素同值就不下发」的那批可继承属性。缺值时必须沿父链上溯，否则整页的
    /// 行高/字距/对齐会退回初值（AngleSharp 路径下这层继承是它顺手做的）。
    /// 集合与 capture.js 的 INHERITED 一一对应：多列会凭空继承到浏览器并未给的属性，少列会丢继承。
    /// </summary>
    static readonly HashSet<string> Inherited = new(StringComparer.OrdinalIgnoreCase)
    {
        "color", "font-family", "font-size", "font-weight", "line-height", "letter-spacing",
        "text-align", "text-transform", "white-space", "word-break", "text-overflow", "direction", "text-wrap",
    };

    readonly Dictionary<string, Dictionary<string, string>> _class = new(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<string, string>> _pseudo = new(StringComparer.Ordinal);
    Dictionary<string, string>? _html;   // html 规则：作为 body 的继承基底合并进去（body 是被 Walk 的根）
    Dictionary<string, string>? _body;

    /// <summary>解析展平 CSS。返回 null = 这份 CSS 不是纯展平形状，调用方按老路（AngleSharp）走。</summary>
    public static FlatCascade? TryBuild(string css)
    {
        if (string.IsNullOrWhiteSpace(css)) return null;
        css = System.Text.RegularExpressions.Regex.Replace(css, @"/\*.*?\*/", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        var f = new FlatCascade();
        int i = 0, n = css.Length;
        while (i < n)
        {
            while (i < n && (char.IsWhiteSpace(css[i]) || css[i] == ';')) i++;
            if (i >= n) break;
            int s0 = i;
            while (i < n && css[i] != '{') i++;
            if (i >= n) return null;                      // 有选择器没有块
            var sel = css[s0..i].Trim();
            i++;

            int b0 = i;
            char qc = '\0';
            while (i < n)
            {
                char c = css[i];
                if (qc != 0) { if (c == qc) qc = '\0'; }
                else if (c == '"' || c == '\'') qc = c;
                else if (c == '{') return null;   // 嵌套块 = @media/@supports，不是展平形状
                else if (c == '}') break;
                i++;
            }
            if (i >= n) return null;              // 块没闭合
            var body = css[b0..i];
            i++;

            if (!f.AddRule(sel, body)) return null;
        }
        if (f._class.Count == 0 && f._pseudo.Count == 0 && f._html == null && f._body == null) return null;

        // html 规则并入 body 基底：展平器把 body 相对 html 的同值继承属性省掉了，
        // 而被 Walk 的根就是 body（AngleSharp 那边 html 仍会继承给它）。
        if (f._html != null)
        {
            var baseMap = new Dictionary<string, string>(f._html, StringComparer.OrdinalIgnoreCase);
            if (f._body != null) foreach (var kv in f._body) baseMap[kv.Key] = kv.Value;
            f._body = baseMap;
        }
        return f;
    }

    /// <summary>收一条规则。选择器不是「.类」/「.类::before|after」/「body」/「html」，或值里有 !important，即返回 false（整份回退）。</summary>
    bool AddRule(string sel, string body)
    {
        Dictionary<string, string> decl;
        if (sel.Length > 1 && sel[0] == '.')
        {
            var key = sel[1..];
            string? ps = null;
            int c = key.IndexOf(':');
            if (c >= 0)
            {
                if (c + 1 >= key.Length || key[c + 1] != ':') return false;   // 单冒号 = 伪类，不做
                ps = key[(c + 2)..].ToLowerInvariant();
                if (ps != "before" && ps != "after") return false;
                key = key[..c];
            }
            if (!IsIdent(key)) return false;   // 组合器 / 逗号 / 属性选择器 / 通配都到不了声明
            decl = GetOrAdd(ps == null ? _class : _pseudo, ps == null ? key : key + "::" + ps);
        }
        else if (sel.Equals("body", StringComparison.OrdinalIgnoreCase))
            decl = _body ??= new(StringComparer.OrdinalIgnoreCase);
        else if (sel.Equals("html", StringComparison.OrdinalIgnoreCase))
            decl = _html ??= new(StringComparer.OrdinalIgnoreCase);
        else return false;

        return ParseDecls(body, decl);
    }

    static Dictionary<string, string> GetOrAdd(Dictionary<string, Dictionary<string, string>> map, string key)
    {
        if (!map.TryGetValue(key, out var d)) map[key] = d = new(StringComparer.OrdinalIgnoreCase);
        return d;
    }

    static bool IsIdent(string s)
    {
        if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_' || s[0] == '-')) return false;
        foreach (var c in s) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return false;
        return true;
    }

    /// <summary>拆声明块（引号感知，值里可含 ; 与 }）。出现 !important 或畸形声明即判失败。</summary>
    static bool ParseDecls(string body, Dictionary<string, string> into)
    {
        int i = 0, n = body.Length;
        while (i < n)
        {
            int s0 = i;
            char qc = '\0';
            while (i < n)
            {
                char c = body[i];
                if (qc != 0) { if (c == qc) qc = '\0'; }
                else if (c == '"' || c == '\'') qc = c;
                else if (c == ';') break;
                i++;
            }
            var decl = body[s0..(i < n ? i : n)].Trim();
            i = Math.Min(n, i + 1);
            if (decl.Length == 0) continue;

            int colon = -1, d2 = 0; char q2 = '\0';
            for (int k = 0; k < decl.Length; k++)
            {
                char c = decl[k];
                if (q2 != 0) { if (c == q2) q2 = '\0'; }
                else if (c == '"' || c == '\'') q2 = c;
                else if (c == '(') d2++;
                else if (c == ')') d2--;
                else if (c == ':' && d2 == 0 && q2 == 0) { colon = k; break; }
            }
            if (colon <= 0) return false;
            var prop = decl[..colon].Trim();
            var val = decl[(colon + 1)..].Trim();
            if (val.EndsWith("!important", StringComparison.OrdinalIgnoreCase)) return false;
            if (!(prop.StartsWith("--") || IsIdent(prop))) return false;
            into[prop] = val;
        }
        return true;
    }

    /// <summary>为一个元素建作用域。返回 null = 该元素无法按展平形状解析，调用方对它回退 AngleSharp。</summary>
    public Scope? For(IElement el, Scope? parent)
    {
        Dictionary<string, string>? decl = null;
        var cls = el.GetAttribute("class");
        if (!string.IsNullOrWhiteSpace(cls))
        {
            Dictionary<string, string>? hit = null;
            foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (c.Length == 0) continue;
                if (_class.TryGetValue(c, out var d))
                {
                    if (hit != null && !ReferenceEquals(hit, d)) return null;   // 两条类规则竞争 → 交给 AngleSharp 定序
                    hit = d;
                }
            }
            decl = hit;
        }
        Dictionary<string, string>? tag = null;
        if (el.TagName.Equals("BODY", StringComparison.OrdinalIgnoreCase)) tag = _body;
        Dictionary<string, string>? inlineD = ParseInline(el.GetAttribute("style"));
        return new Scope(this, decl, tag, inlineD, parent);
    }

    /// <summary>伪元素作用域：声明来自 .ps-N::before|after，继承走宿主元素。没有对应规则时返回 null。</summary>
    public Scope? ForPseudo(IElement el, string pseudo, Scope? owner)
    {
        var cls = el.GetAttribute("class");
        if (string.IsNullOrWhiteSpace(cls)) return null;
        foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (_pseudo.TryGetValue(c + pseudo, out var d))
                return new Scope(this, d, null, null, owner);
        return null;
    }

    static Dictionary<string, string>? ParseInline(string? style)
    {
        if (string.IsNullOrWhiteSpace(style)) return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!ParseDecls(style, d) || d.Count == 0) return null;
        return d;
    }

    /// <summary>一个元素（或伪元素）的取值视图：内联 &gt; 类规则 &gt; tag 规则 &gt;（可继承属性）父链。</summary>
    public sealed class Scope
    {
        readonly Dictionary<string, string>? _decl;
        readonly Dictionary<string, string>? _tag;
        readonly Dictionary<string, string>? _inline;
        readonly Scope? _parent;

        internal Scope(FlatCascade owner, Dictionary<string, string>? decl, Dictionary<string, string>? tag,
                       Dictionary<string, string>? inlineStyle, Scope? parent)
        {
            _decl = decl; _tag = tag; _inline = inlineStyle; _parent = parent;
        }

        /// <summary>取属性值；无值返回空串（交回 ReadStyle 各属性自带的初值兜底）。</summary>
        public string Get(string prop)
        {
            if (_inline != null && _inline.TryGetValue(prop, out var v)) return v;
            if (_decl != null && _decl.TryGetValue(prop, out v)) return v;
            if (_tag != null && _tag.TryGetValue(prop, out v)) return v;
            if (!Inherited.Contains(prop)) return "";
            for (var p = _parent; p != null; p = p._parent)
            {
                if (p._inline != null && p._inline.TryGetValue(prop, out v)) return v;
                if (p._decl != null && p._decl.TryGetValue(prop, out v)) return v;
                if (p._tag != null && p._tag.TryGetValue(prop, out v)) return v;
            }
            return "";
        }
    }
}
