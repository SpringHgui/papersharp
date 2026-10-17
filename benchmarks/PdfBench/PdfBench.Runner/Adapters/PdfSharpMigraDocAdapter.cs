using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace PdfBench.Runner.Adapters;

/// <summary>
/// PDFsharp 6.2.4 还原适配器：三页 A4 定版文档，采用纯 XGraphics 手工排版
/// （坐标系：毫米，y 向下）。challenge.html 的 px 按 96dpi 换算为 mm。
/// 不支持的特性（模糊阴影/径向渐变/opacity）用等效手段近似或灰色占位块。
/// </summary>
public sealed class PdfSharpMigraDocAdapter : IPdfAdapter
{
    static PdfSharpMigraDocAdapter()
    {
        // PDFsharp 6 core 版不解析系统字体；用自定义解析器加载 Microsoft YaHei(TTC 抽取面)/Consolas。
        GlobalFontSettings.FontResolver ??= new WinFontResolver();
    }

    public string Name => "pdfsharp";

    // ---------- 页面几何 ----------
    const double PW = 210, PH = 297, MX = 14;
    const double CW = PW - 2 * MX; // 内容宽 182mm
    static readonly double U = 25.4 / 96.0;          // 1 CSS px = mm
    static double P(double px) => px * U;            // px -> mm
    static double Pt(double px) => px * 0.75;        // CSS px -> pt（字号）
    const double Pt2Mm = 25.4 / 72.0;

