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
            // 只有 table-cell 里的行走「基线绝对下取整」；普通块跟着盒折、行内留分数（见 PaintSnap.BeginLine）
            int lnSnap = box.Style.Display == "table-cell" ? PaintSnap.BeginLine(canvas, baseline) : -1;
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
            PaintSnap.EndLine(canvas, lnSnap);
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

        var typeface = FontManager.Get(s.FontFamily, s.FontWeight, s.FontStyle);
        var color = (fillOverride ?? s.Color).WithAlpha(alpha);
        using var paint = NewTextPaint(typeface, f.FontSize, s.FontStyle, color, s.FontWeight);
        if (fillShader != null) { paint.Shader = fillShader; paint.Color = new SKColor(255, 255, 255, alpha); }
        // x/f.Width 已是 padding box；文字要让开元素自己的左 padding（背景与装饰照旧铺满整盒）
        if (s.TextShadow != null)
            DrawTextShadow(canvas, s, f.Text!, x + f.PadLeft, baseline, typeface, f.FontSize, s.LetterSpacing, false);
        DrawSpaced(canvas, f.Text!, x + f.PadLeft, baseline, paint, s.LetterSpacing, s.FontVariantCaps, s.FontStyle,
            s.FontFamilyList, s.FontWeight);
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
        using var paint = NewTextPaint(tf, fontSize, s.FontStyle, weight: s.FontWeight);   // 阴影层与正文同一条文本 paint 口径
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            var sh = layers[i];
            float sigma = sh.Blur / 2f;
            paint.Color = sh.Color.WithAlpha((byte)Math.Round(sh.Color.Alpha * op));
            paint.ImageFilter = sigma > 0.01f ? SKImageFilter.CreateBlur(sigma, sigma) : null;
            DrawVisualLine(canvas, text, x + sh.OffsetX, baseline + sh.OffsetY, paint, ls, rtl, s.FontVariantCaps, s.FontStyle,
                s.FontFamilyList, s.FontWeight);
        }
    }
    /// <summary>画 lines[first..first+count) 到 (left,top) 起的内容区，宽度 width。lefts 为每行左缩进（环绕浮动），可 null。
    /// <paramref name="lineBoxes"/> = 逐行行盒（回退字体撑高，见 <see cref="BlockBox.LineBoxes"/>），null 表示各行等高。
    /// fillShader/fillOverride 用于 background-clip:text（把背景渐变当作字形填充）。</summary>
    public static void DrawLines(SKCanvas canvas, ComputedStyle s, SKTypeface typeface,
        string[] lines, int first, int count, float left, float top, float width, float[]? lefts = null,
        SKShader? fillShader = null, SKColor? fillOverride = null, (float Asc, float Desc)[]? lineBoxes = null)
    {
        if (lines == null || count <= 0) return;
        byte alpha = (byte)Math.Round(Clamp01(s.Opacity) * 255);
        var color = (fillOverride ?? s.Color).WithAlpha(alpha);

        using var paint = NewTextPaint(typeface, s.FontSize, s.FontStyle, color, s.FontWeight);
        if (fillShader != null) { paint.Shader = fillShader; paint.Color = new SKColor(255, 255, 255, alpha); }
        float ls = s.LetterSpacing;
        float lineHeight = s.LineHeightPx();
        float baseline = top + (lineBoxes != null && first < lineBoxes.Length ? lineBoxes[first].Asc
                                                                                : InlineLayout.FirstBaselineOffset(s));

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

            int lnSnap = s.Display == "table-cell" ? PaintSnap.BeginLine(canvas, baseline) : -1;
            DrawTextShadow(canvas, s, line, x, baseline, typeface, s.FontSize, ls, s.Direction == "rtl");
            DrawVisualLine(canvas, line, x, baseline, paint, ls, s.Direction == "rtl", s.FontVariantCaps, s.FontStyle,
                s.FontFamilyList, s.FontWeight);
            DrawDecoration(canvas, s, x, baseline, textW, color);
            PaintSnap.EndLine(canvas, lnSnap);
            baseline += lineBoxes is { Length: > 0 } lb && idx < lb.Length
                ? lb[idx].Asc + lb[idx].Desc : lineHeight;
        }
    }

    /// <summary>画一行文本：按 bidi run 切段（RTL 基底方向反转 run 序），逐段走 shaped / 普通 / 回退通路。
    /// <paramref name="style"/> 是本行的 font-style：字素级换面时（一行混多脚本）斜身要跟着重判。
    /// <paramref name="list"/>/<paramref name="weight"/> 是**声明的** font-family 整条列表与字重：
    /// 逐字素回退要沿它走，不能只问主面（见 <see cref="TextShaper.ResolveTypeface(string,SKTypeface,string,int,string)"/>）。</summary>
    static void DrawVisualLine(SKCanvas canvas, string line, float x, float y, SKPaint paint, float ls, bool rtl,
        string caps = "normal", string style = "normal", string list = "", int weight = 400)
    {
        if (line.Length == 0) return;
        var tf = paint.Typeface ?? SKTypeface.Default;
        if (!rtl && !TextShaper.NeedsSpecial(line, tf)) { DrawSpaced(canvas, line, x, y, paint, ls, caps, style, list, weight); return; }
        var runs = Layout.TextShaper.BidiRuns(line);
        if (rtl) runs.Reverse();
        float cx = x;
        foreach (var (text, _) in runs)
        {
            DrawSpaced(canvas, text, cx, y, paint, ls, caps, style, list, weight);
            cx += TextShaper.WidthOf(text, tf, paint.TextSize, ls);
        }
    }

    static void DrawSpaced(SKCanvas canvas, string line, float x, float y, SKPaint paint, float ls,
        string caps = "normal", string style = "normal", string list = "", int weight = 400)
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
                    cx += DrawPlainRun(canvas, seg, cx, y, paint, ls, tf, caps, style, list, weight);
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
        {
            // 落笔必须与度量同口径：Skia 一次 DrawText 只按 hmtx 走位，字距是 TextMeasurer.LineWidth
            // 加进行宽的，不补就是「量得窄、画得宽」—— 配对之后的每个字形都比布局以为的位置更往右。
            // invoice 的 "To: 三花股份有限公司 · 采购部"：To 那一对 −1.56px，于是整段中文在叠加视图里
            // 重影 1.5px（同页 From/Date 无配对、Widget A 的配对为 0，所以只有那一行糊）。
            // 切段与推进的口径单源在 <see cref="Pdf.PdfGlyphUsage.DrawKerned"/>：@page 页眉页脚那条
            // 不走布局的路也吃它，两边不会各写一份字距落笔而漂开。
            Pdf.PdfGlyphUsage.DrawKerned(canvas, line, x, y, paint);
            return;
        }
        // 逐字素排布：缺字形的字素换覆盖字体（一行混多脚本时每字素独立回退），其余情况仅 ls 逐字加距
        DrawPlainRun(canvas, line, x, y, paint, ls, tf, caps, style, list, weight);
    }

    /// <summary>非 shaping 绘制：逐字素解析覆盖字体并排布，返回占用宽度。
    /// 字素之间补上该字面的字距对 —— 度量端 <c>TextShaper.WidthOf</c> 是按字面切段后段内加 Adjust 的，
    /// 这里不补就是「量得宽、画得窄」，同一段文字的行宽与落笔位置对不上（叠加视图里的重影）。
    /// caps ≠ normal 时小写字母改画**缩到 x-height 的大写**（顶线落在 x 高度、基线不动），
    /// 但推进仍按原来的小写 advance——Chrome 用的是字体自带的 GSUB smcp 字形，那些字形本就与
    /// 小写同宽（实测 "Small Caps 效果" 整串与小写排版只差 1.2px），所以布局一行都不用改。</summary>
    static float DrawPlainRun(SKCanvas canvas, string seg, float x, float y, SKPaint paint, float ls,
        SKTypeface tf, string caps = "normal", string style = "normal", string list = "", int weight = 400)
    {
        float cx = x;
        float size = paint.TextSize;
        SKTypeface? cur = tf;
        KernTable? curKt = KernTable.For(tf);
        float baseSkew = paint.TextSkewX;
        paint.Typeface = cur;
        try
        {
            int prevCp = -1;
            SKTypeface? prevGt = null;
            foreach (var g in TextShaper.Graphemes(seg))
            {
                var gt = TextShaper.ResolveTypeface(g, tf, list, weight, style);
                if (!ReferenceEquals(gt, cur))
                {
                    paint.Typeface = cur = gt;
                    curKt = KernTable.For(gt);
                    // 换面后重判斜身：真斜体族（Georgia/Arial）里的中文会回退到正立 CJK 面，
                    // 浏览器对那一个字仍是合成斜体，整行沿用主面的「不合成」就会让它站得笔直。
                    paint.TextSkewX = -FontManager.SkewFor(style, gt);
                    // 合成粗体同理按字面重判：主面 Consolas 有真粗体、回退到的 NSimSun 没有，
                    // 不换这个旗就会「量 13.586、画 13.328」，行内逐字累积成重影。
                    paint.FakeBoldText = weight >= 700 && FontManager.LacksRealBold(gt.FamilyName);
                }
                // 只在「相邻两个字素同属一张字面」时配对：跨字面那一对在度量端被切进不同段，两边都不算
                if (prevCp >= 0 && ReferenceEquals(gt, prevGt))
                    cx += curKt?.Pair(prevCp, KernTable.FirstCp(g), size) ?? 0f;
                // 推进必须与布局同口径：等宽链（Chrome 把 generic monospace 落到 NSimSun 后按整「半格」走位）
                // 在度量端走 MonoGridWidth 给 15，绘制端若仍吃字面自己的 hmtx（13.33）就「量得宽、画得窄」——
                // 右对齐的 .num 格里每个字素都比浏览器往左偏一点、且逐字累积，那几列的重影即此。
                float grid = TextShaper.MonoGridWidth(g, tf, size, 0f, list, weight);
                float w = grid >= 0 ? grid : paint.MeasureText(g);
                char ch = g.Length == 1 ? g[0] : '\0';
                bool fold = caps != "normal" && (char.IsLower(ch) || (caps == "all-small-caps" && char.IsUpper(ch)));
                if (fold)
                {
                    // Chrome 用 GSUB smcp 字形的 advance（与小写/大写都不同），逐字素补这块差
                    w += Layout.SmallCaps.DeltaOf(g, cur ?? tf, size, caps);
                    paint.TextSize = size * SmallCapsScale(cur ?? tf);
                    Pdf.PdfGlyphUsage.DrawText(canvas, char.ToUpperInvariant(ch).ToString(), cx, y, paint);
                    paint.TextSize = size;
                }
                else Pdf.PdfGlyphUsage.DrawText(canvas, g, cx, y, paint);
                cx += w + ls;
                prevCp = KernTable.LastCp(g);
                prevGt = gt;
            }
        }
        finally
        {
            paint.Typeface = tf;
            paint.TextSkewX = baseSkew;
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

    /// <summary>建一张**落笔与量宽共用**的文本 paint。三样必须一起带上，缺一样就会有第二个表面量出
    /// 第二个数：<c>IsAntialias</c> + <c>SubpixelText</c>（Skia 只在两旗齐备时才用分数推进，否则把每个
    /// 字形的推进折成整像素，P2-9：9 个字偏 3px），以及斜体的合成 <c>TextSkewX</c>（错切会进 advance，
    /// 实测 italic 的「AV To Ty Ta Wa Ye」两表面差 1.08px —— 别信"错切不动 advance"）。字面自带斜身
    /// （Arial Italic 那种）时 <see cref="FontManager.SkewFor"/> 给 0，交给字面自己。
    /// 凡要画字/量字的地方一律走这里，不要手抄旗。</summary>
    public static SKPaint NewTextPaint(SKTypeface typeface, float size, string fontStyle, SKColor? color = null,
        int weight = 0)
        => new()
        {
            Color = color ?? SKColors.Black,
            TextSize = size,
            Typeface = typeface,
            IsAntialias = true,
            SubpixelText = true,
            Style = SKPaintStyle.Fill,
            TextSkewX = -FontManager.SkewFor(fontStyle, typeface),
            // 没有真粗体面的字族（NSimSun/宋体一类）请求粗体时，浏览器合成粗体并加宽 advance；
            // Skia 只在 paint 上开 FakeBoldText 才加宽，度量与绘制必须同开（见 FontManager.LacksRealBold）。
            FakeBoldText = weight >= 700 && FontManager.LacksRealBold(typeface?.FamilyName),
        };

    /// <summary>整行文本的推进宽度（hmtx + 字距 + letter-spacing），既喂给 text-align 的起笔 x，
    /// 也喂给下划线/删除线的长度。光栅（<see cref="DrawLines"/>）与 SVG（<c>SvgWriter.EmitBlockText</c>）
    /// 共用这一条公式，且**连度量旗也在这里统一**（见函数体注释）——否则两个表面对同一串给出两个宽，
    /// 贴右/居中的落点就与光栅不一致（P2-9）。</summary>
    public static float MeasureWidth(string s, SKPaint paint, float letterSpacing)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var tf = paint.Typeface ?? SKTypeface.Default;
        if (TextShaper.NeedsSpecial(s, tf))
            return TextShaper.WidthOf(s, tf, paint.TextSize, letterSpacing);
        // 必须与绘制端 <see cref="DrawSpaced"/> 同一口径（hmtx + 字距）：少了字距就是「量得宽、画得窄」
        // ——右对齐整行往左跑，居中对齐往左跑一半（实测 Segoe UI 20px「AV To Ty Ta Wa Ye」字距合计
        // −9.20px：text-align:right 行首 dx −9.20、:center 行首 dx 正好是它的一半 −4.6 量级）。
        //
        // 但**光靠调用方的 paint 未必量得出同一个数**：旗是 paint 上的状态，谁手搓一张就会漏带。
        // 所以五个构造点一律走 <see cref="NewTextPaint"/>；这里再兜一道——调用方旗不齐就补一张本地的
        // （两旗齐备 + 沿用调用方的 skew），今后谁新增表面都不会又量出第二个宽。实测同一张字面实例
        // （Handle 相同）下：AA+subpixel 全开 = 11.318、只开 subpixel = 11、全关 = 11。
        if (!paint.SubpixelText || !paint.IsAntialias)
        {
            using var mp = new SKPaint
            {
                Typeface = tf, TextSize = paint.TextSize, Style = SKPaintStyle.Fill,
                SubpixelText = true, IsAntialias = true, TextSkewX = paint.TextSkewX,
            };
            return mp.MeasureText(s) + KernTable.Of(s, tf, paint.TextSize) + letterSpacing * s.Length;
        }
        return paint.MeasureText(s) + KernTable.Of(s, tf, paint.TextSize) + letterSpacing * s.Length;
    }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
