using System.Runtime.InteropServices;
using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Paint;

namespace PaperSharp.Tests;

/// <summary>
/// 「保真度阶段4/5」回归：多层与 inset 的 box-shadow，以及 CSS 边框的几何（斜接梯形 / 明暗分带 / 虚线段长）。
///
/// 对齐 Chrome 的口径：
/// ① 阴影一层 = 最多四个长度值 <c>dx dy blur spread</c>，剩下的 token 才是颜色。<b>blur 与 spread 不能串位</b>：
///    AngleSharp 会把作者写的 <c>0 0 0 2px</c> 重新序列化成 <c>0 0 2px</c>（computed 与 declared 视图都中招），
///    照它画就会把 2px 的实心外环变成 2px 的模糊——所以 StyleResolver 读的是作者原样串。
/// ② inset 层的形状与裁切都贴着 <b>padding box</b>（外阴影才贴 border box），且多层按作者书写序、第一条在最上层。
/// ③ 边框不是四个重叠矩形，而是每边一个斜接梯形：外缘占满整条边、内缘按邻边收缩，四角 45° 相接。
///    只有这套几何同时解释得通「0×0 盒 + 三条透明边 = CSS 三角形」与 groove/ridge 的明暗分界线。
/// ④ 明暗色：groove 上/左外半暗、内半亮（ridge 相反）；inset 上/左整边暗、下/右亮（outset 相反）。
/// ⑤ 虚线：dashed 段长 2×宽、间隙 1×宽；dotted 圆点直径=宽、中心距 2×宽（Chrome 2px/4px/8px 三档实测）。
///
/// 断言不吃金标、不依赖字体：解析层直接调用公开的 <see cref="CssValueParser.ParseShadowLayers"/>，
/// 绘制层自建 SKSurface 走 BoxPainter.PaintDecor 后读像素。
/// </summary>
public class ShadowAndBorderParityTests
{
    // ── 公共设施 ───────────────────────────────────────────────────────────────

    /// <summary>解析一段声明，取回那个 &lt;div&gt; 的 ComputedStyle（作者 CSS 的正路，含原始 CSS 兜底表）。</summary>
    static ComputedStyle Style(string decls)
    {
        var root = StyleResolver.BuildDocument("<div class=s></div>", ".s{" + decls + "}");
        ComputedStyle? hit = null;
        void Walk(ComputedStyle n)
        {
            if (hit == null && n.Tag.Equals("div", StringComparison.OrdinalIgnoreCase)) hit = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(root);
        return hit ?? throw new InvalidOperationException($"样式树里没有 <div>（声明：{decls}）");
    }

    /// <summary>把样式装饰画进一张白底 surface，返回 (x,y)→像素 的探针（一次拷贝，多次读）。</summary>
    static Func<int, int, SKColor> Decorate(string decls, SKRect rect, int w = 100, int h = 100)
    {
        var s = Style(decls);
        s.ResolveBoxSizes(200);       // padding 的 used px 由布局期 Measure 落下，PaintDecor 路径同样需要
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        surface.Canvas.Clear(SKColors.White);
        BoxPainter.PaintDecor(surface.Canvas, rect, s);
        return Grab(surface);
    }

    /// <summary>SKSurface → RGBA8888（非预乘）缓冲 + 像素探针，与 Fixture.Rgba 同一口径。</summary>
    static Func<int, int, SKColor> Grab(SKSurface surface)
    {
        using var image = surface.Snapshot();
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var buf = new byte[info.BytesSize];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            Assert.True(image.ReadPixels(info, h.AddrOfPinnedObject(), info.RowBytes), "读像素失败");
        }
        finally { h.Free(); }
        return (x, y) =>
        {
            int i = y * info.RowBytes + x * 4;
            return new SKColor(buf[i], buf[i + 1], buf[i + 2], buf[i + 3]);
        };
    }

