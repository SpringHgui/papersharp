using SkiaSharp;
using PaperSharp;
using PaperSharp.Layout;

namespace PaperSharp.Tests;

/// <summary>
/// 绘制原点折整（Blink 的 snap to pixel layout boundaries）——像素重影的头号根因。
///
/// 浏览器侧真值（探针 <c>assets/_probe-snap.html</c>，2026-09-29，<c>node tools/boxdiff.mjs _probe-snap
/// --dsf 2 --shot …</c> 出 2x 截图，<c>dotnet run -- snap _probe-snap …</c> 逐行量墨迹纵向质心）：
/// 20 行 top=40.1k+0.1k 的实心条与文本，浏览器侧落位**只出现在偶数设备行**（= 整 CSS px × 2），
/// 且 top=200.5 → 201、top=120.3 → 120，即「四舍五入、.5 远离零」；引擎侧此前按分数设备行连续走。
/// 所以在 2x 光栅上几乎每个盒都与浏览器差 0~1 设备像素——叠在一起看就是满屏重影。
///
/// 反证（防空跑）：把 <see cref="PaintSnap.Enabled"/> 通过 <c>PS_SNAP=0</c> 关掉，本文件每条断言都变红
/// （实心条的边缘行变成半覆盖的抗锯齿行，不再有整行满墨）。
/// </summary>
public class PaintSnapTests
{
    const float W = 300f;
    const float Scale = 2f;
    /// <summary>绝对定位不撑文档高，所以给 body 显式高度，别让探针读到画布外。</summary>
    const string Page = "html,body{margin:0;height:300px}";

    static SKBitmap Raster(string css, string html)
    {
        var b = SKBitmap.Decode(Renderer.RenderPng(html, css, W, scale: Scale));
        Assert.True(b.Height >= 600 && b.Width >= 600, $"光栅太小读不到探针行：{b.Width}x{b.Height}");
        return b;
    }

    /// <summary>第 y 行（设备像素）在 [x0,x1) 区间是否**整行满墨**（每像素都黑到 8/8/8 以内）。</summary>
    static bool RowSolid(SKBitmap b, int y, int x0, int x1)
    {
        for (int x = x0; x < x1; x++)
        {
            var p = b.GetPixel(x, y);
            if (p.Red > 8 || p.Green > 8 || p.Blue > 8) return false;
        }
        return true;
    }

    /// <summary>第 y 行是否整行纯白（边缘外一格都不该有墨）。</summary>
    static bool RowBlank(SKBitmap b, int y, int x0, int x1)
    {
        for (int x = x0; x < x1; x++)
            if (b.GetPixel(x, y).Red < 247) return false;
        return true;
    }

    static (int Top, int Bottom) InkRows(SKBitmap b, int x0, int x1)
    {
        int top = -1, bot = -1;
        for (int y = 0; y < b.Height; y++)
            for (int x = x0; x < x1; x++)
                if (b.GetPixel(x, y).Red < 128) { if (top < 0) top = y; bot = y; break; }
        return (top, bot);
    }

    static string Bar(string pos, float h = 10f) =>
        $"<div style='position:absolute;{pos};width:100px;height:{h:0.#}px;background:#000'></div>";

    [Fact]
    public void Fractional_top_snaps_to_whole_css_pixel()
    {
        // top:120.3 → round=120 → 设备行 240 起，高 10 CSS px = 20 设备行（240..259）整行满墨；
        // 不折整的话边缘落在 240.6，第 240 行只有四成覆盖、第 260 行有六成覆盖 → 没有整行满墨。
        var b = Raster(Page, Bar("top:120.3px;left:20px"));
        Assert.True(RowSolid(b, 240, 40, 240), "折整后首行应整行满墨（top 120.3 → 120）");
        Assert.True(RowSolid(b, 259, 40, 240), "末行（240+20-1）应整行满墨");
        Assert.True(RowBlank(b, 239, 40, 240), "上边缘外一行不该有墨");
        Assert.True(RowBlank(b, 260, 40, 240), "下边缘外一行不该有墨");
    }

    [Fact]
    public void Midpoint_rounds_away_from_zero()
    {
        // 浏览器口径实测：top=200.5 落在 201（不是 200）。
        var b = Raster(Page, Bar("top:200.5px;left:20px"));
        Assert.True(RowSolid(b, 402, 40, 240), "200.5 应进位到 201 → 设备行 402");
        Assert.True(RowBlank(b, 401, 40, 240), "落 200（设备行 400~401 有墨）即为回退");
    }

    [Fact]
    public void Fractional_left_snaps_too()
    {
        // 横轴同一条规则：left:40.1 → 40 → 设备列 80 起整列满墨。
        var b = Raster(Page, Bar("top:20px;left:40.1px"));
        int colSolid = 0;
        for (int x = 80; x < 280; x++)
        {
            bool all = true;
            for (int y = 40; y < 60; y++) if (b.GetPixel(x, y).Red > 8) { all = false; break; }
            if (all) colSolid++;
        }
        Assert.Equal(200, colSolid);   // 不折整 → 左右两条边缘列都是半覆盖，凑不满 200 列
    }

    [Fact]
    public void Snapping_does_not_move_the_layout()
    {
        // 折整只发生在绘制这一层：盒矩形必须仍是作者算出来的那个分数值，
        // 否则 tools/sweep.sh 的几何对拍与 boxdiff 会被绘制层的取整反向污染。
        var html = Bar("top:120.3px;left:20px");
        var r = Renderer.RenderRasterWithBoxes(html, Page, W, scale: Scale);
        var box = r.Boxes.Single(x => x.Tag == "div");
        Assert.Equal(120.3f, box.Y, 2);
        Assert.Equal(20f, box.X, 2);
        Assert.Equal(10f, box.H, 2);
    }
}
