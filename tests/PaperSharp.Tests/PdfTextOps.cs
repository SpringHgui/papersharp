using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PaperSharp.Tests;

/// <summary>
/// 把一份 PDF 的**内容流**摊成逐条文本落笔记录（<c>Tm</c>/<c>Td</c> 累加出的 x, y + 该条 <c>Tj</c> 的字形串）。
///
/// 走内容流而不是 PdfPig 的 Letters：后者会把透明层（clip-path / mask-image 生成的 Form XObject）
/// 摊平回页面，凭空报出同位重复。<see cref="TablePaintDedupTests"/> 用的是同一套判据，这里抽出来给
/// 「凡落笔处都得付字距账」那组哨兵复用。
/// </summary>
static class PdfTextOps
{
    static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;

    static readonly Regex ShowOp = new(
        @"1 0 0 -1 (?<tx>[-\d.]+) (?<ty>[-\d.]+) Tm|(?<dx>[-\d.]+) (?<dy>[-\d.]+) Td|<(?<hex>[0-9A-Fa-f]+)> Tj",
        RegexOptions.Compiled);

    public record struct Op(double X, double Y, string Hex);

    /// <summary>整份 PDF 里所有含文本的流（≈每页一条）的落笔序列。</summary>
    public static List<List<Op>> Pages(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var result = new List<List<Op>>();
        // (?<!end) 很要紧：裸找 "stream" 会连 "endstream" 一起命中，隔一个流才解一个。
        foreach (Match sm in Regex.Matches(text, @"(?<!end)stream\r?\n"))
        {
            int start = sm.Index + sm.Length;
            int end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;
            var inflated = Inflate(pdf, start, end);
            if (inflated is not { Length: > 0 } || !inflated.Contains("Tj")) continue;

            double x = 0, y = 0;
            var ops = new List<Op>();
            foreach (Match m in ShowOp.Matches(inflated))
            {
                if (m.Groups["tx"].Success)
                {
                    x = double.Parse(m.Groups["tx"].Value, Inv);
                    y = double.Parse(m.Groups["ty"].Value, Inv);
                }
                else if (m.Groups["dx"].Success)
                {
                    x += double.Parse(m.Groups["dx"].Value, Inv);
                    y += double.Parse(m.Groups["dy"].Value, Inv);
                }
                else ops.Add(new Op(Math.Round(x, 2), Math.Round(y, 2), m.Groups["hex"].Value));
            }
            if (ops.Count > 0) result.Add(ops);
        }
        return result;
    }

    /// <summary>
    /// 按基线 y 归堆，只留「同一条基线上从左到右推进的多个落点」——即一串被切开落笔的文本。
    /// HarfBuzz 整段造型（shaped）那几条落在 x=0 的原点堆里，天然被这条严格递增的判据滤掉。
    /// </summary>
    public static List<List<Op>> KernedLines(byte[] pdf, int minChunks = 2)
        => Pages(pdf).SelectMany(page => page.GroupBy(op => op.Y))
            .Where(g => g.Count() >= minChunks)
            .Select(g => g.OrderBy(op => op.X).ToList())
            .Where(g => g.Zip(g.Skip(1), (a, b) => b.X > a.X + 0.01).All(ok => ok))
            .ToList();

    /// <summary>逐条落笔之间的推进量序列（首条的绝对落点不算，两处文本的起点本就不同）。</summary>
    public static List<double> Deltas(List<Op> line) =>
        line.Zip(line.Skip(1), (a, b) => Math.Round(b.X - a.X, 2)).ToList();

    /// <summary>PDF 的 FlateDecode = zlib（2 字节头 + deflate）。解不开（图片/未压缩流）返回 null。</summary>
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
