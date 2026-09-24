namespace PaperSharp.Tests;

/// <summary>
/// 边距组的另一半：兄弟坍缩比的不是「子的 margin-top」，而是子盒**这一侧整组**逃逸上来的有效边距。
///
/// m3 实测：h1{margin-bottom:18px} 与 section（自身无 margin、无 padding，首子 h2{margin-top:22px}）
/// 相邻时，浏览器把 18/0/22 三段并成一个边距组取最大 22，section 落在 h1 下方 22px；
/// 引擎此前只拿 section.MarginTop(0) 去和 18 坍，等于把 18 与逃逸的 22 相加（40），
/// 一处算错让整篇 49 个盒齐刷刷下移 18px（均值 68.7px 的漂移全在这一条上）。
///
/// 断言值全部来自 assets/_mgrp.html 在 headless Edge 里的 getBoundingClientRect 实测
/// （boxdiff 对拍 13/15 全绿），不是照抄规范文字。
/// </summary>
public class MarginGroupCollapseTests
{
    const string Html = """
        <div class="a"></div>
        <section class="p1"><h2 class="g"></h2></section>
        <div class="a"></div>
        <section class="p2"><h2 class="g"></h2></section>
        <div class="a"></div>
        <section class="p1"><h2 class="s"></h2></section>
        <div class="note"><strong>INLINE-RUN</strong><ul><li>one</li><li>two</li></ul></div>
        """;

    const string Css = """
        body { margin: 0; font: 16px/1.5 Arial; }
        .a { height: 40px; margin-bottom: 18px; background: #eee; }
        .p1 { background: #ddf; }
        .p2 { padding-top: 5px; background: #fdf; }
        h2.g { height: 30px; margin: 22px 0 10px; background: #ffd; }
        h2.s { height: 30px; margin: 6px 0 10px; background: #dfd; }
        .note { background: #eee; }
        .note strong { background: #f99; }
        .note ul { margin: 12px 0; }
        li { height: 20px; }
        """;

    static Dictionary<string, BoxRect> Boxes()
        => Renderer.RenderRasterWithBoxes(Html, Css, 800).Boxes.ToDictionary(b => b.DomPath);

    /// <summary>父无 border/padding 时，首子的顶边距逃逸上来与兄弟的下边距并成一组：max(18,22)=22。</summary>
    [Fact]
    public void Escaped_child_margin_joins_the_sibling_group()
    {
        var b = Boxes();
        // 上块底 40（div.a 高 40），section 落在 62 —— 中间只剩 22，而不是 18+22=40
        Assert.Equal(40, b["body[1]/div[1]"].H, 1);
        Assert.Equal(62, b["body[1]/section[1]"].Y, 1);
        Assert.Equal(62, b["body[1]/section[1]/h2[1]"].Y, 1);                      // 边距已挪到 section 外，h2 与 section 同顶
    }

    /// <summary>父在这一侧有 padding 时不成组：兄弟间距只按父自己的边距坍，子的顶边距留在父内。</summary>
    [Fact]
    public void Padded_parent_keeps_the_child_margin_inside()
    {
        var b = Boxes();
        Assert.Equal(160, b["body[1]/section[2]"].Y, 1);          // 142（div2 底）+ 18
        Assert.Equal(187, b["body[1]/section[2]/h2[1]"].Y, 1);    // 160 + 5(padding) + 22
    }

    /// <summary>同组里逃逸上来的那截比兄弟边距小时，取大的那个（6 与 18 → 18）。</summary>
    [Fact]
    public void Larger_margin_of_the_group_wins()
    {
        var b = Boxes();
        Assert.Equal(285, b["body[1]/section[3]"].Y, 1);          // 267 + max(18, 6)
        Assert.Equal(285, b["body[1]/section[3]/h2[1]"].Y, 1);
    }

    /// <summary>列表 UA 默认：ul 左缩进 40px，li 的内容宽随之收窄（此前本表缺这条，li 摆到 x=0 满宽）。</summary>
    [Fact]
    public void Lists_get_the_UA_inline_start_indent()
    {
        var b = Boxes();
        var li = b["body[1]/div[4]/ul[1]/li[1]"];
        Assert.Equal(40, li.X, 1);
        Assert.Equal(760, li.W, 1);
    }

    /// <summary>
    /// 与块级兄弟混排的行内元素必须被裹进匿名块：它的盒按文字收缩（实测 105.8/浏览器 92.4，
    /// 余下 13px 是 Arial 字距度量差，另计），而不是当作父块的一个满宽块级子盒（800）——
    /// m1 里 div.note 的 strong 此前实测差 1035px。
    /// </summary>
    [Fact]
    public void Inline_among_block_siblings_shrinks_to_its_text()
    {
        var b = Boxes();
        var strong = b["body[1]/div[4]/strong[1]"];
        Assert.Equal(0, strong.X, 1);
        Assert.True(strong.W < 200, $"strong 宽 {strong.W}：行内元素被当成了满宽块盒");
    }
}
