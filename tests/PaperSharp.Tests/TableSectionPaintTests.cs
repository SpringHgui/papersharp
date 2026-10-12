using SkiaSharp;

namespace PaperSharp.Tests;

/// <summary>
/// 表格里两类「叠加视图重影」的哨兵：行组背景 + 等宽链里汉字的字面与推进。
///
/// <para>
/// **行组背景**：m1.css 的 <c>thead{background:#e8f0f8}</c> 在引擎里整条不落地，Chrome 有。
/// 根因是行组盒被当透传容器用（<c>Renderer.CollectBoxes</c> 明写「行组盒自己从不被定位，几何恒 0」），
/// 而光栅那趟画师按盒树遍历（<c>Painter.PaintFlow</c> 先画自己再画子），0×0 的盒什么底色都画不出来。
/// 分页那趟另走 <c>Paginator</c> 摊平的 DrawItem 序列，表头要跟着每页顶部重复，两条路各修各的、也各测各的。
/// 代价：m1 两条表头带 = 403,653 个 2x 差像素，占全图差像素（1,610,977）的 **25.1%**，修后 1,232,726。
/// 注意这条不进「1x 可见重影 %」——pixdiff 那个指标只数**有墨**像素，纯底色不在分子里，
/// 它当时纹丝不动（21.36% → 21.14%），别拿它当这条修复的证据。
/// </para>
///
/// <para>
/// **等宽链的字面与推进**：<c>.num{font-family:"Consolas",monospace}</c> 里的汉字，度量端一直沿声明列表
/// 找（<c>LineExtents → FaceFromList</c>）落到候选链首的 NSimSun，绘制端却只拿主面去问
/// <c>SKFontManager.MatchCharacter</c> → 画成**黑体**，而 Blink 画**宋体**。同一段字两张面。
/// 推进另一半：Chrome 把 generic monospace 落到 NSimSun 后按整「半格」走位（13.328125px 全宽粗体汉字
/// 每字 ⌈fs/2⌉×2+1 = 15px），布局端 <c>MonoGridWidth</c> 早就算成 15，绘制端仍吃字面自己的 hmtx
/// （1em = 13.328）→ 布局以为 30px 的一格只画 26.66px，右对齐的 .num 格里逐字往左累积。
/// </para>
/// </summary>
public class TableSectionPaintTests
{
    const byte BandR = 232, BandG = 240, BandB = 248;   // m1.css: thead { background: #e8f0f8 }

