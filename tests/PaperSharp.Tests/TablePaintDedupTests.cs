using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PaperSharp.Tests;

/// <summary>
/// 跨页续表的绘制层去重哨兵。
///
/// 血泪：分页器把一行落页时先 <c>EmitInto(行盒)</c>（行盒的 Children 就是那一行的单元格，
/// 于是整行的字随子树被登记一遍），再逐格 <c>EmitInto(格)</c> 登记一遍；rowspan 格又出现在
/// 它跨的每一个行的 TRows 里，每跨一行再多一遍。两遍坐标完全相同，长图光栅上看不出来，
/// 但 PDF 里就是同一段文字的两条 Tj —— 查看器（Edge/PDFium）按各自的取整各画一次，
/// 放大看就是字面重影，文件也白胖一截。m1/_wprobe2 实测：跨页那几页 100% 的文字都被画了两遍。
///
/// 判据直接读**内容流**（把 Tj 的落点逐条数出来），不走 PdfPig 的 Letters：那条路会把
/// 透明层（clip-path / mask-image 生成的 Form XObject）摊平回页面，凭空报出同位重复。
/// </summary>
public class TablePaintDedupTests
{
    public static TheoryData<string> TableSamples => new() { "m1", "m2", "m3", "table", "report", "invoice" };

    [Theory]
    [MemberData(nameof(TableSamples))]
    public void Pdf_paints_each_run_at_most_once_per_page(string name)
    {
        var pdf = Fixture.RenderPdf(name);
        var pages = ContentStreams(pdf);
        Assert.NotEmpty(pages);

        foreach (var (idx, ops) in pages)
        {
            var dup = ops.GroupBy(x => x).Where(g => g.Count() > 1)
                .Select(g => $"{g.Key.hex} @({g.Key.x},{g.Key.y}) ×{g.Count()}")
                .ToList();
            Assert.True(dup.Count == 0, $"{name} 第 {idx + 1} 页有 {dup.Count} 段文字被重复落笔：{string.Join(" · ", dup.Take(5))}");
        }
    }

    static readonly Regex ShowOp = new(
        @"1 0 0 -1 (?<tx>[-\d.]+) (?<ty>[-\d.]+) Tm|(?<dx>[-\d.]+) (?<dy>[-\d.]+) Td|<(?<hex>[0-9A-Fa-f]+)> Tj",
        RegexOptions.Compiled);

    /// <summary>每个含文本的流（= 每页）里，逐条 Tj 的落点（x, y, 字形串）。Td 是相对位移，逐条累加。</summary>
    static List<(int idx, List<(double x, double y, string hex)> ops)> ContentStreams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var result = new List<(int, List<(double, double, string)>)>();
        int idx = 0;
        // (?<!end) 很要紧：裸找 "stream" 会连 "endstream" 里的 stream 一起命中，
        // 于是隔一个流才解一个，页面数直接少一半。
        foreach (Match sm in Regex.Matches(text, @"(?<!end)stream\r?\n"))
        {
            int start = sm.Index + sm.Length;
            int end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;
            var inflated = Inflate(pdf, start, end);
            if (inflated != null && inflated.Contains("Tj"))
            {
                double x = 0, y = 0;
                var ops = new List<(double, double, string)>();
                foreach (Match m in ShowOp.Matches(inflated))
                {
                    if (m.Groups["tx"].Success) { x = double.Parse(m.Groups["tx"].Value, Inv); y = double.Parse(m.Groups["ty"].Value, Inv); }
                    else if (m.Groups["dx"].Success) { x += double.Parse(m.Groups["dx"].Value, Inv); y += double.Parse(m.Groups["dy"].Value, Inv); }
                    else ops.Add((Math.Round(x, 2), Math.Round(y, 2), m.Groups["hex"].Value));
                }
                if (ops.Count > 0) result.Add((idx, ops));
                idx++;
            }
        }
        return result;
    }

    static readonly System.IFormatProvider Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>PDF 的 FlateDecode = zlib（2 字节头 + deflate）。解不开（图片/未压缩流）返回 null 跳过。</summary>
    static string? Inflate(byte[] pdf, int start, int end)
    {
        try
        {
            using var ms = new MemoryStream(pdf, start + 2, end - start - 2);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var sr = new StreamReader(deflate, Encoding.Latin1);
            return sr.ReadToEnd();
        }
        catch { return null; }
    }
}
