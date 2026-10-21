using System.Globalization;

namespace PaperSharp.Css;

/// <summary>
/// CSS <c>transform</c> 的 2D 函数列表：解析作者原样串，按书写顺序保留**原始参数串**
/// （单位折算要等绘制期才知道边框盒尺寸与字号，故此处不折成数）。
///
/// 只做 2D（translate / translateX/Y / scale / scaleX/Y / rotate / rotateZ / skew / skewX/Y / matrix）。
/// 列表里出现引擎不认识的函数（translateZ / rotate3d / rotateX / scale3d / matrix3d / perspective…）
/// 时**整条声明判无效**，而不是只丢那个函数——这是 CSS 与 Chrome 的口径：一个函数解析失败，
/// 整个 transform 作废（DevTools 里该属性直接划掉）。半支持（丢掉 3D 项留下 2D 项）会画出
/// 既不是 A 也不是 B 的东西，最难查。
/// 唯一例外是 <c>rotateZ(θ)</c>：它与 <c>rotate(θ)</c> 逐字同解，判废反而离 Chrome 更远
/// （关键帧里写 <c>rotateZ(0deg)</c> 是常见起手式）。
/// </summary>
public readonly record struct TransformFn(string Name, string[] Args);

public static class TransformParser
{
    /// <summary>解析 <c>transform</c> 值。null = 无变换（none / 空 / 含不支持的函数 / 语法错）。</summary>
    public static List<TransformFn>? Parse(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;

        var fns = new List<TransformFn>();
        int i = 0, n = v.Length;
        while (i < n)
        {
            while (i < n && (char.IsWhiteSpace(v[i]) || v[i] == ',')) i++;
            if (i >= n) break;
            int name0 = i;
            while (i < n && (char.IsLetter(v[i]) || v[i] == '-')) i++;
            string name = v[name0..i].ToLowerInvariant();
            if (name.Length == 0) return null;                       // 不是函数名 → 整条作废
            int p = i;
            while (p < n && char.IsWhiteSpace(v[p])) p++;
            if (p >= n || v[p] != '(') return null;                   // 必须带括号（挡掉 "rotate 24deg"）
            int close = MatchParen(v, p);
            if (close < 0) return null;
            var args = SplitTop(v[(p + 1)..close], ',');
            i = close + 1;
            if (!Supported(name, args.Count)) return null;            // 不认识的函数 → 整条作废
            fns.Add(new TransformFn(name, args.ToArray()));
        }
        return fns.Count == 0 ? null : fns;
    }

    static bool Supported(string name, int argc) => name switch
    {
        "translate" => argc is 1 or 2,
        "translatex" or "translatey" => argc == 1,
        "scale" => argc is 1 or 2,
        "scalex" or "scaley" => argc == 1,
        "rotate" or "rotatez" => argc == 1,
        "skew" or "skewx" or "skewy" => argc == 1,
        "matrix" => argc == 6,
        // 2D 惰性的 3D 写法：没有透视时它们不改变外观，收下（按恒等/取 x,y 处理）。
        // 判整条作废反而与 Chrome 不一致——translateZ(0)/translate3d(…) 是前端最常见的图层提升 hack。
        "translatez" or "scalez" => argc == 1,
        "translate3d" => argc == 3,
        // 真会改变形状的 3D（rotateX/rotateY/rotate3d/scale3d/matrix3d/perspective）：不支持 → 整条作废。
        _ => false,
    };

