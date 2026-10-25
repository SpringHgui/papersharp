using System.Collections;
using System.Text;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using AngleSharp.Dom;

namespace PaperSharp.Css;

/// <summary>
/// AngleSharp 层叠入口的两级加速（都不换层叠语义，仍走 AngleSharp 自己的公开方法）：
///
/// ① **物化样式表集合**。AngleSharp 的 <c>StyleCollection._sheets</c> 是未物化的 LINQ，每枚举一次就
/// 重走一遍全文档收集 &lt;style&gt;/&lt;link&gt;；而公开的 <c>GetComputedStyle</c> 展开后是
/// <c>GetStyleCollection().ComputeDeclarations(el)</c>，其 <c>GetDeclarations</c> 又对元素的**每个祖先**
/// 各枚举一次集合 → 每元素 O(深度 × 文档大小)、整篇 O(n²)。物化后 table 2000 行 25.0×、
/// flexlist 2000 条 19.2×（PERF_REPORT §5.5）。
///
/// ② **按元素缓存层叠结果**（本类的 <see cref="GetDeclarations"/>/<see cref="ComputeDeclarations"/>
/// 实例方法，刻意与 AngleSharp 的**同名扩展方法**同签名——用本类的具体类型调用即遮蔽扩展方法）。
/// 物化后 AngleSharp 仍为每个祖先各跑一次全表匹配，而同一篇文档里祖先的层叠结果被反复重算
/// （pgstyle 探针 G 行显示只算宿主元素可再省 5–8×）。缓存用递推式
/// <c>R(e) = ComputeCascadedStyle(e, parent: R(a1))</c>（a1 = 最近祖先元素），它折叠等价于 AngleSharp 的
/// 「<c>Set(cascade(e))</c> 后逐个 <c>Update(cascade(ai))</c>」：UpdateDeclarations 的三条规则
/// （就近优先、只覆写 <c>IsInherited</c> 占位、过滤不可继承属性）都对逐次折叠可结合——逐属性对账见探针。
/// 于是每元素全表匹配恰一次，且 <c>ReadStyle</c> 的第二轮（<c>Spec()</c> 要的原始声明串）直接吃第一轮
/// 的缓存，双轮并一轮。伪元素两轮**不同链**（AngleSharp 1.1.x 重写后）：raw = 伪元素自身层叠 ∪ 宿主的
/// **祖先**链（<c>GetDeclarations</c> 在替换伪元素**之前**取祖先链，宿主自身不进）；computed = 伪元素层叠
/// ∘ 宿主**自己**的那条链（宿主自身的继承值会进伪元素）。
/// 折算是**叶子一次**而非逐节点（逐节点每条链都要重跑全表匹配，2× 且不换来任何保真度）：唯一的例外是
/// 自定义属性 <c>var()</c> 的嵌套引用，规范要求它在**声明它的那一层**求值，故 <see cref="ComputeContext"/>
/// 的 Resolve 遇到值串里还有 var() 时顺祖先链定位声明层再折（见 <c>ResolveCustom</c>）。
/// raw 轮（<see cref="GetDeclarations"/>）始终不折算，% 与 calc() 由消费方按包含块自己解析。
///
/// 前提是渲染期间文档与样式表静态（层叠阶段只读取值），故枚举一次 + 结果缓存安全。
/// </summary>
public sealed class EagerStyleCollection : IStyleCollection
{
    readonly List<ICssStyleRule> _rules;

    public IRenderDevice Device { get; }

    // 键 = 元素引用 / (宿主, 伪元素名)。文档静态 ⇒ 一次层叠终身有效（见类注释②）。
    readonly Dictionary<IElement, ICssStyleDeclaration> _merged = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<(IElement, string), ICssStyleDeclaration> _mergedPseudoRaw = new();
    readonly Dictionary<(IElement, string), ICssStyleDeclaration> _cascadePseudo = new();
    // 自定义属性 var() 嵌套折算的结果（键 = 求值起点, 属性名），见 ResolveCustom。
    readonly Dictionary<(IElement, string), ICssValue?> _customFolded = new();

    public EagerStyleCollection(IStyleCollection lazy)
    {
        Device = lazy.Device;
        _rules = lazy.ToList();
    }

    public IEnumerator<ICssStyleRule> GetEnumerator() => _rules.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _rules.GetEnumerator();

    /// <summary>与 AngleSharp 的 GetDeclarations 扩展方法同名同签名以遮蔽之：原始声明串（% / calc 未折算），
    /// 含祖先链的继承式合并；每个元素的全表匹配全文档只做一次。</summary>
    public ICssStyleDeclaration GetDeclarations(IElement element, string? pseudoSelector = null)
    {
        if (!string.IsNullOrEmpty(pseudoSelector))
        {
            // AngleSharp：取不到伪元素实例时按宿主自身继续（伪声明 = 宿主声明 ∪ 祖先链）
            var name = pseudoSelector.TrimStart(':');
            var pseudoEl = element.Pseudo(name);
            if (pseudoEl != null)
            {
                var key = (element, name);
                if (!_mergedPseudoRaw.TryGetValue(key, out var ps))
                {
                    ps = this.ComputeCascadedStyle(pseudoEl,
                                                   parent: MergedOrNone(NearestAncestorElement(element)));
                    _mergedPseudoRaw[key] = ps;
                }
                return ps;
            }
        }
        return Merged(element);
    }

