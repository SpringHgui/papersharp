using Aspose.Pdf;
using Aspose.Pdf.Drawing;
using Aspose.Pdf.Text;
using SD = System.Drawing;
using RectShape = Aspose.Pdf.Drawing.Rectangle;

namespace PdfBench.Runner.Adapters;

/// <summary>
/// Aspose.PDF for .NET 手工还原适配器（无许可证=试用模式，每页顶部有红色 Evaluation 水印行）。
/// 策略：每页一个全页 Graph 承载全部矢量形状（Line 折线/Rectangle/Ellipse，绝对坐标、y 轴向上），
/// 文字全部用绝对定位 TextFragment（自研 run 级断行引擎，TextState.MeasureString 量宽），
/// 行高/字距/下划线/删除线/alpha 文字色为 TextState 原生；渐变用条纹近似；
/// 模糊阴影等做不到的特性按占位约定：#DDDDDD 灰块 + 红字 ✗ 特性名。
/// </summary>
public sealed class AsposePdfAdapter : IPdfAdapter
{
    public string Name => "aspose";

    // ---------- 单位与常量 ----------
    const float MmF = 2.834645f;                 // pt / mm
    static double Mm(double mm) => mm * MmF;
    static double Px(double px) => px * 0.75;     // css px -> pt
    const double W = 595.28, H = 841.89;         // A4 pt
    static readonly double ML = Mm(14), MR = Mm(14), MT = Mm(12);
    static readonly double CW = W - ML - MR;     // 内容宽 182mm

    // ---------- 颜色 ----------
    static Color C(string hex) => Color.Parse(hex);
    static (double r, double g, double b) Hex2(string hex)
    {
        hex = hex.TrimStart('#');
        return (Convert.ToInt32(hex.Substring(0, 2), 16) / 255.0,
                Convert.ToInt32(hex.Substring(2, 2), 16) / 255.0,
                Convert.ToInt32(hex.Substring(4, 2), 16) / 255.0);
    }
    static Color Ca(string hex, double alpha)
    {
        var (r, g, b) = Hex2(hex);
        return Color.FromArgb((int)Math.Round(alpha * 255), (int)(r * 255), (int)(g * 255), (int)(b * 255));
    }
    static Color MixHex(string ha, string hb, double t)
    {
        var a = Hex2(ha); var b = Hex2(hb);
        return Color.FromRgb(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t);
    }
    static string HexOnWhite(string hex, double alpha)
    {
        var (r, g, b) = Hex2(hex);
        r = r * alpha + (1 - alpha); g = g * alpha + (1 - alpha); b = b * alpha + (1 - alpha);
        return $"#{(int)(r * 255):x2}{(int)(g * 255):x2}{(int)(b * 255):x2}";
    }

    static readonly Color Ink = C("#222222");
    static readonly Color White = Color.White;

    // ---------- 字体 ----------
    const string FLatin = "Segoe UI";
    const string FCjk = "Microsoft YaHei";
    const string FMono = "Consolas";
    const string FLight = "Segoe UI Light";
    const string FSym = "Segoe UI Symbol";
    static readonly Dictionary<string, Font> FontCache = new();
    static Font GetFont(string name)
    {
        if (!FontCache.TryGetValue(name, out var f))
        {
            try { f = FontRepository.FindFont(name); }
            catch { f = FontRepository.FindFont("Arial"); }
            FontCache[name] = f;
        }
        return f;
    }
    static bool HasCjk(string s)
    {
        foreach (var ch in s)
            if (ch >= 0x2E80 && ch <= 0x9FFF || ch >= 0xF900 && ch <= 0xFAFF || ch >= 0xFF00 && ch <= 0xFFEF)
                return true;
        return false;
    }
    static string AutoFont(string s) => HasCjk(s) ? FCjk : FLatin;

    // ---------- 每页上下文 ----------
    sealed class Ctx
    {
        public Page Page = null!;
        public Graph Graph = null!;
    }
    static Ctx NewPage(Document doc)
    {
        var page = doc.Pages.Add();
        page.SetPageSize(W, H);
        page.PageInfo.Margin = new MarginInfo(0, 0, 0, 0);
        var ctx = new Ctx { Page = page, Graph = new Graph(W, H) };
        page.Paragraphs.Add(ctx.Graph); // 形状先画=在文字下层
        return ctx;
    }

    // ---------- 形状小工具（坐标一律 yTop：距页顶 pt；内部转 PDF y 向上） ----------
    static double Yd(double yTop) => H - yTop;

    static void FillRect(Ctx p, double x, double yTop, double w, double h, Color fill)
    {
        // 注意：Aspose 对「只有填充没有描边」的形状直接不渲染 → 描边取同色极细线
        p.Graph.Shapes.Add(new RectShape((float)x, (float)(Yd(yTop) - h), (float)w, (float)h)
        { GraphInfo = new GraphInfo { FillColor = fill, Color = fill, LineWidth = 0.3f } });
    }

    static void StrokeRect(Ctx p, double x, double yTop, double w, double h, Color color,
        double lw, int[]? dash = null)
    {
        var gi = new GraphInfo { Color = color, LineWidth = (float)lw };
        if (dash != null) gi.DashArray = dash;
        p.Graph.Shapes.Add(new RectShape((float)x, (float)(Yd(yTop) - h), (float)w, (float)h)
        { GraphInfo = gi });
    }

    static void BoxRect(Ctx p, double x, double yTop, double w, double h,
        Color? fill, Color? stroke, double lw, int[]? dash = null, double radius = 0)
    {
        var gi = new GraphInfo { LineWidth = (float)lw };
        if (fill != null) gi.FillColor = fill;
        if (stroke != null) gi.Color = stroke;
        else if (fill != null) { gi.Color = fill; gi.LineWidth = 0.3f; }
        if (dash != null) gi.DashArray = dash;
        var r = new RectShape((float)x, (float)(Yd(yTop) - h), (float)w, (float)h) { GraphInfo = gi };
        if (radius > 0) r.RoundedCornerRadius = radius;
        p.Graph.Shapes.Add(r);
    }

    static void LineSeg(Ctx p, double x1, double yTop1, double x2, double yTop2,
        Color color, double lw, int[]? dash = null)
    {
        var gi = new GraphInfo { Color = color, LineWidth = (float)lw };
        if (dash != null) gi.DashArray = dash;
        p.Graph.Shapes.Add(new Line(new float[] { (float)x1, (float)Yd(yTop1), (float)x2, (float)Yd(yTop2) })
        { GraphInfo = gi });
    }

    static void FillPoly(Ctx p, Color fill, Color? stroke, double lw, params (double x, double yTop)[] pts)
    {
        var arr = new float[(pts.Length + 1) * 2];
        for (int i = 0; i < pts.Length; i++)
        {
            arr[2 * i] = (float)pts[i].x; arr[2 * i + 1] = (float)Yd(pts[i].yTop);
        }
        arr[^2] = (float)pts[0].x; arr[^1] = (float)Yd(pts[0].yTop);
        var gi = new GraphInfo { FillColor = fill, LineWidth = (float)(lw > 0 ? lw : 0.3) };
        gi.Color = stroke ?? fill;
        p.Graph.Shapes.Add(new Line(arr) { GraphInfo = gi });
    }

    static void EllipseShape(Ctx p, double x, double yTop, double w, double h,
        Color? fill, Color? stroke, double lw, int[]? dash = null)
    {
        var gi = new GraphInfo { LineWidth = (float)lw };
        if (fill != null) gi.FillColor = fill;
        if (stroke != null) gi.Color = stroke;
        else if (fill != null) { gi.Color = fill; gi.LineWidth = 0.3f; }
        if (dash != null) gi.DashArray = dash;
        p.Graph.Shapes.Add(new Ellipse(x, Yd(yTop) - h, w, h) { GraphInfo = gi });
    }

