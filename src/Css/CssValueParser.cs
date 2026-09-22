using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace PaperSharp.Css;

/// <summary>
/// CSS 值解析工具。AngleSharp.Css 返回的都是字符串（如 "12px" / "rgb(0, 0, 0)" /
/// "0.5em"），这里统一转成我们需要的 float / SKColor 等。
/// </summary>
public static class CssValueParser
{
    static readonly Regex ReHex = new("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled);
    static readonly Regex ReRgb = new(@"^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*(\d*\.?\d+)\s*)?\)$", RegexOptions.Compiled);

    /// <summary>px / pt / em / rem / % → 像素。% 这里只返回数值比例（layout 层自己乘可用宽度）。</summary>
    public static float ToPixels(string value, float parentFontSize, float rootFontSize = 16f)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        value = value.Trim();
        if (value == "0" || value == "auto" || value == "normal") return 0;

        if (value.EndsWith("px")) return ParseFloat(value[..^2]);
        if (value.EndsWith("pt")) return ParseFloat(value[..^2]) * 96f / 72f;
        if (value.EndsWith("rem")) return ParseFloat(value[..^3]) * rootFontSize;
        if (value.EndsWith("em")) return ParseFloat(value[..^2]) * parentFontSize;
        if (value.EndsWith("%")) return -1f; // 百分比不在这里折算（需包含块尺寸），由 ParseSize 走 CssSize
        return ParseFloat(value);
    }

