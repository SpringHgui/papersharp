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
///
/// <para>本文件另外钉住这条规则的两处**边界**（都由 2026-09-30 的取证得出，改动前先看注释）：
/// ① 横向只折**盒**、不折**字**（<see cref="Solid_bar_forgets_the_fraction_that_text_keeps"/>）；
/// ② 折的是**文档绝对位置**、不是逐层各自折（<see cref="Nested_inline_block_snaps_at_its_own_absolute_y"/>——
/// 引擎里唯一真嵌套的一支是行内原子，它画在祖先的折整作用域里）。</para>
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

    static string Text(string pos) =>
        $"<div style='position:absolute;{pos};width:200px;"
        + "font:16px/1.6 \"Microsoft YaHei\",sans-serif;color:#000'>Minimum</div>";

    /// <summary>逐像素不同的格子数（两张尺寸须一致）。</summary>
    static int DiffPixels(SKBitmap a, SKBitmap b)
    {
        int n = 0;
        for (int y = 0; y < Math.Min(a.Height, b.Height); y++)
            for (int x = 0; x < Math.Min(a.Width, b.Width); x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        return n;
    }

    /// <summary>墨量加权质心（设备像素）。次像素级的落位差只能靠质心量，逐行「有无墨」会被 AA 抹平。</summary>
    static (float X, float Y) InkCentroid(SKBitmap b, int x0, int x1, int y0, int y1)
    {
        double sx = 0, sy = 0, sw = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                var p = b.GetPixel(x, y);
                double w = 255 - (p.Red + p.Green + p.Blue) / 3.0;
                if (w <= 12) continue;
                sx += x * w; sy += y * w; sw += w;
            }
        Assert.True(sw > 200, $"探针窗口里没量到墨：{x0}..{x1} × {y0}..{y1}");
        return ((float)(sx / sw), (float)(sy / sw));
    }

    /// <summary>首条有墨的设备行（用于把「落在哪一格」直接读回来）。</summary>
    static int FirstInkRow(SKBitmap b, int x0, int x1, int from = 0)
    {
        for (int y = from; y < b.Height; y++)
            for (int x = x0; x < x1; x++)
                if (b.GetPixel(x, y).Red < 128) return y;
        return -1;
    }

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

    /// <summary>
    /// 盒折 x、**文字不折 x**（2026-09-30 探针 <c>assets/_probe-phase.html</c>：10 行几何逐盒 0.0px 全等，
    /// 把字的绝对 x 小数部从 0.1 扫到 0.9，浏览器落位相对引擎连续走 −0.8 → +1.0 设备像素 = 小数部 × 2，
    /// 而 dy 恒为 0 → Blink 对字形走次像素定位，只折纵向）。这条是 table / m1 / report / pct 那一类
    /// 「盒全对、字整条错一格」重影的根因，也是 <see cref="PaintSnap.BeginText"/> 存在的唯一理由。
    /// </summary>
    [Fact]
    public void Solid_bar_forgets_the_fraction_that_text_keeps()
    {
        // 实心条：40.2 与 40.4 都 round 到 40 → 两张光栅逐像素全等（小数部被折整整吃掉）。
        Assert.Equal(0, DiffPixels(Raster(Page, Bar("top:20px;left:40.2px")),
                                   Raster(Page, Bar("top:20px;left:40.4px"))));

        // 文字：同一对小数必须留出墨来 —— 若 BeginText 掉了，这两张也会全等（即回到重影态）。
        var a = Raster(Page, Text("top:20px;left:40.2px"));
        var b = Raster(Page, Text("top:20px;left:40.4px"));
        Assert.True(DiffPixels(a, b) > 0, "两张文字光栅逐像素全等 → 横向又被折整了，重影会整页回来");
        // 落位确实跟着小数部走，但**别按线性比例断言**：Skia 让每个字形从整数设备列起笔、零头用次像素
        // 覆盖补，整串墨质心的响应因此是非线性的（实测 0.2 CSS px 只走 0.155 设备像素，而 0.5 CSS px
        // 恰好走整整一列）。这里只取「方向对、幅度不足一列」。
        float dx = InkCentroid(b, 60, 460, 40, 100).X - InkCentroid(a, 60, 460, 40, 100).X;
        Assert.True(dx > 0 && dx < 1, $"左移 0.2 CSS px 后墨质心走了 {dx:0.###} 设备像素（应为 0~1 之间的同向偏移）");

        // 纵向相反：120.2 与 120.4 都折到 120，文字必须逐像素全等（BeginText 只退 x，没把 y 也退了）。
        Assert.Equal(0, DiffPixels(Raster(Page, Text("top:120.2px;left:20px")),
                                   Raster(Page, Text("top:120.4px;left:20px"))));
    }

    /// <summary>
    /// 单元格里的文字行**额外**再折一次（折到绝对基线的下取整），普通块不折。
    ///
    /// 取证（2026-10-01，challenge 末尾那张 border 演示表，行距 19.53125px、格顶相位 .28→.00 共八行）：
    /// 浏览器侧每行基线恒落在 floor(绝对基线)（设备行 3137/3156/3176/3195/3215/3234/3254/3273），
    /// 引擎按 round(格顶)+分数内偏移画，frac(格顶)≥.5 的四行整条字低 1 CSS px ＝ 5 条 2 设备像素位移带。
    /// 而**普通块不能照抄**：无差别挂到所有行时那 5 条确实清零，p/li/div/h1/span/ol 却新长出 17 条，
    /// 整页 1x 可见重影 11.21% → 15.34% —— 浏览器对普通块信盒的 round 原点、行内偏移留分数。
    ///
    /// 判据不依赖字体度量的具体小数：把行内偏移（padding-top）扫过整整 1 CSS px，
    /// 折到整 CSS px 的一支只跨 1 档台阶（2 设备像素），另一支按设备行连续走（≥3 档）。
    /// 反证：<c>PS_LINESNAP=0</c> 时单元格那一支也变成连续多档，本条变红。
    /// </summary>
    [Fact]
    public void Table_cell_lines_floor_their_baseline_plain_blocks_do_not()
    {
        // 盒顶恒为整数 40（前面的占位条高度固定），只有行内 padding 在小数档上扫。
        const string F = "font:16px/1.6 \"Microsoft YaHei\",sans-serif;color:#000";
        static string Cell(float p) =>
            $"<div style='height:40px'></div><table><tr>"
            + $"<td style='{F};padding:{p:0.##}px 0 0 0'>Minimum</td></tr></table>";
        static string Blk(float p) =>
            $"<div style='height:40px'></div><div style='{F};padding-top:{p:0.##}px'>Minimum</div>";

        static float[] Centroids(Func<float, string> mk) =>
            new[] { 0f, 0.34f, 0.67f, 1f }.Select(p => InkCentroid(Raster(Page, mk(p)), 30, 300, 70, 140).Y).ToArray();

        var cell = Centroids(Cell);
        var blk = Centroids(Blk);

        int cellSteps = cell.DistinctBy(c => (int)Math.Round(c * 100)).Count();
        int blkSteps = blk.DistinctBy(c => (int)Math.Round(c * 100)).Count();
        // 折到整 CSS px ⇒ 扫过 1px 只跨 1 档台阶（2 设备像素）；不折则按设备行连续走，档数明显更多。
        Assert.True(cellSteps <= 2,
            $"单元格行没折到整 CSS px：padding 扫 1px 落出了 {cellSteps} 档（{string.Join(" / ", cell.Select(c => c.ToString("0.000")))}）");
        Assert.True(blkSteps >= 3,
            $"普通块的文字被跟着折进整 CSS px 了：{blkSteps} 档（{string.Join(" / ", blk.Select(c => c.ToString("0.000")))}）→ 会重新长出整页位移带");
    }

    /// <summary>
    /// 折整折的是**文档绝对位置**，不是「每一层各自折自己的分数偏移」。P0-4 的 <c>_probe-snap2</c> 探针
    /// （0~9 层 <c>padding-top:.3px</c> 嵌套链）实测 k=4 的 120+0.9 浏览器给设备行 242，逐层各自折会给 240。
    /// 引擎里唯一真正嵌套的折整作用是行内原子（<c>Painter.PaintInlineAtoms</c> 画在祖先的折整作用域里），
    /// 这条钉住那一支：内层 inline-block 落在 <c>round(自己的绝对 y)</c>，而不是「祖先折完剩下的分数」。
    /// </summary>
    [Fact]
    public void Nested_inline_block_snaps_at_its_own_absolute_y()
    {
        // 外盒 top:40.3 + padding-top:0.3 → 内层绝对 y = 40.6 → round = 41 → 设备行 82。
        // 逐层口径会给 40.6 − 0.3 = 40.3（首墨行 81 以下、且上下边缘都是半覆盖）。
        var b = Raster(Page,
            "<div style='position:absolute;top:40.3px;left:20px;padding-top:0.3px;font-size:0;line-height:0'>"
            + "<div style='display:inline-block;vertical-align:top;width:100px;height:10px;background:#000'></div></div>");
        Assert.Equal(82, FirstInkRow(b, 40, 240));
        Assert.True(RowSolid(b, 82, 40, 240), "首墨行该是整行满墨；出现半覆盖 = 落回了分数格");
        Assert.True(RowSolid(b, 101, 40, 240), "10 CSS px 高的内层末行（82+20−1）该整行满墨");
    }
}
