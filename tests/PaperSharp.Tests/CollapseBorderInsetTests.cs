namespace PaperSharp.Tests;

/// <summary>
/// collapse 表里「共享网格线」的两种身份必须分开：<b>描边</b>整条由一格描一次，<b>内容内缩</b>只算半条。
///
/// CSS 2.1 §17.6.2：折叠表里相邻两格共享一条网格线，Blink 把整条线骑跨在两格边界中线上，
/// 每格的内容只被**半条线 + padding** 推开。引擎以前只有「内部格的右边/底边清零、线整条落在右/下那格头上」
/// 这一半（描边归属），内容内缩于是按整条算 ——
/// **表格里每一格的所有文字统一比浏览器靠右 0.5 CSS px**（2x 光栅上 = 1 设备像素）。
/// 这就是 Playground 叠加视图里「整张表都在糊、表外标题一点不糊」的根因：
/// 逐格做水平相关扫描，m1 的 td[2]/td[3]/td[5] 各 13 格的最优位移全部落在 +1 设备像素，而表外 h2 落在 0。
///
/// 修法把两件事拆开：描边照旧走 <c>Style.BorderWidth*</c>，内容内缩记在 <c>BlockBox.CellBwL/R</c> 上，
/// 由 <c>EdgeL/EdgeR</c> 供盒宽、内容原点与三处绘制取用。<b>纵向不折半</b>——试过，格底/行高随之差 1px，
/// 整张表的行线上下错位，m1 的 1x 可见重影反而从 21.02% 涨到 26.44%，比横向那 0.5px 伤得多。
///
/// 净影响（<c>pixdiff &lt;名&gt; .scratch/pixelsweep/shot-&lt;名&gt;.png 1284</c>，1x 可见重影）：
/// table 13.57% → <b>10.80%</b> · m7 30.16% → <b>29.57%</b> · m3 8.11% → <b>7.88%</b> ·
/// m1 21.02% → 20.96%（m1 的残余在<b>画出来的</b>网格线落点，不在这条，见台账 P1-17）·
/// 逐格水平相关扫描：m1 的 td[2]/td[3] 各 13 格最优位移 +1 设备像素 → <b>0</b>。其余样例逐位不动。
/// </summary>
public class CollapseBorderInsetTests
{
    /// <summary>
    /// 同一个字、同一个 padding、同一个 1px 边框，放进 <b>collapse</b> 表与 <b>separate</b> 表各一格：
    /// separate 里每格独占自己那圈整条边框 → 内容内缩 = 1 + padding；
    /// collapse 里网格线与邻居共享、每格只占半条 → 内容内缩 = 0.5 + padding。
    /// 两者相差 0.5 CSS px = 1 设备像素，且与字体/字号/字面侧余量无关（同一个字）。
    /// </summary>
    const string Probe = """
        <style>
          table { margin-bottom: 40px; }
          td { border: 1px solid #000; padding: 8px; font: 14px/1 "Microsoft YaHei"; }
          .cl { border-collapse: collapse; }
          .sp { border-collapse: separate; border-spacing: 0; }
        </style>
        <table class=cl><tr><td>明</td></tr></table>
        <table class=sp><tr><td>明</td></tr></table>
        """;

    [Fact]
    public void Collapsed_cell_insets_content_by_half_the_line()
    {
        var r = Renderer.RenderRasterWithBoxes(Probe, "", 400f, baseDir: Fixture.AssetsDir, scale: 2f);
        var cells = r.Boxes.Where(b => b.DomPath.EndsWith("td[1]")).ToList();
        Assert.Equal(2, cells.Count);
        var collapse = cells[0];
        var separate = cells[1];

        using var img = SkiaSharp.SKImage.FromEncodedData(r.Bytes);
        using var bmp = SkiaSharp.SKBitmap.FromImage(img!);

        // 从本格边框盒左缘往右 4 CSS px（越过描边）找第一列有墨的列；纵向只看本格中段，
        // 免得撞上上/下边框线（它们横贯整列，会被当成「有墨」）。
        int FirstInkOffset(float cellX, float cellY, float cellH)
        {
            int left = (int)((r.BodyX + cellX) * 2);
            int top = (int)((r.BodyY + cellY) * 2);
            for (int x = left + 8; x < left + 220; x++)
                for (int y = top + 16; y < top + (int)(cellH * 2) - 4; y++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (Math.Min(Math.Min(c.Red, c.Green), c.Blue) < 128) return x - left;
                }
            return -1;
        }
        int dCl = FirstInkOffset(collapse.X, collapse.Y, collapse.H);
        int dSp = FirstInkOffset(separate.X, separate.Y, separate.H);
        Assert.True(dCl > 0 && dSp > 0, "没找到单元格里的墨迹");
        // 内缩 = 边 + padding(8) + 字面左余量 → 2x 下应在 16~24 设备像素之间。
        // 不在窗口里说明量的不是字（撞上了框线或跑进了邻格），断言本身就会变成假绿。
        Assert.True(dCl is (> 16) and (< 24) && dSp is (> 16) and (< 24),
            $"内缩测量落在窗口外（collapse {dCl}、separate {dSp} 设备像素），探针或坐标口径变了");

        Assert.True(dSp - dCl == 1,
            $"separate 内缩 {dSp}、collapse {dCl} 设备像素，差应为 1（半条共享线 ×2）——"
            + "collapse 格的内容又按整条边框内缩了，表格里每格文字会整体比浏览器靠右 0.5 CSS px");
    }
}
