using System.Reflection;

namespace PaperSharp.Fonts;

/// <summary>
/// 读 dll 内嵌的打包子集字体（csproj 里 LogicalName 写死的那 5 份）。
///
/// 为什么嵌资源而不是「拷到输出目录」：字体按路径找的是**入口程序集**旁边那份
/// （<c>AppContext.BaseDirectory/assets/fonts</c>），库自己说了不算——
/// 于是 NuGet 消费方（字体根本没进包）静默退回系统字体，同一份 HTML 体积差 2.9~12.7 倍，
/// 而「PDF 体积 + 跨平台一致」正是这批子集存在的唯一理由。见 PERF_REPORT §12.6。
///
/// 字节整份缓存一次（8 MB 级、5 张 face），因为字距表 PRKT 也要从同一份 sfnt 里读，
/// 只交给 Skia 一份副本会把它丢掉。
/// </summary>
static class Bundled
{
    static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.Ordinal);
    static readonly object Lock = new();

    /// <summary>取内嵌字体字节；没有该资源返回 null（不抛——让调用方走降级并给出诊断）。</summary>
    public static byte[]? Read(string logicalName)
    {
        lock (Lock)
        {
            if (Cache.TryGetValue(logicalName, out var hit)) return hit;
            byte[]? bytes = null;
            try
            {
                var asm = typeof(Bundled).Assembly;
                using var s = asm.GetManifestResourceStream(logicalName);
                if (s is not null)
                {
                    bytes = new byte[s.Length];
                    s.ReadExactly(bytes);
                }
            }
            catch { bytes = null; }
            Cache[logicalName] = bytes;
            return bytes;
        }
    }

    /// <summary> dll 里到底有没有这批字体（自检/诊断用，不加载字体）。
    /// 打包配置被改坏时，这里会先变 false，而不是等到渲染时静默退回系统字体。</summary>
    public static bool Present(string logicalName) => Read(logicalName) is { Length: > 0 };
}
