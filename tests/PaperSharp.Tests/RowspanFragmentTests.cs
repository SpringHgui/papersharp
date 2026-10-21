using SkiaSharp;
using PDFtoImage;
using PaperSharp;
using UglyToad.PdfPig;

namespace PaperSharp.Tests;

/// <summary>
/// 跨页 rowspan 单元格的分片绘制。
///
/// 一个 rowspan="6" 的格被页缝切成两段时，两段都得铺它自己的背景、左右边框，切缝那头的横边不描
/// （box-decoration-break: slice），格里的文字只落在起点那一段。修前只在起点行画一次整盒：
/// 续页那一截既没底色也没边框（只剩行背景的斑马纹穿过去），文字按整格高度做 vertical-align 掉到
/// 页缝底下、首页上被切成半截字。Chrome 打印真值见本文件断言里的实测数。
/// </summary>
public class RowspanFragmentTests
{
    /// <summary>
    /// 40 行、每行 ~30px：第一页排到 34 行上下 → 从第 32 行起的 6 行格必然被页缝切开
    /// （断点具体落在 33 还是 34 不影响本判据：只要格跨了页缝，两段都要铺底）。
    /// </summary>
    static string Html
    {
        get
        {
            var sb = new System.Text.StringBuilder("<table><thead><tr><th>名称</th><th>项目</th></tr></thead><tbody>");
            for (int i = 1; i <= 40; i++)
                sb.Append(i == 32
                    ? "<tr><td rowspan=\"6\" class=\"big\">跨页大格</td><td>行 32</td></tr>"
                    : $"<tr><td>行 {i}</td><td class=\"num\">1,000.00</td></tr>");
            return sb.Append("</tbody></table>").ToString();
        }
    }

    // 纯红底 + 深边框：像素判据不受斑马纹与灰字干扰
    const string Css = """
        @page { size: 595pt 842pt; margin: 0 }
        body { font-family: "Microsoft YaHei", sans-serif; font-size: 12px; margin: 0; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border: 1px solid #666; padding: 8px 10px; height: 14px; }
        .big { background-color: #ff0000; }
        """;

    [Fact]
    public void Rowspan_cell_background_is_painted_on_both_pages()
    {
        var pdf = Renderer.RenderPdf(Html, Css);
        Assert.True(pdf.Length > 0);
        using var doc = PdfDocument.Open(pdf);
        Assert.True(doc.NumberOfPages >= 2, "样例没跨页，测不到分片");

        var p1 = RedPixels(Page1(pdf, 0));
        var p2 = RedPixels(Page1(pdf, 1));
        Assert.True(p1 > 2000, $"首页没有大格底色（红像素 {p1}）");
        Assert.True(p2 > 2000, $"续页丢了大格底色（红像素 {p2}）——跨页 rowspan 的分片没铺上");
    }

    [Fact]
    public void Rowspan_cell_text_is_painted_only_on_the_first_page()
    {
        using var doc = PdfDocument.Open(Renderer.RenderPdf(Html, Css));
        var with = Enumerable.Range(1, doc.NumberOfPages)
            .Where(i => doc.GetPage(i).Text.Contains("跨页大格"))
            .ToList();
        Assert.Equal(new[] { 1 }, with);   // 有且只有起点那一页带这段文字（续页不重铺，也不留半截）
    }

    static byte[] Page1(byte[] pdf, int index)
    {
        using var ms = new MemoryStream(pdf);
        using var bmp = Conversion.ToImage(ms, index);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>左半栏里的纯红像素数（避开右侧普通单元格，也不受斑马纹干扰）。</summary>
    static int RedPixels(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        int half = bmp.Width / 2, n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < half; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 200 && c.Green < 70 && c.Blue < 70) n++;
            }
        return n;
    }
}
