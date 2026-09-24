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
    // 一份最小 UA 样式表，覆盖 demo 用到的元素默认值。
    // 不加这个的话 <p>、<h1> 全部 margin=0 会糊在一起。
    // 公开：Playground 这类「引擎 vs 浏览器」比对工具要把同一份表注进预览 iframe，
    // 否则两边 UA 默认值（字体/行高/标题字号）就不是一套，每个文本元素都白报一条高度差。
    public const string UaStylesheet = """
        html, body { margin: 0; padding: 0; }
        body { font-family: sans-serif; font-size: 16px; color: #000; line-height: 1.4; }
        h1 { font-size: 32px; font-weight: bold; margin: 21px 0; }
        h2 { font-size: 24px; font-weight: bold; margin: 20px 0; }
        h3 { font-size: 19px; font-weight: bold; margin: 19px 0; }
        p { margin: 16px 0; }
        /* 列表 UA 默认（实测 headless Edge：li 在 x=40、宽比 ul 少 40；本表缺这两行时引擎把 li 摆到 x=0 满宽，
           是肉眼可见的错位而非比对口径问题）。嵌套列表的块边距归零也是浏览器行为。 */
        ul, ol { margin: 1em 0; padding-left: 40px; }
        ul ul, ul ol, ol ul, ol ol { margin-top: 0; margin-bottom: 0; }
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
    static readonly string[] UnsupportedProps = { "text-overflow", "text-wrap", "vertical-align", "text-fit", "background-image", "background", "mix-blend-mode", "clip-path", "corner-shape", "filter", "mask-image", "background-clip", "backdrop-filter", "counter-reset", "counter-increment", "animation", "overflow", "overflow-x", "overflow-y",
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
        if (ctx.HasPseudoRules) { ctx.PseudoScope = new(StringComparer.OrdinalIgnoreCase); ScanPseudoDisplay(userCss ?? "", ctx); }
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

        var root = Walk(window, body, null, ctx);
        if (ctx.HasPseudoRules) PseudoContent.Apply(root);   // 文档序求值 counter + 注入伪元素匿名盒
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

    static ComputedStyle Walk(IWindow window, IElement element, ComputedStyle? parent, DocContext ctx,
                              FlatCascade.Scope? fparent = null, int nthOfType = 1)
    {
        var fs = ctx.Flat?.For(element, fparent);
        // 索引覆盖不了这个元素（如两个类规则同时命中）：整份退回 AngleSharp 继续，不在半路混用两套继承链。
        if (fs == null && ctx.Flat != null) ctx.Flat = null;
        var cs = ReadStyle(window, element, parent, ctx, flat: fs);
        cs.Tag = element.TagName.ToLowerInvariant();

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
                cs.Color = new SKColor(0x9a, 0xa0, 0xa8);  // 占位符弱化灰（浏览器 ::placeholder 观感）
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
                var childStyle = Walk(window, ce, cs, ctx, fs, seq);
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
        bool blockish = cs.Display == "block" || cs.Display == "table-cell";
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
            cs.Before = BuildPseudoGen(window, element, cs, ctx, "::before", fs);
            cs.After = BuildPseudoGen(window, element, cs, ctx, "::after", fs);
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
        anon.BackgroundColor = SKColor.Empty;
        anon.Background = null;
        anon.Shadow = null;
        return anon;
    }

    /// <summary>为一个伪元素建生成盒：读 content（不成盒则返回 null）+ 伪元素自身 computed 样式（display 归一为 inline 缺省）。</summary>
    static PseudoGen? BuildPseudoGen(IWindow window, IElement element, ComputedStyle owner, DocContext ctx,
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
                var c = window.GetComputedStyle(element, pseudo).GetPropertyValue("content");
                return ExpandVars(c ?? "", element, ctx);
            }
            catch { return ""; }
        }
        var runs = PseudoContent.Parse(Content(), name => element.GetAttribute(name));
        if (runs == null) return null;   // none / normal / 未设 → 不生成盒

        var style = ReadStyle(window, element, owner, ctx, pseudo, fp);
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
            string suffix;
            if (Regex.IsMatch(sel, @"::?before\b", RegexOptions.IgnoreCase)) suffix = "::before";
            else if (Regex.IsMatch(sel, @"::?after\b", RegexOptions.IgnoreCase)) suffix = "::after";
            else continue;
            var body = m.Groups[2].Value;
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
        var suffix = pseudo.Contains("before", StringComparison.OrdinalIgnoreCase) ? "::before" : "::after";
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

    static ComputedStyle ReadStyle(IWindow window, IElement element, ComputedStyle? parent, DocContext ctx,
                                   string? pseudo = null, FlatCascade.Scope? flat = null)
    {
        var s = new ComputedStyle { Parent = parent };

        // AngleSharp.Css 的 GetComputedStyle 是 IWindow 上的扩展方法，会做完 cascade + 继承。
        // 但它对某些值（如 radial-gradient）在 Compute() 里会抛 NullReferenceException——
        // 一旦抛异常该元素整个 computed 都拿不到。这里 try/catch 兜住：拿不到就退回声明值
        // （GetDeclarations，不触发 Compute）+ 父级继承，绝不让渲染崩。
        // pseudo 非空时读伪元素（::before/::after）的 computed，实测 AngleSharp 支持并返回 content。
        // flat 非空时（展平快照）两个调用都不做：那轮层叠在这类输入上实测 1.82 ms/元素，全是白烧。
        ICssStyleDeclaration? computed = null;
        if (flat == null)
        {
            try { computed = window.GetComputedStyle(element, pseudo); }
            catch { /* 该元素 computed 不可用，下面全部走 Spec 兜底 */ }
        }
        string Get(string prop)
        {
            if (flat != null)
            {
                var v = flat.Get(prop);
                return v.Length == 0 ? "" : ExpandVars(v, element, ctx);
            }
            // 单个属性读失败退回空串（下游都有 fallback），不让整个渲染崩
            try
            {
                var cv = computed?.GetPropertyValue(prop);
                return string.IsNullOrEmpty(cv) ? "" : ExpandVars(cv, element, ctx);
            }
            catch
            {
                return "";
            }
        }

        // 声明值（specified）：AngleSharp 的 GetComputedStyle 会把 % 一律按渲染设备的
        // 视口尺寸（1024）折算成 px，从而丢失百分比信息（如 height:100% → "1024px"）。
        // GetDeclarations 返回的是做完 cascade 排序、但未经设备折算的原始声明值，
        // 其中的百分比被原样保留。需要按包含块解析的盒子尺寸属性用它兜底。
        // 展平快照里两者本就是一个东西（浏览器已经把 %/calc 折成 px），故 Spec 直接复用 Get。
        ICssStyleDeclaration? decls = null;
        if (flat == null && !ctx.Precomputed)
        {
            try { decls = window.GetStyleCollection().GetDeclarations(element, pseudo); }
            catch { /* 拿不到声明值时退回 computed，不让渲染崩 */ }
        }
        string Spec(string prop)
        {
            if (flat != null) return Get(prop);
            try { return ExpandVars(decls?.GetPropertyValue(prop) ?? "", element, ctx); }
            catch { return ""; }
        }
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
        s.BorderSpacing = Math.Max(0, CssValueParser.ToPixels(FirstToken(Raw("border-spacing")), s.FontSize));

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
        s.LineHeightFactor = CssValueParser.ParseLineHeight(Val("line-height"));
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
        string MarginVal(string prop)
        {
            if (flat != null) return Size0(prop);   // 快照路径：浏览器已算好 used value，缺值即 0，Spec≡Get 无声明可谈
            if (ctx.Spacing != null && ctx.Spacing.TryGet(element, ctx.ViewportWidth, prop, out var mv)) return mv;
            var sp = Spec(prop);
            return string.IsNullOrWhiteSpace(sp) ? Get(prop) : sp;
        }
        /// <summary>padding 与 margin 同病同治：本表给 ul/ol/dd 写的 UA 默认不得压过作者的 `* { padding:0 }`。</summary>
        string PaddingVal(string prop)
        {
            if (flat != null) return Size0(prop);   // 快照路径：浏览器已折好用值，无声明可谈
            if (ctx.Spacing != null && ctx.Spacing.TryGet(element, ctx.ViewportWidth, prop, out var pv)) return pv;
            return Size0(prop);
        }
        s.MarginTopCss = CssValueParser.ParseSize(MarginVal("margin-top"), s.FontSize);
        s.MarginRightCss = CssValueParser.ParseSize(MarginVal("margin-right"), s.FontSize);
        s.MarginBottomCss = CssValueParser.ParseSize(MarginVal("margin-bottom"), s.FontSize);
        s.MarginLeftCss = CssValueParser.ParseSize(MarginVal("margin-left"), s.FontSize);
        // 作者是否真的写了 auto：AngleSharp 对未声明的 margin 也返回空串，令 CssSize.IsAuto 无法区分
        // "auto" 与 "没设"。computed 值里 "auto" 才是真自动边距（快照路径浏览器已折成解析后 px，恒非 auto）。
        s.MarginTopAuto = IsAutoKw(Get("margin-top"));
        s.MarginRightAuto = IsAutoKw(Get("margin-right"));
        s.MarginBottomAuto = IsAutoKw(Get("margin-bottom"));
        s.MarginLeftAuto = IsAutoKw(Get("margin-left"));
        s.PaddingTopCss = CssValueParser.ParseSize(PaddingVal("padding-top"), s.FontSize);
        s.PaddingRightCss = CssValueParser.ParseSize(PaddingVal("padding-right"), s.FontSize);
        s.PaddingBottomCss = CssValueParser.ParseSize(PaddingVal("padding-bottom"), s.FontSize);
        s.PaddingLeftCss = CssValueParser.ParseSize(PaddingVal("padding-left"), s.FontSize);

        // border-width：CSS 规范不接受百分比（AngleSharp 会直接丢弃），走 computed px/em，退回声明值。
        s.BorderWidthTop = Math.Max(0, CssValueParser.ToPixels(Val("border-top-width"), s.FontSize));
        s.BorderWidthRight = Math.Max(0, CssValueParser.ToPixels(Val("border-right-width"), s.FontSize));
        s.BorderWidthBottom = Math.Max(0, CssValueParser.ToPixels(Val("border-bottom-width"), s.FontSize));
        s.BorderWidthLeft = Math.Max(0, CssValueParser.ToPixels(Val("border-left-width"), s.FontSize));
        s.BorderColor = CssValueParser.ParseColor(Raw("border-color"))
                     ?? CssValueParser.ParseColor(Raw("border-top-color"))
                     ?? s.Color;

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
        if (CssValueParser.TryParseFlexShorthand(Raw("flex"), s.FontSize, out var fxG, out var fxS, out var fxB))
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
        s.VerticalAlign = FirstNonEmpty(Lookup("vertical-align", element, Raw("vertical-align"), ctx), "baseline")!;
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

        // 分页控制
        s.BreakInside = CssValueParser.ParseKeyword(Raw("break-inside"), "auto");
        s.BreakBefore = CssValueParser.ParseKeyword(Raw("break-before"), "auto");
        s.BreakAfter = CssValueParser.ParseKeyword(Raw("break-after"), "auto");
        s.Widows = ParseInt(Raw("widows"), 2);
        s.Orphans = ParseInt(Raw("orphans"), 2);
        s.CaptionSide = CssValueParser.ParseKeyword(Raw("caption-side"), "top");

        // 视觉
        s.Opacity = CssValueParser.ParseOpacity(Raw("opacity"));
        s.BorderRadiusTopLeft = FirstRadius(Raw("border-top-left-radius"), s.FontSize);
        s.BorderRadiusTopRight = FirstRadius(Raw("border-top-right-radius"), s.FontSize);
        s.BorderRadiusBottomRight = FirstRadius(Raw("border-bottom-right-radius"), s.FontSize);
        s.BorderRadiusBottomLeft = FirstRadius(Raw("border-bottom-left-radius"), s.FontSize);
        s.Shadow = CssValueParser.ParseBoxShadow(Raw("box-shadow"), s.FontSize);
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
    static string CleanCounter(string? v) { v = (v ?? "").Trim(); return v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : v; }
    static float FirstRadius(string v, float fontSize)
    {
        var tok = FirstToken(v);
        var px = CssValueParser.ToPixels(tok, fontSize);
        return px > 0 ? px : 0;
    }

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