    /// <summary>容差比较：抗锯齿会在形状边界让出混合色，所以只探离边界 ≥2px 的像素。</summary>
    static bool Near(SKColor c, SKColor want, int tol = 24)
        => Math.Abs(c.Red - want.Red) <= tol && Math.Abs(c.Green - want.Green) <= tol && Math.Abs(c.Blue - want.Blue) <= tol;

    static readonly SKColor Red = new(255, 0, 0);
    static readonly SKColor Green = new(0, 255, 0);
    static readonly SKColor Blue = new(0, 0, 255);
    static readonly SKColor Yellow = new(255, 255, 0);
    static readonly SKColor Black = new(0, 0, 0);
    static readonly SKColor White = new(255, 255, 255);

    // ── 1. 多层 / inset box-shadow 的解析 ──────────────────────────────────────

    [Fact]
    public void Three_layers_keep_blur_and_spread_in_their_own_slots()
    {
        // 任务钉的那串：环 + 外投影 + 内投影。AngleSharp 重序列化后 `0 0 0 2px` 会变成 `0 0 2px`，
        // 这里按作者原样串解析：第一层必须是 blur=0 / spread=2（实线外环），不是 blur=2 / spread=0。
        var layers = CssValueParser.ParseShadowLayers(
            "0 0 0 2px #fff, 0 6px 12px rgba(0,0,0,.2), inset 0 2px 4px rgba(0,0,0,.3)", 16f);
        Assert.NotNull(layers);
        Assert.Equal(3, layers!.Count);

        Assert.Equal(0, layers[0].OffsetX, 3);
        Assert.Equal(0, layers[0].OffsetY, 3);
        Assert.Equal(0, layers[0].Blur, 3);
        Assert.Equal(2, layers[0].Spread, 3);
        Assert.False(layers[0].Inset);
        Assert.True(Near(layers[0].Color, White), "#fff 应折成纯白不透明");

        Assert.Equal(0, layers[1].OffsetX, 3);
        Assert.Equal(6, layers[1].OffsetY, 3);
        Assert.Equal(12, layers[1].Blur, 3);
        Assert.Equal(0, layers[1].Spread, 3);
        Assert.False(layers[1].Inset);
        Assert.Equal(0, layers[1].Color.Red);
        Assert.Equal(0, layers[1].Color.Green);
        Assert.Equal(0, layers[1].Color.Blue);
        Assert.InRange((int)layers[1].Color.Alpha, 50, 52);   // rgba 的 .2 → round(.2*255)=51

        Assert.True(layers[2].Inset, "inset 关键字必须落到该层自己的标志位上，不能整串共用");
        Assert.Equal(0, layers[2].OffsetX, 3);
        Assert.Equal(2, layers[2].OffsetY, 3);
        Assert.Equal(4, layers[2].Blur, 3);
        Assert.Equal(0, layers[2].Spread, 3);
        Assert.InRange((int)layers[2].Color.Alpha, 75, 77);   // .3 → 76/77
    }

    [Fact]
    public void Layer_order_is_the_authored_order()
    {
        // Chrome：box-shadow 的层序就是书写序，先写的画在上面。引擎内部倒序铺（见 PaintDecor 的注释），
        // 但**数据表**必须保持作者顺序——否则任何按索引消费它的通路（SVG/PDF 描述）都会层序颠倒。
        var layers = CssValueParser.ParseShadowLayers("0 0 0 1px #ff0000, inset 0 0 0 2px #00ff00", 16f);
        Assert.Equal(2, layers!.Count);
        Assert.False(layers[0].Inset);
        Assert.True(Near(layers[0].Color, Red));
        Assert.True(layers[1].Inset);
        Assert.True(Near(layers[1].Color, Green));
    }

