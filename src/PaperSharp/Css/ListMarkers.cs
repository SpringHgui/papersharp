using SkiaSharp;
using PaperSharp.Layout;

namespace PaperSharp.Css;

/// <summary>
/// 列表标记（marker）：按文档序给每个 <c>display:list-item</c> 盒编号，并把 marker 做成宿主盒的
/// <c>::before</c> 行内片段，交给 <see cref="PseudoContent"/> 注入。
///
/// 为什么不另起一条"标记层"绘制通路：本引擎的块盒有两条互斥的排版通路（块文本路径只收直接文本、
/// inline-FC 路径收 InlinePieces），而分页裁切、浮动环绕、行内背景全都只认后者已有的实现。
/// 复用伪元素片段 = 一次改动同时拿到折行、分页与 PDF/光栅两条绘制路径。
/// </summary>
public static class ListMarkers
{
    /// <summary>marker 右缘到正文起点的空隙。Chrome 实测（2× 探针，disc @ fs=10/16/24/48 → 空隙 6/8/10/17px）：
    /// 约 3px + 0.3em，与 marker 文本宽无关——正文恒落在 li 内容盒左缘，marker 整体挂在盒外。</summary>
    static float MarkerGap(ComputedStyle s) => 3f + 0.3f * s.FontSize;

    /// <summary>跑一遍整棵树，注入 marker 片段。返回是否至少注入了一个（决定要不要跑 PseudoContent.Apply）。</summary>
    public static bool Apply(ComputedStyle root)
    {
        bool any = false;
        Visit(root, ref any);
        return any;
    }

    static void Visit(ComputedStyle node, ref bool any)
    {
        bool isList = node.Tag is "ul" or "ol" or "menu";
        if (!isList)
        {
            foreach (var c in node.Children) Visit(c, ref any);
            return;
        }
        // 编号作用域 = 这个列表本身：直接子项按文档序 1,2,3…（<ol start=n> 的偏移已在 StyleResolver 折进
        // ListStartOffset），嵌套列表在 li 里自成作用域，故递归下去互不干扰。
        int index = node.ListStartOffset;
        foreach (var c in node.Children)
        {
            if (c.Display == "list-item") Emit(c, ++index, ref any);
            Visit(c, ref any);
        }
    }

    /// <summary>给一个 list-item 盒发 marker：li 自己写的 list-style-type 优先，否则用列表容器的。</summary>
    static void Emit(ComputedStyle item, int index, ref bool any)
    {
        string type = item.ListStyleType;   // StyleResolver 已把"自己的声明 > 继承列表容器 > 标签默认"排好
        if (type == "none") return;
        string glyph = Glyph(type);
        string text = glyph.Length == 0 ? CounterStyle.Format(index, type) + "." : glyph;
        if (text.Length == 0) return;

        var gen = new PseudoGen
        {
            Style = MarkerStyle(item, text),
            Runs = new List<ContentRun> { new ContentRun { Kind = ContentRunKind.Literal, Text = text } },
        };
        if (item.Before == null) item.Before = gen;
        else item.Before.Runs.Insert(0, gen.Runs[0]);   // 浏览器里 marker 排在 ::before 内容之前

        // outside（默认）：marker 落在 li 内容盒**之外**，不占行内宽度。本引擎没有独立的标记层，
        // 用负的 text-indent 让出这块悬挂区——首行起点左移 (markerW+gap)，首行可用宽相应 +（markerW+gap），
        // 而 marker 自己的盒宽含 gap（见 MarkerStyle 的 padding-right），于是正文正好落回内容盒左缘，
        // 折行结果与浏览器一致。用 -= 而不是 =：作者自己写的 text-indent 要与之叠加。
        if (!item.ListStyleInside) item.TextIndent -= MarkerAdvance(item, text);
        any = true;
    }

    /// <summary>无序 marker 的字形；有序（十进制/字母/罗马）返回空串，由调用方拼序号。</summary>
    static string Glyph(string type) => type switch
    {
        "disc" => "•",       // U+2022
        "circle" => "◦",     // U+25E6 白心小圆
        "square" => "▪",     // U+25AA 小实心方块（U+25A0 在 CJK 字体里是全角大方块，明显偏大）
        _ => "",
    };

    /// <summary>marker 占用（含间隙）的横向尺寸：文本宽 + 0.5em 间隙。</summary>
    static float MarkerAdvance(ComputedStyle s, string text)
    {
        using var font = new SKFont(FontManager.Get(s.FontFamily, s.FontWeight), s.FontSize);
        return TextMeasurer.LineWidth(text, font, s.LetterSpacing) + MarkerGap(s);
    }

    /// <summary>marker 盒的样式：继承宿主（字体/字色/行高），但盒级装饰一律清掉——marker 只是行内文字。</summary>
    static ComputedStyle MarkerStyle(ComputedStyle host, string text)
    {
        var s = host.CloneShallow();
        s.Display = "inline";
        s.TextContent = text;
        s.Children = new();
        s.InlinePieces = new();
        s.Before = s.After = null;
        s.BackgroundColor = SKColor.Empty;
        s.Background = null;
        s.Shadows = null;
        s.TextShadow = null;
        s.TextIndent = 0;
        s.BorderWidthTop = s.BorderWidthRight = s.BorderWidthBottom = s.BorderWidthLeft = 0;
        // marker 是行内片段，其横向外扩 = 自己的 padding：宿主 li 的 padding 绝不能跟过来（会在正文前多出一截），
        // 而右半边正是 marker→正文的空隙（InlineLayout.HDeco 据此算原子宽，正文才落在内容盒左缘）。
        s.PaddingLeftCss = s.PaddingBottomCss = s.PaddingTopCss = CssSize.Abs(0);
        s.PaddingRightCss = CssSize.Abs(MarkerGap(host));
        s.HasGeneratedInline = false;
        return s;
    }
}
