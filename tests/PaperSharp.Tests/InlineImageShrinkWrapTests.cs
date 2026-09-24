using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// 「图 + 文字」混排的两条几何契约（Playground 盒差异表实测出来的，浏览器为真值）：
///   ① 块级子项纵向堆叠，flex 项的收缩宽 = 子项之最，不是相加——相加会把后一个兄弟整条推歪（实测 216px）；
///   ② 块里的 img 由匿名块立行盒，替换元素底边压在基线上，行盒底还要留一截 strut descent——
///      缺了它，图之后的每个兄弟都上移（实测 5px），小图独占一行时又会矮于行高。
/// 两条都是「安静错掉」的：不崩不报错，只是排出来的东西和浏览器不一样。
/// </summary>
public class InlineImageShrinkWrapTests
{
    /// <summary>按 Playground 同一口径布局：ToBoxTree → Measure → Position，返回 body 盒。</summary>
    static BlockBox Layout(string html, string css, float width = 600)
    {
        var root = LayoutBuilder.ToBoxTree(StyleResolver.BuildDocument(html, css));
        LayoutBuilder.Measure(root, width);
        LayoutBuilder.Position(root, 0, 0);
        return root;
    }

    // ── ① flex 项收缩宽 ──────────────────────────────────────────────

    [Fact]
    public void FlexItem_widthIsWidestBlockChild_notTheirSum()
    {
        var body = Layout(
            "<div class='c'><div class='i'><div class='w1'></div><div class='w2'></div></div><div class='s'></div></div>",
            ".c{display:flex;width:600px} .w1{width:200px;height:10px} .w2{width:80px;height:10px} .s{width:50px;height:10px}",
            600);
        var row = body.Children[0];

        Assert.Equal(200, row.Children[0].ContentWidth, 1);     // 取最大，不是 200+80
        Assert.Equal(200, row.Children[1].BorderX, 1);          // 后一个兄弟不被推歪
    }

    [Fact]
    public void ColumnFlexItem_crossWidthAlsoShrinksToMax()
    {
        var body = Layout(
            "<div class='c'><div class='i'><div class='w1'></div><div class='w2'></div></div></div>",
            ".c{display:flex;flex-direction:column;align-items:flex-start;width:600px}" +
            ".w1{width:200px;height:10px} .w2{width:80px;height:10px}",
            600);

        Assert.Equal(200, body.Children[0].Children[0].TotalWidth, 1);   // column 的交叉轴是宽：仍取最大
    }

    // ── ② 行内图的行盒 ──────────────────────────────────────────────

    [Fact]
    public void ImageFollowedByBlock_keepsStrutDescentBelowBaseline()
    {
        // 20px 字号 / 行高 2 → strut 基线下方 = 20*0.22 + (40-(20+4.4))/2 = 12.2
        var c = Layout(
            "<div class='c'><img class='im'/><p>x</p></div>",
            ".c{width:400px;font-size:20px;line-height:2} .im{width:100px;height:50px} p{margin:0;height:20px}",
            400).Children[0];
        var anon = c.Children[0];                       // 匿名块：浏览器为图片建的 IFC
        var p = c.Children[1];

        Assert.True(anon.IsInlineFC, "图与块混排时图应落进匿名 inline 块，而不是当块级子盒");
        Assert.Null(anon.Style.DomPath);                   // 匿名盒不对应真实元素，不参与盒比对
        Assert.Equal(50, anon.Atoms![0].Box!.TotalHeight, 1);
        Assert.Equal(62.2f, p.BorderY, 1);                 // 50 + 12.2：图片下方那道缝必须在
    }

    [Fact]
    public void LoneImageBlock_isAtLeastOneStrutTall()
    {
        var body = Layout(
            "<p class='t'><img class='im'/></p>",
            ".t{width:400px;margin:0;font-size:20px;line-height:2} .im{width:16px;height:16px}",
            400);

        Assert.Equal(40, body.Children[0].ContentHeight, 1);   // 小图独占一行 → 行高由 strut 兜底，不是 16
    }

    [Fact]
    public void InlineBlockOnBaseline_doesNotInheritReplacedElementDescent()
    {
        // inline-block 的基线落在其内部文字上，盒底本就在基线上方：给它加 strut descent 会把行盒撑高（inline 样例实测 +4.2/行）
        var body = Layout(
            "<p class='t'><span class='ib'></span> 文字</p>",
            ".t{width:400px;margin:0;font-size:20px;line-height:2} .ib{display:inline-block;width:30px;height:30px}",
            400);

        Assert.Equal(40, body.Children[0].ContentHeight, 1);   // max(行高 40, 30+3)，不是 30+12.2
    }
}
