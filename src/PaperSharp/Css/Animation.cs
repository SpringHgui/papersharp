using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace PaperSharp.Css;

/// <summary>@keyframes 单帧：offset(0..1) + 该帧声明的属性表（原始值串）。</summary>
public sealed class Keyframe
{
    public float Offset;
    public Dictionary<string, string> Decls = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 确定性动画取帧：解析 @keyframes + animation 简写，在给定时刻 t 求值插值、覆盖元素计算样式。
/// 仅覆盖引擎已渲染的属性（opacity / 颜色 / 长度 / 圆角 / 字号 / 定位偏移）；transform 类不支持（引擎未实现 transform）。
/// </summary>
public static class Animation
{
    // ── @keyframes 解析 ────────────────────────────────────────────

    /// <summary>从原始 CSS 解析所有 @keyframes：name → 按 offset 升序的帧表。支持 from/to、百分比、多选择器帧。</summary>
    public static Dictionary<string, List<Keyframe>> ParseKeyframes(string css)
    {
        var result = new Dictionary<string, List<Keyframe>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(css)) return result;
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        int idx = 0;
        while ((idx = IndexOfAtRule(css, "@keyframes", idx, out int nameStart)) >= 0)
        {
            int braceOpen = css.IndexOf('{', nameStart);
            if (braceOpen < 0) break;
            int braceClose = MatchBrace(css, braceOpen);
            if (braceClose < 0) break;
            string name = css[nameStart..braceOpen].Trim();
            var frames = ParseFrames(css[(braceOpen + 1)..braceClose]);
            if (name.Length > 0 && frames.Count > 0)
                result[name] = frames;
            idx = braceClose + 1;
        }
        return result;
    }

