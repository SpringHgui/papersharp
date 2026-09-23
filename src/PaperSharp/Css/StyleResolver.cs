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
    const string UaStylesheet = """
        html, body { margin: 0; padding: 0; }
        body { font-family: sans-serif; font-size: 16px; color: #000; line-height: 1.4; }
        h1 { font-size: 32px; font-weight: bold; margin: 21px 0; }
        h2 { font-size: 24px; font-weight: bold; margin: 20px 0; }
        h3 { font-size: 19px; font-weight: bold; margin: 19px 0; }
        p { margin: 16px 0; }
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
        """;

    // AngleSharp.Css 不实现的属性（computed/declared 都拿不到），从原始 CSS 里按选择器兜底扫出来。
    static readonly string[] UnsupportedProps = { "text-overflow", "text-wrap", "vertical-align", "text-fit", "background-image", "background", "mix-blend-mode", "clip-path", "corner-shape", "filter", "mask-image", "background-clip", "backdrop-filter", "counter-reset", "counter-increment", "animation" };

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
    }

    public static ComputedStyle BuildDocument(string htmlBody, string userCss, double? animationTime = null)
    {
        var ctx = new DocContext();
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
        // 伪元素开销较大（每元素多两次 computed 层叠），仅当 CSS 里确有 ::before/::after 规则时才启用
        ctx.HasPseudoRules = Regex.IsMatch(userCss ?? "", @"::?(before|after)\b", RegexOptions.IgnoreCase);
        if (ctx.HasPseudoRules) ScanPseudoDisplay(userCss ?? "", ctx);
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
        var renderDevice = new DefaultRenderDevice { FontSize = 16, ViewPortWidth = 1024, ViewPortHeight = 1024 };
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
                var mm = Regex.Match(body, prop + @"\s*:\s*([^;}]+)", RegexOptions.IgnoreCase);
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

    static ComputedStyle Walk(IWindow window, IElement element, ComputedStyle? parent, DocContext ctx)
    {
        var cs = ReadStyle(window, element, parent, ctx);
        cs.Tag = element.TagName.ToLowerInvariant();

        // 替换元素：读取 <img> 的 src
        if (cs.Tag == "img")
            cs.ImageSrc = element.GetAttribute("src");

        // <a> 的 href：供 PDF 链接注释
        if (cs.Tag == "a")
            cs.LinkHref = element.GetAttribute("href");

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
                if (NonRenderingTags.Contains(ce.TagName)) continue;
                if (ce.TagName.Equals("BR", StringComparison.OrdinalIgnoreCase))
                {
                    // <br> 只是换行标记，不成盒：进 InlinePieces，但不进 Children（否则会被当成块级子元素破坏 inline 判定）
                    cs.InlinePieces.Add(InlinePiece.Break);
                    continue;
                }
                var childStyle = Walk(window, ce, cs, ctx);
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

        // 伪元素 ::before/::after：仅当 CSS 里确有伪元素规则时才建生成盒（content 片段 + 自身样式），
        // 实际注入与 counter 回填在 BuildDocument 末尾的 PseudoContent.Apply 文档序 pass 里做。
        if (ctx.HasPseudoRules)
        {
            cs.Before = BuildPseudoGen(window, element, cs, ctx, "::before");
            cs.After = BuildPseudoGen(window, element, cs, ctx, "::after");
        }

        return cs;
    }

    /// <summary>为一个伪元素建生成盒：读 content（不成盒则返回 null）+ 伪元素自身 computed 样式（display 归一为 inline 缺省）。</summary>
    static PseudoGen? BuildPseudoGen(IWindow window, IElement element, ComputedStyle owner, DocContext ctx, string pseudo)
    {
        string Content()
        {
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

        var style = ReadStyle(window, element, owner, ctx, pseudo);
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
                var stripped = Regex.Replace(selPart, @"::?(before|after)", " ", RegexOptions.IgnoreCase);
                var toks = Regex.Split(stripped.Trim(), @"[\s>+~]+").Where(x => x.Length > 0).ToArray();
                var last = toks.Length > 0 ? toks[^1] : "";
                last = Regex.Replace(last, @"[:\[].*$", "");   // 去伪类/属性段
                if (last.Length == 0) continue;
                if (dm.Success) ctx.PseudoDisplay[last + suffix] = dm.Groups[1].Value.Trim().ToLowerInvariant();
            }
        }
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

    static string ApplyTransform(string text, string mode)
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

    static ComputedStyle ReadStyle(IWindow window, IElement element, ComputedStyle? parent, DocContext ctx, string? pseudo = null)
    {
        var s = new ComputedStyle { Parent = parent };

        // AngleSharp.Css 的 GetComputedStyle 是 IWindow 上的扩展方法，会做完 cascade + 继承。
        // 但它对某些值（如 radial-gradient）在 Compute() 里会抛 NullReferenceException——
        // 一旦抛异常该元素整个 computed 都拿不到。这里 try/catch 兜住：拿不到就退回声明值
        // （GetDeclarations，不触发 Compute）+ 父级继承，绝不让渲染崩。
        // pseudo 非空时读伪元素（::before/::after）的 computed，实测 AngleSharp 支持并返回 content。
        ICssStyleDeclaration? computed = null;
        try { computed = window.GetComputedStyle(element, pseudo); }
        catch { /* 该元素 computed 不可用，下面全部走 Spec 兜底 */ }
        string Get(string prop)
        {
            // 单个属性读失败退回空串（下游都有 fallback），不让整个渲染崩
            try
            {
                var v = computed?.GetPropertyValue(prop);
                return string.IsNullOrEmpty(v) ? "" : ExpandVars(v, element, ctx);
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
        ICssStyleDeclaration? decls = null;
        try { decls = window.GetStyleCollection().GetDeclarations(element, pseudo); }
        catch { /* 拿不到声明值时退回 computed，不让渲染崩 */ }
        string Spec(string prop)
        {
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

        s.Display = FirstNonEmpty(Raw("display"), "block"); // display 不继承
        s.BoxSizing = CssValueParser.ParseKeyword(Raw("box-sizing"), "content-box");
        s.BorderCollapse = CssValueParser.ParseKeyword(Raw("border-collapse"), "separate");
        s.BorderSpacing = Math.Max(0, CssValueParser.ToPixels(FirstToken(Raw("border-spacing")), s.FontSize));

        // 字号：支持 %（相对父字号）、em/rem（父字号基准）、px/pt
        s.FontSize = ParseFontSize(Val("font-size"), parent?.FontSize ?? 16f);

        s.FontFamily = FirstNonEmpty(CssValueParser.ParseFirstFontFamily(Raw("font-family")), parent?.FontFamily, "sans-serif")!;
        s.FontWeight = CssValueParser.ParseFontWeight(Raw("font-weight"), parent?.FontWeight ?? 400);
        s.Color = CssValueParser.ParseColor(Raw("color")) ?? parent?.Color ?? SKColors.Black;
        s.BackgroundColor = CssValueParser.ParseColor(Raw("background-color")) ?? SKColor.Empty;
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
        s.MarginTopCss = CssValueParser.ParseSize(Val("margin-top"), s.FontSize);
        s.MarginRightCss = CssValueParser.ParseSize(Val("margin-right"), s.FontSize);
        s.MarginBottomCss = CssValueParser.ParseSize(Val("margin-bottom"), s.FontSize);
        s.MarginLeftCss = CssValueParser.ParseSize(Val("margin-left"), s.FontSize);
        s.PaddingTopCss = CssValueParser.ParseSize(Val("padding-top"), s.FontSize);
        s.PaddingRightCss = CssValueParser.ParseSize(Val("padding-right"), s.FontSize);
        s.PaddingBottomCss = CssValueParser.ParseSize(Val("padding-bottom"), s.FontSize);
        s.PaddingLeftCss = CssValueParser.ParseSize(Val("padding-left"), s.FontSize);

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
        s.JustifyContent = CssValueParser.ParseKeyword(Raw("justify-content"), "flex-start");
        s.AlignItems = CssValueParser.ParseKeyword(Raw("align-items"), "stretch");
        s.AlignSelf = CssValueParser.ParseKeyword(Raw("align-self"), "auto");
        s.FlexGrow = CssValueParser.ParseFlexNumber(Raw("flex-grow"), 0f);
        s.FlexShrink = CssValueParser.ParseFlexNumber(Raw("flex-shrink"), 1f);
        s.FlexBasis = CssValueParser.ParseSize(Val("flex-basis"), s.FontSize);
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
