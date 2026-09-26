using SkiaSharp;

namespace PaperSharp.Css;

/// <summary>
/// 一个元素"计算后的样式 + 结构信息"。由 StyleResolver 从 AngleSharp 的
/// IComputedStyle 转换而来，同时保留 DOM 树父子关系供布局遍历。
/// </summary>
public sealed class ComputedStyle
{
    // 结构
    public string Tag = "";
    public ComputedStyle? Parent;
    public List<ComputedStyle> Children = new();
    /// <summary>
    /// 该样式对应元素的 DOM 身份，形如 <c>body/div[1]/p[2]</c>（tag + 同标签兄弟序，含被跳过的兄弟）。
    /// 仅供比对类工具把引擎盒与浏览器 <c>getBoundingClientRect()</c> 对上位；
    /// 默认 null（零开销，见 StyleResolver.BuildDocument 的 domPath 开关），匿名盒恒为 null。
    /// </summary>
    public string? DomPath;
    /// <summary>该元素直接文本子节点拼起来（不含后代元素的文本）。</summary>
    public string? TextContent;
    /// <summary>&lt;img&gt; 的 src（文件路径 / data URI / URL）。</summary>
    public string? ImageSrc;
    /// <summary>&lt;a&gt; 的 href（PDF 链接注释 / 书签用），其余元素为 null。</summary>
    public string? LinkHref;

    /// <summary>&lt;input&gt; 的 type（text/tel/checkbox/radio…），其余元素为空。</summary>
    public string InputType = "";
    /// <summary>&lt;input&gt; 无值时以 placeholder 文本占位显示（用弱化色绘制）。</summary>
    public string? PlaceholderText;
    /// <summary>复选/单选框是否处于选中态（HTML checked 属性）。</summary>
    public bool InputChecked;

    /// <summary>有序行内片段（文本 / 行内子元素），仅用于 inline 格式化上下文，保留 DOM 顺序。</summary>
    public List<InlinePiece> InlinePieces = new();
    /// <summary>该块是否是纯 inline 格式化上下文（非 flex/table 块，含文本或行内元素，且无块级子元素）。</summary>
    public bool IsInlineFC;

    /// <summary>注入了行内伪元素（::before/::after 且 display 非 block）——强制 owner 走 inline 格式化上下文。</summary>
    public bool HasGeneratedInline;

    /// <summary>伪元素生成盒（未设 content 或 content:none/normal 时为 null）。counter 值在文档序后处理里回填。</summary>
    public PseudoGen? Before;
    public PseudoGen? After;

    /// <summary>CSS 计数器：counter-reset / counter-increment 原始声明值（后处理 pass 解析）。</summary>
    public string CounterReset = "";
    public string CounterIncrement = "";

    /// <summary>文档级元数据（&lt;title&gt;/&lt;meta&gt;），仅挂在 BuildDocument 返回的根节点上。</summary>
    public DocumentInfo? Document;

    /// <summary>
    /// 浅拷贝一份样式（供匿名 flex/grid 子项包装、伪元素生成盒用）；调用方自行重置 Children/InlinePieces。
    /// DomPath 一并清零：克隆出来的一律是匿名盒，不对应任何真实 DOM 元素，
    /// 留着会让盒级比对里同一个身份出现两次（后写覆盖，Δ 表就废了）。
    /// </summary>
    public ComputedStyle CloneShallow()
    {
        var c = (ComputedStyle)MemberwiseClone();
        c.DomPath = null;
        return c;
    }

    /// <summary>display 是否行内级（inline / inline-block）。</summary>
    public static bool IsInlineLevel(string display) => display is "inline" or "inline-block" or "inline-flex" or "inline-grid";

    /// <summary>
    /// 原子行内级盒：自身按行内参与行盒，内部另立格式化上下文（尺寸由内容决定，不随父宽拉伸）。
    /// inline-table 也算；纯 inline 不算（它没有自己的盒尺寸）。
    /// </summary>
    public static bool IsAtomicInlineLevel(string display)
        => display is "inline-block" or "inline-flex" or "inline-grid" or "inline-table";