    /// <summary>与 AngleSharp 的 ComputeDeclarations 扩展方法同名同签名以遮蔽之：
    /// 在缓存的层叠结果上跑同一套值折算（GetPropertyValue 得到的就是 computed 串）。</summary>
    public ICssStyleDeclaration ComputeDeclarations(IElement element, string? pseudoSelector = null)
    {
        var raw = CascadeForCompute(element, pseudoSelector);
        var context = ((INode)element).Owner?.Context;
        return raw.Compute(new ComputeContext(Device, context, raw, this, element));
    }

    /// <summary>computed 那轮吃哪条折叠链：普通元素 = 宿主的 _merged；伪元素 = 伪元素层叠 ∘ 宿主自身层叠
    /// （AngleSharp 1.1.x 起 computed 逐节点下传 root → … → 宿主 → 伪元素，宿主的继承值会进伪元素）。
    /// raw 轮（<see cref="GetDeclarations"/>）不带宿主自身，两轮因此分叉。</summary>
    ICssStyleDeclaration CascadeForCompute(IElement host, string? pseudoSelector)
    {
        if (string.IsNullOrEmpty(pseudoSelector)) return Merged(host);
        var name = pseudoSelector!.TrimStart(':');
        var key = (host, name);
        if (_cascadePseudo.TryGetValue(key, out var hit)) return hit;
        // 取不到伪元素实例时与 AngleSharp 一致：按宿主自身继续
        var pseudoEl = host.Pseudo(name);
        var cascaded = pseudoEl != null ? this.ComputeCascadedStyle(pseudoEl, Merged(host)) : Merged(host);
        _cascadePseudo[key] = cascaded;
        return cascaded;
    }

