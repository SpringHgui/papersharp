namespace PaperSharp.Tests;

/// <summary>
/// 轨道/弹性尺寸的「内容地板」与「不收缩」两类行为，逐条对拍无头 Edge 实测值。
///
/// 起因是 Playground 对拍里两条各自独立、但都属于「浏览器会让网格/弹性容器合法溢出，
/// 而引擎把它们压回容器宽」的缺陷：
/// · CSS Grid §12.6.2 + §12.7.1：1fr 实为 minmax(auto,1fr)，min-width:auto 且 overflow 为 visible 的
///   子项会给轨道立一条 min-content 地板；地板高于它该分的份额时冻结在地板上并剔除出分配，
///   **整幅网格照例溢出**（login-page 实测轨道 640px/372px，合计比内容盒多 8px）。
///   地板有两个豁免：写了具体 min-width、或 overflow 非 visible（.plan-card{overflow:hidden} 靠这条不立底）。
/// · CSS Flexbox §7.2：flex-shrink:0（含简写 flex:none）的子项不参与收缩，余量全压给可缩项。
/// 全部期望值取自无头 Edge 对同一份标记的实测（见各用例注释），不是理想化整数。
/// </summary>
public class TrackContentFloorTests
{
    const string Css = """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font: 14px/1.5 Arial, sans-serif; }
        .g { display: grid; grid-template-columns: 100px 1fr; gap: 20px; width: 300px; }
        .g > i { display: block; height: 10px; }
        .inner { width: 220px; height: 10px; }
        .ovf { overflow: hidden; }
        .mw0 { min-width: 0; }
        .f { display: flex; gap: 14px; width: 300px; align-items: flex-start; }
        .fn { width: 100px; height: 20px; flex: none; }
        .fs { width: 100px; height: 20px; }
        """;

    const string LongText = "第二项是可伸缩的长文本占位用来制造溢出以观察谁被压缩掉宽度内容很多";

    /// <summary>取 body 下第一个容器（body[1]/div[1]）的第一个 div 子项宽度——别用 EndsWith("/div[1]")，
    /// 那会连容器自身一起命中。</summary>
    static float W(string html)
        => Renderer.RenderRasterWithBoxes(html, Css, 800).Boxes
            .Single(b => b.DomPath == "body[1]/div[1]/div[1]").W;

    [Fact]
    public void GridFr_track_is_floored_by_item_min_content_and_may_overflow_the_container()
    {
        // 实测 [100, 220]：份额只有 300−100−20 = 180，但子项里那个 220px 定宽 div 立起地板，
        // 网格合计 340 > 容器 300 —— 浏览器就是这么溢出，引擎不得把它压回 180。
        Assert.Equal(220, W("<div class=g><i></i><div><div class=inner></div></div></div>"), 1);
    }

    [Fact]
    public void GridFr_track_has_no_floor_when_item_clips_overflow()
    {
        // 实测 [100, 180]：子项 overflow:hidden → §9.4.1 的自动最小豁免，回到纯份额。
        Assert.Equal(180, W("<div class=g><i></i><div class=ovf><div class=inner></div></div></div>"), 1);
    }

    [Fact]
    public void GridFr_track_has_no_floor_when_item_sets_explicit_min_width()
    {
        // 实测 [100, 180]：min-width:0 同样取消内容地板。
        Assert.Equal(180, W("<div class=g><i></i><div class=mw0><div class=inner></div></div></div>"), 1);
    }

    [Fact]
    public void Flex_item_with_flex_none_is_not_shrunk()
    {
        // 实测 [100, 186]：容器 300 装不下 100+14+文本，浏览器把收缩全压给第二项；
        // 对照组（不写 flex:none，同为 100px）实测被压到 50.89。
        Assert.Equal(100, W("<div class=f><div class=fn></div><span>" + LongText + "</span></div>"), 1);
    }
}