    /// <summary>是否为 flex 容器（含 inline-flex）：布局测量按 flex 走。</summary>
    public static bool IsFlexDisplay(string display) => display is "flex" or "inline-flex";
    /// <summary>是否为 grid 容器（含 inline-grid）：布局测量按 grid 走。</summary>
    public static bool IsGridDisplay(string display) => display is "grid" or "inline-grid";

    // 布局开关
    public string Display = "block"; // block | flex | none | inline | inline-block | table | table-row | table-cell | ...
    public string BoxSizing = "content-box"; // content-box | border-box

    // 表格（Display 为 table 家族时生效）
    public string BorderCollapse = "separate"; // separate | collapse
    public float BorderSpacing;                // separate 时单元格横向间距（px）
    public float BorderSpacingV;               // separate 时单元格纵向间距；border-spacing 只写一个值时同 BorderSpacing
    public int Colspan = 1;                    // <td>/<th> 的 colspan
    public int Rowspan = 1;                    // <td>/<th> 的 rowspan
    public string CaptionSide = "top";         // top | bottom

    // Flexbox（Display == "flex" 时生效；子项用 FlexGrow/Shrink/Basis/AlignSelf）
    public string FlexDirection = "row";   // row | column | row-reverse | column-reverse
    public string FlexWrap = "nowrap";     // nowrap | wrap | wrap-reverse
    public string JustifyContent = "flex-start"; // flex-start|center|flex-end|space-between|space-around|space-evenly
    public string AlignItems = "stretch";  // stretch|flex-start|center|flex-end
    public float FlexGrow = 0f;
    public float FlexShrink = 1f;
    public CssSize FlexBasis;              // auto | 绝对 | 百分比
    public CssSize Gap;                    // 主轴/交叉轴统一间距（auto|绝对|百分比，百分比按内容宽解析）
    public string AlignSelf = "auto";      // auto|stretch|flex-start|center|flex-end
    public string JustifySelf = "auto";    // grid 子项主轴（行内轴）自对齐：auto|stretch|start|center|end
    public string JustifyItems = "stretch";// 容器给子项的主轴默认（grid）
    public string AlignContent = "normal"; // 多轨交叉轴分布（flex wrap / grid）

    // Grid（Display == "grid" 时生效）
    public string GridTemplateColumns = ""; // 轨道定义，如 "100px 200px auto" 或 "1fr 2fr"
    public string GridTemplateRows = "";    // 轨道定义
    public CssSize ColumnGap;               // 列间距
    public CssSize RowGap;                  // 行间距
    public int GridColumnStart = 0;         // 网格项起始列（1-based，0=auto）
    public int GridColumnEnd = 0;           // 网格项结束列（1-based，0=auto）
    public int GridRowStart = 0;            // 网格项起始行
    public int GridRowEnd = 0;              // 网格项结束行
    public int GridColumnSpan = 1;          // 跨列数
    public int GridRowSpan = 1;             // 跨行数

    // 盒模型（已解析 px，供布局/绘制直接消费）
    public float MarginTop, MarginRight, MarginBottom, MarginLeft;
    public float PaddingTop, PaddingRight, PaddingBottom, PaddingLeft;
    public float BorderWidthTop, BorderWidthRight, BorderWidthBottom, BorderWidthLeft;
    /// <summary>四边各自的边框色。单边简写（`border-right: 1px solid #eceef0`）只落到 border-right-color，
    /// 四边共用一色时读不到它，只能退回 currentColor——登录框的两条分隔线因此被染成文字色。</summary>
    public SKColor BorderColorTop = SKColors.Black, BorderColorRight = SKColors.Black,
                   BorderColorBottom = SKColors.Black, BorderColorLeft = SKColors.Black;
    /// <summary>圆角描边与 SVG 只能整圈一色：取最宽那条边的颜色（与 BoxPainter 的宽度取最大、DominantDash 同一口径）。</summary>
    public SKColor BorderColor =>
        BorderWidthTop >= Math.Max(BorderWidthRight, Math.Max(BorderWidthBottom, BorderWidthLeft)) ? BorderColorTop
      : BorderWidthRight >= Math.Max(BorderWidthBottom, BorderWidthLeft) ? BorderColorRight
      : BorderWidthBottom >= BorderWidthLeft ? BorderColorBottom
      : BorderColorLeft;
    /// <summary>border-*-style 用值。目前只区分 dashed/dotted（画虚线），其余一律按 solid；
    /// 默认 solid 是有意的：快照路径（capture.js）不下发 style，缺值不能把实线画没。</summary>
    public string BorderStyleTop = "solid", BorderStyleRight = "solid", BorderStyleBottom = "solid", BorderStyleLeft = "solid";

