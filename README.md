# PaperSharp

**面向 .NET 的高性能文档渲染引擎** —— 纯托管管线把 HTML/CSS 直接编译为矢量 PDF 与高清图片，**无需浏览器，无需 headless Chrome**。

> `HTML + CSS` → `PDF · PNG · JPEG · WebP · ICO · SVG`

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)](#)
[![No headless browser](https://img.shields.io/badge/no--headless--browser-%E2%9C%93-brightgreen)](#)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/PaperSharp)](https://www.nuget.org/packages/PaperSharp)

## 为什么选它

- **零浏览器依赖** —— 不装 Chromium、不起子进程、不开调试端口；一个类库嵌进你的 .NET 进程即可渲染
- **服务端效率** —— 单页 PDF 中位约 200ms，单线程吞吐 ~5 页/秒；**线程安全**，多请求可并发进同一进程；常驻仅一个类库的体量，没有 V8/渲染树的内存包袱
- **中文原生排版** —— CJK 逐字折行、GB2312 常用字字体子集嵌入（PDF 仅 ~2.5MB 且文字可选中可搜索）、中文数字页码
- **打印级语义** —— 天生面向分页媒体：`@page` 页边距、页眉页脚边距盒、自动跨页回流、表格跨页续 + `<thead>` 重复、`break-inside` / `orphans` / `widows`
- **确定性输出** —— 默认离线渲染，同一输入恒定产物，适合发票、报表、合同等财务级文档

## 30 秒上手

```csharp
using PaperSharp;

byte[] pdf = Renderer.RenderPdf(html, css);                        // 分页矢量 PDF（自动注入书签、链接注释、元数据）
byte[] png = Renderer.RenderPng(html, css, width: 794, scale: 2);  // 2× 高清（~192 DPI）
```

## 示例集锦

> 每个场景一行调用 / 一段 CSS 搞定；文末给出仓内对应的完整验收样例。

**① 一行代码出 PDF**

```csharp
byte[] pdf = Renderer.RenderPdf(
    "<h1>发票</h1><p>合计：¥1,234.00</p>",
    "h1 { color: #2c3e50 }  p { font-size: 14px }");
```

**② 打印文档：页边距 + 页眉页脚页码，全自动**

```css
@page {
  size: A4;  margin: 25mm 20mm;
  @top-center    { content: "季度财务报表"; font-size: 9pt; color: #888 }
  @bottom-center { content: "第 " counter(page) " 页 / 共 " counter(pages) " 页" }
}
```

内容超一页自动回流，边距盒每页重绘。→ 完整样例 `assets/report.html`

**③ 400 行长表：跨页续 + 每页重复表头 + 中文页码**

```css
table { border-collapse: collapse; width: 100% }
thead { display: table-header-group }   /* 每页自动重放表头 */
tr    { break-inside: avoid }           /* 单行不跨页折断 */
td    { padding: 6px 10px }             /* 支持 % 列宽、<col>、colspan/rowspan */
@page { @bottom-right { content: counter(page, cjk-ideographic) " / " counter(pages) " 页" } }
```

跨页列宽共享、`rowspan` 续格自动补边框。→ `assets/m1.html`（400 行明细发票）

**④ 图卡输出：一个模板，五种格式**

```csharp
using SkiaSharp;

byte[] png = Renderer.RenderPng(html, css, width: 1200, scale: 2);   // OG 图（1600px 宽 2× 高清）
byte[] jpg = Renderer.RenderImage(html, css, 1200, SKEncodedImageFormat.Jpeg, quality: 82);
byte[] svg = Renderer.RenderSvg(html, css, 1200);                    // 矢量：渐变/裁剪映射为 SVG 原生
byte[] ico = Renderer.RenderIco(html, css, size: 64, width: 512);    // favicon，多尺寸
```

**⑤ 视觉全开：conic 渐变 + squircle + 遮罩 + 滤镜，纯 CSS**

```css
.hero {
  border-radius: 32px;  corner-shape: squircle;
  background: conic-gradient(from 45deg, #f6d365, #fd6589, #84fab0, #f6d365);
  box-shadow: 0 12px 40px rgba(0, 0, 0, .25);
  mask-image: linear-gradient(to bottom, black 60%, transparent);
  filter: blur(0px) drop-shadow(0 4px 12px rgba(255, 0, 128, .4));
  mix-blend-mode: screen;
}
```

→ `assets/m3.html`（OG 图卡风整页）

**⑥ 中文图文混排：真 inline 行盒**

```html
<p>本月营收 <strong class="up">↑ 128%</strong>（含税口径），
   扫码或点击 <a href="https://example.com/report">完整报表</a>
   <img src="imgs/qr.png" width="16" /> 查看明细。</p>
```

混排按行盒折行、基线对齐、CJK 逐字断行；渲进 PDF 后 `<a>` 变成**可点击的链接注释**，精确到行内片段矩形。→ `assets/inline.html`

**⑦ 书籍级 PDF：书签目录自动生成**

```csharp
byte[] pdf = Renderer.RenderPdf(bookHtml, css);
// h1–h6 → PDF 阅读器侧边栏书签层级；<title>/<meta name=author|description> → 文档元数据
```

零配置，Skia 不提供此 API——书签/注释由自研 `PdfEnhancer` 增量尾部注入。→ `assets/m5.html`

**⑧ 字体与主题：一行拉云端字体，var/calc 全支持**

```csharp
css = GoogleFonts.Fetch("Noto Serif SC:wght@400;700") + css;   // 联网一次，之后永久离线复用
```

```css
body  { --brand: #1a73e8; --gap: 12px }
.btn  { background: var(--brand, #333); padding: calc(var(--gap) / 2) calc(var(--gap) * 2) }
.col  { width: calc(100% / 3 - var(--gap)) }
```

→ `assets/m4.html`（var/calc 全谱 + 多字体）

**⑨ 多文种混排：阿拉伯连写 + RTL 双向 + emoji**

```css
.letter { direction: rtl; text-align: start }   /* RTL 段落：折行、对齐、行内盒序全部镜像 */
```

阿拉伯文经 HarfBuzz 连写成形；RTL 段里的拉丁词/数字按 bidi run 排布；emoji（含 ZWJ 家庭序列）与缺字按字形级回退，不再出豆腐块。→ `assets/m7.html`（阿拉伯/希伯来/越南/韩中混排整页）

## 架构

- **AngleSharp + AngleSharp.Css** — HTML 解析、选择器匹配、CSS 层叠与继承
- **SkiaSharp (+HarfBuzz)** — 字体读取、文本 shaping（中文/CJK/阿拉伯连写）、光栅化、PDF 矢量输出
- **自研** — block/flex/grid/float/table 布局、inline 折行、块级分页、绘制遍历、`var()/calc()/%` 补全、PDF 增量尾部（书签/注释）

一句话：**AngleSharp 做 CSS 语义层，SkiaSharp 做视觉几何层，中间那层 block-flow 布局是自己写的。**

## 能力速览（每项都有验收样例，`src/PaperSharpSamples/assets/`，m1–m5、m7–m9 对应里程碑）

- **分页**：块级跨页回流；长表跨页续 + 共享列宽 + 重复 `<thead>`；`break-before/after: page`、`break-inside: avoid`、`orphans/widows`；`@page` size/margin + 页眉页脚边距盒 + `counter(page/pages)`（含中文数字页码）
- **布局**：flexbox 全家（含 wrap/reverse/gap/嵌套）、Grid 子集、float + 图文环绕、relative/absolute + z-index 盖顶、`%` 单位按包含块布局期解析、`box-sizing`、min/max
- **选择器与伪元素**：`:is()`/`:where()`（AngleSharp 原生）、`::before`/`::after` 生成内容（字符串 / `attr()` / `open-quote`·`close-quote` / `url()` 图 / `counter()`·`counters()` 自动编号，含中文数字），块级与行内成盒
- **文本**：inline 混排真行盒（span/a/br/inline-block/行内 img，基线对齐 + `vertical-align`）、CJK 逐字 / Latin 逐词折行、`white-space`/`word-break`/`text-overflow: ellipsis`/`letter-spacing`/`text-transform`/`text-decoration`、`text-wrap: balance`、`text-fit`
- **视觉**：圆角（含 squircle）/阴影/linear·radial·conic 渐变/opacity/`mix-blend-mode`/`clip-path`/`mask-image`/`filter: blur|drop-shadow` 链/`background-clip: text`（渐变文字）/`backdrop-filter`（毛玻璃，仅位图路径）/内联 `<svg>`
- **字体**：构建期 fonttools 子集化（PDF 23MB→2.5MB）、`@font-face`（多 src 回退/woff2）、`GoogleFonts.Fetch()` 下载+缓存+离线复用、`RegisterFontDirectory`、跨平台系统回退
- **复杂文字**：HarfBuzz shaping（阿拉伯连写）、`direction: rtl` + 轻量 bidi 混排、扩展字素安全折行（emoji ZWJ/组合符不拆散）、字形级字体回退（emoji/小语种不再出豆腐块）
- **动画（确定性取帧）**：解析 `@keyframes` + `animation` 全套（timing/iteration/direction/fill），渲染传 `animationTime`（秒）即取该时刻的一帧，对 opacity/颜色/长度/圆角/字号/定位偏移按帧插值；输出仍是静态单帧、逐字节确定。Tailwind `animate-pulse` 已映射（详见边界）
- **输出**：矢量 PDF（h1–h6 书签 + `<a>` 链接注释 + metadata，经自研 `PdfEnhancer` 增量尾部注入）/ PNG（scale 高清）/ JPEG / WebP / ICO / SVG
- **边界**：`@media`/`overflow`/`transform`/`fixed`/`sticky`/网络图未做；`transform` 未实现 → 依赖它的动画（`spin`/`ping`/`bounce`）关键帧会被解析但不生效（不做假变换），仅 opacity/颜色/长度类动画可取帧；`backdrop-filter` 只在位图（PNG/JPEG/WebP）路径生效，PDF/SVG 矢量路径跳过；嵌套双向、国旗 emoji、shaped run 的 letter-spacing 是复杂文字的已知简化——全量清单见 [ROADMAP.md](ROADMAP.md)

## 目录结构

```
papersharp/
├── PaperSharp.slnx
├── src/
│   ├── PaperSharp/                 # 纯渲染库（net8.0 类库）
│   │   ├── Renderer.cs             # 顶层 API：RenderPdf / RenderPng / RenderImage / RenderIco / RenderSvg
│   │   ├── Css/                    # StyleResolver(AngleSharp 桥+UA 表+兜底) / ComputedStyle / CssSize /
│   │   │                           # CssValueParser / Visual / FontFaceParser / GoogleFonts / PageSpec(@page)
│   │   ├── Layout/                 # LayoutBuilder / BlockBox / InlineLayout / FloatContext / Paginator /
│   │   │                           # TextMeasurer / ImageLoader / FontManager
│   │   ├── Pdf/                    # PdfEnhancer(书签+注释+xref 重写) / PdfSemantic(从盒树收书签与链接矩形)
│   │   └── Paint/                  # Painter(单页长图) / PagePainter(分页) / BoxPainter / TextPainter /
│   │                               # SvgWriter(绘制期录 SVG) / MarginBoxRenderer
│   ├── PaperSharp.Wasm/            # browser-wasm 宿主（JSExport：Render / InstallSubsetFont / RegisterFont）
│   └── PaperSharpSamples/          # 验收样例 + 基准宿主（Exe，引用 PaperSharp）
│       ├── Program.cs / Benchmark.cs / run.bat
│       ├── tools/subset-font.py    # 一次性裁字体子集（fonttools）
│       └── assets/                 # invoice/long/flex/img/report/pct/polish/inline/table + m1–m5（html+css）、imgs/、fonts/
├── js/                             # papersharp npm 包（JSX/HTML + Tailwind 子集 → 同一套 wasm 引擎）
├── extension/                      # MV3 浏览器插件：任意网页右上角导出 PDF/PNG/JPEG/WebP/SVG/ICO
└── tests/PaperSharp.Tests/         # xUnit + PdfPig + PDFtoImage，goldens/ 提交基准
```

## 跑起来

```bat
cd src/PaperSharpSamples
python tools/subset-font.py   :: 一次性，需 pip install fonttools；跳过则 PDF 回退嵌整份系统雅黑 ~20MB
dotnet run
```

产物在 `bin/Debug/net10.0/output/`。也可直接双击 `run.bat`。

## 浏览器插件（任意网页 → 六种格式）

`extension/` 是一个 MV3 插件：在任意页面右上角加一个「导出」按钮，把整页导出成引擎支持的
**PDF / PNG / JPEG / WebP / SVG / ICO**——渲染全在本机 .NET WASM 里，不截图、不上传、无服务端。

```bat
node extension/build.mjs --publish         :: 产出 wasm + 中文字体子集
:: 浏览器 → 扩展页 → 开发者模式 → 加载已解压的扩展程序 → 选 extension 目录
node extension/test/e2e-browser.mjs --diag :: 端到端实测（Edge 无头装载扩展跑六格式）
```

快照走「计算样式展平」而不是把网页 CSS 原样交给引擎：浏览器负责解析级联 / `@layer` / 媒体查询 /
阴影 DOM，插件只下发每个元素最终生效的扁平规则（签名去重后通常 200–300 KB），正好落在引擎的
CSS 子集上；`overflow:hidden` 用已实现的 `clip-path` 近似、`background-image: url()` 合成成绝对定位
`<img>`、图片一律内联成 data URI。真实网站的错位主要靠「钉死」解决——块级盒直接按浏览器算好的矩形
`position:absolute` 摆位（长图整页钉，PDF 只钉 `fixed/sticky/absolute` 与 flex/grid 子女以保住分页回流，
表格只钉位置）。导出侧再把快照标记成 `precomputed`，让引擎跳过「声明值」那一轮层叠：
Bootstrap 控制台样张的引擎段从 10.9 s 降到 3.0 s。取舍与已知限制见 [extension/README.md](extension/README.md)。

## 支持的 CSS 子集

- **选择器**：type / `#id` / `.class` / 复合 / 后代 / `>` / 属性 / `:is()` / `:where()` —— AngleSharp.Css 原生
- **伪元素与计数器**：`::before` / `::after` 生成内容 —— 字符串、`attr()`、`open-quote`/`close-quote`、`url()` 图片、`counter()`/`counters()` 自动编号（`counter-reset`/`counter-increment`，counter-style 含十进制/字母/罗马/`cjk-ideographic`/`trad-chinese-informal`）；行内与块级伪元素各自成盒，多级列表嵌套编号
- **盒模型**：`width/height/min/max`（px/pt/em/rem/%）、`margin/padding`（1–4 值简写 + longhand，负值、%）、`border-*-width/color`、`box-sizing: border-box`（块/flex/img/单元格一致）。`margin/padding` 的 % 一律相对包含块**宽**；`height%` 在父高 auto 时退化为 auto
- **Flexbox**：direction（含 reverse）/ justify-content 6 种 / align-items·align-self（含 stretch）/ grow·shrink·basis（含 %·calc）/ wrap / 嵌套；`align-content` 暂简化为 flex-start
- **Grid 子集**：template-columns/rows（长度/%/`fr`/`auto`/`minmax()`/`repeat()`/auto-fill）、gap、`grid-column/row: span N`、跨轨道 stretch
- **定位**：relative / absolute（shrink-to-fit、相对最近定位祖先 padding box、`left+right` 拉伸、负偏移）+ `z-index` 排序盖顶；fixed/sticky 按 static 处理
- **视觉**：border-radius（含 squircle）/ box-shadow / linear·radial·conic-gradient / opacity / mix-blend-mode / clip-path(inset·circle·polygon) / mask-image(线性渐变) / filter: blur()·drop-shadow() 链 / `background-clip: text`（渐变文字：背景铺进字形）/ `backdrop-filter`（毛玻璃，读取盒背后已绘像素再模糊，仅位图路径）
- **文本与 inline**：`display:inline`（span/a/em/strong/…UA 已补）与 inline-block 真行盒（基线、`text-align`、`vertical-align`、balance、浮动绕排）；`white-space`(normal/nowrap/pre/pre-wrap/pre-line)、`word-break`、ellipsis、`letter-spacing`、`text-transform`、`text-decoration`、`line-height`（无单位/%)
- **表格**：table 全家族 display + colspan/**rowspan**/caption/`<col>`；collapse/separate 两种边框模型；列宽自动分配（单跨 max-content 基准、显式 width/%/<col> 优先、超限等比缩放）；跨页续表
- **图片**：`<img>` 文件路径（相对/绝对）与 `data:` URI、内联 `<svg>`；宽高三种模式（都给/单给等比/自然尺寸）；网络 URL 不抓（离线 + 确定性输出）
- **字体**：`font-family/size/weight/line-height`、`@font-face`（`src` 多候选按序回退、字重就近）、`GoogleFonts.Fetch("Roboto:wght@400;700")`；未做 `format()` 校验与 `local()`
- **动画（确定性取帧）**：`@keyframes`（from/to、百分比、多选择器帧）+ `animation` 简写（duration/delay/timing 含 cubic-bezier·steps/iteration-count/direction/fill-mode）；渲染传 `animationTime`（秒）按该时刻求值插值，覆盖 opacity / color / background-color / font-size / letter-spacing / width / height / top·right·bottom·left / border-radius。`transform` 未实现 → 依赖它的动画停在基线态（不造假）。Tailwind `animate-pulse` 已映射为 opacity 关键帧
- **var() / calc()**：自研（AngleSharp 处理不全）——var 作用域链 + 回退 + 嵌套；calc 线性式（px/pt/em/rem/%、嵌套括号），布局期按包含块落地
- **@page 分页媒体**：`size`（A2–A5/Letter/Legal/两长度/landscape）、`margin`（mm/cm/in/pt/px）、`@top-*/@bottom-*` 边距盒（content 字面串 + counter(page)/counter(pages)，counter-style 含 `cjk-ideographic`/`trad-chinese-informal`）
- **PDF 语义**：书签 outline（h1–h6）、链接注释（`<a href>`，行内精确矩形）、元数据（`<title>`/`<meta name=author|description|keywords>`）

## 测试

`tests/PaperSharp.Tests`，三层守门：

| 层 | 守什么 |
|---|---|
| 结构（PdfStructureTests） | PDF 可解析（兜住 PdfEnhancer 手写 xref）、页数、A4 尺寸、<6MB 体积哨兵 |
| 语义（PdfSemanticTests） | 书签树 / 链接注释 / 元数据、中文字可抽取（ToUnicode）、跨页有内容 |
| 像素（PngGolden + PdfRaster） | PNG 字节级 golden；PDF 经 PDFium 读回栅格 diff ≤0.1%——验证"别人打开长什么样" |

```bat
dotnet test tests/PaperSharp.Tests                        :: 全量 ~30s
dotnet test tests/PaperSharp.Tests --filter "Category!=Slow"   :: 跳过 PDF 栅格
UPDATE_GOLDENS=1 dotnet test tests/PaperSharp.Tests       :: 有意变更后重生成基准（评审 diff 再提交）
```

约定：**永远不比 PDF 字节**（Skia 嵌时间戳）；golden 依赖本机字体栈，当前仅 Windows 钉基准；测试**并行执行**——库已线程安全（`RenderScope` 每请求隔离），全套并行跑本身就是并发回归哨兵，另有 `ConcurrencyTests` 专测（16 线程字节一致 / 文档间无污染 / BaseDir 不串台）。

## 性能

详见 [PERF_REPORT.md](PERF_REPORT.md)（Release `bench 50` 净跑，数据在 `src/PaperSharpSamples/bench_20260922.txt`）。要点：

- 单页 PDF 中位 184–280ms，其中 paintEncode（字体子集写入）占 **85–95%**；布局（measure+paginate）<2%。PNG 中位 27–107ms；吞吐 invoice **4.9 页/秒**（单线程）。
- **`buildDoc`（AngleSharp 逐元素层叠）随元素数超线性 ~O(n^2.5)**：table 2000 行 74.4s / flexlist 2000 条 21.3s，而同规模自研布局仅 76/162ms。上千元素文档的唯一瓶颈是 CSS 层叠——ROADMAP M6（自研级联）的量化依据。

## AngleSharp 坑位备忘（均已绕过，改这块代码前必读）

- **computed 把 `%` 按 1024 视口折算成 px**（`height:100%`→`1024px`），`calc()` 同理被预解析 → 需要原始值的属性走 `GetDeclarations()` 取**声明值**（`Val()/Spec()`），% 与模板串交给 `CssSize` 布局期解析
- **computed 不序列化 `margin`/`padding` 简写**（返回空串）→ 一律逐边读 longhand，否则静默丢 0
- **`radial-gradient` 让整元素 `GetComputedStyle` 抛 NRE；`repeating-*` 渐变的 `CssText` 必抛** → `Get()` try/catch 兜底；background/background-image 一律不读 computed
- **`text-overflow` 根本不实现** → `ScanUnsupported` 从原始 CSS 建 class/id/tag 索引，`Lookup()` 兜底；AngleSharp 缺啥往 `UnsupportedProps` 加
- **em/rem/% 需要非空 render device** → `BuildDocument` 里 `WithRenderDevice(new DefaultRenderDevice{ FontSize=16, ... })`
- **AngleSharp 不内置 UA display** → `UaStylesheet` 显式补 `span/a/em/…{display:inline}` 与表格家族；`<br>` 只进 inline 片段不进 Children（否则误判成块级）
- **伪元素 UA 默认注入 `display:block`**（computed 与 declared 都是，作者没写也判块）→ 不能据此定行内/块级；`ScanPseudoDisplay` 从原始 CSS 扫作者显式声明的 display，缺省按 inline
- **`counters(a,',')` 的逗号被重序列化吃掉**（computed/declared 都变 `counters(a ,)`），且按 token 兜底会串味（`li` 命中别的选择器的 `li`）→ content 串仍走 computed（选择器门控正确），但解析器 `FuncArgs` 兼容「逗号或空白」两种分隔
- **`counter-reset`/`counter-increment` computed 恒返回 `"none"`** → 挡住 `Lookup` 兜底；`CleanCounter` 把 `"none"`/空当缺失，强制走原始 CSS 声明值
- **`background-clip` computed 恒返回 `border-box`**（即便写了 `text`）→ `Lookup` 传空 current 强制读原始声明值才能认出 `text`
- **`animation`/`@keyframes` 完全不实现** → 走 `UnsupportedProps` 原始 CSS 兜底 + 自研 `Animation` 解析与取帧；简写解析里 `forwards`/`backwards` 以 `s` 结尾，须先认关键字再认时间（否则误当秒数）
- 版本号会漂：restore 失败跑 `dotnet add package AngleSharp.Css --prerelease`（AngleSharp 主库需匹配）

历史缺陷（已修，动到对应模块前先读代码注释）：表格定位 O(行²)→线性、inline 片段合并漏推进 `curW`、单元格建列宽前未 `ResolveBoxSizes` 致逐字折行、`ParseSize` 负值当 auto。

其它环境问题：Linux 缺中文字体装 `fonts-noto-cjk`；`monospace` 回退是纯拉丁系，中文等宽需显式配 CJK 字体；`dotnet run --no-build` 在构建失败时会**静默跑旧 dll**——回归前确认 build 真成功。

## 扩展方向

见 [ROADMAP.md](ROADMAP.md)（差距矩阵 + M5 剩余 / M6 自研级联 + 明确不做；M7 复杂文字与并发安全化已交付）。
