using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// CSS <c>transform</c>（2D）与 Chrome 的逐像素对账。
///
/// 全部断言都是「按颜色数外接矩形 + 面积」的几何判据，不钉具体像素值——
/// 变换的正确性体现在形状/位置上，钉字节会让下一次抗锯齿调整就误报。
/// 每条用例的期望值都取自同文档在无头浏览器里的 2x 截图（<c>node tools/boxdiff.mjs _probe-tf --dsf 2 --shot …</c>，
/// 探针页 <c>assets/_probe-tf.html</c>），实测 6/7 逐张一致；唯一不一致的那条是 3D 函数（见下）。
///
/// 三条不变量（改 <see cref="TransformSpace"/> 前必读）：
/// ① transform 不参与布局——盒矩形与兄弟排布一律按未变换几何算；
/// ② 祖先链逐层后乘，子盒自己算累计矩阵，绘制时不能把父变换留在画布上下传（会乘两次）；
/// ③ 列表里出现真 3D 函数（rotateX/Y/3d、matrix3d、perspective、scale3d）→ **整条 transform 作废**。
///    这是 CSS 口径而非偷懒：Chrome 支持 3D，我们不支，两种选择都不等于 Chrome，
///    但「整条不画」比「画出一个压扁的近似」更容易被发现和定位。
///    2D 惰性的 <c>translateZ</c>/<c>scaleZ</c>/<c>rotateZ</c> 例外——它们没有透视时不改变外观。
/// </summary>
public class TransformTests
{
    const float W = 400f;

    static SKBitmap Render(string css, string body)
    {
        var png = Renderer.RenderPng(body, css, W, scale: 1f);
        return SKBitmap.Decode(png);
    }