    static int IndexOfAtRule(string s, string at, int from, out int contentStart)
    {
        contentStart = 0;
        int i = from;
        while ((i = s.IndexOf(at, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int after = i + at.Length;
            // 边界：前一字符非标识符，后一字符是空白/名字起点
            bool leftOk = i == 0 || !(char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '-' || s[i - 1] == '@');
            bool rightOk = after < s.Length && (char.IsWhiteSpace(s[after]) || s[after] == '{');
            if (leftOk && rightOk) { contentStart = after; return i; }
            i = after;
        }
        return -1;
    }

    static List<Keyframe> ParseFrames(string body)
    {
        var frames = new List<Keyframe>();
        foreach (Match m in Regex.Matches(body, @"([^{}]+)\{([^{}]*)\}"))
        {
            var selectors = m.Groups[1].Value.Split(',');
            var decls = ParseDecls(m.Groups[2].Value);
            foreach (var selRaw in selectors)
            {
                float off = ParseOffset(selRaw.Trim());
                if (float.IsNaN(off)) continue;
                var kf = new Keyframe { Offset = off };
                foreach (var kv in decls) kf.Decls[kv.Prop] = kv.Val;
                frames.Add(kf);
            }
        }
        // 同 offset 合并（后定义覆盖），按 offset 升序
        return frames.GroupBy(f => f.Offset)
                     .Select(g => { var kf = new Keyframe { Offset = g.Key }; foreach (var f in g) foreach (var kv in f.Decls) kf.Decls[kv.Key] = kv.Value; return kf; })
                     .OrderBy(k => k.Offset).ToList();
    }

    static float ParseOffset(string sel) => sel.ToLowerInvariant() switch
    {
        "from" => 0f, "to" => 1f,
        var s when s.EndsWith("%") && float.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => Math.Clamp(p / 100f, 0f, 1f),
        _ => float.NaN,
    };

    static List<(string Prop, string Val)> ParseDecls(string body)
    {
        var list = new List<(string, string)>();
        foreach (var stmt in body.Split(';'))
        {
            int colon = stmt.IndexOf(':');
            if (colon <= 0) continue;
            var p = stmt[..colon].Trim();
            var v = stmt[(colon + 1)..].Trim();
            if (p.Length > 0 && v.Length > 0) list.Add((p, v));
        }
        return list;
    }

    static int MatchBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    // ── animation 简写解析（写入 ComputedStyle）──────────────────────

    /// <summary>解析 animation 简写串，填充样式字段。顺序无关：时间→duration/delay、数字/infinite→iteration、
    /// 关键字→direction/fill/timing、其余标识符→name。</summary>
    public static void ParseShorthand(string value, ComputedStyle s)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        // 拆分顶层逗号（多动画取第一个）
        var first = Regex.Split(value, @",(?![^()]*\))")[0].Trim();
        string? name = null;
        var times = new List<float>();
        foreach (var tok in SplitToplevel(first))
        {
            var t = tok.Trim();
            if (t.Length == 0) continue;
            string low = t.ToLowerInvariant();
            // 先认关键字（forwards/backwards 也以 's' 结尾，若先判时间会被误当秒数），再认时间
            if (low == "infinite") s.AnimationIteration = float.PositiveInfinity;
            else if (low is "normal" or "reverse" or "alternate" or "alternate-reverse") s.AnimationDirection = low;
            else if (low is "none" or "forwards" or "backwards" or "both") { if (low != "none") s.AnimationFill = low; }
            else if (low is "ease" or "ease-in" or "ease-out" or "ease-in-out" or "linear"
                     || low.StartsWith("cubic-bezier") || low.StartsWith("steps")) s.AnimationTiming = low;
            else if ((low.EndsWith("ms") || low.EndsWith("s")) && TryParseSeconds(t, out float sec)) times.Add(sec);
            else if (float.TryParse(low, NumberStyles.Float, CultureInfo.InvariantCulture, out var it) && name == null && times.Count == 0)
            { s.AnimationIteration = it; }   // 迭代数字（出现在时间前时也接受）
            else if (name == null && (char.IsLetter(low[0]) || low[0] == '_' || low[0] == '-')) name = t;
        }
        if (times.Count > 0) s.AnimationDuration = times[0];
        if (times.Count > 1) s.AnimationDelay = times[1];
        if (name != null) s.AnimationName = name;
    }

    /// <summary>把 "2s" / "500ms" 解析成秒。数值部分须能独立解析（"forwards" 去掉尾 s 不成立 → 判假）。</summary>
    static bool TryParseSeconds(string token, out float seconds)
    {
        seconds = 0f;
        string low = token.ToLowerInvariant();
        bool ms = low.EndsWith("ms");
        if (!ms && !low.EndsWith("s")) return false;
        string num = ms ? token[..^2] : token[..^1];
        if (!float.TryParse(num.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return false;
        seconds = ms ? v / 1000f : v;
        return true;
    }

    static IEnumerable<string> SplitToplevel(string s)
    {
        int depth = 0; var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (c == ' ' && depth == 0) { yield return sb.ToString(); sb.Clear(); }
            else sb.Append(c);
        }
        yield return sb.ToString();
    }

    // ── 按时刻 t 求值 + 覆盖 ────────────────────────────────────────

    public static void Apply(ComputedStyle root, Dictionary<string, List<Keyframe>> keyframes, double timeSeconds)
    {
        if (keyframes.Count == 0) return;
        Visit(root, keyframes, (float)timeSeconds);
    }

    static void Visit(ComputedStyle node, Dictionary<string, List<Keyframe>> kf, float t)
    {
        if (node.AnimationName.Length > 0 && kf.TryGetValue(node.AnimationName, out var frames) && frames.Count > 0)
            ApplyToNode(node, frames, t);
        foreach (var c in node.Children) Visit(c, kf, t);
        // 伪元素生成盒也参与（其 Style 挂在 Before/After）
        if (node.Before != null) Visit(node.Before.Style, kf, t);
        if (node.After != null) Visit(node.After.Style, kf, t);
    }

    static void ApplyToNode(ComputedStyle s, List<Keyframe> frames, float t)
    {
        float p = Progress(s, t);
        if (float.IsNaN(p)) return;   // fill=none 且不在作用区间 → 不覆盖
        p = Math.Clamp(p, 0f, 1f);
        // 找 p 两侧的帧
        Keyframe lo = frames[0], hi = frames[^1];
        foreach (var f in frames) { if (f.Offset <= p) lo = f; }
        foreach (var f in frames) { if (f.Offset >= p) { hi = f; break; } }
        float span = hi.Offset - lo.Offset;
        float lt = span > 1e-6f ? (p - lo.Offset) / span : 0f;
        foreach (var prop in UnionProps(lo, hi))
        {
            var fromVal = lo.Decls.TryGetValue(prop, out var a) ? a : (hi.Decls.TryGetValue(prop, out var b) ? b : null);
            var toVal = hi.Decls.TryGetValue(prop, out var c) ? c : (lo.Decls.TryGetValue(prop, out var d) ? d : null);
            if (fromVal == null || toVal == null) continue;
            ApplyProp(s, prop, fromVal, toVal, lt);
        }
    }

    static IEnumerable<string> UnionProps(Keyframe a, Keyframe b)
    {
        var set = new HashSet<string>(a.Decls.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in b.Decls.Keys) set.Add(k);
        return set;
    }

    /// <summary>把时刻 t 折算成关键帧进度 p∈[0,1]（含 delay / iteration / direction / fill / timing）。
    /// 返回 NaN 表示该时刻不应用动画（fill=none 且在作用区间外）。</summary>
    static float Progress(ComputedStyle s, float t)
    {
        float dur = s.AnimationDuration, delay = s.AnimationDelay;
        float local = t - delay;
        bool fillFwd = s.AnimationFill is "forwards" or "both";
        bool fillBack = s.AnimationFill is "backwards" or "both";

        if (dur <= 0f)
        {
            if (local < 0) return fillBack ? 0f : float.NaN;
            return fillFwd ? 1f : 0f;   // 0 时长：瞬间到末帧（fill forwards）或首帧
        }
        if (local < 0) return fillBack ? Eased(s, s.AnimationDirection is "reverse" or "alternate-reverse" ? 1f : 0f) : float.NaN;

        float iterProgress = local / dur;
        float iter = (float)Math.Floor(iterProgress);
        bool infinite = float.IsPositiveInfinity(s.AnimationIteration);
        if (!infinite && iter >= s.AnimationIteration)
            return fillFwd ? Eased(s, EndFrac(s, iter)) : float.NaN;

        float frac = iterProgress - iter;
        bool reversed = s.AnimationDirection switch
        {
            "reverse" or "alternate-reverse" => true,
            "alternate" or "alternate-reverse" => (int)iter % 2 == 1,
            _ => false,
        };
        if (s.AnimationDirection is "alternate" or "alternate-reverse")
            reversed = ((int)iter % 2 == 1) ^ (s.AnimationDirection == "alternate-reverse");
        if (reversed) frac = 1f - frac;
        return Eased(s, frac);
    }

    static float EndFrac(ComputedStyle s, float lastIter)
    {
        // 最后一次执行的迭代索引（0 基）
        int lastIdx = (int)lastIter - 1;
        bool endsReversed = s.AnimationDirection switch
        {
            "reverse" => true,
            "alternate" => lastIdx % 2 == 1,
            "alternate-reverse" => lastIdx % 2 == 0,
            _ => false,
        };
        return endsReversed ? 0f : 1f;
    }

    static float Eased(ComputedStyle s, float frac) => Timing(s.AnimationTiming, Math.Clamp(frac, 0f, 1f));

    // ── 属性插值（仅引擎已渲染的属性）──────────────────────────────

    static void ApplyProp(ComputedStyle s, string prop, string from, string to, float t)
    {
        switch (prop.ToLowerInvariant())
        {
            case "opacity": s.Opacity = LerpF(Parse01(from), Parse01(to), t); break;
            case "color": s.Color = LerpColor(from, to, t); break;
            case "background-color":
                var bc = LerpColor(from, to, t); s.BackgroundColor = bc; break;
            case "font-size": s.FontSize = Math.Max(1f, LerpPx(from, to, t, s.FontSize)); break;
            case "letter-spacing": s.LetterSpacing = LerpPx(from, to, t, s.FontSize); break;
            case "width": s.Width = CssSize.Abs(LerpPx(from, to, t, s.FontSize)); break;
            case "height": s.Height = CssSize.Abs(LerpPx(from, to, t, s.FontSize)); break;
            case "top": s.OffsetTop = CssSize.Abs(LerpPx(from, to, t, s.FontSize)); break;
            case "right": s.OffsetRight = CssSize.Abs(LerpPx(from, to, t, s.FontSize)); break;
            case "bottom": s.OffsetBottom = CssSize.Abs(LerpPx(from, to, t, s.FontSize)); break;
            case "left": s.OffsetLeft = CssSize.Abs(LerpPx(from, to, t, s.FontSize)); break;
            case "border-radius":
                var rad = LerpPx(from, to, t, s.FontSize);
                s.BorderRadiusTopLeft = s.BorderRadiusTopRight = s.BorderRadiusBottomRight = s.BorderRadiusBottomLeft = Math.Max(0, rad); break;
            case "border-top-left-radius": s.BorderRadiusTopLeft = Math.Max(0, LerpPx(from, to, t, s.FontSize)); break;
            case "border-top-right-radius": s.BorderRadiusTopRight = Math.Max(0, LerpPx(from, to, t, s.FontSize)); break;
            case "border-bottom-right-radius": s.BorderRadiusBottomRight = Math.Max(0, LerpPx(from, to, t, s.FontSize)); break;
            case "border-bottom-left-radius": s.BorderRadiusBottomLeft = Math.Max(0, LerpPx(from, to, t, s.FontSize)); break;
            // transform / 渐变背景等不支持（引擎未实现 transform）→ 忽略
        }
    }

    static float Parse01(string v)
    {
        v = v.Trim();
        if (v.EndsWith("%")) return CssValueParser.ParseFloat(v[..^1]) / 100f;
        return CssValueParser.ParseFloat(v);
    }
    static float LerpF(float a, float b, float t) => a + (b - a) * t;
    static float LerpPx(string a, string b, float t, float baseFs)
        => LerpF(CssValueParser.ToPixels(a, baseFs), CssValueParser.ToPixels(b, baseFs), t);

    static SKColor LerpColor(string a, string b, float t)
    {
        var ca = CssValueParser.ParseColor(a) ?? SKColors.Black;
        var cb = CssValueParser.ParseColor(b) ?? SKColors.Black;
        byte M(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return new SKColor(M(ca.Red, cb.Red), M(ca.Green, cb.Green), M(ca.Blue, cb.Blue), M(ca.Alpha, cb.Alpha));
    }

    // ── 缓动函数 ────────────────────────────────────────────────────

    public static float Timing(string fn, float x)
    {
        fn = fn.Trim().ToLowerInvariant();
        switch (fn)
        {
            case "linear": return x;
            case "ease": return CubicBezier(0.25f, 0.1f, 0.25f, 1f, x);
            case "ease-in": return CubicBezier(0.42f, 0f, 1f, 1f, x);
            case "ease-out": return CubicBezier(0f, 0f, 0.58f, 1f, x);
            case "ease-in-out": return CubicBezier(0.42f, 0f, 0.58f, 1f, x);
        }
        if (fn.StartsWith("cubic-bezier("))
        {
            var n = ParseNums(fn[13..^1]);
            if (n.Length == 4) return CubicBezier(n[0], n[1], n[2], n[3], x);
        }
        if (fn.StartsWith("steps("))
        {
            var inner = fn[6..^1];
            var parts = inner.Split(',', StringSplitOptions.TrimEntries);
            int steps = parts.Length > 0 ? (int)CssValueParser.ParseFloat(parts[0]) : 1;
            bool jumpStart = parts.Length > 1 && parts[1] == "start";
            if (steps < 1) steps = 1;
            return jumpStart ? Math.Min(1f, (float)Math.Floor(x * steps) / steps)
                             : Math.Ceiling(x * steps) / steps > 1 ? 1f : (float)Math.Ceiling(x * steps) / steps;
        }
        return x; // 未知 → 线性
    }

    static float[] ParseNums(string s) =>
        s.Split(',', StringSplitOptions.TrimEntries).Select(t => CssValueParser.ParseFloat(t)).ToArray();

    /// <summary>三次贝塞尔缓动：给 x 求 y（Newton 迭代 + 二分兜底）。</summary>
    static float CubicBezier(float x1, float y1, float x2, float y2, float x)
    {
        if (x <= 0) return 0; if (x >= 1) return 1;
        static float Bez(float a, float b, float t) => 3 * a * (1 - t) * (1 - t) * t + 3 * b * (1 - t) * t * t + t * t * t;
        static float Slope(float a, float b, float t) => 3 * a * (1 - t) * (1 - 3 * t) + 3 * b * t * (2 - 3 * t) + 3 * t * t;
        float t = x;
        for (int i = 0; i < 8; i++)
        {
            float cx = Bez(x1, x2, t) - x;
            if (Math.Abs(cx) < 1e-5f) break;
            float d = Slope(x1, x2, t);
            if (Math.Abs(d) < 1e-6f) break;
            t -= cx / d;
        }
        // 二分兜底
        if (t < 0 || t > 1)
        {
            float lo = 0, hi = 1; t = x;
            for (int i = 0; i < 24; i++) { float cx = Bez(x1, x2, t); if (Math.Abs(cx - x) < 1e-5f) break; if (cx > x) hi = t; else lo = t; t = (lo + hi) / 2; }
        }
        return Bez(y1, y2, t);
    }
}