    /// <summary>数一张 PNG 里等于表头底色的像素。</summary>
    static int BandPixels(byte[] png)
    {
        using var img = SKImage.FromEncodedData(png);
        Assert.NotNull(img);
        using var bmp = SKBitmap.FromImage(img);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red == BandR && c.Green == BandG && c.Blue == BandB) n++;
            }
        return n;
    }

    [Fact]
    public void Raster_paints_thead_background()
    {
        int n = BandPixels(Fixture.RenderPng("m1"));
        // 两张表各一条 thead：794 宽下带子约 1361×87 设备像素 ×2 ≈ 23.7 万，扣掉压在下面的字与格边框。
        // 修前实测 0（整条不落地），所以这个断言退回改动必红。
        Assert.True(n > 100_000, $"m1 光栅里 thead 底色只有 {n} 像素——行组背景又没落地");
    }

    [Fact]
    public void Paginated_pdf_paints_repeated_thead_background()
    {
        var pdf = Fixture.RenderPdf("m1");
        // 引擎把色值写成 4 位小数且省掉前导 0：#e8f0f8 → 「.9098 .9412 .9725 rg」。
        // 分页路径每页顶部重复表头，所以这条填充至少出现两次。
        int hits = InflateStreams(pdf)
            .Sum(t => System.Text.RegularExpressions.Regex.Matches(
                t, @"0?\.909\d?\s+0?\.941\d?\s+0?\.972\d?\s+rg").Count);
        Assert.True(hits >= 2, $"PDF 内容流里表头底色只出现 {hits} 次——续页顶部重复的 thead 背景没跟着铺");
    }

    /// <summary>
    /// 等宽链里汉字的**落点字面**必须是 NSimSun（宋体），不能是系统 generic 回退的黑体。
    /// </summary>
    [Fact]
    public void Monospace_cjk_paints_with_nsimsum_not_system_sans()
    {
        var consolas = PaperSharp.Layout.FontManager.Get("Consolas", 700);
        var face = PaperSharp.Layout.TextShaper.ResolveTypeface("序", consolas, "\"Consolas\", monospace", 700);
        Assert.Equal("NSimSun", face.FamilyName);
    }

    /// <summary>探针页：一行 <c>.num</c> 那套声明下的粗体中文。</summary>
    const string MonoProbe =
        "<p style=\"font-family:'Consolas',monospace;font-size:13.328125px;font-weight:700\">序号 数量 单价</p>";

    /// <summary>端到端钉住「绘制端确实沿声明列表找字面」：PDF 字体字典里嵌进去的必须是 NSimSun。
    /// 上面那条单测只钉解析函数，这条钉接线——以前绘制端只拿主面问系统，嵌的是黑体那张面。</summary>
    [Fact]
    public void Monospace_cjk_embeds_nsimsum_in_the_pdf()
    {
        var text = System.Text.Encoding.Latin1.GetString(
            Renderer.RenderPdf(MonoProbe, "", baseDir: Fixture.AssetsDir));
        Assert.Contains("NSimSun", text);
        Assert.DoesNotContain("NotoSansSC", text);
        Assert.DoesNotContain("MicrosoftYaHei", text);
    }

    /// <summary>推进判据：同一探针页里相邻两个汉字的落点间隔必须是半格网格的 15.0，
    /// 不是字面 hmtx 的 1em=13.328125。修前实测 13.328（6 对全是），修后 15.0。</summary>
    [Fact]
    public void Monospace_cjk_advance_uses_the_half_cell_grid()
    {
        var streams = InflateStreams(Renderer.RenderPdf(MonoProbe, "", baseDir: Fixture.AssetsDir));
        var gaps = new List<float>();
        foreach (var stream in streams)
        {
            float? prevX = null, prevY = null;
            foreach (var m in System.Text.RegularExpressions.Regex.Matches(
                         stream, @"1 0 0 -1 ([\d.]+) ([\d.]+) Tm\s*\n<[0-9A-Fa-f]{4}> Tj").AsEnumerable())
            {
                float x = float.Parse(m.Groups[1].Value, Inv), y = float.Parse(m.Groups[2].Value, Inv);
                // 只认全宽候选：半宽（等宽空格 7.33、拉丁 6~8）与词间空白都不算，否则最小间隔永远是空格
                if (prevY != null && Math.Abs(prevY.Value - y) < 0.01f && x - prevX!.Value > 10f)
                    gaps.Add(x - prevX.Value);
                prevX = x; prevY = y;
            }
        }
        Assert.NotEmpty(gaps);
        float min = gaps.Min();
        Assert.True(min > 14.9f, $"等宽链里相邻汉字的最小间隔是 {min:0.####} —— 绘制端没走半格网格（应为 15.0）");
    }

    static readonly System.IFormatProvider Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>把 PDF 里每条 FlateDecode 流单独解开（内容流是压缩的，直接搜字节搜不到）。</summary>
    static List<string> InflateStreams(byte[] pdf)
    {
        var s = System.Text.Encoding.Latin1.GetString(pdf);
        var outp = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(s, @"(?<!end)stream\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = s.IndexOf("endstream", start, System.StringComparison.Ordinal);
            if (end < 0) continue;
            try
            {
                // PDF 的 FlateDecode = zlib（2 字节头 + raw deflate）；DeflateStream 只吃后半段。
                using var ms = new System.IO.MemoryStream(pdf, start + 2, end - start - 2);
                using var d = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
                outp.Add(new StreamReader(d, System.Text.Encoding.Latin1).ReadToEnd());
            }
            catch { /* 图片流/未压缩流解不开，跳过 */ }
        }
        return outp;
    }
}
