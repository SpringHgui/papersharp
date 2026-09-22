# PaperSharp · 最小可运行 demo

> **HTML/CSS → PDF & PNG, without a browser.**

<!-- 徽章位：发布 NuGet / 接 CI / 定 license 后，把占位徽章的 label 与 (#) 换成真实链接即可 -->
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)](#)
[![No headless browser](https://img.shields.io/badge/no--headless--browser-%E2%9C%93-brightgreen)](#)
[![License](https://img.shields.io/badge/license-TBD-yellow)](#)
[![Build](https://img.shields.io/badge/build-CI--placeholder-lightgrey)](#)
[![NuGet](https://img.shields.io/badge/NuGet-not--yet--published-lightgrey)](https://www.nuget.org/packages?q=PaperSharp)

**一段 HTML + 一段 CSS → A4 PDF + PNG**，跨平台。用 **SkiaSharp + AngleSharp.Css + 手写的 block-flow 布局层**。

对应之前讨论的方案：
- **AngleSharp + AngleSharp.Css** 负责 HTML 解析、CSS 层叠、选择器匹配、`GetComputedStyle`
- **SkiaSharp** 负责字体读取、文本 shaping（含中文）、光栅化、PDF 输出
- **自己写的**：Block 流布局（盒模型 margin/border/padding）、文本折行（Latin 按空格 / CJK 按字符）、**块级分页引擎**、绘制遍历

已实测解决的核心能力：
- **PDF 体积**：构建期用 `fonttools` 把系统雅黑裁成子集（18.8MB→1.77MB），运行时 `SKTypeface.FromFile` 加载，PDF 从 **23MB 降到 2.5MB**。
- **跨页自动分页**：`Paginator` 按可用页高回流块级内容，长容器跨页重复边框/背景、长段落按行拆到续页（`assets/long.html` 26 行明细自动分成 2 页）。
- **Flexbox 布局**：`LayoutBuilder` 支持 `display:flex` + `flex-direction`（row/column）+ `justify-content`（6 种）+ `align-items`/`align-self`（含 stretch）+ `flex-grow/shrink/basis` + `gap`，可嵌套（`assets/flex.html` 演示 5 种典型布局）。
- **`<img>` 图片**：`ImageLoader` 解码 + 缓存，支持相对/绝对路径与 `data:` URI；替换元素按 CSS 尺寸 + 自然尺寸定 content 盒（只给一个维度时等比缩放），可作 block 或 flex item，跨页时原子不拆分（`assets/img.html` 演示三种尺寸模式 + 图文混排 + 内联 base64）。
- **`@page` 页边距 + 页眉页脚页码**：`AtPageParser` 解析 `size` / `margin` / 嵌套 `@top-*` `@bottom-*` 边距盒，`MarginBoxRenderer` 每页渲染并替换 `counter(page)`/`counter(pages)`（`assets/report.html` 演示跨页 + "第 N 页 / 共 M 页"）。
- **`%` 百分比单位**：`CssSize`（Auto/Absolute/Percent）承载未解析尺寸，布局期按**包含块**折算（`width%`/`height%`/`flex-basis%`/`gap%`/`margin%`/`padding%`/`font-size%`/`line-height%`）。绕开了 AngleSharp 把 `%` 按视口折算的坑（改用 `GetDeclarations` 取声明值），顺带修好了 margin/padding 因读 computed 简写返回空串而被丢成 0 的隐藏 bug。`assets/pct.html` 演示两栏 + 条形图 + 定高容器撑满 + margin/padding 缩进。
- **第二梯队视觉/排版**：`border-radius` / `box-shadow` / 线性·径向渐变 / `opacity` / `min-max-width/height` / `position: relative|absolute`（含负偏移、absolute 出流收缩适配、盖顶绘制）/ `text-decoration` / `letter-spacing` / `white-space`(nowrap/pre/pre-wrap) + `text-overflow:ellipsis` / `text-transform` / `word-break` / `@font-face`。`assets/polish.html` 一页演示全部（PNG + 分页 PDF 两条路径都验过）。
- **`box-sizing: border-box`**：`width`/`height` 连同 `min/max` 在块路径、flex 主轴、`img`、表格单元格上都按盒模型语义折算（border-box 时先扣 padding+border 再当内容尺寸用）。既有 7 样例字节不变（它们全用 content-box），新增 `assets/pct.html`/`table.html` 里可见 border-box 生效。
- **inline 格式化上下文**：`<p>文本 <span>/<strong>/<em>/<a>/<u> <br> 行内 <img> inline-block 药丸</p>` 这类混排真正按行流动——`InlineLayout` 把块内有序片段摊平成原子序列（文本 run / `<br>` / inline-block 与行内 img 作原子盒），按宽度折成行盒（基线对齐、`text-align`、行高取行内最大字号、CJK/word-break 断词、white-space 折叠），每段带自己的字体/颜色/背景/字距/装饰线绘制。纯文本块仍走老的 `WrappedLines` 路径以保证零回归；只有含行内元素/`<br>`/图文混排的块才切到新引擎。`assets/inline.html` 演示全部（PNG + 2 页 PDF 都验过）。
- **`<table>` 布局**：`display: table/table-row-group/table-header-group/table-footer-group/table-row/table-cell` + `<td colspan>`，列宽按「单跨单元格 max-content 定基准、多跨不足均摊、显式 `width` 或超可用宽时按比例缩放」自动分配；`border-collapse: collapse`（内部只留 top+left 去双线）与 `separate`（`border-spacing`）两种边框模型；单元格撑满行高（行背景/斑马纹可见）、格内可放 inline 混排。**暂不支持** `rowspan`、跨页断行（表格整块放置）、`table-caption`、列宽 `%`。`assets/table.html` 演示合并/分离边框 + colspan + 单元格内行内混排（PNG + PDF 都验过）。

## 目录结构

```
paper-sharp/
├── PaperSharp.csproj
├── Program.cs                      # 入口：渲染 invoice + long + flex + img + report + pct + polish + inline + table 九个样例
├── run.bat                         # 双击一键 restore+run
├── tools/
│   └── subset-font.py              # 构建期字体子集化（fonttools）
├── src/
│   ├── Renderer.cs                 # 顶层 API：RenderPdf（分页+页眉页脚）/ RenderPng（单页长图）
│   ├── Css/
│   │   ├── StyleResolver.cs        # AngleSharp 桥接 + UA 样式表 + 递归提取 + 不支持属性兜底
│   │   ├── ComputedStyle.cs        # 我们自己的样式模型（结构 + 已解析值 + 声明值）
│   │   ├── CssSize.cs              # 尺寸值对象（Auto/Absolute/Percent），布局期按包含块 Resolve
│   │   ├── CssValueParser.cs       # px/em/pt/%/负值、rgb()/hex、border-radius/box-shadow/gradient/opacity 等
│   │   ├── Visual.cs               # BoxShadow / GradientSpec（渐变在矩形上建 SKShader）
│   │   ├── FontFaceParser.cs       # @font-face 块解析 → 登记 family→文件
│   │   └── PageSpec.cs             # @page 解析（size/margin/边距盒/counter）
│   ├── Layout/
│   │   ├── BlockBox.cs             # 布局盒数据结构（含 inline 原子/行盒、表格网格中间态）
│   │   ├── LayoutBuilder.cs        # ToBoxTree / Measure / Position（block + flex + img + relative/absolute + min/max + box-sizing + table）
│   │   ├── InlineLayout.cs         # inline 格式化上下文：片段摊平 + 按宽折行盒（基线/对齐/断词/white-space）
│   │   ├── Paginator.cs            # 块级分页引擎（跨页回流 + 碎片化 + inline 按行拆 + absolute 盖顶列表；表格整块放置）
│   │   ├── TextMeasurer.cs         # 折行（Latin + CJK + white-space + word-break + letter-spacing）
│   │   ├── ImageLoader.cs          # 图像解码 + 缓存（路径 / data URI）
│   │   └── FontManager.cs          # 子集字体优先 + @font-face + 系统字体跨平台回退
│   └── Paint/
│       ├── Painter.cs              # 单页长图绘制（PNG 用，先画在流再画 absolute；inline 行盒 + 原子盒）
│       ├── PagePainter.cs          # 分页绘制（PDF 用，消费 DrawItem 列表 + AbsItems）
│       ├── BoxPainter.cs           # 盒装饰：圆角/阴影/渐变/边框/opacity（两路径共用）
│       ├── TextPainter.cs          # 文本绘制：对齐/字距/装饰线/opacity + inline 行盒逐段样式（两路径共用）
│       └── MarginBoxRenderer.cs    # 页眉/页脚边距盒 + 页码渲染
├── assets/
│   ├── invoice.html / invoice.css  # 单页样例
│   ├── long.html / long.css        # 多页样例（26 行明细，演示分页）
│   ├── flex.html / flex.css        # flexbox 样例（5 种典型布局）
│   ├── img.html / img.css          # 图片样例（三种尺寸模式 + 图文混排 + data URI）
│   ├── report.html / report.css    # @page 样例（页边距 + 页眉页脚页码，跨 2 页）
│   ├── pct.html / pct.css          # % 单位样例（width%/height%/font-size%/gap%/margin%/padding% + 条形图）
│   ├── polish.html / polish.css    # 第二梯队样例（圆角/阴影/渐变/透明度/文本装饰/字距/截断/定位/min-max/@font-face）
│   ├── inline.html / inline.css    # inline 格式化上下文样例（行内 run/图文混排/br/inline-block 药丸/对齐/长段折行）
│   ├── table.html / table.css      # 表格样例（合并/分离边框 + colspan + 斑马纹 + 单元格内行内混排）
│   ├── imgs/                       # 测试图（logo/photo/qr.png）
│   └── fonts/                      # 子集化产物（YaheiSub-Regular/Bold.ttf）
└── output/                         # 生成物（九个样例各自的 pdf + png）
```

## 跑起来

```
cd paper-sharp
python tools/subset-font.py   # 一次性：裁字体子集（需 pip install fonttools）
dotnet restore
dotnet run
```

或者直接双击 `run.bat`（不含字体子集化那步）。产物在 `bin/Debug/net8.0/output/`。

> 没跑 `subset-font.py` 也能运行——`FontManager` 找不到子集文件时自动退回系统字体，只是 PDF 会大（整份嵌入雅黑 ~20MB）。

## 支持的 CSS 子集

**选择器**：type / `#id` / `.class` / `.a.b`（复合）/ 后代空格 / `>` 子代 / 属性选择器 — 全部由 AngleSharp.Css 提供，跟浏览器同源。

**盒模型**：`width` / `height`（px/pt/em/rem/%）、`min/max-width` / `min/max-height`（含 %，块路径与 flex 主轴都夹取）、`margin` / `padding`（1-4 值简写或 longhand，px/pt/em/rem/%，**负值支持**）、`border-*-width`（px/em，**不接受 %**）、`border-color`、`box-sizing: content-box|border-box`（border-box 时 `width`/`height`/`min`/`max` 连同 padding+border 一起算，块/flex/img/表格单元格一致处理）。`width`/`height`/`flex-basis` 的 `%` 在布局期按**包含块**解析（宽度% 相对父内容宽、高度% 相对父确定高，父高 auto 时% 高退化为 auto）；`margin`/`padding` 的 `%` 按 CSS 规范一律相对**包含块宽度**（横竖同基准），由 `CssSize` 承载、`ComputedStyle.ResolveBoxSizes` 在 `Measure` 里折算。

**Flexbox**：`display:flex`、`flex-direction`（row/column）、`justify-content`（flex-start/center/flex-end/space-between/space-around/space-evenly）、`align-items`/`align-self`（stretch/flex-start/center/flex-end）、`flex-grow`/`flex-shrink`/`flex-basis`（含 %）/`flex`、`gap`（px 或 %，% 按容器内容宽解析）。支持嵌套。**暂不支持** `flex-wrap`、`row-reverse`/`column-reverse`。

**定位**：`position: static|relative|absolute` + `top/right/bottom/left`（px/em/%，**支持负值**）。`relative` 在流内位置基础上按偏移挪动；`absolute` 出流（不占流内空间、`width:auto` 时按内容收缩 shrink-to-fit），相对**最近定位祖先**的 padding box 定位（无则相对 body），`left`/`right` 与 `top`/`bottom` 各自取先出现的一端。absolute 盒在两条绘制路径里都最后画（盖顶）。**未做** `fixed`/`sticky`、z-index 层叠上下文、`left+right` 同时拉伸宽度。

**视觉**：`background-color`、`color`、`opacity`（0–1，作用于背景+边框+文本+图片）、`border-radius`（1–4 值，圆角背景 + 圆角边框描边）、`box-shadow`（单层 `offset-x offset-y [blur [spread]] color`，高斯模糊投影）、`background-image: linear-gradient()` / `radial-gradient()`（方向支持 `to 方位`/`<deg>`，2+ 色标含可选百分比位置）。

**图片**：`<img>` 替换元素，`src` 支持相对/绝对文件路径与 `data:image/...;base64,` URI；`width`/`height` 三种模式（都给=拉伸、只给一个=等比缩放、都不给=自然尺寸且超容器等比缩小）。可作 block 或 flex item。

**文本**：`font-family`、`font-size`（px/pt/em/rem/%，% 相对父字号）、`font-weight`、`line-height`（无单位倍数或 %，% 折算成对字号的倍数）、`text-align`（left/center/right）、`text-decoration`（underline/line-through/overline）、`letter-spacing`（px/em，含负值，逐字排布）、`text-transform`（uppercase/lowercase/capitalize）、`white-space`（normal/nowrap/pre/pre-wrap/pre-line）、`text-overflow: ellipsis`（配合 nowrap + 固定宽，超宽截断加省略号）、`word-break`（normal/break-all/keep-all）。

**inline 格式化上下文**：`display: inline`（UA 已给 `span/a/em/i/strong/b/u/…` 补齐）与 `inline-block`。`InlineLayout` 把块内有序片段（文本 / `<br>` / 行内元素 / 行内 `<img>`）摊平成原子序列，按内容宽折成行盒：CJK 逐字、Latin 逐词、`word-break`/`white-space` 生效、连续空白折叠、行高取行内最大字号、基线对齐、`text-align` 作用于整行、`inline-block` 与行内 `<img>` 作原子盒（auto 宽 shrink-to-fit）。每段按自己的字体/颜色/背景/字距/装饰线绘制。**边界**：只有含行内元素/`<br>`/图文混排的块才走此引擎，纯文本块仍走 `WrappedLines`；`vertical-align` 未做（原子盒按基线底对齐）；跨页时含原子盒的 inline 块整块放置（纯文本 inline 块可按行拆）。

**表格**：`display: table/table-row-group/table-header-group/table-footer-group/table-row/table-cell`（UA 已给 `<table>/thead/tbody/tfoot/tr/td/th` 补齐）+ `<td colspan>`。列宽自动分配（单跨 max-content 定基准、多跨不足均摊、显式 `width` 或超可用宽按比例缩放）；`border-collapse: collapse`（内部仅留 top+left 去双线）/ `separate`（`border-spacing`）；单元格撑满行高使行背景/斑马纹可见；单元格内容可放 inline 混排。**暂不支持** `rowspan`、跨页断行（整表原子放置）、`table-caption`、列宽 `%`/`<col>`。

**`@font-face`**：`font-family` + `src: url(...)`（相对 `baseDir` 解析）+ `font-weight`，由 `FontFaceParser` 从 CSS 文本解析并登记到 `FontManager`，按字重就近选文件、`SKTypeface.FromFile` 加载。**未做** `format()` 校验、多 `src` 回退、woff2 变体、`local()`。

**`@page` 分页媒体**：`size`（A2–A5 / Letter / Legal / 两长度 / landscape）、`margin`（简写 + `margin-*`，支持 mm/cm/in/pt/px）；嵌套边距盒 `@top-left/center/right`、`@bottom-left/center/right`，`content` 支持字面串 + `counter(page)` + `counter(pages)`，以及盒内 `font-size`/`font-family`/`font-weight`/`color`。由 `AtPageParser` 从 CSS 文本直接解析（不依赖 AngleSharp 的分页媒体）。

**继承**：由 AngleSharp.Css 的 `GetComputedStyle()` 完成，跟浏览器行为一致。**例外——百分比**：`GetComputedStyle` 会把 `%` 一律按 render device 的视口（1024）折算成 px，丢失百分比信息（`height:100%` → `"1024px"`）。故对 `width`/`height`/`flex-basis`/`gap`/`font-size`/`line-height`/`margin-*`/`padding-*` 额外读 `GetStyleCollection().GetDeclarations()` 拿**声明值**（保留 `%`），是百分比就用它、否则用 computed。详见"关键代码导读"。

## 明确不支持的（demo 边界）

- **grid / float** — 布局层没实现（flex / inline / table 已支持，见上）
- **flex-wrap / *-reverse** — flex 只做了单行不换行、正向
- **表格 `rowspan` / 跨页断行 / `table-caption` / 列宽 `%`** — 表格做了自动列宽 + colspan + 合并/分离边框，但跨行合并、长表跨页续表头、标题行、按 `%` 定列宽还没做
- **inline 的 `vertical-align` / 行内背景跨行** — 原子盒只按基线底对齐；行内背景逐段画、不跨行合并
- **`break-inside: avoid` / 孤行寡行控制** — 分页器只做"块放不下就换页 + 超长块按行/子块拆"，没做 CSS Fragmentation 的高级约束
- **伪类 / 伪元素（`::before`/`::after`）/ `@media` / SVG** — 都没做
- **`overflow: hidden` 裁剪 / `transform`** — 都没做（圆角/阴影/渐变/opacity/box-sizing 已做，见上）
- **网络图片 URL** — `ImageLoader` 默认不联网（保证离线 + 确定性输出）

这些恰好是"再加就能用"的空间，架构上不用返工。

## 关键代码导读

**入口**：`src/Renderer.cs` — `RenderPdf`（Parse @page → Measure → Paginate → 逐页 BeginPage + 页眉页脚）/ `RenderPng`（Measure → Position → 单张长图）。`RenderPng` 带 `scale` 设备缩放因子：布局仍按 CSS px 算、画布放大后光栅，`scale=2`（demo 默认，~192 DPI）出图清晰；`scale=1` 是旧的 ~96 DPI、放大看会发虚。PDF 是矢量、任何缩放都清晰，不受此影响。

**CSS 层叠在哪**：`src/Css/StyleResolver.cs` — `window.GetComputedStyle(element)` 拿 AngleSharp.Css 算好的最终值；`ReadStyle()` 逐属性映射到 `ComputedStyle`。百分比属性另走 `window.GetStyleCollection().GetDeclarations(element)` 取**声明值**（`Val()` 帮助方法：声明是 `%` 用声明、否则用 computed），因为 computed 会把 `%` 折算成视口 px。

**百分比在哪落地**：`src/Css/CssSize.cs` 的 `CssSize`（Auto/Absolute/Percent）承载未解析的尺寸；`LayoutBuilder.Measure/MeasureHeight/MeasureFlex` 在已知包含块宽（`availWidth`/`ContentWidth`）或确定高（`containingHeight`）时调 `Resolve(base)` 折算——`width%`×父内容宽、`height%`×父确定高（父 auto→% 退化 auto）、`flex-basis%`×主轴可用、`gap%`×容器内容宽。`margin`/`padding` 的百分比经 `ComputedStyle.ResolveBoxSizes(availWidth)` 折算（横竖都 ×包含块宽，`ResolveEdge` 把 auto/未知基准落到 0）。`font-size%`/`line-height%` 在 `StyleResolver` 里按父字号折算（无需布局期）。

**margin/padding 为什么读 longhand**：早期版本读 computed 的 `margin`/`padding` **简写**（`GetComputedStyle().margin`），但 CSSOM 规范不要求 getComputedStyle 序列化简写——四边不一致或用 longhand 设置时 AngleSharp 直接返回空串，导致 `margin-bottom:20px` 这类被静默丢成 0。改成逐边读 longhand（`margin-top`/`-right`/`-bottom`/`-left`）后既修好了这个隐藏 bug，也让每边能独立带 `%`。

**布局在哪**：`src/Layout/LayoutBuilder.cs` — `ToBoxTree`（建盒子 + 加载 img；inline 块摊平成原子序列）/ `Measure`（算尺寸，block/flex/img/table 四分支 + min/max 夹取 + box-sizing + absolute 出流收缩适配）/ `Position`（分配坐标，block 堆叠 + flex 的 justify/align + inline 行盒 + table 网格 + relative 偏移 + absolute 按包含块定位，`CB` 结构沿树传递最近定位祖先的 padding box）。

**inline 在哪**：`src/Layout/InlineLayout.cs` — `BuildAtoms`（把块的 `InlinePieces` 递归摊平成原子序列：文本 run / `<br>` / inline-block 与行内 img 作原子盒）/ `BreakIntoLines`（按内容宽贪心折行，`Tokenize` 处理 white-space 折叠 + word-break + CJK 断词，合并同样式尾片段时**务必同步推进 `curW`**，否则后续片段会全部叠在同一 x）。行盒的基线 = 行内最大字号 ascent，行高取 `max(字号×line-height, ascent+descent)`。`LayoutBuilder.MeasureInline` 先测原子盒再折行，`PositionInline` 按 `text-align` 定每行左偏移、原子盒按基线定位。绘制在 `TextPainter.DrawInlineLines`（逐段用自己的字体/色/背景/字距/装饰线）。

**表格在哪**：`LayoutBuilder.MeasureTable` — `CollectRows` 穿透 row-group 收行 → 按 `colspan` 建网格 → 列宽用单跨单元格 `IntrinsicContentWidth`（max-content）定基准、多跨不足均摊、显式 `width` 或超可用宽按比例缩放 → 逐格 `Measure` 后行高取格最大值、格撑满行高。`PositionTable` 把网格按列宽/行高铺开、逐格 `PositionRec`，并把行盒几何撑满整行让行背景可见。`border-collapse: collapse` 在 `ApplyCollapseBorders` 里把内部右/下边框清零（仅末列/末行保留）去双线。**踩坑**：算列宽前必须先对每个单元格 `ResolveBoxSizes`，否则 `CellHDeco` 读到未解析的 padding=0，列宽偏窄致逐字折行。

**分页在哪**：`src/Layout/Paginator.cs` — `Place` 三态（当前页放得下 / 换页放得下 / 比整页还高需碎片化），`Fragment` 递归拆容器、`FlowText` 按行拆纯文本段落、`FlowInline` 按行盒拆混排 inline 块，跨页处把容器装饰带 `Insert(0, …)` 到每页最底层；`img`、含原子盒的 inline 块、以及表格作为原子块不拆分（整块放置）；absolute 子树 `EmitInto` 到每页 `AbsItems`（画在 Items 之上）。

**图片在哪**：`src/Layout/ImageLoader.cs` 解码 + 按 src 缓存（相对路径基于 `Renderer` 传入的 `baseDir`）；`LayoutBuilder.MeasureImage` 按 CSS/自然尺寸定 content 盒；`Painter`/`PagePainter` 用 `canvas.DrawImage` 画进内容区。

**@page 在哪**：`src/Css/PageSpec.cs` 的 `AtPageParser` 用花括号配对从 CSS 文本解析 `size`/`margin`/嵌套边距盒 + `counter()`；`Paginator` 按 `@page` 边距算内容区；`src/Paint/MarginBoxRenderer.cs` 每页把边距盒文本（含页码）画到页边距带里。

**字体子集化**：`tools/subset-font.py` 构建期裁字 → `src/Layout/FontManager.cs` 运行时 `SKTypeface.FromFile` 优先加载子集，PDF 因此只嵌 ~1.8MB 而非整份雅黑。

**文本折行**：`src/Layout/TextMeasurer.cs` — `Tokenize()` 里 `IsCjk()` 判定，中文按字断，Latin 按空格断。

**画到 canvas**：`src/Paint/Painter.cs`（单页）与 `PagePainter.cs`（分页）共用 `BoxPainter`（圆角/阴影/渐变/边框/opacity）+ `TextPainter`（对齐/字距/装饰线/opacity）——同一套绘制逻辑，PDF canvas 和 PNG surface canvas 通用，两条路径不会漂移。

## 想扩展的话

按对"渲任意真实网页"的价值排序：

1. **表格补全** — `rowspan`、长表跨页续 `<thead>`、`table-caption`、列宽 `%`/`<col>`。
2. **inline 补全** — `vertical-align`（top/middle/bottom）、行内背景跨行合并、行内盒的 `position`/相对定位。
3. **分页高级约束** — `break-inside: avoid`、`orphans`/`widows`、`@page` 命名页 / `:first` / `:blank`。
4. **flex-wrap / *-reverse** — 在 `MeasureFlex` 里加换行分组 + 反向遍历。
5. **absolute 补全** — `left+right` 同时给时拉伸宽度、`z-index` 层叠上下文、`fixed`/`sticky`。
6. **网络图片 / `@font-face` 多 src** — `ImageLoader` 放开 http 分支（注意破坏确定性输出）；`@font-face` 补 `format()`/woff2/`local()`。
7. **换 UA 样式表** — `StyleResolver.UaStylesheet` 常量，或从外部 `assets/ua.css` 加载。

## 常见坑提示

- **`dotnet restore` 报找不到 `AngleSharp.Css 1.0.0-beta.176`** — 版本号会漂，跑 `dotnet add package AngleSharp.Css --prerelease` 让 NuGet 拉最新。AngleSharp 主库需 ≥1.3.1 才匹配。
- **`GetComputedStyle` 抛 `ArgumentException: A non null render device ... required to calculate em or rem units`** — AngleSharp.Css 计算 em/rem/% 等相对单位需要非空渲染尺寸。`StyleResolver.BuildDocument` 已用 `Configuration.WithCss().WithRenderDevice(new DefaultRenderDevice{ FontSize=16, ... })` 注册。
- **百分比被 AngleSharp"吃掉"** — 注册 render device 后，`GetComputedStyle`/`ComputeStyle`/`ComputeDeclarations` 都会把 `%` 按视口（1024×1024）折算成 px（`height:100%`→`"1024px"`、`font-size:150%`→`"1536px"`），拿不到百分比本身，布局期无从按包含块重算。解法：对需要 % 的属性改用 `GetStyleCollection().GetDeclarations(element)` 取**声明值**（保留 `%`，含内联 style），是百分比就交给 `CssSize` 在布局期解析。踩过的坑：漏了这步时 `height:100%` 撑成 1024px，长图一度渲成 794×116243。
- **computed 的 `margin`/`padding` 简写返回空串** — CSSOM 不要求 `getComputedStyle` 序列化简写，四边不等或用 `margin-bottom:20px` 这种 longhand 设置时，`GetComputedStyle().margin` 直接给 `""`。若照简写读，这些外边距/内边距会被静默丢成 0（本 demo 早期就中招，改读逐边 longhand 后 `invoice` 等样例才补回应有的留白）。教训：盒边距一律读 `margin-top`/`-right`/`-bottom`/`-left` 四个 longhand，别读 `margin` 简写。
- **PDF 文件 20+ MB（未跑子集化脚本时）** — SkiaSharp 2.88 的 PDF backend 对**系统 CJK 字体不做子集化**，会把整个微软雅黑（~20MB）嵌进去。本 demo 的解法：`python tools/subset-font.py` 用 fonttools 预裁到 GB2312 常用字（18.8MB→1.77MB），`FontManager` 运行时优先加载子集，PDF 降到 ~2.5MB。若跳过这步，会退回系统字体嵌入、体积回升。**其它可选路径**：换 `Noto Sans SC`；升级 SkiaSharp 3.x（PDF backend 支持子集，但 SKPaint 文字 API 有 breaking change）；PDF 侧改用 PDFsharp。
- **Linux 上找不到中文字体** — 装 `fonts-noto-cjk`（Debian/Ubuntu）或 `google-noto-sans-cjk-ttc-fonts`（Fedora）。`FontManager` 里已经写了 `Noto Sans CJK SC` 别名。
- **中文渲染成方框** — 十有八九是 Windows 上跑的但当前用户环境没有"微软雅黑"，`FontManager.Fallbacks` 会退回默认字体，默认字体是拉丁系，遇到 CJK 就是 `.notdef` 方框。装字体或改 CSS 里的 `font-family`。
- **图片不显示** — 相对路径基于 `Renderer.RenderPdf/RenderPng` 的 `baseDir` 参数（Program 里传的是 assets 目录）；`src` 里的路径要相对 HTML 文件。网络 URL 默认不抓。
- **`GetPropertyValue("border-color")` 抛 NullReferenceException** — AngleSharp.Css 1.0.0-beta 对某些简写属性序列化有 bug，`StyleResolver.Get()` 已用 try/catch 兜底，单属性读失败退回父级/默认值。
- **`radial-gradient` 让 `GetComputedStyle` 整个抛 NRE** — AngleSharp.Css 在 `CssRadialGradientValue.Compute()` 里有 bug，只要某元素级联里有 `radial-gradient`，`window.GetComputedStyle(element)` 就抛异常、该元素 computed 全拿不到。解法：`ReadStyle` 把 `GetComputedStyle` 包在 try/catch 里，失败则该元素所有属性退回**声明值**（`GetDeclarations`，不触发 Compute）+ 父级继承（`Raw()`/`Val()` 帮助方法：computed 优先、空则用 Spec）。`linear-gradient` 计算不炸但同样走 Spec 拿原始串自解析。
- **`text-overflow` AngleSharp 根本不实现** — computed 和 GetDeclarations 都返回空串。解法：`FontFaceParser` 式的 `ScanUnsupported` 从原始 CSS 按「最后一段简单选择器」建 class/id/tag→值 索引，`Lookup()` 在 AngleSharp 拿不到时兜底。目前只兜 `text-overflow`，将来 AngleSharp 缺啥往 `UnsupportedProps` 里加。
- **`ParseSize` 曾把负值判成 auto** — 早先 `if (px > 0)` 才认绝对值，导致 `top:-10px`/`right:-10px`/负 margin 被丢成 auto（角标跑到左上角）。改成"带长度单位即认绝对值（含负）"，`Off()` 也放开负偏移。
- **absolute 盒 `width:auto` 撑满父宽** — 出流盒不该像 block 那样填满包含块，要 shrink-to-fit。`MeasureOutOfFlow` 对 auto 宽的 absolute 子盒按 `IntrinsicContentWidth` 收缩（角标才是小药丸而非整条）。
- **`monospace` 遇中文变方框** — `FontManager` 的 monospace 回退（Consolas/Courier…）是纯拉丁字体，没 CJK 字形 → `.notdef` 方框。要中文等宽得显式配带 CJK 的字体或子集。
- **inline 折行时片段全部叠在一起** — `BreakIntoLines` 把连续同一样式的 token 合并进尾部 frag 时，只加了 `last.Width` 忘了加 `curW`，导致后续片段的 `X` 停在合并片段起点 → 全挤在同一 x 重叠。合并分支里 `curW += tw` 才对齐。
- **`<br>` 让整块退化成块级** — `<br>` 默认 computed display 为空→兜底成 `block`，若把它当普通子元素塞进 `Children`，`hasBlockChild` 变真 → 该块不再是 inline FC、`<br>` 前后各起一行。`<br>` 只进 `InlinePieces`（成换行标记），不进 `Children`（它不成盒）。
- **纯文本块误走 inline 引擎致回归** — 若 `IsInlineFC` 用"有文本"当触发条件，所有 `<p>纯文本</p>` 都改走新折行引擎，既有样例字节漂。触发条件收紧为"含行内元素 / `<br>` / 图文混排"，纯文本块继续走 `WrappedLines` 老路径。
- **`span` 等没被当行内** — AngleSharp 未必内置 UA 表，`<span>` 无显式 `display` 时 computed 可能给 `block` → 混排各起一行。`UaStylesheet` 里显式补 `a,span,em,i,strong,b,u,…{display:inline}` 与表格家族默认 display，判定才稳。
- **表格列宽偏窄、单元格逐字竖排** — `MeasureTable` 用 `CellHDeco`（padding+border）算列宽，但单元格的 `padding` 声明值要经 `ResolveBoxSizes` 才落到 `PaddingLeft` float；若在建列宽前没对每个单元格 `ResolveBoxSizes`，padding 读成 0 → 列宽 = 内在文本宽 + 2（仅边框）→ 内容宽恰好等于文本宽，亚像素就折行。解法：建列宽前先遍历所有单元格 `ResolveBoxSizes`，并给列宽 +1px 余量。

## 一句话总结

**AngleSharp.Css 做 CSS 语义层（解析+选择器+层叠+继承），SkiaSharp 做视觉几何层（字体+shaping+光栅+PDF），你只需要写中间那一层 block-flow 布局。** 这就是我们之前讨论的"最省依赖"落地。
