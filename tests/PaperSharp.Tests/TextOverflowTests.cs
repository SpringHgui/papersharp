using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// text-overflow:ellipsis 的生效闸门。三条判据全部取自无头 Edge 对同一份标记的实测
/// （探针页 src/PaperSharpSamples/assets/_ell.html，逐行像素对照见 .scratch/ell_pairs.png）：
///
/// ① 只有**本盒自己**裁剪溢出才截（CSS Overflow 3：行盒在行尾方向被裁时 ellipsis 才生效）。
///    overflow:visible（初值）+ nowrap + ellipsis → 浏览器整行原样溢出、一个省略号都不加（探针 A/D 行）；
///    祖先 overflow:hidden 替它裁也一样不加（探针 K 行）。
/// ② 宽度写不写死无关：max-width 撑出的 auto 宽盒照样截（探针 F 行）。
/// ③ text-overflow:clip 永远不加省略号，超出部分交给绘制期的溢出裁剪硬切（探针 E 行）。
///
/// 断言落在 WrappedLines 而不是像素：截断发生在测量期，像素层还叠着裁剪与抗锯齿，
/// 对照式（同一份 CSS 只换 overflow 值）在盒树上才是干净的单变量实验。
/// </summary>
public class TextOverflowTests
{
    const string Text = "省略号生效闸门探针文字省略号生效闸门探针文字省略号生效闸门探针文字";

    static string Css(string extra) =>
        "html,body{margin:0}#t{width:150px;font-size:20px;line-height:1.2;white-space:nowrap;" + extra + "}";

    /// <summary>测完的盒树里第一条带文本的块盒的逐行文本（探针页只有一个文本块，命中即它）。</summary>
    static string[] Lines(string html, string css, float width = 400f)
    {
        var body = LayoutBuilder.ToBoxTree(StyleResolver.BuildDocument(html, css));
        LayoutBuilder.Measure(body, width);
        return Probe(body) ?? throw new InvalidOperationException("探针失效：盒树里没有带 WrappedLines 的块盒");
    }

    static string[]? Probe(BlockBox b)
    {
        if (b.WrappedLines is { Length: > 0 } lines) return lines;
        foreach (var c in b.Children)
            if (Probe(c) is { } r) return r;
        return null;
    }

    [Fact]
    public void Ellipsis_truncates_only_when_the_box_clips_its_own_overflow()
    {
        var hidden = Lines("<div id=t>" + Text + "</div>", Css("text-overflow:ellipsis;overflow:hidden"));
        Assert.Single(hidden);
        Assert.EndsWith("…", hidden[0]);

        // 对照组：除 overflow 值外逐字相同。这条不成立，上面那条就是假绿。
        var visible = Lines("<div id=t>" + Text + "</div>", Css("text-overflow:ellipsis;overflow:visible"));
        Assert.Equal(Text, visible[0]);
        Assert.DoesNotContain("…", visible[0]);
    }

    [Fact]
    public void Ancestor_clip_does_not_arm_the_ellipsis()
    {
        // 裁剪来自祖先而非本盒：浏览器只把内容在祖先的 padding box 上硬切，本盒那行文本原样保留。
        var css = "html,body{margin:0}#p{overflow:hidden}" + Css("text-overflow:ellipsis");
        var lines = Lines("<div id=p><div id=t>" + Text + "</div></div>", css);
        Assert.Equal(Text, lines[0]);
    }

    [Fact]
    public void Ellipsis_works_on_a_max_width_box_with_auto_width()
    {
        // 旧条件 !Width.IsAuto 会把这一类整个漏掉（引擎原先原样溢出，浏览器截）。
        var css = "html,body{margin:0}#t{max-width:150px;font-size:20px;line-height:1.2;" +
                  "white-space:nowrap;text-overflow:ellipsis;overflow:hidden}";
        var lines = Lines("<div id=t>" + Text + "</div>", css);
        Assert.Single(lines);
        Assert.EndsWith("…", lines[0]);
    }

    [Fact]
    public void Clip_keyword_never_appends_an_ellipsis()
    {
        var lines = Lines("<div id=t>" + Text + "</div>", Css("text-overflow:clip;overflow:hidden"));
        Assert.Equal(Text, lines[0]);
    }

    [Fact]
    public void Text_that_fits_is_left_alone()
    {
        var lines = Lines("<div id=t>短</div>", Css("text-overflow:ellipsis;overflow:hidden"));
        Assert.Equal("短", Assert.Single(lines));
    }
}