    // margin / padding 的**声明值**（可能是 %）；布局期用 ResolveBoxSizes 按包含块宽折算进上面的 float。
    // border-width 无百分比（CSS 不接受），故只有 float。
    public CssSize MarginTopCss, MarginRightCss, MarginBottomCss, MarginLeftCss;
    public CssSize PaddingTopCss, PaddingRightCss, PaddingBottomCss, PaddingLeftCss;

    // 作者是否显式写了 auto 边距（区别于未声明）：margin:auto 居中/靠右判定用这个，别用 CssSize.IsAuto。
    public bool MarginTopAuto, MarginRightAuto, MarginBottomAuto, MarginLeftAuto;

    /// <summary>
    /// 把 margin/padding 的声明值按包含块行内尺寸（宽）解析成 px，写入对应的 float 字段。
    /// CSS 规定 margin/padding 的百分比横竖都相对包含块**宽**，故统一用 <paramref name="containingBlockWidth"/>。
    /// auto / 无法解析（父宽未知）→ 0。布局期 Measure 拿到 availWidth 后调用一次（幂等）。
    /// </summary>
    public void ResolveBoxSizes(float containingBlockWidth)
    {
        MarginTop = MarginTopCss.ResolveEdge(containingBlockWidth);
        MarginRight = MarginRightCss.ResolveEdge(containingBlockWidth);
        MarginBottom = MarginBottomCss.ResolveEdge(containingBlockWidth);
        MarginLeft = MarginLeftCss.ResolveEdge(containingBlockWidth);
        PaddingTop = PaddingTopCss.ResolveEdge(containingBlockWidth);
        PaddingRight = PaddingRightCss.ResolveEdge(containingBlockWidth);
        PaddingBottom = PaddingBottomCss.ResolveEdge(containingBlockWidth);
        PaddingLeft = PaddingLeftCss.ResolveEdge(containingBlockWidth);
    }

    // 尺寸（auto | 绝对 | 百分比）
    public CssSize Width;
    public CssSize Height;
    public CssSize MinWidth, MaxWidth, MinHeight, MaxHeight;

    // 定位
    public string Position = "static";   // static | relative | absolute | fixed | sticky
    public CssSize OffsetTop, OffsetRight, OffsetBottom, OffsetLeft; // % 相对包含块（Position 期解析）
    public int ZIndex = 0;               // 层叠顺序
    public bool HasZIndex = false;       // 是否显式设置了 z-index

    // 浮动
    public string Float = "none";        // none | left | right
    public string Clear = "none";        // none | left | right | both

    /// <summary>
    /// 溢出裁剪开关（由 <c>overflow</c> / <c>overflow-x</c> / <c>overflow-y</c> 折算而来）。
    /// 静态文档没有滚动条也不画滚动条：hidden | clip | auto | scroll | overlay 一律按「裁剪到自身
    /// padding box」处理（见 <c>BlockBox.OverflowClip</c>）。规范规定一轴 visible 而另一轴非 visible
    /// 时，visible 那轴折算成 auto → 两轴同裁，所以这里不保留分轴语义（真要单轴裁剪时再拆）。
    /// &lt;html&gt; 上的 overflow 到不了这里：按 CSS 它传播给视口，而本引擎的「视口」就是整页/长图本身。
    /// </summary>
    public bool ClipsOverflow;

