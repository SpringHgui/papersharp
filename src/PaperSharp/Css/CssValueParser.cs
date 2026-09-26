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

    // CSS 绝对长度（1in=96px 基准）。AngleSharp 的 computed 串会替我们折成 px，但作者原文
    // （AuthorEdge 简写、calc 内部）不折——曾经 `.page{padding:12mm 14mm 16mm}` 整条静默变 0。
    internal const float PxPerMm = 96f / 25.4f;
    internal const float PxPerCm = 96f / 2.54f;

    /// <summary>绝对长度单位 → px 系数；不是这些单位返回 null。</summary>
    internal static float? AbsoluteUnitFactor(string unit) => unit switch
    {
        "px" => 1f,
        "pt" => 96f / 72f,
        "mm" => PxPerMm,
        "cm" => PxPerCm,
        "in" => 96f,
        "pc" => 16f,
        "q" => PxPerCm / 4f,
        _ => null,
    };

    /// <summary>末尾若是绝对长度单位，返回 (数值, 系数)。</summary>
    static bool TryAbsoluteLength(ReadOnlySpan<char> v, out float num, out float factor)
    {
        num = 0; factor = 1f;
        foreach (var u in AbsoluteUnits)
        {
            if (v.EndsWith(u, StringComparison.Ordinal))
            {
                num = ParseFloat(v[..^u.Length]);
                factor = AbsoluteUnitFactor(u)!.Value;
                return true;
            }
        }
        return false;
    }

    // 顺序重要：先长的单位，避免 "mm" 被 "m" 之类的短前缀截走。
    static readonly string[] AbsoluteUnits = { "px", "pt", "mm", "cm", "in", "pc", "q" };

    /// <summary>px / pt / mm / cm / in / pc / q / em / rem / % → 像素。% 这里只返回数值比例（layout 层自己乘可用宽度）。</summary>
    public static float ToPixels(string value, float parentFontSize, float rootFontSize = 16f)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        // 全程走 span 切片（09-25 span 化）：单位剥离不再产生 value[..^2] 子串分配。
        // 所有关键字比较保持 Ordinal 大小写敏感——与原 `value == "auto"` 逐字一致。
        var v = value.AsSpan().Trim();
        if (v.Equals("0", StringComparison.Ordinal) || v.Equals("auto", StringComparison.Ordinal) || v.Equals("normal", StringComparison.Ordinal)) return 0;

        if (v.IndexOf("calc(", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var ev = EvalCalc(value, parentFontSize, rootFontSize);
            return ev.HasValue ? ev.Value.abs : 0;   // 无包含块基准，百分比项忽略
        }
        if (TryAbsoluteLength(v, out float num, out float factor)) return num * factor;
        if (v.EndsWith("rem", StringComparison.Ordinal)) return ParseFloat(v[..^3]) * rootFontSize;
        if (v.EndsWith("em", StringComparison.Ordinal)) return ParseFloat(v[..^2]) * parentFontSize;
        if (v.EndsWith("%", StringComparison.Ordinal)) return -1f; // 百分比不在这里折算（需包含块尺寸），由 ParseSize 走 CssSize
        return ParseFloat(v);
    }

    /// <summary>解析 width/height/flex-basis/偏移：auto→Auto；%→Percent；px/pt/em/rem→Absolute（含负值，供 top/left 偏移）；calc()→Calc/Absolute。</summary>
    public static CssSize ParseSize(string value, float fontSize)
    {
        var v = (value ?? "").AsSpan().Trim();   // span 化：见 ToPixels 同款注释
        if (v.Length == 0 || v.Equals("auto", StringComparison.Ordinal) || v.Equals("none", StringComparison.Ordinal)) return CssSize.Auto;
        if (v.IndexOf("calc(", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var ev = EvalCalc(value, fontSize);
            if (!ev.HasValue) return CssSize.Auto;
            return ev.Value.pct != 0 ? CssSize.Calc(ev.Value.pct, ev.Value.abs) : CssSize.Abs(ev.Value.abs);
        }
        if (v.EndsWith("%", StringComparison.Ordinal))
        {
            var num = v[..^1];                    // 与原实现逐字一致：数值取未 trim 切片（"50 %" → ParseFloat 失败 = 0）
            return num.Trim().Length > 0 ? CssSize.Pct(ParseFloat(num)) : CssSize.Auto;
        }
        bool isLen = IsLength(v);
        float px = ToPixels(value, fontSize);
        if (px > 0) return CssSize.Abs(px);
        if (px < 0 && isLen) return CssSize.Abs(px);   // 负偏移（top:-10px 等）合法
        if (v.Equals("0", StringComparison.Ordinal) || (isLen && ParseFloat(v.TrimEnd(LengthUnitChars)) == 0)) return CssSize.Abs(0);
        return CssSize.Auto;
    }

    static readonly char[] LengthUnitChars = { 'p', 'x', 't', 'm', 'c', 'i', 'n', 'q', 'e', 'r' };

    /// <summary>以长度单位结尾（px/pt/mm/cm/in/pc/q/em/rem）——负值与 0 值都要靠它判定"这是个长度"。</summary>
    static bool IsLength(ReadOnlySpan<char> v)
    {
        foreach (var u in AbsoluteUnits)
            if (v.EndsWith(u, StringComparison.Ordinal)) return true;
        return v.EndsWith("em", StringComparison.Ordinal) || v.EndsWith("rem", StringComparison.Ordinal);
    }

    /// <summary>
    /// 求值 calc()：四则 + px/pt/em/rem/% 混合，嵌套括号。CSS 规定乘除只允许一侧为无量纲数字。
    /// 结果折算成线性式 pct% + abs（em/rem/px 归入 abs，% 归入 pct）。解析失败返回 null。
    /// </summary>
    public static (float pct, float abs)? EvalCalc(string value, float fontSize, float rootFontSize = 16f)
    {
        value = value?.Trim() ?? "";
        int open = value.IndexOf("calc(", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        try
        {
            // 把嵌套 calc( 拉平成普通括号，再取第一对括号内的表达式
            var s = Regex.Replace(value, "calc\\s*\\(", "(", RegexOptions.IgnoreCase);
            open = s.IndexOf('(');
            int depth = 0, close = -1;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')' && --depth == 0) { close = i; break; }
            }
            if (close < 0) return null;
            var p = new CalcParser(s[(open + 1)..close], fontSize, rootFontSize);
            var (pct, abs) = p.Expr();
            p.Ws();
            if (p.Pos != p.S.Length) return null;   // 有残留 token → 非法
            return (pct, abs);
        }
        catch { return null; }
    }

    sealed class CalcParser
    {
        public readonly string S;
        public int Pos;
        readonly float _em, _rem;
        public CalcParser(string s, float em, float rem) { S = s; _em = em; _rem = rem; }

        // expr := term (('+'|'-') term)*
        public (float pct, float abs) Expr()
        {
            var acc = Term();
            while (true)
            {
                Ws();
                if (Pos >= S.Length) return acc;
                char op = S[Pos];
                if (op != '+' && op != '-') return acc;
                Pos++;
                var (p, a) = Term();
                acc = (acc.pct + (op == '+' ? p : -p), acc.abs + (op == '+' ? a : -a));
            }
        }

        // term := operand (('*'|'/') number)?
        (float pct, float abs) Term()
        {
            var v = Operand();
            Ws();
            if (Pos < S.Length && (S[Pos] == '*' || S[Pos] == '/'))
            {
                char op = S[Pos]; Pos++;
                Ws();
                float n = Number();
                if (op == '/') { if (n == 0) throw new FormatException(); n = 1 / n; }
                v = (v.pct * n, v.abs * n);
            }
            return v;
        }

        // operand := '(' expr ')' | length|number
        (float pct, float abs) Operand()
        {
            Ws();
            if (Pos < S.Length && S[Pos] == '(')
            {
                Pos++;
                var v = Expr();
                Ws();
                if (Pos >= S.Length || S[Pos] != ')') throw new FormatException();
                Pos++;
                return v;
            }
            var (num, unit) = NumUnit();
            if (unit.Length == 0) return (0, num);
            if (AbsoluteUnitFactor(unit) is float absFactor) return (0, num * absFactor);
            return unit switch
            {
                "em" => (0, num * _em),
                "rem" => (0, num * _rem),
                "%" => (num, 0),
                _ => throw new FormatException(),
            };
        }

        public void Ws() { while (Pos < S.Length && (char.IsWhiteSpace(S[Pos]) || S[Pos] == '\t')) Pos++; }

        float Number()
        {
            int start = Pos;
            while (Pos < S.Length && (char.IsDigit(S[Pos]) || S[Pos] == '.' || S[Pos] == '-' || S[Pos] == '+')) Pos++;
            // e/E 只有紧跟数字时才是科学计数法的指数，否则它属于单位 em：
            // "calc(0.5em + 8px)" 曾被切成数字 "0.5e" + 单位 "m"，整条 calc 抛错折成 0（m4 的 .c 实测少 30px）。
            if (Pos < S.Length && (S[Pos] == 'e' || S[Pos] == 'E') && ExpDigits(Pos + 1))
                for (Pos++; Pos < S.Length && (char.IsDigit(S[Pos]) || S[Pos] == '+' || S[Pos] == '-'); Pos++) { }
            if (start == Pos) throw new FormatException();
            return float.Parse(S[start..Pos], NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>e/E 之后是否确实是指数数字（可带符号）。</summary>
        bool ExpDigits(int i) =>
            i < S.Length && (char.IsDigit(S[i]) || ((S[i] == '+' || S[i] == '-') && i + 1 < S.Length && char.IsDigit(S[i + 1])));

        (float num, string unit) NumUnit()
        {
            float n = Number();
            Ws();
            int start = Pos;
            while (Pos < S.Length && char.IsLetter(S[Pos])) Pos++;
            string unit = S[start..Pos];
            if (unit.Length == 0 && Pos < S.Length && S[Pos] == '%') { unit = "%"; Pos++; }
            return (n, unit.ToLowerInvariant());
        }
    }


    public static SKColor? ParseColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value == "transparent") return new SKColor(0, 0, 0, 0);

        // 两条正则都是 ^ 锚定的：前导字符不符就必然失配。ExtractBackgroundColor 对每个 depth-0
        // token（"no-repeat"/"url(...)" 之类占多数）都调一次，这层守卫省掉几乎全部 Match 分配。
        var hex = value[0] == '#' ? ReHex.Match(value) : null;
        if (hex is { Success: true })
        {
            var s = hex.Groups[1].Value;
            if (s.Length == 3)
                s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
            byte r = Convert.ToByte(s.Substring(0, 2), 16);
            byte g = Convert.ToByte(s.Substring(2, 2), 16);
            byte b = Convert.ToByte(s.Substring(4, 2), 16);
            return new SKColor(r, g, b);
        }

        Match? rgb = null;
        if (value.Length >= 4 && (value[0] is 'r' or 'R') && (value[1] is 'g' or 'G') && (value[2] is 'b' or 'B'))
            rgb = ReRgb.Match(value);
        if (rgb is { Success: true })
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

    // 命名色表很小：逐条 IgnoreCase 等值比较即可，不再为整串分配 ToLowerInvariant 结果。
    static SKColor? TryParseNamedColor(string name)
    {
        var n = name.AsSpan();
        if (n.Equals("black", StringComparison.OrdinalIgnoreCase)) return SKColors.Black;
        if (n.Equals("white", StringComparison.OrdinalIgnoreCase)) return SKColors.White;
        if (n.Equals("red", StringComparison.OrdinalIgnoreCase)) return SKColors.Red;
        if (n.Equals("green", StringComparison.OrdinalIgnoreCase)) return new SKColor(0, 128, 0);
        if (n.Equals("blue", StringComparison.OrdinalIgnoreCase)) return SKColors.Blue;
        if (n.Equals("gray", StringComparison.OrdinalIgnoreCase) || n.Equals("grey", StringComparison.OrdinalIgnoreCase)) return SKColors.Gray;
        if (n.Equals("lightgray", StringComparison.OrdinalIgnoreCase) || n.Equals("lightgrey", StringComparison.OrdinalIgnoreCase)) return SKColors.LightGray;
        if (n.Equals("darkgray", StringComparison.OrdinalIgnoreCase) || n.Equals("darkgrey", StringComparison.OrdinalIgnoreCase)) return SKColors.DarkGray;
        if (n.Equals("yellow", StringComparison.OrdinalIgnoreCase)) return SKColors.Yellow;
        if (n.Equals("orange", StringComparison.OrdinalIgnoreCase)) return SKColors.Orange;
        if (n.Equals("navy", StringComparison.OrdinalIgnoreCase)) return SKColors.Navy;
        if (n.Equals("silver", StringComparison.OrdinalIgnoreCase)) return SKColors.Silver;
        return null;
    }

    /// <summary>行高倍数。<c>normal</c>/未声明/带单位 → NaN（带单位的交给 <see cref="ParseLineHeightLength"/>；
    /// normal 由调用方按字体度量取行距，勿在此凑一个经验值）。</summary>
    public static float ParseLineHeight(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "normal") return float.NaN;
        var v = value.AsSpan().Trim();
        if (v.EndsWith("%", StringComparison.Ordinal))
        {
            var p = ParseFloat(v[..^1]);
            return p > 0 ? p / 100f : float.NaN;
        }
        if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor))
            return factor > 0 ? factor : float.NaN;
        return float.NaN;   // 带单位（px/em/…）：见 ParseLineHeightLength
    }

    /// <summary>
    /// 带单位的行高（<c>46px</c> / <c>1.5em</c> / <c>150%</c> 之外的长度与 calc()）折算成像素。
    /// 数字、百分比、<c>normal</c>、未声明一律 NaN——那三类要乘字号，走 <see cref="ParseLineHeight"/> 的倍数口径。
    /// em 按**本元素**字号折算（CSS 的 computed-value 口径），故继承给子元素时不再跟着子字号缩放。
    /// </summary>
    public static float ParseLineHeightLength(string value, float fontSize, float rootFontSize = 16f)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "normal") return float.NaN;
        var v = value.AsSpan().Trim();
        bool isLen = IsLength(v) || v.IndexOf("calc(", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isLen) return float.NaN;
        float px = ToPixels(value, fontSize, rootFontSize);
        return px > 0 ? px : float.NaN;
    }

    public static CssTextAlign ParseTextAlign(string value)
    {
        if (value is null) return CssTextAlign.Left;
        var v = value.AsSpan().Trim();
        if (v.Equals("center", StringComparison.OrdinalIgnoreCase)) return CssTextAlign.Center;
        if (v.Equals("right", StringComparison.OrdinalIgnoreCase) || v.Equals("end", StringComparison.OrdinalIgnoreCase)) return CssTextAlign.Right;
        return CssTextAlign.Left;
    }

    public static string ParseFirstFontFamily(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "sans-serif";
        var first = value.Split(',')[0].Trim();
        return first.Trim('\'', '"');
    }

    public static int ParseFontWeight(string value, int fallback = 400)
    {
        if (int.TryParse(value, out var w)) return w;
        if (value is null) return fallback;
        var v = value.AsSpan().Trim();
        if (v.Equals("bold", StringComparison.OrdinalIgnoreCase)) return 700;
        if (v.Equals("bolder", StringComparison.OrdinalIgnoreCase)) return 800;
        if (v.Equals("lighter", StringComparison.OrdinalIgnoreCase)) return 300;
        if (v.Equals("normal", StringComparison.OrdinalIgnoreCase)) return 400;
        return fallback;
    }

    /// <summary>flex-grow / flex-shrink：无单位数字，非法时回退默认。</summary>
    public static float ParseFlexNumber(string value, float fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return float.TryParse(value.AsSpan().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>
    /// flex 简写（CSS 2.2 flexbox §7.1.1）：
    ///   none → 0 0 auto ｜ auto → 1 1 auto ｜ 单个 &lt;number&gt; → n 1 <b>0%</b> ｜
    ///   单个 &lt;width&gt; → 1 1 width ｜ 其余按 grow / shrink / basis 位序，缺 shrink 补 1。
    /// 要命的是 `flex: 1` 的 basis 是 0%（不是 auto）：只读三个长写就等于把简写整条丢掉，
    /// 「等分剩余空间」会排成按内容宽（实测 flex 样例 448/362 vs 浏览器 404/404）。
    /// 返回 false = 不是合法简写，调用方保留长写值不动。
    /// </summary>
    public static bool TryParseFlexShorthand(string value, float fontSize, out float grow, out float shrink, out CssSize basis)
    {
        grow = 0f; shrink = 1f; basis = CssSize.Auto;
        var toks = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (toks.Length == 0) return false;
        if (toks.Length == 1 && toks[0] is "none") { shrink = 0f; return true; }
        if (toks.Length == 1 && toks[0] is "auto") { grow = 1f; return true; }
        if (toks.Length == 1 && toks[0] is "initial") return true;

        static bool Num(string t, out float v) => float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        // 裸数字出现在 basis 位（`flex: 1 1 0`）按长度 0 解；带单位/百分比/auto/calc 交回 ParseSize
        static CssSize BasisOf(string t, float em) => Num(t, out var z) ? CssSize.Abs(z) : ParseSize(t, em);

        if (!Num(toks[0], out var g))
        {
            grow = 1f; basis = BasisOf(toks[0], fontSize);       // `flex: 200px` / `flex: 40%`
            return toks.Length == 1;
        }
        grow = Math.Max(0, g);
        int i = 1;
        if (i < toks.Length && Num(toks[i], out var s)) { shrink = Math.Max(0, s); i++; }
        if (i < toks.Length) { basis = BasisOf(toks[i], fontSize); i++; }
        else if (toks.Length == 1) basis = CssSize.Pct(0);       // 单个 <number>：basis 归零
        return i == toks.Length;
    }

    /// <summary>
    /// positional 两值简写（place-items / place-self / gap）：拆成「交叉轴 主轴」两个关键字。
    /// 一个值时两轴同值；带第二个关键字修饰（如 "left right"、"baseline center"）不认，交回长写路径。
    /// </summary>
    public static bool TryParsePositional2(string value, out string cross, out string main)
    {
        cross = main = "";
        var toks = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (toks.Length == 0) return false;
        if (toks.Length == 1) { cross = main = toks[0].ToLowerInvariant(); return true; }
        if (toks.Length != 2) return false;
        cross = toks[0].ToLowerInvariant(); main = toks[1].ToLowerInvariant();
        return true;
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
        var v = (value ?? "").AsSpan().Trim();
        if (v.Length == 0) return 1f;
        if (v.EndsWith("%", StringComparison.Ordinal))
        {
            var p = ParseFloat(v[..^1]);
            return Clamp01(p / 100f);
        }
        if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return Clamp01(x);
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

    /// <summary>box-shadow 简写：逗号分层，每层 <c>inset? dx dy [blur [spread]] color</c>。none/非法→null。
    /// 层序即绘制序（CSS 里第一条画在最上层），inset 与实色 <c>0 0 0 2px</c> 环都是作者常用的招。
    /// text-shadow 复用本函数（<c>allowSpread=false</c>：它没有 spread，也不接受 inset）。</summary>
    public static List<BoxShadow>? ParseShadowLayers(string value, float fontSize, bool allowSpread = true)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        List<BoxShadow>? layers = null;
        foreach (var raw in SplitTopLevel(value, ','))
        {
            var one = ParseShadowLayer(raw, fontSize, allowSpread);
            if (one == null) continue;
            (layers ??= new List<BoxShadow>()).Add(one);
        }
        return layers;
    }

    /// <summary>单层阴影。长度值最多 4 个（dx dy blur spread），其余 token 合成颜色串。</summary>
    public static BoxShadow? ParseShadowLayer(string value, float fontSize, bool allowSpread = true)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        bool inset = false;
        var nums = new List<float>();
        var colorToks = new List<string>();
        int maxNums = allowSpread ? 4 : 3;
        foreach (var t in SplitTopLevel(value, ' '))
        {
            var tok = t.Trim();
            if (tok.Length == 0) continue;
            if (tok.Equals("inset", StringComparison.OrdinalIgnoreCase)) { inset = true; continue; }
            if (nums.Count < maxNums && colorToks.Count == 0 && IsLengthToken(tok))
            {
                nums.Add(ToPixels(tok, fontSize));
                continue;
            }
            colorToks.Add(tok);
        }
        if (nums.Count < 2) return null;
        return new BoxShadow
        {
            OffsetX = nums[0],
            OffsetY = nums[1],
            Blur = nums.Count > 2 ? Math.Max(0, nums[2]) : 0,
            Spread = nums.Count > 3 && allowSpread ? nums[3] : 0,
            Color = ParseColor(string.Join(" ", colorToks)) ?? new SKColor(0, 0, 0, 128),
            Inset = inset,
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

    /// <summary>list-style-type 关键字 → 本引擎的规范名（CounterStyle 支持的 style 名 / 三个图形符号 / none）。
    /// latin 是 alpha 的别名；未收录的 counter-style 与 url() 回 null，由调用方决定兜底。</summary>
    public static string? ListStyleTypeKeyword(string kw)
    {
        kw = kw.Trim().ToLowerInvariant();
        return kw switch
        {
            "disc" or "circle" or "square" or "none" => kw,
            "decimal-leading-zero" => kw,
            "decimal" or "arabic-indic" or "devanagari" or "persian" or "gujarati" or "kannada" or "malayalam" or "oriya" or "tamil" or "telugu" => "decimal",
            "lower-alpha" or "lower-latin" => "lower-alpha",
            "upper-alpha" or "upper-latin" => "upper-alpha",
            "lower-roman" => "lower-roman",
            "upper-roman" => "upper-roman",
            "cjk-decimal" or "simp-chinese-informal" or "simp-chinese-formal" or "japanese-formal" => "cjk-ideographic",
            "trad-chinese-informal" or "trad-chinese-formal" => "trad-chinese-informal",
            "lower-greek" => "lower-alpha",
            "armenian" or "georgian" or "hebrew" or "ethiopic-halehame" => "upper-roman",
            _ => null,
        };
    }

    /// <summary>解析 background(-image) 里的 linear-gradient(...) / radial-gradient(...)。非渐变→null。</summary>
    public static GradientSpec? ParseGradient(string value)
    {
        value = value?.Trim() ?? "";
        bool radial, conic;
        int open;
        if (value.StartsWith("linear-gradient(", StringComparison.OrdinalIgnoreCase)) { radial = false; conic = false; open = "linear-gradient(".Length - 1; }
        else if (value.StartsWith("radial-gradient(", StringComparison.OrdinalIgnoreCase)) { radial = true; conic = false; open = "radial-gradient(".Length - 1; }
        else if (value.StartsWith("conic-gradient(", StringComparison.OrdinalIgnoreCase)) { radial = false; conic = true; open = "conic-gradient(".Length - 1; }
        else return null;
        int close = value.LastIndexOf(')');
        if (close <= open) return null;
        var inner = value[(open + 1)..close];
        var parts = SplitTopLevel(inner, ',');
        if (parts.Count < 2) return null;

        var spec = new GradientSpec { Radial = radial, Conic = conic, AngleDeg = conic ? 0 : 180 };
        int startIdx = 0;
        if (!radial && !conic)
        {
            // 第一段可能是方向（to right / 90deg），也可能是第一个颜色
            var dir = parts[0].Trim();
            float? angle = ParseGradientDirection(dir);
            if (angle != null) { spec.AngleDeg = angle.Value; startIdx = 1; }
        }
        else if (conic)
        {
            // 第一段可能是 "from 90deg"（可带 "at x y"，中心固定 50% 50%）
            var head = parts[0].Trim();
            if (head.StartsWith("from", StringComparison.OrdinalIgnoreCase) || head.StartsWith("at", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var tok in SplitTopLevel(head, ' '))
                    if (tok.Trim().EndsWith("deg")) { spec.AngleDeg = ParseFloat(tok.Trim()[..^3]); break; }
                startIdx = 1;
            }
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
        var stopsPct = new List<float?>();  // 百分比/无单位 0 → 0..1
        var stopsPx = new List<float?>();   // px 停止位（BuildShader 期按渐变线长折算）
        bool anyStop = false;
        for (int i = startIdx; i < parts.Count; i++)
        {
            var stop = parts[i].Trim();
            float? pct = null, px = null;
            // 末尾可能是 " 50%" / " 12px" / " 0"（AngleSharp computed 会把 0% 折成无单位 0、100% 折成视口 px）
            var sp = SplitTopLevel(stop, ' ');
            if (sp.Count >= 2)
            {
                var last = sp[^1].Trim();
                bool taken = false;
                if (last.EndsWith("%")) { pct = ParseFloat(last[..^1]) / 100f; taken = true; }
                else if (TryAbsoluteLength(last, out float stopNum, out float stopFactor)) { px = stopNum * stopFactor; taken = true; }
                else if (last.EndsWith("deg") && conic) { pct = ParseFloat(last[..^3]) / 360f; taken = true; }   // conic 角停止位
                else if (last == "0") { pct = 0; taken = true; }
                if (taken)
                {
                    stop = string.Join(" ", sp.Take(sp.Count - 1));
                    anyStop = true;
                }
            }
            var col = ParseColor(stop.Trim());
            if (col == null) continue;
            colors.Add(col.Value);
            stopsPct.Add(pct);
            stopsPx.Add(px);
        }
        if (colors.Count < 2) return null;
        spec.Colors = colors.ToArray();
        if (anyStop)
        {
            // 缺位置的按线性内插填充
            var pctArr = new float[colors.Count];
            var pxArr = new float[colors.Count];
            for (int i = 0; i < pctArr.Length; i++)
            {
                pctArr[i] = stopsPct[i] ?? float.NaN;
                pxArr[i] = stopsPx[i] ?? float.NaN;
                if (stopsPct[i] == null && stopsPx[i] == null)
                {
                    float v = (float)i / (pctArr.Length - 1);
                    pctArr[i] = v; pxArr[i] = float.NaN;
                }
            }
            spec.Stops = pctArr;
            spec.StopsPx = pxArr;
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

    /// <summary>filter: blur(4px) drop-shadow(2px 4px 6px rgba(...)) —— 支持 blur 与单个 drop-shadow。</summary>
    public static CssFilter? ParseFilter(string value)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0 || value == "none") return null;
        CssFilter? f = null;
        foreach (var part in SplitTopLevel(value, ' '))
        {
            var p = part.Trim();
            if (p.StartsWith("blur(", StringComparison.OrdinalIgnoreCase))
            {
                (f ??= new CssFilter()).Blur = ToPixels(p[5..^1], 16);
            }
            else if (p.StartsWith("drop-shadow(", StringComparison.OrdinalIgnoreCase))
            {
                var args = SplitTopLevel(p[12..^1], ' ');
                var nums = new List<string>();
                SKColor? col = null;
                foreach (var a in args)
                {
                    var t = a.Trim();
                    if (t.Length == 0) continue;
                    if (col == null && (t.EndsWith("px") || float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                        nums.Add(t);
                    else col = ParseColor(t);
                }
                var ff = f ??= new CssFilter();
                ff.HasShadow = true;
                if (nums.Count > 0) ff.ShadowX = ToPixels(nums[0], 16);
                if (nums.Count > 1) ff.ShadowY = ToPixels(nums[1], 16);
                if (nums.Count > 2) ff.ShadowBlur = ToPixels(nums[2], 16);
                ff.ShadowColor = col ?? SKColors.Black;
            }
        }
        return f;
    }

    /// <summary>clip-path 子集：inset(t r b l [round 半径]) / circle(R at X Y) / polygon(x y, ...)。% 相对盒宽高。</summary>
    public static SkiaSharp.SKPath? BuildClipPath(string value, SkiaSharp.SKRect r)
    {
        value = value?.Trim() ?? "";
        try
        {
            if (value.StartsWith("inset(", StringComparison.OrdinalIgnoreCase))
                return ClipInset(value[6..value.LastIndexOf(')')], r);
            if (value.StartsWith("circle(", StringComparison.OrdinalIgnoreCase))
                return ClipCircle(value[7..value.LastIndexOf(')')], r);
            if (value.StartsWith("polygon(", StringComparison.OrdinalIgnoreCase))
                return ClipPolygon(value[8..value.LastIndexOf(')')], r);
        }
        catch { /* 解析失败视为无裁剪 */ }
        return null;
    }

    static SkiaSharp.SKPath ClipInset(string arg, SkiaSharp.SKRect r)
    {
        var toks = SplitTopLevel(arg, ' ');
        float round = 0;
        var vals = new List<string>();
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i].Trim();
            if (t == "round") { if (i + 1 < toks.Count) round = ToPixels(toks[i + 1].Trim(), 16); break; }
            vals.Add(t);
        }
        float L() => PctPx(vals.Count > 3 ? vals[3] : vals.Count > 1 ? vals[1] : "0", r.Width);
        float R() => PctPx(vals.Count > 1 ? vals[1] : vals.Count > 0 ? vals[0] : "0", r.Width);
        float T() => PctPx(vals.Count > 0 ? vals[0] : "0", r.Height);
        float B() => PctPx(vals.Count > 2 ? vals[2] : vals.Count > 0 ? vals[0] : "0", r.Height);
        var rect = new SkiaSharp.SKRect(r.Left + L(), r.Top + T(), r.Right - R(), r.Bottom - B());
        var p = new SkiaSharp.SKPath();
        if (round > 0) p.AddRoundRect(rect, round, round);
        else p.AddRect(rect);
        return p;
    }

    static SkiaSharp.SKPath ClipCircle(string arg, SkiaSharp.SKRect r)
    {
        var toks = new List<string>();
        foreach (var t in SplitTopLevel(arg, ' ')) if (t.Trim().Length > 0) toks.Add(t.Trim());
        float cx = r.MidX, cy = r.MidY, rad = Math.Min(r.Width, r.Height) / 2;
        int i = 0;
        if (toks.Count > 0 && toks[0] != "at") { rad = PctPx(toks[0], Math.Max(r.Width, r.Height) / 2f); i = 1; }
        if (i < toks.Count && toks[i] == "at")
        {
            if (i + 1 < toks.Count) cx = PctPx(toks[i + 1], r.Width) + r.Left;
            if (i + 2 < toks.Count) cy = PctPx(toks[i + 2], r.Height) + r.Top;
        }
        var p = new SkiaSharp.SKPath();
        p.AddCircle(cx, cy, Math.Max(0, rad));
        return p;
    }

    static SkiaSharp.SKPath ClipPolygon(string arg, SkiaSharp.SKRect r)
    {
        var pts = new List<float>();
        foreach (var pair in SplitTopLevel(arg, ','))
        {
            var xy = SplitTopLevel(pair, ' ').Where(t => t.Trim().Length > 0).ToList();
            if (xy.Count < 2) continue;
            pts.Add(r.Left + PctPx(xy[0].Trim(), r.Width));
            pts.Add(r.Top + PctPx(xy[1].Trim(), r.Height));
        }
        var p = new SkiaSharp.SKPath();
        if (pts.Count >= 6)
        {
            var poly = new SkiaSharp.SKPoint[pts.Count / 2];
            for (int i = 0; i < poly.Length; i++) poly[i] = new SkiaSharp.SKPoint(pts[2 * i], pts[2 * i + 1]);
            p.AddPoly(poly);
        }
        return p;
    }

    static float PctPx(string v, float base_)
    {
        v = v.Trim();
        if (v.EndsWith("%")) return ParseFloat(v[..^1]) / 100f * base_;
        return ToPixels(v, 16);
    }

    public static float ParseFloat(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>span 版：与 string 版同一 NumberStyles/文化，失败回退 0，不再为切单位分配子串。</summary>
    public static float ParseFloat(ReadOnlySpan<char> s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
