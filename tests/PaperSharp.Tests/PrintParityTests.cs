using Microsoft.Extensions.Logging.Abstractions;
using PaperSharpBlazorApp.Services;

namespace PaperSharp.Tests;

/// <summary>
/// 打印对拍参照的端到端验证：真的拉起无头 Edge 出参照，与引擎逐盒对拍。
/// 需要 node + Edge（秒级、吃一份浏览器），默认跳过；手动 `PP_E2E=1 dotnet test --filter PrintParity` 才跑。
/// 它钉的是台账 P0-10 的结论：**无头(Skia) 参照 == 引擎打印输出，逐位对齐**——有头 iframe 才漂。
/// </summary>
public class PrintParityTests
{
    [Fact]
    public async Task Headless_reference_matches_engine_golden_for_challenge()
    {
        if (Environment.GetEnvironmentVariable("PP_E2E") != "1") return;   // 无浏览器环境不阻塞常规 CI

        var samples = new PlaygroundSamples(NullLogger<PlaygroundSamples>.Instance);
        var svc = new PlaygroundService(samples, NullLogger<PlaygroundService>.Instance);
        var read = samples.Read("challenge");
        Assert.NotNull(read);
        var (html, css) = read!.Value;

        var render = await svc.RenderAsync(html, css, 1284, 2, default);
        var pp = await svc.PrintParityAsync(html, css, render.CssWidth, 2,
            (render.PageMarginTop, render.PageMarginRight, render.PageMarginBottom, render.PageMarginLeft),
            default);
        Assert.True(pp.Rects.Count > 150, $"无头参照矩形过少：{pp.Rects.Count}（多半是没加载到 challenge 正文）");
        Assert.NotNull(svc.PeekImage(pp.Token));   // 参照 PNG 落地、可经 image 端点取

        // 权威判据：无头(Skia)参照 == 引擎**金标**输出（boxes-challenge.json，与 sweep.sh 同源、走 bin 资产）。
        // 不对 svc.Diff(render.Token) 判：测试里 PlaygroundSamples 从 test bin 上溯到 **src** 资产，而真机
        // Playground 与 CLI 都用 **bin** 资产（AppContext.BaseDirectory/assets 存在）；src/bin 的字体注册态
        // 会让含 light/small-caps 的行内 run 漂 ~18px——那是测试 harness 的资产路径差，不是参照物本身的问题。
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PaperSharpSamples", "bin", "Release", "net10.0", "output", "boxes-challenge.json");
        if (!File.Exists(goldenPath)) return;   // 没跑过 boxes 就没金标，跳过别误红
        var golden = System.Text.Json.JsonSerializer.Deserialize<GoldenDoc>(File.ReadAllText(goldenPath),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var gm = golden.Boxes.ToDictionary(b => b.DomPath, b => b);

        int drift = 0; float worst = 0; string worstPath = "";
        foreach (var r in pp.Rects)
        {
            if (!gm.TryGetValue(r.DomPath, out var g)) continue;
            float m = Math.Max(Math.Max(Math.Abs(r.X - g.X), Math.Abs(r.Y - g.Y)),
                               Math.Max(Math.Abs(r.W - g.W), Math.Abs(r.H - g.H)));
            if (m > 0.5f) { drift++; if (m > worst) { worst = m; worstPath = r.DomPath; } }
        }
        Assert.True(drift == 0, $"无头参照与引擎金标差 {drift} 盒（最坏 {worst:F2}px @ {worstPath}）——打印对拍参照应逐位对齐（P0-10）");
    }

    sealed class GoldenDoc { public List<GoldenBox> Boxes { get; set; } = new(); }
    sealed class GoldenBox
    {
        public string DomPath { get; set; } = "";
        public string Tag { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float W { get; set; }
        public float H { get; set; }
    }
}