    /// <summary>某颜色的外接矩形（CSS px）+ 像素数；没画出来返回 null。</summary>
    static (float L, float T, float R, float B, int N)? Box(SKBitmap bmp, byte r, byte g, byte b, int tol = 90)
    {
        int? l = null, t = null, rr = null, bb = null;
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (Math.Abs(p.Red - r) + Math.Abs(p.Green - g) + Math.Abs(p.Blue - b) > tol) continue;
                n++;
                l = l == null ? x : Math.Min(l.Value, x);
                t = t == null ? y : Math.Min(t.Value, y);
                rr = rr == null ? x : Math.Max(rr.Value, x);
                bb = bb == null ? y : Math.Max(bb.Value, y);
            }
        return l == null ? null : ((float)l.Value, (float)t.Value, (float)rr!.Value, (float)bb!.Value, n);
    }

    static bool In((float L, float T, float R, float B, int N)? b, float l, float t, float r, float bb, float eps = 2.5f)
        => b != null && Math.Abs(b.Value.L - l) <= eps && Math.Abs(b.Value.T - t) <= eps
                    && Math.Abs(b.Value.R - r) <= eps && Math.Abs(b.Value.B - bb) <= eps;

    static void Close((float L, float T, float R, float B, int N)? actual, float l, float t, float r, float bb, string what)
    {
        Assert.NotNull(actual);
        Assert.True(In(actual, l, t, r, bb),
            $"{what}：期望外接矩形 ({l},{t})-({r},{bb})，实测 ({actual!.Value.L},{actual.Value.T})-({actual.Value.R},{actual.Value.B})");
    }

    const string Base = "html,body{margin:0;background:#fff}body{position:relative;width:400px;height:400px}" +
                        ".b{position:absolute;left:150px;top:150px;width:100px;height:100px}";

    [Fact]
    public void Rotate_45deg_becomes_a_diamond_about_its_center()
    {
        using var bmp = Render(Base + ".b{background:#0000ff;transform:rotate(45deg)}", "<div class=b></div>");
        var d = Box(bmp, 0, 0, 255);
        Assert.NotNull(d);
        // 100×100 转 45°：外接 141.4 见方、中心仍是 (200,200)、面积不变
        Close(d, 200 - 70.7f, 200 - 70.7f, 200 + 70.7f, 200 + 70.7f, "rotate(45deg)");
        Assert.InRange(d!.Value.N, 9000, 11000);                    // 面积 ≈10000（只转不缩）
        // 方块左上角 (150,150) 旋转后应空出来，中心仍实心
        Assert.DoesNotContain("#0000ff", Hex(bmp, 152, 152));
        Assert.Contains("#0000ff", Hex(bmp, 200, 200));
    }

    [Fact]
    public void TranslateX_minus50pct_centers_the_box()
    {
        // 页签蓝条那一形：left:50% + translateX(-50%)。% 相对**自身**边框盒宽，不是包含块。
        using var bmp = Render(Base + ".b{background:#ff00ff;left:50%;transform:translateX(-50%)}", "<div class=b></div>");
        Close(Box(bmp, 255, 0, 255), 150, 150, 250, 250, "left:50% + translateX(-50%)");
    }

    [Fact]
    public void Scale2_doubles_about_the_center()
    {
        using var bmp = Render(Base + ".b{background:#ffcc00;transform:scale(2)}", "<div class=b></div>");
        Close(Box(bmp, 255, 204, 0), 100, 100, 300, 300, "scale(2)");
    }

    [Fact]
    public void SkewX45_shifts_the_top_edge_right_by_its_height()
    {
        using var bmp = Render(Base + ".b{background:#00aaaa;transform:skewX(45deg)}", "<div class=b></div>");
        // 绕中心 (200,200) skewX 45°：x' = x + (y − 200) → 顶边(y=150)左移 50、底边右移 50，外接 200 宽
        Close(Box(bmp, 0, 170, 170), 100, 150, 300, 250, "skewX(45deg)");
    }

    [Fact]
    public void Transform_origin_0_0_rotates_about_the_top_left_corner()
    {
        using var bmp = Render(Base + ".b{background:#00aa00;transform:rotate(90deg);transform-origin:0 0}", "<div class=b></div>");
        // 绕左上角 (150,150) 顺时针 90°：原 (150..250,150..250) 落到 (50..150, 150..250)
        Close(Box(bmp, 0, 170, 0), 50, 150, 150, 250, "transform-origin:0 0 + rotate(90deg)");
    }

    [Fact]
    public void Child_is_carried_by_the_parents_rotation()
    {
        // 祖先累计：父盒转 45°，里面的黑色子条必须跟着转（不是留在正放的位置）
        var css = Base + ".b{background:#888888;transform:rotate(45deg)}" +
                  ".b i{display:block;position:absolute;left:0;top:0;width:100px;height:20px;background:#000000}";
        using var bmp = Render(css, "<div class=b><i></i></div>");
        // 只量黑色子条：灰色父盒被它压住一块，可见灰区的外接会缩水，不适合当判据。
        // 100×20 的条绕父盒中心转 45° → 外接 ≈ (100+20)/√2 = 84.9 见方；
        // 正放时是 100 宽 × 20 高——宽高一测就能分辨「跟着转了」还是「留在原位」。
        var black = Box(bmp, 0, 0, 0);
        Assert.NotNull(black);
        float bw = black!.Value.R - black.Value.L, bh = black.Value.B - black.Value.T;
        Assert.InRange(bw, 60, 100);
        Assert.InRange(bh, 60, 100);
    }

    [Fact]
    public void Transform_does_not_move_the_layout()
    {
        // ①不变量：transform 只影响绘制。第二个块的位置不能被第一个块的 translate 改动。
        var css = "html,body{margin:0;background:#fff}*{box-sizing:border-box}" +
                  ".a{width:100px;height:40px;background:#0000ff;transform:translateY(-30px)}" +
                  ".c{width:100px;height:40px;background:#ff0000}";
        using var bmp = Render(css, "<div class=a></div><div class=c></div>");
        Close(Box(bmp, 255, 0, 0), 0, 40, 99, 79, "兄弟块仍按未变换几何排布");
        // 蓝块本来占 y=0..40，translateY(-30px) 后画到 y=−30..10 → 可见部分只剩顶部 0..10
        Close(Box(bmp, 0, 0, 255), 0, 0, 99, 10, "蓝块自己画到上移 30px 处");
    }

    [Fact]
    public void Real_3d_function_invalidates_the_whole_transform()
    {
        using var bmp = Render(Base + ".b{background:#7700ff;transform:rotateX(45deg) rotate(45deg)}", "<div class=b></div>");
        // 真 3D 函数 → 整条作废（含同列表里的 rotate）：方块正放
        Close(Box(bmp, 119, 0, 255), 150, 150, 250, 250, "含 rotateX 的 transform 应整条不生效");
    }

    [Fact]
    public void TranslateZ_and_rotateZ_stay_2d_inert()
    {
        // 2D 惰性的 3D 写法不该把变换整体判废：translateZ 无透视时不改变外观，rotateZ 与 rotate 同解
        using var bmp = Render(Base + ".b{background:#00cc00;transform:translateZ(10px) rotateZ(45deg)}", "<div class=b></div>");
        Close(Box(bmp, 0, 204, 0), 200 - 70.7f, 200 - 70.7f, 200 + 70.7f, 200 + 70.7f, "translateZ + rotateZ(45deg)");
    }

    static string Hex(SKBitmap bmp, int x, int y)
    {
        var p = bmp.GetPixel(x, y);
        return $"#{p.Red:x2}{p.Green:x2}{p.Blue:x2}";
    }
}
