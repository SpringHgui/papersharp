using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Paint;

/// <summary>
/// 文本绘制：按行画，处理 text-align、letter-spacing（逐字排布）、text-decoration、opacity。
/// PNG 与 PDF 两条路径共用，避免逻辑漂移。
/// </summary>
public static class TextPainter
{
    /// <summary>
    /// 画 inline 行盒里的文本片段（原子盒由调用方另画）。以 (contentLeft,contentTop) 为内容原点，
    /// 画 lines[firstLine..firstLine+lineCount)。每段的样式（字体/色/背景/字距/装饰）取自 frag 自己的 ComputedStyle。
    /// </summary>
    public static void DrawInlineLines(SKCanvas canvas, BlockBox box,
        float contentLeft, float contentTop, int firstLine, int lineCount,
        SKShader? fillShader = null, SKColor? fillOverride = null)
    {
        var lines = box.Lines;
        if (lines == null || lineCount <= 0) return;
        float y = contentTop;
        for (int i = 0; i < firstLine && i < lines.Count; i++) y += lines[i].Height;
        int end = Math.Min(lines.Count, firstLine + lineCount);
        // 行内边框按 box-decoration-break: slice 走：跨行的行内盒只在整盒的首/末片段画竖边。
        var caps = InlineBorderCaps(lines, firstLine, end);
        for (int i = firstLine; i < end; i++)
        {
            var ln = lines[i];
            float lineLeft = contentLeft + ln.Left;
            float baseline = y + ln.Baseline;
            foreach (var f in ln.Frags)
            {
                if (f.Text == null) continue;
                bool capL = true, capR = true;
                if (caps != null && caps.TryGetValue(f.Style, out var span))
                {
                    capL = ReferenceEquals(span.First, f);
                    capR = ReferenceEquals(span.Last, f);
                }
                DrawRun(canvas, f, lineLeft + f.X, baseline + f.AlignDY, fillShader, fillOverride, capL, capR);
            }
            y += ln.Height;
        }
    }

    static Dictionary<ComputedStyle, (InlineFrag First, InlineFrag Last)>? InlineBorderCaps(
        List<LineBox> lines, int firstLine, int end)
    {
        Dictionary<ComputedStyle, (InlineFrag First, InlineFrag Last)>? caps = null;
        for (int i = firstLine; i < end; i++)
            foreach (var f in lines[i].Frags)
            {
                var st = f.Style;
                if (f.Text == null || f.HostText || st.BorderWidthTop <= 0 && st.BorderWidthBottom <= 0
                                               && st.BorderWidthLeft <= 0 && st.BorderWidthRight <= 0) continue;
                caps ??= new(ReferenceEqualityComparer.Instance);
                if (caps.TryGetValue(st, out var cur)) caps[st] = (cur.First, f);
                else caps[st] = (f, f);
            }
        return caps;
    }

    static void DrawRun(SKCanvas canvas, InlineFrag f, float x, float baseline,
        SKShader? fillShader = null, SKColor? fillOverride = null, bool capLeft = true, bool capRight = true)
    {
        var s = f.Style;
        float op = Clamp01(s.Opacity);
        byte alpha = (byte)Math.Round(op * 255);

        // inline 背景：CSS 里行内盒只有**字体内容区**那么高，line-height 只撑行盒、不撑它自己，
        // 按 字号×行高 画会让每块 code 芯片比浏览器高出一截（m8 的 h1：37.4px vs 浏览器 25.5px）。
        // 口径与 Renderer.CollectInlineBoxes 报的行内盒一致：基线 ± 片段真实 ascent/descent
        // （baseline 已由调用方折进 vertical-align 的下移），纵向补元素自己的 padding+border（横向 Width 已含）。
        // 块自身的文字（HostText）一律跳过：它的 Style 就是块样式，铺出来的"行内背景"实为
        // 块底色按块 padding 外扩，会把上一行文字的下半截盖掉（m4 卡片标题被裁的根因）。
        if (!f.HostText)
        {
            float vTop = s.BorderWidthTop + s.PaddingTop, vBot = s.BorderWidthBottom + s.PaddingBottom;
            var box = new SKRect(x, baseline - f.Ascent - vTop, x + f.Width, baseline + f.Descent + vBot);
            if (!s.BackgroundColor.Equals(SKColor.Empty))
                BoxPainter.PaintInlineBackground(canvas, box, s,
                    s.BackgroundColor.WithAlpha((byte)Math.Round(s.BackgroundColor.Alpha * op)));
            BoxPainter.PaintInlineBorder(canvas, box, s, op, capLeft, capRight);
        }

        var typeface = FontManager.Get(s.FontFamily, s.FontWeight);
        var color = (fillOverride ?? s.Color).WithAlpha(alpha);
        using var paint = new SKPaint
        {
            Color = color,
            TextSize = f.FontSize,
            Typeface = typeface,
            IsAntialias = true,
            SubpixelText = true,
            Style = SKPaintStyle.Fill,
        };
        if (fillShader != null) { paint.Shader = fillShader; paint.Color = new SKColor(255, 255, 255, alpha); }
        // x/f.Width 已是 padding box；文字要让开元素自己的左 padding（背景与装饰照旧铺满整盒）
        if (s.TextShadow != null)
            DrawTextShadow(canvas, s, f.Text!, x + f.PadLeft, baseline, typeface, f.FontSize, s.LetterSpacing, false);
        DrawSpaced(canvas, f.Text!, x + f.PadLeft, baseline, paint, s.LetterSpacing, s.FontVariantCaps);
        DrawDecoration(canvas, s, x, baseline, f.Width, color);
    }

