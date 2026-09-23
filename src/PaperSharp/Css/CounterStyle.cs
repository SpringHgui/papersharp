using System.Text;

namespace PaperSharp.Css;

/// <summary>
/// CSS counter-style 数字格式化（共享）：@page 边距盒的 counter(page/pages) 与伪元素
/// counter()/counters() 都用它，避免两处各抄一份。
/// 覆盖：decimal / decimal-leading-zero / lower-·upper-alpha / lower-·upper-roman /
/// cjk-ideographic / trad-chinese-informal；未知样式回退十进制。
/// </summary>
public static class CounterStyle
{
    public static string Format(int num, string style)
    {
        switch (style)
        {
            case "decimal": return num.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "decimal-leading-zero": return num.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            case "lower-alpha": case "lower-latin": return ToAlpha(num, lower: true);
            case "upper-alpha": case "upper-latin": return ToAlpha(num, lower: false);
            case "lower-roman": return ToRoman(num).ToLowerInvariant();
            case "upper-roman": return ToRoman(num);
            case "cjk-ideographic": return ToCjkIdeographic(num);
            case "trad-chinese-informal": return ToTradChineseInformal(num);
            default: return num.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>a..z, aa..zz, …（1→a, 26→z, 27→aa）。CSS 的 lower-alpha 无零。</summary>
    static string ToAlpha(int num, bool lower)
    {
        if (num <= 0) return num.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var chars = new List<char>();
        while (num > 0)
        {
            num--;                       // 转成 0-based
            chars.Add((char)('a' + num % 26));
            num /= 26;
        }
        chars.Reverse();
        var s = new string(chars.ToArray());
        return lower ? s : s.ToUpperInvariant();
    }

    static readonly (int v, string s)[] Romans =
    {
        (1000,"M"),(900,"CM"),(500,"D"),(400,"CD"),(100,"C"),(90,"XC"),
        (50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I"),
    };

    static string ToRoman(int num)
    {
        if (num <= 0) return num.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        foreach (var (v, s) in Romans)
            while (num >= v) { sb.Append(s); num -= v; }
        return sb.ToString();
    }

    /// <summary>中文数字（简体）：一二三四五六七八九十百千万</summary>
    public static string ToCjkIdeographic(int num)
    {
        if (num == 0) return "零";
        if (num < 0) return "-" + ToCjkIdeographic(-num);

        string[] digits = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        string[] units = { "", "十", "百", "千", "万", "十", "百", "千", "亿" };

        var sb = new StringBuilder();
        int unitIdx = 0;
        bool lastWasZero = false;

        while (num > 0)
        {
            int digit = num % 10;
            num /= 10;

            if (digit == 0)
            {
                if (!lastWasZero && sb.Length > 0) sb.Insert(0, "零");
                lastWasZero = true;
            }
            else
            {
                if (unitIdx == 1 && digit == 1 && num == 0)
                    sb.Insert(0, units[unitIdx]); // 10-19: 十、十一...十九（不带"一"）
                else
                    sb.Insert(0, digits[digit] + units[unitIdx]);
                lastWasZero = false;
            }
            unitIdx++;
        }
        return sb.ToString();
    }

    /// <summary>传统中文数字（大写）：壹贰叁肆伍陆柒捌玖拾佰仟</summary>
    public static string ToTradChineseInformal(int num)
    {
        if (num == 0) return "零";
        if (num < 0) return "-" + ToTradChineseInformal(-num);

        string[] digits = { "零", "壹", "贰", "叁", "肆", "伍", "陆", "柒", "捌", "玖" };
        string[] units = { "", "拾", "佰", "仟", "万", "拾", "佰", "仟", "亿" };

        var sb = new StringBuilder();
        int unitIdx = 0;
        bool lastWasZero = false;

        while (num > 0)
        {
            int digit = num % 10;
            num /= 10;

            if (digit == 0)
            {
                if (!lastWasZero && sb.Length > 0) sb.Insert(0, "零");
                lastWasZero = true;
            }
            else
            {
                if (unitIdx == 1 && digit == 1 && num == 0)
                    sb.Insert(0, units[unitIdx]);
                else
                    sb.Insert(0, digits[digit] + units[unitIdx]);
                lastWasZero = false;
            }
            unitIdx++;
        }
        return sb.ToString();
    }
}
