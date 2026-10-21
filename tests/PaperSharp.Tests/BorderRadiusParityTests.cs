using System.Runtime.InteropServices;
using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Paint;

namespace PaperSharp.Tests;

/// <summary>
/// 「保真度阶段5」回归：border-radius 的**每角两轴 + 百分比**，以及替换内容（图片/SVG）的圆角裁剪。
///
/// 对齐 Chrome 的三条口径（都是本阶段踩过才修对的）：
/// ① 百分比的基准是**该盒自己的边框盒**宽/高（<c>border-radius:50%</c> 在 120×76 的盒上就是 60/38，
///    在长方盒上是椭圆而不是胶囊）；AngleSharp 的 computed 会按**视口**宽把 50% 折成定值 px
///    （实测给到 397px），所以 StyleResolver 必须以作者原样串为准，把百分比留成小数、绘制期再折算。
/// ② 简写的角落展开（1~4 值）与 <c>x / y</c> 两列表是 CSS 定义的独立规则，y 列表缺省时复用 x 列表。
/// ③ 替换内容的圆角是**内容盒**轮廓：每轴半径要逐轴减去它外面的边框与内边距、下限 0
///    （Chrome 实测 20px 圆角 + 3px 边框 + 5px 内边距的图，可见圆角是 12px；减到 ≤0 就不裁）。
///
/// 断言一律不吃金标、也不依赖字体度量：样式层直接读 ComputedStyle 的数值，绘制层自建 SKSurface 读角点像素。
/// </summary>
public class BorderRadiusParityTests
{
    /// <summary>解析一段声明，取回那个 &lt;div&gt; 的 ComputedStyle（走作者 CSS 的正路，含原始 CSS 兜底表）。</summary>
    static ComputedStyle Resolve(string decls)
    {
        var root = StyleResolver.BuildDocument("<div class=r></div>", ".r{" + decls + "}");
        ComputedStyle? hit = null;
        void Walk(ComputedStyle n)
        {
            if (hit == null && n.Tag.Equals("div", StringComparison.OrdinalIgnoreCase)) hit = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(root);
        var s = hit ?? throw new InvalidOperationException($"样式树里没有 <div>（声明：{decls}）");
        // padding/margin 的 used px 是布局期 Measure 调 ResolveBoxSizes 才落到 float 字段上的（ClipReplacedContent 读那四个字段）。
        // 本文件的 padding 全是绝对值，基准宽 200 只为复现「Measure 之后」的样式状态。
        s.ResolveBoxSizes(200);
        return s;
    }

    /// <summary>样式里的四个绝对角半径，按 TL/TR/BR/BL 摊平成 (x,y)。</summary>
    static SKPoint[] Abs(ComputedStyle s) => new[]
    {
        new SKPoint(s.BorderRadiusTopLeft, s.BorderRadiusTopLeftY),
        new SKPoint(s.BorderRadiusTopRight, s.BorderRadiusTopRightY),
        new SKPoint(s.BorderRadiusBottomRight, s.BorderRadiusBottomRightY),
        new SKPoint(s.BorderRadiusBottomLeft, s.BorderRadiusBottomLeftY),
    };

    static void AssertCorners(SKPoint[] got, params (float x, float y)[] expect)
    {
        Assert.Equal(expect.Length, got.Length);
        for (int i = 0; i < expect.Length; i++)
        {
            Assert.Equal(expect[i].x, got[i].X, 3);
            Assert.Equal(expect[i].y, got[i].Y, 3);
        }
    }

    /// <summary>百分比八元组（TLx,TLy,TRx,TRy,BRx,BRy,BLx,BLy）；期望传 8 个小数。</summary>
    static void AssertPercent(float[]? pct, params float[] expect)
    {
        Assert.NotNull(pct);
        Assert.Equal(expect.Length, pct!.Length);
        for (int i = 0; i < expect.Length; i++) Assert.Equal(expect[i], pct[i], 4);
    }

    // ── 1. 样式层：每角两轴 + 百分比（作者原样串路径） ─────────────────────────────

    [Fact]
    public void Percent_radius_stays_a_fraction_and_never_folds_to_viewport_px()
    {
        // Chrome：border-radius:50% 计算值仍是 50%（换算发生在绘制期，按边框盒尺寸）。
        // 这里钉两件事：百分比落成 0.5 的小数（八元组全填），且**绝对分量恒为 0**——
        // 一旦哪天改回读 AngleSharp 的 computed，绝对分量就会变成按视口宽折出来的 397px，这条立刻红。
        var s = Resolve("border-radius:50%");
        AssertPercent(s.BorderRadiusPercent, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f);
        AssertCorners(Abs(s), (0, 0), (0, 0), (0, 0), (0, 0));
        Assert.True(BoxPainter.HasRadius(s), "只有百分比的圆角也算「有圆角」，否则绘制期会走直角分支");
    }

    [Fact]
    public void Slash_separates_x_and_y_axes_absolute()
    {
        // `30px / 60px`：x 列表 30、y 列表 60 → 四角都是 (30,60) 的椭圆角。
        var s = Resolve("border-radius:30px / 60px");
        AssertCorners(Abs(s), (30, 60), (30, 60), (30, 60), (30, 60));
        Assert.Null(s.BorderRadiusPercent);   // 一条百分比都没写 → null，不是全零数组
    }

    [Fact]
    public void Four_x_values_with_four_y_values_pair_per_corner()
    {
        // `40px 8px / 8px 40px`：x 顺次 TL/TR/BR/BL = 40/8/40/8，y 顺次 = 8/40/8/40。
        // Chrome 里这就是「叶片/眼睛」那种对角椭圆角的写法，两列表必须按角配对而不是按轴串联。
        var s = Resolve("border-radius:40px 8px / 8px 40px");
        AssertCorners(Abs(s), (40, 8), (8, 40), (40, 8), (8, 40));
        Assert.Null(s.BorderRadiusPercent);
    }

    [Fact]
    public void Mixed_absolute_and_percent_per_axis()
    {
        // `50% 0 / 0 50%`：x 列表 [50%,0] 给 TL/BR=50%、TR/BL=0；y 列表 [0,50%] 给 TR/BL=50%、TL/BR=0。
        // 即 Chrome 上沿的左右两角被裁成椭圆、另外两角保持直角。混合写法最容易被「统一折成 px」实现吃掉。
        var s = Resolve("border-radius:50% 0 / 0 50%");
        AssertPercent(s.BorderRadiusPercent, 0.5f, 0f, 0f, 0.5f, 0.5f, 0f, 0f, 0.5f);
        AssertCorners(Abs(s), (0, 0), (0, 0), (0, 0), (0, 0));
    }

    [Fact]
    public void Single_corner_longhand_takes_two_axes()
    {
        // `border-top-left-radius:10px 20px`：长写法本身就是 x y 两值（缺省 y 同 x）。
        var s = Resolve("border-top-left-radius:10px 20px");
        AssertCorners(Abs(s), (10, 20), (0, 0), (0, 0), (0, 0));
        Assert.Null(s.BorderRadiusPercent);
    }

    [Fact]
    public void Single_corner_longhand_percent_stays_a_fraction()
    {
        // 单角长写法的百分比同样不能被视口折算：25% 在 120×80 的盒上就是 (30,·)。
        // 走内联 style 属性（StyleResolver 的作者原样串第一优先级），与 CSS 规则里的简写同源。
        var root = StyleResolver.BuildDocument(
            "<div class=r style=\"border-bottom-right-radius:25% 10px\"></div>", ".r{color:red}");
        ComputedStyle? s = null;
        void Walk(ComputedStyle n)
        {
            if (s == null && n.Tag.Equals("div", StringComparison.OrdinalIgnoreCase)) s = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(root);
        Assert.NotNull(s);
        Assert.Equal(0, s!.BorderRadiusBottomRight, 3);      // 绝不该是 AngleSharp computed 的 256（=1024×25%）
        Assert.Equal(10, s.BorderRadiusBottomRightY, 3);
        AssertPercent(s.BorderRadiusPercent, 0f, 0f, 0f, 0f, 0.25f, 0f, 0f, 0f);
        // 只有 BR 有 x 轴百分比：绘制期按边框盒 120×80 折成 30，其余角保持原样。
        AssertCorners(BoxPainter.CornerRadii(new SKRect(0, 0, 120, 80), s), (0, 0), (0, 0), (30, 10), (0, 0));
    }

    [Theory]
    [InlineData("border-radius:10px", new[] { 10f, 10f, 10f, 10f })]                       // 1 值 → 四角
    [InlineData("border-radius:10px 20px", new[] { 10f, 20f, 10f, 20f })]                  // 2 值 → TL/BR 与 TR/BL 交替
    [InlineData("border-radius:10px 20px 30px", new[] { 10f, 20f, 30f, 20f })]             // 3 值 → BL 同 TR
    [InlineData("border-radius:10px 20px 30px 40px", new[] { 10f, 20f, 30f, 40f })]        // 4 值 → 顺时针
    public void Shorthand_corner_expansion_follows_css_order(string decl, float[] expect)
    {
        // Chrome（CSS Values 3 §3.1）：1~4 个值的角落展开按「TL / TR|BL / BR」的顺时针规则，
        // 两轴列表各自独立展开。y 列表缺省时整份复用 x 列表（所以下面 x/y 断言同一个数）。
        var s = Resolve(decl);
        AssertCorners(Abs(s), (expect[0], expect[0]), (expect[1], expect[1]), (expect[2], expect[2]), (expect[3], expect[3]));
    }

    // ── 2. 绘制层：CornerRadii 按边框盒折算百分比 ────────────────────────────────

    [Fact]
    public void CornerRadii_folds_percent_by_border_box_size()
    {
        // 任务钉的数：120×76 的盒 + border-radius:50% → 每角 (60,38)。
        // Chrome 的 DevTools 在同样尺寸下算出的 used value 就是 60/38（x 按宽、y 按高，各轴独立）。
        var s = Resolve("border-radius:50%");
        var got = BoxPainter.CornerRadii(new SKRect(0, 0, 120, 76), s);
        AssertCorners(got, (60, 38), (60, 38), (60, 38), (60, 38));
    }

    [Fact]
    public void CornerRadii_adds_absolute_and_percent()
    {
        // 同一角可以同时有绝对值与百分比（`10px 30px / 20%`）：绘制期把百分比折成 px 后**加**到绝对值上。
        // 100×50 的盒：x 列表 TL/BR=10px、TR/BL=30px；y 列表 20% → 每角 +10px。
        var s = Resolve("border-radius:10px 30px / 20%");
        var got = BoxPainter.CornerRadii(new SKRect(0, 0, 100, 50), s);
        AssertCorners(got, (10, 10), (30, 10), (10, 10), (30, 10));
    }

    [Fact]
    public void No_radius_means_zero_radii_and_no_percent()
    {
        var s = Resolve("color:red");
        Assert.Null(s.BorderRadiusPercent);
        Assert.False(BoxPainter.HasRadius(s));
        AssertCorners(BoxPainter.CornerRadii(new SKRect(0, 0, 120, 76), s), (0, 0), (0, 0), (0, 0), (0, 0));
    }

    // ── 3. 替换内容圆角裁剪 ─────────────────────────────────────────────────────

    /// <summary>把一张「纯色图」画进盒里，返回 (x,y)→像素 的探针（一次拷贝，多次读）。</summary>
    static Func<int, int, SKColor> PaintReplaced(ComputedStyle s, SKRect borderRect, SKRect dst, SKColor imageColor)
    {
        var info = new SKImageInfo((int)borderRect.Right, (int)borderRect.Bottom);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        int save = BoxPainter.ClipReplacedContent(canvas, borderRect, dst, s);
        using var p = new SKPaint { Color = imageColor, IsAntialias = true };
        canvas.DrawRect(borderRect, p);      // 故意铺满整个边框盒：能留下来越界像素就说明裁剪没生效
        canvas.RestoreToCount(save);
        return Grab(surface);
    }

    /// <summary>SKSurface → RGBA8888 缓冲 + 像素探针。与 Fixture.Rgba 同一口径（非预乘，读到的就是画进去的颜色）。</summary>
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

    static bool Near(SKColor c, SKColor want, int tol = 24)
        => Math.Abs(c.Red - want.Red) <= tol && Math.Abs(c.Green - want.Green) <= tol && Math.Abs(c.Blue - want.Blue) <= tol;

    [Fact]
    public void Replaced_content_is_clipped_to_content_box_radius()
    {
        // Chrome：图片的可见轮廓是**内容盒**圆角，半径 = 作者半径逐轴减去外面的边框与内边距。
        // 20px 圆角 − 3px 边框 − 5px 内边距 = 12px；内容盒 (8,8)-(68,48) → 左上角圆心 (20,20)、r=12。
        // 角点 (8,8) 到圆心距离 16.97 > 12 → 圆外（留白）；(13,13) 距离 9.9 < 12 → 圆内（图的红色）。
        var s = Resolve("width:60px;height:40px;border:3px solid #00ff00;padding:5px;border-radius:20px");
        Assert.Equal(5, s.PaddingLeft, 3);       // 前提：内边距确实按 px 落进了样式
        Assert.Equal(3, s.BorderWidthLeft, 3);
        var px = PaintReplaced(s, new SKRect(0, 0, 76, 56), new SKRect(8, 8, 68, 48), SKColors.Red);
        Assert.True(Near(px(8, 8), SKColors.White), "圆角外的角点不该被图填满（内容盒 12px 圆角必须生效）");
        Assert.True(Near(px(13, 13), SKColors.Red), "圆角内的像素必须是图的颜色，否则是整盒没画");
        Assert.True(Near(px(40, 30), SKColors.Red), "内容盒中部照常是图");
        Assert.True(Near(px(3, 3), SKColors.White), "图只能进内容盒轮廓，不能溢到边框带里");
    }

    [Fact]
    public void Replaced_content_radius_floors_at_zero()
    {
        // 半径被边框+内边距减光（8 − 3 − 5 = 0）时 CSS 口径是「不裁剪」而不是裁成一个负半径的怪形状。
        // Chrome 实测这时图的直角一直顶到内容盒角上 → (8,8) 必须是红的。
        var s = Resolve("width:60px;height:40px;border:3px solid #00ff00;padding:5px;border-radius:8px");
        var px = PaintReplaced(s, new SKRect(0, 0, 76, 56), new SKRect(8, 8, 68, 48), SKColors.Red);
        Assert.True(Near(px(8, 8), SKColors.Red), "内容半径折算成 0 时不该再裁角");
        Assert.True(Near(px(40, 30), SKColors.Red));
    }

    [Fact]
    public void Replaced_content_clips_percent_radius_by_content_box_axes()
    {
        // 百分比的折算基准是**边框盒**（120×80 → 60/40），减完边框 10px 才是内容盒的椭圆 (50,30)。
        // 若误按内容盒折百分比，会得到 (50,30)−10=(40,20) 的更小椭圆，(40,30) 就会掉到椭圆外。
        // 判据用椭圆方程 ((x−cx)/a)²+((y−cy)/b)²：(10,10) 得 2.0 → 圆外；(40,30) 得 0.27 → 圆内。
        var s = Resolve("width:100px;height:60px;border:10px solid #00ff00;border-radius:50%");
        var px = PaintReplaced(s, new SKRect(0, 0, 120, 80), new SKRect(10, 10, 110, 70), SKColors.Red);
        Assert.True(Near(px(10, 10), SKColors.White), "椭圆角外的角点必须留白");
        Assert.True(Near(px(40, 30), SKColors.Red), "椭圆角内的像素必须保住（百分比基准错成内容盒时这点会被裁掉）");
        Assert.True(Near(px(60, 40), SKColors.Red), "内容盒中心恒在裁剪内");
    }

    [Fact]
    public void Clip_replaced_content_returns_a_save_level_the_caller_can_restore()
    {
        // 契约：返回的是 canvas.Save() 的层号，RestoreToCount 之后裁剪必须消失（调用方 Painter 靠它收尾）。
        // 不这么做的话，画完图后同一画布上的兄弟盒会被这张圆角clip咬掉一角。
        var s = Resolve("width:60px;height:40px;border:3px solid #00ff00;padding:5px;border-radius:20px");
        var info = new SKImageInfo(76, 56);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        int save = BoxPainter.ClipReplacedContent(canvas, new SKRect(0, 0, 76, 56), new SKRect(8, 8, 68, 48), s);
        canvas.RestoreToCount(save);
        using var p = new SKPaint { Color = SKColors.Blue };
        canvas.DrawRect(0, 0, 20, 20, p);
        var px = Grab(surface);
        Assert.True(Near(px(2, 2), SKColors.Blue), "RestoreToCount 之后角上应能正常落墨");
    }
}
