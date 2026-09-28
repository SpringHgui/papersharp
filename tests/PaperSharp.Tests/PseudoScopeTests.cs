using SkiaSharp;
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

    // ── 波次2：绝对定位的装饰伪元素必须块级化成盒（Chrome 里它们就是块盒）──────────────

    [Fact]
    public void Absolute_empty_content_pseudo_gets_blockified()
    {
        // 作者没写 display → 规范初值 inline；但 position:absolute 要把 display 再块级化一次。
        // 归一发生在块级化之后时，空内容的行内伪元素「不成盒」，整块装饰直接消失。
        var cs = Build("<div class=a>x</div>",
            "html,body{margin:0}.a{position:relative;width:200px;height:100px}" +
            ".a::after{content:\"\";position:absolute;top:0;left:0;width:50px;height:50px;background:#ff0000}");
        var after = First(cs, "div").After;
        Assert.NotNull(after);
        Assert.Equal("block", after!.Style.Display);
    }

    [Fact]
    public void Absolute_empty_content_pseudo_paints_its_box()
    {
        // 端到端：Chrome 在该位置画 50×50 红块，引擎也必须画出来
        const string Css = "html,body{margin:0}.a{position:relative;width:200px;height:100px;background:#00ff00}" +
                           ".a::after{content:\"\";position:absolute;top:0;left:0;width:50px;height:50px;background:#ff0000}";
        using var bmp = SKBitmap.Decode(Renderer.RenderPng("<div class=a>x</div>", Css, 400f, scale: 1f));
        var inside = bmp.GetPixel(25, 25);
        Assert.True(inside.Red > 180 && inside.Green < 80, $"装饰伪元素该画出红块，实测 ({inside.Red},{inside.Green},{inside.Blue})");
        var outside = bmp.GetPixel(150, 25);
        Assert.True(outside.Green > 180 && outside.Red < 80, $"盒外不该被染色，实测 ({outside.Red},{outside.Green},{outside.Blue})");
    }

    [Fact]
    public void Inline_empty_content_pseudo_still_does_not_box()
    {
        // 对照组：非绝对定位的空内容行内伪元素在 Chrome 里也是零尺寸——不得被波次2 顺手改成成盒
        var cs = Build("<div class=a>x</div>", ".a::before{content:\"\"}");
        var after = First(cs, "div").Before;
        Assert.NotNull(after);
        Assert.Equal("inline", after!.Style.Display);
    }

    [Fact]
    public void LoginPage_hero_decorations_are_boxed()
    {
        // login-page 横幅右侧那两块圆角装饰件（.hero::before / ::after，绝对定位 + 空 content）
        var html = File.ReadAllText(Path.Combine(Fixture.AssetsDir, "login-page.html"));
        var cs = StyleResolver.BuildDocument(html, "", viewportWidth: 1284);
        var hero = First(cs, "header");
        Assert.NotNull(hero.Before);
        Assert.NotNull(hero.After);
        Assert.Equal("block", hero.Before!.Style.Display);
        Assert.Equal("block", hero.After!.Style.Display);
    }

    // ── 波次3：多类名闸门 + AngleSharp computed 抛异常时的 declared 回落 ──────────────

    [Fact]
    public void MultiClass_pseudo_selector_passes_the_gate()
    {
        // 闸门候选必须摊到末段复合选择器里的每个简单 token：PseudoMayMatch 按元素的 class 列表
        // 逐个拼 ".tab"/".active" 来问，而旧口径存的是 ".tab.active" 整串 → 永不同名 → 整条规则被跳过。
        var cs = Build("<div class=\"a b\">x</div>",
            ".a{position:relative}.a.b::after{content:\"\";position:absolute;width:20px;height:4px;background:#1a5cf0}");
        var after = First(cs, "div").After;
        Assert.NotNull(after);
        Assert.Equal("block", after!.Style.Display);
    }

    [Fact]
    public void Transform_declaration_does_not_kill_the_pseudo_box()
    {
        // AngleSharp 取 ::after 的 computed 时，值里带 translateX(-50%) 这类 transform 会抛 NRE；
        // 旧写法 catch 后返回空串 = 「没写 content」，整条伪元素规则连同它的装饰一起消失。
        var cs = Build("<div class=a>x</div>",
            ".a{position:relative}.a::after{content:\"\";position:absolute;left:50%;bottom:0;transform:translateX(-50%);" +
            "width:104px;height:4px;border-radius:2px;background:#1a5cf0}");
        var after = First(cs, "div").After;
        Assert.NotNull(after);
        Assert.Equal("block", after!.Style.Display);
        Assert.Equal(104, after.Style.Width.Absolute, 1);
    }

    [Fact]
    public void LoginPage_active_tab_underline_is_boxed()
    {
        // 现场回归：登录页选中页签那条 104×4 蓝条（.tab.active::after，多类名 + transform 两个坑叠在一起）
        var html = File.ReadAllText(Path.Combine(Fixture.AssetsDir, "login-page.html"));
        var cs = StyleResolver.BuildDocument(html, "", viewportWidth: 1284);
        var tab = FindByText(cs, "手机号登录");
        Assert.NotNull(tab);
        var after = tab!.After;
        Assert.NotNull(after);
        Assert.Equal("block", after!.Style.Display);
        Assert.Equal(104, after.Style.Width.Absolute, 1);
        Assert.Equal(2, after.Style.BorderRadiusTopLeft, 1);
    }

    static ComputedStyle? FindByText(ComputedStyle n, string text)
    {
        if ((n.TextContent ?? "").Contains(text, StringComparison.Ordinal)) return n;
        foreach (var c in n.Children)
            if (FindByText(c, text) is { } h) return h;
        return null;
    }
}
