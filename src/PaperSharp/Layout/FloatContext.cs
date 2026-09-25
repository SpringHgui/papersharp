namespace PaperSharp.Layout;

/// <summary>一个浮动区。Top/Bottom 为测量期全局 y；Edge 为占据宽度：左浮动=距容器内容左缘的距离，右浮动=距右缘的距离。</summary>
public sealed class FloatRegion
{
    public bool IsLeft;
    public float Top, Bottom, Edge;
}

/// <summary>
/// 浮动上下文：含浮动子盒的块容器创建一个，Parent 链到外层容器（float 穿透到后续兄弟块的内容行）。
/// 折行时按行的全局 y 查询左右缩进；x 方向假设嵌套块与建立容器共用内容左/右缘（无左内边距的常规块成立）。
/// </summary>
public sealed class FloatContext
{
    public FloatContext? Parent;
    public List<FloatRegion> Own = new();

    /// <summary>区间 [y, y+h) 与浮动区有交叠时，左右各被占据的最小宽度。</summary>
    public (float left, float right) Insets(float y, float h)
    {
        float l = 0, r = 0;
        for (var c = this; c != null; c = c.Parent)
            foreach (var g in c.Own)
                if (g.Bottom > y && g.Top < y + h)
                {
                    if (g.IsLeft) l = Math.Max(l, g.Edge);
                    else r = Math.Max(r, g.Edge);
                }
        return (l, r);
    }

    /// <summary>clear 推进：把 y 推到所有（含祖先）同侧浮动区之下。</summary>
    public float ClearTo(bool wantLeft, bool wantRight, float y)
    {
        for (var c = this; c != null; c = c.Parent)
            foreach (var g in c.Own)
                if (g.Bottom > y && (wantLeft && g.IsLeft || wantRight && !g.IsLeft))
                    y = Math.Max(y, g.Bottom);
        return y;
    }

    /// <summary>
    /// 放置一个浮动盒（CSS 2.1 §9.5.1）：在候选 top 的那条带上贴着同侧已占边缘排布，**只有放不下才下移**。
    /// 「同侧有交叠就下移」是错的：两个 120px 的左浮动并排放得下，浏览器给同一行 x=0/x=120，
    /// 老实现却把它们摞成两行（第二个 top 被推到第一个的底边），容器撑高也因此从 60 变 100。
    /// 返回 margin-box 左上角（x 相对本容器内容左缘，y 全局）并登记浮动区。
    /// </summary>
    public (float x, float top) Place(bool isLeft, float containerW, float wantTop, float totalW, float totalH)
    {
        float top = wantTop;
        float edge = 0;
        while (true)
        {
            edge = 0;                       // 同带内同侧已占边缘（左浮动=距左缘，右浮动=距右缘）
            float other = 0;                // 异侧已占的宽度
            float descend = float.NaN;      // 让位时要落到的最低底边（**两侧都算**：被对岸挤下也要越过对岸）
            foreach (var g in Own)
                if (g.Bottom > top && g.Top < top + totalH)
                {
                    if (g.IsLeft == isLeft) edge = Math.Max(edge, g.Edge);
                    else other = Math.Max(other, g.Edge);
                    if (float.IsNaN(descend) || g.Bottom > descend) descend = g.Bottom;
                }
            if (totalW <= containerW - edge - other || float.IsNaN(descend)) break;
            top = descend;                  // 本带被占满：落到最矮可让位处再试
        }

        float x = isLeft ? edge : Math.Max(0, containerW - totalW - edge);
        float regionEdge = isLeft ? x + totalW : containerW - x;
        Own.Add(new FloatRegion { IsLeft = isLeft, Top = top, Bottom = top + totalH, Edge = regionEdge });
        return (x, top);
    }

    /// <summary>本容器全部浮动区，y 转成相对容器内容顶（供定位期复现 clear 推进）。</summary>
    public (bool IsLeft, float TopRel, float BottomRel)[] BandsRel(float contentTopY)
    {
        var list = new List<(bool, float, float)>();
        for (var c = this; c != null; c = c.Parent)
            foreach (var g in c.Own)
                list.Add((g.IsLeft, g.Top - contentTopY, g.Bottom - contentTopY));
        return list.ToArray();
    }
}
