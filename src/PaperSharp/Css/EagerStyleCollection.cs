using System.Collections;
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
/// 的缓存，双轮并一轮。伪元素与 AngleSharp 同构：声明 = 伪元素自身层叠 ∪ 宿主的**祖先**链（不含宿主），
/// 注意其 <c>GetDeclarations</c> 是在替换伪元素**之前**取宿主祖先链的。
///
/// 前提是渲染期间文档与样式表静态（层叠阶段只读取值），故枚举一次 + 结果缓存安全。
/// </summary>
public sealed class EagerStyleCollection : IStyleCollection
{
    readonly List<ICssStyleRule> _rules;

    public IRenderDevice Device { get; }

    // 键 = 元素引用 / (宿主, 伪元素名)。文档静态 ⇒ 一次层叠终身有效（见类注释②）。
    readonly Dictionary<IElement, ICssStyleDeclaration> _merged = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<(IElement, string), ICssStyleDeclaration> _mergedPseudo = new();

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
                if (!_mergedPseudo.TryGetValue(key, out var ps))
                {
                    ps = this.ComputeCascadedStyle(pseudoEl,
                                                   parent: MergedOrNone(NearestAncestorElement(element)));
                    _mergedPseudo[key] = ps;
                }
                return ps;
            }
        }
        return Merged(element);
    }

    /// <summary>与 AngleSharp 的 ComputeDeclarations 扩展方法同名同签名以遮蔽之：
    /// 在缓存的原始声明上跑同一套值折算（GetPropertyValue 得到的就是 computed 串）。</summary>
    public ICssStyleDeclaration ComputeDeclarations(IElement element, string? pseudoSelector = null)
    {
        var raw = GetDeclarations(element, pseudoSelector);
        var context = ((INode)element).Owner?.Context;
        return raw.Compute(new ComputeContext(Device, context, raw));
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

    /// <summary>复刻 AngleSharp 的私有 CssComputeContext：Device/Context 透传、Converter 恒 null、
    /// Resolve 只认 --自定义属性（在**原始声明**上按名取首个 RawValue）。</summary>
    sealed class ComputeContext : ICssComputeContext
    {
        readonly IRenderDevice _device;
        readonly IBrowsingContext? _context;
        readonly ICssProperties _properties;

        public ComputeContext(IRenderDevice device, IBrowsingContext? context, ICssProperties properties)
        {
            _device = device;
            _context = context;
            _properties = properties;
        }

        public IRenderDevice Device => _device;
        public IBrowsingContext? Context => _context;
        public IValueConverter? Converter => null;

        public ICssValue? Resolve(string name)
            => name.StartsWith("--", StringComparison.Ordinal)
                ? _properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal))?.RawValue
                : null;
    }
}