    /// <summary>逐角圆角矩形（多段线近似），支持填充+描边+虚线。radii: TL,TR,BR,BL。</summary>
    static void RoundRect(Ctx p, double x, double yTop, double w, double h,
        (double tl, double tr, double br, double bl) rad,
        Color? fill, Color? stroke, double lw, int[]? dash = null)
    {
        var pts = new List<(double, double)>();
        void Arc(double cx, double cy, double a0Deg, double a1Deg, double rx, double ry)
        {
            const int n = 8;
            double a0 = a0Deg * Math.PI / 180, a1 = a1Deg * Math.PI / 180; // Math.Cos/Sin 用弧度
            for (int i = 0; i <= n; i++)
            {
                double a = a0 + (a1 - a0) * i / n;
                pts.Add((cx + rx * Math.Cos(a), cy + ry * Math.Sin(a)));
            }
        }
        double tl = Math.Min(rad.tl, Math.Min(w, h) / 2), tr = Math.Min(rad.tr, Math.Min(w, h) / 2),
               br = Math.Min(rad.br, Math.Min(w, h) / 2), bl = Math.Min(rad.bl, Math.Min(w, h) / 2);
        // 从左上角顺时针（数学角：0=右，90=上）
        Arc(x + tl, yTop + tl, 180, 270, tl, tl);           // TL
        Arc(x + w - tr, yTop + tr, 270, 360, tr, tr);       // TR
        Arc(x + w - br, yTop + h - br, 0, 90, br, br);      // BR
        Arc(x + bl, yTop + h - bl, 90, 180, bl, bl);        // BL
        var arr = new float[pts.Count * 2];
        for (int i = 0; i < pts.Count; i++) { arr[2 * i] = (float)pts[i].Item1; arr[2 * i + 1] = (float)Yd(pts[i].Item2); }
        var gi = new GraphInfo { LineWidth = (float)lw };
        if (fill != null) gi.FillColor = fill;
        if (stroke != null) gi.Color = stroke;
        else if (fill != null) { gi.Color = fill; gi.LineWidth = 0.3f; }
        if (dash != null) gi.DashArray = dash;
        p.Graph.Shapes.Add(new Line(arr) { GraphInfo = gi });
    }

    /// <summary>线性渐变：网格小块采样填充（Aspose 形状无裁剪路径，条纹会溢出盒外，故用 48×16 网格）。
    /// CSS 角语义：0=向上 90=向右 180=向下。</summary>
    static void LinearGradient(Ctx p, double x, double yTop, double w, double h, double angleDeg,
        params (double stop, string hex)[] stops)
    {
        const int NX = 48, NY = 16;
        double rad = angleDeg * Math.PI / 180;
        double dx = Math.Sin(rad), dy = -Math.Cos(rad); // yTop 向下为正
        double cx = x + w / 2, cy = yTop + h / 2;
        double len = Math.Abs(w * dx) + Math.Abs(h * dy);
        double cellW = w / NX, cellH = h / NY;
        for (int i = 0; i < NX; i++)
            for (int j = 0; j < NY; j++)
            {
                double px2 = x + (i + 0.5) * cellW - cx, py2 = yTop + (j + 0.5) * cellH - cy;
                double t = (px2 * dx + py2 * dy) / len + 0.5;
                FillRect(p, x + i * cellW, yTop + j * cellH, cellW + 0.05, cellH + 0.05, SampleHex(stops, Math.Clamp(t, 0, 1)));
            }
    }

    static Color SampleHex((double stop, string hex)[] stops, double t)
    {
        if (t <= stops[0].stop) return C(stops[0].hex);
        for (int i = 0; i < stops.Length - 1; i++)
            if (t <= stops[i + 1].stop)
            {
                double span = stops[i + 1].stop - stops[i].stop;
                return MixHex(stops[i].hex, stops[i + 1].hex, span <= 0 ? 0 : (t - stops[i].stop) / span);
            }
        return C(stops[^1].hex);
    }

    /// <summary>径向渐变：网格小块按到焦点距离采样。</summary>
    static void RadialGradient(Ctx p, double x, double yTop, double w, double h,
        double fx, double fy, string innerHex, string outerHex)
    {
        const int NX = 48, NY = 16;
        double cx = x + w * fx, cy = yTop + h * fy;
        double maxR = 0;
        foreach (var (kx, ky) in new[] { (x, yTop), (x + w, yTop), (x, yTop + h), (x + w, yTop + h) })
            maxR = Math.Max(maxR, Math.Sqrt((kx - cx) * (kx - cx) + (ky - cy) * (ky - cy)));
        double cellW = w / NX, cellH = h / NY;
        for (int i = 0; i < NX; i++)
            for (int j = 0; j < NY; j++)
            {
                double px2 = x + (i + 0.5) * cellW - cx, py2 = yTop + (j + 0.5) * cellH - cy;
                double t = Math.Clamp(Math.Sqrt(px2 * px2 + py2 * py2) / maxR, 0, 1);
                FillRect(p, x + i * cellW, yTop + j * cellH, cellW + 0.05, cellH + 0.05, MixHex(innerHex, outerHex, t));
            }
    }

    // ---------- 文字 ----------
    static readonly Dictionary<string, TextFragment> MeasureCache = new();
    static double Measure(string font, double size, double charSpacing, string s)
    {
        if (s.Length == 0) return 0;
        var key = font + "|" + size + "|" + charSpacing;
        if (!MeasureCache.TryGetValue(key, out var f))
        {
            f = new TextFragment("x");
            f.TextState.Font = GetFont(font);
            f.TextState.FontSize = (float)size;
            f.TextState.CharacterSpacing = (float)charSpacing;
            MeasureCache[key] = f;
        }
        try { return f.TextState.MeasureString(s); }
        catch { return s.Length * size * 0.5; }
    }

    /// <summary>放置单行文字，yBottom = 文字框底边距页顶 pt。返回实际宽度。</summary>
    static double PlaceText(Ctx p, string text, string font, double size, Color color,
        double x, double yBottom, bool bold = false, bool italic = false,
        bool underline = false, bool strikeout = false, double charSpacing = 0,
        Color? strokeColor = null)
    {
        if (text.Length == 0) return 0;
        var f = new TextFragment(text);
        f.TextState.Font = GetFont(font);
        f.TextState.FontSize = (float)size;
        f.TextState.ForegroundColor = color;
        if (bold && italic) f.TextState.FontStyle = FontStyles.Bold | FontStyles.Italic;
        else if (bold) f.TextState.FontStyle = FontStyles.Bold;
        else if (italic) f.TextState.FontStyle = FontStyles.Italic;
        f.TextState.Underline = underline;
        f.TextState.StrikeOut = strikeout;
        if (charSpacing > 0) f.TextState.CharacterSpacing = (float)charSpacing;
        f.Position = new Position((float)x, (float)Yd(yBottom));
        p.Page.Paragraphs.Add(f);
        return Measure(font, size, charSpacing, text);
    }

    // ---------- run 级富文本引擎 ----------
    sealed class Run
    {
        public string Text = ""; public string Font = FLatin; public double Size = 9;
        public Color Color = Ink; public bool Bold, Italic, Underline, StrikeOut;
        public double CharSpacing;
        public Color? Bg; public Color? Border; public double BorderW = 1; public double Radius;
        public double PadX; public double PadY;
        public Run CloneStyle() => (Run)MemberwiseClone();
    }

    static Run R(string text, string font, double size, Color color, bool bold = false, bool italic = false,
        bool underline = false, bool strikeout = false, Color? bg = null, Color? border = null,
        double borderW = 1, double radius = 0, double padX = 0, double padY = 0, double charSpacing = 0)
        => new()
        {
            Text = text, Font = font, Size = size, Color = color, Bold = bold, Italic = italic,
            Underline = underline, StrikeOut = strikeout, Bg = bg, Border = border,
            BorderW = borderW, Radius = radius, PadX = padX, PadY = padY, CharSpacing = charSpacing,
        };

