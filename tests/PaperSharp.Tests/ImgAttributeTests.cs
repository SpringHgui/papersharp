using PaperSharp;
using PaperSharp.Css;

namespace PaperSharp.Tests;

/// <summary>
/// img 的 HTML 表现属性（width/height）。浏览器把它们当特异度 0 的作者规则参与布局，
/// 引擎此前完全忽略——结果 <c>&lt;img width="60"&gt;</c> 按原图尺寸画，整块版式往下顶。
/// 关键是「兜底」而不是「覆盖」：作者 CSS 声明了宽高时必须让 CSS 赢。
/// </summary>
public class ImgAttributeTests
{
    /// <summary>量出 img 盒的尺寸（走真实渲染入口，顺带覆盖 MeasureImage 那条路）。</summary>
    static (float W, float H) ImgSize(string html, string css = "")
    {
        var box = Renderer.RenderRasterWithBoxes(html, css, width: 600, baseDir: Fixture.AssetsDir).Boxes
            .Single(b => b.Tag == "img");
        return (box.W, box.H);
    }

    const string Img = "<img src=\"imgs/qr.png\">";   // 原图 120×120

    [Fact]
    public void No_attribute_uses_intrinsic_size()
        => Assert.Equal((120f, 120f), (ImgSize(Img).W, ImgSize(Img).H));

    [Theory]
    [InlineData("<img src=\"imgs/qr.png\" width=\"60\">", 60, 60)]        // 只给宽 → 按固有比例补高
    [InlineData("<img src=\"imgs/qr.png\" height=\"48\">", 48, 48)]
    [InlineData("<img src=\"imgs/qr.png\" width=\"60\" height=\"20\">", 60, 20)]
    public void Width_and_height_attributes_are_honored(string html, float w, float h)
    {
        var got = ImgSize(html);
        Assert.Equal(w, got.W, 1);
        Assert.Equal(h, got.H, 1);
    }

    /// <summary>表现属性优先级低于作者 CSS：这是 presentational hint 的定义，反过来就吃掉规则了。</summary>
    [Fact]
    public void Author_css_beats_the_attribute()
        => Assert.Equal(100f, ImgSize("<img src=\"imgs/qr.png\" width=\"60\">", "img{width:100px}").W, 1);

    [Theory]
    [InlineData("abc")]      // 非数字 → 忽略，回固有尺寸
    [InlineData("-20")]      // 负数 → 忽略
    [InlineData("")]         // 空串 → 忽略
    public void Malformed_attribute_falls_back_to_intrinsic(string raw)
        => Assert.Equal(120f, ImgSize($"<img src=\"imgs/qr.png\" width=\"{raw}\">").W, 1);

    [Fact]
    public void Percent_attribute_resolves_against_containing_block()
        => Assert.Equal(300f, ImgSize("<img src=\"imgs/qr.png\" width=\"50%\">").W, 1);   // 600 视口的 50%
}
