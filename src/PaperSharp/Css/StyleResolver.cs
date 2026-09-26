using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using SkiaSharp;

namespace PaperSharp.Css;

/// <summary>
/// 用 AngleSharp + AngleSharp.Css 把 HTML 字符串解析成 DOM，
/// 应用 CSS 层叠，然后遍历每个元素把 GetComputedStyle 的结果
/// 转成我们的 ComputedStyle 树（保留父子关系）。
/// </summary>
public static class StyleResolver
{
    /// <summary>&lt;input&gt; 占位符的默认色：Chrome 无作者 ::placeholder 声明时实测 placeholderText = #757575。</summary>
    static readonly SKColor PlaceholderColor = new(0x75, 0x75, 0x75);
    // 一份最小 UA 样式表，覆盖 demo 用到的元素默认值。
    // 不加这个的话 <p>、<h1> 全部 margin=0 会糊在一起。
    // 公开：Playground 这类「引擎 vs 浏览器」比对工具要把同一份表注进预览 iframe，
    // 否则两边 UA 默认值（字体/行高/标题字号）就不是一套，每个文本元素都白报一条高度差。
    public const string UaStylesheet = """
        html, body { margin: 0; padding: 0; }
        /* 不写 line-height：Chrome 的 UA 默认是 normal（= 字体 ascent+descent 逐度量取整），
           这里补一条 1.4 会让所有未显式设行高的元素按 1.4 走行盒（雅黑 1.4 > normal 1.31），
           每行 +0.2~1.2px，整页累积成 10px+ 的纵向漂移。缺省值交给 ComputedStyle.LineHeightPx 的 normal 分支。 */
        body { font-family: sans-serif; font-size: 16px; color: #000; }
        h1 { font-size: 32px; font-weight: bold; margin: 21px 0; }
        h2 { font-size: 24px; font-weight: bold; margin: 20px 0; }
        h3 { font-size: 19px; font-weight: bold; margin: 19px 0; }
        p { margin: 16px 0; }
        /* 列表 UA 默认（实测 headless Edge：li 在 x=40、宽比 ul 少 40；本表缺这两行时引擎把 li 摆到 x=0 满宽，
           是肉眼可见的错位而非比对口径问题）。嵌套列表的块边距归零也是浏览器行为。 */
        ul, ol { margin: 1em 0; padding-left: 40px; }
        ul ul, ul ol, ol ul, ol ol { margin-top: 0; margin-bottom: 0; }
        /* Chrome UA 的嵌套记号（2× 探针实测）：ul 二层 circle、三层 square；ol 二层 lower-alpha、三层 lower-roman。
           ol 里套 ul 仍换 circle，ul 里套 ol 仍是 decimal——只有同类连续嵌套才升级记号。 */
        ul ul, ol ul { list-style-type: circle; }
        ul ul ul, ol ul ul { list-style-type: square; }
        ol ol { list-style-type: lower-alpha; }
        ol ol ol { list-style-type: lower-roman; }
        dd { margin-left: 40px; }
        blockquote { margin: 1em 40px; }
        figure { margin: 1em 40px; }
        /* 本表按 author 源注入：作者写了 margin（含 `* { margin:0 }` 重置）时由 AuthorBoxSpacing 微级联裁决，
           不会像现在这样被这里的元素选择器反向压过重置；无作者声明才落到本表/内置 UA 默认值。 */
        div, section, article, header, footer, main { display: block; }
        /* 行内级元素默认 display:inline（AngleSharp 未必内置 UA 表，显式声明以驱动 inline 格式化上下文检测） */
        a, span, em, i, strong, b, u, s, small, code, label, cite, abbr, mark, sub, sup, tt, kbd, var, q, ins, del, time { display: inline; }
        /* 表格家族 UA 默认 */
        table { display: table; border-collapse: separate; border-spacing: 2px; }
        thead { display: table-header-group; }
        tbody { display: table-row-group; }
        tfoot { display: table-footer-group; }
        tr { display: table-row; }
        td, th { display: table-cell; padding: 1px; vertical-align: middle; }
        th { font-weight: bold; }
        caption { display: table-caption; }
        /* 表单控件 UA 默认：按钮文字水平+垂直居中（浏览器把 button 当弹性盒居中），输入/按钮继承字体 */
        button { display: inline-flex; align-items: center; justify-content: center; text-align: center; }
        """;

    /// <summary>
    /// 注进「浏览器预览文档」的那一份：裹进 <c>@layer</c>，让作者样式（未分层）恒压过它。
    /// 引擎侧本表是真 UA 源——作者写 <c>* { margin:0 }</c> 就把它压掉（AuthorBoxSpacing 裁决）；
    /// 浏览器侧只能当 author 注，若不分层，<c>h2{margin:20px 0}</c>（0,0,1）会反胜 <c>*</c>（0,0,0），
    /// 于是每个标题凭空差出 20px（pricing-mid 实测整棵子树下坠）。分层后两侧优先级才是一套。
    /// </summary>
    public static string UaStylesheetForPreview => "@layer paper-ua {\n" + UaStylesheet + "\n}";

    // AngleSharp.Css 不实现的属性（computed/declared 都拿不到），从原始 CSS 里按选择器兜底扫出来。
    // overflow 三兄弟实测：hidden/auto/scroll/visible 正常，但 clip、overlay、以及两值简写
    // "visible hidden" 在 computed 与 GetDeclarations 里都是空串（解析阶段就被判非法丢弃），
    // 只能靠这张原始文本表捞回来（探针记录见 OverflowClipTests）。
    static readonly string[] UnsupportedProps = { "flex", "text-overflow", "text-wrap", "vertical-align", "text-fit", "background-image", "background", "mix-blend-mode", "clip-path", "corner-shape", "filter", "mask-image", "background-clip", "backdrop-filter", "box-shadow", "border-radius", "counter-reset", "counter-increment", "animation", "overflow", "overflow-x", "overflow-y",
        // 盒对齐（Box Alignment）新写法的属性：AngleSharp 只认 flexbox 时代的关键字集，
        // align-self:start/end、justify-self、place-self/place-items 整条声明在解析期就被丢掉
        // （computed 与 GetDeclarations 都取不到空），grid 子项因此恒被当成 stretch 拉满轨道
        // （_gsel 实测：align-self:start 高度 120 而非 21.6，justify-self:start 宽 200 而非 12.1）。
        "align-self", "justify-self", "align-items", "justify-items", "place-self", "place-items", "align-content", "justify-content" };

    // 不参与渲染的标签：不成盒、其文本不得混入正文（style 已在 BuildDocument 源头剥除，此处兜底）。
    static readonly HashSet<string> NonRenderingTags = new(StringComparer.OrdinalIgnoreCase)
        { "style", "script", "template", "meta", "link", "title", "base", "noscript", "head" };

