using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace PaperSharp.Layout;

/// <summary>
/// 一张字面的**物理身份**：族名 + 字重 + 字宽 + 倾斜档 + 字形总数 + head 表校验和。
/// 倾斜档只在**字面本身真的带斜身**时入键：Skia 在要不到真斜面时，会把请求的 slant 值回填到
/// 拿回来的那张正立面上（见 <see cref="FontManager.FaceIsSlanted"/> 的注释），于是同一张物理面
/// 会以 s0/s1/s2 三个身份各存一份 18MB 的字体流。真有斜面的字族（Arial → Arial Italic）本来
/// 就是另一个文件、head 校验和不同，不靠这一档区分。
/// 为什么不用对象引用：同一张 msyh 会从 <c>FontManager.Get</c>、<c>SKFontManager.MatchCharacter</c>、
/// <c>SKTypeface.FromFamilyName</c> 好几条路各发一个实例出来，引用相等一个都认不出；
/// 而每个实例都**自带一整份字体流**（系统 CJK 面 12–19MB）。head 校验和足以区分
/// 「同名不同文件」（打包子集 vs 系统字面），取不到就退到前四项 + 字形数。
/// </summary>
internal readonly record struct FaceKey(string Family, int Weight, int Width, int Slant, int GlyphCount, uint HeadSum);

/// <summary>按字面实例算/缓存 <see cref="FaceKey"/>（ConditionalWeakTable：字面回收即随条目消失，不钉住原生内存）。</summary>
internal static class FaceIdentity
{
    static readonly ConditionalWeakTable<SKTypeface, Box> Keys = new();

    sealed class Box(FaceKey key) { public readonly FaceKey Key = key; }

    public static FaceKey Of(SKTypeface tf) => Keys.GetValue(tf, static t => new Box(Compute(t))).Key;

    static FaceKey Compute(SKTypeface t)
    {
        uint sum = 0;
        try
        {
            // head.checkSumAdjustment（偏移 8，4 字节大端）＝该字体的内容指纹，读一次 54 字节
            var head = t.GetTableData(0x68656164);   // 'head'
            if (head is { Length: >= 12 })
                sum = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
        }
        catch { /* 拿不到表：靠下面四项 + 字形数兜底 */ }
        int slant = FontManager.FaceIsSlanted(t) ? (int)t.FontSlant : (int)SKFontStyleSlant.Upright;
        return new FaceKey(t.FamilyName, t.FontWeight, t.FontWidth, slant, t.GlyphCount, sum);
    }
}
