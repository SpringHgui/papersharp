using PaperSharp.Css;
using SkiaSharp;

namespace PaperSharp.Tests;

/// <summary>
/// 展平快照级联（ROADMAP M6 步骤②）：快照形状判定 + 换路后必须自己做对的继承/优先级语义。
/// 加速的门槛是「不改结果」——老路里这些语义是 AngleSharp 顺手给的，新路一旦漏掉就是整页错位，
/// 而像素基准（PngGoldenTests）走的是作者 CSS 路径，覆盖不到这里。
/// </summary>
public class FlatCascadeTests
{
    static ComputedStyle At(string html, string css, string tag, bool precomputed = true)
    {
        var root = StyleResolver.BuildDocument(html, css, precomputed: precomputed);
        ComputedStyle? hit = null;
        void Walk(ComputedStyle n)
        {
            if (hit == null && n.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)) hit = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(root);
        return hit ?? throw new InvalidOperationException($"树里找不到 <{tag}>");
    }

    [Fact]
    public void TryBuild_accepts_flat_snapshot_shape()
        => Assert.NotNull(FlatCascade.TryBuild("body{color:red}.ps-1{display:block}.ps-1::before{content:'x'}"));

    [Theory]
    [InlineData("@media all{.ps-1{color:red}}")]      // at-rule：有嵌套块
    [InlineData("@import url(a.css);")]               // at-rule：没有块
    [InlineData(".ps-1 .ps-2{color:red}")]            // 组合器
    [InlineData(".ps-1:hover{color:red}")]            // 伪类
    [InlineData("#x{color:red}")]                     // id 选择器
    [InlineData("div.ps-1{color:red}")]               // 带 tag 的复合选择器
    [InlineData(".ps-1,.ps-2{color:red}")]            // 选择器组
    [InlineData(".ps-1{color:red !important}")]       // !important 改变层叠序
    [InlineData(".ps-1{color:red")]                   // 块没闭合
    [InlineData(".ps-1")]                             // 有选择器没块
    public void TryBuild_rejects_anything_not_plainly_flattened(string css)
        => Assert.Null(FlatCascade.TryBuild(css));

    [Fact]
    public void Inherited_props_walk_up_the_parent_chain()
    {
        // 展平端「与父同值即不下发」，所以子节点自己没写 color/font-size 时必须沿父链上溯。
        var s = At("<div class=\"ps-1\"><span class=\"ps-2\">x</span></div>",
            ".ps-1{color:rgb(255,0,0);font-size:20px;line-height:1.5}", "span");
        Assert.Equal(SKColors.Red, s.Color);
        Assert.Equal(20f, s.FontSize);
        Assert.Equal(1.5f, s.LineHeightFactor);
    }

    [Fact]
    public void Html_rule_is_the_inheritance_base_of_body()
    {
        // 被 Walk 的根就是 body，html 若不并入基底，html 上的字体颜色就整页丢失。
        var s = At("<div class=\"ps-1\">x</div>", "html{color:rgb(0,128,0);font-size:18px}.ps-1{display:block}", "div");
        Assert.Equal(new SKColor(0, 128, 0), s.Color);
        Assert.Equal(18f, s.FontSize);
    }

    [Fact]
    public void Inline_style_beats_class_rule()
    {
        var s = At("<div class=\"ps-1\" style=\"color:rgb(0,0,255)\">x</div>", ".ps-1{color:rgb(255,0,0)}", "div");
        Assert.Equal(SKColors.Blue, s.Color);
    }

    [Fact]
    public void Non_inherited_gap_is_initial_zero_not_auto()
    {
        // 快照缺 margin/padding 即「浏览器算出来是 0」。若按 ParseSize("") 的 Auto 处理，
        // 布局会把它们当 margin:auto 居中，整页块级元素错位。
        var s = At("<div class=\"ps-1\"><span>x</span></div>", ".ps-1{margin-top:10px}", "div");
        Assert.Equal(10f, s.MarginTopCss.Value);
        var child = At("<div class=\"ps-1\"><span class=\"ps-2\">x</span></div>", ".ps-1{margin-top:10px}", "span");
        Assert.False(child.MarginTopCss.IsAuto);
        Assert.Equal(0f, child.MarginTopCss.Value);
    }

    [Fact]
    public void Author_css_keeps_margin_auto_for_centering()
    {
        // 上一条的补集：`margin: 0 auto` 在作者 CSS 路径上必须以 Auto 到达布局层，否则水平居中消失。
        var s = At("<div>x</div>", "div{margin:0 auto}", "div", precomputed: false);
        Assert.True(s.MarginLeftCss.IsAuto);
        Assert.True(s.MarginRightCss.IsAuto);
    }

    [Fact]
    public void Pseudo_element_uses_its_own_rule_and_inherits_the_host()
    {
        var s = At("<div class=\"ps-1\">x</div>",
            ".ps-1{font-size:20px}.ps-1::before{content:'B';color:rgb(0,0,255)}", "div");
        Assert.NotNull(s.Before);
        Assert.Equal(SKColors.Blue, s.Before!.Style.Color);
        Assert.Equal(20f, s.Before.Style.FontSize);   // 宿主 20px，伪元素未写 → 继承
    }

    [Fact]
    public void Flex_normal_keyword_is_normalized_to_its_used_value()
    {
        // 浏览器 computed value 会下发 normal，而布局层只认 stretch/flex-start。
        var s = At("<div class=\"ps-1\">x</div>", ".ps-1{display:flex;align-items:normal;justify-content:normal}", "div");
        Assert.Equal("stretch", s.AlignItems);
        Assert.Equal("flex-start", s.JustifyContent);
    }

    [Fact]
    public void Competing_class_rules_fall_back_to_the_full_cascade()
    {
        // 两个类规则同时命中：展平索引无法定序，整份退回 AngleSharp，按 CSS 出现顺序取后者。
        var s = At("<div class=\"a b\">x</div>", ".a{color:rgb(255,0,0)}.b{color:rgb(0,0,255)}", "div");
        Assert.Equal(SKColors.Blue, s.Color);
    }
}
