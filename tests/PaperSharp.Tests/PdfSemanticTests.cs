using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace PaperSharp.Tests;

/// <summary>
/// 语义层：书签/链接/元数据/文本抽取。书签与链接是 PdfEnhancer 手写 PDF 增量尾部注入的，
/// Skia 本身不出这些对象——PdfPig 能读到的那一刻即证明增量更新语法正确。
/// </summary>
public class PdfSemanticTests
{
    [Fact]
    public void M5_has_bookmark_tree_within_page_range()
    {
        using var doc = PdfDocument.Open(Fixture.RenderPdf("m5"));
        Assert.True(doc.TryGetBookmarks(out Bookmarks? bm), "m5 应带书签（PdfEnhancer 注入失败会静默返回原文件）");
        var nodes = bm!.GetNodes().ToList();
        Assert.Equal(7, nodes.Count); // 探针实测：h1–h6 层级共 7 节点
        Assert.Contains(nodes.OfType<DocumentBookmarkNode>(), n => n.PageNumber >= 1 && n.PageNumber <= doc.NumberOfPages);
        Assert.All(nodes.OfType<DocumentBookmarkNode>(), n =>
            Assert.InRange(n.PageNumber, 1, doc.NumberOfPages));
        Assert.Contains("M5", nodes[0].Title);
    }

    [Fact]
    public void M5_has_link_annotations_with_uri()
    {
        using var doc = PdfDocument.Open(Fixture.RenderPdf("m5"));
        var links = doc.GetPages().SelectMany(p => p.GetHyperlinks()).ToList();
        Assert.Equal(3, links.Count); // 探针实测
        Assert.All(links, l => Assert.Matches("^(https?|mailto):", l.Uri));
    }

    [Fact]
    public void M5_metadata_matches_html_head()
    {
        using var doc = PdfDocument.Open(Fixture.RenderPdf("m5"));
        Assert.Equal("M5 语义文档", doc.Information.Title);
        Assert.Equal("PaperSharp 团队", doc.Information.Author);
    }

    [Fact]
    public void Samples_without_title_fall_back_to_papersharp()
    {
        using var doc = PdfDocument.Open(Fixture.RenderPdf("invoice"));
        Assert.Equal("PaperSharp", doc.Information.Title);
    }

    [Fact]
    public void Subset_font_text_extraction_is_readable()
    {
        // 子集字体若丢 ToUnicode，这里会抽出乱码——同时守住"PDF 里文本可选中/可搜索"
        using var doc = PdfDocument.Open(Fixture.RenderPdf("invoice"));
        var text = doc.GetPage(1).Text;
        Assert.Contains("发票", text);
        Assert.Contains("Invoice", text);
        Assert.Contains("Acme", text);
    }

    [Fact]
    public void Pagination_flows_content_to_later_pages()
    {
        using var doc = PdfDocument.Open(Fixture.RenderPdf("long"));
        Assert.Equal(2, doc.NumberOfPages);
        Assert.False(string.IsNullOrWhiteSpace(doc.GetPage(2).Text));
    }
}
