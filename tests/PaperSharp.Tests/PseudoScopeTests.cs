using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// 伪元素作用域隔离：`::before`/`::after` 上声明的**非继承**属性只能落在伪元素自己身上，
/// 既不能画到宿主元素，也不能横向泄给引用同名自定义属性的兄弟元素。
///
/// 为什么单独一档测试（Chrome 真值取证 2026-09-28，见 docs/chrome-parity-todo.md P0-1/P0-2）：
/// 少数属性（border-radius / box-shadow / 背景 / clip-path / mix-blend-mode / background-clip /
/// overflow / flex / align-* …共 30+ 条）AngleSharp 读不稳，StyleResolver 另建了一张
/// 「选择器末段 token → 作者原样串」的原始 CSS 兜底表。旧口径建键时把冒号之后的内容一律剥掉，
/// <c>.hero::after</c> 与 <c>.hero</c> 并成同一把键，于是伪元素的声明冒充了宿主的声明——
/// login-page 的横幅（CSS 里没写 border-radius）因此被画成 64px 大圆角 + 一圈白色内描边。
/// 盒矩形对拍看不见这类错（几何全绿、像素画错），只能靠语义断言钉住。
/// </summary>
public class PseudoScopeTests
{
    /// <summary>取样式树里第一个指定标签的样式（深度优先，找不到即失败）。</summary>
    static ComputedStyle First(ComputedStyle root, string tag)
    {
        if (root.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)) return root;
        foreach (var c in root.Children)
        {
            var hit = Try(c, tag);
            if (hit != null) return hit;
        }
        throw new InvalidOperationException($"样式树里没有 <{tag}>");

        static ComputedStyle? Try(ComputedStyle n, string t)
        {
            if (n.Tag.Equals(t, StringComparison.OrdinalIgnoreCase)) return n;
            foreach (var c in n.Children)
                if (Try(c, t) is { } h) return h;
            return null;
        }
    }

    static ComputedStyle Build(string html, string css)
        => StyleResolver.BuildDocument(html, css, viewportWidth: 400);

    [Fact]
    public void Pseudo_border_radius_does_not_leak_to_host()
    {
        var cs = Build("<div class=a>hi</div>", ".a::after{border-radius:40px}");
        Assert.Equal(0, First(cs, "div").BorderRadiusTopLeft);
        Assert.Equal(0, First(cs, "div").BorderRadiusBottomLeftY);
    }

    [Fact]
    public void Pseudo_keeps_its_own_border_radius()
    {
        // 隔离不能把伪元素自己的值一起挡掉：装饰件圆角必须仍然生效
        var cs = Build("<div class=a>hi</div>", ".a::after{content:\"X\";border-radius:40px}");
        var after = First(cs, "div").After;
        Assert.NotNull(after);
        Assert.Equal(40, after!.Style.BorderRadiusTopLeft);
    }

    [Theory]
    [InlineData(".a::after{box-shadow:inset 0 0 0 1px #f00}", "shadow")]
    [InlineData(".a::before{background:linear-gradient(#f00,#00f)}", "bg")]
    [InlineData(".a::before{clip-path:circle(50%)}", "clip")]
    [InlineData(".a::before{mix-blend-mode:multiply}", "blend")]
    [InlineData(".a::before{background-clip:text}", "bclip")]
    [InlineData(".a::before{overflow:hidden}", "ovf")]
    [InlineData(".a::before{filter:blur(2px)}", "filter")]
    [InlineData(".a::before{corner-shape:squircle}", "shape")]
    public void Pseudo_declarations_do_not_leak_any_property(string css, string kind)
    {
        var host = First(Build("<div class=a>hi</div>", css), "div");
        switch (kind)
        {
            case "shadow": Assert.Empty(host.Shadows ?? new List<BoxShadow>()); break;
            case "bg": Assert.Null(host.Background); break;
            case "clip": Assert.Null(host.ClipPath); break;
            case "blend": Assert.Equal("normal", host.MixBlendMode); break;
            case "bclip": Assert.Equal("border-box", host.BackgroundClip); break;
            case "ovf": Assert.False(host.ClipsOverflow); break;
            case "filter": Assert.Null(host.Filter); break;
            case "shape": Assert.Equal("round", host.CornerShape); break;
        }
    }

    [Fact]
    public void Pseudo_custom_property_does_not_leak_to_sibling()
    {
        // Chrome：--pad 只活在 .a::after 这一层，.b 取不到 → 走 var() 的回退值 0px。
        // 断言落在**盒宽**上而不是 PaddingLeft：padding 简写带 var() 时 AngleSharp 整条丢弃，
        // 真正吃到值的是 AuthorBoxSpacing 那条作者源微级联，只有盒矩形才是端到端真值。
        var cs = Build("<div class=a>x</div><div class=b>y</div>",
            ".a::after{--pad:30px}.b{padding:var(--pad,0px)}");
        var root = LayoutBuilder.ToBoxTree(cs);
        LayoutBuilder.Measure(root, 400);
        var b = root.Children[1];
        Assert.Equal(400, b.ContentWidth, 1);
    }

    [Fact]
    public void Host_custom_property_still_visible_to_pseudo()
    {
        // 反方向必须照常：伪元素继承宿主的自定义属性
        var cs = Build("<div class=a>hi</div>",
            ".a{--r:9px}.a::after{content:\"X\";border-radius:var(--r,0px)}");
        var after = First(cs, "div").After;
        Assert.NotNull(after);
        Assert.Equal(9, after!.Style.BorderRadiusTopLeft, 1);
        // 已知边界（docs/chrome-parity-todo.md P1-8）：同一份变量写在 margin/padding 上时
        // 走的是 AuthorBoxSpacing 那条作者源索引，它不认伪元素作用域键，故宿主变量喂不到伪元素的 margin 上。
    }

    [Fact]
    public void Grouped_selector_pseudo_part_does_not_leak_to_other_part()
    {
        // 逗号组里前一段是伪元素、后一段是宿主：宿主该拿到 9，伪元素那段不能反过来污染别的宿主
        var cs = Build("<div class=a>x</div><div class=b>y</div>", ".a::before,.b{border-radius:9px}");
        var divs = cs.Children.Where(c => c.Tag == "div").ToList();
        Assert.Equal(0, divs[0].BorderRadiusTopLeft);   // .a 什么都没写
        Assert.Equal(9, divs[1].BorderRadiusTopLeft);   // .b 拿到属于它自己的 9
        // 已知边界（docs/chrome-parity-todo.md P1-7）：兜底表只索引整条选择器串的末段，
        // 反过来的 ".a,.b::after{…}" 会丢掉 .a —— 本波刻意不扩面，先钉住现状别退步。
    }

    [Fact]
    public void LoginPage_hero_has_no_radius_and_no_inset_ring()
    {
        // 回归本档的来由：assets/login-page.html 的 .hero 吃了 .hero::after 的 64px 圆角与白色内描边
        var html = File.ReadAllText(Path.Combine(Fixture.AssetsDir, "login-page.html"));
        var cs = StyleResolver.BuildDocument(html, "", viewportWidth: 1284);
        var hero = First(cs, "header");
        Assert.Equal(0, hero.BorderRadiusTopLeft);
        Assert.Equal(0, hero.BorderRadiusBottomRight);
        Assert.Empty(hero.Shadows ?? new List<BoxShadow>());
        // 卡片自己的 24px 圆角不能被误伤
        Assert.Equal(24, First(cs, "main").BorderRadiusTopLeft);
    }
}