    /// <summary>解析 width/height/flex-basis/偏移：auto→Auto；%→Percent；px/pt/em/rem→Absolute（含负值，供 top/left 偏移）。</summary>
    public static CssSize ParseSize(string value, float fontSize)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0 || value == "auto" || value == "none") return CssSize.Auto;
        if (value.EndsWith("%"))
        {
            var p = ParseFloat(value[..^1]);
            return value[..^1].Trim().Length > 0 ? CssSize.Pct(p) : CssSize.Auto;
        }
        bool isLen = value.EndsWith("px") || value.EndsWith("pt") || value.EndsWith("em") || value.EndsWith("rem");
        float px = ToPixels(value, fontSize);
        if (px > 0) return CssSize.Abs(px);
        if (px < 0 && isLen) return CssSize.Abs(px);   // 负偏移（top:-10px 等）合法
        if (value == "0" || value == "0px" || value == "0pt" || value == "0em") return CssSize.Abs(0);
        return CssSize.Auto;
    }

    public static SKColor? ParseColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value == "transparent") return new SKColor(0, 0, 0, 0);

        var hex = ReHex.Match(value);
        if (hex.Success)
        {
            var s = hex.Groups[1].Value;
            if (s.Length == 3)
                s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
            byte r = Convert.ToByte(s.Substring(0, 2), 16);
            byte g = Convert.ToByte(s.Substring(2, 2), 16);
            byte b = Convert.ToByte(s.Substring(4, 2), 16);
            return new SKColor(r, g, b);
        }

        var rgb = ReRgb.Match(value);
        if (rgb.Success)
        {
            byte r = byte.Parse(rgb.Groups[1].Value, CultureInfo.InvariantCulture);
            byte g = byte.Parse(rgb.Groups[2].Value, CultureInfo.InvariantCulture);
            byte b = byte.Parse(rgb.Groups[3].Value, CultureInfo.InvariantCulture);
            byte a = 255;
            if (rgb.Groups[4].Success)
                a = (byte)Math.Round(float.Parse(rgb.Groups[4].Value, CultureInfo.InvariantCulture) * 255);
            return new SKColor(r, g, b, a);
        }

        // 少数常见命名色
        var named = TryParseNamedColor(value);
        return named;
    }

    static SKColor? TryParseNamedColor(string name) => name.ToLowerInvariant() switch
    {
        "black" => SKColors.Black,
        "white" => SKColors.White,
        "red" => SKColors.Red,
        "green" => new SKColor(0, 128, 0),
        "blue" => SKColors.Blue,
        "gray" or "grey" => SKColors.Gray,
        "lightgray" or "lightgrey" => SKColors.LightGray,
        "darkgray" or "darkgrey" => SKColors.DarkGray,
        "yellow" => SKColors.Yellow,
        "orange" => SKColors.Orange,
        "navy" => SKColors.Navy,
        "silver" => SKColors.Silver,
        _ => null,
    };

    public static float ParseLineHeight(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "normal") return 1.4f;
        value = value.Trim();
        if (value.EndsWith("%"))
        {
            var p = ParseFloat(value[..^1]);
            return p > 0 ? p / 100f : 1.4f;
        }
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor))
            return factor > 0 ? factor : 1.4f;
        // 带单位（px/em）的行高这里按 1.4 兜底（demo 不做绝对行高单位换算）
        return 1.4f;
    }

    public static CssTextAlign ParseTextAlign(string value) => value?.Trim().ToLowerInvariant() switch
    {
        "center" => CssTextAlign.Center,
        "right" or "end" => CssTextAlign.Right,
        _ => CssTextAlign.Left,
    };

    public static string ParseFirstFontFamily(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "sans-serif";
        var first = value.Split(',')[0].Trim();
        return first.Trim('\'', '"');
    }

    public static int ParseFontWeight(string value, int fallback = 400)
    {
        if (int.TryParse(value, out var w)) return w;
        return value?.Trim().ToLowerInvariant() switch
        {
            "bold" => 700,
            "bolder" => 800,
            "lighter" => 300,
            "normal" => 400,
            _ => fallback,
        };
    }

    /// <summary>flex-grow / flex-shrink：无单位数字，非法时回退默认。</summary>
    public static float ParseFlexNumber(string value, float fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>归一化关键字（小写、去空格），空则回退。</summary>
    public static string ParseKeyword(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Trim().ToLowerInvariant();
    }

    // ── Tier-2 解析：opacity / border-radius / box-shadow / gradient ──────────────

    /// <summary>opacity：0..1 数字或百分比；非法回退 1。</summary>
    public static float ParseOpacity(string value)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0) return 1f;
        if (value.EndsWith("%"))
        {
            var p = ParseFloat(value[..^1]);
            return Clamp01(p / 100f);
        }
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return Clamp01(v);
        return 1f;
    }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>border-radius 简写：1-4 值（圆角为正方形半径，横纵同值）。返回 tl,tr,br,bl。</summary>
    public static (float tl, float tr, float br, float bl) ParseBorderRadius(string value, float fontSize)
    {
        if (string.IsNullOrWhiteSpace(value)) return (0, 0, 0, 0);
        // 去掉 " / ..." 的纵向半径部分（demo 用正方形圆角）
        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (0, 0, 0, 0);
        float V(int i) => Math.Max(0, ToPixels(parts[i < parts.Length ? i : parts.Length - 1], fontSize));
        float a = V(0);
        if (parts.Length == 1) return (a, a, a, a);
        float b = V(1);
        if (parts.Length == 2) return (a, b, a, b);
        float c = V(2);
        if (parts.Length == 3) return (a, b, c, b);
        return (a, b, V(3), c);
    }

    /// <summary>box-shadow：取第一条阴影 "offsetX offsetY [blur [spread]] color"。none/非法→null。</summary>
    public static BoxShadow? ParseBoxShadow(string value, float fontSize)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        // 多条阴影取第一条（逗号分隔，但颜色里 rgb() 也含逗号——用顶层逗号切）
        var first = SplitTopLevel(value, ',')[0].Trim();
        var toks = SplitTopLevel(first, ' ');
        var nums = new List<float>();
        var colorToks = new List<string>();
        foreach (var t in toks)
        {
            if (nums.Count < 4 && colorToks.Count == 0 && IsLengthToken(t))
                nums.Add(ToPixels(t, fontSize));
            else
                colorToks.Add(t);
        }
        if (nums.Count < 2) return null;
        var color = ParseColor(string.Join(" ", colorToks)) ?? new SKColor(0, 0, 0, 128);
        return new BoxShadow
        {
            OffsetX = nums[0],
            OffsetY = nums[1],
            Blur = nums.Count > 2 ? Math.Max(0, nums[2]) : 0,
            Spread = nums.Count > 3 ? nums[3] : 0,
            Color = color,
        };
    }

    static bool IsLengthToken(string t)
    {
        t = t.Trim();
        if (t.Length == 0) return false;
        if (t == "0") return true;
        var c = t[0];
        return char.IsDigit(c) || c == '-' || c == '+' || c == '.';
    }

    /// <summary>解析 background(-image) 里的 linear-gradient(...) / radial-gradient(...)。非渐变→null。</summary>
    public static GradientSpec? ParseGradient(string value)
    {
        value = value?.Trim() ?? "";
        bool radial;
        int open;
        if (value.StartsWith("linear-gradient(", StringComparison.OrdinalIgnoreCase)) { radial = false; open = "linear-gradient(".Length - 1; }
        else if (value.StartsWith("radial-gradient(", StringComparison.OrdinalIgnoreCase)) { radial = true; open = "radial-gradient(".Length - 1; }
        else return null;
        int close = value.LastIndexOf(')');
        if (close <= open) return null;
        var inner = value[(open + 1)..close];
        var parts = SplitTopLevel(inner, ',');
        if (parts.Count < 2) return null;

        var spec = new GradientSpec { Radial = radial, AngleDeg = 180 };
        int startIdx = 0;
        if (!radial)
        {
            // 第一段可能是方向（to right / 90deg），也可能是第一个颜色
            var dir = parts[0].Trim();
            float? angle = ParseGradientDirection(dir);
            if (angle != null) { spec.AngleDeg = angle.Value; startIdx = 1; }
        }
        else
        {
            // radial 第一段可能是 shape（circle/ellipse ...），非颜色则跳过
            var head = parts[0].Trim();
            if (head.StartsWith("circle", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("ellipse", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
                startIdx = 1;
        }

        var colors = new List<SKColor>();
        var stops = new List<float>();
        bool anyStop = false;
        for (int i = startIdx; i < parts.Count; i++)
        {
            var stop = parts[i].Trim();
            float? pos = null;
            // 末尾可能是 " 50%" 位置
            var sp = SplitTopLevel(stop, ' ');
            if (sp.Count >= 2 && sp[^1].EndsWith("%"))
            {
                pos = ParseFloat(sp[^1][..^1]) / 100f;
                stop = string.Join(" ", sp.Take(sp.Count - 1));
                anyStop = true;
            }
            var col = ParseColor(stop.Trim());
            if (col == null) continue;
            colors.Add(col.Value);
            stops.Add(pos ?? -1);
        }
        if (colors.Count < 2) return null;
        spec.Colors = colors.ToArray();
        if (anyStop)
        {
            // 缺位置的按线性内插填充
            var arr = stops.ToArray();
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] < 0) arr[i] = (float)i / (arr.Length - 1);
            spec.Stops = arr;
        }
        return spec;
    }

    static float? ParseGradientDirection(string dir)
    {
        dir = dir.Trim().ToLowerInvariant();
        if (dir.EndsWith("deg")) return ParseFloat(dir[..^3]);
        if (dir.StartsWith("to "))
        {
            return dir switch
            {
                "to top" => 0, "to right" => 90, "to bottom" => 180, "to left" => 270,
                "to top right" or "to right top" => 45,
                "to bottom right" or "to right bottom" => 135,
                "to bottom left" or "to left bottom" => 225,
                "to top left" or "to left top" => 315,
                _ => null,
            };
        }
        return null; // 不是方向 → 它就是第一个颜色
    }

    /// <summary>按分隔符切分，但忽略括号（如 rgb(...)）内的分隔符。</summary>
    public static List<string> SplitTopLevel(string s, char sep)
    {
        var result = new List<string>();
        int depth = 0; var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (c == sep && depth == 0) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    static float ParseFloat(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