    /// <summary>
    /// 解析 <c>transform-origin</c>：1~2 个值（写了第三个 z 就忽略它）。
    /// 返回 (xPx, yPx, xPct, yPct)，绘制期按边框盒宽/高把 pct 折成 px 再相加。
    /// 关键字按 CSS：left/top=0%、center=50%、right/bottom=100%；只写一个值时另一轴取 center。
    /// 语法不对返回 null → 调用方保持初值 50% 50%。
    /// </summary>
    public static (float X, float Y, float XPct, float YPct)? ParseOrigin(string? value, float fontSize)
    {
        var toks = SplitTop((value ?? "").Trim(), ' ');
        if (toks.Count is 0 or > 3) return null;

        // 单值时第一轴只接受水平关键字；作者给的是 top/bottom 就按「写的是 y 轴」处理、x 回落 center
        // ——比直接判废更接近 Chrome 的实际观感。
        if (toks.Count == 1 && (toks[0].Equals("top", StringComparison.OrdinalIgnoreCase)
                             || toks[0].Equals("bottom", StringComparison.OrdinalIgnoreCase)))
        {
            var y = Axis(toks[0], fontSize);
            return y == null ? null : (0f, y.Value.Px, 50f, y.Value.Pct);
        }
        var a = Axis(toks[0], fontSize);
        var b = toks.Count >= 2 ? Axis(toks[1], fontSize) : (0f, 50f);
        if (a == null || b == null) return null;
        return (a.Value.Px, b.Value.Px, a.Value.Pct, b.Value.Pct);
    }

    /// <summary>单个 origin 值 → (px, pct)。</summary>
    static (float Px, float Pct)? Axis(string tok, float fontSize)
    {
        if (tok.Equals("left", StringComparison.OrdinalIgnoreCase) || tok.Equals("top", StringComparison.OrdinalIgnoreCase))
            return (0f, 0f);
        if (tok.Equals("center", StringComparison.OrdinalIgnoreCase)) return (0f, 50f);
        if (tok.Equals("right", StringComparison.OrdinalIgnoreCase) || tok.Equals("bottom", StringComparison.OrdinalIgnoreCase))
            return (0f, 100f);
        if (tok.EndsWith("%", StringComparison.Ordinal))
            return Num(tok[..^1]) is float pc ? (0f, pc) : null;
        return IsNumericStart(tok) ? (CssValueParser.ToPixels(tok, fontSize), 0f) : null;
    }

    static bool IsNumericStart(string t) =>
        t.Length > 0 && (char.IsDigit(t[0]) || t[0] == '.' || t[0] == '-' || t[0] == '+');

    /// <summary>裸数值；非法返回 null（不静默当 0，让调用方判废）。</summary>
    static float? Num(string s) =>
        float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null;

    /// <summary>按顶层分隔符切开（括号内的不算）。',' 切参数，' ' 切值列表（连续空白当一个分隔）。</summary>
    static List<string> SplitTop(string inner, char sep)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i <= inner.Length; i++)
        {
            bool isSep = i == inner.Length
                || (depth == 0 && (inner[i] == sep || (sep == ' ' && char.IsWhiteSpace(inner[i]))));
            if (isSep)
            {
                var part = inner[start..i].Trim();
                if (part.Length > 0) list.Add(part);
                start = i + 1;
                continue;
            }
            if (inner[i] == '(') depth++;
            else if (inner[i] == ')') depth--;
        }
        return list;
    }

    static int MatchParen(string s, int openAt)
    {
        int depth = 0;
        for (int i = openAt; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    /// <summary>长度参数 → px；<c>%</c> 返回 0，其百分数由 <see cref="Percent"/> 单独取用。</summary>
    public static float LengthPx(string raw, float fontSize) =>
        raw.Trim().EndsWith("%", StringComparison.Ordinal) ? 0f : CssValueParser.ToPixels(raw, fontSize);

    /// <summary>角度参数 → 度（deg / grad / turn / rad / 裸数按 deg）。</summary>
    public static float Degrees(string raw)
    {
        var t = raw.Trim().ToLowerInvariant();
        float Take(string body, Func<float, float> f) => Num(body) is float x ? f(x) : 0f;
        if (t.EndsWith("deg")) return Take(t[..^3], v => v);
        if (t.EndsWith("grad")) return Take(t[..^4], v => v * 0.9f);
        if (t.EndsWith("turn")) return Take(t[..^4], v => v * 360f);
        if (t.EndsWith("rad")) return Take(t[..^3], v => v * (180f / MathF.PI));
        return Take(t, v => v);
    }

    /// <summary>参数是百分比时返回其百分数，否则 null。</summary>
    public static float? Percent(string raw) =>
        raw.Trim().EndsWith("%", StringComparison.Ordinal) ? Num(raw.Trim()[..^1]) : null;

    /// <summary>裸数值（scale / matrix 的参数）。</summary>
    public static float Scalar(string raw) => Num(raw) ?? 0f;
}
