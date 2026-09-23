using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// flex 交叉轴几何：单行容器有确定交叉尺寸时，flex 线要吃满容器（align-content 的 normal 按 stretch 处理），
/// 否则 align-items:center 只会在"最高子项"那条线内居中，定高卡片永远垂直居中不了。
/// 子项一律给死宽高，避免断言掺进字体度量。
/// </summary>
public class FlexCrossAxisTests
{
    /// <summary>布局并返回 .c 盒（body → .c）。</summary>
    static BlockBox Card(string html, string css, float width)
    {
        var root = LayoutBuilder.ToBoxTree(StyleResolver.BuildDocument(html, css));
        LayoutBuilder.Measure(root, width);
        LayoutBuilder.Position(root, 0, 0);
        return root.Children[0];
    }

    [Fact]
    public void Row_definiteHeight_alignItemsCenter_centersInContainer()
    {
        var card = Card(
            "<div class='c'><div class='a'></div></div>",
            ".c { width:600px; height:400px; display:flex; align-items:center; justify-content:center; }" +
            ".a { width:100px; height:50px; }", 600);
        var item = card.Children[0];

        Assert.Equal(250, item.BorderX, 1);   // 主轴：(600-100)/2
        Assert.Equal(175, item.BorderY, 1);   // 交叉轴：(400-50)/2，而非 (50-50)/2
    }

    [Fact]
    public void Column_definiteWidth_alignItemsCenter_centersInContainer()
    {
        var item = Card(
            "<div class='c'><div class='a'></div></div>",
            ".c { width:600px; height:400px; display:flex; flex-direction:column; align-items:center; }" +
            ".a { width:100px; height:50px; }", 600).Children[0];

        Assert.Equal(250, item.BorderX, 1);   // column 的交叉轴是宽
        Assert.Equal(0, item.BorderY, 1);     // 主轴仍从顶部排
        Assert.Equal(100, item.ContentWidth, 1);   // 显式宽不被容器宽吞掉
    }

    [Fact]
    public void Row_autoHeight_alignItemsCenter_doesNotInventCrossSpace()
    {
        var card = Card(
            "<div class='c'><div class='a'></div><div class='b'></div></div>",
            ".c { width:600px; display:flex; align-items:center; }" +
            ".a { width:100px; height:50px; } .b { width:100px; height:20px; }", 600);

        Assert.Equal(50, card.ContentHeight, 1);              // 交叉尺寸不确定 → 线高即最高子项
        Assert.Equal(0, card.Children[0].BorderY, 1);
        Assert.Equal(15, card.Children[1].BorderY, 1);        // (50-20)/2，仍在本线内居中
    }

    [Fact]
    public void Row_definiteHeight_alignItemsFlexEnd_pinsToBottom()
    {
        var item = Card(
            "<div class='c'><div class='a'></div></div>",
            ".c { width:600px; height:400px; display:flex; align-items:flex-end; }" +
            ".a { width:100px; height:50px; }", 600).Children[0];

        Assert.Equal(350, item.BorderY, 1);   // 400-50
    }

    [Fact]
    public void Column_defaultStretch_stillFillsWidth()
    {
        var item = Card(
            "<div class='c'><div class='a'></div></div>",
            ".c { width:600px; height:400px; display:flex; flex-direction:column; }" +
            ".a { height:50px; }", 600).Children[0];

        Assert.Equal(600, item.ContentWidth, 1);
        Assert.Equal(0, item.BorderX, 1);
    }
}
