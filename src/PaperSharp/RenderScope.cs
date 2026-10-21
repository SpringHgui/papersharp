using SkiaSharp;

namespace PaperSharp;

/// <summary>
/// 一次渲染请求的作用域（线程/异步流隔离）。承载 BaseDir、本文档 @font-face 注册表
/// 与请求级字体缓存，使多个线程可同时调用 Renderer 而互不污染。
/// Renderer 的公共入口自动开启；手工驱动 StyleResolver/LayoutBuilder 的调用方
/// 用 <c>using RenderScope.Begin(baseDir)</c> 包裹即可。
/// </summary>
public sealed class RenderScope : IDisposable
{
    static readonly AsyncLocal<RenderScope?> _current = new();

    /// <summary>当前线程/异步流内最近的激活作用域；无则 null（回退全局注册表）。</summary>
    public static RenderScope? Current => _current.Value;

    /// <summary>相对资源路径的解析基准（HTML 所在目录），可为 null。</summary>
    public string? BaseDir { get; }

    // 本文档的 @font-face 注册：family → [(weight, 候选路径链)]。查找时优先于全局注册表。
    readonly Dictionary<string, List<(int weight, string[] paths)>> _fonts = new(StringComparer.OrdinalIgnoreCase);
    readonly object _fontsLock = new();

    // 请求级 typeface 缓存：只缓存"从 @font-face 文件加载出来的"字体（结果依赖本文档，不能进程共享，
    // 归本 Scope 所有并在 Dispose 时释放——Renderer 入口返回前绘制已全部完成，之后无人引用）。
    readonly Dictionary<(string, int), SKTypeface> _typefaces = new();
    readonly object _typefacesLock = new();

    private readonly RenderScope? _previous;

    private RenderScope(string? baseDir)
    {
        BaseDir = baseDir;
        _previous = _current.Value;
        _current.Value = this;
    }

    /// <summary>开启作用域，返回句柄；Dispose 时恢复外层（可嵌套）。</summary>
    public static RenderScope Begin(string? baseDir) => new(baseDir);

    internal void AddFont(string family, IEnumerable<string> paths, int weight)
    {
        lock (_fontsLock)
        {
            if (!_fonts.TryGetValue(family, out var list)) _fonts[family] = list = new();
            list.RemoveAll(e => e.weight == weight);
            list.Add((weight, paths.ToArray()));
        }
        List<SKTypeface> evicted = new();
        lock (_typefacesLock)
        {
            foreach (var k in _typefaces.Keys.Where(k => string.Equals(k.Item1, family, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _typefaces.Remove(k, out var old);
                if (old != null) evicted.Add(old);
            }
        }
        foreach (var t in evicted) t.Dispose();
    }

    /// <summary>取该 family 的注册候选（就近字重由调用方挑）；未注册返回 null。</summary>
    internal (int weight, string[] paths)[]? TryGetFaces(string family)
    {
        lock (_fontsLock)
            return _fonts.TryGetValue(family, out var list) && list.Count > 0 ? list.ToArray() : null;
    }

    /// <summary>factory 返回 null 表示"未命中注册文件"，不缓存（调用方自行回退到进程级解析）。</summary>
    internal SKTypeface? GetOrAddTypeface(string family, int weight, Func<SKTypeface?> factory)
    {
        var key = (family, weight);
        lock (_typefacesLock)
            if (_typefaces.TryGetValue(key, out var hit)) return hit;
        var created = factory();
        if (created == null) return null;
        lock (_typefacesLock)
        {
            if (_typefaces.TryGetValue(key, out var win)) { created.Dispose(); return win; }
            _typefaces[key] = created;
            return created;
        }
    }

    public void Dispose()
    {
        _current.Value = _previous;
        List<SKTypeface> owned;
        lock (_typefacesLock)
        {
            owned = _typefaces.Values.ToList();
            _typefaces.Clear();
        }
        foreach (var t in owned) t.Dispose();
    }
}
