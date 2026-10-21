using System.Security.Cryptography;

namespace PaperSharp.Tests;

/// <summary>
/// 并发安全化验收：多线程同时走公共渲染入口，产物必须与串行基准逐字节一致，
/// 且跨请求（不同 baseDir / 不同文档）不得互相污染。
/// </summary>
public class ConcurrencyTests
{
    const int Threads = 16;

    static byte[] Sha(byte[] b) => SHA256.HashData(b);

    [Theory]
    [InlineData("m1")]
    [InlineData("m3")]
    [InlineData("table")]
    public void ParallelPng_MatchSequentialBytes(string name)
    {
        var reference = Fixture.RenderPng(name);
        var results = new byte[Threads][];
        Parallel.For(0, Threads, i => results[i] = Fixture.RenderPng(name));
        for (int i = 0; i < Threads; i++)
            Assert.True(results[i]!.AsSpan().SequenceEqual(reference),
                $"{name}: 第 {i} 号线程输出与串行基准不一致（{results[i]!.Length} vs {reference.Length} 字节）");
    }

    [Fact]
    public void InterleavedDocs_NoCrossPollution()
    {
        // 不同文档并发交错渲染：各自输出必须等于自己的串行基准（元数据/字体/变量索引曾走静态态串味）
        var names = new[] { "invoice", "m1", "m3", "inline" };
        var references = names.ToDictionary(n => n, Fixture.RenderPng);
        var mismatches = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(
            Enumerable.Range(0, Threads).SelectMany(r => names),
            new ParallelOptions { MaxDegreeOfParallelism = Threads },
            name =>
            {
                var got = Fixture.RenderPng(name);
                if (!Sha(got).SequenceEqual(Sha(references[name]))) mismatches.Add(name);
            });
        Assert.Empty(mismatches);
    }

    [Fact]
    public void DifferentBaseDirsConcurrently_NoImageCrossTalk()
    {
        // 同一 src 相对名、不同 baseDir：路径拼接键不同，各拿各的图（旧实现 key 只用裸 src 会串图）
        var (html, css) = Fixture.ReadSample("img");
        var dirA = Fixture.AssetsDir;
        var dirB = Path.Combine(Fixture.AssetsDir, "fonts", ".."); // 同目录不同字符串 → 不同缓存键
        var refA = Renderer.RenderPng(html, css, Fixture.PngWidth, dirA, Fixture.PngScale);
        var results = new byte[Threads][];
        Parallel.For(0, Threads, i =>
            results[i] = Renderer.RenderPng(html, css, Fixture.PngWidth, i % 2 == 0 ? dirA : dirB, Fixture.PngScale));
        foreach (var r in results)
            Assert.True(r.AsSpan().SequenceEqual(refA));
    }

    [Fact]
    public void ParallelPdf_AllProduceValidOutput()
    {
        var names = new[] { "m1", "m5", "long" };
        Parallel.ForEach(names, name =>
        {
            for (int i = 0; i < 4; i++)
            {
                var pdf = Fixture.RenderPdf(name);
                Assert.True(pdf.Length > 1000, $"{name} 第 {i} 轮 PDF 过小");
                Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
            }
        });
    }
}
