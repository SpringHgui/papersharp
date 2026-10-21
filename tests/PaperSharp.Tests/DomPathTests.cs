using PaperSharp.Css;
using SkiaSharp;

namespace PaperSharp.Tests;

/// <summary>
/// DOM 身份串（ComputedStyle.DomPath）与盒矩形采集（Renderer.RenderRasterWithBoxes）。
/// 这是 Playground「引擎 vs 浏览器」盒级比对的对位基础：两边必须按同一条 nth-of-type 口径
/// 数出同一个串，否则 Δ 表会把 A 元素的差值记到 B 元素头上——比对口径错了，比得再准也没用。
/// </summary>
public class DomPathTests
{
    /// <summary>把样式树摊平成「DomPath → 节点」表（跳过匿名盒）。</summary>
    static Dictionary<string, ComputedStyle> Paths(string html, string css = "")
    {
        var root = StyleResolver.BuildDocument(html, css, domPath: true);
        var map = new Dictionary<string, ComputedStyle>();
        void Walk(ComputedStyle n)
        {
            if (n.DomPath != null) map[n.DomPath] = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(root);
        return map;
    }

    [Fact]
    public void Off_by_default_so_production_render_untouched()
    {
        var root = StyleResolver.BuildDocument("<p>hi</p>", "");
        Assert.Null(root.DomPath);
        Assert.All(root.Children, c => Assert.Null(c.DomPath));
    }

    [Fact]
    public void Root_is_body_and_children_are_tagged_in_document_order()
    {
        var map = Paths("<div><span>a</span><span>b</span></div><p>c</p>");
        Assert.Contains("body[1]", map.Keys);
        Assert.Contains("body[1]/div[1]", map.Keys);
        Assert.Contains("body[1]/div[1]/span[1]", map.Keys);
        Assert.Contains("body[1]/div[1]/span[2]", map.Keys);
        Assert.Contains("body[1]/p[1]", map.Keys);
    }

    /// <summary>
    /// 不成盒的兄弟也占号：浏览器 nth-of-type 就是这么数的。
    /// 若引擎侧跳过 script/style/br 不占号，第二个 p 会数成 p[1]，两边全体错位。
    /// </summary>
    [Theory]
    [InlineData("<p>a</p><script>var x=1</script><p>b</p>")]
    [InlineData("<p>a</p><style>p{color:red}</style><p>b</p>")]
    [InlineData("<p>a</p><br><p>b</p>")]
    [InlineData("<p>a</p><template><p>x</p></template><p>b</p>")]
    public void Non_rendering_siblings_still_consume_the_index(string html)
    {
        var map = Paths(html);
        Assert.Contains("body[1]/p[1]", map.Keys);
        Assert.Contains("body[1]/p[2]", map.Keys);
        Assert.DoesNotContain("body[1]/p[3]", map.Keys);
    }

    /// <summary>替换内容内部不成盒（svg 子节点、input 无子盒），浏览器侧同样不下钻，故两边都不出现。</summary>
    [Fact]
    public void Replaced_elements_do_not_expose_inner_nodes()
    {
        var map = Paths("<svg><circle/></svg><input type=text>");
        Assert.Contains("body[1]/svg[1]", map.Keys);
        Assert.Contains("body[1]/input[1]", map.Keys);
        Assert.DoesNotContain(map.Keys, k => k.Contains("/circle", StringComparison.Ordinal));
    }

    /// <summary>
    /// 匿名盒（flex 直放文本包出的匿名项、::before/::after 生成盒）不对应真实元素，
    /// 采集出来的矩形必须恰好是「每个真实元素一条」——多一条就会在 Δ 表里变成重复行。
    /// </summary>
    [Fact]
    public void Anonymous_boxes_carry_no_identity()
    {
        var html = "<div style=\"display:flex\">裸文本<i>x</i></div>";
        var paths = Renderer.RenderRasterWithBoxes(html, "div::before{content:'B'}", width: 400).Boxes
            .Select(b => b.DomPath).ToList();

        Assert.Equal(
            new[] { "body[1]", "body[1]/div[1]", "body[1]/div[1]/i[1]" }.OrderBy(x => x),
            paths.OrderBy(x => x));
    }

    [Fact]
    public void Boxes_are_normalised_to_the_body_border_box_origin()
    {
        var html = "<div style=\"width:120px;height:40px;background:#eee\">x</div>";
        var outp = Renderer.RenderRasterWithBoxes(html, "", width: 400);

        var body = Assert.Single(outp.Boxes, b => b.Tag == "body" || b.DomPath == "body[1]");
        Assert.Equal(0, body.X, 2);
        Assert.Equal(0, body.Y, 2);

        var div = outp.Boxes.Single(b => b.Tag == "div");
        Assert.Equal("body[1]/div[1]", div.DomPath);
        Assert.Equal(120, div.W, 1);      // border-box 宽 = 声明宽（无 padding/border）
        Assert.True(outp.Bytes.Length > 0);
        Assert.True(outp.LayoutHeight > 0);
    }

    /// <summary>盒矩形与 PNG 出自同一轮布局：PNG 高（scale=1）应等于 LayoutHeight，且内容不超出画布。</summary>
    [Fact]
    public void Raster_and_boxes_come_from_the_same_layout_pass()
    {
        var html = "<div style=\"width:200px;height:80px\"></div><p>tail</p>";
        var outp = Renderer.RenderRasterWithBoxes(html, "", width: 320, scale: 1f);
        using var img = SKImage.FromEncodedData(outp.Bytes);
        Assert.Equal(320, img.Width);
        Assert.Equal((int)Math.Ceiling(outp.LayoutHeight), img.Height);
        Assert.All(outp.Boxes, b => Assert.True(b.Y + b.H <= outp.LayoutHeight + 1, $"{b.DomPath} 越出画布"));
    }

    /// <summary>
    /// 表格行组（thead/tbody/tfoot）在引擎里是透传容器，几何恒为 0：
    /// 必须从盒列表里剔除，否则比对工具会报出「浏览器 1235 宽 vs 引擎 0 宽」这种假的最大偏差；
    /// 但里面的 tr/td 有真实几何，不能被一起跳过。
    /// </summary>
    [Fact]
    public void Table_row_groups_are_excluded_but_rows_and_cells_are_not()
    {
        var html = "<table><thead><tr><th>表头</th></tr></thead><tbody><tr><td>格子</td></tr></tbody></table>";
        var paths = Renderer.RenderRasterWithBoxes(html, "", width: 600).Boxes;

        Assert.DoesNotContain(paths, b => b.Tag is "thead" or "tbody" or "tfoot");
        // 行组被剔除后，它下面的 tr/td 必须还在，且有真实几何（不能连坐）
        Assert.Equal(2, paths.Count(b => b.Tag == "tr"));
        Assert.All(paths.Where(b => b.Tag is "tr" or "th" or "td"),
            b => Assert.True(b.W > 0 && b.H > 0, $"{b.Tag} 该有真实几何"));
        var header = paths.Single(b => b.Tag == "th");
        var cell = paths.Single(b => b.Tag == "td");
        Assert.True(cell.Y > header.Y, "正文行应排在表头行之下");
        Assert.Equal(header.X, cell.X, 1);   // 同列左右对齐
    }

    /// <summary>
    /// 纯行内元素引擎不单独成盒，但比对工具需要它的矩形（否则网页里每个 span 都成假孤儿）。
    /// 补出来的矩形必须落在父块内、排在同行文本之后，且跨行时取并集。
    /// </summary>
    [Fact]
    public void Inline_only_elements_get_a_union_rect()
    {
        var html = "<p style=\"width:200px\">前前前前<b>粗体</b>后后后后</p>";
        var boxes = Renderer.RenderRasterWithBoxes(html, "", width: 600).Boxes;

        var p = boxes.Single(b => b.Tag == "p");
        var b = boxes.Single(b => b.Tag == "b");
        Assert.Equal("body[1]/p[1]/b[1]", b.DomPath);
        Assert.True(b.W > 0 && b.H > 0, "行内片段没量出尺寸");
        Assert.True(b.X >= p.X && b.X + b.W <= p.X + p.W + 1, $"b 越出父块：b=[{b.X},{b.X + b.W}] p=[{p.X},{p.X + p.W}]");
        Assert.True(b.Y >= p.Y - 1 && b.Y + b.H <= p.Y + p.H + 1, "b 越出父块纵向范围");
    }

    /// <summary>嵌套行内：内层片段同时并给外层祖先（CSS 里 b 的盒本就包含 i 的内容）。</summary>
    [Fact]
    public void Nested_inlines_each_get_their_own_union()
    {
        var boxes = Renderer.RenderRasterWithBoxes("<p><b><i>x</i></b></p>", "", width: 600).Boxes;
        var b = boxes.Single(x => x.Tag == "b");
        var i = boxes.Single(x => x.Tag == "i");
        // 同一字符：两级行内并出来的矩形应当重合（差在亚像素取整）
        Assert.Equal(b.X, i.X, 0);
        Assert.Equal(b.Y, i.Y, 0);
        Assert.Equal(b.W, i.W, 0);
    }

    /// <summary>盒级结果优先：已经成盒的身份（如 inline-block）不能被行内补算覆盖成两条。</summary>
    [Fact]
    public void Boxed_identities_are_never_duplicated_by_the_inline_pass()
    {
        var boxes = Renderer.RenderRasterWithBoxes("<p>a<span style=\"display:inline-block;width:40px;height:10px\"></span>b</p>", "", width: 600).Boxes;
        Assert.Single(boxes, b => b.DomPath.EndsWith("/span[1]"));
    }
}