    /// <summary>取 &lt;meta name="X" content="..."&gt;（属性顺序不敏感）。</summary>
    static string? MetaContent(string html, string name)
    {
        var m = Regex.Match(html, @"<meta\s+[^>]*name=[""']" + name + @"[""'][^>]*>", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var c = Regex.Match(m.Value, @"content=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        return c.Success ? System.Net.WebUtility.HtmlDecode(c.Groups[1].Value).Trim() : null;
    }

    // 一次 BuildDocument 的私有状态：不支持属性兜底索引、CSS 变量索引、文档元数据。
    // 刻意不做成静态——并发渲染时跨请求互相污染（曾是 ROADMAP「并发缺口」的主源头之一）。
    sealed class DocContext
    {
        public Dictionary<string, Dictionary<string, string>> UnsupportedMap = new();
        public Dictionary<string, Dictionary<string, string>> VarMap = new(StringComparer.OrdinalIgnoreCase);
        public string? Title, Author, Description, Keywords;
        public bool HasPseudoRules;   // CSS 里出现 ::before/::after 才做伪元素（否则零开销，既有样例不回归）
        // 伪元素显式声明的 display（"末token::before/after" → 值）。AngleSharp 对伪元素 UA 默认注入
        // display:block（computed/declared 都是），无法据其判断作者意图，故从原始 CSS 扫显式声明。
        // 同表还捎带 "末token::placeholder" → color 声明：::placeholder 在 AngleSharp 里根本不是可解析的
        // 伪元素（实测 ComputeDeclarations(el,"::placeholder") 空），作者的占位符颜色只能这样兜回来。
        public Dictionary<string, string> PseudoDisplay = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<Keyframe>> Keyframes = new();   // @keyframes 表（渲染传 animationTime 时按时刻取帧）
        public AuthorBoxSpacing? Spacing;       // margin/padding 作者源微级联索引（见 AuthorBoxSpacing）
        public float ViewportWidth = 1024;  // 供 Spacing 的 @media 条件判定
        // 伪元素作用域 token 集（.cls / #id / tag）：只有 CSS 里所有伪元素选择器都能约成这种 token 时才非 null，
        // 用来跳过「明显不可能有 ::before/::after 的元素」——每次 GetComputedStyle(el, pseudo) 是一整轮层叠
        // （实测 1.7ms/元素/次，伪元素前后各一次 + 各自样式再一次 → 每元素最多多花 ~5ms），是快照类输入最大的开销。
        public HashSet<string>? PseudoScope;
        // 输入已是浏览器算好的 used value（展平快照）：跳过 GetDeclarations 这第二轮层叠（实测再省 1.4ms/元素）。
        public bool Precomputed;
        // 是否为每个元素生成 DomPath（见 ComputedStyle.DomPath）。生产渲染恒 false → 零开销；
        // 只有比对类工具（Playground / 未来的盒级 diff）需要把引擎盒与浏览器矩形对位时才开。
        public bool DomPath;
        // 展平快照的类名级联索引（见 FlatCascade）。非 null 时 ReadStyle 完全不碰 AngleSharp——
        // 实测 GetComputedStyle 在这类输入上每元素 1.82ms，是快照导出的头号开销（904 元素 WASM 侧 27s）。
        public FlatCascade? Flat;
        // 作者 CSS 那条路的层叠入口（见 EagerStyleCollection）：整篇文档枚举一次样式表 + 每元素层叠结果终身缓存。
        // 用具体类型 = 以本类的同名方法遮蔽 AngleSharp 的扩展方法，ReadStyle 的双轮层叠并成一条缓存路径。
        public EagerStyleCollection Styles = null!;
    }

    public static ComputedStyle BuildDocument(string htmlBody, string userCss, double? animationTime = null, bool precomputed = false, float viewportWidth = 1024, bool domPath = false)
    {
        var ctx = new DocContext { Precomputed = precomputed, DomPath = domPath };
        // 支持整份 HTML 内嵌 <style>：内容并入 userCss（参数在前、内嵌在后，符合后定义覆盖的层叠顺序），
        // 标签从原文剥除，避免 CSS 源码被当成可见文本渲染；head 里的也会在此被捞回。
        var embedded = Regex.Matches(htmlBody, @"<style[^>]*>(.*?)</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (embedded.Count > 0)
        {
            var sb = new StringBuilder().Append(userCss);
            foreach (Match s in embedded) sb.Append('\n').Append(s.Groups[1].Value);
            userCss = sb.ToString();
            htmlBody = Regex.Replace(htmlBody, @"<style[^>]*>.*?</style>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }
        userCss = InlineUniqueVars(userCss);   // 必须先于下面所有索引与 AngleSharp：见方法注释
        userCss = ExpandVarBorderShorthands(userCss);   // 同上：AngleSharp 丢带 var() 的 border 简写
        ScanUnsupported(userCss, ctx);
        ScanVars(userCss, ctx);
        // margin 作者源微级联（见 AuthorBoxSpacing）：AngleSharp 的 UA 源比较按特异度而非源，
        // `* { margin:0 }` 压不住 h4 等内置默认；作者声明了 margin 时由本索引裁决。
        ctx.Spacing = AuthorBoxSpacing.Parse(userCss ?? "");
        ctx.ViewportWidth = viewportWidth;
        // 展平快照（precomputed）才试建类名索引；CSS 里只要还有 @rule/组合器/特异度竞争就返回 null，整份走老路。
        if (precomputed) ctx.Flat = FlatCascade.TryBuild(userCss);
        // 伪元素开销较大（每元素多两次 computed 层叠），仅当 CSS 里确有 ::before/::after 规则时才启用
        ctx.HasPseudoRules = Regex.IsMatch(userCss ?? "", @"::?(before|after)\b", RegexOptions.IgnoreCase);
        if (ctx.HasPseudoRules) ctx.PseudoScope = new(StringComparer.OrdinalIgnoreCase);
        // 只有 ::placeholder 规则时也值得扫一遍（它只往 PseudoDisplay 里塞颜色，不开伪元素那套开销）
        if (ctx.HasPseudoRules || Regex.IsMatch(userCss ?? "", @"::placeholder\b", RegexOptions.IgnoreCase))
            ScanPseudoDisplay(userCss ?? "", ctx);
        // @keyframes 表（仅当 CSS 里确有 @keyframes 时解析；渲染传 animationTime 才按时刻取帧覆盖样式）
        if (Regex.IsMatch(userCss ?? "", @"@keyframes\b", RegexOptions.IgnoreCase))
            ctx.Keyframes = Animation.ParseKeyframes(userCss ?? "");
        // 调用方常把整份 HTML 文档传进来：抽 <title> 供元数据用，只把 <body> 内容当正文，
        // 否则 head 里的 meta/title 会被当成可见块渲染。
        var mt = Regex.Match(htmlBody, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        ctx.Title = mt.Success ? System.Net.WebUtility.HtmlDecode(mt.Groups[1].Value).Trim() : null;
        ctx.Author = MetaContent(htmlBody, "author");
        ctx.Description = MetaContent(htmlBody, "description");
        ctx.Keywords = MetaContent(htmlBody, "keywords");
        var mb = Regex.Match(htmlBody, @"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (mb.Success) htmlBody = mb.Groups[1].Value;
        var fullHtml =
            "<!doctype html><html><head><meta charset='utf-8'>" +
            $"<style>{UaStylesheet}</style>" +
            $"<style>{userCss}</style>" +
            $"</head><body>{htmlBody}</body></html>";

        // 注册 render device：AngleSharp.Css 计算 em/rem/% 等相对单位时需要非空渲染尺寸，
        // 否则 GetComputedStyle 会对这些值抛 ArgumentException。
        // ViewPortWidth 用真实排版宽度：@media (max-width/min-width) 据此判定，否则恒按 1024 命中/落空。
        var renderDevice = new DefaultRenderDevice { FontSize = 16, ViewPortWidth = (int)Math.Max(1, viewportWidth), ViewPortHeight = 1024 };
        var config = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var context = BrowsingContext.New(config);
        using var doc = context.OpenAsync(req => req.Content(fullHtml)).GetAwaiter().GetResult();
        var body = doc.Body ?? throw new InvalidOperationException("no <body>");
        var window = doc.DefaultView ?? throw new InvalidOperationException("no window (Did WithCss() get applied?)");
        // 层叠用的样式表在此物化一次（此后文档与样式表都只读）
        ctx.Styles = new EagerStyleCollection(window.GetStyleCollection());

        var root = Walk(body, null, ctx);
        // 列表 marker 先做成 ::before 片段（复用伪元素的注入与匿名盒机制，两条排版通路一次到位），
        // 再和作者的伪元素一起由 PseudoContent 落进树里。
        bool anyMarker = ListMarkers.Apply(root);
        if (ctx.HasPseudoRules || anyMarker) PseudoContent.Apply(root);   // 文档序求值 counter + 注入伪元素匿名盒
        if (animationTime.HasValue && ctx.Keyframes.Count > 0)
            Animation.Apply(root, ctx.Keyframes, animationTime.Value);   // 确定性取帧：按时刻插值覆盖样式
        root.Document = new DocumentInfo(ctx.Title, ctx.Author, ctx.Description, ctx.Keywords);
        return root;
    }

    /// <summary>扫原始 CSS：把不支持属性按「最后一段简单选择器 token → 值」建索引。</summary>
    static void ScanUnsupported(string css, DocContext ctx)
    {
        var map = ctx.UnsupportedMap = new();
        foreach (var p in UnsupportedProps) map[p] = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(css)) return;
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
        {
            var sel = m.Groups[1].Value.Trim();
            if (sel.StartsWith("@")) continue; // 跳过 @page/@font-face 等 at-rule
            var body = m.Groups[2].Value;
            foreach (var prop in UnsupportedProps)
            {
                // 左边界 (?<![\w-])：否则 "overflow" 会命中同一条规则里的 "text-overflow:clip"，
                // 把文本截断值当成盒级裁剪值——同名前缀的属性全靠这条兜底表，必须按整词匹配。
                var mm = Regex.Match(body, @"(?<![\w-])" + prop + @"\s*:\s*([^;}]+)", RegexOptions.IgnoreCase);
                if (!mm.Success) continue;
                var val = mm.Groups[1].Value.Trim().ToLowerInvariant();
                var parts = Regex.Split(sel, @"[\s>+~]+").Where(x => x.Length > 0).ToArray();
                var last = parts.Length > 0 ? parts[^1] : sel;
                last = Regex.Replace(last, @"[:\[].*$", ""); // 去掉伪类/属性选择器
                if (last.Length > 0) map[prop][last] = val;
            }
        }
    }

    /// <summary>取属性值：有 AngleSharp 结果就用；否则按元素 class/id/tag 命中兜底索引。</summary>
    /// <summary>少数「UA 默认值由标签决定、但 AngleSharp 与作者 CSS 都读不到」的属性兜底。</summary>
    static string? TagDefaultVerticalAlign(IElement el) => el.TagName.ToUpperInvariant() switch
    {
        "SUP" => "super",
        "SUB" => "sub",
        _ => null,
    };

    static string Lookup(string prop, IElement element, string current, DocContext ctx)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            if (ctx.UnsupportedMap.TryGetValue(prop, out var map) && map.Count > 0)
            {
                var cls = element.GetAttribute("class");
                if (!string.IsNullOrWhiteSpace(cls))
                    foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (map.TryGetValue("." + c, out var v)) return ExpandVars(v, element, ctx);
                var id = element.GetAttribute("id");
                if (!string.IsNullOrWhiteSpace(id) && map.TryGetValue("#" + id, out var iv)) return ExpandVars(iv, element, ctx);
                if (map.TryGetValue(element.TagName.ToLowerInvariant(), out var tv)) return ExpandVars(tv, element, ctx);
            }
            return current;
        }
        return ExpandVars(current, element, ctx);
    }

    /// <summary>
    /// overflow 值串里是否含裁剪关键字。auto/scroll/overlay 一律视同 hidden（定格输出不画滚动条）；
    /// 简写可能带两值（如 "visible hidden"），任一轴裁剪即整盒裁剪，故逐 token 判。
    /// </summary>
    static bool AnyClips(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        foreach (var t in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (t is "hidden" or "clip" or "auto" or "scroll" or "overlay") return true;
        return false;
    }

    // ── var() / 自定义属性 ─────────────────────────────────────────────

    /// <summary>扫原始 CSS 里的 --name 声明：按「选择器（逗号拆开后每段）末 token → name → 值」建索引，后定义覆盖先定义（层叠顺序）。</summary>
    static void ScanVars(string css, DocContext ctx)
    {
        var varMap = ctx.VarMap = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(css)) return;
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
        {
            var body = m.Groups[2].Value;
            if (!body.Contains("--")) continue;
            Dictionary<string, string>? defs = null;
            foreach (Match d in Regex.Matches(body, @"(--[A-Za-z][\w-]*)\s*:\s*([^;}]+)"))
                (defs ??= new(StringComparer.OrdinalIgnoreCase))[d.Groups[1].Value] = d.Groups[2].Value.Trim();
            if (defs == null) continue;
            foreach (var selPart in m.Groups[1].Value.Split(','))
            {
                var key = SelectorScopeKey(selPart.Trim());
                if (key == null) continue;
                if (!varMap.TryGetValue(key, out var map)) varMap[key] = map = new(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in defs) map[kv.Key] = kv.Value;
            }
        }
    }

    /// <summary>选择器 → 作用域 key：:root/html → "html"，* → "*"，其余取末段简单选择器（去伪类/属性段）。</summary>
    static string? SelectorScopeKey(string sel)
    {
        if (sel.Length == 0 || sel.StartsWith("@")) return null;
        if (sel == ":root" || sel.Equals("html", StringComparison.OrdinalIgnoreCase)) return "html";
        if (sel == "*") return "*";
        var parts = Regex.Split(sel, @"[\s>+~]+").Where(x => x.Length > 0).ToArray();
        var last = parts.Length > 0 ? parts[^1] : sel;
        last = Regex.Replace(last, @"[:\[].*$", ""); // 去掉伪类/属性选择器
        return last.Length > 0 ? last.ToLowerInvariant() : null;
    }

    /// <summary>
    /// 把「全局唯一定值」的自定义属性直接写回 CSS 源码（`padding: var(--pad)` → `padding: 14px`）。
    ///
    /// 为什么必须在解析前：AngleSharp 认不出值里带 var() 的**简写**，整条声明直接丢弃——实测
    /// `padding: var(--pad)`、`border: 1px solid var(--brand)`、`margin: var(--pad)` 的盒几何全为 0
    /// （m4 因此均值 172px），而长写 `padding-bottom: var(--pad)` 能作为原始串读到、由 ExpandVars 救回。
    /// 只有同名变量在多个作用域取到**不同值**（m4 的 --accent：:root/.scoped/.accent2 各一份）时才有
    /// 元素级作用域可谈，那种保持原样交给 ExpandVars 逐元素裁决；唯一定值的变量在任何元素上都等于
    /// 那个串，文本内联与逐元素展开结果一致，且顺带把 calc()/简写里的引用一并喂给了 AngleSharp。
    /// 只改引用处、不动 `--x: …` 声明本身，故 VarMap 仍完整（内联 style 属性里的 var 照旧走逐元素路）。
    /// </summary>
    /// <summary>
    /// 把值里带 <c>var()</c> 的 <c>border</c> / <c>border-{side}</c> 简写拆成 12 条（或 3 条）长写。
    ///
    /// 为什么必须拆：AngleSharp 认不出值里含 var() 的**简写**，整条声明直接丢弃（探针 _probe-brd5 实测
    /// <c>border: 4px solid var(--multi)</c> 四条边全没、<c>border-bottom: calc(2px + 1pt) solid var(--x)</c>
    /// 少 3px），而 m4 的 h1/.scoped/.quote 正好都是这一形。长写带 var() 是读的（同探针 .e 用例：
    /// <c>border-bottom-color: var(--multi)</c> 盒高与浏览器一致），引擎侧再由 <see cref="ExpandVars"/>
    /// 逐元素展开，作用域覆盖照常生效。
    /// 只对「值里确实有 var()」的简动手，其余简写原样留给 AngleSharp（它解析得比我们好）。
    /// 缺的分量按 CSS 初值补：width=medium、style=none、color=currentColor —— 简写本就重置这三项。
    /// </summary>
    static string ExpandVarBorderShorthands(string css)
    {
        if (string.IsNullOrWhiteSpace(css) || !css.Contains("var(", StringComparison.OrdinalIgnoreCase)) return css;
        return Regex.Replace(css, @"(^|[{;\s])((?:border|border-top|border-right|border-bottom|border-left)(?<!-)\w*)\s*:\s*([^;{}]*)",
            m =>
            {
                var prop = m.Groups[2].Value;
                var value = m.Groups[3].Value;
                // 只处理精确的 border 系列属性名（挡掉 border-radius / border-spacing / border-image / -collapse）
                if (prop.Length > 6 && !prop.Equals("border-top", StringComparison.OrdinalIgnoreCase)
                    && !prop.Equals("border-right", StringComparison.OrdinalIgnoreCase)
                    && !prop.Equals("border-bottom", StringComparison.OrdinalIgnoreCase)
                    && !prop.Equals("border-left", StringComparison.OrdinalIgnoreCase)
                    && !prop.Equals("border", StringComparison.OrdinalIgnoreCase)) return m.Value;
                if (!value.Contains("var(", StringComparison.OrdinalIgnoreCase)) return m.Value;
                var pre = m.Groups[1].Value;
                return pre + string.Join(";", Sides(prop).SelectMany(side =>
                {
                    var (w, st, c) = SplitBorderValue(value);
                    return new[] { $"{side}-width:{w}", $"{side}-style:{st}", $"{side}-color:{c}" };
                })) + ";";
            });
    }

    static IEnumerable<string> Sides(string prop) => prop.Length > 6
        ? new[] { prop } : new[] { "border-top", "border-right", "border-bottom", "border-left" };

    /// <summary>把 border 简写值按顶层空格切 token，分派 width / style / color 三段；未知 token（含 var()）三段都填。</summary>
    static (string Width, string Style, string Color) SplitBorderValue(string value)
    {
        string w = null, st = null, co = null;
        foreach (var tok in SplitTopLevel(value))
        {
            if (tok.Length == 0) continue;
            if (IsBorderWidthKw(tok) || IsSizeToken(tok)) w ??= tok;
            else if (IsBorderStyleKw(tok)) st ??= tok;
            else if (tok.StartsWith("var(", StringComparison.OrdinalIgnoreCase)) { w ??= tok; st ??= tok; co ??= tok; }
            else co ??= tok;
        }
        return (w ?? "medium", st ?? "none", co ?? "currentColor");
    }

    /// <summary><c>border-color</c> 简写的 1~4 值按 CSS 规则落到某条边：side 序 0=top、1=right、2=bottom、3=left。</summary>
    static string ColorSlot(IReadOnlyList<string> t, int side) => t.Count switch
    {
        0 => "",
        1 => t[0],
        2 => t[side % 2],
        3 => t[side == 3 ? 1 : side],
        _ => t[side],
    };

    /// <summary>是否长度类 token：数字打头（含符号/小数点）、带单位函数（calc()/min()/max()/clamp()）或百分比。</summary>
    static bool IsSizeToken(string t)
    {
        if (t.Length == 0) return false;
        var c0 = t[0];
        if (char.IsDigit(c0) || c0 == '.' || c0 == '+' || c0 == '-') return true;
        return t.StartsWith("calc(", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("min(", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("max(", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("clamp(", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("env(", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsBorderWidthKw(string t) =>
        t.Equals("thin", StringComparison.OrdinalIgnoreCase) || t.Equals("medium", StringComparison.OrdinalIgnoreCase)
        || t.Equals("thick", StringComparison.OrdinalIgnoreCase);

    static bool IsBorderStyleKw(string t) => t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("hidden", StringComparison.OrdinalIgnoreCase)
        || t.Equals("dotted", StringComparison.OrdinalIgnoreCase) || t.Equals("dashed", StringComparison.OrdinalIgnoreCase)
        || t.Equals("solid", StringComparison.OrdinalIgnoreCase) || t.Equals("double", StringComparison.OrdinalIgnoreCase)
        || t.Equals("groove", StringComparison.OrdinalIgnoreCase) || t.Equals("ridge", StringComparison.OrdinalIgnoreCase)
        || t.Equals("inset", StringComparison.OrdinalIgnoreCase) || t.Equals("outset", StringComparison.OrdinalIgnoreCase);

    /// <summary>按顶层空格切分：括号与字符串内部的空格不是分隔符（calc(1px + 2pt)、var(--a, #f00)）。</summary>
    static List<string> SplitTopLevel(string v)
    {
        var outp = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        char quote = '\0';
        foreach (var c in v)
        {
            if (quote != '\0') { sb.Append(c); if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'' or '`') { quote = c; sb.Append(c); continue; }
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            if (c == ' ' && depth == 0) { if (sb.Length > 0) { outp.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) outp.Add(sb.ToString());
        return outp;
    }

    static string InlineUniqueVars(string css)
    {
        if (string.IsNullOrWhiteSpace(css) || !css.Contains("var(")) return css;

        // 定值表：名字 → 值；同名取到不同值即歧义（作用域相关），整条退出内联
        var defs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match block in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
            foreach (Match d in Regex.Matches(block.Groups[2].Value, @"(--[A-Za-z][\w-]*)\s*:\s*([^;}]+)"))
            {
                var name = d.Groups[1].Value;
                var val = d.Groups[2].Value.Trim();
                if (ambiguous.Contains(name)) continue;
                if (defs.TryGetValue(name, out var prev))
                {
                    if (prev != val) { ambiguous.Add(name); defs.Remove(name); }
                    continue;
                }
                defs[name] = val;
            }
        if (defs.Count == 0) return css;

        // 值本身可能再引用别的变量（变量链）：先把可内联的名字解析到底，成环的退出
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in defs.Keys.ToList())
            if (ResolveVarText(name, defs, resolved, new HashSet<string>(StringComparer.OrdinalIgnoreCase)) is { } rv)
                resolved[name] = rv;
        if (resolved.Count == 0) return css;

        var sb = new StringBuilder(css.Length);
        for (int i = 0; i < css.Length;)
        {
            char c = css[i];
            if (c == '"' || c == '\'')  // 字符串字面量（content: "var(--x)"）原样抄，内联会把它当值替换掉
            {
                int q = i + 1;
                while (q < css.Length && css[q] != c) q += css[q] == '\\' ? 2 : 1;
                q = Math.Min(q + 1, css.Length);
                sb.Append(css, i, q - i);
                i = q;
                continue;
            }
            if ((c == 'v' || c == 'V') && i + 4 <= css.Length && css.AsSpan(i, 4).Equals("var(", StringComparison.OrdinalIgnoreCase))
            {
                int close = MatchParen(css, i + 3);
                if (close > 0 && TryParseVar(css[(i + 4)..close], out var vname, out var vfallback)
                    && resolved.TryGetValue(vname, out var vval))
                {
                    sb.Append(vval);
                    i = close + 1;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();

        // 引用链解析：命中歧义名 / 环 / 括号不闭合都返回 null，该名字保持 var() 原样。
        string? ResolveVarText(string name, Dictionary<string, string> defs, Dictionary<string, string> done, HashSet<string> path)
        {
            if (done.TryGetValue(name, out var cached)) return cached;
            if (!path.Add(name)) return null;
            var text = Subst(defs[name]);
            path.Remove(name);
            if (text != null) done[name] = text;
            return text;

            string? Subst(string value)
            {
                var b = new StringBuilder(value.Length);
                for (int k = 0; k < value.Length;)
                {
                    if ((value[k] == 'v' || value[k] == 'V') && k + 4 <= value.Length
                        && value.AsSpan(k, 4).Equals("var(", StringComparison.OrdinalIgnoreCase))
                    {
                        int close = MatchParen(value, k + 3);
                        if (close > 0 && TryParseVar(value[(k + 4)..close], out var n2, out var fb))
                        {
                            string? v2 = defs.TryGetValue(n2, out var dv) ? ResolveVarText(n2, defs, done, path) : null;
                            if (v2 == null && fb == null) return null;      // 歧义/成环且无兜底 → 整条不内联
                            b.Append(v2 ?? (fb ?? "").Trim());
                            k = close + 1;
                            continue;
                        }
                        return null;
                    }
                    b.Append(value[k]);
                    k++;
                }
                return b.ToString();
            }
        }
    }

    /// <summary>自 open（'(' 的下标）起找配对 ')'，返回其下标；不闭合返回 -1。</summary>
    static int MatchParen(string s, int open)
    {
        for (int j = open + 1, depth = 1; j < s.Length; j++)
        {
            if (s[j] == '(') depth++;
            else if (s[j] == ')' && --depth == 0) return j;
        }
        return -1;
    }

    /// <summary>拆 var() 的圆括号内文本：变量名 + 顶层逗号后的兜底值。名字非法（嵌套函数等）返回 false。</summary>
    static bool TryParseVar(string inner, out string name, out string? fallback)
    {
        name = "";
        fallback = null;
        int comma = -1;
        for (int d = 0, k = 0; k < inner.Length; k++)
        {
            if (inner[k] == '(') d++;
            else if (inner[k] == ')') d--;
            else if (inner[k] == ',' && d == 0) { comma = k; break; }
        }
        var n = (comma < 0 ? inner : inner[..comma]).Trim();
        if (n.Length < 3 || n[0] != '-' || n[1] != '-' || !char.IsLetter(n[2])) return false;
        name = n;
        if (comma >= 0) fallback = inner[(comma + 1)..];
        return true;
    }

    /// <summary>把值里的 var(--x[, 兜底]) 按元素作用域展开；支持嵌套与变量链，环/超深在 16 轮后放弃。</summary>
    static string ExpandVars(string value, IElement element, DocContext ctx)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("var(", StringComparison.OrdinalIgnoreCase)) return value;
        for (int guard = 0; guard < 16; guard++)
        {
            int at = value.IndexOf("var(", StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;
            int j = at + 4, depth = 1;
            while (j < value.Length && depth > 0)
            {
                if (value[j] == '(') depth++;
                else if (value[j] == ')') depth--;
                j++;
            }
            if (depth > 0) break;   // 括号不闭合，停止展开避免烂尾
            var inner = value[(at + 4)..(j - 1)];
            int comma = -1, d2 = 0;
            for (int k = 0; k < inner.Length; k++)
            {
                if (inner[k] == '(') d2++;
                else if (inner[k] == ')') d2--;
                else if (inner[k] == ',' && d2 == 0) { comma = k; break; }
            }
            var name = (comma < 0 ? inner : inner[..comma]).Trim();
            var fallback = comma < 0 ? "" : inner[(comma + 1)..].Trim();
            var def = name.StartsWith("--") ? FindVar(element, name, ctx) : null;
            value = value[..at] + (def ?? fallback) + value[j..];
        }
        return value;
    }

    /// <summary>内联 <c>style="a:b; c:d"</c> 拆成小写键表（无声明→null）。键按 CSS 属性名原样，值去空白。</summary>
    static Dictionary<string, string>? ParseStyleAttribute(string? style)
    {
        if (string.IsNullOrWhiteSpace(style)) return null;
        Dictionary<string, string>? d = null;
        foreach (var decl in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = decl.IndexOf(':');
            if (colon <= 0) continue;
            var k = decl[..colon].Trim().ToLowerInvariant();
            if (k.Length == 0) continue;
            (d ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[k] = decl[(colon + 1)..].Trim();
        }
        return d;
    }

    /// <summary><ol type="a|A|i|I|1"> / <ul type="disc|circle|square"> → 本引擎的 list-style-type 规范名；没有或不认识→null。</summary>
    static string? ListTypeHint(IElement el)
    {
        var t = (el.GetAttribute("type") ?? "").Trim();
        if (t.Length == 0) return null;
        if (el.TagName.Equals("OL", StringComparison.OrdinalIgnoreCase))
            return t switch
            {
                "1" => "decimal", "a" => "lower-alpha", "A" => "upper-alpha",
                "i" => "lower-roman", "I" => "upper-roman", _ => null,
            };
        return CssValueParser.ListStyleTypeKeyword(t);
    }

    /// <summary>list-style 简写里的 type 关键字（disc/circle/square/decimal/lower-roman/…，含 latin↔alpha 别名）；没有→null。</summary>
    static string? ListShorthandType(string decl) => ListShorthandKeyword(decl, CssValueParser.ListStyleTypeKeyword);

    /// <summary>list-style 简写里的 position 关键字 inside/outside；没有→null。</summary>
    static string? ListShorthandPosition(string decl) => ListShorthandKeyword(decl, k => k is "inside" or "outside" ? k : null);

    static string? ListShorthandKeyword(string decl, Func<string, string?> match)
    {
        if (string.IsNullOrWhiteSpace(decl)) return null;
        foreach (var tok in TopLevelTokens(decl))
            if (match(tok.ToLowerInvariant()) is { } v) return v;
        return null;
    }

    /// <summary>按 CSS 继承语义查变量：从元素自身向上走，同级内联 style &gt; class &gt; id &gt; tag &gt; *。</summary>
    static string? FindVar(IElement el, string name, DocContext ctx)
    {
        var varMap = ctx.VarMap;
        for (IElement? e = el; e != null; e = e.ParentElement)
        {
            var inline = e.GetAttribute("style");
            if (inline != null && inline.Contains(name, StringComparison.Ordinal))
            {
                var m = Regex.Match(inline, Regex.Escape(name) + @"\s*:\s*([^;]+)");
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            var cls = e.GetAttribute("class");
            if (!string.IsNullOrWhiteSpace(cls))
                foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (varMap.TryGetValue("." + c, out var cm) && cm.TryGetValue(name, out var v)) return v;
            var id = e.GetAttribute("id");
            if (!string.IsNullOrWhiteSpace(id) && varMap.TryGetValue("#" + id, out var im) && im.TryGetValue(name, out var iv)) return iv;
            var tag = e.TagName.ToLowerInvariant();
            if (varMap.TryGetValue(tag, out var tm) && tm.TryGetValue(name, out var tv)) return tv;
            if (varMap.TryGetValue("*", out var wm) && wm.TryGetValue(name, out var wv)) return wv;
            if (e.TagName.Equals("HTML", StringComparison.OrdinalIgnoreCase)) break; // 已到根，html 的 key 亦即 :root
        }
        return null;
    }

    /// <summary>
    /// HTML 表现属性（img 的 width/height）→ 尺寸值。单位是 CSS px（布局单位），
    /// 允许 <c>50%</c>；非法/缺省一律回退 auto，让上层按固有尺寸走。
    /// </summary>
    static CssSize PresentationalSize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return CssSize.Auto;
        raw = raw.Trim();
        if (raw.EndsWith("%", StringComparison.Ordinal)
            && float.TryParse(raw[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            return CssSize.Pct(pct);
        return float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px) && px >= 0
            ? CssSize.Abs(px) : CssSize.Auto;
    }

    static ComputedStyle Walk(IElement element, ComputedStyle? parent, DocContext ctx,
                              FlatCascade.Scope? fparent = null, int nthOfType = 1)
    {
        var fs = ctx.Flat?.For(element, fparent);
        // 索引覆盖不了这个元素（如两个类规则同时命中）：整份退回 AngleSharp 继续，不在半路混用两套继承链。
        if (fs == null && ctx.Flat != null) ctx.Flat = null;
        var cs = ReadStyle(element, parent, ctx, flat: fs);
        cs.Tag = element.TagName.ToLowerInvariant();

        // 列表：标签默认（UA 的 ul{list-style-type:disc} / ol{decimal}）与 <ol type> / <ul type> 的
        // presentational hint。两条都只在作者没写过 list-style* 时才生效——作者声明压过 UA 提示是级联的铁律。
        if (cs.Tag is "ul" or "ol")
        {
            // AngleSharp 自带的 UA 表就把 ol 的 list-style-type 报成 decimal、ul 报成 disc，和作者声明
            // 长得一模一样（SpecOnly 拿到的也是它），所以 authorSet 对这两标签不可信。判据换成
            // "值是否等于本标签的 UA 默认"：等于就说明没人真写过，<ol type> 提示与标签默认都能上。
            string uaDefault = cs.Tag == "ol" ? "decimal" : "disc";
            var hint = ListTypeHint(element);
            bool authorWroteIt = cs.ListStyleAuthorSet && cs.ListStyleType != uaDefault;
            if (!authorWroteIt) cs.ListStyleType = hint ?? uaDefault;
            if (int.TryParse(element.GetAttribute("start"), out var lstart) && lstart >= 1)
                cs.ListStartOffset = lstart - 1;
        }

        // DOM 身份串（body/div[1]/p[2]）：兄弟序按 CSS nth-of-type 的口径数——连 script/style/br 这些
        // 不成盒的兄弟也占号，浏览器侧才能用同一条规则数出同一个下标。默认关，见 DocContext.DomPath。
        if (ctx.DomPath)
            cs.DomPath = (parent?.DomPath is { Length: > 0 } pp ? pp + "/" + cs.Tag : cs.Tag) + "[" + nthOfType + "]";

        // 替换元素：读取 <img> 的 src
        if (cs.Tag == "img")
        {
            cs.ImageSrc = element.GetAttribute("src");
            // HTML 的 width/height 属性是 presentational hint：参与布局，但优先级低于任何作者 CSS 声明
            // （浏览器把它当特异度 0 的作者规则）。故只在 CSS 侧仍是 auto 时兜底填上，
            // 反过来覆盖就会把 `img { width: 100% }` 这类规则吃掉。
            if (cs.Width.IsAuto) cs.Width = PresentationalSize(element.GetAttribute("width"));
            if (cs.Height.IsAuto) cs.Height = PresentationalSize(element.GetAttribute("height"));
        }

        // <a> 的 href：供 PDF 链接注释
        if (cs.Tag == "a")
            cs.LinkHref = element.GetAttribute("href");

        // <input>：读取 type / checked / placeholder。文本类控件无值时以 placeholder 占位显示；
        // checkbox/radio 由绘制层据 InputType 画原生方框/圆框（无子盒，提前返回）。
        if (cs.Tag == "input")
        {
            cs.InputType = (element.GetAttribute("type") ?? "text").ToLowerInvariant();
            cs.InputChecked = element.HasAttribute("checked");
            if (cs.InputType is "checkbox" or "radio") return cs;
            var ph = element.GetAttribute("placeholder");
            if (!string.IsNullOrEmpty(ph))
            {
                cs.PlaceholderText = ph;
                cs.TextContent = ph;   // 走块/inline 文本路径绘制
                // 占位符不吃控件自身的 color（浏览器里 input{color:blue} 的占位符仍是系统灰），
                // 默认色取 Chrome 实测的 placeholderText #757575；作者写了 ::placeholder{color:…} 才覆盖。
                cs.Color = (LookupPseudoMap(ctx.PseudoDisplay, element, "::placeholder") is { Length: > 0 } phColor
                            && CssValueParser.ParseColor(phColor) is { } pc) ? pc : PlaceholderColor;
            }
            return cs;
        }

        // 内联 <svg>：整体捕获为替换内容，内部节点不成盒
        if (cs.Tag == "svg")
        {
            cs.SvgMarkup = element.OuterHtml;
            return cs;
        }

        // 单元格 colspan（HTML 属性，非 CSS）
        if (cs.Tag == "td" || cs.Tag == "th")
        {
            if (int.TryParse(element.GetAttribute("colspan"), out var span) && span > 1) cs.Colspan = span;
            if (int.TryParse(element.GetAttribute("rowspan"), out var rspan) && rspan > 1) cs.Rowspan = rspan;
        }

        // 按 DOM 顺序遍历子节点：文本→InlinePiece(文本)；元素→递归 + Children + InlinePiece(子/br)。
        // 同时聚合直接文本到 TextContent（块路径用），并保留有序片段（inline 路径用）。
        string allText = "";
        // 同标签兄弟计数器（仅开 DomPath 时建）：占号发生在任何 continue 之前，口径才等于 nth-of-type。
        Dictionary<string, int>? tagSeq = ctx.DomPath ? new(StringComparer.OrdinalIgnoreCase) : null;
        foreach (var node in element.ChildNodes)
        {
            if (node.NodeType == NodeType.Text)
            {
                string t = node.TextContent;
                allText += t;
                if (!string.IsNullOrEmpty(t)) cs.InlinePieces.Add(InlinePiece.OfText(t));
            }
            else if (node is IElement ce)
            {
                int seq = 1;
                if (tagSeq != null)
                {
                    tagSeq.TryGetValue(ce.TagName, out seq);
                    tagSeq[ce.TagName] = ++seq;
                }
                if (NonRenderingTags.Contains(ce.TagName)) continue;
                if (ce.TagName.Equals("BR", StringComparison.OrdinalIgnoreCase))
                {
                    // <br> 只是换行标记，不成盒：进 InlinePieces，但不进 Children（否则会被当成块级子元素破坏 inline 判定）
                    cs.InlinePieces.Add(InlinePiece.Break);
                    continue;
                }
                var childStyle = Walk(ce, cs, ctx, fs, seq);
                cs.Children.Add(childStyle);
                cs.InlinePieces.Add(InlinePiece.OfChild(childStyle));
            }
        }
        allText = allText.Replace("\r\n", "\n").Trim();
        cs.TextContent = string.IsNullOrEmpty(allText) ? null : allText;

        // text-transform 在源头施加一次，下游折行/测量/绘制都拿到已变形的文本
        if (cs.TextTransform is "uppercase" or "lowercase" or "capitalize" && cs.TextContent != null)
            cs.TextContent = ApplyTransform(cs.TextContent, cs.TextTransform);

        // 判定纯 inline 格式化上下文：块级、无块级子元素、且含【行内元素 / 换行 / 图文混排的 img】。
        // 关键：只有文本、无任何行内结构的块 NOT 走这里 → 仍用老的 WrappedLines 路径，保证既有样例零回归。
        bool hasBlockChild = cs.Children.Any(c => c.Display != "none" && !ComputedStyle.IsInlineLevel(c.Display) && c.Tag is not ("img" or "svg"));
        bool hasInlineEl = cs.Children.Any(c => ComputedStyle.IsInlineLevel(c.Display) && c.Tag != "img");
        bool hasImgChild = cs.Children.Any(c => c.Tag is "img" or "svg" && c.Display != "none");
        bool hasBr = cs.InlinePieces.Any(p => p.IsBreak);
        // 只有 block 与 table-cell 为其内容建立 inline 格式化上下文（flex/table/row/行内级都不算）
        // list-item 也算（AngleSharp 的 UA 表给 li 的就是它）：漏掉的话 li 里的 <span> 走不到
        // InlinePieces，块路径只收直接文本 → 整段彩色 span 凭空消失。
        bool blockish = cs.Display is "block" or "table-cell" or "list-item";
        cs.IsInlineFC = blockish && !hasBlockChild && (hasInlineEl || hasBr || (hasImgChild && cs.TextContent != null));

        // 块里与块级兄弟混排的行内级内容（inline 元素、img/svg）：CSS 会把它裹进匿名块再立行盒。
        // 不裹的话它就是父块的一个普通块级子盒 —— 整行拉满、按块级定位（m1 的
        // div.note > strong 实测引擎 1168×21 vs 浏览器 132×17，还连带把后面的 <ul> 顶下一行）。
        // 只裹「元素行内片段」：直接文本参与裹行需要按 DOM 序切分文本，本引擎的 TextContent
        // 是整块合并的，切不准则宁可退回老路径，故有直接文本时不接管。
        if (blockish && !cs.IsInlineFC && string.IsNullOrEmpty(cs.TextContent))
            cs.Children = WrapAnonymousInlineRuns(cs);

        // 伪元素 ::before/::after：仅当 CSS 里确有伪元素规则时才建生成盒（content 片段 + 自身样式），
        // 实际注入与 counter 回填在 BuildDocument 末尾的 PseudoContent.Apply 文档序 pass 里做。
        if (ctx.HasPseudoRules && PseudoMayMatch(element, ctx))
        {
            cs.Before = BuildPseudoGen(element, cs, ctx, "::before", fs);
            cs.After = BuildPseudoGen(element, cs, ctx, "::after", fs);
        }

        return cs;
    }

    /// <summary>
    /// 把块级子元素之间连续的行内级片段（inline 元素 / img / svg）裹成匿名 inline-FC 块
    /// （CSS 的 anonymous block），其余子元素原位保留。浏览器对「行内 + 块」混排正是这么建的：
    /// 匿名块给行内内容立一行盒，图片下方那截 strut descent 也才留得下来。
    /// 纯 img/svg 的run即便父块没有块级兄弟也要裹（保持原行为：块里只有图时整块就是行盒）；
    /// 纯 inline 元素的run只有在确实与块级兄弟混排时才裹——全是行内时上面已判成 IsInlineFC。
    /// </summary>
    static List<ComputedStyle> WrapAnonymousInlineRuns(ComputedStyle owner)
    {
        bool Inlineish(ComputedStyle c) => c.Display != "none"
            && (c.Tag is "img" or "svg" || ComputedStyle.IsInlineLevel(c.Display));
        bool Replaced(ComputedStyle c) => c.Tag is "img" or "svg";
        bool hasBlockSibling = owner.Children.Any(c => !Inlineish(c));
        var outList = new List<ComputedStyle>(owner.Children.Count);
        List<ComputedStyle>? run = null;
        void Flush()
        {
            if (run == null) return;
            bool worthWrapping = hasBlockSibling || run.All(Replaced);
            if (worthWrapping) outList.Add(AnonInlineBlock(owner, run));
            else outList.AddRange(run);   // 没有块级兄弟可比照，退回原样（不凭空造匿名层）
            run = null;
        }
        foreach (var c in owner.Children)
        {
            if (Inlineish(c)) (run ??= new List<ComputedStyle>()).Add(c);
            else { Flush(); outList.Add(c); }
        }
        Flush();
        return outList;
    }

    /// <summary>
    /// 匿名 inline 块：只承载行内片段。匿名盒按规范只继承可继承属性，故盒装饰与尺寸一律清零
    /// （父级 padding/margin/%宽不得复制进来）；DomPath 由 CloneShallow 归 null，比对时不出盒。
    /// </summary>
    static ComputedStyle AnonInlineBlock(ComputedStyle owner, List<ComputedStyle> run)
    {
        var anon = owner.CloneShallow();
        anon.Display = "block";
        anon.IsInlineFC = true;
        anon.TextContent = null;
        anon.Before = anon.After = null;
        anon.Children = new();
        anon.InlinePieces = new();
        foreach (var c in run)
        {
            c.Parent = anon;   // 行内片段的祖先链改挂匿名块：内联补盒上溯到此即止，不会越过它去改祖先
            anon.InlinePieces.Add(InlinePiece.OfChild(c));
        }
        anon.Width = anon.Height = anon.MinWidth = anon.MaxWidth = anon.MinHeight = anon.MaxHeight = default;
        anon.MarginTopCss = anon.MarginRightCss = anon.MarginBottomCss = anon.MarginLeftCss = CssSize.Abs(0);
        anon.MarginTopAuto = anon.MarginRightAuto = anon.MarginBottomAuto = anon.MarginLeftAuto = false;
        anon.MarginTop = anon.MarginRight = anon.MarginBottom = anon.MarginLeft = 0;
        anon.PaddingTopCss = anon.PaddingRightCss = anon.PaddingBottomCss = anon.PaddingLeftCss = default;
        anon.PaddingTop = anon.PaddingRight = anon.PaddingBottom = anon.PaddingLeft = 0;
        anon.BorderWidthTop = anon.BorderWidthRight = anon.BorderWidthBottom = anon.BorderWidthLeft = 0;
        anon.BorderRadiusTopLeft = anon.BorderRadiusTopRight = anon.BorderRadiusBottomRight = anon.BorderRadiusBottomLeft = 0;
        anon.BorderRadiusTopLeftY = anon.BorderRadiusTopRightY = anon.BorderRadiusBottomRightY = anon.BorderRadiusBottomLeftY = 0;
        anon.BorderRadiusPercent = null;
        anon.BackgroundColor = SKColor.Empty;
        anon.Background = null;
        anon.Shadows = null;
        return anon;
    }

    /// <summary>为一个伪元素建生成盒：读 content（不成盒则返回 null）+ 伪元素自身 computed 样式（display 归一为 inline 缺省）。</summary>
    static PseudoGen? BuildPseudoGen(IElement element, ComputedStyle owner, DocContext ctx,
                                     string pseudo, FlatCascade.Scope? fscope)
    {
        var fp = ctx.Flat?.ForPseudo(element, pseudo, fscope);
        // 索引整体适用而这类没有伪元素规则 = 确定没有，不必再让 AngleSharp 为它跑一整轮层叠。
        if (fp == null && ctx.Flat != null && fscope != null) return null;
        string Content()
        {
            // 展平快照：content 就在 .ps-N::before 那条规则里，直接取。
            if (fp != null) return ExpandVars(fp.Get("content"), element, ctx);
            // 用 AngleSharp computed 判定伪元素是否成盒并取串：它做真正的选择器匹配（避免按 token 兜底把
            // `ul.tags li::after` 泄漏到 `ol.outline li`）。counters(a,',') 会被它重序列化成 counters(a ,)
            // （吞逗号），交由 PseudoContent.Parse 的空格/逗号双兼容解析还原。
            try
            {
                var c = ctx.Styles.ComputeDeclarations(element, pseudo).GetPropertyValue("content");
                return ExpandVars(c ?? "", element, ctx);
            }
            catch { return ""; }
        }
        var runs = PseudoContent.Parse(Content(), name => element.GetAttribute(name));
        if (runs == null) return null;   // none / normal / 未设 → 不生成盒

        var style = ReadStyle(element, owner, ctx, pseudo, fp);
        // display：AngleSharp 对伪元素 UA 默认注入 display:block（computed/declared 都是），无法据此判断作者意图；
        // 故以「作者是否在原始 CSS 里显式声明 display」为准——未声明则按 CSS 规范初值 inline。
        var declaredDisp = LookupPseudoMap(ctx.PseudoDisplay, element, pseudo) ?? "";
        style.Display = string.IsNullOrWhiteSpace(declaredDisp) || declaredDisp == "normal"
            ? "inline" : declaredDisp;
        style.Children = new();
        style.InlinePieces = new();
        style.TextContent = null;
        style.Before = style.After = null;
        style.HasGeneratedInline = false;
        return new PseudoGen { Style = style, Runs = runs };
    }

    /// <summary>扫原始 CSS：记录每个伪元素规则里显式声明的 display 与 content 原文（键 = 末段简单选择器 token + ::before/::after）。</summary>
    static void ScanPseudoDisplay(string css, DocContext ctx)
    {
        if (string.IsNullOrWhiteSpace(css)) return;
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
        {
            var sel = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            if (Regex.IsMatch(sel, @"::placeholder\b", RegexOptions.IgnoreCase))
            {
                // 占位符颜色：只记 color，且不进 PseudoScope（那是 ::before/::after 生成盒的闸门）。
                var cm = Regex.Match(body, @"(?:^|[;\s])color\s*:\s*([^;}]+)", RegexOptions.IgnoreCase);
                if (!cm.Success) continue;
                foreach (var selPart in sel.Split(','))
                {
                    var toks = Regex.Split(Regex.Replace(selPart, @"::placeholder", " ", RegexOptions.IgnoreCase).Trim(),
                                           @"[\s>+~]+").Where(x => x.Length > 0).ToArray();
                    if (toks.Length == 0) continue;
                    var tok = Regex.Replace(toks[^1], @"[:\[].*$", "");
                    if (tok.Length > 0) ctx.PseudoDisplay[tok + "::placeholder"] = cm.Groups[1].Value.Trim();
                }
                continue;
            }
            string suffix;
            if (Regex.IsMatch(sel, @"::?before\b", RegexOptions.IgnoreCase)) suffix = "::before";
            else if (Regex.IsMatch(sel, @"::?after\b", RegexOptions.IgnoreCase)) suffix = "::after";
            else continue;
            var dm = Regex.Match(body, @"(?:^|[;\s])display\s*:\s*([^;}]+)", RegexOptions.IgnoreCase);
            foreach (var selPart in sel.Split(','))
            {
                var stripped = Regex.Replace(selPart, @"::?(before|after)", " ", RegexOptions.IgnoreCase).Trim();
                var toks = Regex.Split(stripped, @"[\s>+~]+").Where(x => x.Length > 0).ToArray();
                var rawLast = toks.Length > 0 ? toks[^1] : "";
                // 末段含 :is()/:where()/属性选择器/通配/伪类以外的结构时，token 匹配不再保守 → 放弃跳过
                if (rawLast.Length == 0 || rawLast.Contains(":is(") || rawLast.Contains(":where(")
                    || rawLast.Contains('[') || rawLast.Contains('*') || rawLast.Contains(":not("))
                {
                    ctx.PseudoScope = null;
                    continue;
                }
                var last = Regex.Replace(rawLast, @"[:\[].*$", "");   // 去伪类/属性段
                if (ctx.PseudoScope != null && last.Length > 0) ctx.PseudoScope.Add(last.ToLowerInvariant());
                if (last.Length == 0) continue;
                if (dm.Success) ctx.PseudoDisplay[last + suffix] = dm.Groups[1].Value.Trim().ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// 该元素是否可能命中某条伪元素规则：按「末段简单选择器 token」保守筛（class &gt; id &gt; tag 任一命中即放行）。
    /// ctx.PseudoScope 为 null（CSS 里有 :is()/[attr]/* 之类无法约成 token 的末段）时一律放行，保持旧行为。
    /// </summary>
    static bool PseudoMayMatch(IElement element, DocContext ctx)
    {
        var scope = ctx.PseudoScope;
        if (scope == null || scope.Count == 0) return true;
        if (scope.Contains(element.TagName.ToLowerInvariant())) return true;
        var id = element.GetAttribute("id");
        if (!string.IsNullOrEmpty(id) && scope.Contains("#" + id)) return true;
        var cls = element.GetAttribute("class");
        if (!string.IsNullOrWhiteSpace(cls))
            foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (scope.Contains("." + c)) return true;
        return false;
    }

    /// <summary>按元素 class/id/tag 命中伪元素原始 CSS 索引（就近优先 class &gt; id &gt; tag）。</summary>
    static string? LookupPseudoMap(Dictionary<string, string> map, IElement el, string pseudo)
    {
        var suffix = pseudo.Contains("before", StringComparison.OrdinalIgnoreCase) ? "::before"
                   : pseudo.Contains("after", StringComparison.OrdinalIgnoreCase) ? "::after"
                   : pseudo;   // ::placeholder：键就是原样后缀
        string? Get(string tok) => map.TryGetValue(tok + suffix, out var v) ? v : null;
        var cls = el.GetAttribute("class");
        if (!string.IsNullOrWhiteSpace(cls))
            foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            { var v = Get("." + c); if (v != null) return v; }
        var id = el.GetAttribute("id");
        if (!string.IsNullOrWhiteSpace(id)) { var v = Get("#" + id); if (v != null) return v; }
        return Get(el.TagName.ToLowerInvariant());
    }

    internal static string ApplyTransform(string text, string mode)
    {
        if (mode == "uppercase") return text.ToUpperInvariant();
        if (mode == "lowercase") return text.ToLowerInvariant();
        // capitalize：每个词首字母大写
        var sb = new System.Text.StringBuilder(text.Length);
        bool atWordStart = true;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { atWordStart = true; sb.Append(ch); }
            else { sb.Append(atWordStart ? char.ToUpperInvariant(ch) : ch); atWordStart = false; }
        }
        return sb.ToString();
    }

    static ComputedStyle ReadStyle(IElement element, ComputedStyle? parent, DocContext ctx,
                                   string? pseudo = null, FlatCascade.Scope? flat = null)
    {
        var s = new ComputedStyle { Parent = parent };

        // AngleSharp.Css 的层叠入口：GetComputedStyle(window, el, pseudo) 展开就是
        // window.GetStyleCollection().ComputeDeclarations(el, pseudo)，这里吃物化过的集合（见 EagerStyleCollection）。
        // 但它对某些值（如 radial-gradient）在 Compute() 里会抛 NullReferenceException——
        // 一旦抛异常该元素整个 computed 都拿不到。这里 try/catch 兜住：拿不到就退回声明值
        // （GetDeclarations，不触发 Compute）+ 父级继承，绝不让渲染崩。
        // pseudo 非空时读伪元素（::before/::after）的 computed，实测 AngleSharp 支持并返回 content。
        // flat 非空时（展平快照）两个调用都不做：那轮层叠在这类输入上实测 1.82 ms/元素，全是白烧。
        ICssStyleDeclaration? computed = null;
        PropCache? computedView = null, rawView = null;
        if (flat == null)
        {
            try { computed = ctx.Styles.ComputeDeclarations(element, pseudo); }
            catch { /* 该元素 computed 不可用，下面全部走 Spec 兜底 */ }
            computedView = new PropCache(computed, element, ctx);
        }
        string Get(string prop)
        {
            if (flat != null)
            {
                var v = flat.Get(prop);
                return v.Length == 0 ? "" : ExpandVars(v, element, ctx);
            }
            return computedView!.Read(prop);
        }

        // 声明值（specified）：AngleSharp 的 GetComputedStyle 会把 % 一律按渲染设备的
        // 视口尺寸（1024）折算成 px，从而丢失百分比信息（如 height:100% → "1024px"）。
        // GetDeclarations 返回的是做完 cascade 排序、但未经设备折算的原始声明值，
        // 其中的百分比被原样保留。需要按包含块解析的盒子尺寸属性用它兜底。
        // 展平快照里两者本就是一个东西（浏览器已经把 %/calc 折成 px），故 Spec 直接复用 Get。
        ICssStyleDeclaration? decls = null;
        if (flat == null && !ctx.Precomputed)
        {
            try { decls = ctx.Styles.GetDeclarations(element, pseudo); }
            catch { /* 拿不到声明值时退回 computed，不让渲染崩 */ }
        }
        if (flat == null) rawView = new PropCache(decls, element, ctx);
        string Spec(string prop) => flat != null ? Get(prop) : rawView!.Read(prop);
        // 尺寸类：声明值是百分比或 calc() → 用声明值（保留 % 与线性式，交布局层解析）；否则优先 computed
        // （已解析 px/em/rem 且含继承），computed 不可用时退回声明值。
        string Val(string prop)
        {
            var sp = Spec(prop);
            if (sp.EndsWith("%") || sp.Contains("calc(", StringComparison.OrdinalIgnoreCase)) return sp;
            var g = Get(prop);
            return string.IsNullOrEmpty(g) ? sp : g;
        }
        // 关键字/一般值：优先 computed，空则退回声明值（应对 computed 整体不可用的元素）。
        string Raw(string prop)
        {
            var g = Get(prop);
            return string.IsNullOrEmpty(g) ? Spec(prop) : g;
        }
        // 本元素自己的内联 style 属性表（AngleSharp 不认识的属性靠它兜底）。整个元素只解析一次。
        Dictionary<string, string>? inlineAttrs = null;
        string Inline(string prop)
        {
            inlineAttrs ??= ParseStyleAttribute(element.GetAttribute("style"));
            return inlineAttrs != null && inlineAttrs.TryGetValue(prop, out var v) ? v : "";
        }
        // 声明值 → 内联属性：给 text-indent/text-shadow/list-style* 这类 AngleSharp 未注册的属性用
        string Decl(string prop) { var v = Raw(prop); return v.Length != 0 ? v.Trim() : Inline(prop).Trim(); }
        // 只算"本元素自己写过"的声明：computed（含继承）与内联属性，不含父链
        string SpecOnly(string prop) { var v = Spec(prop); return v.Length != 0 ? v.Trim() : Inline(prop).Trim(); }
        // margin/padding 的初值是 0 不是 auto：展平快照只在浏览器算出的用值非 0 时才下发（capture.js），
        // 缺值即 0。走 ParseSize 的空调用会得到 CssSize.Auto，布局按 margin:auto 居中处理 → 整页块级元素错位。
        // 仅限展平路径：作者 CSS 那边 margin-left 读空正是 `margin: 0 auto` 的到达方式（AngleSharp 表达不出
        // auto 时返回空串），在这里补 0px 会把水平居中吃掉（实测打掉 m2 基准）。
        string Size0(string prop) { var v = Val(prop); return v.Length == 0 && flat != null ? "0px" : v; }

        s.Display = FirstNonEmpty(Raw("display"), "block"); // display 不继承
        // flex/grid 子项按 CSS 规范"块级化"（blockify）：inline→block、inline-block→block、
        // inline-flex→flex、inline-grid→grid、inline-table→table。否则行内级子项（如 flex 里的 <span>
        // 混排文本+链接）不会建立 inline 格式化上下文，导致其行内子元素被当成块逐行堆叠。
        if (parent is { Display: "flex" or "inline-flex" or "grid" or "inline-grid" })
            s.Display = s.Display switch
            {
                "inline" or "inline-block" => "block",
                "inline-flex" => "flex",
                "inline-grid" => "grid",
                "inline-table" => "table",
                _ => s.Display,
            };
        s.BoxSizing = CssValueParser.ParseKeyword(Raw("box-sizing"), "content-box");
        s.BorderCollapse = CssValueParser.ParseKeyword(Raw("border-collapse"), "separate");
        // border-spacing 是两值属性："6px 4px" = 横向 6 纵向 4；只写一个值时纵同横。
        var bs = (Raw("border-spacing") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        s.BorderSpacing = PixelSpacing(bs, 0, s.FontSize);
        s.BorderSpacingV = PixelSpacing(bs, bs.Length > 1 ? 1 : 0, s.FontSize);

        // 字号：支持 %（相对父字号）、em/rem（父字号基准）、px/pt
        s.FontSize = ParseFontSize(Val("font-size"), parent?.FontSize ?? 16f);

        s.FontFamily = FirstNonEmpty(CssValueParser.ParseFirstFontFamily(Raw("font-family")), parent?.FontFamily, "sans-serif")!;
        s.FontWeight = CssValueParser.ParseFontWeight(Raw("font-weight"), parent?.FontWeight ?? 400);
        s.Color = CssValueParser.ParseColor(Raw("color")) ?? parent?.Color ?? SKColors.Black;
        // background 简写用 var() 时（如 body { background: var(--bg) }），AngleSharp 不会把展开后的
        // 色值折进 computed background-color（拿到 transparent/空），导致页面背景不画、卡片像浮在白底上。
        // 故 computed 透明/缺失时，从已 ExpandVars 的 background 声明简写里补取"顶层纯色分量"。
        var bgc = CssValueParser.ParseColor(Raw("background-color"));
        if (bgc == null || bgc.Value.Alpha == 0)
            bgc = ExtractBackgroundColor(Spec("background")) ?? bgc;
        s.BackgroundColor = bgc ?? SKColor.Empty;
        // line-height:normal（含从未声明）不是固定倍数，而是**该字体自己的**正常行距：
        // 浏览器量的就是 ascent+descent+lineGap（雅黑 1.318em、serif 回退 1.4875em），
        // 统一按 1.4 兜底会让每一段正文各差几个百分点，逐段累积把整页下部推下去。
        // em/rem 行高要读**声明值**：AngleSharp 的 computed 已把它们按初值字号 16px 折算过（探针实测
        // `font-size:30px; line-height:1.5em` computed 给 24px 而非 45px），基准根本不是本元素字号。
        var lhText = Val("line-height");
        var lhDeclared = Spec("line-height");
        if (lhDeclared.Contains("em", StringComparison.OrdinalIgnoreCase)) lhText = lhDeclared;
        float lhFactor = CssValueParser.ParseLineHeight(lhText);
        s.LineHeightAbsPx = CssValueParser.ParseLineHeightLength(lhText, s.FontSize);
        s.LineHeightNumeric = !float.IsNaN(lhFactor);
        s.LineHeightFactor = float.IsNaN(lhFactor)
            ? PaperSharp.Layout.FontManager.NormalLineHeight(s.FontFamily, s.FontWeight)
            : lhFactor;
        // direction：computed 拿不到时按 CSS 继承语义手动取父级；RTL 段落里 text-align 的
        // start/end 按书写方向解析（start→右），未显式设置时默认右对齐。
        var dirRaw = Raw("direction");
        s.Direction = dirRaw.Contains("rtl", StringComparison.OrdinalIgnoreCase) ? "rtl"
                    : string.IsNullOrWhiteSpace(dirRaw) ? (parent?.Direction ?? "ltr") : "ltr";
        var taRaw = Raw("text-align");
        s.TextAlignment = taRaw.Trim().ToLowerInvariant() switch
        {
            "center" => CssTextAlign.Center,
            "right" or "end" => CssTextAlign.Right,
            "left" or "start" => s.Direction == "rtl" ? CssTextAlign.Right : CssTextAlign.Left,
            _ => s.Direction == "rtl" ? CssTextAlign.Right : CssTextAlign.Left,
        };

        // margin / padding：按 longhand 读声明值（保留 %），布局期按包含块宽解析。
        // CSS 里 margin/padding 的百分比一律相对包含块的**行内尺寸（宽）**，横竖同基准。
        // 两者都不走继承，声明值即权威：AngleSharp 的 computed/declared 会让内置 UA 的
        // margin-block-start、以及本表给 ul/ol 的 padding-left 穿透作者 `* { margin:0; padding:0 }`
        // 重置（h4 实测返回 1.12em），故先交 AuthorBoxSpacing 按作者源裁决，无声明才退 AngleSharp。
        // 八条边**一次算完**再按槽位取：AuthorBoxSpacing 每问一条边都要重跑一遍规则桶（桶里每条规则
        // 做一次 el.Matches 真匹配），逐条问就是把同样的匹配做八遍。pgbuild 实测这是 AngleSharp 层叠
        // 之外最贵的一项（m1 510 元素 buildDoc 1072ms，其中 computed+declared 两段只占 578ms）。
        string?[]? author8 = flat == null ? ctx.Spacing?.Resolve(element, ctx.ViewportWidth) : null;
        string? Edge(int i) => author8 is { } a ? a[i] : null;
        string? AuthorEdge(string prop) => prop switch
        {
            "margin-top" => Edge(0), "margin-right" => Edge(1), "margin-bottom" => Edge(2), "margin-left" => Edge(3),
            "padding-top" => Edge(4), "padding-right" => Edge(5), "padding-bottom" => Edge(6), "padding-left" => Edge(7),
            _ => null,
        };
        // 宿主元素的作者桶**不能**当伪元素的 padding/margin 用：Resolve 只按宿主匹配，::after 于是继承了宿主的
        // padding（m8 的 .card::after 被 .card 的 padding:12px 撑成 30px 见高，整卡 122.5 vs 浏览器 74.5）、
        // margin（ul.tags li{margin-right:10px} 钻进 li::after，徽标平白宽 10px）。两者本就永不继承。
        // 而 AngleSharp 的 computed **和** declared 两条读取路都会把宿主声明混进伪元素结果里
        // （GetDeclarations(el,"before") 实测原样吐 .card 的 padding），所以生成盒的八条边只能自己裁决：
        // 作者为该伪元素写过就用，没写过就是初值 0。
        bool isPseudo = !string.IsNullOrEmpty(pseudo);
        string?[]? pseudo8 = flat == null && isPseudo ? ctx.Spacing?.ResolvePseudo(element, pseudo!, ctx.ViewportWidth) : null;
        string PseudoEdge(string prop) => pseudo8?[Slot(prop)] is { Length: > 0 } pv ? pv : "0px";
        static int Slot(string prop) => prop switch
        {
            "margin-top" => 0, "margin-right" => 1, "margin-bottom" => 2, "margin-left" => 3,
            "padding-top" => 4, "padding-right" => 5, "padding-bottom" => 6, "padding-left" => 7,
            _ => -1,
        };

        string MarginVal(string prop)
        {
            if (flat != null) return Size0(prop);   // 快照路径：浏览器已算好 used value，缺值即 0，Spec≡Get 无声明可谈
            if (isPseudo) return PseudoEdge(prop);
            if (AuthorEdge(prop) is { Length: > 0 } mv) return mv;
            var sp = Spec(prop);
            return string.IsNullOrWhiteSpace(sp) ? Get(prop) : sp;
        }
        /// <summary>padding 与 margin 同病同治：本表给 ul/ol/dd 写的 UA 默认不得压过作者的 `* { padding:0 }`。</summary>
        string PaddingVal(string prop)
        {
            if (flat != null) return Size0(prop);   // 快照路径：浏览器已折好用值，无声明可谈
            if (isPseudo) return PseudoEdge(prop);
            if (AuthorEdge(prop) is { Length: > 0 } pv) return pv;
            return Size0(prop);
        }
        s.MarginTopCss = CssValueParser.ParseSize(MarginVal("margin-top"), s.FontSize);
        s.MarginRightCss = CssValueParser.ParseSize(MarginVal("margin-right"), s.FontSize);
        s.MarginBottomCss = CssValueParser.ParseSize(MarginVal("margin-bottom"), s.FontSize);
        s.MarginLeftCss = CssValueParser.ParseSize(MarginVal("margin-left"), s.FontSize);
        // 作者是否真的写了 auto：AngleSharp 对未声明的 margin 也返回空串，令 CssSize.IsAuto 无法区分
        // "auto" 与 "没设"。computed 值里 "auto" 才是真自动边距（快照路径浏览器已折成解析后 px，恒非 auto）。
        // 伪元素仍走 computed 会把宿主的 `margin:0 auto` 当成自己的自动边距，只认它自己的裁决结果。
        string AutoSrc(string prop) => isPseudo ? (pseudo8?[Slot(prop)] ?? "") : Get(prop);
        s.MarginTopAuto = IsAutoKw(AutoSrc("margin-top"));
        s.MarginRightAuto = IsAutoKw(AutoSrc("margin-right"));
        s.MarginBottomAuto = IsAutoKw(AutoSrc("margin-bottom"));
        s.MarginLeftAuto = IsAutoKw(AutoSrc("margin-left"));
        s.PaddingTopCss = CssValueParser.ParseSize(PaddingVal("padding-top"), s.FontSize);
        s.PaddingRightCss = CssValueParser.ParseSize(PaddingVal("padding-right"), s.FontSize);
        s.PaddingBottomCss = CssValueParser.ParseSize(PaddingVal("padding-bottom"), s.FontSize);
        s.PaddingLeftCss = CssValueParser.ParseSize(PaddingVal("padding-left"), s.FontSize);

        // border-width：CSS 规范不接受百分比（AngleSharp 会直接丢弃），走 computed px/em，退回声明值。
        s.BorderWidthTop = UsedBorderWidth(Val("border-top-width"), s.FontSize);
        s.BorderWidthRight = UsedBorderWidth(Val("border-right-width"), s.FontSize);
        s.BorderWidthBottom = UsedBorderWidth(Val("border-bottom-width"), s.FontSize);
        s.BorderWidthLeft = UsedBorderWidth(Val("border-left-width"), s.FontSize);
        s.BorderStyleTop = CssValueParser.ParseKeyword(Raw("border-top-style"), "solid");
        s.BorderStyleRight = CssValueParser.ParseKeyword(Raw("border-right-style"), "solid");
        s.BorderStyleBottom = CssValueParser.ParseKeyword(Raw("border-bottom-style"), "solid");
        s.BorderStyleLeft = CssValueParser.ParseKeyword(Raw("border-left-style"), "solid");
        // 边框色逐边取：单边简写（.prefix 的 `border-right: 1px solid #eceef0`）在 AngleSharp 里只落到
        // border-right-color，整圈读 border-color/border-top-color 会读空 → 退回 currentColor，分隔线被染成文字色。
        // 只对**有宽度**的边取值：无框元素（页面上的绝大多数）一次属性读都不多花。
        string? bcShort = null;
        List<string>? bcToks = null;
        SKColor SideBorderColor(string prop, int side, float width)
        {
            if (width <= 0) return s.Color;
            if (CssValueParser.ParseColor(Raw(prop)) is { } c) return c;
            // 长写读不到才是 `border-color: A B C D` 多值简写的场面（AngleSharp 不总展开它）
            if (string.IsNullOrEmpty(bcShort ??= Raw("border-color"))) return s.Color;
            bcToks ??= SplitTopLevel(bcShort);
            return CssValueParser.ParseColor(ColorSlot(bcToks, side)) ?? s.Color;
        }
        s.BorderColorTop = SideBorderColor("border-top-color", 0, s.BorderWidthTop);
        s.BorderColorRight = SideBorderColor("border-right-color", 1, s.BorderWidthRight);
        s.BorderColorBottom = SideBorderColor("border-bottom-color", 2, s.BorderWidthBottom);
        s.BorderColorLeft = SideBorderColor("border-left-color", 3, s.BorderWidthLeft);

        s.Width = CssValueParser.ParseSize(Val("width"), s.FontSize);
        s.Height = CssValueParser.ParseSize(Val("height"), s.FontSize);

        // Flexbox
        s.FlexDirection = CssValueParser.ParseKeyword(Raw("flex-direction"), "row");
        s.FlexWrap = CssValueParser.ParseKeyword(Raw("flex-wrap"), "nowrap");
        // normal 是浏览器对未设 align/justify 的 computed 原话（快照会照抄），按规范折算：
        // align-items:normal ≡ stretch（布局只认 stretch，LayoutBuilder 的 stretchDefault 否则失效），
        // justify-content:normal ≡ flex-start。
        // 对齐属性一律走 Lookup：AngleSharp 丢掉的那批（start/end/justify-self/place-*）由原始 CSS 兜底表补回。
        string Aln(string prop, string dflt) => CssValueParser.ParseKeyword(Lookup(prop, element, Raw(prop), ctx), dflt);
        s.JustifyContent = Aln("justify-content", "flex-start");
        if (s.JustifyContent == "normal") s.JustifyContent = "flex-start";
        s.AlignItems = Aln("align-items", "stretch");
        if (s.AlignItems == "normal") s.AlignItems = "stretch";
        s.AlignSelf = Aln("align-self", "auto");
        // grid 的两轴自对齐 + 多轨分布：longhand 缺省时才由 place-self/place-items 简写补
        //（同条规则里长写恒压简写，与 margin 那套同理，按属性粒度裁决）。
        s.JustifySelf = Aln("justify-self", "auto");
        s.JustifyItems = Aln("justify-items", "stretch");
        s.AlignContent = Aln("align-content", "normal");
        if (CssValueParser.TryParsePositional2(Lookup("place-self", element, Raw("place-self"), ctx), out var psA, out var psJ))
        {
            if (string.IsNullOrWhiteSpace(Raw("align-self"))) s.AlignSelf = psA;
            if (string.IsNullOrWhiteSpace(Raw("justify-self"))) s.JustifySelf = psJ;
        }
        if (CssValueParser.TryParsePositional2(Lookup("place-items", element, Raw("place-items"), ctx), out var piA, out var piJ))
        {
            if (string.IsNullOrWhiteSpace(Raw("align-items"))) s.AlignItems = piA == "auto" ? "stretch" : piA;
            if (string.IsNullOrWhiteSpace(Raw("justify-items"))) s.JustifyItems = piJ == "auto" ? "stretch" : piJ;
        }
        if (s.AlignItems == "normal") s.AlignItems = "stretch";
        if (s.JustifyItems == "normal") s.JustifyItems = "stretch";
        s.FlexGrow = CssValueParser.ParseFlexNumber(Raw("flex-grow"), 0f);
        s.FlexShrink = CssValueParser.ParseFlexNumber(Raw("flex-shrink"), 1f);
        s.FlexBasis = CssValueParser.ParseSize(Val("flex-basis"), s.FontSize);
        // flex 简写：AngleSharp 不把它摊成三个长写，只读上面三行等于整条丢掉 `flex: 1`。
        // 显式写了长写的那一项以长写为准（同条规则里长写覆盖简写），其余由简写补上。
        // ⚠️ 未解：`flex: none` 这条整条失效——TrackContentFloorTests.Flex_item_with_flex_none_is_not_shrunk
        // 用无头 Edge 实测钉住了它（浏览器 [100,186] 不缩，引擎 50.89 = 与「默认可缩」对照组同值），
        // 说明 FlexShrink 停在初始值 1。换 Raw→Spec 做守卫已试过、无变化；下一步要打印这三项的
        // Get()/Spec()/Raw() 取值才能分清是 AngleSharp 拒绝 none 关键字，还是简写串根本没读到。
        // ⚠️ AngleSharp 在**声明阶段**就丢弃 `flex: none`（实测 decls 里没有 flex、computed 三长写全空，
        // 于是 FlexShrink 停在初始值 1，`flex:none` 项照被收缩——Edge 实测 [100,186] 不缩 / 引擎 50.89）。
        // 故简写串走 Lookup：computed/declared 都为空时，从作者 CSS 的自扫描表（UnsupportedProps）兜底取回。
        if (CssValueParser.TryParseFlexShorthand(Lookup("flex", element, Raw("flex"), ctx), s.FontSize, out var fxG, out var fxS, out var fxB))
        {
            if (string.IsNullOrWhiteSpace(Raw("flex-grow"))) s.FlexGrow = fxG;
            if (string.IsNullOrWhiteSpace(Raw("flex-shrink"))) s.FlexShrink = fxS;
            if (string.IsNullOrWhiteSpace(Val("flex-basis"))) s.FlexBasis = fxB;
        }
        s.Gap = CssValueParser.ParseSize(Val("gap"), s.FontSize);

        // Grid：模板串优先取声明值——computed 会把 calc()/repeat() 按 1024 视口提前折算成 px 列表，
        // 丢失轨道表达式；声明值经 ExpandVars 后保留 repeat/minmax/calc 原文，轨道在布局期按容器解析。
        s.GridTemplateColumns = FirstNonEmpty(Spec("grid-template-columns"), Raw("grid-template-columns"));
        s.GridTemplateRows = FirstNonEmpty(Spec("grid-template-rows"), Raw("grid-template-rows"));
        s.ColumnGap = CssValueParser.ParseSize(Val("column-gap"), s.FontSize);
        s.RowGap = CssValueParser.ParseSize(Val("row-gap"), s.FontSize);
        // gap 简写兜底（AngleSharp 对 row-gap 扩展不完整时用）
        var gapRaw = Raw("gap");
        if (!string.IsNullOrWhiteSpace(gapRaw) && gapRaw != "normal")
        {
            var gt = gapRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            s.ColumnGap = CssValueParser.ParseSize(gt[0], s.FontSize);
            s.RowGap = gt.Length > 1 ? CssValueParser.ParseSize(gt[1], s.FontSize) : s.ColumnGap;
        }
        ParseGridPlacement(Raw("grid-column"), out var gcs, out var gce, out var gcspan);
        s.GridColumnStart = gcs; s.GridColumnEnd = gce; s.GridColumnSpan = gcspan;
        ParseGridPlacement(Raw("grid-row"), out var grs, out var gre, out var grspan);
        s.GridRowStart = grs; s.GridRowEnd = gre; s.GridRowSpan = grspan;

        // 尺寸约束（支持 %）
        s.MinWidth = CssValueParser.ParseSize(Val("min-width"), s.FontSize);
        s.MaxWidth = CssValueParser.ParseSize(Val("max-width"), s.FontSize);
        s.MinHeight = CssValueParser.ParseSize(Val("min-height"), s.FontSize);
        s.MaxHeight = CssValueParser.ParseSize(Val("max-height"), s.FontSize);

        // 定位 + 偏移
        s.Position = CssValueParser.ParseKeyword(Raw("position"), "static");
        s.OffsetTop = CssValueParser.ParseSize(Val("top"), s.FontSize);
        s.OffsetRight = CssValueParser.ParseSize(Val("right"), s.FontSize);
        s.OffsetBottom = CssValueParser.ParseSize(Val("bottom"), s.FontSize);
        s.OffsetLeft = CssValueParser.ParseSize(Val("left"), s.FontSize);
        // CSS 规范：position 为 absolute/fixed 时 display 的计算值一并「块级化」。
        // 不补这一步，<span style="position:absolute"> 留在行内路径上既不建盒也不绘制
        // （fluent-docs 滑杆的拇指就是这么整个消失的）。
        if (s.Position is "absolute" or "fixed")
            s.Display = s.Display switch
            {
                "inline" or "inline-block" => "block",
                "inline-flex" => "flex",
                "inline-grid" => "grid",
                "inline-table" => "table",
                _ => s.Display,
            };
        var zIndex = Raw("z-index");
        if (!string.IsNullOrEmpty(zIndex) && zIndex != "auto")
        {
            if (int.TryParse(zIndex.Trim(), out var z))
            {
                s.ZIndex = z;
                s.HasZIndex = true;
            }
        }

        // 浮动 + 清除
        s.Float = CssValueParser.ParseKeyword(Raw("float"), "none");
        s.Clear = CssValueParser.ParseKeyword(Raw("clear"), "none");

        // 溢出裁剪：简写与两个长写里任一轴给出裁剪关键字即整盒裁剪（折算规则见 ComputedStyle.ClipsOverflow）。
        // 走 Lookup 而不是只信 Raw：AngleSharp 若不实现 overflow，computed 为空，需要从原始 CSS 兜底表命中。
        s.ClipsOverflow = AnyClips(Lookup("overflow", element, Raw("overflow"), ctx))
                       || AnyClips(Lookup("overflow-x", element, Raw("overflow-x"), ctx))
                       || AnyClips(Lookup("overflow-y", element, Raw("overflow-y"), ctx));

        // 文本高级：vertical-align / text-wrap / text-fit（非标准，走属性兜底 + HTML 属性）
        // Chrome UA 表给 sub/sup 的 vertical-align。AngleSharp 不认这条属性（全靠 UnsupportedProps 扫作者
        // CSS 兜底），而 ScanUnsupported 又只扫作者 CSS、不扫本引擎注入的 UA 表，所以 UA 里写了也读不到
        // ——实测 m3 的 <sup>/<sub> 因此两条都落在基线上（y 完全相同，与浏览器差 -6.0/+4.5）。
        // 放在级联与作者扫描**之后**，保证作者显式声明仍能压过它。
        s.VerticalAlign = FirstNonEmpty(Lookup("vertical-align", element, Raw("vertical-align"), ctx),
                                        TagDefaultVerticalAlign(element), "baseline")!;
        s.TextWrap = CssValueParser.ParseKeyword(Lookup("text-wrap", element, Raw("text-wrap"), ctx), "wrap");
        var tf = Lookup("text-fit", element, Raw("text-fit"), ctx);
        if (string.IsNullOrWhiteSpace(tf)) tf = element.GetAttribute("text-fit") ?? "";
        s.TextFit = CssValueParser.ParseKeyword(tf, "none");

        // 文本装饰 / 排版细节
        s.TextDecoration = FirstNonEmpty(Raw("text-decoration-line"), FirstToken(Raw("text-decoration")), "none");
        var lsp = Raw("letter-spacing");
        s.LetterSpacing = lsp.Trim() == "normal" ? 0 : CssValueParser.ToPixels(lsp, s.FontSize);
        s.WhiteSpace = CssValueParser.ParseKeyword(Raw("white-space"), "normal");
        s.TextTransform = CssValueParser.ParseKeyword(Raw("text-transform"), "none");
        s.TextOverflow = CssValueParser.ParseKeyword(Lookup("text-overflow", element, Raw("text-overflow"), ctx), "clip");
        s.WordBreak = CssValueParser.ParseKeyword(Raw("word-break"), "normal");

        // text-indent / text-shadow：AngleSharp 没注册这两条属性时 computed 与 declared 都是空串，
        // 只能在读不到之后回头看元素**自己的** style 属性。不进 UnsupportedProps 兜底表：那张表按 tag/class
        // 建键，一条 `p[data-f=textindent]{text-indent:2em}` 会把 2em 漏给页面上所有 <p>。
        // 取声明值而非 computed：AngleSharp 的 computed 按渲染设备字号（16）折 em，
        // 而 text-indent 的 em 该按**本元素字号**折（`.a{font-size:20px;text-indent:2em}` 实测 40px 而非 32px）。
        var tiDecl = SpecOnly("text-indent");
        // text-indent 是继承属性：祖先写的缩进要落到本块首行（% 需要包含块宽，样式期拿不到 → 按 0）
        s.TextIndent = tiDecl.Length == 0 ? parent?.TextIndent ?? 0
                     : tiDecl.Contains('%') ? 0 : CssValueParser.ToPixels(tiDecl, s.FontSize);
        // text-shadow 在 CSS 里是继承属性：本元素没写过就照抄父亲的那几层（含已折算好的 px 偏移）。
        var tsDecl = SpecOnly("text-shadow");   // 同上：em 要按本元素字号折，别用 computed 的 16 折算结果
        s.TextShadow = tsDecl.Length == 0 ? parent?.TextShadow
                     : CssValueParser.ParseShadowLayers(tsDecl, s.FontSize, allowSpread: false);

        // list-style：AngleSharp 的 computed 会把初值 disc 与继承来的值一起吐出来，分不清"作者在本元素写过"，
        // 而 <ol type> 的 presentational hint 只在没写过时才生效 → 判"作者写过"只认声明值/内联属性（SpecOnly）。
        // 没写过时的取值次序：浏览器 computed（支持继承时就是父链的值）→ 本引擎父样式（不支持时）→ 标签默认。
        string declShorthand = SpecOnly("list-style");
        string declaredType = FirstNonEmpty(CssValueParser.ListStyleTypeKeyword(SpecOnly("list-style-type")),
                                            ListShorthandType(declShorthand));
        s.ListStyleSpecified = declaredType;
        // 与父亲的声明原文同值 = 只是继承（AngleSharp 把继承值也当"本元素的声明"返回），此时要用父亲
        // **裁决后**的值——<ol type="a"> 这类 HTML 提示在 Walk 里才落到父样式上，抄原文会把它吃掉。
        bool ownType = declaredType.Length > 0 && declaredType != parent?.ListStyleSpecified;
        if (ownType) s.ListStyleType = declaredType;
        else s.ListStyleType = parent?.ListStyleType ?? "disc";   // 标签默认（ul→disc / ol→decimal）在 Walk 里补
        s.ListStyleAuthorSet = ownType;
        string declaredPos = FirstNonEmpty(SpecOnly("list-style-position"), ListShorthandPosition(declShorthand) ?? "");
        s.ListStyleInside = declaredPos.Length > 0 ? declaredPos.Contains("inside", StringComparison.OrdinalIgnoreCase)
                      : Raw("list-style-position") is { Length: > 0 } pc ? pc.Contains("inside", StringComparison.OrdinalIgnoreCase)
                      : parent?.ListStyleInside ?? false;

        // 分页控制
        s.BreakInside = CssValueParser.ParseKeyword(Raw("break-inside"), "auto");
        s.BreakBefore = CssValueParser.ParseKeyword(Raw("break-before"), "auto");
        s.BreakAfter = CssValueParser.ParseKeyword(Raw("break-after"), "auto");
        s.Widows = ParseInt(Raw("widows"), 2);
        s.Orphans = ParseInt(Raw("orphans"), 2);
        s.CaptionSide = CssValueParser.ParseKeyword(Raw("caption-side"), "top");

        // 视觉
        s.Opacity = CssValueParser.ParseOpacity(Raw("opacity"));
        // border-radius 每角两轴（绝对 + 百分比）。百分比留到绘制期按边框盒宽高折算，此刻还没有盒尺寸。
        // AngleSharp 的 computed 会把百分比按**视口**尺寸折成定值 px（实测 120px 宽的盒上 50% 出 397px），
        // 故一律以作者原样串为准、computed 兜底；长写法与简写都读，后读到简写时整体覆盖。
        var radAbs = new float[8];
        var radPct = new float[8];
        string[] radiusProps =
        {
            "border-top-left-radius", "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius",
        };
        for (int c = 0; c < 4; c++)
        {
            var authored = FirstNonEmpty(Inline(radiusProps[c]), Lookup(radiusProps[c], element, "", ctx));
            var (ax, ay, px, py) = ReadRadius(authored.Length > 0 ? authored : Raw(radiusProps[c]), s.FontSize);
            radAbs[c * 2] = ax; radAbs[c * 2 + 1] = ay;
            radPct[c * 2] = px; radPct[c * 2 + 1] = py;
        }
        var radiusShorthand = FirstNonEmpty(Inline("border-radius"), Lookup("border-radius", element, "", ctx));
        if (ParseRadii(radiusShorthand, s.FontSize) is { } ra)
        {
            Array.Copy(ra.abs, radAbs, 8);
            Array.Copy(ra.pct, radPct, 8);
        }
        s.BorderRadiusTopLeft = radAbs[0]; s.BorderRadiusTopRight = radAbs[2];
        s.BorderRadiusBottomRight = radAbs[4]; s.BorderRadiusBottomLeft = radAbs[6];
        s.BorderRadiusTopLeftY = radAbs[1]; s.BorderRadiusTopRightY = radAbs[3];
        s.BorderRadiusBottomRightY = radAbs[5]; s.BorderRadiusBottomLeftY = radAbs[7];
        if (radPct.Any(p => p > 0)) s.BorderRadiusPercent = radPct;
        // AngleSharp 会把 `0 0 0 2px` 重序列化成 `0 0 2px`（blur 与 spread 串位，实测三重阴影那格的
        // 2px 外环被当成 2px 模糊），声明值视图同样中招。故一律取作者原样串：内联 style 属性 >
        // 原始 CSS 兜底表 > 声明值。em 也按本元素字号折算。
        var shadowDecl = FirstNonEmpty(Inline("box-shadow"), Lookup("box-shadow", element, "", ctx), Spec("box-shadow"));
        s.Shadows = CssValueParser.ParseShadowLayers(shadowDecl, s.FontSize);
        // background-image/background 一律不读 computed：值里只要含 repeating-* 渐变，
        // AngleSharp 的 CssPeriodicValue.CssText 必抛 NRE（其解析也不完整，渐变串本就走 Spec/Lookup 自解析）。
        string bgi = Spec("background-image");
        if (string.IsNullOrEmpty(bgi)) bgi = Spec("background");
        // conic-gradient 等 AngleSharp 不认识的写法：从原始 CSS 兜底
        s.Background = CssValueParser.ParseGradient(
            Lookup("background-image", element, Lookup("background", element, bgi, ctx), ctx));
        // background-clip: text（渐变文字）——AngleSharp 会把 computed 判成 "border-box"（吞掉作者的 text），
        // 故只信原始 CSS 兜底表（传空 current 强制走 UnsupportedMap 的 class/id/tag 命中）。
        s.BackgroundClip = FirstNonEmpty(FirstToken(Lookup("background-clip", element, "", ctx)), "border-box");

        // 进阶视觉：blend / clip / mask / filter / squircle（AngleSharp 多不支持，走原始 CSS 兜底）
        s.MixBlendMode = CssValueParser.ParseKeyword(Lookup("mix-blend-mode", element, Raw("mix-blend-mode"), ctx), "normal");
        s.CornerShape = CssValueParser.ParseKeyword(Lookup("corner-shape", element, Raw("corner-shape"), ctx), "round");
        var cp = Lookup("clip-path", element, Raw("clip-path"), ctx);
        s.ClipPath = string.IsNullOrWhiteSpace(cp) || cp == "none" ? null : cp;
        s.MaskImage = CssValueParser.ParseGradient(Lookup("mask-image", element, Raw("mask-image"), ctx));
        s.Filter = CssValueParser.ParseFilter(Lookup("filter", element, Raw("filter"), ctx));
        // backdrop-filter：AngleSharp 不认，走原始 CSS 兜底（传空 current 强制查表）
        s.BackdropFilter = CssValueParser.ParseFilter(Lookup("backdrop-filter", element, "", ctx));

        // CSS 计数器声明：AngleSharp 常把未实现的 counter-* computed 成 "none"，令 Lookup 兜底失效；
        // 故把 none/空视为「无声明」，强制走原始 CSS 兜底（按 class>id>tag 命中）。
        s.CounterReset = Lookup("counter-reset", element, CleanCounter(Raw("counter-reset")), ctx) ?? "";
        s.CounterIncrement = Lookup("counter-increment", element, CleanCounter(Raw("counter-increment")), ctx) ?? "";

        // animation 简写（AngleSharp 不实现，走原始 CSS 兜底表）：解析进样式字段，渲染传 animationTime 时按时刻取帧
        Animation.ParseShorthand(Lookup("animation", element, "", ctx) ?? "", s);

        return s;
    }

    /// <summary>
    /// 每元素的属性读记忆化视图：同一个声明集合上每条属性**至多问 AngleSharp 一次**，
    /// 结果（含 ExpandVars 后的串、空串、以及「读取抛异常」= null）都进字典。
    /// 动机（pgbuild §5.7 实测）：ReadStyle 的包装函数彼此不缓存——<c>Val</c> 恒先 Spec 再 Get
    /// （2 次读）、<c>Raw</c>/Lookup 在 computed 为空时还会再 Spec 一遍，每元素实际发生 ~130 次
    /// <c>GetPropertyValue</c>（去重后唯一属性只有 ~94 个），是 buildDoc 里最大的单一开销。
    /// 缓存安全的前提与 EagerStyleCollection 相同：渲染期间文档静态，同一声明的同一属性取值恒定。
    /// 单条读失败退回空串（下游都有 fallback），不让整个渲染崩。
    /// </summary>
    sealed class PropCache
    {
        readonly ICssStyleDeclaration? _decl;
        readonly IElement _element;
        readonly DocContext _ctx;
        Dictionary<string, string?>? _map;   // null 值 = 该属性读取抛过（缓存后不再重试、不再抛）

        public PropCache(ICssStyleDeclaration? decl, IElement element, DocContext ctx)
        {
            _decl = decl;
            _element = element;
            _ctx = ctx;
        }

        public string Read(string prop)
        {
            var map = _map ??= new Dictionary<string, string?>(StringComparer.Ordinal);
            if (map.TryGetValue(prop, out var cached)) return cached ?? "";
            string? v;
            try
            {
                var raw = _decl?.GetPropertyValue(prop);
                v = string.IsNullOrEmpty(raw) ? "" : ExpandVars(raw!, _element, _ctx);
            }
            catch { v = null; }
            map[prop] = v;
            return v ?? "";
        }
    }

    // 从 background 简写里取顶层纯色分量：逐个 depth-0 token 试 ParseColor，
    // 跳过 linear-gradient(...)/url(...)/no-repeat 等非色值 token。
    static SKColor? ExtractBackgroundColor(string decl)
    {
        foreach (var tok in TopLevelTokens(decl))
        {
            var c = CssValueParser.ParseColor(tok);
            if (c != null && c.Value.Alpha > 0) return c;
        }
        return null;
    }

    // 按 depth-0 空格切分：括号内（含 rgb()/rgba() 的逗号、渐变端点）不拆，保证函数值留成单个 token。
    static IEnumerable<string> TopLevelTokens(string v)
    {
        if (string.IsNullOrEmpty(v)) yield break;
        var cur = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < v.Length; i++)
        {
            char ch = v[i];
            if (ch == '(') { depth++; cur.Append(ch); }
            else if (ch == ')') { if (depth > 0) depth--; cur.Append(ch); }
            else if (ch == ' ' && depth == 0)
            {
                if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); }
            }
            else cur.Append(ch);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }

    static bool IsAutoKw(string v) => v.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
    static string FirstToken(string v) => (v ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    static float PixelSpacing(string[] toks, int i, float fontSize)
    {
        var px = i < toks.Length ? CssValueParser.ToPixels(toks[i], fontSize) : float.NaN;
        return float.IsNaN(px) || px < 0 ? 0 : px;
    }
    static string CleanCounter(string? v) { v = (v ?? "").Trim(); return v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : v; }
    /// <summary>
    /// 边框宽的**用值**：Chrome 在有边框时把计算值向下取整，且不足 1px 仍按 1px 画。
    /// 无头 Edge 实测（九档边框宽对拍）：0.4/0.5/0.6px→1，1.5→1，1.99→1，2.5→2，2.51→2，3.5→3，4.9→4，
    /// 上下两条 4.9px 各折 4（合计 8 而非 9.8 取整）。不折的话整页非整数边框（calc(2px + 1pt) 等）
    /// 每处多 0.1~0.9px，m4 的 h1 边框实测差 3px。
    /// </summary>
    static float UsedBorderWidth(string v, float fontSize)
    {
        var px = CssValueParser.ToPixels(v, fontSize);
        return px <= 0 ? 0 : Math.Max(1, MathF.Floor(px));
    }

    /// <summary>border-radius **长写法**（单角）：`x轴 [y轴]`，两轴各为长度或百分比（缺省 y 同 x）。</summary>
    static (float ax, float ay, float px, float py) ReadRadius(string v, float fontSize)
    {
        var toks = RadiusTokens(v);
        if (toks.Length == 0) return (0, 0, 0, 0);
        var (ax, px) = RadiusAxis(toks[0], fontSize);
        if (toks.Length < 2) return (ax, ax, px, px);
        var (ay, py) = RadiusAxis(toks[1], fontSize);
        return (ax, ay, px, py);
    }

    /// <summary>border-radius **简写**：`x列表 [/ y列表]`，每个列表 1~4 值按角落规则展开。
    /// 返回八元组，顺序 TLx,TLy,TRx,TRy,BRx,BRy,BLx,BLy（百分比为小数）；串为空或非法则 null。</summary>
    static (float[] abs, float[] pct)? ParseRadii(string decl, float fontSize)
    {
        foreach (var kw in new[] { "inherit", "initial", "unset", "revert" })
            if (decl.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return null;      // 关键字值展不开，交回 computed 视图
        var slash = decl.IndexOf('/');
        var xt = RadiusTokens(slash < 0 ? decl : decl[..slash]);
        var yt = slash < 0 ? xt : RadiusTokens(decl[(slash + 1)..]);
        if (xt.Length == 0 || xt.Length > 4 || yt.Length == 0 || yt.Length > 4) return null;
        var abs = new float[8];
        var pct = new float[8];
        for (int corner = 0; corner < 4; corner++)
        {
            // 角落展开：1→四角同值；2→TL/BR 与 TR/BL 交替；3→TL/TR/BR 顺次、BL 同 TR；4→顺次
            var (ax, px) = RadiusAxis(xt[Src(corner, xt.Length)], fontSize);
            var (ay, py) = RadiusAxis(yt[Src(corner, yt.Length)], fontSize);
            abs[corner * 2] = ax; pct[corner * 2] = px;
            abs[corner * 2 + 1] = ay; pct[corner * 2 + 1] = py;
        }
        return (abs, pct);

        static int Src(int corner, int len) =>
            len switch { 1 => 0, 2 => corner % 2, 3 => corner == 3 ? 1 : corner, _ => corner };
    }

    static (float abs, float pct) RadiusAxis(string tok, float fontSize) =>
        tok.EndsWith("%", StringComparison.Ordinal)
            ? (0, Math.Max(0, CssValueParser.ToPixels(tok[..^1], fontSize)) / 100f)
            : (Math.Max(0, CssValueParser.ToPixels(tok, fontSize)), 0);

    static string[] RadiusTokens(string v) =>
        v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>字号：支持 %（相对父字号）、em/rem（父字号基准）、px/pt；无效则继承父字号。</summary>
    static float ParseFontSize(string v, float parentFontSize)
    {
        v = v?.Trim() ?? "";
        if (v.Length == 0) return parentFontSize;
        if (v.EndsWith("%"))
        {
            var p = CssValueParser.ToPixels(v, parentFontSize); // 走不到（% 返回 -1），单独解析
            if (float.TryParse(v[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                return parentFontSize * pct / 100f;
            return parentFontSize;
        }
        var px = CssValueParser.ToPixels(v, parentFontSize);
        return px > 0 ? px : parentFontSize;
    }

    static string FirstNonEmpty(params string?[] vs)
    {
        foreach (var v in vs) if (!string.IsNullOrWhiteSpace(v)) return v!;
        return "";
    }

    static int ParseInt(string v, int fallback)
    {
        v = (v ?? "").Trim();
        if (int.TryParse(v, out var n) && n >= 1) return n;
        return fallback;
    }

    /// <summary>解析 grid-column / grid-row：auto | N | span N | "A / B"（线号或 span 混合）。</summary>
    static void ParseGridPlacement(string? val, out int start, out int end, out int span)
    {
        start = 0; end = 0; span = 1;
        if (string.IsNullOrWhiteSpace(val)) return;
        var parts = val.Split('/', StringSplitOptions.TrimEntries);
        var (s0, sp0) = ParseGridSide(parts[0]);
        start = s0; span = sp0;
        if (parts.Length > 1)
        {
            var (s1, sp1) = ParseGridSide(parts[1]);
            end = s1;
            if (s1 > 0 && start > 0) span = Math.Max(1, s1 - start);
            else if (sp1 > 1 && start == 0) span = sp1;
        }
    }

    static (int line, int span) ParseGridSide(string tok)
    {
        tok = tok.Trim().ToLowerInvariant();
        if (tok.Length == 0 || tok == "auto") return (0, 1);
        if (tok.StartsWith("span"))
        {
            var rest = tok[4..].Trim();
            return int.TryParse(rest, out var n) && n >= 1 ? (0, n) : (0, 1);
        }
        return int.TryParse(tok, out var l) && l >= 1 ? (l, 1) : (0, 1);
    }
}