    /// <summary>text-shadow：把同一串字形按各层的颜色/偏移/模糊再画一遍，垫在真字底下。层序按 CSS
    /// （第一条画在最上层）倒着铺。走 <see cref="DrawVisualLine"/> 与正文同一条 shaping/回退通路，
    /// PDF 的字形子集记账才不会把这些副本字形漏掉（漏了就是成品里阴影整块空白）。</summary>
    static void DrawTextShadow(SKCanvas canvas, ComputedStyle s, string text, float x, float baseline,
        SKTypeface tf, float fontSize, float ls, bool rtl)
    {
        var layers = s.TextShadow;
        if (layers == null || text.Length == 0) return;
        float op = Clamp01(s.Opacity);
        using var paint = new SKPaint
        {
            TextSize = fontSize,
            Typeface = tf,
            IsAntialias = true,
            SubpixelText = true,
            Style = SKPaintStyle.Fill,
        };
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            var sh = layers[i];
            float sigma = sh.Blur / 2f;
            paint.Color = sh.Color.WithAlpha((byte)Math.Round(sh.Color.Alpha * op));
            paint.ImageFilter = sigma > 0.01f ? SKImageFilter.CreateBlur(sigma, sigma) : null;
            DrawVisualLine(canvas, text, x + sh.OffsetX, baseline + sh.OffsetY, paint, ls, rtl, s.FontVariantCaps);
        }
    }
    /// <summary>画 lines[first..first+count) 到 (left,top) 起的内容区，宽度 width。lefts 为每行左缩进（环绕浮动），可 null。
    /// fillShader/fillOverride 用于 background-clip:text（把背景渐变当作字形填充）。</summary>
    public static void DrawLines(SKCanvas canvas, ComputedStyle s, SKTypeface typeface,
        string[] lines, int first, int count, float left, float top, float width, float[]? lefts = null,
        SKShader? fillShader = null, SKColor? fillOverride = null)
    {
        if (lines == null || count <= 0) return;
        byte alpha = (byte)Math.Round(Clamp01(s.Opacity) * 255);
        var color = (fillOverride ?? s.Color).WithAlpha(alpha);

        using var paint = new SKPaint
        {
            Color = color,
            TextSize = s.FontSize,
            Typeface = typeface,
            IsAntialias = true,
            SubpixelText = true,
            Style = SKPaintStyle.Fill,
        };
        if (fillShader != null) { paint.Shader = fillShader; paint.Color = new SKColor(255, 255, 255, alpha); }
        float ls = s.LetterSpacing;
        float lineHeight = s.LineHeightPx();
        float baseline = top + InlineLayout.FirstBaselineOffset(s);

        for (int k = 0; k < count; k++)
        {
            int idx = first + k;
            if (idx >= lines.Length) break;
            var line = lines[idx];
            float inset = lefts != null && idx < lefts.Length ? lefts[idx] : 0;
            float textW = MeasureWidth(line, paint, ls);

            float x = left + inset;
            if (s.TextAlignment == CssTextAlign.Center) x = left + inset + (width - inset - textW) / 2;
            else if (s.TextAlignment == CssTextAlign.Right) x = left + width - textW;

            DrawTextShadow(canvas, s, line, x, baseline, typeface, s.FontSize, ls, s.Direction == "rtl");
            DrawVisualLine(canvas, line, x, baseline, paint, ls, s.Direction == "rtl", s.FontVariantCaps);
            DrawDecoration(canvas, s, x, baseline, textW, color);
            baseline += lineHeight;
        }
    }

    /// <summary>画一行文本：按 bidi run 切段（RTL 基底方向反转 run 序），逐段走 shaped / 普通 / 回退通路。</summary>
    static void DrawVisualLine(SKCanvas canvas, string line, float x, float y, SKPaint paint, float ls, bool rtl,
        string caps = "normal")
    {
        if (line.Length == 0) return;
        var tf = paint.Typeface ?? SKTypeface.Default;
        if (!rtl && !TextShaper.NeedsSpecial(line, tf)) { DrawSpaced(canvas, line, x, y, paint, ls, caps); return; }
        var runs = Layout.TextShaper.BidiRuns(line);
        if (rtl) runs.Reverse();
        float cx = x;
        foreach (var (text, _) in runs)
        {
            DrawSpaced(canvas, text, cx, y, paint, ls, caps);
            cx += TextShaper.WidthOf(text, tf, paint.TextSize, ls);
        }
    }

    static void DrawSpaced(SKCanvas canvas, string line, float x, float y, SKPaint paint, float ls,
        string caps = "normal")
    {
        if (line.Length == 0) return;
        var tf = paint.Typeface ?? SKTypeface.Default;
        if (TextShaper.NeedsShaping(line))
        {
            // 按字素切「shaping / 普通」交替段：shaping 段整体走 HarfBuzz（阿拉伯连写保持成组），
            // 普通段逐字素回退——避免整行锁进单一回退字体让另一脚本出豆腐块。
            float cx = x;
            var sb = new System.Text.StringBuilder();
            bool segShaped = false;
            void Flush()
            {
                if (sb.Length == 0) return;
                string seg = sb.ToString();
                if (segShaped && TextShaper.DrawShaped(canvas, seg, cx, y, paint))
                    cx += TextShaper.WidthOf(seg, tf, paint.TextSize, 0); // shaped 段不加 ls（已知限制）
                else
                    cx += DrawPlainRun(canvas, seg, cx, y, paint, ls, tf, caps);
                sb.Clear();
            }
            foreach (var g in TextShaper.Graphemes(line))
            {
                bool sh = TextShaper.NeedsShaping(g);
                if (sb.Length > 0 && sh != segShaped) Flush();
                if (sb.Length == 0) segShaped = sh;
                sb.Append(g);
            }
            Flush();
            return;
        }
        bool special = TextShaper.FirstUncovered(line, tf) != 0;
        if (!special && Math.Abs(ls) < 0.01f && caps == "normal")
        { Pdf.PdfGlyphUsage.DrawText(canvas, line, x, y, paint); return; }
        // 逐字素排布：缺字形的字素换覆盖字体（一行混多脚本时每字素独立回退），其余情况仅 ls 逐字加距
        DrawPlainRun(canvas, line, x, y, paint, ls, tf, caps);
    }

    /// <summary>非 shaping 绘制：逐字素解析覆盖字体并排布，返回占用宽度。
    /// caps ≠ normal 时小写字母改画**缩到 x-height 的大写**（顶线落在 x 高度、基线不动），
    /// 但推进仍按原来的小写 advance——Chrome 用的是字体自带的 GSUB smcp 字形，那些字形本就与
    /// 小写同宽（实测 "Small Caps 效果" 整串与小写排版只差 1.2px），所以布局一行都不用改。</summary>
    static float DrawPlainRun(SKCanvas canvas, string seg, float x, float y, SKPaint paint, float ls,
        SKTypeface tf, string caps = "normal")
    {
        float cx = x;
        float size = paint.TextSize;
        SKTypeface? cur = tf;
        paint.Typeface = cur;
        try
        {
            foreach (var g in TextShaper.Graphemes(seg))
            {
                var gt = TextShaper.ResolveTypeface(g, tf);
                if (!ReferenceEquals(gt, cur)) paint.Typeface = cur = gt;
                float w = paint.MeasureText(g);
                char ch = g.Length == 1 ? g[0] : '\0';
                bool fold = caps != "normal" && (char.IsLower(ch) || (caps == "all-small-caps" && char.IsUpper(ch)));
                if (fold)
                {
                    paint.TextSize = size * SmallCapsScale(cur ?? tf);
                    Pdf.PdfGlyphUsage.DrawText(canvas, char.ToUpperInvariant(ch).ToString(), cx, y, paint);
                    paint.TextSize = size;
                }
                else Pdf.PdfGlyphUsage.DrawText(canvas, g, cx, y, paint);
                cx += w + ls;
            }
        }
        finally
        {
            paint.Typeface = tf;
        }
        return cx - x - ls; // 末字素后不加间距
    }

    // small-caps 的缩放系数 = x-height / cap-height（缩出来的大写正好顶到 x 高度）。只跟字面有关，按字面缓存。
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> SmallCapsK = new();

    static float SmallCapsScale(SKTypeface tf)
        => SmallCapsK.GetOrAdd(tf.FamilyName + '/' + tf.FontWeight, _ =>
        {
            using var f = new SKFont(tf, 100f);
            var m = f.Metrics;
            return m.CapHeight > 0.01f ? Math.Clamp(m.XHeight / m.CapHeight, 0.4f, 1f) : 0.72f;
        });

    static void DrawDecoration(SKCanvas canvas, ComputedStyle s, float x, float baseline, float width, SKColor color)
    {
        if (s.TextDecoration == "none" || width <= 0) return;
        float thickness = Math.Max(0.8f, s.FontSize / 12f);
        using var p = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        if (s.TextDecoration.Contains("underline"))
            canvas.DrawRect(x, baseline + s.FontSize * 0.14f, width, thickness, p);
        if (s.TextDecoration.Contains("line-through"))
            canvas.DrawRect(x, baseline - s.FontSize * 0.32f, width, thickness, p);
        if (s.TextDecoration.Contains("overline"))
            canvas.DrawRect(x, baseline - s.FontSize * 0.86f, width, thickness, p);
    }

    public static float MeasureWidth(string s, SKPaint paint, float letterSpacing)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var tf = paint.Typeface ?? SKTypeface.Default;
        if (TextShaper.NeedsSpecial(s, tf))
            return TextShaper.WidthOf(s, tf, paint.TextSize, letterSpacing);
        return paint.MeasureText(s) + letterSpacing * s.Length;
    }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
