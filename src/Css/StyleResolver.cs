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
    static readonly string[] UnsupportedProps = { "text-overflow" };
    static Dictionary<string, Dictionary<string, string>> _unsupportedMap = new();

    public static ComputedStyle BuildDocument(string htmlBody, string userCss)
    {
        ScanUnsupported(userCss);
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

        return Walk(window, body, null);
    }

    /// <summary>扫原始 CSS：把不支持属性按「最后一段简单选择器 token → 值」建索引。</summary>
    static void ScanUnsupported(string css)
    {
        _unsupportedMap = new();
        foreach (var p in UnsupportedProps) _unsupportedMap[p] = new(StringComparer.OrdinalIgnoreCase);
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
                if (last.Length > 0) _unsupportedMap[prop][last] = val;
            }
        }
    }

    /// <summary>取属性值：有 AngleSharp 结果就用；否则按元素 class/id/tag 命中兜底索引。</summary>
    static string Lookup(string prop, IElement element, string current)
    {
        if (!string.IsNullOrWhiteSpace(current)) return current;
        if (!_unsupportedMap.TryGetValue(prop, out var map) || map.Count == 0) return current;
        var cls = element.GetAttribute("class");
        if (!string.IsNullOrWhiteSpace(cls))
            foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (map.TryGetValue("." + c, out var v)) return v;
        var id = element.GetAttribute("id");
        if (!string.IsNullOrWhiteSpace(id) && map.TryGetValue("#" + id, out var iv)) return iv;
        if (map.TryGetValue(element.TagName.ToLowerInvariant(), out var tv)) return tv;
        return current;
    }

    static ComputedStyle Walk(IWindow window, IElement element, ComputedStyle? parent)
    {
        var cs = ReadStyle(window, element, parent);
        cs.Tag = element.TagName.ToLowerInvariant();

        // 替换元素：读取 <img> 的 src
        if (cs.Tag == "img")
            cs.ImageSrc = element.GetAttribute("src");

        // 单元格 colspan（HTML 属性，非 CSS）
        if (cs.Tag == "td" || cs.Tag == "th")
            if (int.TryParse(element.GetAttribute("colspan"), out var span) && span > 1) cs.Colspan = span;

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
                if (ce.TagName.Equals("BR", StringComparison.OrdinalIgnoreCase))
                {
                    // <br> 只是换行标记，不成盒：进 InlinePieces，但不进 Children（否则会被当成块级子元素破坏 inline 判定）
                    cs.InlinePieces.Add(InlinePiece.Break);
                    continue;
                }
                var childStyle = Walk(window, ce, cs);
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
        bool hasBlockChild = cs.Children.Any(c => c.Display != "none" && !ComputedStyle.IsInlineLevel(c.Display) && c.Tag != "img");
        bool hasInlineEl = cs.Children.Any(c => ComputedStyle.IsInlineLevel(c.Display) && c.Tag != "img");
        bool hasImgChild = cs.Children.Any(c => c.Tag == "img" && c.Display != "none");
        bool hasBr = cs.InlinePieces.Any(p => p.IsBreak);
        // 只有 block 与 table-cell 为其内容建立 inline 格式化上下文（flex/table/row/行内级都不算）
        bool blockish = cs.Display == "block" || cs.Display == "table-cell";
        cs.IsInlineFC = blockish && !hasBlockChild && (hasInlineEl || hasBr || (hasImgChild && cs.TextContent != null));

        return cs;
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

    static ComputedStyle ReadStyle(IWindow window, IElement element, ComputedStyle? parent)
    {
        var s = new ComputedStyle { Parent = parent };

        // AngleSharp.Css 的 GetComputedStyle 是 IWindow 上的扩展方法，会做完 cascade + 继承。
        // 但它对某些值（如 radial-gradient）在 Compute() 里会抛 NullReferenceException——
        // 一旦抛异常该元素整个 computed 都拿不到。这里 try/catch 兜住：拿不到就退回声明值
        // （GetDeclarations，不触发 Compute）+ 父级继承，绝不让渲染崩。
        ICssStyleDeclaration? computed = null;
        try { computed = window.GetComputedStyle(element); }
        catch { /* 该元素 computed 不可用，下面全部走 Spec 兜底 */ }
        string Get(string prop)
        {
            // 单个属性读失败退回空串（下游都有 fallback），不让整个渲染崩
            try
            {
                var v = computed?.GetPropertyValue(prop);
                return string.IsNullOrEmpty(v) ? "" : v;
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
        try { decls = window.GetStyleCollection().GetDeclarations(element, null); }
        catch { /* 拿不到声明值时退回 computed，不让渲染崩 */ }
        string Spec(string prop)
        {
            try { return decls?.GetPropertyValue(prop) ?? ""; }
            catch { return ""; }
        }
        // 尺寸类：声明值是百分比 → 用声明值（保留 %，交布局层解析）；否则优先 computed
        // （已解析 px/em/rem 且含继承），computed 不可用时退回声明值。
        string Val(string prop)
        {
            var sp = Spec(prop);
            if (sp.EndsWith("%")) return sp;
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
        s.TextAlignment = CssValueParser.ParseTextAlign(Raw("text-align"));

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
        s.JustifyContent = CssValueParser.ParseKeyword(Raw("justify-content"), "flex-start");
        s.AlignItems = CssValueParser.ParseKeyword(Raw("align-items"), "stretch");
        s.AlignSelf = CssValueParser.ParseKeyword(Raw("align-self"), "auto");
        s.FlexGrow = CssValueParser.ParseFlexNumber(Raw("flex-grow"), 0f);
        s.FlexShrink = CssValueParser.ParseFlexNumber(Raw("flex-shrink"), 1f);
        s.FlexBasis = CssValueParser.ParseSize(Val("flex-basis"), s.FontSize);
        s.Gap = CssValueParser.ParseSize(Val("gap"), s.FontSize);

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

        // 文本装饰 / 排版细节
        s.TextDecoration = FirstNonEmpty(Raw("text-decoration-line"), FirstToken(Raw("text-decoration")), "none");
        var lsp = Raw("letter-spacing");
        s.LetterSpacing = lsp.Trim() == "normal" ? 0 : CssValueParser.ToPixels(lsp, s.FontSize);
        s.WhiteSpace = CssValueParser.ParseKeyword(Raw("white-space"), "normal");
        s.TextTransform = CssValueParser.ParseKeyword(Raw("text-transform"), "none");
        s.TextOverflow = CssValueParser.ParseKeyword(Lookup("text-overflow", element, Raw("text-overflow")), "clip");
        s.WordBreak = CssValueParser.ParseKeyword(Raw("word-break"), "normal");

        // 视觉
        s.Opacity = CssValueParser.ParseOpacity(Raw("opacity"));
        s.BorderRadiusTopLeft = FirstRadius(Raw("border-top-left-radius"), s.FontSize);
        s.BorderRadiusTopRight = FirstRadius(Raw("border-top-right-radius"), s.FontSize);
        s.BorderRadiusBottomRight = FirstRadius(Raw("border-bottom-right-radius"), s.FontSize);
        s.BorderRadiusBottomLeft = FirstRadius(Raw("border-bottom-left-radius"), s.FontSize);
        s.Shadow = CssValueParser.ParseBoxShadow(Raw("box-shadow"), s.FontSize);
        var bgi = FirstNonEmpty(Spec("background-image"), Raw("background-image"), Spec("background"), Raw("background"));
        s.Background = CssValueParser.ParseGradient(bgi);

        return s;
    }

    static string FirstToken(string v) => (v ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
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
}
