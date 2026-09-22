using System.Globalization;
using System.Text;
using SkiaSharp;
using PaperSharp.Css;
using PaperSharp.Layout;

namespace PaperSharp.Paint;

/// <summary>
/// 把已定位（Position 过）的盒子树序列化为 SVG 文档。
/// 覆盖：背景（纯色/linear/radial 渐变，conic 降级为首末色 linear）、圆角、边框、
/// 文本行（含对齐/字距/装饰线）、inline 行盒、图片（base64 内嵌）、内联 SVG（原样嵌入）、
/// clip-path / mix-blend-mode / filter（经 style 属性交给浏览器 SVG 的 CSS 支持）。
/// 简化：mask 未输出；四角半径不等时取均值；边框四边取最大宽度（与位图路径一致）。
/// </summary>
public static class SvgWriter
{
    public static byte[] Write(BlockBox root, float pageW, float pageH)
    {
        var defs = new StringBuilder();
        var body = new StringBuilder();
        var w = new Writer(defs, body);

        w.PaintFlow(root);
        w.PaintAbsolute(root);

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
                  $"width=\"{F(pageW)}\" height=\"{F(pageH)}\" viewBox=\"0 0 {F(pageW)} {F(pageH)}\">");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>");
        if (defs.Length > 0) sb.Append("<defs>").Append(defs).Append("</defs>");
        sb.Append(body);
        sb.Append("</svg>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string F4(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    static string Color(SKColor c) => $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";
    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: if (ch >= 0x20 || ch == '\t') sb.Append(ch); break;
            }
        return sb.ToString();
    }

    sealed class Writer
    {
        readonly StringBuilder _defs, _body;
        int _id;
        readonly Dictionary<GradientSpec, string> _gradIds = new();
        readonly Dictionary<uint, string> _imgUris = new();

        public Writer(StringBuilder defs, StringBuilder body) { _defs = defs; _body = body; }

        string NextId(string prefix) => prefix + (++_id).ToString(CultureInfo.InvariantCulture);

        public void PaintFlow(BlockBox box)
        {
            Emit(box);
            foreach (var c in box.Children)
                if (c.Style.Position != "absolute") PaintFlow(c);
        }

        public void PaintAbsolute(BlockBox root)
        {
            var abs = new List<BlockBox>();
            Collect(root, abs);
            abs.Sort((a, b) =>
            {
                bool ah = a.Style.HasZIndex, bh = b.Style.HasZIndex;
                if (!ah && !bh) return 0;
                if (!ah) return -1;
                if (!bh) return 1;
                return a.Style.ZIndex.CompareTo(b.Style.ZIndex);
            });
            foreach (var b in abs) EmitFull(b);

            static void Collect(BlockBox box, List<BlockBox> r)
            {
                foreach (var c in box.Children)
                {
                    if (c.Style.Position == "absolute") r.Add(c);
                    else Collect(c, r);
                }
            }
        }

        void EmitFull(BlockBox box)
        {
            Emit(box);
            foreach (var c in box.Children) EmitFull(c);
        }

        /// <summary>画一个盒的自身内容（装饰+文本+替换内容），与 Painter.PaintSelf 对齐。</summary>
        void Emit(BlockBox box)
        {
            var s = box.Style;
            if (s.Display == "none") return;
            float bw = s.BorderWidthLeft + s.PaddingLeft + box.ContentWidth + s.PaddingRight + s.BorderWidthRight;
            float bh = s.BorderWidthTop + s.PaddingTop + box.ContentHeight + s.PaddingBottom + s.BorderWidthBottom;
            if (bw <= 0 || bh <= 0) return;
            float x = box.BorderX, y = box.BorderY;
            float opacity = Clamp01(s.Opacity <= 0 ? 0 : s.Opacity);
            if (opacity <= 0.001f) return;

            var g = new StringBuilder();
            if (opacity < 0.999f) g.Append($" opacity=\"{F4(opacity)}\"");
            string style = EffectStyle(s);
            if (style.Length > 0) g.Append($" style=\"{style}\"");
            string? clipId = ClipDef(s, new SKRect(x, y, x + bw, y + bh));
            if (clipId != null) g.Append($" clip-path=\"url(#{clipId})\"");
            bool wrapped = g.Length > 0;
            if (wrapped) _body.Append($"<g{g}>");

            // 背景
            float rx = UniformRadius(s);
            if (s.Background != null)
            {
                string id = GradientId(s.Background, new SKRect(x, y, x + bw, y + bh));
                _body.Append(Shape(s, x, y, bw, bh, rx, $"fill=\"url(#{id})\""));
            }
            else if (!s.BackgroundColor.Equals(SKColor.Empty) && s.BackgroundColor.Alpha > 0)
            {
                var bc = s.BackgroundColor;
                _body.Append(Shape(s, x, y, bw, bh, rx,
                    $"fill=\"{Color(bc)}\" fill-opacity=\"{F4(bc.Alpha / 255f)}\""));
            }

            // 阴影（box-shadow → filter: drop-shadow，浏览器 SVG 支持 CSS filter 函数）
            // 已由 EffectStyle 输出？否——box-shadow 不在 filter 里，单独补：
            if (s.Shadow != null)
            {
                var sh = s.Shadow;
                _body.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(bw)}\" height=\"{F(bh)}\" rx=\"{F4(rx)}\" " +
                    $"fill=\"none\" style=\"filter:drop-shadow({F(sh.OffsetX)}px {F(sh.OffsetY)}px {F(sh.Blur / 2f)}px {Color(sh.Color)})\"/>");
            }

            // 边框
            float bt = Math.Max(s.BorderWidthTop, Math.Max(s.BorderWidthRight, Math.Max(s.BorderWidthBottom, s.BorderWidthLeft)));
            if (bt > 0)
                _body.Append(Shape(s, x + bt / 2, y + bt / 2, bw - bt, bh - bt, Math.Max(0, rx - bt / 2),
                    $"fill=\"none\" stroke=\"{Color(s.BorderColor)}\" stroke-opacity=\"{F4(s.BorderColor.Alpha / 255f)}\" stroke-width=\"{F(bt)}\""));

            float contentX = x + s.BorderWidthLeft + s.PaddingLeft;
            float contentY = y + s.BorderWidthTop + s.PaddingTop + box.ValignDY;

            // 替换内容
            if (box.Image != null) EmitImage(box.Image, contentX, contentY, box.ContentWidth, box.ContentHeight);
            else if (box.SvgPicture != null) EmitSvgMarkup(s, contentX, contentY, box.ContentWidth, box.ContentHeight, box.NaturalW, box.NaturalH);

            // 文本
            if (box.WrappedLines != null) EmitBlockText(box, contentX, contentY);
            else if (box.Lines != null) EmitInlineLines(box, contentX, contentY);

            if (wrapped) _body.Append("</g>");
        }

        /// <summary>rect 或圆角不统一时的 path 近似（用 rect + 平均 rx 已足够，这里统一 rect）。</summary>
        static string Shape(ComputedStyle s, float x, float y, float w, float h, float rx, string fillAttrs) =>
            $"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(Math.Max(0, w))}\" height=\"{F(Math.Max(0, h))}\"" +
            (rx > 0.05f ? $" rx=\"{F4(rx)}\"" : "") + $" {fillAttrs}/>";

        static float UniformRadius(ComputedStyle s) =>
            (s.BorderRadiusTopLeft + s.BorderRadiusTopRight + s.BorderRadiusBottomRight + s.BorderRadiusBottomLeft) / 4f;

        string EffectStyle(ComputedStyle s)
        {
            var sb = new StringBuilder();
            if (s.MixBlendMode != "normal") sb.Append($"mix-blend-mode:{s.MixBlendMode};");
            if (s.Filter != null)
            {
                var f = s.Filter;
                var parts = new List<string>();
                if (f.Blur > 0.01f) parts.Add($"blur({F(f.Blur / 2f)}px)");
                if (f.HasShadow) parts.Add($"drop-shadow({F(f.ShadowX)}px {F(f.ShadowY)}px {F(f.ShadowBlur / 2f)}px {Color(f.ShadowColor)})");
                if (parts.Count > 0) sb.Append("filter:").Append(string.Join(" ", parts)).Append(';');
            }
            return sb.ToString();
        }

        string? ClipDef(ComputedStyle s, SKRect r)
        {
            if (s.ClipPath == null) return null;
            string v = s.ClipPath.Trim();
            string? shape = null;
            try
            {
                if (v.StartsWith("circle(", StringComparison.OrdinalIgnoreCase))
                {
                    var toks = v[7..v.LastIndexOf(')')].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    float rad = Pct(toks[0], Math.Max(r.Width, r.Height) / 2f);
                    float cx = r.MidX, cy = r.MidY;
                    int at = Array.FindIndex(toks, t => t == "at");
                    if (at >= 0 && at + 2 < toks.Length) { cx = r.Left + Pct(toks[at + 1], r.Width); cy = r.Top + Pct(toks[at + 2], r.Height); }
                    shape = $"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(Math.Max(0, rad))}\"/>";
                }
                else if (v.StartsWith("inset(", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = v[6..v.LastIndexOf(')')].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    float T() => Pct(parts[0], r.Height);
                    float R() => Pct(parts.Length > 3 ? parts[1] : parts.Length > 1 ? parts[1] : parts[0], r.Width);
                    float B() => Pct(parts.Length > 3 ? parts[2] : parts[0], r.Height);
                    float L() => Pct(parts.Length > 3 ? parts[3] : parts.Length > 1 ? parts[1] : parts[0], r.Width);
                    float round = 0;
                    int ri = Array.FindIndex(parts, p => p == "round");
                    if (ri >= 0 && ri + 1 < parts.Length) round = Pct(parts[ri + 1], r.Width);
                    shape = $"<rect x=\"{F(r.Left + L())}\" y=\"{F(r.Top + T())}\" width=\"{F(Math.Max(0, r.Width - L() - R()))}\" height=\"{F(Math.Max(0, r.Height - T() - B()))}\" rx=\"{F(round)}\"/>";
                }
                else if (v.StartsWith("polygon(", StringComparison.OrdinalIgnoreCase))
                {
                    var pts = new List<string>();
                    foreach (var pair in v[8..v.LastIndexOf(')')].Split(','))
                    {
                        var xy = pair.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (xy.Length < 2) continue;
                        pts.Add($"{F(r.Left + Pct(xy[0], r.Width))},{F(r.Top + Pct(xy[1], r.Height))}");
                    }
                    if (pts.Count >= 3) shape = $"<polygon points=\"{string.Join(" ", pts)}\"/>";
                }
            }
            catch { return null; }
            if (shape == null) return null;
            string id = NextId("clip");
            _defs.Append($"<clipPath id=\"{id}\">{shape}</clipPath>");
            return id;
        }

        static float Pct(string t, float base_)
        {
            t = t.Trim();
            if (t.EndsWith("%")) return CssValueParser.ParseFloat(t[..^1]) / 100f * base_;
            return CssValueParser.ToPixels(t, 16);
        }

        void EmitImage(SKImage img, float x, float y, float w, float h)
        {
            if (!_imgUris.TryGetValue(img.UniqueId, out var uri))
            {
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                uri = "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
                _imgUris[img.UniqueId] = uri;
            }
            _body.Append($"<image x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" preserveAspectRatio=\"none\" href=\"{uri}\"/>");
        }

        void EmitSvgMarkup(ComputedStyle s, float x, float y, float w, float h, float natW, float natH)
        {
            var markup = s.SvgMarkup;
            if (string.IsNullOrEmpty(markup)) return;
            // 原标记按自然尺寸嵌入；盒尺寸不同则等比拉伸
            var sb = new StringBuilder();
            sb.Append($"<g transform=\"translate({F(x)},{F(y)})");
            if (natW > 0 && natH > 0 && (Math.Abs(w - natW) > 0.5f || Math.Abs(h - natH) > 0.5f))
                sb.Append($" scale({F4(w / natW)},{F4(h / natH)})");
            sb.Append("\">").Append(markup).Append("</g>");
            _body.Append(sb);
        }

        string GradientId(GradientSpec g, SKRect r)
        {
            if (_gradIds.TryGetValue(g, out var exist)) return exist;
            string id = NextId("grad");
            _gradIds[g] = id;
            var stops = new StringBuilder();
            var pos = StopPositions(g, r);
            for (int i = 0; i < g.Colors.Length; i++)
            {
                var c = g.Colors[i];
                stops.Append($"<stop offset=\"{F4(pos[i])}\" stop-color=\"{Color(c)}\" stop-opacity=\"{F4(c.Alpha / 255f)}\"/>");
            }
            if (g.Radial)
            {
                float rad = Math.Max(r.Width, r.Height) / 2f;
                _defs.Append($"<radialGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" cx=\"{F(r.MidX)}\" cy=\"{F(r.MidY)}\" r=\"{F(rad)}\">{stops}</radialGradient>");
            }
            else
            {
                // conic 降级：首→末色线性（顶→底）
                double rad2 = ((g.Conic ? 180 : g.AngleDeg) - 90) * Math.PI / 180.0;
                float dx = (float)Math.Cos(rad2), dy = (float)Math.Sin(rad2);
                float proj = Math.Abs(dx) * r.Width / 2f + Math.Abs(dy) * r.Height / 2f;
                _defs.Append($"<linearGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" " +
                    $"x1=\"{F(r.MidX - dx * proj)}\" y1=\"{F(r.MidY - dy * proj)}\" x2=\"{F(r.MidX + dx * proj)}\" y2=\"{F(r.MidY + dy * proj)}\">{stops}</linearGradient>");
            }
            return id;
        }

        /// <summary>停止位 0..1（与 GradientSpec.ResolvePositions 同逻辑的简化复制：Stops 优先，NaN 均匀）。</summary>
        static float[] StopPositions(GradientSpec g, SKRect r)
        {
            int n = g.Colors.Length;
            var pos = new float[n];
            for (int i = 0; i < n; i++)
            {
                float v = g.Stops != null && i < g.Stops.Length ? g.Stops[i] : float.NaN;
                if (float.IsNaN(v)) v = n == 1 ? 0 : (float)i / (n - 1);
                pos[i] = Math.Clamp(v, 0f, 1f);
                if (i > 0 && pos[i] < pos[i - 1]) pos[i] = pos[i - 1];
            }
            return pos;
        }

        void EmitBlockText(BlockBox box, float left, float top)
        {
            var s = box.Style;
            var typeface = box.Typeface;
            using var paint = new SKPaint { TextSize = s.FontSize, Typeface = typeface };
            float lineHeight = s.FontSize * s.LineHeightFactor;
            float baseline = top + s.FontSize;
            float width = box.ContentWidth;
            for (int i = 0; i < box.WrappedLines!.Length; i++)
            {
                string line = box.WrappedLines[i];
                if (line.Length == 0) { baseline += lineHeight; continue; }
                float inset = box.LineLefts != null && i < box.LineLefts.Length ? box.LineLefts[i] : 0;
                float textW = paint.MeasureText(line) + s.LetterSpacing * Math.Max(0, line.Length - 1);
                float x = left + inset;
                if (s.TextAlignment == CssTextAlign.Center) x = left + inset + (width - inset - textW) / 2;
                else if (s.TextAlignment == CssTextAlign.Right) x = left + width - textW;
                EmitText(line, x, baseline, s, s.FontSize, textW);
                baseline += lineHeight;
            }
        }

        void EmitInlineLines(BlockBox box, float left, float top)
        {
            var lines = box.Lines!;
            float y = top;
            foreach (var ln in lines)
            {
                float lineLeft = left + ln.Left;
                float baseline = y + ln.Baseline;
                foreach (var f in ln.Frags)
                {
                    if (f.Text == null) continue;
                    EmitTextRun(f, lineLeft + f.X, baseline + f.AlignDY);
                }
                y += ln.Height;
            }
            // 原子盒（inline-block / 行内 img / svg）：已定位，直接递归
            foreach (var ln in lines)
                foreach (var f in ln.Frags)
                    if (f.Box != null) EmitFull(f.Box);
        }

        void EmitText(string text, float x, float y, ComputedStyle s, float fontSize, float width)
        {
            EmitDecor(text, x, y, s, fontSize, width);
            _body.Append(TextEl(text, x, y, s, fontSize));
        }

        void EmitTextRun(InlineFrag f, float x, float y)
        {
            var s = f.Style;
            EmitDecor(f.Text!, x, y, s, f.FontSize, f.Width);
            _body.Append(TextEl(f.Text!, x, y, s, f.FontSize));
        }

        string TextEl(string text, float x, float y, ComputedStyle s, float fontSize)
        {
            var c = s.Color;
            return $"<text x=\"{F(x)}\" y=\"{F(y)}\" font-family=\"{Escape(s.FontFamily)}\" font-size=\"{F(fontSize)}\"" +
                   (s.FontWeight >= 600 ? " font-weight=\"bold\"" : "") +
                   $" fill=\"{Color(c)}\" fill-opacity=\"{F4(c.Alpha / 255f)}\"" +
                   (Math.Abs(s.LetterSpacing) > 0.01f ? $" letter-spacing=\"{F4(s.LetterSpacing)}\"" : "") +
                   $" xml:space=\"preserve\">{Escape(text)}</text>";
        }

        void EmitDecor(string text, float x, float y, ComputedStyle s, float fontSize, float width)
        {
            if (s.TextDecoration == "none" || width <= 0) return;
            float th = Math.Max(0.8f, fontSize / 12f);
            var c = s.Color;
            string stroke = $"stroke=\"{Color(c)}\" stroke-width=\"{F(th)}\"";
            if (s.TextDecoration.Contains("underline"))
                _body.Append($"<line x1=\"{F(x)}\" y1=\"{F(y + fontSize * 0.14f + th / 2)}\" x2=\"{F(x + width)}\" y2=\"{F(y + fontSize * 0.14f + th / 2)}\" {stroke}/>");
            if (s.TextDecoration.Contains("line-through"))
                _body.Append($"<line x1=\"{F(x)}\" y1=\"{F(y - fontSize * 0.32f + th / 2)}\" x2=\"{F(x + width)}\" y2=\"{F(y - fontSize * 0.32f + th / 2)}\" {stroke}/>");
            if (s.TextDecoration.Contains("overline"))
                _body.Append($"<line x1=\"{F(x)}\" y1=\"{F(y - fontSize * 0.86f + th / 2)}\" x2=\"{F(x + width)}\" y2=\"{F(y - fontSize * 0.86f + th / 2)}\" {stroke}/>");
        }
    }
}
