using SkiaSharp;
using PaperSharp;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// font-style（斜体）。引擎原先整条属性都没实现：CSS 层不解析 <c>font-style</c>、UA 表里 <c>em/i/cite/
/// var/dfn/address</c> 只声明了 <c>display:inline</c>、<see cref="FontManager"/> 的字面解析连倾斜档都不带
/// （<c>SKFontStyleSlant.Upright</c> 写死），所以 <c>&lt;em&gt;</c> 与 <c>font-style:italic</c> 一律站得笔直
/// —— inline 样例里「斜体强调」在 Playground 叠图上与浏览器差出一整层字形。
///
/// 期望值全部取自无头 Edge 真值（探针 <c>assets/_probe-it.html</c> + canvas 逐行拟合 + CDP
/// <c>CSS.getPlatformFontsForNode</c>，结论抄在下面各条注释里）。
/// </summary>
public class FontStyleTests
{
    static ComputedStyle At(string html, string css, string domPathSuffix)
    {
        var root = StyleResolver.BuildDocument(html, css, domPath: true);
        ComputedStyle? hit = null;
        void Walk(ComputedStyle n)
        {
            if (hit == null && n.DomPath?.EndsWith(domPathSuffix, StringComparison.Ordinal) == true) hit = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(root);
        return hit ?? throw new InvalidOperationException($"树里找不到 {domPathSuffix}");
    }

    // ── 计算值 ────────────────────────────────────────────────────────

    /// <summary>UA 默认：HTML 渲染规范里这六个强调标签是 italic（Edge 实测 computed 一致）。
    /// 注意 <c>span/b/u</c> 不在其内，写进来就是把「斜体」当默认值的常见误解。</summary>
    [Theory]
    [InlineData("<em>x</em>", "em[1]", "italic")]
    [InlineData("<i>x</i>", "i[1]", "italic")]
    [InlineData("<cite>x</cite>", "cite[1]", "italic")]
    [InlineData("<var>x</var>", "var[1]", "italic")]
    [InlineData("<dfn>x</dfn>", "dfn[1]", "italic")]
    [InlineData("<address>x</address>", "address[1]", "italic")]
    [InlineData("<span>x</span>", "span[1]", "normal")]
    [InlineData("<b>x</b>", "b[1]", "normal")]
    [InlineData("<u>x</u>", "u[1]", "normal")]
    public void Ua_default_per_tag(string html, string suffix, string expected)
        => Assert.Equal(expected, At(html, "", suffix).FontStyle);

    [Fact]
    public void Inherits_through_plain_children_and_resets()
    {
        // <em> 里没写样式的 <span>：浏览器把 italic 一路带下去
        Assert.Equal("italic", At("<em>a<span>b</span></em>", "", "span[1]").FontStyle);
        // 子级显式 normal 压回正立
        Assert.Equal("normal", At("<em><span style=\"font-style:normal\">b</span></em>", "", "span[1]").FontStyle);
        // 继承的是原样那一条（oblique 不会被改写成 italic）
        Assert.Equal("oblique", At("<div style=\"font-style:oblique\"><p>x</p></div>", "", "p[1]").FontStyle);
        Assert.Equal("oblique 20deg", At("<div style=\"font-style:oblique 20deg\"><p>x</p></div>", "", "p[1]").FontStyle);
    }

    [Theory]
    [InlineData("font-style:italic", "italic")]
    [InlineData("font-style:oblique", "oblique")]
    [InlineData("font-style:OBLIQUE 14DEG", "oblique 14deg")]      // 大小写与空格归一
    [InlineData("font-style:oblique 20deg", "oblique 20deg")]
    [InlineData("font-style:oblique -20deg", "oblique -20deg")]
    [InlineData("font-style:italic 20deg", "normal")]              // 整条非法 → computed 落回 normal（Edge 实测）
    [InlineData("font-style:", "normal")]
    [InlineData("font-style:bogus", "normal")]
    public void Declaration_normalisation(string rule, string expected)
        => Assert.Equal(expected, At("<div>x</div>", "div{" + rule + "}", "div[1]").FontStyle);

    /// <summary><c>font</c> 简写里的斜体分量（AngleSharp 拆长写，引擎跟着读到）。</summary>
    [Fact]
    public void Font_shorthand_carries_style()
    {
        Assert.Equal("italic", At("<div>x</div>", "div{font:italic 24px sans-serif}", "div[1]").FontStyle);
        Assert.Equal(700, At("<div>x</div>", "div{font:bold italic 24px sans-serif}", "div[1]").FontWeight);
        Assert.Equal("normal", At("<div>x</div>", "div{font:24px sans-serif}", "div[1]").FontStyle);
    }

    /// <summary>伪元素按 CSS 继承宿主：宿主的斜体要落到 <c>::before</c> 的生成内容上，
    /// 反过来 <c>::before</c> 自己写的斜体也不能糊给宿主文字（兜底表已按伪元素分键，
    /// 见 <see cref="PseudoScopeTests"/>）。</summary>
    [Fact]
    public void Pseudo_element_style_is_scoped()
    {
        var host = At("<div class=a>x</div>", ".a::before{content:\"B\";font-style:italic}", "div[1]");
        Assert.Equal("normal", host.FontStyle);
        Assert.Equal("italic", host.Before!.Style.FontStyle);
        var inheriting = At("<em class=a>x</em>", ".a::after{content:\"Z\"}", "em[1]");
        Assert.Equal("italic", inheriting.FontStyle);
        Assert.Equal("italic", inheriting.After!.Style.FontStyle);
    }

    // ── 错切量（浏览器真值）────────────────────────────────────────────

    /// <summary>合成斜体的量：Edge 在 100px/400px 下拟合竖笔左缘，斜率都是 0.24998 ≈ 0.25 整
    /// （= tan 14°，CSS Fonts 4 的 oblique 缺省角）。</summary>
    [Theory]
    [InlineData("italic", 0.25f)]
    [InlineData("oblique", 0.25f)]
    [InlineData("oblique 14deg", 0.25f)]
    [InlineData("normal", 0f)]
    [InlineData("oblique 20deg", 0f)]     // 显式角度：浏览器不合成，竖笔斜率实测 0
    [InlineData("oblique -20deg", 0f)]
    [InlineData("oblique 12deg", 0f)]
    public void Requested_skew_matches_browser(string style, float expected)
        => Assert.Equal(expected, FontManager.RequestedSkew(style), 3);

    /// <summary>打包子集（Noto/雅黑）只有正立面 → 必须合成。浏览器对同一族也是拿正立面合成
    /// （CDP 报的 postScriptName 就是 <c>Noto-Sans-SC</c>），所以这里不能去找系统的 Noto 斜体面。</summary>
    [Fact]
    public void Subset_faces_are_synthesized()
    {
        var noto = FontManager.Get("sans-serif", 400, "italic");
        Assert.False(FontManager.FaceIsSlanted(noto));
        Assert.Equal(FontManager.SyntheticSkew, FontManager.SkewFor("italic", noto), 3);
        var bold = FontManager.Get("Microsoft YaHei", 700, "italic");
        Assert.Equal(FontManager.SyntheticSkew, FontManager.SkewFor("italic", bold), 3);
    }

    /// <summary>判据不能是 <c>SKTypeface.FontSlant</c>：向系统要不到斜体面时 Skia 会把**请求值**回填
    /// 上来（本机 "Noto Sans SC" 要 Italic，有时报 Oblique 有时报 Upright，跨版本不稳），
    /// 而字面真身仍是正立那张（post.italicAngle 仍为 0）。浏览器同理：CDP 报的 postScriptName
    /// 是正立的 <c>Noto-Sans-SC</c>，斜体靠合成。所以这里比对的是**表数据**。</summary>
    [Fact]
    public void Slant_detected_from_face_tables_not_skia_echo()
    {
        using var up = SKTypeface.FromFamilyName("Noto Sans SC",
            new SKFontStyle(SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
        using var it = SKTypeface.FromFamilyName("Noto Sans SC",
            new SKFontStyle(SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Italic));
        if (up == null || it == null) return;                       // 本机没装这族
        static float Angle(SKTypeface t)
        {
            var post = t.GetTableData((uint)((0x70 << 24) | 0x6F << 16 | 0x73 << 8 | 0x74));
            return post is { Length: >= 8 } ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(post.AsSpan(4)) / 65536f : 0f;
        }
        if (Angle(it) == Angle(up))                                 // 两张其实是同一张正立面
            Assert.False(FontManager.FaceIsSlanted(it));
        else                                                        // 系统真有斜体面 → 不合成
            Assert.True(FontManager.FaceIsSlanted(it));
    }

    // ── 布局不变量 ────────────────────────────────────────────────────

    /// <summary>合成斜体只动落笔矩阵，**advance 一字不改**（Edge 实测 italic 与 normal 的逐字
    /// advance 完全相同，Skia 的 TextSkewX 同样不改 MeasureText）。这条守住就说明斜体不需要
    /// 重做任何折行/盒宽口径。</summary>
    [Fact]
    public void Italic_does_not_change_measured_width()
    {
        const string text = "Inline Formatting Context 斜体强调";
        var up = FontManager.Get("sans-serif", 400, "normal");
        var it = FontManager.Get("sans-serif", 400, "italic");
        Assert.Equal(TextShaper.WidthOf(text, up, 26f, 0f), TextShaper.WidthOf(text, it, 26f, 0f), 3);
        Assert.Same(up, it);   // 子集族：斜体压根不换字面，靠合成
    }

    // ── 像素层：斜体真的画斜了没有 ──────────────────────────────────────

    /// <summary>端到端钉住「斜体无效」这一类回归：光看盒矩形永远发现不了（错切不改宽），
    /// 所以直接量渲出来的竖笔斜率。100px 的 | 在 2x 光栅下高约 220 设备像素，
    /// 斜率拟合精度足够（阈值 ±0.03）。</summary>
    [Theory]
    [InlineData("font-style:italic", -0.25f)]
    [InlineData("font-style:oblique", -0.25f)]
    [InlineData("font-style:oblique 20deg", 0f)]
    [InlineData("font-style:normal", 0f)]
    public void Rendered_stem_leans_as_browser_does(string rule, float expectedSlope)
    {
        var png = Renderer.RenderPng(
            "<div style=\"font-family:sans-serif;font-size:100px;" + rule + "\">|</div>",
            "", 300f, null, 2f);
        var slope = StemSlope(png);
        Assert.True(Math.Abs(slope - expectedSlope) < 0.03f,
            $"竖笔斜率 {slope:0.####}，期望 {expectedSlope:0.####}（规则 {rule}）");
    }

    /// <summary>图中竖笔（每行墨宽 &lt; 20 设备像素的行）左缘对 y 的最小二乘斜率。</summary>
    static float StemSlope(byte[] png)
    {
        using var img = SKImage.FromEncodedData(png) ?? throw new InvalidDataException("PNG 解不开");
        using var bm = SKBitmap.FromImage(img);
        int w = bm.Width, h = bm.Height;
        double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = 0;
        for (int y = 0; y < h; y++)
        {
            int lo = -1, hi = -1;
            for (int x = 0; x < w; x++)
            {
                var c = bm.GetPixel(x, y);
                if (c.Red < 128 && c.Green < 128 && c.Blue < 128) { if (lo < 0) lo = x; hi = x; }
            }
            if (lo < 0 || hi - lo >= 20) continue;
            n++; sx += y; sy += lo; sxx += (double)y * y; sxy += (double)y * lo;
        }
        Assert.True(n > 40, $"竖笔行太少（{n}），量不到斜率");
        return (float)((n * sxy - sx * sy) / (n * sxx - sx * sx));
    }

    /// <summary>同一条断言搬到 **PDF 出口**：Skia 把 TextSkewX 编成文字矩阵的剪切分量
    /// （<c>1 0 .25 -1 x y Tm</c>），这里用 PDFium 把成品读回来量竖笔，钉住「PNG 斜了、PDF 没斜」
    /// 这类只在纸面上复活的回归。</summary>
    [Theory]
    [Trait("Category", "Slow")]
    [InlineData("italic", -0.25f)]
    [InlineData("oblique", -0.25f)]
    [InlineData("normal", 0f)]
    public void Pdf_stem_leans_as_browser_does(string rule, float expectedSlope)
    {
        var pdf = Renderer.RenderPdf(
            "<div style=\"font-family:sans-serif;font-size:100px;font-style:" + rule + "\">|</div>", "");
        byte[] png;
        using (var ms = new MemoryStream(pdf))
        {
            using var bmp = PDFtoImage.Conversion.ToImage(ms, 0);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            png = data.ToArray();
        }
        var slope = StemSlope(png);
        Assert.True(Math.Abs(slope - expectedSlope) < 0.03f,
            $"PDF 竖笔斜率 {slope:0.####}，期望 {expectedSlope:0.####}（font-style:{rule}）");
    }
}
