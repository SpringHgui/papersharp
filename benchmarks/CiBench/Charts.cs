using System.Globalization;
using System.Net;
using System.Text;

namespace CiBench;

/// <summary>ValueColors 非空时按类别逐柱着色（用于「每家一根柱」的单系列图），图例改用类别名。</summary>
public sealed record ChartSeries(string Name, string Color, double?[] Values, string[]? ValueColors = null);

/// <summary>零依赖的内嵌 SVG 分组柱状图。不引 CDN，CI artifact 离线打开也能看。</summary>
public static class Charts
{
    const int W = 760;
    const int H = 340;
    const int X0 = 72;
    const int X1 = W - 22;
    const int Y0 = 52;
    const int Y1 = H - 76;

    public static string GroupedBars(string title, string unit, string[] categories, IReadOnlyList<ChartSeries> series)
    {
        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" width=\"100%\" role=\"img\" " +
                  "xmlns=\"http://www.w3.org/2000/svg\" class=\"chart\">");
        sb.Append($"<title>{E(title)}</title>");
        sb.Append($"<text x=\"{X0}\" y=\"27\" class=\"chart-title\">{E(title)}</text>");
        sb.Append($"<text x=\"{X1}\" y=\"27\" class=\"chart-unit\" text-anchor=\"end\">{E(unit)}</text>");

        double max = 0;
        foreach (var s in series)
            foreach (var v in s.Values)
                if (v is not null && !double.IsNaN(v.Value) && v.Value > max) max = v.Value;
        double yMax = NiceMax(max);

        for (int g = 0; g <= 4; g++)
        {
            double val = yMax * g / 4.0;
            double y = Y1 - (Y1 - Y0) * g / 4.0;
            sb.Append($"<line x1=\"{X0}\" y1=\"{F(y)}\" x2=\"{X1}\" y2=\"{F(y)}\" class=\"grid\"/>");
            sb.Append($"<text x=\"{X0 - 10}\" y=\"{F(y + 4)}\" class=\"axis\" text-anchor=\"end\">{E(Num(val))}</text>");
        }
        sb.Append($"<line x1=\"{X0}\" y1=\"{Y1}\" x2=\"{X1}\" y2=\"{Y1}\" class=\"axis-line\"/>");

        double groupW = (X1 - X0) / (double)Math.Max(1, categories.Length);
        int n = Math.Max(1, series.Count);
        double barW = n == 1 ? Math.Min(110, groupW * 0.5) : Math.Min(54, groupW / (n + 0.9));
        double gap = barW * 0.16;
        double totalW = n * barW + (n - 1) * gap;

        for (int c = 0; c < categories.Length; c++)
        {
            double groupCenter = X0 + groupW * (c + 0.5);
            double start = groupCenter - totalW / 2.0;
            for (int s = 0; s < series.Count; s++)
            {
                double x = start + s * (barW + gap);
                var v = c < series[s].Values.Length ? series[s].Values[c] : null;
                if (v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value))
                {
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(Y1 - 16)}\" width=\"{F(barW)}\" height=\"16\" " +
                              "rx=\"3\" class=\"na-bar\"/>");
                    sb.Append($"<text x=\"{F(x + barW / 2)}\" y=\"{F(Y1 - 22)}\" " +
                              "class=\"na-label\" text-anchor=\"middle\">N/A</text>");
                    continue;
                }
                double h = Math.Max(2, Math.Min(Y1 - Y0, v.Value / yMax * (Y1 - Y0)));
                double y = Y1 - h;
                var vc = series[s].ValueColors;
                string color = vc is not null && c < vc.Length ? vc[c] : series[s].Color;
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(barW)}\" height=\"{F(h)}\" " +
                          $"rx=\"3\" fill=\"{E(color)}\"/>");
                sb.Append($"<text x=\"{F(x + barW / 2)}\" y=\"{F(y - 7)}\" " +
                          $"class=\"value\" text-anchor=\"middle\">{E(Num(v.Value))}</text>");
            }
            sb.Append($"<text x=\"{F(groupCenter)}\" y=\"{Y1 + 22}\" class=\"cat\" text-anchor=\"middle\">" +
                      $"{E(categories[c])}</text>");
        }

        var legendItems = new List<(string Name, string Color)>();
        if (series.Count == 1 && series[0].ValueColors is { } vc0)
        {
            for (int c = 0; c < categories.Length; c++)
                legendItems.Add((categories[c], c < vc0.Length ? vc0[c] : series[0].Color));
        }
        else
        {
            foreach (var s in series) legendItems.Add((s.Name, s.Color));
        }

        double legendW = legendItems.Sum(i => 18 + i.Name.Length * 7 + 20);
        double lx = X0 + ((X1 - X0) - legendW) / 2.0;
        if (lx < X0) lx = X0;
        foreach (var item in legendItems)
        {
            sb.Append($"<rect x=\"{F(lx)}\" y=\"{H - 32}\" width=\"12\" height=\"12\" rx=\"2\" fill=\"{E(item.Color)}\"/>");
            sb.Append($"<text x=\"{F(lx + 18)}\" y=\"{H - 22}\" class=\"legend\">{E(item.Name)}</text>");
            lx += 18 + item.Name.Length * 7 + 20;
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    static double NiceMax(double max)
    {
        if (max <= 0 || double.IsNaN(max)) return 1;
        double exp = Math.Floor(Math.Log10(max));
        double f = max / Math.Pow(10, exp);
        double[] steps = [1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10];
        double s = steps.FirstOrDefault(x => x >= f * 1.12, 10);
        return s * Math.Pow(10, exp);
    }

    static string Num(double v) => v switch
    {
        >= 1000 => v.ToString("0", CultureInfo.InvariantCulture),
        >= 100 => v.ToString("0", CultureInfo.InvariantCulture),
        >= 10 => v.ToString("0.#", CultureInfo.InvariantCulture),
        _ => v.ToString("0.##", CultureInfo.InvariantCulture),
    };

    static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    static string E(string s) => WebUtility.HtmlEncode(s);
}
