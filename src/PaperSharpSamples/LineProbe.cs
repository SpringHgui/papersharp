using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharpSamples;

/// <summary>行盒级探针：dotnet run -- lines &lt;name&gt; [width]。打印每个 inline FC 块的行基线/高度与
/// 每个文本 frag 的绘制矩形 + 行内背景矩形（DrawRun 口径），用于定位"首行下半截消失"类问题。</summary>
public static class LineProbe
{
    public static void Run(string assets, string name, float width)
    {
        var html = File.ReadAllText(Path.Combine(assets, name + ".html"));
        var css = File.Exists(Path.Combine(assets, name + ".css")) ? File.ReadAllText(Path.Combine(assets, name + ".css")) : "";
        var computed = StyleResolver.BuildDocument(html, css, null, false, width, domPath: true);
        var root = LayoutBuilder.ToBoxTree(computed);
        LayoutBuilder.Measure(root, width);
        LayoutBuilder.Position(root, 0, 0);
        Walk(root);
    }

    static void Walk(BlockBox box)
    {
        // 纯文本块走 WrappedLines + 逐行行盒（LineBoxes）绘制，与 Lines（含原子盒）是两条通路：
        // 基线算错 1px 只会出现在其中一条，所以两条都要能打印。
        if (box.Lines == null && box.WrappedLines != null)
        {
            var s = box.Style;
            float cy = box.BorderY + s.BorderWidthTop + s.PaddingTop + box.ValignDY;
            var (a, d) = PaperSharp.Layout.FontManager.TextExtents(s.FontFamily, s.FontWeight, s.FontSize);
            float lead = s.LineHeightPx() - (a + d);
            Console.WriteLine($"<{s.Tag}> dom={s.DomPath} content={cy:0.###} fs={s.FontSize} lh={s.LineHeightPx():0.###} " +
                $"ext={a:0.###}/{d:0.###} lead={lead:0.###} base={a + MathF.Floor(lead / 2):0.###} " +
                $"lb={(box.LineBoxes == null ? "null" : string.Join(" ", box.LineBoxes.Select(l => $"{l.Asc:0.###}/{l.Desc:0.###}")))} " +
                $"n={box.WrappedLines.Length} \"{Trim(box.WrappedLines[0])}\"");
        }
        if (box.Lines != null)
        {
            var s = box.Style;
            float contentX = box.BorderX + s.BorderWidthLeft + s.PaddingLeft;
            float contentY = box.BorderY + s.BorderWidthTop + s.PaddingTop + box.ValignDY;
            Console.WriteLine($"<{s.Tag}> dom={s.DomPath} border=({box.BorderX:0.##},{box.BorderY:0.##}) pad=({s.PaddingTop},{s.PaddingLeft}) radius={s.BorderRadiusTopLeft} content=({contentX:0.##},{contentY:0.##})");
            float y = contentY;
            int li = 0;
            foreach (var ln in box.Lines)
            {
                Console.WriteLine($"  line{li} top={y:0.##} h={ln.Height:0.##} baseline={(y + ln.Baseline):0.##}");
                foreach (var f in ln.Frags)
                {
                    if (f.Text != null)
                    {
                        float fvTop = f.Style.BorderWidthTop + f.Style.PaddingTop;
                        float baseline = y + ln.Baseline + f.AlignDY;
                        float bgTop = baseline - f.Ascent - fvTop;
                        float bgH = f.Ascent + f.Descent + fvTop + f.Style.BorderWidthBottom + f.Style.PaddingBottom;
                        Console.WriteLine($"    frag \"{Trim(f.Text)}\" x={(contentX + ln.Left + f.X):0.##} w={f.Width:0.##} asc={f.Ascent:0.##} desc={f.Descent:0.##} host={ReferenceEquals(f.Style, s)} bg={f.Style.BackgroundColor} bgRect=({(contentX + ln.Left + f.X):0.##},{bgTop:0.##}..{bgTop + bgH:0.##})");
                    }
                    else if (f.Box != null)
                        Console.WriteLine($"    atom <{f.Box.Style.Tag}> x={(contentX + ln.Left + f.X):0.##} w={f.Width:0.##}");
                }
                y += ln.Height; li++;
            }
        }
        foreach (var c in box.Children) Walk(c);
        if (box.Atoms != null)
            foreach (var a in box.Atoms) if (a.Box != null) Walk(a.Box);
    }

    static string Trim(string t) => t.Length <= 12 ? t : t[..12] + "…";
}