    // 文本高级
    public string VerticalAlign = "baseline"; // baseline | top | bottom | middle | length/percentage（原子盒与表格单元生效）
    public string TextWrap = "wrap";         // wrap | balance
    public string TextFit = "none";          // none | scale-down（来自 HTML text-fit 属性，AngleSharp 不识别该 CSS 属性）

    // 视觉
    public SKColor Color = SKColors.Black;
    public SKColor BackgroundColor = SKColor.Empty;
    public GradientSpec? Background;     // background-image: linear/radial-gradient(...)
    public string BackgroundClip = "border-box"; // border-box | padding-box | text（text：背景裁剪到字形=渐变文字）
    /// <summary>box-shadow 的每一层（作者书写序，第一条画在最上层）。inset 层画在盒内。</summary>
    public List<BoxShadow>? Shadows;
    public float BorderRadiusTopLeft, BorderRadiusTopRight, BorderRadiusBottomRight, BorderRadiusBottomLeft;
    /// <summary>border-radius 每角的 **y 轴**半径（绝对部分，px）。x 轴用上面四个字段；两轴不等即椭圆角
    /// ——CSS 的 `border-radius:50%` 作用在长方盒上就是椭圆，只存一个值会退化成胶囊。</summary>
    public float BorderRadiusTopLeftY, BorderRadiusTopRightY, BorderRadiusBottomRightY, BorderRadiusBottomLeftY;
    /// <summary>border-radius 的百分比分量，顺序 TLx,TLy,TRx,TRy,BRx,BRy,BLx,BLy（小数）。
    /// 百分比按**边框盒**的宽/高折算，而样式阶段还没有盒尺寸，故留到绘制期由 BoxPainter.CornerRadii 落地。
    /// null = 一条百分比都没写过。</summary>
    public float[]? BorderRadiusPercent;
    public float Opacity = 1f;

    // 视觉增强（M3）
    public string MixBlendMode = "normal";   // normal | multiply | screen | overlay | darken | lighten ...
    public string CornerShape = "round";     // round | squircle（配合 border-radius）
    public string? ClipPath;                 // clip-path 原始值（inset/circle/polygon）
    public GradientSpec? MaskImage;          // mask-image: linear-gradient(...)（alpha 用渐变）
    public CssFilter? Filter;                // filter: blur() / drop-shadow()（前景）
    public CssFilter? BackdropFilter;        // backdrop-filter: 模糊/滤镜作用于盒背后的已绘内容（仅光栅/PNG 路径）
    public string? SvgMarkup;                // 内联 <svg> 的标记（替换元素渲染）

    // 文本
    public string FontFamily = "sans-serif";
    public float FontSize = 14f;
    public int FontWeight = 400;
    public float LineHeightFactor = 1.4f;
    /// <summary>CSS 是否显式给出数字/百分比行高。false = normal（走字体度量，浏览器对它不做 1/64 量化）。</summary>
    public bool LineHeightNumeric;
    /// <summary>带单位的行高（<c>line-height: 46px</c> / <c>1.5em</c>）折算成的绝对像素；NaN = 作者没写长度行高。
    /// 浏览器里这类行高**不乘字号**，直接就是行盒高——按 normal 兜底会让定高盒里的文字整条基线抬高
    /// (L−normal)/2（m9 的 <c>height:46px; line-height:46px</c> 实测偏 11px，叠图上一眼可见的重影）。</summary>
    public float LineHeightAbsPx = float.NaN;