    ICssStyleDeclaration Merged(IElement element)
    {
        if (_merged.TryGetValue(element, out var hit)) return hit;

        // 自最近祖先向根走，撞到第一个已缓存节点就停；再从该节点向下逐层嵌套。
        List<IElement> chain = new();
        var cur = element;
        ICssStyleDeclaration? acc = null;
        while (cur != null)
        {
            if (_merged.TryGetValue(cur, out acc)) break;
            chain.Add(cur);
            cur = NearestAncestorElement(cur);
        }
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            acc = this.ComputeCascadedStyle(chain[i], acc);
            _merged[chain[i]] = acc;
        }
        return acc!;
    }

    ICssStyleDeclaration? MergedOrNone(IElement? e) => e == null ? null : Merged(e);

    // NearestAncestorElement 与 AngleSharp 的 GetAncestors(element).OfType<IElement>().FirstOrDefault() 同源：
    // 顺 INode.Parent 上溯取第一个 IElement（IDocument 不是 IElement ⇒ 链自然止于 html）。
    static IElement? NearestAncestorElement(IElement e)
    {
        var node = ((INode)e).Parent;
        while (node != null && node is not IElement) node = node.Parent;
        return node as IElement;
    }

    /// <summary>自定义属性值串里的 <c>var()</c> 嵌套引用按**声明作用域**折算（Chrome 与 AngleSharp 1.1.4
    /// 的 GetComputedDeclarations→PrepareComputedDeclarations→CssCustomPropertyResolver 同构）：
    /// <c>--tip-color: var(--accent)</c> 写在 :root 时，折出的是 :root 的 --accent，而不是使用点作用域里
    /// 更近的那一份（m4 的 .nested-text：#77b3d4，按使用点折会拿 .scoped 的 #8a2be2）。
    /// 值串不含 var() 时两种口径等价（就近优先），直接返回原值对象 ⇒ 调用方只在真嵌套时走这条路。</summary>
    ICssValue? ResolveCustom(IElement scope, string name, ICssValue? rawAtUsePoint)
        => FoldCustom(scope, name, rawAtUsePoint, new HashSet<(IElement, string)>());

    ICssValue? FoldCustom(IElement scope, string name, ICssValue? rawUse, HashSet<(IElement, string)> active)
    {
        var key = (scope, name);
        if (_customFolded.TryGetValue(key, out var hit)) return hit;
        if (!active.Add(key)) return null;   // var(--a)→var(--b)→var(--a)：判环，按「无效」处理（同 Chrome 的环检测）
        ICssValue? result;
        try
        {
            // 声明层 = 沿祖先链第一个「这里的属性对象与父链那份不是同一个引用」的节点。
            // 层叠结果里继承下来的条目与祖先那份是**同一个对象**（探针实测），故只能这么定位，不能按下标。
            IElement declaring = scope;
            ICssValue? raw = rawUse;
            for (var a = scope; a != null; a = NearestAncestorElement(a))
            {
                var here = First(Merged(a), name);
                if (here == null) break;
                var up = First(MergedOrNone(NearestAncestorElement(a)), name);
                if (ReferenceEquals(here, up)) continue;
                declaring = a;
                raw = here.RawValue;
                break;
            }
            var text = raw?.CssText;
            if (text == null) result = null;
            else if (!HasNestedVar(text)) result = raw;   // 声明层不含 var ⇒ 原值即答案
            else result = new ResolvedLiteral(ExpandVars(text, m => FoldCustom(declaring, m, null, active)?.CssText));
        }
        finally
        {
            active.Remove(key);
        }
        _customFolded[key] = result;
        return result;
    }

    static ICssProperty? First(ICssProperties? styles, string name)
        => styles?.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));

    static bool HasNestedVar(string text)
    {
        for (int i = text.IndexOf("var", StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf("var", i + 1, StringComparison.OrdinalIgnoreCase))
        {
            int j = i + 3;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (j < text.Length && text[j] == '(')
            {
                j++;
                while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                if (j + 1 < text.Length && text[j] == '-' && text[j + 1] == '-') return true;
            }
        }
        return false;
    }

    /// <summary>把 <c>var(--x, fallback)</c>（可嵌套、可多个）换成 <c>lookup("--x")</c> 的返回串；
    /// 取不到就用 fallback（fallback 自身也再展开），都没有则留空。括号不闭合时原样收尾，避免烂尾。</summary>
    static string ExpandVars(string text, Func<string, string?> lookup)
    {
        StringBuilder sb = new();
        int i = 0;
        while (true)
        {
            int at = text.IndexOf("var(", i, StringComparison.OrdinalIgnoreCase);
            if (at < 0) { sb.Append(text, i, text.Length - i); break; }
            int close = at + 3, depth = 0;
            while (close < text.Length)
            {
                if (text[close] == '(') depth++;
                else if (text[close] == ')') depth--;
                close++;
                if (depth == 0) break;
            }
            if (depth > 0) { sb.Append(text, at, text.Length - at); break; }
            sb.Append(text, i, at - i);
            var inner = text.Substring(at + 4, close - at - 5);
            int comma = -1, d2 = 0;
            for (int k = 0; k < inner.Length; k++)
            {
                if (inner[k] == '(') d2++;
                else if (inner[k] == ')') d2--;
                else if (inner[k] == ',' && d2 == 0) { comma = k; break; }
            }
            var name = (comma < 0 ? inner : inner.Substring(0, comma)).Trim();
            var fallback = comma < 0 ? null : inner.Substring(comma + 1).Trim();
            var val = name.StartsWith("--", StringComparison.Ordinal) ? lookup(name) : null;
            sb.Append(val ?? (fallback == null ? "" : ExpandVars(fallback, lookup)));
            i = close;
        }
        return sb.ToString();
    }

    /// <summary>折算完的自定义属性字面量：等价 AngleSharp 内部的 <c>CssAnyValue(text, isResolved:true)</c>
    /// ——再 Compute 只回自身，交在使用点上时由该属性自己的转换器解析（十六进制→rgba 等）。</summary>
    sealed class ResolvedLiteral : ICssValue
    {
        readonly string _text;
        public ResolvedLiteral(string text) => _text = text;
        public string CssText => _text;
        public ICssValue Compute(ICssComputeContext context) => this;
        public bool Equals(ICssValue? other) => other != null && other.CssText == _text;
        public override int GetHashCode() => _text.GetHashCode();
    }

    /// <summary>复刻 AngleSharp 的私有 CssComputeContext：Device/Context 透传、Converter 恒 null、
    /// Resolve 只认 --自定义属性（原始声明值；值里还有嵌套 var() 时按声明作用域折算，见 <see cref="ResolveCustom"/>）。</summary>
    sealed class ComputeContext : ICssComputeContext
    {
        readonly IRenderDevice _device;
        readonly IBrowsingContext? _context;
        readonly ICssProperties _properties;
        readonly EagerStyleCollection _owner;
        readonly IElement _scope;

        public ComputeContext(IRenderDevice device, IBrowsingContext? context, ICssProperties properties,
            EagerStyleCollection owner, IElement scope)
        {
            _device = device;
            _context = context;
            _properties = properties;
            _owner = owner;
            _scope = scope;
        }

        public IRenderDevice Device => _device;
        public IBrowsingContext? Context => _context;
        public IValueConverter? Converter => null;

        public ICssValue? Resolve(string name)
        {
            if (!name.StartsWith("--", StringComparison.Ordinal)) return null;
            var p = First(_properties, name);
            if (p == null) return null;
            var raw = p.RawValue;
            var text = raw?.CssText;
            if (text == null) return null;
            if (!HasNestedVar(text)) return raw;
            return _owner.ResolveCustom(_scope, name, raw) ?? raw;
        }
    }
}