    // ---------- 调色板 ----------
    static XColor C(string hex) => XColor.FromArgb(
        int.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber),
        int.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber),
        int.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber));
    static XColor A(XColor c, double alpha) => XColor.FromArgb((int)Math.Round(alpha * 255), c);

    static readonly XColor
        Ink = C("222222"), Slate = C("34495e"), Tag = C("7f8c8d"), H2Ink = C("2c3e50"),
        Accent = C("3498db"), LblC = C("95a5a6"), Tint = C("ecf0f1"), FtrLn = C("bdc3c7"),
        Red = C("e74c3c"), Blue = C("2980b9"), Green = C("27ae60"), Orange = C("f39c12"),
        Purple = C("8e44ad"), DOrange = C("d35400"), Teal = C("16a085"), Yellow = C("f1c40f"),
        DarkRed = C("c0392b"), Cream = C("fdf6ec"), GrayBg = C("dddddd");

    // ---------- 字体缓存 ----------
    // 注意：XGraphics 页面单位为毫米时，GDI+ 语义下字号按页面单位解释（15pt 会画成 15mm），
    // 因此创建 XFont 时先把磅值乘 25.4/72 换算成毫米。
    readonly Dictionary<(string, double, int), XFont?> _fonts = new();

    static XFont MakeFont(string fam, double size, int styleBits)
    {
        var style = (styleBits & 1) != 0
            ? ((styleBits & 2) != 0 ? XFontStyleEx.BoldItalic : XFontStyleEx.Bold)
            : ((styleBits & 2) != 0 ? XFontStyleEx.Italic : XFontStyleEx.Regular);
        return new XFont(fam, size * Pt2Mm, style);
    }

    XFont F(R r, double scale = 1.0)
    {
        string fam = r.Mono ? "Consolas" : (r.Light ? "Microsoft YaHei Light" : "Microsoft YaHei");
        var key = (fam, r.Size * scale, (r.Bold ? 1 : 0) | (r.Italic ? 2 : 0));
        if (!_fonts.TryGetValue(key, out var f))
        {
            try { f = MakeFont(fam, key.Item2, key.Item3); }
            catch
            {
                try { f = MakeFont("Microsoft YaHei", key.Item2, key.Item3); }
                catch { f = null; }
            }
            _fonts[key] = f;
        }
        return f ?? MakeFont("Arial", key.Item2, key.Item3);
    }

    // ---------- 文本运行样式 ----------
    sealed class R
    {
        public string Text = "";
        public double Size = Pt(10.5);
        public XColor Color = Ink;
        public bool Bold, Italic, Underline, Strike, SmallCaps, Mono, Light;
        public double Spacing;            // 额外字距 mm
        public XColor? Back, Border;      // 芯片/高亮背景、边框
        public double BackPad;            // 背景左右内边距 mm
        public R() { }
        public R(string t) { Text = t; }
        public R(string t, double px, XColor c) { Text = t; Size = Pt(px); Color = c; }
        public R B() { Bold = true; return this; }
        public R I() { Italic = true; return this; }
        public R U() { Underline = true; return this; }
        public R S() { Strike = true; return this; }
        public R SC() { SmallCaps = true; return this; }
        public R LT() { Light = true; return this; }
        public R Sz(double px) { Size = Pt(px); return this; }
        public R Cl(XColor c) { Color = c; return this; }
        public R Sp(double px) { Spacing = P(px); return this; }
        public R Chip(XColor bg, XColor fg, XColor? bd, double px = 9.5)
        { Back = bg; Color = fg; Border = bd; Mono = true; BackPad = P(4); Size = Pt(px); return this; }
        public R Hi(XColor bg) { Back = bg; BackPad = 0; return this; }
        public R Clone(string t) => new()
        {
            Text = t, Size = Size, Color = Color, Bold = Bold, Italic = Italic,
            Underline = Underline, Strike = Strike, SmallCaps = SmallCaps, Mono = Mono,
            Light = Light, Spacing = Spacing, Back = Back, Border = Border, BackPad = BackPad
        };
    }

    enum TA { Left, Center, Right, Justify }

    readonly struct Tk
    {
        public readonly string T; public readonly R R; public readonly bool Space; public readonly double Wd;
        public Tk(string t, R r, double w) { T = t; R = r; Space = t.Length > 0 && t == " "; Wd = w; }
    }

    static bool IsCjk(char ch) =>
        ch >= 0x2E80 || (ch >= 0xFF00 && ch <= 0xFFEF) || "，。：；、（）【】《》—…·×→".Contains(ch);

    double TokW(XGraphics g, string t, R r)
    {
        if (r.SmallCaps) return ScWidth(g, t, r);
        double w = g.MeasureString(t, F(r)).Width + r.Spacing * Math.Max(0, t.Length - 1);
        if (r.Back != null) w += 2 * r.BackPad;
        return w;
    }

    List<Tk> Tokenize(XGraphics g, List<R> runs, bool breakAll)
    {
        var toks = new List<Tk>();
        foreach (var r in runs)
        {
            if (r.Back != null || r.SmallCaps || r.Spacing > 0)
            {
                toks.Add(new Tk(r.Text, r, TokW(g, r.Text, r)));
                continue;
            }
            int n = r.Text.Length, i = 0;
            while (i < n)
            {
                char ch = r.Text[i];
                if (ch == ' ') { toks.Add(new Tk(" ", r, g.MeasureString(" ", F(r)).Width)); i++; continue; }
                if (IsCjk(ch) || breakAll) { toks.Add(new Tk(ch.ToString(), r, TokW(g, ch.ToString(), r))); i++; continue; }
                int j = i;
                while (j < n && r.Text[j] != ' ' && !IsCjk(r.Text[j])) j++;
                var sub = r.Text.Substring(i, j - i);
                toks.Add(new Tk(sub, r, TokW(g, sub, r)));
                i = j;
            }
        }
        return toks;
    }

    double AsOf(R r) { var m = F(r).Metrics; return r.Size * Pt2Mm * m.Ascent / m.UnitsPerEm; }
    double DsOf(R r) { var m = F(r).Metrics; return r.Size * Pt2Mm * Math.Abs(m.Descent) / m.UnitsPerEm; }

    // ---------- 小字体（Small Caps 近似） ----------
    double ScWidth(XGraphics g, string t, R r)
    {
        double w = 0; bool wordStart = true;
        foreach (var ch in t)
        {
            bool small = !wordStart && char.IsLower(ch);
            w += g.MeasureString(char.ToUpper(ch).ToString(), F(r, small ? 0.78 : 1.0)).Width;
            if (ch == ' ') wordStart = true; else if (char.IsLetter(ch)) wordStart = false;
        }
        return w;
    }

    double DrawSc(XGraphics g, string t, R r, double x, double baseline)
    {
        double x0 = x; bool wordStart = true;
        foreach (var ch in t)
        {
            bool small = !wordStart && char.IsLower(ch);
            var rr = r.Clone(char.ToUpper(ch).ToString());
            var f = F(rr, small ? 0.78 : 1.0);
            g.DrawString(rr.Text, f, Br(rr.Color), x, baseline - AsOf(rr) * (small ? 0.78 : 1.0), XStringFormats.TopLeft);
            x += g.MeasureString(rr.Text, f).Width;
            if (ch == ' ') wordStart = true; else if (char.IsLetter(ch)) wordStart = false;
        }
        return x - x0;
    }

    // ---------- 富文本段落引擎（基线对齐 + 自动换行 + 两端对齐） ----------
    double Para(XGraphics g, double x, double y, double width, List<R> runs, double lineH,
        TA align = TA.Left, double indent = 0, XColor? lineBg = null, bool breakAll = false)
    {
        var toks = Tokenize(g, runs, breakAll);
        var lines = new List<List<Tk>>();
        var cur = new List<Tk>(); double curW = 0, limit = width - indent;
        foreach (var t in toks)
        {
            if (t.Space && cur.Count == 0) continue;
            if (curW + t.Wd > limit + 0.02 && cur.Count > 0)
            {
                while (cur.Count > 0 && cur[^1].Space) cur.RemoveAt(cur.Count - 1);
                lines.Add(cur); cur = new List<Tk>(); curW = 0; limit = width;
                if (t.Space) continue;
                cur.Add(t); curW = t.Wd;
            }
            else { cur.Add(t); curW += t.Wd; }
        }
        if (cur.Count > 0)
        {
            while (cur[^1].Space) cur.RemoveAt(cur.Count - 1);
            if (cur.Count > 0) lines.Add(cur);
        }
        if (lines.Count == 0) return y;

        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            double lineW = line.Sum(t => t.Wd);
            double extra = 0;
            double sx = x + (li == 0 ? indent : 0);
            if (align == TA.Center) sx += (width - lineW) / 2;
            else if (align == TA.Right) sx += width - lineW;
            else if (align == TA.Justify && li < lines.Count - 1 && line.Count > 1) extra = (width - lineW) / (line.Count - 1);
            double asc = line.Count > 0 ? line.Max(t => AsOf(t.R)) : AsOf(runs[0]);
            double baseline = y + asc;
            if (lineBg is not null) g.DrawRectangle(Br(lineBg.Value), sx - 0, y, width - (sx - x), lineH);
            double cx = sx;
            foreach (var t in line)
            {
                if (t.Space) { cx += t.Wd + extra; continue; }
                DrawTokenBg(g, t, cx, baseline);
                DrawTokenText(g, t, cx, baseline);
                cx += t.Wd + extra;
            }
            y += lineH;
        }
        return y;
    }

    void DrawTokenBg(XGraphics g, Tk t, double x, double baseline)
    {
        if (t.R.Back is null) return;
        double pad = t.R.BackPad;
        var rc = new XRect(x, baseline - AsOf(t.R) - P(1.2), t.Wd, AsOf(t.R) + DsOf(t.R) + P(2.4));
        if (t.R.Border is not null)
        {
            var path = RoundPath(rc.X, rc.Y, rc.Width, rc.Height, P(3));
            g.DrawPath(new XPen(t.R.Border.Value, P(1)), Br(t.R.Back.Value), path);
        }
        else g.DrawRectangle(Br(t.R.Back.Value), rc);
    }

    void DrawTokenText(XGraphics g, Tk t, double x, double baseline)
    {
        double tx = x + t.R.BackPad;
        if (t.R.SmallCaps) { DrawSc(g, t.T, t.R, tx, baseline); }
        else if (t.R.Spacing > 0)
        {
            double cx = tx;
            foreach (var ch in t.T)
            {
                var s = ch.ToString();
                g.DrawString(s, F(t.R), Br(t.R.Color), cx, baseline - AsOf(t.R), XStringFormats.TopLeft);
                cx += g.MeasureString(s, F(t.R)).Width + t.R.Spacing;
            }
        }
        else g.DrawString(t.T, F(t.R), Br(t.R.Color), tx, baseline - AsOf(t.R), XStringFormats.TopLeft);

        if (t.R.Underline)
        {
            double w = t.Wd - 2 * t.R.BackPad, th = Math.Max(P(0.7), t.R.Size * Pt2Mm * 0.07);
            g.DrawLine(new XPen(t.R.Color, th), tx, baseline + t.R.Size * Pt2Mm * 0.16, tx + w, baseline + t.R.Size * Pt2Mm * 0.16);
        }
        if (t.R.Strike)
        {
            double w = t.Wd - 2 * t.R.BackPad, th = Math.Max(P(0.7), t.R.Size * Pt2Mm * 0.07);
            g.DrawLine(new XPen(t.R.Color, th), tx, baseline - t.R.Size * Pt2Mm * 0.30, tx + w, baseline - t.R.Size * Pt2Mm * 0.30);
        }
    }

    readonly Dictionary<XColor, XSolidBrush> _brushes = new();
    XSolidBrush Br(XColor c) { if (!_brushes.TryGetValue(c, out var b)) { b = new XSolidBrush(c); _brushes[c] = b; } return b; }

    // ---------- 图形助手 ----------
    static XGraphics NewPage(PdfDocument doc)
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromMillimeter(PW);
        page.Height = XUnit.FromMillimeter(PH);
        return XGraphics.FromPdfPage(page, XGraphicsUnit.Millimeter);
    }

    static XGraphicsPath RoundPath(double x, double y, double w, double h, double r)
        => CornerPath(x, y, w, h, r, r, r, r);

    static XGraphicsPath CornerPath(double x, double y, double w, double h, double rTL, double rTR, double rBR, double rBL)
    {
        double cap = Math.Min(w, h) / 2;
        rTL = Math.Min(rTL, cap); rTR = Math.Min(rTR, cap); rBR = Math.Min(rBR, cap); rBL = Math.Min(rBL, cap);
        var p = new XGraphicsPath { FillMode = XFillMode.Alternate };
        p.AddArc(x, y, 2 * rTL, 2 * rTL, 180, 90);
        p.AddLine(x + rTL, y, x + w - rTR, y);
        p.AddArc(x + w - 2 * rTR, y, 2 * rTR, 2 * rTR, 270, 90);
        p.AddLine(x + w, y + rTR, x + w, y + h - rBR);
        p.AddArc(x + w - 2 * rBR, y + h - 2 * rBR, 2 * rBR, 2 * rBR, 0, 90);
        p.AddLine(x + w - rBR, y + h, x + rBL, y + h);
        p.AddArc(x, y + h - 2 * rBL, 2 * rBL, 2 * rBL, 90, 90);
        p.AddLine(x, y + h - rBL, x, y + rTL);
        p.CloseFigure();
        return p;
    }

    enum BStyle { Solid, Dashed, Dotted, Double_, Groove, Ridge }

    static XPen Pen(XColor c, double wMm, BStyle style = BStyle.Solid)
    {
        var pen = new XPen(c, wMm) { LineJoin = XLineJoin.Miter };
        if (style == BStyle.Dashed) { pen.DashStyle = XDashStyle.Custom; pen.DashPattern = new double[] { 3, 3 }; }
        if (style == BStyle.Dotted) { pen.DashStyle = XDashStyle.Custom; pen.DashPattern = new double[] { 1, 1 }; }
        return pen;
    }

    /// <summary>画一个 CSS 风格边框盒（边框在 padding 盒外侧，线宽 wMm）。</summary>
    void BorderBox(XGraphics g, double x, double y, double w, double h, double b, XColor c, BStyle style)
    {
        switch (style)
        {
            case BStyle.Double_:
                {
                    double t = b / 3;
                    g.DrawRectangle(Pen(c, t), x + t / 2, y + t / 2, w - t, h - t);
                    g.DrawRectangle(Pen(c, t), x + b - t / 2, y + b - t / 2, w - 2 * b + t, h - 2 * b + t);
                    break;
                }
            case BStyle.Groove:
            case BStyle.Ridge:
                {
                    XColor dark = Scale(c, 0.5), light = style == BStyle.Ridge ? Scale(c, 1.25) : c;
                    double t = b / 2;
                    XColor outer = style == BStyle.Groove ? dark : light;
                    XColor inner = style == BStyle.Groove ? light : dark;
                    g.DrawRectangle(Pen(outer, t), x + t / 2, y + t / 2, w - t, h - t);
                    g.DrawRectangle(Pen(inner, t), x + b - t / 2, y + b - t / 2, w - 2 * b + t, h - 2 * b + t);
                    break;
                }
            default:
                g.DrawRectangle(Pen(c, b, style), x + b / 2, y + b / 2, w - b, h - b);
                break;
        }
    }

    static XColor Scale(XColor c, double k) =>
        XColor.FromArgb((int)Math.Min(255, c.R * k), (int)Math.Min(255, c.G * k), (int)Math.Min(255, c.B * k));

    /// <summary>逐边异构边框：四边各自线型/宽度/颜色。</summary>
    void SidesBox(XGraphics g, double x, double y, double w, double h,
        (double b, XColor c, BStyle s)? top, (double b, XColor c, BStyle s)? right,
        (double b, XColor c, BStyle s)? bottom, (double b, XColor c, BStyle s)? left)
    {
        void Side((double b, XColor c, BStyle s)? sd, bool horiz, double off, double len, bool first)
        {
            if (sd is null) return;
            var (b, c, s) = sd.Value;
            double a = horiz ? (off == 0 ? y + b / 2 : y + h - b / 2) : (off == 0 ? x + b / 2 : x + w - b / 2);
            if (s == BStyle.Double_)
            {
                double t = b / 3;
                for (int i = 0; i < 2; i++)
                {
                    double o = horiz ? (first ? y + t / 2 + i * (b - t) : y + h - b + t / 2 + i * (b - t))
                                     : (first ? x + t / 2 + i * (b - t) : x + w - b + t / 2 + i * (b - t));
                    if (horiz) g.DrawLine(Pen(c, t), x, o, x + w, o);
                    else g.DrawLine(Pen(c, t), o, y, o, y + h);
                }
                return;
            }
            if (horiz) g.DrawLine(Pen(c, b, s), x, a, x + w, a);
            else g.DrawLine(Pen(c, b, s), a, y, a, y + h);
        }
        Side(top, true, 0, w, true);
        Side(bottom, true, 1, w, false);
        Side(left, false, 0, h, true);
        Side(right, false, 1, h, false);
    }

    /// <summary>CSS 角度线性渐变（支持多停靠点），裁剪到 rc。</summary>
    void LinGrad(XGraphics g, XRect rc, double angleCss, (double pos, XColor col)[] stops)
    {
        double th = (angleCss - 90) * Math.PI / 180;
        var st = g.Save();
        g.IntersectClip(rc);
        g.TranslateTransform(rc.X + rc.Width / 2, rc.Y + rc.Height / 2);
        g.RotateTransform(th * 180 / Math.PI);
        double half = Math.Abs(rc.Width / 2 * Math.Cos(th)) + Math.Abs(rc.Height / 2 * Math.Sin(th));
        for (int i = 0; i < stops.Length - 1; i++)
        {
            double t0 = -half + 2 * half * stops[i].pos, t1 = -half + 2 * half * stops[i + 1].pos;
            if (t1 <= t0) continue;
            var brush = new XLinearGradientBrush(new XPoint(t0, 0), new XPoint(t1, 0), stops[i].col, stops[i + 1].col);
            // 垂直渐变轴方向用足够大的范围覆盖整个（旋转后的）矩形；相邻段微重叠消除接缝白线
            double ext = rc.Width + rc.Height;
            g.DrawRectangle(brush, t0 - 0.02, -ext, t1 - t0 + 0.04, 2 * ext);
        }
        g.Restore(st);
    }

    /// <summary>径向渐变近似：同心圆环（PDFsharp 无径向画刷）。</summary>
    void RadGrad(XGraphics g, XRect rc, double cx, double cy, double rMax, double r0, double r1, XColor inner, XColor outer)
    {
        var st = g.Save();
        g.IntersectClip(rc);
        const int n = 60;
        for (int i = n; i >= 1; i--)
        {
            double r = rMax * i / n;
            double f = Math.Clamp((r - r0) / (r1 - r0), 0, 1);
            var col = XColor.FromArgb(
                (int)Math.Round(inner.R + (outer.R - inner.R) * f),
                (int)Math.Round(inner.G + (outer.G - inner.G) * f),
                (int)Math.Round(inner.B + (outer.B - inner.B) * f));
            g.DrawEllipse(Br(col), cx - r, cy - r, 2 * r, 2 * r);
        }
        g.Restore(st);
    }

    /// <summary>模糊阴影近似：多层“与底色预混合”的偏移矩形（无需画布 alpha）。</summary>
    void SoftShadow(XGraphics g, XRect rc, double dx, double dy, double blur, XColor col, double alpha, XColor baseBg)
    {
        const int n = 8;
        for (int i = n; i >= 1; i--)
        {
            double t = (double)i / n;
            double a = alpha / n * 1.15;
            var blend = Mix(baseBg, col, a);
            g.DrawRectangle(Br(blend), rc.X + dx * t + blur * (1 - t) * 0.0, rc.Y + dy * t, rc.Width, rc.Height);
        }
    }

    static XColor Mix(XColor over, XColor under, double a) => XColor.FromArgb(
        (int)Math.Round(over.R * a + under.R * (1 - a)),
        (int)Math.Round(over.G * a + under.G * (1 - a)),
        (int)Math.Round(over.B * a + under.B * (1 - a)));

    /// <summary>做不到的特性占位块：浅灰块 + 红字 ✗。</summary>
    void Placeholder(XGraphics g, double x, double y, double w, double h, string label)
    {
        g.DrawRectangle(Br(GrayBg), x, y, w, h);
        var r = new R("✗ " + label, 9, Red);
        g.DrawString(r.Text, F(r), Br(r.Color), x + 2, y + h / 2 - AsOf(r) / 1.6, XStringFormats.TopLeft);
    }

    /// <summary>
    /// Windows 系统字体解析器：PDFsharp 6.2 core 版无公开 TTC 支持，
    /// 这里从 msyh*.ttc 中抽取单个面（重写 sfnt 表目录）后交给解析器。
    /// </summary>
    sealed class WinFontResolver : IFontResolver
    {
        readonly Dictionary<string, byte[]> _faces = new();
        const string Dir = @"C:\Windows\Fonts\";

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var fam = familyName.ToLowerInvariant();
            string file, key; bool simItalic = false;
            if (fam.Contains("consolas"))
            {
                file = (isBold, isItalic) switch
                {
                    (true, true) => "consolaz.ttf", (true, false) => "consolab.ttf",
                    (false, true) => "consolai.ttf", _ => "consola.ttf",
                };
                key = "Consolas" + (isBold ? "B" : "") + (isItalic ? "I" : "");
            }
            else
            {
                bool light = fam.Contains("light");
                file = light ? "msyhl.ttc" : isBold ? "msyhbd.ttc" : "msyh.ttc";
                key = "YaHei" + (light ? "L" : "") + (isBold ? "B" : "") + (isItalic ? "I" : "");
                simItalic = isItalic; // 雅黑无斜体面，用风格模拟
            }
            if (!_faces.ContainsKey(key))
            {
                var data = File.ReadAllBytes(Dir + file);
                if (file.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)) data = ExtractTtcFace(data, 0);
                _faces[key] = data;
            }
            return new FontResolverInfo(key, false, simItalic);
        }

        public byte[]? GetFont(string faceName) => _faces.TryGetValue(faceName, out var b) ? b : null;

        static int Be16(byte[] d, int i) => (d[i] << 8) | d[i + 1];
        static int Be32(byte[] d, int i) => (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
        static void Wb32(byte[] d, int i, int v) { d[i] = (byte)(v >> 24); d[i + 1] = (byte)(v >> 16); d[i + 2] = (byte)(v >> 8); d[i + 3] = (byte)v; }

        public static byte[] ExtractTtcFace(byte[] ttc, int index)
        {
            if (ttc.Length < 16 || Be32(ttc, 0) != 0x74746366) return ttc; // 'ttcf'
            int nFonts = Be32(ttc, 8);
            if (index >= nFonts) index = 0;
            int fo = Be32(ttc, 12 + index * 4);
            int numTables = Be16(ttc, fo + 4);
            int header = 12 + 16 * numTables, total = header;
            var src = new int[numTables]; var len = new int[numTables];
            for (int i = 0; i < numTables; i++)
            {
                int e = fo + 12 + 16 * i;
                src[i] = Be32(ttc, e + 8); len[i] = Be32(ttc, e + 12);
                total += (len[i] + 3) & ~3;
            }
            var outB = new byte[total];
            Array.Copy(ttc, fo, outB, 0, header); // sfnt 头 + 表目录（随后只改偏移）
            int dst = header;
            for (int i = 0; i < numTables; i++)
            {
                Array.Copy(ttc, src[i], outB, dst, len[i]);
                Wb32(outB, 12 + 16 * i + 8, dst);
                dst += (len[i] + 3) & ~3;
            }
            return outB;
        }
    }

    // ---------- 页眉页脚 ----------
    void Header(XGraphics g, string tag)
    {
        var brand = new R("PDFBENCH · VISUAL CHALLENGE", 13, Slate).B().Sp(1);
        DrawSpaced(g, brand.Text, brand, MX, 12.1);
        var t = new R(tag, 10, Tag);
        double w = g.MeasureString(tag, F(t)).Width;
        g.DrawString(tag, F(t), Br(t.Color), PW - MX - w, 12.6, XStringFormats.TopLeft);
        g.DrawLine(Pen(Slate, P(2)), MX, 17.3, PW - MX, 17.3);
    }

    void DrawSpaced(XGraphics g, string text, R r, double x, double yTop)
    {
        double cx = x;
        foreach (var ch in text)
        {
            var s = ch.ToString();
            g.DrawString(s, F(r), Br(r.Color), cx, yTop, XStringFormats.TopLeft);
            cx += g.MeasureString(s, F(r)).Width + r.Spacing;
        }
    }

    void Footer(XGraphics g, string pageNo)
    {
        g.DrawLine(Pen(FtrLn, P(1)), MX, 285.9, PW - MX, 285.9);
        var t = new R("PdfBench · challenge.html · generated by spec v0.1", 9, Tag);
        g.DrawString(t.Text, F(t), Br(t.Color), MX, 287.2, XStringFormats.TopLeft);
        double pw = P(34), ph = P(16);
        double px = PW - MX - pw, py = 287.0;
        var pill = CornerPath(px, py, pw, ph, P(8), P(8), P(8), P(8));
        g.DrawPath(Pen(Slate, P(1)), pill);
        var n = new R(pageNo, 9, Slate).B();
        double nw = g.MeasureString(pageNo, F(n)).Width;
        g.DrawString(pageNo, F(n), Br(n.Color), px + (pw - nw) / 2, py + (ph - AsOf(n) - DsOf(n)) / 2, XStringFormats.TopLeft);
    }

    // ---------- 标题助手 ----------
    double H1(XGraphics g, double y, string text)
    {
        var r = new R(text, 20, Slate).B();
        g.DrawString(text, F(r), Br(r.Color), MX, y, XStringFormats.TopLeft);
        return y + P(20 * 1.35) + P(3 * 3.7795); // 行高 + 3mm 下边距
    }

    double H2(XGraphics g, double y, string text)
    {
        y += P(3.5 * 3.7795); // 3.5mm 上边距
        double lineH = P(14 * 1.35);
        g.DrawRectangle(Br(Accent), MX, y, P(4), lineH);
        var r = new R(text, 14, H2Ink).B();
        g.DrawString(text, F(r), Br(r.Color), MX + P(4) + P(6), y, XStringFormats.TopLeft);
        return y + lineH + P(2 * 3.7795);
    }

    static List<R> Runs(params R[] rs) => rs.ToList();
    static R T(string s) => new(s);

    // =====================================================================
    public void Generate(AdapterContext ctx, string outPdfPath)
    {
        var doc = new PdfDocument();
        doc.Info.Title = "PdfBench Visual Challenge — PDFsharp";
        doc.Info.Creator = "PdfBench/PDFsharp 6.2.4";
        Page1(doc);
        Page2(doc, ctx);
        Page3(doc, ctx);
        doc.Save(outPdfPath);
    }

    // ---------------------------- 第 1 页 ----------------------------
    void Page1(PdfDocument doc)
    {
        var g = NewPage(doc);
        Header(g, "Page 1 — Typography & Text");
        double y = 21.4;

        y = H1(g, y, "排版与文字 Rendering Test");

        y = H2(g, y, "1.1 层级与字重");
        // H3 subtitle + 右侧说明
        var h3 = new R("H3 subtitle", 11, Tag).B();
        g.DrawString(h3.Text, F(h3), Br(h3.Color), MX, y, XStringFormats.TopLeft);
        var note = new R("h3 → h1 右对齐抬升", 9, LblC);
        g.DrawString(note.Text, F(note), Br(note.Color), PW - MX - g.MeasureString(note.Text, F(note)).Width, y + P(1.5), XStringFormats.TopLeft);
        y += P(11 * 1.35) + P(2);

        y = Para(g, MX, y, CW, Runs(
            T("字重："), new R("粗体 bold", 10.5, Ink).B(), T("、"),
            new R("斜体 italic", 10.5, Ink).I(), T("、"),
            new R("粗斜体", 10.5, Ink).B().I(), T("、"),
            new R("细体 light 300", 10.5, Ink).LT(), T("、"),
            new R("下划线", 10.5, Ink).U(), T("、"),
            new R("删除线", 10.5, Ink).S(), T("、"),
            new R("Small Caps", 10.5, Ink).SC(), T(" 效果。")), P(10.5 * 1.35));
        y += P(2.5 * 3.7795);

        y = Para(g, MX, y, CW, Runs(
            T("行内代码芯片："),
            new R("PdfGenerator.Render()").Chip(C("f3e5f5"), C("8e24aa"), C("ce93d8")),
            T(" 与 "),
            new R("spec.json").Chip(C("e3f2fd"), C("1565c0"), null),
            T(" 混排于行文中。")), P(10.5 * 1.35));
        y += P(2.5 * 3.7795);

        y = H2(g, y, "1.2 中西混排与行高");
        y = Para(g, MX, y, CW, Runs(
            T("中文段落混合 English words 与数字 0123456789，标点，以及括号（半角）与全角符号。 中英文基线对齐是渲染引擎的常见短板，The quick brown fox 应跳 over the lazy dog 而不断行错位。")),
            P(10.5 * 1.8));
        y += P(2.5 * 3.7795);

        // 行高演示
        (double, string, string)[] lh = { (1.0, "line-height: 1.0", "密集行距示例 The quick brown fox jumps over the lazy dog 中文密集行示例，行与行可能粘连。"),
                                          (1.4, "line-height: 1.4", "正常行距示例 The quick brown fox jumps over the lazy dog 中文正常行距示例，阅读舒适。"),
                                          (2.2, "line-height: 2.2", "宽松行距示例 The quick brown fox 中文宽松行距示例。") };
        foreach (var (mult, lbl, txt) in lh)
        {
            var l = new R(lbl, 8, LblC);
            g.DrawString(lbl, F(l), Br(l.Color), MX, y, XStringFormats.TopLeft);
            y += P(8 * 1.35) + P(1);
            double lineH = P(10.5 * mult);
            y = Para(g, MX, y, CW, Runs(T(txt)), lineH, lineBg: Cream);
            y += P(1 * 3.7795);
        }
        y += P(1.5 * 3.7795);

        // 字距
        (double, string)[] tr = { (0, "normal: tracking 字距"), (2, "wide 2px: tracking 字距"), (5, "wider 5px: tracking 字距") };
        foreach (var (sp, txt) in tr)
        {
            var r = new R(txt, 12, Ink).Sp(sp);
            DrawSpaced(g, txt, r, MX, y);
            y += P(12 * 1.35);
        }
        y = Para(g, MX, y, CW, Runs(T("首行缩进两个字符：这是段落首行缩进的验证文本，缩进应恰好为 2 个全角字符宽度且仅作用于首行。")),
            P(10.5 * 1.35), indent: P(21));

        y = H2(g, y, "1.3 文字效果与对齐");
        // text-shadow：硬阴影偏移 2px（无模糊，可原生近似）
        var sh = new R("阴影文字 Text Shadow", 16, H2Ink).B();
        g.DrawString(sh.Text, F(sh), Br(C("f5b7b1")), MX + P(2), y + P(2), XStringFormats.TopLeft);
        g.DrawString(sh.Text, F(sh), Br(sh.Color), MX, y, XStringFormats.TopLeft);
        y += P(16 * 1.35) + P(2.5 * 3.7795);

        y = Para(g, MX, y, CW, Runs(
            new R("红", 10.5, Red), T(" "), new R("绿", 10.5, Green), T(" "), new R("蓝", 10.5, Blue),
            T(" "), new R("橙", 10.5, Orange), T(" "), new R("紫", 10.5, Purple), T(" —— 五色文字，纯色无渐变。")),
            P(10.5 * 1.35));
        y += P(2.5 * 3.7795);

        // 四格对齐
        double gap = P(4 * 3.7795), unit = (CW - 3 * gap) / 4.6;
        double[] ws = { unit, unit, unit, unit * 1.6 };
        double[] xs = new double[4];
        xs[0] = MX; for (int i = 1; i < 4; i++) xs[i] = xs[i - 1] + ws[i - 1] + gap;
        double cellH = 3 * P(9 * 1.35) + 2 * P(2.5 * 3.7795);
        string[] texts = { "左对齐 left 文", "居中 center 文", "右对齐 right 文",
            "两端对齐 justify：This paragraph is justified so that both left and right edges form clean lines across its full width 无论中英文。" };
        TA[] aligns = { TA.Left, TA.Center, TA.Right, TA.Justify };
        for (int i = 0; i < 4; i++)
        {
            g.DrawRectangle(Br(Tint), xs[i], y, ws[i], cellH);
            Para(g, xs[i] + P(2.5 * 3.7795), y + P(2.5 * 3.7795), ws[i] - 2 * P(2.5 * 3.7795),
                Runs(new R(texts[i], 9, Ink)), P(9 * 1.35), aligns[i]);
        }
        y += cellH + P(2.5 * 3.7795);

        y = H2(g, y, "1.4 列表与嵌套");
        double colW = (CW - 2 * gap) / 3;
        double[] cx3 = { MX, MX + colW + gap, MX + 2 * (colW + gap) };
        double ly = y;
        var lbl8 = new R("", 8, LblC);

        // 列 1：disc 嵌套
        g.DrawString("unordered disc，marker 着色", F(lbl8), Br(lbl8.Color), cx3[0], ly, XStringFormats.TopLeft);
        double iy = ly + P(8 * 1.35) + P(2);
        double ilh = P(10 * 1.35);
        void Bullet(double bx, double by, char kind)
        {
            if (kind == 'o') g.DrawEllipse(Pen(Ink, P(0.8)), bx - P(2.6), by - P(2.6), P(5.2), P(5.2));
            else if (kind == 's') g.DrawRectangle(Br(Ink), bx - P(2.2), by - P(2.2), P(4.4), P(4.4));
            else g.DrawEllipse(Br(Ink), bx - P(2.6), by - P(2.6), P(5.2), P(5.2));
        }
        (double lvl, char mk, List<R> runs)[] col1 =
        {
            (0, 'd', Runs(new R("项目一：", 10, Red), T("无序列表 disc 符号"))),
            (0, 'd', Runs(T("项目二：Marker 颜色独立于文字"))),
            (0, 'd', Runs(T("项目三：三级嵌套入口"))),
            (1, 'o', Runs(T("嵌套层 A"))),
            (2, 's', Runs(T("方块 square 层"))),
        };
        foreach (var (lvl, mk, runs) in col1)
        {
            double bx = cx3[0] + P(12) + lvl * P(26), ty = cx3[0] + P(26) + lvl * P(26);
            Bullet(bx, iy + AsOf(runs[0]) + DsOf(runs[0]) / 2, mk);
            Para(g, ty, iy, colW - P(26) - lvl * P(26), runs, ilh);
            iy += ilh;
        }

        // 列 2：有序列表
        g.DrawString("ordered decimal / lower-alpha / upper-roman", F(lbl8), Br(lbl8.Color), cx3[1], ly, XStringFormats.TopLeft);
        iy = ly + P(8 * 1.35) + P(2);
        (string num, double lvl, string txt)[] col2 =
        {
            ("1.", 0, "十进制 ordered"), ("2.", 0, "第二项"),
            ("a.", 1, "lower-alpha 子项"), ("b.", 1, "子项二"),
            ("I.", 0, "upper-roman 独立编号"), ("II.", 0, "应为 II"),
        };
        foreach (var (num, lvl, txt) in col2)
        {
            double nx = cx3[1] + P(12) + lvl * P(26), tx = cx3[1] + P(26) + lvl * P(26);
            var nr = new R(num, 10, Ink);
            g.DrawString(num, F(nr), Br(nr.Color), nx - g.MeasureString(num, F(nr)).Width, iy, XStringFormats.TopLeft);
            Para(g, tx, iy, colW - P(26) - lvl * P(26), Runs(T(txt)), ilh);
            iy += ilh;
        }

        // 列 3：circle / none
        g.DrawString("自定义 marker：circle + 无标记", F(lbl8), Br(lbl8.Color), cx3[2], ly, XStringFormats.TopLeft);
        iy = ly + P(8 * 1.35) + P(2);
        (char mk, string txt)[] col3 = { ('o', "circle 空心圈"), ('n', "none：无标记但有缩进对齐"), ('o', "circle 第二项") };
        foreach (var (mk, txt) in col3)
        {
            if (mk != 'n') Bullet(cx3[2] + P(12), iy + AsOf(new R("x", 10, Ink)) + DsOf(new R("x", 10, Ink)) / 2, mk);
            Para(g, cx3[2] + P(26), iy, colW - P(26), Runs(T(txt)), ilh);
            iy += ilh;
        }

        Footer(g, "1 / 3");
        g.Dispose();
    }

    // ---------------------------- 第 2 页 ----------------------------
    void Page2(PdfDocument doc, AdapterContext ctx)
    {
        var g = NewPage(doc);
        Header(g, "Page 2 — Borders & Corners");
        double y = 21.4;
        double gap = P(4 * 3.7795), pad = P(2.5 * 3.7795);
        y = H1(g, y, "边框家族与圆角");

        y = H2(g, y, "2.1 四种线型 × 三种宽度");
        // 3 行 × 3 格线型盒
        (string txt, double b, XColor c, BStyle s)[][] rows =
        {
            new[] { ("solid 2px", P(2), Red, BStyle.Solid), ("solid 4px", P(4), Red, BStyle.Solid), ("solid 8px", P(8), Red, BStyle.Solid) },
            new[] { ("dashed 2px", P(2), Blue, BStyle.Dashed), ("dashed 4px", P(4), Blue, BStyle.Dashed), ("dashed 4px+", P(8), Blue, BStyle.Dashed) },
            new[] { ("dotted 2px", P(2), Green, BStyle.Dotted), ("dotted 4px", P(4), Green, BStyle.Dotted), ("double 8px", P(8), Purple, BStyle.Double_) },
            new[] { ("double 4px", P(4), Purple, BStyle.Double_), ("groove 4px", P(4), DOrange, BStyle.Groove), ("ridge 4px", P(4), Teal, BStyle.Ridge) },
        };
        var tr9 = new R("", 9, Ink);
        double th = P(9 * 1.35);
        foreach (var row in rows)
        {
            double x = MX;
            double h = th + 2 * pad + 2 * row.Max(r => r.b);
            foreach (var (txt, b, c, s) in row)
            {
                double w = g.MeasureString(txt, F(tr9)).Width + 2 * pad + 2 * b;
                BorderBox(g, x, y, w, h, b, c, s);
                Para(g, x + b + pad, y + b + pad, w - 2 * b - 2 * pad, Runs(new R(txt, 9, Ink)), P(9 * 1.35));
                x += w + gap;
            }
            y += h + P(1.5 * 3.7795);
        }
        y += P(2);

        y = H2(g, y, "2.2 逐边异构");
        double sw = (CW - 2 * gap) / 3;
        double sh = th + 2 * P(5 * 3.7795) + P(8);
        double sx = MX;
        SidesBox(g, sx, y, sw, sh, (P(3), Blue, BStyle.Solid), (P(3), Red, BStyle.Dashed), (P(3), Green, BStyle.Dotted), (P(4), Orange, BStyle.Double_));
        Para(g, sx + P(3) + P(3 * 3.7795), y + P(5 * 3.7795), sw - 2 * P(3 * 3.7795) - P(6), Runs(new R("四边四线型：上实/右虚/下点/左双", 9, Ink)), P(9 * 1.35));
        sx += sw + gap;
        SidesBox(g, sx, y, sw, sh, (P(3), Red, BStyle.Solid), (P(3), Blue, BStyle.Solid), (P(3), Green, BStyle.Solid), (P(3), Orange, BStyle.Solid));
        Para(g, sx + P(3) + P(3 * 3.7795), y + P(5 * 3.7795), sw - 2 * P(3 * 3.7795) - P(6), Runs(new R("四边同线型异色：上红/右蓝/下绿/左橙", 9, Ink)), P(9 * 1.35));
        sx += sw + gap;
        SidesBox(g, sx, y, sw, sh, (P(2), DarkRed, BStyle.Solid), (P(2), DarkRed, BStyle.Solid), (P(2), DarkRed, BStyle.Solid), (P(2), DarkRed, BStyle.Solid));
        Para(g, sx + P(2) + P(3 * 3.7795), y + P(5 * 3.7795), sw - 2 * P(3 * 3.7795) - P(4), Runs(new R("currentColor 边框：边色随字色变红", 9, DarkRed)), P(9 * 1.35));
        y += sh + P(1.5 * 3.7795);

        // 单边
        double bh = th + 2 * P(2 * 3.7795);
        sx = MX;
        g.DrawRectangle(Br(Tint), sx, y, sw, bh);
        g.DrawLine(Pen(Purple, P(3)), sx, y + bh - P(3) / 2, sx + sw, y + bh - P(3) / 2);
        Para(g, sx + P(3), y + P(2 * 3.7795), sw - P(6), Runs(new R("仅底边：文章式分隔线", 9, Ink)), P(9 * 1.35));
        sx += sw + gap;
        g.DrawRectangle(Br(Tint), sx, y, sw, bh);
        g.DrawLine(Pen(Green, P(5)), sx + P(5) / 2, y, sx + P(5) / 2, y + bh);
        Para(g, sx + P(5) + P(3), y + P(2 * 3.7795), sw - P(5) - P(6), Runs(new R("仅左边：引文强调条", 9, Ink)), P(9 * 1.35));
        sx += sw + gap;
        g.DrawLine(Pen(Blue, P(2)), sx, y + P(1), sx + sw, y + P(1));
        g.DrawLine(Pen(Blue, P(2)), sx, y + bh - P(1), sx + sw, y + bh - P(1));
        Para(g, sx + P(3), y + P(2 * 3.7795), sw - P(6), Runs(new R("上下边：band 样式", 9, Ink)), P(9 * 1.35));
        y += bh + P(1);

        y = H2(g, y, "2.3 圆角");
        // 五格圆角
        double bw = P(26 * 3.7795), bh2 = P(18 * 3.7795);
        (string txt, double[] r)[] rad =
        {
            ("r=0", new[] { 0.0, 0, 0, 0 }), ("4px", new[] { 4.0, 4, 4, 4 }.Select(P).ToArray()),
            ("10px", new[] { 10.0, 10, 10, 10 }.Select(P).ToArray()),
            ("8/2 对角", new[] { 8.0, 2, 8, 2 }.Select(P).ToArray()),
            ("四角异值", new[] { 14.0, 2, 14, 2 }.Select(P).ToArray()),
        };
        double rx = MX;
        foreach (var (txt, rs) in rad)
        {
            var path = CornerPath(rx, y, bw, bh2, rs[0], rs[1], rs[2], rs[3]);
            g.DrawPath(Pen(Blue, P(2)), Br(C("d6eaf8")), path);
            Para(g, rx + pad, y + pad, bw - 2 * pad, Runs(new R(txt, 9, Ink)), P(9 * 1.35));
            rx += bw + gap;
        }
        y += bh2 + P(2 * 3.7795);

        // 正圆/椭圆/胶囊/虚线圆角
        double rowY = y, rowH = P(18 * 3.7795);
        double cirD = P(18 * 3.7795);
        g.DrawPath(Pen(DarkRed, P(2)), Br(C("fadbd8")), CornerPath(MX, rowY, cirD, cirD, cirD / 2, cirD / 2, cirD / 2, cirD / 2));
        double lx = MX + cirD + P(2 * 3.7795);
        var l9 = new R("50% 正圆", 8, LblC);
        g.DrawString(l9.Text, F(l9), Br(l9.Color), lx, rowY + (rowH - P(8 * 1.35)) / 2, XStringFormats.TopLeft);
        lx += g.MeasureString(l9.Text, F(l9)).Width + P(2 * 3.7795);
        double elW = P(11 * 3.7795);
        g.DrawEllipse(Pen(Green, P(2)), Br(C("d5f5e3")), lx, rowY, elW, rowH);
        lx += elW + P(2 * 3.7795);
        var l9b = new R("50% 椭圆", 8, LblC);
        g.DrawString(l9b.Text, F(l9b), Br(l9b.Color), lx, rowY + (rowH - P(8 * 1.35)) / 2, XStringFormats.TopLeft);
        lx += g.MeasureString(l9b.Text, F(l9b)).Width + P(2 * 3.7795);
        double pillW = P(34 * 3.7795), pillH = P(10 * 3.7795), pillY = rowY + (rowH - pillH) / 2;
        g.DrawPath(Pen(DOrange, P(3)), Br(C("fdebd0")), CornerPath(lx, pillY, pillW, pillH, pillH / 2, pillH / 2, pillH / 2, pillH / 2));
        var pt = new R("胶囊 pill r=高度/2", 9, Ink);
        g.DrawString(pt.Text, F(pt), Br(pt.Color), lx + (pillW - g.MeasureString(pt.Text, F(pt)).Width) / 2, pillY + (pillH - AsOf(pt) - DsOf(pt)) / 2, XStringFormats.TopLeft);
        lx += pillW + gap;
        var dash = new XPen(Purple, P(3)) { DashStyle = XDashStyle.Custom, DashPattern = new double[] { 3, 2.4 } };
        g.DrawPath(dash, Br(C("d6eaf8")), CornerPath(lx, rowY, bw, rowH, P(10), P(10), P(10), P(10)));
        lx += bw + P(2 * 3.7795);
        var l9c = new R("圆角+虚线复合", 8, LblC);
        g.DrawString(l9c.Text, F(l9c), Br(l9c.Color), lx, rowY + (rowH - P(8 * 1.35)) / 2, XStringFormats.TopLeft);
        y += rowH + P(2 * 3.7795);

        // 复合盒
        double half = (CW - gap) / 2;
        double compH = 2 * P(9 * 1.35) + 2 * P(3 * 3.7795) + 2 * P(6);
        g.DrawPath(Pen(Slate, P(6)), Br(C("f8f9f9")), CornerPath(MX, y, half, compH, P(14), P(14), P(14), P(14)));
        Para(g, MX + P(6) + P(3 * 3.7795), y + P(6) + P(3 * 3.7795), half - 2 * P(6) - 2 * P(3 * 3.7795),
            Runs(new R("厚边框(6px)+大圆角(14px)+内边距+背景色 复合盒：边框与背景的圆角必须同心，内边距把内容从边框推开。", 9, Ink)), P(9 * 1.35));
        // overflow:hidden 裁切过头圆角
        double ox = MX + half + gap;
        double headH = P(9 * 1.35) + 2 * P(2 * 3.7795), bodyH = P(9 * 1.35) + 2 * P(2 * 3.7795);
        var clip = CornerPath(ox, y, half, headH + bodyH, P(10), P(10), P(10), P(10));
        var st = g.Save();
        g.IntersectClip(clip);
        g.DrawRectangle(Br(Green), ox, y, half, headH);
        var ht = new R("裁切过头圆角", 9, XColors.White).B();
        g.DrawString(ht.Text, F(ht), Br(ht.Color), ox + P(3 * 3.7795), y + P(2 * 3.7795), XStringFormats.TopLeft);
        g.DrawRectangle(Br(XColors.White), ox, y + headH, half, bodyH);
        var bt = new R("overflow:hidden 子元素背景填满左上圆角", 9, Ink);
        g.DrawString(bt.Text, F(bt), Br(bt.Color), ox + P(3 * 3.7795), y + headH + P(2 * 3.7795), XStringFormats.TopLeft);
        g.Restore(st);
        g.DrawPath(Pen(Green, P(2)), clip);
        y += Math.Max(compH, headH + bodyH) + P(2);

        y = H2(g, y, "2.4 边框即图形");
        double ty0 = y;
        double t1w = P(28 * 3.7795), t1h = P(18 * 3.7795);
        g.DrawPolygon(Br(Red), new[] { new XPoint(MX, ty0 + t1h), new XPoint(MX + t1w / 2, ty0), new XPoint(MX + t1w, ty0 + t1h) }, XFillMode.Winding);
        double tx2 = MX + t1w + P(3 * 3.7795);
        var tl = new R("border 三角形（等腰）", 8, LblC);
        g.DrawString(tl.Text, F(tl), Br(tl.Color), tx2, ty0 + (t1h - P(8 * 1.35)) / 2, XStringFormats.TopLeft);
        tx2 += g.MeasureString(tl.Text, F(tl)).Width + P(3 * 3.7795);
        double t2w = P(16 * 3.7795), t2h = P(12 * 3.7795);
        double t2y = ty0 + (t1h - t2h) / 2;
        g.DrawPolygon(Br(Orange), new[] { new XPoint(tx2, t2y), new XPoint(tx2, t2y + t2h), new XPoint(tx2 + t2w, t2y + t2h) }, XFillMode.Winding);
        tx2 += t2w + P(3 * 3.7795);
        var tl2 = new R("直角三角形（右向）", 8, LblC);
        g.DrawString(tl2.Text, F(tl2), Br(tl2.Color), tx2, ty0 + (t1h - P(8 * 1.35)) / 2, XStringFormats.TopLeft);
        tx2 += g.MeasureString(tl2.Text, F(tl2)).Width + P(3 * 3.7795);
        g.DrawPolygon(Br(Blue), new[] { new XPoint(tx2, ty0), new XPoint(tx2 + t1w, ty0), new XPoint(tx2 + t1w / 2, ty0 + t1h) }, XFillMode.Winding);
        tx2 += t1w + P(3 * 3.7795);
        var tl3 = new R("倒三角", 8, LblC);
        g.DrawString(tl3.Text, F(tl3), Br(tl3.Color), tx2, ty0 + (t1h - P(8 * 1.35)) / 2, XStringFormats.TopLeft);

        Footer(g, "2 / 3");
        g.Dispose();
    }

    // ---------------------------- 第 3 页 ----------------------------
    void Page3(PdfDocument doc, AdapterContext ctx)
    {
        var g = NewPage(doc);
        Header(g, "Page 3 — Effects, Images, Layout");
        double y = 21.4;
        double gap = P(4 * 3.7795), pad = P(2.5 * 3.7795);
        y = H1(g, y, "效果、图像与布局");

        y = H2(g, y, "3.1 阴影 box-shadow");
        double bw = P(36 * 3.7795), bh = P(16 * 3.7795);
        double bx = MX + P(4 * 3.7795);
        // 1) 外阴影模糊（多层近似）
        SoftShadow(g, new XRect(bx, y, bw, bh), P(2), P(2), P(5), XColors.Black, 0.45, XColors.White);
        g.DrawRectangle(Br(Tint), bx, y, bw, bh);
        Para(g, bx + pad, y + pad, bw - 2 * pad, Runs(new R("外阴影 模糊", 9, Ink)), P(9 * 1.35));
        // 2) 硬阴影
        bx += bw + P(8 * 3.7795);
        g.DrawRectangle(Br(Yellow), bx + P(5), y + P(5), bw, bh);
        g.DrawRectangle(Pen(Yellow, P(1)), Br(XColors.White), bx, y, bw, bh);
        Para(g, bx + pad, y + pad, bw - 2 * pad, Runs(new R("硬阴影 无模糊", 9, Ink)), P(9 * 1.35));
        // 3) 内阴影
        bx += bw + P(8 * 3.7795);
        g.DrawRectangle(Br(Tint), bx, y, bw, bh);
        {
            var st = g.Save();
            g.IntersectClip(new XRect(bx, y, bw, bh));
            const int n = 8;
            for (int i = 0; i < n; i++)
            {
                double a = 0.5 * (1 - (double)i / n) / n * 1.6;
                var col = Mix(XColors.Black, Tint, a);
                double t = P(3) * (n - i) / n;
                g.DrawLine(new XPen(col, P(3) / n + 0.02), bx + t / 2, y, bx + t / 2, y + bh);   // 左带
                g.DrawLine(new XPen(col, P(3) / n + 0.02), bx, y + t / 2, bx + bw, y + t / 2);   // 上带
            }
            g.Restore(st);
        }
        Para(g, bx + pad, y + pad, bw - 2 * pad, Runs(new R("内阴影 inset", 9, Ink)), P(9 * 1.35));
        // 4) 三重阴影
        bx += bw + P(8 * 3.7795);
        SoftShadow(g, new XRect(bx, y, bw, bh), P(3), P(3), P(6), Slate, 0.6, XColors.White);
        g.DrawRectangle(Pen(Blue, P(2)), Br(XColors.White), bx, y, bw, bh);
        {
            var st = g.Save();
            g.IntersectClip(new XRect(bx + P(2), y + P(2), bw - 2 * P(2), bh - 2 * P(2)));
            g.DrawRectangle(Br(C("e8f6f3")), bx + P(2), y + bh - P(2) - P(4), bw - 2 * P(2), P(4));
            g.Restore(st);
        }
        Para(g, bx + pad, y + pad, bw - 2 * pad, Runs(new R("三重阴影叠加", 9, Ink)), P(9 * 1.35));
        y += bh + P(1);

        y = H2(g, y, "3.2 渐变与透明");
        double gw = (CW - 3 * P(3 * 3.7795)) / 4, gh = P(16 * 3.7795);
        double gx = MX;
        LinGrad(g, new XRect(gx, y, gw, gh), 45, new[] { (0.0, Red), (0.5, Yellow), (1.0, Green) });
        gx += gw + P(3 * 3.7795);
        LinGrad(g, new XRect(gx, y, gw, gh), 90, new[] { (0.0, Slate), (1.0, Tint) });
        gx += gw + P(3 * 3.7795);
        // 径向渐变：同心圆近似（PDFsharp 无 radial 画刷）
        double rrMax = Math.Sqrt(Math.Pow(Math.Max(0.30, 0.70) * gw, 2) + Math.Pow(Math.Max(0.40, 0.60) * gh, 2));
        RadGrad(g, new XRect(gx, y, gw, gh), gx + 0.30 * gw, y + 0.40 * gh,
            rrMax, 0.10 * rrMax, 0.90 * rrMax, XColors.White, Blue);
        gx += gw + P(3 * 3.7795);
        // 渐变+alpha：端点与白底预混合
        LinGrad(g, new XRect(gx, y, gw, gh), 180,
            new[] { (0.0, Mix(Red, XColors.White, 0.8)), (1.0, Mix(Blue, XColors.White, 0.2)) });
        var at = new R("渐变+alpha", 8, XColors.White);
        g.DrawString(at.Text, F(at), Br(at.Color), gx + P(1), y + P(1), XStringFormats.TopLeft);
        y += gh + P(1.5 * 3.7795);

        // 透明叠加行
        double trH = P(26 * 3.7795);
        // 三个 22mm 半透明方块：区域颜色 = 精确 alpha 预计算（白底合成）
        double sq = P(22 * 3.7795), sqx = MX, sqy = y;
        XColor wr = Mix(Red, XColors.White, 0.55), wg = Mix(Green, XColors.White, 0.55), wb = Mix(Blue, XColors.White, 0.55);
        XColor wrg = Mix(Green, wr, 0.55), wrb = Mix(Blue, wr, 0.55), wgb = Mix(Blue, wg, 0.55), wrgb = Mix(Blue, wrg, 0.55);
        g.DrawRectangle(Br(wr), sqx, sqy, sq, sq);
        g.DrawRectangle(Br(wg), sqx + P(9 * 3.7795), sqy, sq, sq);
        g.DrawRectangle(Br(wb), sqx + P(4.5 * 3.7795), sqy + P(8 * 3.7795), sq, sq);
        // 交叠区（矩形交）
        void Rect2(XGraphics g, double l, double t, double r, double b, XColor c)
        {
            if (r > l && b > t) g.DrawRectangle(Br(c), l, t, r - l, b - t);
        }
        // 红∩绿
        Rect2(g, Math.Max(sqx, sqx + P(9 * 3.7795)), sqy, Math.Min(sqx + sq, sqx + P(9 * 3.7795) + sq), sqy + sq, wrg);
        // 红∩蓝
        Rect2(g, Math.Max(sqx, sqx + P(4.5 * 3.7795)), Math.Max(sqy, sqy + P(8 * 3.7795)), Math.Min(sqx + sq, sqx + P(4.5 * 3.7795) + sq), Math.Min(sqy + sq, sqy + P(8 * 3.7795) + sq), wrb);
        // 绿∩蓝
        Rect2(g, Math.Max(sqx + P(9 * 3.7795), sqx + P(4.5 * 3.7795)), Math.Max(sqy, sqy + P(8 * 3.7795)), Math.Min(sqx + P(9 * 3.7795) + sq, sqx + P(4.5 * 3.7795) + sq), Math.Min(sqy + sq, sqy + P(8 * 3.7795) + sq), wgb);
        // 红∩绿∩蓝
        Rect2(g, sqx + P(9 * 3.7795), sqy + P(8 * 3.7795), Math.Min(sqx + P(9 * 3.7795) + sq, sqx + sq), Math.Min(sqy + sq, sqy + P(8 * 3.7795) + sq), wrgb);
        var ol = new R("半透明三色叠加（重叠区混色）", 8, LblC);
        g.DrawString(ol.Text, F(ol), Br(ol.Color), MX + P(46 * 3.7795) + P(3 * 3.7795), y + (trH - P(8 * 1.35)) / 2, XStringFormats.TopLeft);
        double opX = MX + P(46 * 3.7795) + P(3 * 3.7795) + g.MeasureString(ol.Text, F(ol)).Width + P(4 * 3.7795);
        double opW = P(30 * 3.7795), opH = P(18 * 3.7795), opY = y + (trH - opH) / 2;
        XColor opBg = Mix(Blue, XColors.White, 0.4);
        g.DrawRectangle(Br(opBg), opX, opY, opW, opH);
        var ot = new R("opacity 0.4 整块淡出", 9, Mix(XColors.White, opBg, 0.4));
        g.DrawString(ot.Text, F(ot), Br(ot.Color), opX + pad, opY + pad, XStringFormats.TopLeft);
        double hlX = opX + opW + P(4 * 3.7795), hlW = P(34 * 3.7795), hlH = P(18 * 3.7795);
        g.DrawRectangle(Pen(C("d5d8dc"), P(1)), Br(C("f8f9f9")), hlX, opY, hlW, hlH);
        Para(g, hlX + pad, opY + pad, hlW - 2 * pad, Runs(
            new R("行内半透明背景高亮（文字保持全不透明）").Sz(9).Hi(Mix(Yellow, C("f8f9f9"), 0.4))), P(9 * 1.35));
        y += trH + P(1);

        y = H2(g, y, "3.3 图像");
        var lbl9 = new R("JPEG 400×267 缩放显示", 8, LblC);
        g.DrawString(lbl9.Text, F(lbl9), Br(lbl9.Color), MX, y, XStringFormats.TopLeft);
        double imgY = y + P(8 * 1.35) + P(1);
        double i1w = P(150), i1h = P(100);
        g.DrawImage(XImage.FromFile(Path.Combine(ctx.AssetsDir, "photo.jpg")), MX, imgY, i1w, i1h);
        double i2x = MX + i1w + P(5 * 3.7795);
        var lbl9b = new R("PNG alpha 透明通道（深色底验证）", 8, LblC);
        g.DrawString(lbl9b.Text, F(lbl9b), Br(lbl9b.Color), i2x, y, XStringFormats.TopLeft);
        double i2w = P(120), i2h = P(60);
        g.DrawRectangle(Br(H2Ink), i2x, imgY, i2w + 2 * P(2 * 3.7795), i2h + 2 * P(2 * 3.7795));
        g.DrawImage(XImage.FromFile(Path.Combine(ctx.AssetsDir, "logo-alpha.png")), i2x + P(2 * 3.7795), imgY + P(2 * 3.7795), i2w, i2h);
        double i3x = i2x + i2w + 2 * P(2 * 3.7795) + P(5 * 3.7795);
        var lbl9c = new R("图片圆角+边框复合", 8, LblC);
        g.DrawString(lbl9c.Text, F(lbl9c), Br(lbl9c.Color), i3x, y, XStringFormats.TopLeft);
        double circD = P(110);
        double circY = imgY + (i1h - circD) / 2 + P(4);
        {
            var st = g.Save();
            g.IntersectClip(CornerPath(i3x, circY, circD, circD, circD / 2, circD / 2, circD / 2, circD / 2));
            g.DrawImage(XImage.FromFile(Path.Combine(ctx.AssetsDir, "photo.jpg")), i3x, circY, circD, circD);
            g.Restore(st);
        }
        g.DrawEllipse(Pen(Green, P(4)), i3x, circY, circD, circD);
        y = Math.Max(imgY + i1h, circY + circD + P(8 * 1.35)) + P(1);

        y = H2(g, y, "3.4 Flex 布局");
        double lcolW = (CW - gap) * 2 / 3, rcolW = (CW - gap) / 3;
        double lcolX = MX, rcolX = MX + lcolW + gap;
        double fy = y;
        double cellH = P(9 * 1.35) + 2 * pad;
        // 行 A：space-between
        var c1 = new R("space-between 左", 9, XColors.White);
        double w1 = g.MeasureString(c1.Text, F(c1)).Width + 2 * pad;
        g.DrawRectangle(Br(Red), lcolX, fy, w1, cellH);
        g.DrawString(c1.Text, F(c1), Br(c1.Color), lcolX + pad, fy + pad, XStringFormats.TopLeft);
        var c2 = new R("中", 9, XColors.White);
        double w2 = g.MeasureString(c2.Text, F(c2)).Width + 2 * pad;
        g.DrawRectangle(Br(Blue), lcolX + (lcolW - w2) / 2, fy, w2, cellH);
        g.DrawString(c2.Text, F(c2), Br(c2.Color), lcolX + (lcolW - w2) / 2 + pad, fy + pad, XStringFormats.TopLeft);
        var c3 = new R("右", 9, XColors.White);
        double w3 = g.MeasureString(c3.Text, F(c3)).Width + 2 * pad;
        g.DrawRectangle(Br(Green), lcolX + lcolW - w3, fy, w3, cellH);
        g.DrawString(c3.Text, F(c3), Br(c3.Color), lcolX + lcolW - w3 + pad, fy + pad, XStringFormats.TopLeft);
        fy += cellH + P(2 * 3.7795);
        // 行 B：flex-start + margin-left:auto
        var c4 = new R("flex-start", 9, XColors.White);
        double w4 = g.MeasureString(c4.Text, F(c4)).Width + 2 * pad;
        g.DrawRectangle(Br(Purple), lcolX, fy, w4, cellH);
        g.DrawString(c4.Text, F(c4), Br(c4.Color), lcolX + pad, fy + pad, XStringFormats.TopLeft);
        var c5 = new R("margin-left:auto 顶右", 9, XColors.White);
        double w5 = g.MeasureString(c5.Text, F(c5)).Width + 2 * pad;
        g.DrawRectangle(Br(DOrange), lcolX + lcolW - w5, fy, w5, cellH);
        g.DrawString(c5.Text, F(c5), Br(c5.Color), lcolX + lcolW - w5 + pad, fy + pad, XStringFormats.TopLeft);
        fy += cellH + P(2 * 3.7795);
        // 行 C：wrap
        var tr9 = new R("", 9, Ink);
        double wrapW = 0.47 * lcolW;
        g.DrawRectangle(Br(C("d6eaf8")), lcolX, fy, wrapW, cellH);
        g.DrawString("wrap A", F(tr9), Br(Ink), lcolX + pad, fy + pad, XStringFormats.TopLeft);
        g.DrawRectangle(Br(C("fadbd8")), lcolX + wrapW + P(2), fy, wrapW, cellH);
        g.DrawString("wrap B", F(tr9), Br(Ink), lcolX + wrapW + P(2) + pad, fy + pad, XStringFormats.TopLeft);
        fy += cellH + P(2);
        g.DrawRectangle(Br(C("d5f5e3")), lcolX, fy, wrapW, cellH);
        g.DrawString("wrap C 换行", F(tr9), Br(Ink), lcolX + pad, fy + pad, XStringFormats.TopLeft);
        fy += cellH + P(2 * 3.7795);
        // 行 D：嵌套等分列
        double nestH = P(8 * 1.35) + 2 * P(2 * 3.7795) + P(1 * 3.7795) + P(4 * 3.7795);
        g.DrawRectangle(Pen(FtrLn, P(1)), lcolX, fy, lcolW, nestH + 2 * P(1));
        double ncw = (lcolW - 2 * P(1)) / 2;
        double nInH = nestH;
        g.DrawRectangle(Br(Cream), lcolX + P(1), fy + P(1), ncw, nInH);
        var nt = new R("嵌套：flex:1 等分列左", 8, Ink);
        g.DrawString(nt.Text, F(nt), Br(nt.Color), lcolX + P(1) + P(2 * 3.7795), fy + P(1) + P(2 * 3.7795), XStringFormats.TopLeft);
        g.DrawRectangle(Br(C("eafaf1")), lcolX + P(1) + ncw, fy + P(1), ncw, nInH);
        g.DrawString("右列，列内再套", F(nt), Br(nt.Color), lcolX + P(1) + ncw + P(2 * 3.7795), fy + P(1) + P(2 * 3.7795), XStringFormats.TopLeft);
        double barY = fy + P(1) + P(2 * 3.7795) + P(8 * 1.35) + P(1 * 3.7795);
        double barH = P(4 * 3.7795), barW = ncw - 2 * P(2 * 3.7795);
        g.DrawRectangle(Br(C("aed6f1")), lcolX + P(1) + ncw + P(2 * 3.7795), barY, barW / 3, barH);
        g.DrawRectangle(Br(C("f5b7b1")), lcolX + P(1) + ncw + P(2 * 3.7795) + barW / 3, barY, barW * 2 / 3, barH);
        fy += nestH + 2 * P(1) + P(2 * 3.7795);
        // 右列：交叉轴居中（拉伸到左列等高）
        double colTotal = fy - y;
        g.DrawRectangle(Br(C("f4ecf7")), rcolX, y, rcolW, colTotal);
        var dashPen = new XPen(Purple, P(1)) { DashStyle = XDashStyle.Custom, DashPattern = new double[] { 4, 3 } };
        g.DrawRectangle(dashPen, rcolX, y, rcolW, colTotal);
        double pbW = 0.6 * rcolW, pbH = P(14 * 3.7795);
        g.DrawRectangle(Br(Purple), rcolX + (rcolW - pbW) / 2, y + (colTotal - pbH) / 2, pbW, pbH);
        var pbt = new R("交叉轴居中", 9, XColors.White);
        g.DrawString(pbt.Text, F(pbt), Br(pbt.Color),
            rcolX + (rcolW - g.MeasureString(pbt.Text, F(pbt)).Width) / 2,
            y + (colTotal - AsOf(pbt) - DsOf(pbt)) / 2, XStringFormats.TopLeft);
        y = fy + P(2);

        y = H2(g, y, "3.5 数据表格");
        double[] tcw = new[] { 0.12, 0.40, 0.20, 0.28 }.Select(f => f * CW).ToArray();
        double[] tcx = new double[5];
        tcx[0] = MX; for (int i = 1; i < 5; i++) tcx[i] = tcx[i - 1] + tcw[i - 1];
        double thh = P(8.5 * 1.35) + 2 * P(1.2 * 3.7795);
        double trh = P(8.5 * 1.35) + 2 * P(1 * 3.7795);
        var thd = new R("", 8.5, XColors.White).B();
        var tb = new R("", 8.5, Ink);
        var tbb = new R("", 8.5, Ink).B();
        string[] heads = { "编号 Code", "名称 Name", "数量 Qty", "金额 Amount (¥)" };
        g.DrawRectangle(Br(Slate), MX, y, CW, thh);
        for (int i = 0; i < 4; i++)
        {
            bool right = i >= 2;
            double tx2 = right ? tcx[i + 1] - P(2 * 3.7795) - g.MeasureString(heads[i], F(thd)).Width : tcx[i] + P(2 * 3.7795);
            g.DrawString(heads[i], F(thd), Br(XColors.White), tx2, y + P(1.2 * 3.7795), XStringFormats.TopLeft);
        }
        g.DrawLine(Pen(C("5d6d7e"), P(1)), MX, y + thh - 0.5 * P(1), PW - MX, y + thh - 0.5 * P(1));
        y += thh;
        string[][] body =
        {
            new[] { "A-001", "工业级渲染管线 Industrial pipeline", "12", "1,280.00" },
            new[] { "A-002", "矢量字体子集 Vector subset", "3", "96.50" },
            new[] { "B-014", "分页引擎 Pagination engine", "1", "2,400.00" },
            new[] { "B-015", "阴影合成器 Shadow compositor", "8", "640.00" },
            new[] { "C-101", "渐变缓存 Gradient cache", "24", "1,152.00" },
            new[] { "C-102", "Alpha 通道混合 Alpha blending", "6", "306.00" },
            new[] { "D-200", "CJK 换行分析 Line-break CJK", "2", "780.00" },
        };
        for (int ri = 0; ri < body.Length; ri++)
        {
            if (ri % 2 == 1) g.DrawRectangle(Br(C("f8f9f9")), MX, y, CW, trh);
            for (int ci = 0; ci < 4; ci++)
            {
                bool right = ci >= 2;
                double tx2 = right ? tcx[ci + 1] - P(2 * 3.7795) - g.MeasureString(body[ri][ci], F(tb)).Width : tcx[ci] + P(2 * 3.7795);
                g.DrawString(body[ri][ci], ci == 0 ? F(tbb) : F(tb), Br(Ink), tx2, y + P(1 * 3.7795), XStringFormats.TopLeft);
            }
            y += trh;
        }
        // 合计行
        g.DrawRectangle(Br(C("eaeded")), MX, y, CW, trh);
        g.DrawString("合计 Total (7 行明细)", F(tbb), Br(Ink), tcx[0] + P(2 * 3.7795), y + P(1 * 3.7795), XStringFormats.TopLeft);
        g.DrawString("56", F(tbb), Br(Ink), tcx[3] - P(2 * 3.7795) - g.MeasureString("56", F(tbb)).Width, y + P(1 * 3.7795), XStringFormats.TopLeft);
        g.DrawString("¥ 6,654.50", F(tbb), Br(Ink), tcx[4] - P(2 * 3.7795) - g.MeasureString("¥ 6,654.50", F(tbb)).Width, y + P(1 * 3.7795), XStringFormats.TopLeft);
        y += trh;
        // 表格网格线
        var grid = Pen(C("d5d8dc"), P(1));
        double tTop = y - trh * (body.Length + 1) - thh;
        g.DrawLine(grid, MX, tTop, PW - MX, tTop);
        for (int ri = 0; ri <= body.Length + 1; ri++)
        {
            double ly = tTop + thh + ri * trh;
            g.DrawLine(grid, MX, ly, PW - MX, ly);
        }
        for (int ci = 1; ci < 4; ci++)
            g.DrawLine(grid, tcx[ci], tTop + thh, tcx[ci], y);
        for (int ci = 1; ci < 4; ci++)
            g.DrawLine(Pen(C("5d6d7e"), P(1)), tcx[ci], tTop, tcx[ci], tTop + thh);

        // word-break 段落
        y += P(2 * 3.7795);
        double wbH = P(9 * 1.35) + 2 * P(1.5 * 3.7795);
        g.DrawRectangle(Br(Cream), MX, y, CW, wbH);
        g.DrawRectangle(Br(DOrange), MX, y, P(3), wbH);
        Para(g, MX + P(3) + P(1.5 * 3.7795), y + P(1.5 * 3.7795), CW - P(3) - 2 * P(1.5 * 3.7795), Runs(
            new R("超长URL自动断行验证：https://example.org/very/long/path/文件与单词混排abcdefghij无空格也可断开以适配窄容器supercalifragilistic 末行。").Sz(9)),
            P(9 * 1.35), breakAll: true);

        Footer(g, "3 / 3");
        g.Dispose();
    }
}
