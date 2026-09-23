using System.Text;

namespace PaperSharp.Css;

public enum ContentRunKind { Literal, Counter, Counters, Image, Quote }

/// <summary>content 值解析出的一段。</summary>
public sealed class ContentRun
{
    public ContentRunKind Kind;
    public string? Text;          // Literal / Quote 解析后的文本
    public string? CounterName;   // Counter / Counters
    public string Sep = "";       // Counters 连接符
    public string Style = "decimal";
    public string? Url;           // Image
    public bool OpenQuote;        // Quote：true=open，false=close
}

/// <summary>一个伪元素生成盒：自身 computed 样式 + content 片段（counter 值待后处理回填）。</summary>
public sealed class PseudoGen
{
    public ComputedStyle Style = null!;
    public List<ContentRun> Runs = new();
}

/// <summary>
/// 伪元素 content 解析 + 计数器文档序求值 + 匿名盒注入。
/// 由 StyleResolver 在整棵树 Walk 完之后调用 Apply()。
/// </summary>
public static class PseudoContent
{
    /// <summary>解析 content 值。返回 null 表示不生成盒（none/normal/缺省）；返回空表表示生成空内容盒。</summary>
    public static List<ContentRun>? Parse(string? value, Func<string, string?> attr)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase)
                             || value.Equals("normal", StringComparison.OrdinalIgnoreCase))
            return null;

        var runs = new List<ContentRun>();
        int i = 0, n = value.Length;
        while (i < n)
        {
            char c = value[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }

            if (c == '"' || c == '\'')
            {
                i++; var sb = new StringBuilder();
                while (i < n && value[i] != c)
                {
                    if (value[i] == '\\' && i + 1 < n) { sb.Append(Unescape(value[i + 1])); i += 2; }
                    else sb.Append(value[i++]);
                }
                i++; // 收尾引号
                runs.Add(new ContentRun { Kind = ContentRunKind.Literal, Text = sb.ToString() });
                continue;
            }

            if (TakeFunc(value, ref i, n, "attr", out var arg))
            {
                var a = FuncArgs(arg);
                var name = a.Count > 0 ? a[0] : "";
                var fb = a.Count > 1 ? Unquote(a[1]) : null;
                runs.Add(new ContentRun { Kind = ContentRunKind.Literal, Text = attr(name) ?? fb ?? "" });
                continue;
            }
            if (TakeFunc(value, ref i, n, "counter", out var carg))
            {
                var a = FuncArgs(carg);
                runs.Add(new ContentRun { Kind = ContentRunKind.Counter, CounterName = a.Count > 0 ? a[0].ToLowerInvariant() : "", Style = a.Count > 1 ? Unquote(a[1]) : "decimal" });
                continue;
            }
            if (TakeFunc(value, ref i, n, "counters", out var sarg))
            {
                var a = FuncArgs(sarg);
                runs.Add(new ContentRun { Kind = ContentRunKind.Counters, CounterName = a.Count > 0 ? a[0].ToLowerInvariant() : "", Sep = a.Count > 1 ? Unquote(a[1]) : "", Style = a.Count > 2 ? Unquote(a[2]) : "decimal" });
                continue;
            }
            if (TakeFunc(value, ref i, n, "url", out var uarg))
            {
                runs.Add(new ContentRun { Kind = ContentRunKind.Image, Url = Unquote(uarg.Trim()) });
                continue;
            }

            // 关键字
            if (MatchWord(value, ref i, n, "open-quote")) { runs.Add(new ContentRun { Kind = ContentRunKind.Quote, OpenQuote = true }); continue; }
            if (MatchWord(value, ref i, n, "close-quote")) { runs.Add(new ContentRun { Kind = ContentRunKind.Quote, OpenQuote = false }); continue; }
            if (MatchWord(value, ref i, n, "no-quote")) { continue; }

            // 未知 token：跳过到下一个分隔
            while (i < n && !char.IsWhiteSpace(value[i]) && value[i] != ',') i++;
        }
        return runs;
    }

    static char Unescape(char c) => c switch
    {
        'n' => '\n', 't' => '\t', _ => c,
    };

    static bool TakeFunc(string s, ref int i, int n, string fn, out string arg)
    {
        arg = "";
        int len = fn.Length;
        if (n - i < len + 1) return false;
        if (!s.AsSpan(i, len).Equals(fn, StringComparison.OrdinalIgnoreCase)) return false;
        int p = i + len;
        while (p < n && char.IsWhiteSpace(s[p])) p++;
        if (p >= n || s[p] != '(') return false;   // 必须是函数调用（如 counter( 而非 counterX）
        // 若 fn 后紧跟字母（非括号）则不是本函数
        p++;
        int depth = 1, start = p;
        while (p < n && depth > 0)
        {
            if (s[p] == '(') depth++;
            else if (s[p] == ')') depth--;
            if (depth == 0) break;
            p++;
        }
        arg = s[start..p];
        i = Math.Min(p + 1, n); // 跳过收尾 )
        return true;
    }

    static bool MatchWord(string s, ref int i, int n, string w)
    {
        if (n - i < w.Length) return false;
        if (!s.AsSpan(i, w.Length).Equals(w, StringComparison.OrdinalIgnoreCase)) return false;
        int after = i + w.Length;
        if (after < n && (char.IsLetterOrDigit(s[after]) || s[after] == '-')) return false;
        i = after;
        return true;
    }

    /// <summary>
    /// 拆函数参数：按顶层逗号或空白切分（AngleSharp 会把 counters(a,',') 重序列化成 counters(a ,)，
    /// 逗号变空白），引号内与括号内不切。返回去空白的 token 串。
    /// </summary>
    static List<string> FuncArgs(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        int depth = 0; char q = '\0';
        void Flush() { if (sb.Length > 0) { list.Add(sb.ToString().Trim()); sb.Clear(); } }
        foreach (var c in s)
        {
            if (q != '\0') { sb.Append(c); if (c == q) q = '\0'; continue; }
            if (c == '"' || c == '\'') { q = c; sb.Append(c); continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (depth == 0 && (c == ',' || char.IsWhiteSpace(c))) { Flush(); continue; }
            sb.Append(c);
        }
        Flush();
        return list;
    }

    static string Unquote(string s)
    {
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0]) return s[1..^1];
        return s;
    }

    // ── 计数器文档序求值 + 注入 ──────────────────────────────────────

    /// <summary>从 counter-reset / counter-increment 声明值解析 (name, delta) 序列。</summary>
    static List<(string name, int val)> ParseCounterDecls(string decl, int defaultDelta)
    {
        var list = new List<(string, int)>();
        if (string.IsNullOrWhiteSpace(decl) || decl.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return list;
        var toks = decl.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < toks.Length; i++)
        {
            var name = toks[i].Trim().ToLowerInvariant();
            if (name.Length == 0) continue;
            int val = defaultDelta;
            if (i + 1 < toks.Length && int.TryParse(toks[i + 1], out var v)) { val = v; i++; }
            list.Add((name, val));
        }
        return list;
    }

    /// <summary>为行内伪元素建立格式化上下文的块级 owner（含 list-item——li 的 UA display）。</summary>
    static bool Blockish(ComputedStyle s) => s.Display is "block" or "table-cell" or "list-item" or "flow-root";

    /// <summary>整棵树的文档序 pass：维护计数器作用域，回填伪元素 content，注入匿名盒。</summary>
    public static void Apply(ComputedStyle root)
    {
        var counters = new Dictionary<string, List<int>>();   // name → 作用域栈（内层覆盖外层）
        Visit(root, counters);
    }

    static void Visit(ComputedStyle node, Dictionary<string, List<int>> counters)
    {
        // 1) counter-reset：为本子树新建计数器（push），记录待 pop
        var pushed = new List<string>();
        foreach (var (name, val) in ParseCounterDecls(node.CounterReset, 0))
        {
            if (!counters.TryGetValue(name, out var stack)) counters[name] = stack = new List<int>();
            stack.Add(val);
            pushed.Add(name);
        }
        // 2) counter-increment：自增当前作用域值（无则从 0 建）
        foreach (var (name, delta) in ParseCounterDecls(node.CounterIncrement, 1))
        {
            if (!counters.TryGetValue(name, out var stack)) counters[name] = stack = new List<int>();
            if (stack.Count == 0) stack.Add(0);
            stack[^1] += delta;
        }

        // 3) 快照原有子元素（注入的伪元素盒不参与计数器求值）
        var originalChildren = node.Children.ToArray();

        // 4) 回填并注入 ::before / ::after
        if (node.Before != null) Materialize(node, node.Before, isBefore: true, counters);
        if (node.After != null) Materialize(node, node.After, isBefore: false, counters);

        // 4b) 行内伪元素落在「有块级子元素的块」上 → 裹匿名 inline 块子盒（如嵌套列表 li::before）
        if (node.HasGeneratedInline && !node.IsInlineFC && Blockish(node))
            WrapInlineContent(node);

        // 5) 递归原有子元素
        foreach (var c in originalChildren) Visit(c, counters);

        // 6) pop 本节点 reset 建立的计数器
        for (int i = pushed.Count - 1; i >= 0; i--)
            if (counters.TryGetValue(pushed[i], out var st) && st.Count > 0) st.RemoveAt(st.Count - 1);
    }

    static string ResolveRuns(PseudoGen gen, Dictionary<string, List<int>> counters)
    {
        var sb = new StringBuilder();
        foreach (var r in gen.Runs)
        {
            switch (r.Kind)
            {
                case ContentRunKind.Literal: sb.Append(r.Text); break;
                case ContentRunKind.Quote: sb.Append(r.OpenQuote ? "“" : "”"); break;
                case ContentRunKind.Counter:
                    sb.Append(CounterStyle.Format(Peek(counters, r.CounterName), r.Style)); break;
                case ContentRunKind.Counters:
                    if (counters.TryGetValue(r.CounterName ?? "", out var st))
                        sb.Append(string.Join(r.Sep, st.Select(v => CounterStyle.Format(v, r.Style))));
                    break;
                // Image 不并入文本串，单独成盒
            }
        }
        return sb.ToString();
    }

    static int Peek(Dictionary<string, List<int>> counters, string? name)
        => name != null && counters.TryGetValue(name, out var st) && st.Count > 0 ? st[^1] : 0;

    static void Materialize(ComputedStyle owner, PseudoGen gen, bool isBefore, Dictionary<string, List<int>> counters)
    {
        var disp = gen.Style.Display;
        bool blockishPseudo = disp is "block" or "list-item" or "flex" or "grid" or "table";

        // 图片片段（url()）：合成行内替换元素
        var imageRuns = gen.Runs.Where(r => r.Kind == ContentRunKind.Image).ToList();
        string text = ResolveRuns(gen, counters);

        if (blockishPseudo)
        {
            // 块级伪元素：作为匿名块子盒插入 owner.Children（::before 置首，::after 置尾）。
            // 若 owner 自身有裸文本且是 ::before，先把文本包成匿名块子盒，令 ::before 排在其前
            // （否则块流会先画 owner 文本再画 children，装饰性 ::before 顶栏会跑到文字下面）。
            if (isBefore && !string.IsNullOrEmpty(owner.TextContent) && !owner.IsInlineFC)
            {
                var anon = owner.CloneShallow();
                anon.TextContent = owner.TextContent;
                anon.Children = new();
                anon.InlinePieces = new();
                anon.Before = anon.After = null;
                anon.Display = "block";
                owner.TextContent = null;
                owner.Children.Insert(0, anon);
            }
            gen.Style.Display = disp;
            gen.Style.TextContent = string.IsNullOrEmpty(text) ? null : text;
            gen.Style.Children = new();
            gen.Style.InlinePieces = new();
            if (isBefore) owner.Children.Insert(0, gen.Style);
            else owner.Children.Add(gen.Style);
            return;
        }

        // 行内 / inline-block：注入 owner.InlinePieces
        bool inlineBlock = disp == "inline-block";
        var pieces = new List<InlinePiece>();
        if (inlineBlock)
        {
            gen.Style.Display = "inline-block";
            gen.Style.TextContent = string.IsNullOrEmpty(text) ? null : text;
            gen.Style.Children = new();
            gen.Style.InlinePieces = new();
            foreach (var img in imageRuns) gen.Style.Children.Add(MakeImgPiece(img.Url!, gen.Style));
            pieces.Add(InlinePiece.OfChild(gen.Style));
        }
        else
        {
            // inline：文本 + 图片各自成片，用伪元素自身样式承载文本片段
            gen.Style.Display = "inline";
            gen.Style.Children = new();
            gen.Style.InlinePieces = new();
            if (!string.IsNullOrEmpty(text)) gen.Style.InlinePieces.Add(InlinePiece.OfText(text));
            foreach (var img in imageRuns) gen.Style.InlinePieces.Add(InlinePiece.OfChild(MakeImgPiece(img.Url!, gen.Style)));
            if (gen.Style.InlinePieces.Count == 0) return;   // 空 inline 伪元素：不成盒
            pieces.Add(InlinePiece.OfChild(gen.Style));
        }

        if (isBefore) owner.InlinePieces.InsertRange(0, pieces);
        else owner.InlinePieces.AddRange(pieces);

        // 块级 / inline-block owner：无块级子元素→自身切 inline-FC（inline-block 本身即格式化上下文根）；
        // 有块级子元素→标记待裹（Visit 里包匿名 inline 块子盒）。
        if (Blockish(owner) || owner.Display == "inline-block")
        {
            owner.HasGeneratedInline = true;
            bool hasBlockChild = owner.Children.Any(c => c.Display != "none" && !ComputedStyle.IsInlineLevel(c.Display) && c.Tag is not ("img" or "svg"));
            if (!hasBlockChild) owner.IsInlineFC = true;
        }
    }

    /// <summary>
    /// 「文本 + 块级子元素 + 行内伪元素」混排（如嵌套列表 li）：把行内内容（含已注入伪元素片段）
    /// 包成匿名 inline-FC 块子盒，置于块子元素之前。有行内元素子节点时跳过（避免与块路径重复渲染，Phase-1 已知简化）。
    /// </summary>
    static void WrapInlineContent(ComputedStyle owner)
    {
        if (owner.Children.Any(c => ComputedStyle.IsInlineLevel(c.Display) && c.Tag is not ("img" or "svg"))) return;
        var inlinePieces = owner.InlinePieces
            .Where(p => p.IsBreak || p.Text != null ||
                        (p.Child != null && (ComputedStyle.IsInlineLevel(p.Child.Display) || p.Child.Tag is "img" or "svg")))
            .ToList();
        if (inlinePieces.Count == 0) return;
        var anon = owner.CloneShallow();
        anon.TextContent = owner.TextContent;
        anon.InlinePieces = inlinePieces;
        anon.Children = new();
        anon.Before = anon.After = null;
        anon.Display = "block";
        anon.IsInlineFC = true;
        anon.HasGeneratedInline = false;
        owner.TextContent = null;
        owner.InlinePieces = new();
        owner.Children.Insert(0, anon);
    }

    static ComputedStyle MakeImgPiece(string url, ComputedStyle from)
    {
        var img = from.CloneShallow();
        img.Tag = "img";
        img.Display = "inline";
        img.ImageSrc = url;
        img.Children = new();
        img.InlinePieces = new();
        img.TextContent = null;
        return img;
    }
}