    /// <summary>把 runs 拆成不可分原语（CJK 单字、拉丁词、空格），再贪心断行绘制。
    /// 返回消耗高度。leading = size*lineHeight；align: 0=left 1=center 2=right 3=justify。</summary>
    static double DrawRich(Ctx p, double x, double yTop, double maxW, List<Run> runs,
        double lineHeight, int align = 0, double firstIndent = 0, bool breakAll = false,
        Color? blockBg = null, double bgPad = 0)
    {
        // 1) 展平为原子
        var atoms = new List<(Run run, string text, double w, bool isSpace)>();
        foreach (var r in runs)
        {
            foreach (var (piece, isSpace) in Tokenize(r.Text, breakAll))
            {
                double w = Measure(r.Font, r.Size, r.CharSpacing, piece);
                atoms.Add((r, piece, w, isSpace));
            }
        }
        if (atoms.Count == 0) return 0;
        double baseSize = runs.Count > 0 ? runs.Max(r => r.Size) : 9;
        double leading = baseSize * 1.4 * lineHeight / 1.15; // 1.15≈默认行框比，粗校准
        leading = baseSize * lineHeight;

        // 2) 贪心断行
        var lines = new List<List<(Run run, string text, double w, bool isSpace)>>();
        var cur = new List<(Run run, string text, double w, bool isSpace)>();
        double curW = firstIndent;
        foreach (var a in atoms)
        {
            if (a.isSpace && cur.Count == 0) continue;
            if (curW + a.w > maxW && cur.Count > 0)
            {
                while (cur.Count > 0 && cur[^1].isSpace) { curW -= cur[^1].w; cur.RemoveAt(cur.Count - 1); }
                lines.Add(cur); cur = new(); curW = 0;
                if (a.isSpace) continue;
            }
            cur.Add(a); curW += a.w;
        }
        if (cur.Count > 0) lines.Add(cur);

        // 3) 背景块
        double totalH = leading * lines.Count;
        if (blockBg != null)
            FillRect(p, x - bgPad, yTop - bgPad, maxW + 2 * bgPad, totalH + 2 * bgPad, blockBg);

        // 4) 逐行绘制
        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            double lineW = line.Sum(a => a.w);
            double spaces = line.Count(a => a.isSpace);
            double dx = 0;
            if (align == 1) dx = (maxW - lineW) / 2;
            else if (align == 2) dx = maxW - lineW;
            double extra = 0;
            if (align == 3 && li < lines.Count - 1 && spaces > 0 && lineW < maxW)
                extra = (maxW - lineW) / spaces;
            double px = x + (li == 0 ? firstIndent : 0) + dx;
            double lineTop = yTop + li * leading;
            double maxSz = line.Max(a => a.run.Size);
            double baselineBottom = lineTop + maxSz * 1.12; // 文字框底边（含降部余量）
            foreach (var (run, text, w, isSpace) in line)
            {
                if (isSpace) { px += w + extra; continue; }
                double chipW = w + 2 * run.PadX;
                double chipTop = baselineBottom - run.Size * 1.12 - run.PadY;
                double chipH = run.Size * 1.12 + 2 * run.PadY;
                if (run.Bg != null)
                    RoundRect(p, px - run.PadX, chipTop, chipW, chipH,
                        (run.Radius, run.Radius, run.Radius, run.Radius), run.Bg, null, 0);
                if (run.Border != null)
                    RoundRect(p, px - run.PadX - run.BorderW / 2, chipTop - run.BorderW / 2,
                        chipW + run.BorderW, chipH + run.BorderW,
                        (run.Radius, run.Radius, run.Radius, run.Radius), null, run.Border, run.BorderW);
                PlaceText(p, text, run.Font, run.Size, run.Color, px, baselineBottom,
                    run.Bold, run.Italic, run.Underline, run.StrikeOut, run.CharSpacing);
                px += w + extra;
            }
        }
        return totalH;
    }

    static IEnumerable<(string piece, bool isSpace)> Tokenize(string s, bool breakAll)
    {
        int i = 0;
        while (i < s.Length)
        {
            char ch = s[i];
            if (ch == ' ') { yield return (" ", true); i++; continue; }
            if (IsCjkChar(ch)) { yield return (ch.ToString(), false); i++; continue; }
            if (breakAll) { yield return (ch.ToString(), false); i++; continue; }
            int j = i;
            while (j < s.Length && s[j] != ' ' && !IsCjkChar(s[j])) j++;
            yield return (s[i..j], false);
            i = j;
        }
    }
    static bool IsCjkChar(char ch) =>
        ch >= 0x2E80 && ch <= 0x9FFF || ch >= 0xF900 && ch <= 0xFAFF || ch >= 0xFF00 && ch <= 0xFFEF
        || ch is '，' or '。' or '（' or '）' or '：' or '；' or '、' or '—' or '“' or '”';

    // ---------- 页眉 / 页脚 ----------
    static double Header(Ctx p, string tag)
    {
        double y = MT;
        PlaceText(p, "PDFBENCH · VISUAL CHALLENGE", FLatin, Px(13), C("#34495e"), ML, y + Px(13) * 1.12,
            bold: true, charSpacing: Px(1));
        double tw = Measure(FLatin, Px(10), 0, tag);
        PlaceText(p, tag, FLatin, Px(10), C("#7f8c8d"), ML + CW - tw, y + Px(10) * 1.12);
        double lineY = y + Px(16) + Mm(2);
        LineSeg(p, ML, lineY, ML + CW, lineY, C("#34495e"), Px(2));
        return lineY + Mm(4); // margin-bottom 4mm
    }

    static void Footer(Ctx p, int pageNo)
    {
        double pillBottom = H - Mm(6);            // bottom:6mm
        double pillTop = pillBottom - Mm(16 / 3); // 16px 高
        double textBottom = (pillTop + pillBottom) / 2 + Px(9) * 0.4;
        PlaceText(p, "PdfBench · challenge.html · generated by spec v0.1", FLatin, Px(9),
            C("#7f8c8d"), ML, textBottom);
        string num = pageNo + " / 3";
        double nw = Measure(FLatin, Px(9), 0, num);
        double pillW = nw + Px(8);
        double pillX = ML + CW - pillW;
        RoundRect(p, pillX, pillTop, pillW, pillBottom - pillTop,
            (Px(8), Px(8), Px(8), Px(8)), null, C("#34495e"), 0.8);
        PlaceText(p, num, FLatin, Px(9), C("#34495e"), pillX + Px(4), (pillTop + pillBottom) / 2 + Px(9) * 0.4, bold: true);
        LineSeg(p, ML, pillTop - Mm(1.5), ML + CW, pillTop - Mm(1.5), C("#bdc3c7"), 0.75);
    }

    // ---------- 标题 ----------
    static double H1(Ctx p, double y, string s)
    {
        PlaceText(p, s, AutoFont(s), Px(20), C("#34495e"), ML, y + Px(20) * 1.12, bold: true);
        return y + Px(20) * 1.12 + Mm(3);
    }
    static double H2(Ctx p, double y, string s)
    {
        double top = y + Mm(3.5);
        LineSeg(p, ML, top, ML, top + Px(14) * 1.05, C("#3498db"), Px(4));
        PlaceText(p, s, AutoFont(s), Px(14), C("#2c3e50"), ML + Px(6) + Px(2), top + Px(14) * 1.12, bold: true);
        return top + Px(14) * 1.12 + Mm(2);
    }
    static double Lbl(Ctx p, double y, string s)
    {
        PlaceText(p, s, AutoFont(s), Px(8), C("#95a5a6"), ML, y + Px(8) * 1.12);
        return y + Px(8) * 1.12 + 1;
    }

    static void Fail(Ctx p, double x, double yTop, double w, double h, string feature)
    {
        FillRect(p, x, yTop, w, h, C("#DDDDDD"));
        PlaceText(p, "✗", FSym, Px(9), C("#e74c3c"), x + Mm(2), yTop + Mm(4) + Px(9) * 0.4);
        PlaceText(p, feature, FCjk, Px(9), C("#e74c3c"), x + Mm(2) + Px(11), yTop + Mm(4) + Px(9) * 1.12);
    }

    static int[] DashFor(double w, string style) => style switch
    {
        "dashed" => new int[] { (int)Math.Round(w * 2.2), (int)Math.Round(w * 1.8) },
        "dotted" => new int[] { 1, Math.Max(2, (int)Math.Round(w * 1.2)) },
        _ => null!,
    };

    // ---------- 边框盒（cell）：按线型画 4 边 ----------
    static void BorderBox(Ctx p, double x, double yTop, double w, double h,
        (double top, double right, double bottom, double left) widths,
        (string top, string right, string bottom, string left) styles,
        (string top, string right, string bottom, string left) colors)
    {
        void Side(bool horizontal, double len, double lw, string style, string hex, double sx, double sy)
        {
            Color color = C(hex);
            if (lw <= 0) return;
            double off = lw / 2;
            if (style == "double")
            {
                double t = Math.Max(1, lw / 3);
                if (horizontal)
                {
                    LineSeg(p, sx + t, sy + t, sx + len - t, sy + t, color, t);
                    LineSeg(p, sx + t, sy + lw - t, sx + len - t, sy + lw - t, color, t);
                }
                else
                {
                    LineSeg(p, sx + t, sy + t, sx + t, sy + len - t, color, t);
                    LineSeg(p, sx + lw - t, sy + t, sx + lw - t, sy + len - t, color, t);
                }
                return;
            }
            if (style == "groove" || style == "ridge")
            {
                bool groove = style == "groove";
                Color dark = MixHex(hex, "#000000", 0.35), light = MixHex(hex, "#ffffff", 0.55);
                double t = Math.Max(0.8, lw / 3);
                Color first = groove ? dark : light, last = groove ? light : dark;
                if (horizontal)
                {
                    LineSeg(p, sx, sy + t / 2, sx + len, sy + t / 2, first, t);
                    LineSeg(p, sx, sy + lw / 2, sx + len, sy + lw / 2, color, t);
                    LineSeg(p, sx, sy + lw - t / 2, sx + len, sy + lw - t / 2, last, t);
                }
                else
                {
                    LineSeg(p, sx + t / 2, sy, sx + t / 2, sy + len, first, t);
                    LineSeg(p, sx + lw / 2, sy, sx + lw / 2, sy + len, color, t);
                    LineSeg(p, sx + lw - t / 2, sy, sx + lw - t / 2, sy + len, last, t);
                }
                return;
            }
            int[]? dash = null;
            if (style == "dashed") dash = DashFor(lw, "dashed");
            if (style == "dotted") dash = DashFor(lw, "dotted");
            if (horizontal) LineSeg(p, sx, sy + off, sx + len, sy + off, color, lw, dash);
            else LineSeg(p, sx + off, sy, sx + off, sy + len, color, lw, dash);
        }
        Side(true, w, widths.top, styles.top, colors.top, x, yTop);
        Side(true, w, widths.bottom, styles.bottom, colors.bottom, x, yTop + h - widths.bottom);
        Side(false, h, widths.left, styles.left, colors.left, x, yTop);
        Side(false, h, widths.right, styles.right, colors.right, x + w - widths.right, yTop);
    }

    // =====================================================================
    public void Generate(AdapterContext ctx, string outPdfPath)
    {
        var doc = new Document();
        doc.PageInfo.Margin = new MarginInfo(0, 0, 0, 0);

        var p1 = NewPage(doc);
        double y = Header(p1, "Page 1 — Typography & Text");
        y = BuildPage1(p1, y);
        Footer(p1, 1);

        var p2 = NewPage(doc);
        y = Header(p2, "Page 2 — Borders & Corners");
        y = BuildPage2(p2, y);
        Footer(p2, 2);

        var p3 = NewPage(doc);
        y = Header(p3, "Page 3 — Effects, Images, Layout");
        y = BuildPage3(p3, y, ctx);
        Footer(p3, 3);

        doc.Save(outPdfPath);
    }

    // =====================================================================
    // PAGE 1
    // =====================================================================
    double BuildPage1(Ctx p, double y)
    {
        y = H1(p, y, "排版与文字 Rendering Test");
        y = H2(p, y, "1.1 层级与字重");

        // H3 subtitle + 右对齐抬升
        PlaceText(p, "H3 subtitle", FLatin, Px(11), C("#7f8c8d"), ML, y + Px(11) * 1.12, bold: true);
        PlaceText(p, "h3 → h1 右对齐抬升", FCjk, Px(9), C("#95a5a6"),
            ML + CW - Measure(FCjk, Px(9), 0, "h3 → h1 右对齐抬升"), y + Px(11) * 1.12);
        y += Px(11) * 1.12 + Mm(3);

        // 字重
        var wRuns = new List<Run>
        {
            R("字重：", FCjk, Px(10.5), Ink),
            R("粗体 bold", FCjk, Px(10.5), Ink, bold: true),
            R("、", FCjk, Px(10.5), Ink),
            R("斜体 italic", FLatin, Px(10.5), Ink, italic: true),
            R("、", FCjk, Px(10.5), Ink),
            R("粗斜体", FCjk, Px(10.5), Ink, bold: true, italic: true),
            R("、", FCjk, Px(10.5), Ink),
            R("细体 light 300", FLight, Px(10.5), Ink),
            R("、", FCjk, Px(10.5), Ink),
            R("下划线", FCjk, Px(10.5), Ink, underline: true),
            R("、", FCjk, Px(10.5), Ink),
            R("删除线", FCjk, Px(10.5), Ink, strikeout: true),
            R("、", FCjk, Px(10.5), Ink),
            R("SMALL CAPS ", FLatin, Px(8.4), Ink),
            R("效果", FCjk, Px(10.5), Ink),
            R("。", FCjk, Px(10.5), Ink),
        };
        y += DrawRich(p, ML, y, CW, wRuns, 1.5) + Mm(2.5);

        // 行内代码芯片
        var cRuns = new List<Run>
        {
            R("行内代码芯片：", FCjk, Px(10.5), Ink),
            R("PdfGenerator.Render()", FMono, Px(9.5), C("#8e24aa"),
                bg: C("#f3e5f5"), border: C("#ce93d8"), borderW: 0.75, radius: Px(3), padX: Px(4), padY: 1),
            R(" 与 ", FCjk, Px(10.5), Ink),
            R("spec.json", FMono, Px(9.5), C("#1565c0"),
                bg: C("#e3f2fd"), radius: Px(3), padX: Px(4), padY: 1),
            R(" 混排于行文中。", FCjk, Px(10.5), Ink),
        };
        y += DrawRich(p, ML, y, CW, cRuns, 1.7) + Mm(2.5);

        y = H2(p, y, "1.2 中西混排与行高");
        y += DrawRich(p, ML, y, CW,
            new List<Run> { R("中文段落混合 English words 与数字 0123456789，标点，以及括号（半角）与全角符号。 中英文基线对齐是渲染引擎的常见短板，The quick brown fox 应跳 over the lazy dog 而不断行错位。", FCjk, Px(10.5), Ink) }, 1.8)
            + Mm(2.5);

        // line-height 演示
        y = Lbl(p, y, "line-height: 1.0");
        y += DrawRich(p, ML, y, CW,
            new List<Run> { R("密集行距示例 The quick brown fox jumps over the lazy dog 中文密集行示例，行与行可能粘连。", FCjk, Px(10.5), Ink) }, 1.0,
            blockBg: C("#fdf6ec")) + Mm(1);
        y = Lbl(p, y, "line-height: 1.4");
        y += DrawRich(p, ML, y, CW,
            new List<Run> { R("正常行距示例 The quick brown fox jumps over the lazy dog 中文正常行距示例，阅读舒适。", FCjk, Px(10.5), Ink) }, 1.4,
            blockBg: C("#fdf6ec")) + Mm(1);
        y = Lbl(p, y, "line-height: 2.2");
        y += DrawRich(p, ML, y, CW,
            new List<Run> { R("宽松行距示例 The quick brown fox 中文宽松行距示例。", FCjk, Px(10.5), Ink) }, 2.2,
            blockBg: C("#fdf6ec")) + Mm(2);

        // letter-spacing
        PlaceText(p, "normal: tracking 字距", FCjk, Px(12), Ink, ML, y + Px(12) * 1.12); y += Px(12) * 1.4;
        PlaceText(p, "wide 2px: tracking 字距", FCjk, Px(12), Ink, ML, y + Px(12) * 1.12, charSpacing: Px(2)); y += Px(12) * 1.4;
        PlaceText(p, "wider 5px: tracking 字距", FCjk, Px(12), Ink, ML, y + Px(12) * 1.12, charSpacing: Px(5)); y += Px(12) * 1.4 + Mm(1);

        // text-indent
        y += DrawRich(p, ML, y, CW,
            new List<Run> { R("首行缩进两个字符：这是段落首行缩进的验证文本，缩进应恰好为 2 个全角字符宽度且仅作用于首行。", FCjk, Px(10.5), Ink) },
            1.5, firstIndent: Px(21)) + Mm(2.5);

        y = H2(p, y, "1.3 文字效果与对齐");

        // text-shadow：先画偏移阴影色，再画主色
        string sh = "阴影文字 Text Shadow";
        PlaceText(p, sh, FCjk, Px(16), C("#f5b7b1"), ML + Px(2), y + Px(16) * 1.12 + Px(2), bold: true);
        PlaceText(p, sh, FCjk, Px(16), C("#2c3e50"), ML, y + Px(16) * 1.12, bold: true);
        y += Px(16) * 1.4 + Mm(2.5);

        // 五色文字
        var colRuns = new List<Run>();
        (string, string)[] five = { ("红", "#e74c3c"), ("绿", "#27ae60"), ("蓝", "#2980b9"), ("橙", "#f39c12"), ("紫", "#8e44ad") };
        for (int i = 0; i < five.Length; i++)
        {
            colRuns.Add(R(five[i].Item1, FCjk, Px(10.5), C(five[i].Item2)));
            colRuns.Add(R(i < five.Length - 1 ? " " : " —— 五色文字，纯色无渐变。", FCjk, Px(10.5), Ink));
        }
        y += DrawRich(p, ML, y, CW, colRuns, 1.5) + Mm(2.5);

        // 对齐四格
        double gap = Mm(4);
        double tot = 1 + 1 + 1 + 1.6;
        double wAvail = CW - 3 * gap;
        double[] aw = { wAvail / tot, wAvail / tot, wAvail / tot, wAvail * 1.6 / tot };
        double[] ax = new double[4];
        ax[0] = ML;
        for (int i = 1; i < 4; i++) ax[i] = ax[i - 1] + aw[i - 1] + gap;
        string[] at = { "左对齐 left 文", "居中 center 文", "右对齐 right 文",
            "两端对齐 justify：This paragraph is justified so that both left and right edges form clean lines across its full width 无论中英文。" };
        for (int i = 0; i < 4; i++)
        {
            double cellH = Measure(FCjk, Px(9), 0, "0") * 0 + Px(9) * 1.4 * (i == 3 ? 4 : 1) + Mm(5);
            FillRect(p, ax[i], y, aw[i], cellH, C("#ecf0f1"));
            DrawRich(p, ax[i] + Mm(2.5), y + Mm(2.5), aw[i] - Mm(5),
                new List<Run> { R(at[i], AutoFont(at[i]), Px(9), Ink) }, 1.4, i);
        }
        y += Px(9) * 1.4 * 4 + Mm(5) + Mm(1);

        y = H2(p, y, "1.4 列表与嵌套");

        // 三列列表
        double colW = (CW - 2 * gap) / 3;
        double listY = y;
        double ly;

        // 列1: disc + 嵌套
        ly = Lbl(p, listY, "unordered disc，marker 着色");
        double li1 = ly, ind = 12;
        string[] cjk1 = { "项目一：", "项目二：Marker 颜色独立于文字", "项目三：三级嵌套入口" };
        PlaceText(p, "•", FLatin, Px(10), C("#7f8c8d"), ML + 2, li1 + Px(10) * 1.12);
        DrawRich(p, ML + ind, li1, colW - ind, new List<Run>
        { R("项目一：", FCjk, Px(10), C("#e74c3c")), R("无序列表 disc 符号", FCjk, Px(10), Ink) }, 1.3);
        li1 += Px(10) * 1.3 * 1 + 2;
        PlaceText(p, "•", FLatin, Px(10), C("#7f8c8d"), ML + 2, li1 + Px(10) * 1.12);
        PlaceText(p, "项目二：Marker 颜色独立于文字", FCjk, Px(10), Ink, ML + ind, li1 + Px(10) * 1.12);
        li1 += Px(10) * 1.3 + 2;
        PlaceText(p, "•", FLatin, Px(10), C("#7f8c8d"), ML + 2, li1 + Px(10) * 1.12);
        PlaceText(p, "项目三：三级嵌套入口", FCjk, Px(10), Ink, ML + ind, li1 + Px(10) * 1.12);
        li1 += Px(10) * 1.3 + 2;
        PlaceText(p, "•", FLatin, Px(10), C("#7f8c8d"), ML + ind + 2, li1 + Px(10) * 1.12);
        PlaceText(p, "嵌套层 A", FCjk, Px(10), Ink, ML + ind * 2, li1 + Px(10) * 1.12);
        li1 += Px(10) * 1.3 + 2;
        PlaceText(p, "▪", FLatin, Px(10), C("#7f8c8d"), ML + ind * 2 + 2, li1 + Px(10) * 1.12);
        PlaceText(p, "方块 square 层", FCjk, Px(10), Ink, ML + ind * 3, li1 + Px(10) * 1.12);
        li1 += Px(10) * 1.3;

        // 列2: ordered
        double lx = ML + colW + gap;
        ly = Lbl2(p, listY, lx, "ordered decimal / lower-alpha / upper-roman");
        li1 = ly;
        void OL(double x, string marker, string text, double size)
        {
            PlaceText(p, marker, FLatin, size, Ink, x, li1 + size * 1.12);
            PlaceText(p, text, FCjk, size, Ink, x + Measure(FLatin, size, 0, marker) + 4, li1 + size * 1.12);
            li1 += size * 1.3 + 2;
        }
        OL(lx + 2, "1.", "十进制 ordered", Px(10));
        li1 -= 2; OL(lx + 2, "2.", "第二项", Px(10));
        OL(lx + 16, "a.", "lower-alpha 子项", Px(10));
        OL(lx + 16, "b.", "子项二", Px(10));
        li1 += 2;
        OL(lx + 2, "I.", "upper-roman 独立编号", Px(10));
        li1 -= 2; OL(lx + 2, "II.", "应为 II", Px(10));
        double col2End = li1;

        // 列3: circle + none
        double rx = ML + 2 * (colW + gap);
        ly = Lbl2(p, listY, rx, "自定义 marker：circle + 无标记");
        li1 = ly;
        PlaceText(p, "◦", FLatin, Px(10), Ink, rx + 2, li1 + Px(10) * 1.12);
        PlaceText(p, "circle 空心圈", FCjk, Px(10), Ink, rx + 12, li1 + Px(10) * 1.12);
        li1 += Px(10) * 1.3 + 2;
        PlaceText(p, "none：无标记但有缩进对齐", FCjk, Px(10), Ink, rx + 12, li1 + Px(10) * 1.12);
        li1 += Px(10) * 1.3 + 2;
        PlaceText(p, "◦", FLatin, Px(10), Ink, rx + 2, li1 + Px(10) * 1.12);
        PlaceText(p, "circle 第二项", FCjk, Px(10), Ink, rx + 12, li1 + Px(10) * 1.12);
        li1 += Px(10) * 1.3;

        y = Math.Max(listY + Px(8) * 1.2 + Px(10) * 1.3 * 6, col2End) + Mm(2);
        return y;
    }

    static double Lbl2(Ctx p, double y, double x, string s)
    {
        PlaceText(p, s, AutoFont(s), Px(8), C("#95a5a6"), x, y + Px(8) * 1.12);
        return y + Px(8) * 1.12 + 2;
    }

    // =====================================================================
    // PAGE 2
    // =====================================================================
    double BuildPage2(Ctx p, double y)
    {
        y = H1(p, y, "边框家族与圆角");
        y = H2(p, y, "2.1 四种线型 × 三种宽度");

        double cw = Mm(30), gap = Mm(4);
        Color red = C("#e74c3c"), blue = C("#2980b9"), green = C("#27ae60"), purple = C("#8e44ad");
        double cellH = Px(9) * 1.12 + Mm(5);
        void StyleRow((string label, double w, string style, string hex)[] cells, ref double yy)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                double x = ML + i * (cw + gap);
                var c = cells[i];
                BorderBox(p, x, yy, cw, cellH, (Px(c.w), Px(c.w), Px(c.w), Px(c.w)),
                    (c.style, c.style, c.style, c.style), (c.hex, c.hex, c.hex, c.hex));
                PlaceText(p, c.label, AutoFont(c.label), Px(9), Ink, x + Mm(2.5), yy + Px(c.w) + Mm(2.5) + Px(9) * 1.12);
            }
            yy += cellH + Mm(1.5);
        }
        StyleRow(new[] { ("solid 2px", 2.0, "solid", "#e74c3c"), ("solid 4px", 4.0, "solid", "#e74c3c"), ("solid 8px", 8.0, "solid", "#e74c3c") }, ref y);
        StyleRow(new[] { ("dashed 2px", 2.0, "dashed", "#2980b9"), ("dashed 4px", 4.0, "dashed", "#2980b9"), ("dashed 4px+", 8.0, "dashed", "#2980b9") }, ref y);
        StyleRow(new[] { ("dotted 2px", 2.0, "dotted", "#27ae60"), ("dotted 4px", 4.0, "dotted", "#27ae60"), ("double 8px", 8.0, "double", "#8e44ad") }, ref y);
        StyleRow(new[] { ("double 4px", 4.0, "double", "#8e44ad"), ("groove 4px", 4.0, "groove", "#d35400"), ("ridge 4px", 4.0, "ridge", "#16a085") }, ref y);

        y = H2(p, y + Mm(1), "2.2 逐边异构");
        double col3 = (CW - 2 * gap) / 3;
        double ph = Px(9) * 1.12 + Mm(10);
        // 四边四线型
        BorderBox(p, ML, y, col3, ph, (Px(3), Px(3), Px(3), Px(4)),
            ("solid", "dashed", "dotted", "double"), ("#2980b9", "#e74c3c", "#27ae60", "#f39c12"));
        PlaceText(p, "四边四线型：上实/右虚/下点/左双", FCjk, Px(9), Ink, ML + Mm(3), y + Mm(5) + Px(9) * 1.12);
        // 四边异色
        double x2 = ML + col3 + gap;
        BorderBox(p, x2, y, col3, ph, (Px(3), Px(3), Px(3), Px(3)),
            ("solid", "solid", "solid", "solid"), ("#e74c3c", "#2980b9", "#27ae60", "#f39c12"));
        PlaceText(p, "四边同线型异色：上红/右蓝/下绿/左橙", FCjk, Px(9), Ink, x2 + Mm(3), y + Mm(5) + Px(9) * 1.12);
        // currentColor
        double x3 = ML + 2 * (col3 + gap);
        BorderBox(p, x3, y, col3, ph, (Px(2), Px(2), Px(2), Px(2)),
            ("solid", "solid", "solid", "solid"), ("#c0392b", "#c0392b", "#c0392b", "#c0392b"));
        PlaceText(p, "currentColor 边框：边色随字色变红", FCjk, Px(9), C("#c0392b"), x3 + Mm(3), y + Mm(5) + Px(9) * 1.12);
        y += ph + Mm(1.5);

        // 单边三格
        double sh2 = Px(9) * 1.12 + Mm(4);
        FillRect(p, ML, y, col3, sh2, C("#ecf0f1"));
        LineSeg(p, ML, y + sh2 - 1.5, ML + col3, y + sh2 - 1.5, purple, Px(3));
        PlaceText(p, "仅底边：文章式分隔线", FCjk, Px(9), Ink, ML + Mm(3), y + Mm(2) + Px(9) * 1.12);
        FillRect(p, x2, y, col3, sh2, C("#ecf0f1"));
        LineSeg(p, x2 + 2.5, y, x2 + 2.5, y + sh2, green, Px(5));
        PlaceText(p, "仅左边：引文强调条", FCjk, Px(9), Ink, x2 + Mm(3), y + Mm(2) + Px(9) * 1.12);
        LineSeg(p, x3, y + 1, x3 + col3, y + 1, blue, Px(2));
        LineSeg(p, x3, y + sh2 - 1, x3 + col3, y + sh2 - 1, blue, Px(2));
        PlaceText(p, "上下边：band 样式", FCjk, Px(9), Ink, x3 + Mm(3), y + Mm(2) + Px(9) * 1.12);
        y += sh2 + Mm(1);

        y = H2(p, y, "2.3 圆角");
        // 五个圆角盒 r=0/4/10/8-2/14-2-14-2
        double bw = Mm(26), bh = Mm(18);
        (string, (double, double, double, double))[] radii =
        {
            ("r=0", (0, 0, 0, 0)),
            ("4px", (Px(4), Px(4), Px(4), Px(4))),
            ("10px", (Px(10), Px(10), Px(10), Px(10))),
            ("8/2 对角", (Px(8), Px(2), Px(8), Px(2))),
            ("四角异值", (Px(14), Px(2), Px(14), Px(2))),
        };
        for (int i = 0; i < radii.Length; i++)
        {
            double x = ML + i * (bw + gap);
            RoundRect(p, x, y, bw, bh, radii[i].Item2, C("#d6eaf8"), blue, Px(2));
            PlaceText(p, radii[i].Item1, AutoFont(radii[i].Item1), Px(9), Ink, x + Mm(2.5), y + Mm(2.5) + Px(9) * 1.12);
        }
        y += bh + Mm(2);

        // 正圆 / 椭圆 / 胶囊 / 虚线圆角
        EllipseShape(p, ML, y, Mm(18), Mm(18), C("#fadbd8"), C("#c0392b"), Px(2));
        PlaceText(p, "50% 正圆", FCjk, Px(8), C("#95a5a6"), ML + Mm(20), y + Mm(9) + Px(8) * 0.4);
        double ex = ML + Mm(32);
        EllipseShape(p, ex, y, Mm(11), Mm(18), C("#d5f5e3"), C("#27ae60"), Px(2));
        PlaceText(p, "50% 椭圆", FCjk, Px(8), C("#95a5a6"), ex + Mm(13), y + Mm(9) + Px(8) * 0.4);
        double px2 = ex + Mm(32);
        RoundRect(p, px2, y + Mm(4), Mm(34), Mm(10), (Mm(5), Mm(5), Mm(5), Mm(5)), C("#fdebd0"), C("#d35400"), Px(3));
        PlaceText(p, "胶囊 pill r=高度/2", FCjk, Px(9), Ink, px2 + Mm(2), y + Mm(9) + Px(9) * 0.4);
        double dx2 = px2 + Mm(38);
        RoundRect(p, dx2, y, Mm(26), Mm(18), (Px(10), Px(10), Px(10), Px(10)),
            C("#d6eaf8"), purple, Px(3), DashFor(Px(3), "dashed"));
        PlaceText(p, "圆角+虚线复合", FCjk, Px(8), C("#95a5a6"), dx2 + Mm(28), y + Mm(9) + Px(8) * 0.4);
        y += Mm(18) + Mm(2);

        // 复合盒
        double halfW = (CW - gap) / 2;
        double compH = Px(9) * 1.12 * 3 + Mm(6) + Px(6) * 2;
        RoundRect(p, ML, y, halfW, compH, (Px(14), Px(14), Px(14), Px(14)), C("#f8f9f9"), C("#34495e"), Px(6));
        DrawRich(p, ML + Px(6) + Mm(3), y + Px(6) + Mm(3), halfW - 2 * (Px(6) + Mm(3)),
            new List<Run> { R("厚边框(6px)+大圆角(14px)+内边距+背景色 复合盒：边框与背景的圆角必须同心，内边距把内容从边框推开。", FCjk, Px(9), Ink) }, 1.4);
        // overflow hidden 裁切过头圆角
        double ox = ML + halfW + gap;
        RoundRect(p, ox, y, halfW, compH, (Px(10), Px(10), Px(10), Px(10)), White, green, Px(2));
        RoundRect(p, ox + 1, y + 1, halfW - 2, Mm(2 * 2 + 9 * 0.75 / 25.4 * 10) + Px(9) * 1.12,
            (Px(9), Px(9), 0, 0), green, null, 0);
        PlaceText(p, "裁切过头圆角", FCjk, Px(9), White, ox + Mm(3), y + Mm(2) + Px(9) * 1.12 + 1);
        PlaceText(p, "overflow:hidden 子元素背景填满左上圆角", FCjk, Px(9), Ink, ox + Mm(3), y + Mm(2) + Px(9) * 1.12 * 2 + Mm(2) + 4);
        y += compH + Mm(1);

        y = H2(p, y, "2.4 边框即图形");
        // 等腰三角（border-bottom 红）
        FillPoly(p, red, null, 0, (ML, y + Mm(18)), (ML + Mm(28), y + Mm(18)), (ML + Mm(14), y));
        PlaceText(p, "border 三角形（等腰）", FCjk, Px(8), C("#95a5a6"), ML + Mm(31), y + Mm(9) + Px(8) * 0.4);
        // 直角三角（右向 橙）
        double t2x = ML + Mm(31) + Measure(FCjk, Px(8), 0, "border 三角形（等腰）") + Mm(3);
        FillPoly(p, C("#f39c12"), null, 0, (t2x, y), (t2x, y + Mm(12)), (t2x + Mm(16), y + Mm(12)));
        PlaceText(p, "直角三角形（右向）", FCjk, Px(8), C("#95a5a6"), t2x + Mm(19), y + Mm(6) + Px(8) * 0.4);
        // 倒三角（蓝）
        double t3x = t2x + Mm(19) + Measure(FCjk, Px(8), 0, "直角三角形（右向）") + Mm(3);
        FillPoly(p, blue, null, 0, (t3x, y), (t3x + Mm(28), y), (t3x + Mm(14), y + Mm(18)));
        PlaceText(p, "倒三角", FCjk, Px(8), C("#95a5a6"), t3x + Mm(31), y + Mm(9) + Px(8) * 0.4);
        y += Mm(18);
        return y;
    }

    // =====================================================================
    // PAGE 3
    // =====================================================================
    double BuildPage3(Ctx p, double y, AdapterContext ctx)
    {
        y = H1(p, y, "效果、图像与布局");
        y = H2(p, y, "3.1 阴影 box-shadow");

        double bw = Mm(36), bh = Mm(16);
        double x = ML + Mm(4);
        // 1) 高斯模糊外阴影：不支持 → 占位
        FillRect(p, x, y, bw, bh, White);
        StrokeRect(p, x, y, bw, bh, C("#ecf0f1"), 0.5);
        Fail(p, x, y, bw, bh, "外阴影 模糊");
        // 2) 硬阴影：原生可画
        x += bw + Mm(8);
        FillRect(p, x + Px(5), y + Px(5), bw, bh, C("#f1c40f"));
        FillRect(p, x, y, bw, bh, White);
        StrokeRect(p, x, y, bw, bh, C("#f1c40f"), Px(1));
        PlaceText(p, "硬阴影 无模糊", FCjk, Px(9), Ink, x + Mm(2.5), y + Mm(2.5) + Px(9) * 1.12);
        // 3) 内阴影 inset：不支持 → 占位
        x += bw + Mm(8);
        FillRect(p, x, y, bw, bh, C("#ecf0f1"));
        Fail(p, x, y, bw, bh, "内阴影 inset");
        // 4) 三重阴影：spread 环 + inset 底带可画，模糊层占位
        x += bw + Mm(8);
        FillRect(p, x, y, bw, bh, White);
        FillRect(p, x, y + bh - 4, bw, 4, C("#e8f6f3"));
        StrokeRect(p, x, y, bw, bh, C("#2980b9"), Px(2));
        PlaceText(p, "三重阴影叠加", FCjk, Px(9), Ink, x + Mm(2.5), y + Mm(2.5) + Px(9) * 1.12);
        PlaceText(p, "✗ 模糊层", FCjk, Px(6.5), C("#e74c3c"), x + Mm(2.5), y + Mm(2.5) + Px(9) * 1.12 + Px(8));
        y += bh + Px(5) + Mm(1);

        y = H2(p, y, "3.2 渐变与透明");
        double gap3 = Mm(3);
        double gw = (CW - 3 * gap3) / 4;
        LinearGradient(p, ML, y, gw, Mm(16), 45, (0, "#e74c3c"), (0.5, "#f1c40f"), (1, "#27ae60"));
        LinearGradient(p, ML + gw + gap3, y, gw, Mm(16), 90, (0, "#34495e"), (1, "#ecf0f1"));
        RadialGradient(p, ML + 2 * (gw + gap3), y, gw, Mm(16), 0.3, 0.4, "#ffffff", "#2980b9");
        double g4x = ML + 3 * (gw + gap3);
        LinearGradient(p, g4x, y, gw, Mm(16), 180,
            (0, HexOnWhite("#e74c3c", 0.8)), (1, HexOnWhite("#2980b9", 0.2)));
        PlaceText(p, "渐变+alpha", FCjk, Px(8), White, g4x + 2, y + Px(8) * 1.12 + 2);
        y += Mm(16) + Mm(2);

        // 半透明叠加
        double vx = ML;
        FillRect(p, vx, y, Mm(22), Mm(22), Ca("#e74c3c", 0.55));
        FillRect(p, vx + Mm(9), y, Mm(22), Mm(22), Ca("#27ae60", 0.55));
        FillRect(p, vx + Mm(4.5), y + Mm(8), Mm(22), Mm(22), Ca("#2980b9", 0.55));
        PlaceText(p, "半透明三色叠加（重叠区混色）", FCjk, Px(8), C("#95a5a6"), vx + Mm(33), y + Mm(13) + Px(8) * 0.4);
        double opx = ML + Mm(58);
        FillRect(p, opx, y + Mm(4), Mm(30), Mm(18), Ca("#2980b9", 0.4));
        PlaceText(p, "opacity 0.4 整块淡出", FCjk, Px(9), Color.FromArgb((int)(0.4 * 255), 255, 255, 255),
            opx + Mm(2.5), y + Mm(6.5) + Px(9) * 1.12);
        double hlx = opx + Mm(34);
        FillRect(p, hlx, y + Mm(4), Mm(34), Mm(18), C("#f8f9f9"));
        StrokeRect(p, hlx, y + Mm(4), Mm(34), Mm(18), C("#d5d8dc"), Px(1));
        DrawRich(p, hlx + Mm(1.5), y + Mm(5.5), Mm(31),
            new List<Run> { R("行内半透明背景高亮（文字保持全不透明）", FCjk, Px(9), Ink,
                bg: Ca("#f1c40f", 0.4), padX: 1, padY: 1) }, 1.6);
        y += Mm(26) + Mm(1);

        y = H2(p, y, "3.3 图像");
        // JPEG
        double ix = ML;
        y = Lbl(p, y, "JPEG 400×267 缩放显示");
        double imgTop = y;
        p.Page.AddImage(System.IO.Path.Combine(ctx.AssetsDir, "photo.jpg"),
            new Aspose.Pdf.Rectangle(ix, Yd(imgTop + Px(100)), ix + Px(150), Yd(imgTop)));
        // PNG alpha on dark（深色底直接压进图片：AddImage 内容位于 Graph 矢量层之下，不能用矢量垫底）
        double ix2 = ix + Px(150) + Mm(5);
        Lbl2(p, y - Px(8) * 1.12 - 2, ix2, "PNG alpha 透明通道（深色底验证）");
        double abW = Px(120) + Mm(4), abH = Px(60) + Mm(4);
        string flatPng = MakeAlphaOnDark(System.IO.Path.Combine(ctx.AssetsDir, "logo-alpha.png"),
            System.IO.Path.Combine(ctx.OutDir, "aspose_alpha_flat.png"), abW, abH);
        p.Page.AddImage(flatPng, new Aspose.Pdf.Rectangle(ix2, Yd(imgTop + abH), ix2 + abW, Yd(imgTop)));
        // 圆形裁切照片（System.Drawing 预裁）
        double ix3 = ix2 + Px(120) + Mm(4) + Mm(5);
        Lbl2(p, y - Px(8) * 1.12 - 2, ix3, "图片圆角+边框复合");
        string circlePng = MakeCirclePhoto(System.IO.Path.Combine(ctx.AssetsDir, "photo.jpg"),
            System.IO.Path.Combine(ctx.OutDir, "aspose_circle.png"));
        double d = Px(110);
        p.Page.AddImage(circlePng, new Aspose.Pdf.Rectangle(ix3 + Px(4), Yd(imgTop + Px(4) + d), ix3 + Px(4) + d, Yd(imgTop + Px(4))));
        EllipseShape(p, ix3, imgTop, d + Px(8), d + Px(8), null, C("#27ae60"), Px(4));
        y = imgTop + Math.Max(Px(100), Px(60) + Mm(4)) + Px(8) + Mm(2);

        y = H2(p, y, "3.4 Flex 布局");
        y = BuildFlex(p, y);

        y = H2(p, y, "3.5 数据表格");
        y = BuildDataTable(p, y);

        // word-break
        double wbH = DrawRich(p, ML + Mm(1.5), y + Mm(1.5), CW - Mm(3) - Px(3),
            new List<Run> { R("超长URL自动断行验证：https://example.org/very/long/path/文件与单词混排abcdefghij无空格也可断开以适配窄容器supercalifragilistic 末行。", FCjk, Px(9), Ink) },
            1.4, breakAll: true, blockBg: C("#fdf6ec"));
        LineSeg(p, ML, y, ML, y + wbH, C("#d35400"), Px(3));
        y += wbH + Mm(3);
        return y;
    }

    static string MakeAlphaOnDark(string src, string dst, double boxW, double boxH)
    {
        try
        {
            using var img = SD.Image.FromFile(src);
            int w = (int)Math.Round(boxW * 2), h = (int)Math.Round(boxH * 2);
            using var bmp = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.Clear(SD.Color.FromArgb(0x2c, 0x3e, 0x50));
                g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
                float dw = img.Width * 0.5f, dh = img.Height * 0.5f;
                g.DrawImage(img, (w - dw) / 2, (h - dh) / 2, dw, dh);
            }
            bmp.Save(dst, SD.Imaging.ImageFormat.Png);
            return dst;
        }
        catch { return src; }
    }

    static string MakeCirclePhoto(string src, string dst)    {
        try
        {
            using var img = SD.Image.FromFile(src);
            const int S = 330;
            using var bmp = new SD.Bitmap(S, S, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                using var path = new SD.Drawing2D.GraphicsPath();
                path.AddEllipse(0, 0, S, S);
                g.SetClip(path);
                g.DrawImage(img, 0, 0, S, S);
            }
            bmp.Save(dst, SD.Imaging.ImageFormat.Png);
            return dst;
        }
        catch { return src; }
    }

    // ---------- 3.4 flex ----------
    double BuildFlex(Ctx p, double y)
    {
        double gap = Mm(4);
        double rightW = (CW - gap) / 3, leftW = CW - gap - rightW;
        double top = y;

        // 左列行1：space-between 三块
        double rowX = leftW;
        double c1w = Measure(FCjk, Px(9), 0, "space-between 左") + Mm(5);
        double c2w = Measure(FCjk, Px(9), 0, "中") + Mm(5);
        double c3w = Measure(FCjk, Px(9), 0, "右") + Mm(5);
        double chipH = Px(9) * 1.12 + 2 * Px(5);
        FillRect(p, ML, y, c1w, chipH, C("#e74c3c"));
        PlaceText(p, "space-between 左", FCjk, Px(9), White, ML + Mm(2.5), y + Px(5) + Px(9) * 1.12);
        FillRect(p, ML + (rowX - c2w) / 2, y, c2w, chipH, C("#2980b9"));
        PlaceText(p, "中", FCjk, Px(9), White, ML + (rowX - c2w) / 2 + Mm(2.5), y + Px(5) + Px(9) * 1.12);
        FillRect(p, ML + rowX - c3w, y, c3w, chipH, C("#27ae60"));
        PlaceText(p, "右", FCjk, Px(9), White, ML + rowX - c3w + Mm(2.5), y + Px(5) + Px(9) * 1.12);
        y += chipH + Px(6);

        // 行2：flex-start + margin-left:auto
        double f1w = Measure(FCjk, Px(9), 0, "flex-start") + Mm(5);
        double f2w = Measure(FCjk, Px(9), 0, "margin-left:auto 顶右") + Mm(5);
        FillRect(p, ML, y, f1w, chipH, C("#8e44ad"));
        PlaceText(p, "flex-start", FCjk, Px(9), White, ML + Mm(2.5), y + Px(5) + Px(9) * 1.12);
        FillRect(p, ML + rowX - f2w, y, f2w, chipH, C("#d35400"));
        PlaceText(p, "margin-left:auto 顶右", FCjk, Px(9), White, ML + rowX - f2w + Mm(2.5), y + Px(5) + Px(9) * 1.12);
        y += chipH + Px(6);

        // 行3：wrap 47%
        double wcw = rowX * 0.47;
        double wrH = Px(9) * 1.12 + 2 * Px(5);
        FillRect(p, ML, y, wcw, wrH, C("#d6eaf8"));
        PlaceText(p, "wrap A", FLatin, Px(9), Ink, ML + Mm(2.5), y + Px(5) + Px(9) * 1.12);
        FillRect(p, ML + wcw + 1.5, y, wcw, wrH, C("#fadbd8"));
        PlaceText(p, "wrap B", FLatin, Px(9), Ink, ML + wcw + 1.5 + Mm(2.5), y + Px(5) + Px(9) * 1.12);
        y += wrH + 2;
        FillRect(p, ML, y, wcw, wrH, C("#d5f5e3"));
        PlaceText(p, "wrap C 换行", FCjk, Px(9), Ink, ML + Mm(2.5), y + Px(5) + Px(9) * 1.12);
        y += wrH + Px(6);

        // 行4：嵌套等分列
        double nestH = Px(8) * 1.12 + Mm(4) * 2;
        StrokeRect(p, ML, y, rowX, nestH, C("#bdc3c7"), 0.75);
        FillRect(p, ML + 1, y + 1, rowX / 2 - 1, nestH - 2, C("#fdf6ec"));
        PlaceText(p, "嵌套：flex:1 等分列左", FCjk, Px(8), Ink, ML + Mm(2), y + Mm(2) + Px(8) * 1.12);
        FillRect(p, ML + rowX / 2, y + 1, rowX / 2 - 1, nestH - 2, C("#eafaf1"));
        PlaceText(p, "右列，列内再套", FCjk, Px(8), Ink, ML + rowX / 2 + Mm(2), y + Mm(2) + Px(8) * 1.12);
        double barY = y + Mm(2) + Px(8) * 1.12 + Mm(1);
        double barW = (rowX / 2 - Mm(4)) / 3;
        FillRect(p, ML + rowX / 2 + Mm(2), barY, barW, Mm(4), C("#aed6f1"));
        FillRect(p, ML + rowX / 2 + Mm(2) + barW + 1, barY, barW * 2, Mm(4), C("#f5b7b1"));
        y += nestH;

        // 右列：虚线盒 + 居中紫块（交叉轴居中）
        double boxH = Math.Max(y - top, Mm(40));
        double bx = ML + leftW + gap;
        StrokeRect(p, bx, top, rightW, boxH, C("#8e44ad"), 0.75, DashFor(0.75, "dashed"));
        FillRect(p, bx, top, rightW, boxH, Ca("#f4ecf7", 1));
        StrokeRect(p, bx, top, rightW, boxH, C("#8e44ad"), 0.75, DashFor(0.75, "dashed"));
        double pbw = rightW * 0.6, pbh = Mm(14);
        double pbx = bx + (rightW - pbw) / 2, pby = top + (boxH - pbh) / 2;
        FillRect(p, pbx, pby, pbw, pbh, C("#8e44ad"));
        PlaceText(p, "交叉轴居中", FCjk, Px(9), White, pbx + (pbw - Measure(FCjk, Px(9), 0, "交叉轴居中")) / 2, pby + pbh / 2 + Px(9) * 0.4);
        return top + Math.Max(boxH, y - top) + Mm(1);
    }

    // ---------- 3.5 表格 ----------
    static double BuildDataTable(Ctx p, double y)
    {
        double[] pct = { 0.12, 0.40, 0.20, 0.28 };
        var xs = new double[5]; xs[0] = ML;
        for (int i = 0; i < 4; i++) xs[i + 1] = xs[i] + CW * pct[i];
        double fs = Px(8.5);
        double rowH = fs * 1.12 + Mm(2);
        Color lineC = C("#d5d8dc"), hdrLine = C("#5d6d7e");

        // 表头
        FillRect(p, ML, y, CW, rowH, C("#34495e"));
        string[] hdr = { "编号 Code", "名称 Name", "数量 Qty", "金额 Amount (¥)" };
        for (int i = 0; i < 4; i++)
            PlaceText(p, hdr[i], AutoFont(hdr[i]), fs, White,
                i >= 2 ? xs[i + 1] - Mm(2) - Measure(AutoFont(hdr[i]), fs, 0, hdr[i]) : xs[i] + Mm(2),
                y + rowH / 2 + fs * 0.4);
        y += rowH;
        string[][] rows =
        {
            new[] { "A-001", "工业级渲染管线 Industrial pipeline", "12", "1,280.00" },
            new[] { "A-002", "矢量字体子集 Vector subset", "3", "96.50" },
            new[] { "B-014", "分页引擎 Pagination engine", "1", "2,400.00" },
            new[] { "B-015", "阴影合成器 Shadow compositor", "8", "640.00" },
            new[] { "C-101", "渐变缓存 Gradient cache", "24", "1,152.00" },
            new[] { "C-102", "Alpha 通道混合 Alpha blending", "6", "306.00" },
            new[] { "D-200", "CJK 换行分析 Line-break CJK", "2", "780.00" },
        };
        double tblTop = y - rowH;
        for (int r = 0; r < rows.Length; r++)
        {
            if (r % 2 == 1) FillRect(p, ML, y, CW, rowH, C("#f8f9f9"));
            for (int i = 0; i < 4; i++)
            {
                string t = rows[r][i];
                double tx = i >= 2 ? xs[i + 1] - Mm(2) - Measure(AutoFont(t), fs, 0, t) : xs[i] + Mm(2);
                PlaceText(p, t, AutoFont(t), fs, Ink, tx, y + rowH / 2 + fs * 0.4, bold: i == 0);
            }
            y += rowH;
        }
        // 合计行
        FillRect(p, ML, y, CW, rowH, C("#eaeded"));
        PlaceText(p, "合计 Total (7 行明细)", FCjk, fs, Ink, xs[0] + Mm(2), y + rowH / 2 + fs * 0.4, bold: true);
        PlaceText(p, "56", FLatin, fs, Ink, xs[3] - Mm(2) - Measure(FLatin, fs, 0, "56"), y + rowH / 2 + fs * 0.4, bold: true);
        PlaceText(p, "¥ 6,654.50", FCjk, fs, Ink, xs[4] - Mm(2) - Measure(FCjk, fs, 0, "¥ 6,654.50"), y + rowH / 2 + fs * 0.4, bold: true);
        y += rowH;
        // 网格线
        for (int r = 0; r <= rows.Length + 2; r++)
        {
            double ly2 = tblTop + r * rowH;
            LineSeg(p, ML, ly2, ML + CW, ly2, r == 0 ? hdrLine : lineC, 0.75);
        }
        for (int i = 1; i < 4; i++)
            LineSeg(p, xs[i], tblTop, xs[i], y, lineC, 0.75);
        LineSeg(p, ML, y, ML + CW, y, lineC, 0.75);
        return y + Mm(2);
    }
}
