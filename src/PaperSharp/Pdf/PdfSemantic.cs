using System.Text;
using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Pdf;

/// <summary>
/// 从分页结果里收集 PDF 语义信息：标题（h1..h6）→ 书签；&lt;a href&gt; → 链接注释。
/// 与 PagePainter 共用同一套坐标语义（DrawItem 的页内绝对坐标 + TextTop/FirstLine 逐行推进）。
/// </summary>
public static class PdfSemantic
{
    public static (List<PdfOutline> Outlines, List<PdfLink> Links) Collect(List<Page> pages)
    {
        var outlines = new List<PdfOutline>();
        var links = new List<PdfLink>();
        var seenHeading = new HashSet<BlockBox>();

        for (int pi = 0; pi < pages.Count; pi++)
        {
            foreach (var item in pages[pi].Items.Concat(pages[pi].AbsItems))
            {
                var box = item.Box;
                var s = box.Style;

                if (item.DrawDecor)
                {
                    int level = s.Tag switch { "h1" => 1, "h2" => 2, "h3" => 3, "h4" => 4, "h5" => 5, "h6" => 6, _ => 0 };
                    if (level > 0 && seenHeading.Add(box))
                        outlines.Add(new PdfOutline { Level = level, Title = HeadingText(box), Page = pi, YTop = item.Y });

                    // 块级 <a>（display:block 等）：整个盒子是链接
                    if (s.LinkHref != null && IsUri(s.LinkHref))
                        links.Add(new PdfLink
                        {
                            Page = pi,
                            Rect = new SKRect(item.X, item.Y, item.X + item.W, item.Y + item.H),
                            Uri = s.LinkHref,
                        });
                }

                // inline FC：行盒里带 href 的 frag（文本 run / 原子盒）
                var lines = box.Lines;
                if (lines == null || item.LineCount <= 0) continue;
                float y = item.TextTop;
                for (int i = 0; i < item.FirstLine && i < lines.Count; i++) y += lines[i].Height;
                int end = Math.Min(lines.Count, item.FirstLine + item.LineCount);
                for (int i = item.FirstLine; i < end; i++)
                {
                    var ln = lines[i];
                    float baseline = y + ln.Baseline;
                    foreach (var f in ln.Frags)
                    {
                        var href = f.Style?.LinkHref ?? f.Box?.Style?.LinkHref;
                        if (href == null || !IsUri(href)) continue;
                        links.Add(new PdfLink
                        {
                            Page = pi,
                            Rect = new SKRect(item.TextLeft + ln.Left + f.X, baseline - f.FontSize,
                                item.TextLeft + ln.Left + f.X + f.Width, baseline + f.FontSize * 0.25f),
                            Uri = href,
                        });
                    }
                    y += ln.Height;
                }
            }
        }
        return (outlines, links);
    }

    static string HeadingText(BlockBox box)
    {
        var s = box.Style;
        var t = s.TextContent;
        if (string.IsNullOrWhiteSpace(t) && s.Children.Count > 0) t = SubtreeText(s);
        if (string.IsNullOrWhiteSpace(t) && box.WrappedLines is { Length: > 0 } wl) t = string.Join(" ", wl);
        if (string.IsNullOrWhiteSpace(t) && box.Lines is { Count: > 0 } ls)
        {
            var sb = new StringBuilder();
            foreach (var ln in ls)
                foreach (var f in ln.Frags)
                    if (f.Text != null) sb.Append(f.Text);
            t = sb.ToString();
        }
        t = Collapse(t ?? "");
        return t.Length > 0 ? t : s.Tag;
    }

    static string SubtreeText(ComputedStyle s)
    {
        var sb = new StringBuilder();
        void Rec(ComputedStyle c)
        {
            if (c.TextContent != null) sb.Append(c.TextContent).Append(' ');
            foreach (var k in c.Children) Rec(k);
        }
        Rec(s);
        return sb.ToString();
    }

    static string Collapse(string t) =>
        System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();

    /// <summary>只接受绝对 URI（外部链接）；#锚点等内部目标暂不做 GoTo。</summary>
    static bool IsUri(string href) =>
        href.Contains("://", StringComparison.Ordinal) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
}