    [Fact]
    public void Engine_reads_the_authored_string_so_spread_does_not_become_blur()
    {
        // 这是 CSS 规则路径：AngleSharp 的 computed/declared 视图都会把 `0 0 0 2px` 吐成 `0 0 2px`，
        // StyleResolver 必须以原始 CSS 兜底表为准。哪天有人把它改回读 computed，Blur 会立刻变成 2。
        var s = Style("box-shadow:0 0 0 2px #ffffff");
        Assert.NotNull(s.Shadows);
        Assert.Single(s.Shadows!);
        Assert.Equal(0, s.Shadows![0].Blur, 3);
        Assert.Equal(2, s.Shadows[0].Spread, 3);
        Assert.False(s.Shadows[0].Inset);
    }

    [Fact]
    public void Engine_reads_spread_from_the_inline_style_attribute_too()
    {
        // 内联 style 是作者原样串的第一优先级；Playground 的样例大量用内联，这条路也得钉住。
        var root = StyleResolver.BuildDocument(
            "<div class=s style=\"box-shadow:0 0 0 3px #ff0000, inset 0 4px 8px rgba(0,0,0,.5)\"></div>", ".s{color:red}");
        ComputedStyle? s = null;
        void Walk(ComputedStyle n)
        {
            if (s == null && n.Tag.Equals("div", StringComparison.OrdinalIgnoreCase)) s = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(root);
        Assert.NotNull(s);
        var l = s!.Shadows;
        Assert.Equal(2, l!.Count);
        Assert.Equal(0, l[0].Blur, 3);
        Assert.Equal(3, l[0].Spread, 3);
        Assert.True(Near(l[0].Color, Red));
        Assert.True(l[1].Inset);
        Assert.Equal(4, l[1].OffsetY, 3);
        Assert.Equal(8, l[1].Blur, 3);
        Assert.Equal(0, l[1].Spread, 3);
        Assert.InRange((int)l[1].Color.Alpha, 127, 128);
    }

    [Fact]
    public void Length_units_fold_by_the_element_font_size()
    {
        // em 相对**本元素**字号（Chrome 同口径），rem 相对根字号 16。
        var l = CssValueParser.ParseShadowLayers("0 1em 2em 0.5em #000", 10f)![0];
        Assert.Equal(10, l.OffsetY, 3);
        Assert.Equal(20, l.Blur, 3);
        Assert.Equal(5, l.Spread, 3);
        var r = CssValueParser.ParseShadowLayers("0 0 0 2rem #000", 10f)![0];
        Assert.Equal(32, r.Spread, 3);
    }

    [Fact]
    public void Negative_spread_stays_signed_and_none_clears_the_layers()
    {
        // 负 spread 是常见的「内收一圈」写法（-3px 就是让阴影比盒小 3px）：折成 blur 会让方向整个丢掉。
        var l = CssValueParser.ParseShadowLayers("0 2px 0 -3px #000", 16f)![0];
        Assert.Equal(0, l.Blur, 3);
        Assert.Equal(-3, l.Spread, 3);
        Assert.Null(CssValueParser.ParseShadowLayers("none", 16f));
        Assert.Null(CssValueParser.ParseShadowLayers("", 16f));
        Assert.Null(CssValueParser.ParseShadowLayers("5px", 16f));      // 不足两个长度值 → 非法，整层丢弃
    }

    [Fact]
    public void Missing_color_falls_back_to_the_translucent_black_default()
    {
        // `0 0 6px`（没写颜色）在 Chrome 里是 initial color（等价 currentColor 的黑）；
        // 引擎用半透明黑兜底，这里钉住「没颜色不至于整层消失」。
        var l = CssValueParser.ParseShadowLayers("0 0 6px", 16f)![0];
        Assert.Equal(6, l.Blur, 3);
        Assert.Equal(0, l.Spread, 3);
        Assert.True(l.Color.Alpha > 0);
    }

    // ── 2. 阴影的绘制：inset 贴 padding box、外阴影按 spread/offset 成形 ─────────

    [Fact]
    public void Inset_shadow_rides_the_padding_box_not_the_border_box()
    {
        // 盒 100×100、20px 绿边框 → padding box 是 (20,20)-(80,80)。
        // `inset 0 0 0 6px` 的环 = padding box 往里 6px（x∈[20,26)），于是 (22,50) 必须是红的。
        // 若按 border box 铺，环会落在 x∈[0,6) 被绿边框整个盖住 → (22,50) 只剩白底，这条立刻红。
        var px = Decorate("width:60px;height:60px;border:20px solid #00ff00;background:#ffffff;" +
                          "box-shadow:inset 0 0 0 6px #ff0000", new SKRect(0, 0, 100, 100));
        Assert.True(Near(px(22, 50), Red), "inset 环应贴着 padding box 内缘");
        Assert.True(Near(px(50, 22), Red), "上边同理：环在 padding box 的顶边往下 6px");
        Assert.True(Near(px(60, 50), White), "padding box 中间是洞，不该有阴影");
        Assert.True(Near(px(10, 50), Green), "边框带归边框");
    }

    [Fact]
    public void Outer_shadow_spread_is_a_sharp_ring_not_a_blur()
    {
        // 盒 (20,20)-(80,80) + `0 0 0 5px` → 实心蓝环正好外扩 5px（15..20），外侧 14px 处必须干净是白。
        // blur 与 spread 串位时这里会变成一圈 2.5σ 的高斯羽化：淡、且一直拖到 13 之外，两条断言一起失效。
        var px = Decorate("width:60px;height:60px;background:#ffffff;box-shadow:0 0 0 5px #0000ff",
                          new SKRect(20, 20, 80, 80));
        Assert.True(Near(px(16, 50), Blue), "spread 环应外扩满 5px");
        Assert.True(Near(px(18, 50), Blue));
        Assert.False(Near(px(12, 50), Blue, 60), "环外不该有羽化（blur=0）");
        Assert.True(Near(px(40, 50), White), "环画在背景之下，盒内还是白的");
    }

    [Fact]
    public void Outer_shadow_offset_shifts_the_shadow_box()
    {
        // `8px 8px 0 0` → 阴影盒整体右下移 8px：可见的黑只出现在右边 x∈[60,68] 与下边 y∈[60,68]。
        // dx/dy 读反或把 dx 当 blur 都会让 (63,40) 变白。
        var px = Decorate("width:40px;height:40px;background:#ffffff;box-shadow:8px 8px 0 0 #000000",
                          new SKRect(20, 20, 60, 60));
        Assert.True(Near(px(63, 40), Black), "右下偏移的阴影应出现在盒右侧 8px 带里");
        Assert.True(Near(px(40, 63), Black));
        Assert.True(Near(px(70, 40), White), "偏移量只有 8px，70 已出界");
        Assert.True(Near(px(50, 50), White), "背景压在阴影之上");
    }

    // ── 3. 边框几何：斜接梯形 ───────────────────────────────────────────────────

    [Fact]
    public void Zero_size_box_with_three_transparent_borders_is_a_css_triangle()
    {
        // Chrome 里画 flag/箭头的经典招数：0×0 盒 + 左右 20px 透明边 + 下 30px 实色边 → 一个三角形。
        // 只有「每边一个斜接梯形」的画法能得到它：下边的外缘占满 0..40，两个内缘点都被邻边收缩到 (20,0)。
        // 重叠矩形画法在这里会把整条 40×30 的带子涂满，角点不可能是白的。
        var s = Style("width:0;height:0;border-left:20px solid transparent;" +
                      "border-right:20px solid transparent;border-bottom:30px solid #ff0000");
        // 前提：透明边确实按「宽 20 + alpha 0」落进样式（宽度决定斜接线，颜色决定不落地）
        Assert.Equal(20, s.BorderWidthLeft, 3);
        Assert.Equal(20, s.BorderWidthRight, 3);
        Assert.Equal(30, s.BorderWidthBottom, 3);
        Assert.Equal(0, s.BorderWidthTop, 3);
        Assert.Equal(0, s.BorderColorLeft.Alpha);
        Assert.Equal(0, s.BorderColorRight.Alpha);

        using var surface = SKSurface.Create(new SKImageInfo(40, 30));
        surface.Canvas.Clear(SKColors.White);
        BoxPainter.PaintDecor(surface.Canvas, new SKRect(0, 0, 40, 30), s);
        var px = Grab(surface);
        Assert.True(Near(px(20, 28), Red), "底边中点：三角形的底");
        Assert.True(Near(px(20, 3), Red), "顶点附近仍应在斜接出的窄条里");
        Assert.True(Near(px(15, 15), Red), "半高处宽度是底的一半（10..30）");
        Assert.False(Near(px(5, 15), Red, 60), "半高处左边 10px 外是透明边让出来的斜角");
        Assert.False(Near(px(35, 15), Red, 60), "同理右侧");
        Assert.True(Near(px(1, 1), White), "盒角必须在三角形外");
        Assert.True(Near(px(38, 1), White));
    }

    [Fact]
    public void Adjacent_side_colors_meet_on_a_45_degree_miter()
    {
        // 四边各一色：CSS 的四角是 45° 斜接（每边一个梯形），不是「后画的边压住整块角」。
        // 于是左上角沿 y=x 分开：偏上的像素是上边色，偏左的是左边色。
        var px = Decorate("border-width:10px;border-style:solid;border-top-color:#ff0000;" +
                          "border-right-color:#00ff00;border-bottom-color:#0000ff;border-left-color:#ffff00",
                          new SKRect(0, 0, 50, 50));
        Assert.True(Near(px(25, 5), Red), "上边中段");
        Assert.True(Near(px(45, 25), Green), "右边中段");
        Assert.True(Near(px(25, 45), Blue), "下边中段");
        Assert.True(Near(px(5, 25), Yellow), "左边中段");
        Assert.True(Near(px(4, 1), Red), "左上角斜接线上半：属于上边");
        Assert.True(Near(px(1, 4), Yellow), "左上角斜接线下半：属于左边");
        Assert.True(Near(px(48, 6), Green), "右上角斜接线外半：属于右边（不是被上边整块盖住）");
    }

    [Fact]
    public void Groove_and_ridge_split_the_band_into_light_and_dark_halves()
    {
        // CSS 2.1 §8.5 + Chrome 实测：光源在左上。groove 的上边「外半暗、内半亮」，下边相反；
        // ridge 是 groove 的逐半取反。12px 带宽 → 分界在 6px，故 (25,3) 与 (25,9) 必须一深一浅。
        // 基准色取 #808080：引擎按 (max±84)/max 等比缩放 → 暗 44 / 亮 212，和 128 三者分得开。
        var g = Decorate("border:12px groove #808080", new SKRect(0, 0, 50, 50));
        var dark = new SKColor(44, 44, 44);
        var light = new SKColor(212, 212, 212);
        Assert.True(Near(g(25, 3), dark), "groove 上边外半应压暗（朝光边先暗才像凹槽）");
        Assert.True(Near(g(25, 9), light), "groove 上边内半提亮");
        Assert.True(Near(g(25, 41), dark), "groove 下边内半暗");
        Assert.True(Near(g(25, 47), light), "groove 下边外半亮");

        var r = Decorate("border:12px ridge #808080", new SKRect(0, 0, 50, 50));
        Assert.True(Near(r(25, 3), light), "ridge 是 groove 的反相");
        Assert.True(Near(r(25, 9), dark));
        Assert.True(Near(r(25, 47), dark));
        Assert.True(Near(r(25, 41), light));
    }

    [Fact]
    public void Inset_and_outset_borders_are_one_shade_per_side()
    {
        // 与 groove/ridge 不同，inset/outset 每边只有一色：inset 的上/左整边暗、下/右整边亮（outset 相反）。
        var i = Decorate("border:10px inset #808080", new SKRect(0, 0, 50, 50));
        var dark = new SKColor(44, 44, 44);
        var light = new SKColor(212, 212, 212);
        Assert.True(Near(i(25, 5), dark), "inset 上边暗");
        Assert.True(Near(i(5, 25), dark), "inset 左边暗");
        Assert.True(Near(i(25, 45), light), "inset 下边亮");
        Assert.True(Near(i(45, 25), light), "inset 右边亮");

        var o = Decorate("border:10px outset #808080", new SKRect(0, 0, 50, 50));
        Assert.True(Near(o(25, 5), light), "outset 与 inset 逐边相反");
        Assert.True(Near(o(5, 25), light));
        Assert.True(Near(o(25, 45), dark));
        Assert.True(Near(o(45, 25), dark));
    }

    [Fact]
    public void Double_border_is_two_thirds_of_the_band_and_a_third_of_gap()
    {
        // CSS：double 把带宽分成三份——外 1/3 与内 1/3 是线，中间 1/3 空。12px 带 → 0..4 红、4..8 空、8..12 红。
        // 背景铺白才能看出「中间那份是空的」，而不是被底色填实。
        var px = Decorate("border:12px double #ff0000;background:#ffffff", new SKRect(0, 0, 50, 50));
        Assert.True(Near(px(25, 2), Red), "外 1/3 是一条线");
        Assert.True(Near(px(25, 6), White), "中间 1/3 必须是空的");
        Assert.True(Near(px(25, 10), Red), "内 1/3 是另一条线");
    }

    [Fact]
    public void Dashed_segments_are_two_widths_long_with_one_width_gaps()
    {
        // Chrome 实测（2px/4px/8px 三档）：dashed 段长 = 2×宽、间隙 = 1×宽，理想周期 3×宽。
        // 这里 w=4、上边内沿长 96 → 整数化成 8 段：on=8、off=4，故 x=3 红、10 白、16 红、22 白、28 红。
        // 段/间隙比例一旦被改成 1:1 或周期算错，第二个红段与第二个白点会同时对不上。
        var px = Decorate("width:100px;height:20px;border-top:4px dashed #ff0000", new SKRect(0, 0, 100, 24));
        Assert.True(Near(px(3, 2), Red), "第一段（0..8）");
        Assert.True(Near(px(10, 2), White), "第一个间隙（8..12）= 1×宽");
        Assert.True(Near(px(16, 2), Red), "第二段（12..20）");
        Assert.True(Near(px(22, 2), White), "第二个间隙（20..24）");
        Assert.True(Near(px(28, 2), Red), "第三段（24..32）");
    }

    [Fact]
    public void Dotted_dots_repeat_every_two_widths()
    {
        // dotted：圆点直径 = 带宽、中心距 = 2×宽（Chrome 不做整数化，恒为 2w）。w=4 → 点心 0/8/16/24。
        // 探 x=8 与 16 是红的、间隙 x=4 与 12 是白的，就把「点距 2w」钉死了。
        var px = Decorate("width:100px;height:20px;border-top:4px dotted #ff0000", new SKRect(0, 0, 100, 24));
        Assert.True(Near(px(8, 2), Red), "第二个点（点心 x≈8）");
        Assert.True(Near(px(16, 2), Red), "第三个点（点心 x≈16）→ 中心距正好 2×宽");
        Assert.False(Near(px(4, 2), Red, 60), "两点之间是空的");
        Assert.False(Near(px(12, 2), Red, 60), "同上");
    }

    [Fact]
    public void Solid_border_fills_the_whole_band()
    {
        // 对照组：solid 时带宽 0..12 全是红——上面 groove/double 的「分半 / 分三份」才有对照基线，
        // 否则一旦 PaintBorder 整条边不画，那些「某半是暗的」断言会假绿。
        var px = Decorate("border:12px solid #ff0000", new SKRect(0, 0, 50, 50));
        for (int y = 1; y < 12; y += 2) Assert.True(Near(px(25, y), Red), $"solid 上边 y={y} 应整条实色");
        Assert.True(Near(px(25, 25), White), "边框带以外不该有红");
    }
}