    /// <summary>
    /// used line-height。Blink 的口径：font-size 先量化到 1/64px（就近），再对「量化后的 fs × 倍数」
    /// 向下取到 1/64px。13.3333×1.6 → 21.3125 而不是 21.3333——每行 −0.0208px 看着无害，长文档里
    /// 逐行累积到页面下部就是 2~3px 的整体纵向漂移（m1 探针实测：td 高 38.3125 = 8+8+1+21.3125）。
    /// </summary>
    public float LineHeightPx()
    {
        if (!float.IsNaN(LineHeightAbsPx)) return LineHeightAbsPx;
        if (!LineHeightNumeric) return FontSize * LineHeightFactor;
        double fs = Math.Round((double)FontSize * 64d, MidpointRounding.AwayFromZero) / 64d;
        return (float)(Math.Floor(fs * LineHeightFactor * 64d + 1e-6d) / 64d);
    }
    public CssTextAlign TextAlignment = CssTextAlign.Left;
    /// <summary>text-indent 折成 px：只作用于块的首行，可为负（列表 marker 的悬挂缩进就靠它）。
    /// 浏览器口径是首行**起点**平移 indent、首行**可用宽**随之变 <c>width - indent</c>（负值⇒首行更宽），
    /// 所以它必须同时进折行与绘制，只补绘制位移会让首行少排一个字。</summary>
    public float TextIndent;
    /// <summary>text-shadow 的每一层（复用 BoxShadow：dx/dy/blur/color，spread 恒 0）。画在字形下偏处。</summary>
    public List<BoxShadow>? TextShadow;
    public string TextDecoration = "none";   // none | underline | line-through | overline
    public float LetterSpacing;              // px
    public string WhiteSpace = "normal";     // normal | nowrap | pre | pre-wrap | pre-line
    public string TextTransform = "none";    // none | uppercase | lowercase | capitalize
    public string TextOverflow = "clip";     // clip | ellipsis
    public string WordBreak = "normal";      // normal | break-all | keep-all
    public string Direction = "ltr";         // ltr | rtl（CSS direction：驱动 bidi 重排与默认对齐）

    // 列表标记（list-style-* 简写/长写 + <ol type>/<ol start> 的 presentational hint）
    public string ListStyleType = "disc";    // disc|circle|square|decimal|lower-alpha|upper-alpha|lower-roman|upper-roman|none
    public bool ListStyleInside;             // list-style-position: inside（false=outside：marker 落在负 text-indent 让出的悬挂区）
    public int ListStartOffset;              // <ol start="n"> → n-1（序号 = 该偏移 + 本项在列表中的第几项）
    public bool ListStyleAuthorSet;          // 作者写过 list-style*：<ol type> 只能在没写时覆盖它
    /// <summary>本元素自己（或经继承落到本元素上的）list-style-type 声明原文。子元素用它辨别
    /// "这条声明是我写的还是从父亲抄来的"——AngleSharp 的声明值里两者长得一样。</summary>
    public string ListStyleSpecified = "";

    // 分页控制（打印媒体 / @page 上下文）
    public string BreakInside = "auto";      // auto | avoid
    public string BreakBefore = "auto";      // auto | page
    public string BreakAfter = "auto";       // auto | page
    public int Widows = 2;                   // 段落在分页后底部保留的最少行数
    public int Orphans = 2;                  // 段落在分页后顶部保留的最少行数

    // 动画（确定性取帧）：animation 简写解析结果。仅当渲染传入 animationTime 时按该时刻求值覆盖样式。
    public string AnimationName = "";        // 关联的 @keyframes 名（none/空 → 无动画）
    public float AnimationDuration = 0f;     // 秒
    public float AnimationDelay = 0f;        // 秒
    public string AnimationTiming = "ease";  // ease | linear | ease-in | ease-out | ease-in-out | cubic-bezier() | steps()
    public float AnimationIteration = 1f;    // 次数；infinite → float.PositiveInfinity
    public string AnimationDirection = "normal"; // normal | reverse | alternate | alternate-reverse
    public string AnimationFill = "none";    // none | forwards | backwards | both
}

public enum CssTextAlign { Left, Center, Right }

/// <summary>从 HTML 头部抽出的文档元数据（PDF 元数据用），随根 ComputedStyle 返回，不做全局态。</summary>
public sealed record DocumentInfo(string? Title, string? Author, string? Description, string? Keywords);

/// <summary>inline 序列里的一段：文本（Owner 的样式）或一个行内子元素（引用其 ComputedStyle）。</summary>
public sealed class InlinePiece
{
    public string? Text;
    public ComputedStyle? Child;
    public bool IsBreak;   // <br>
    public static InlinePiece OfText(string t) => new() { Text = t };
    public static InlinePiece OfChild(ComputedStyle c) => new() { Child = c };
    public static readonly InlinePiece Break = new() { IsBreak = true };
}
