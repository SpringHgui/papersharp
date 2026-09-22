# PaperSharp 路线图 · 对标 takumi

> 对标对象：**takumi**（<https://github.com/kane50613/takumi>，Apache-2.0/MIT，Rust 引擎，~3k stars）。
> 定位：无头浏览器地把 **JSX / HTML / CSS** 渲成 **OG 图 + 分页 PDF**，跑在 Node/Bun/Cloudflare Workers/浏览器/Rust。
> 架构：node tree → **Taffy**（flex/grid 布局）→ **Parley + Skrifa**（文字 shaping）→ raster / SVG / PDF 后端。
>
> 本文把 takumi 支持的特性全量列出，逐条对照 PaperSharp 现状，并给出分阶段路线。可行性判断基于我们的技术栈（AngleSharp.Css + SkiaSharp 2.88 + 手写布局层）。

---

## 1. takumi 特性全清单

### 1.1 图像 / OG（`takumi-js`）

- 输出格式：**PNG · JPEG · WebP · SVG · ICO · 裸像素**；动画 **GIF · animated WebP · APNG · 裸帧**
- **text-fit**：标题自动放大/缩小撑满行盒，无需测量循环
- **text-wrap: balance / pretty**：标题多行均衡、减少"孤字尾行"
- **动画**：`@keyframes` / `animate-spin` 按时间采样整棵树 → WebP/APNG/GIF/视频帧（`renderAnimation` + scenes/fps）
- **样式表**：复杂选择器、`var()`、`calc()`、媒体查询
- **布局**：Flexbox、**CSS Grid**、block、inline、**float**
- **`lang`**：同一码点按语言选对应汉字字形（zh/ja/ko 分形）
- **路径文字**：`offset-path` 让文字沿路径走；`background-clip: text` + **conic 渐变** 上字
- **图层合成**：mask、clip、filter、**blend mode**
- **SVG 滤镜**：`filter: url(...)`，含 `feTurbulence`、`feDisplacementMap`
- **corner-shape**：圆角可换 squircle / bevel / scoop / notch / superellipse(n)
- **Tailwind v4** 原子类直接可用（含任意值 `[...]`）
- 兼容 `next/og`：`ImageResponse` 一行替换

### 1.2 PDF（`takumi-pdf`）

- **分页控制**：`break-before: page` / `break-after: page` / `break-inside: avoid`
- **widows / orphans**：默认 2，防孤行寡行
- **页眉页脚**：每页重复；`<PageNumber/>` `<TotalPages/>` 支持 CSS counter 风格，含 `trad-chinese-informal` 等中文数字
- **表格**：跨页**共享列宽** + 每页**重复 `<thead>`**
- **文字**：可选中、可搜索；字体以**子集**嵌入
- **链接 + 元数据**带进输出；`outline: true` 从标题生成**书签/大纲**
- **附件**：内嵌文件，含 **Factur-X 电子发票 XML**
- **Tagged PDF** 默认开启；提供 **PDF/A** 与 **PDF/UA** 合规与校验
- **多文字系统**：**阿拉伯文 shaping**、**双向 bidi**、CJK、emoji（取决于加载字体）
- 单页视口模式；发票/报表/票据模板

### 1.3 运行时 / 输入 / 工具链

- 运行时：Node.js & Bun（原生绑定，mac/linux glibc+musl/win x64+arm64）、CF Workers & 浏览器（WASM）、Rust crate
- 输入：**JSX、HTML、node tree**；`measure()` API；**paint tree**（导出"最终用到的值"）
- helpers：模板转换、`googleFonts()` 字体、图片、emoji

---

## 2. 差距矩阵（takumi 特性 → PaperSharp 现状 → 我们栈的成本）

图例：✅ 已实现 · 🟡 部分/需补 · ❌ 无。成本：低（Skia/AngleSharp 原生）· 中（手写布局/绘制）· 高（架构级，可能要换后端或引外部库）。

### 布局

| 特性 | 现状 | 成本 | 说明 |
|---|---|---|---|
| block / inline / box-sizing / min-max | ✅ | — | 已有 |
| Flexbox（direction/justify/align/grow-shrink-basis/gap） | ✅ | — | 已有 |
| **flex-wrap / *-reverse** | ❌ | 中 | `MeasureFlex` 加换行分组 + 反向遍历 |
| **float** | ❌ | 中-高 | 需让 inline 文本绕浮动盒，动到 inline FC |
| **CSS Grid** | ❌ | 高 | 手写轨道 sizing（fr/minmax/auto-fill）；takumi 靠 Taffy |
| 相对/绝对定位 | 🟡 | 低 | 有 relative/absolute；缺 `left+right` 拉伸、`z-index`、`fixed/sticky` |

### 文本

| 特性 | 现状 | 成本 | 说明 |
|---|---|---|---|
| 折行 Latin+CJK / white-space / word-break / letter-spacing / text-decoration / text-transform / ellipsis | ✅ | — | 已有 |
| inline 混排（span/em/strong/a/br/inline-block/行内 img） | ✅ | — | 已有 |
| **vertical-align** | ❌ | 中 | inline 原子盒目前只按基线底对齐 |
| **text-wrap: balance / pretty** | ❌ | 中 | 折行后做再平衡 pass |
| **text-fit** | ❌ | 中 | 迭代缩放字号直到填满盒，需缓存测量 |
| **`lang` 分形（zh/ja/ko）** | ❌ | 中 | 按 lang 设 `SKFont` 的 locale / 选字体 |
| **阿拉伯文 shaping + bidi + emoji** | ❌ | **高** | SkiaSharp 2.88 无 HarfBuzz shaping，需引外部 shaping（HarfBuzz/SkiaParagraph 绑定）——**架构级** |
| 路径文字 `offset-path` | ❌ | 高 | 逐字沿路径摆放 |

### 视觉

| 特性 | 现状 | 成本 | 说明 |
|---|---|---|---|
| border-radius / box-shadow / linear+radial gradient / opacity | ✅ | — | 已有 |
| **conic/sweep 渐变** | ❌ | 低 | `SKShader.CreateSweepGradient` |
| **blend mode** | ❌ | 低 | `SKPaint.BlendMode` |
| **mask / clip-path** | ❌ | 中 | `SKCanvas.ClipPath` + 遮罩层 |
| **filter: blur / drop-shadow**（仅图像路径） | ❌ | 中 | `SKImageFilter.CreateBlur`；PDF 路径 takumi 也拒绝 blur |
| **corner-shape（squircle…）** | ❌ | 中 | 自定义圆角路径 |
| **SVG 滤镜 feTurbulence/feDisplacementMap** | ❌ | 中-高 | Skia 有 `CreateTurbulence`/`CreateDisplacementMap`，但接进 CSS `filter:url()` 要写滤镜图解析 |
| **嵌入 `<svg>` 图** | ❌ | 中 | SkiaSharp `SKSvg` 可解码绘制 |

### 分页 / PDF

| 特性 | 现状 | 成本 | 说明 |
|---|---|---|---|
| @page size/margin + 页眉页脚边距盒 + counter(page/pages) | ✅ | — | 已有 |
| 块级跨页回流 + 长段按行拆 | ✅ | — | 已有 |
| 文字可选中可搜索 | ✅ | — | 实测 `get_text()` 命中，Type0 子集字体 |
| 字体子集嵌入 | ✅ | — | 构建期 fonttools，PDF ~2.4MB |
| **表格跨页续 + 共享列宽 + 重复 `<thead>`** | ❌ | 中 | 现在表格整块原子放置；改为可断 + 表头重放 |
| **`rowspan`** | ❌ | 中 | 单元格跨行占位 + 行高分摊 |
| **break-before/after: page、break-inside: avoid** | ❌ | 中 | 分页器读这三条约束 |
| **widows / orphans** | ❌ | 低-中 | `Fragment` 拆段时约束首末行数 |
| 中文数字页码 counter 风格 | ❌ | 低 | counter 渲染器加 `cjk-decimal`/`trad-chinese-informal` |
| **书签/outline（从标题生成）** | ❌ | 中 | 需验证 Skia `SKDocument` 是否暴露 outline API；否则自写 PDF 结构 |
| **链接注释 + 文档 metadata** | 🟡 | 中 | metadata 已有 Title/Producer；可加 author/subject/keywords；链接需 PDF annotation API |
| **附件 / Factur-X XML** | ❌ | 高 | Skia PDF 后端不支持内嵌文件，需换/补 PDF 写入层 |
| **Tagged PDF / PDF/A / PDF/UA** | ❌ | **高** | 实测 `MarkInfo`/`StructTreeRoot` 为 null；Skia PDF 后端不出结构树/XMP，**需换 PDF 发射器**（如 PDFsharp / 自写 / iText） |

### 输入 / 输出 / 工具

| 特性 | 现状 | 成本 | 说明 |
|---|---|---|---|
| HTML + CSS 字符串输入 | ✅ | — | 已有 |
| PNG（含 `scale` 高清）+ 矢量 PDF | ✅ | — | 已有 |
| **JPEG / WebP / ICO 输出** | ❌ | 低 | `SKImage.Encode` 换格式 |
| **SVG 输出** | ❌ | 中 | 绘制期录 SVG（takumi 有 `renderSvg`） |
| **动画 GIF/WebP/APNG** | ❌ | 高 | 需按时间采样整棵树 + 多帧编码，与"文档渲染器"定位偏离 |
| **`var()` / `calc()`** | 🟡 | 低-中 | 依赖 AngleSharp.Css 支持度，需实测补齐 |
| 媒体查询 | ❌ | 中 | 我们无"视口"概念，仅对图卡有意义 |
| Tailwind 原子类 / JSX 输入 | ❌ | — | **不做**（属前端构建期，可让用户上游预处理成 HTML+CSS） |
| 多运行时 / WASM | ❌ | — | **不适用**：我们本就是 .NET；可另议 Native AOT |

---

## 3. 分阶段路线

排序原则：**① 能渲更多真实文档（发票/报表/合同/文章）优先 → ② 架构不返工 → ③ YAGNI，动画/多运行时/JSX 这类偏离"HTML→文档"主线的最后或不做。**

### M1 — 表格与分页补全（刚需，架构已就位）🎯 先做

- 表格 **跨页续 + 共享列宽 + 每页重复 `<thead>`**、**`rowspan`**、`table-caption`、列宽 `%`/`<col>`
- 分页控制：**`break-inside: avoid`**、**`break-before/after: page`**、**widows/orphans**
- 中文数字页码 counter 风格
- **验收**：一张 400 行明细 + 表头的发票跨 3 页、列宽一致、每页带表头与"第 N 页/共 M 页（中文数字）"；长段落不出现寡行。
- **理由**：这是"文档渲染器"最常被真实需求打中的点，且直接复用现有 `Paginator`/`MeasureTable`，投入产出比最高。

### M2 — 布局能力扩展

- **flex-wrap / *-reverse**、**float**、**CSS Grid 子集**（先 grid-template-columns/rows + gap + span，后 fr/minmax/auto-fill）
- 定位补全：`left+right` 拉伸、`z-index` 层叠、`fixed/sticky`
- **验收**：一个真实"卡片网格 + 图文环绕 + 多行 flex 标签流"页面渲对。
- **理由**：决定"任意真实网页"能不能落地；Grid 成本高，放 M2 末，且可只做子集。

### M3 — 文本与视觉增强（Skia 原生，多为低成本高观感）

- 文本：**vertical-align**、**text-wrap: balance**、**text-fit**、`lang` 分形
- 视觉：**conic 渐变**、**blend mode**、**mask/clip-path**、**corner-shape**、图像路径 **blur/drop-shadow**、**嵌入 `<svg>`**
- **验收**：OG 风格图卡（渐变文字、圆角 squircle、阴影、遮罩）与杂志式排版（首字下沉、路径文字可选）。
- **理由**：`SKShader`/`SKPaint.BlendMode`/`SKImageFilter` 现成，性价比高；SVG 滤镜图放最后。

### M4 — 输出格式与字体工具

- 输出：**JPEG / WebP / ICO**、**SVG**
- 字体：Google Fonts 式 helper（下载+缓存+注册）、`@font-face` 多 src/woff2
- CSS：实测并补 **`var()`/`calc()`**
- **验收**：同一模板一键出 PNG/JPEG/WebP/PDF 四份；联网拉字体一次后离线复用。
- **理由**：把"能渲"变"能交付"；仍不碰架构。

### M5 — PDF 语义 / 无障碍 / 合规（可能触发后端选型）

- **书签 outline**、**链接注释**、完整 **metadata**
- **Tagged PDF → PDF/UA → PDF/A**、**附件 / Factur-X**
- **⚠️ 决策点**：Skia 的 PDF 后端不出结构树/XMP/附件。此阶段大概率要**引入独立 PDF 写入层**（PDFsharp 或自写 content-stream 生成器）与 Skia 光栅分工。建议 M5 前单开一次技术选型 spike。
- **验收**：发票 PDF 可被屏幕阅读器读出结构（tagged）、过 veraPDF 的 PDF/A-2b、内嵌 Factur-X XML。
- **理由**：企业/政务/无障碍场景才需要，价值高但成本最高，故排在功能成熟之后。

### M6 — 复杂文字系统（阿拉伯文 + bidi）

- HarfBuzz shaping + Unicode bidi 双向算法 + 字体回退
- **⚠️ 架构级**：SkiaSharp 2.88 managed 不带 HarfBuzz；需外部 shaping 绑定或升级到带 SkParagraph 的方案。
- **理由**：除非目标市场明确需要 RTL/阿拉伯文，否则按 YAGNI 可长期搁置。

### 明确不做（与"HTML→任意文档"主线冲突）

- 动画 GIF/WebP/APNG、视频帧（takumi 主打 OG 动画，我们不做）
- JSX 输入、Tailwind 原子类解析（属前端构建期，交上游预处理）
- Cloudflare Workers/WASM 多运行时（我们是 .NET 库；Native AOT 另议）

---

## 4. 一页速览（建议节奏）

| 里程碑 | 主题 | 关键交付 | 成本 | 触发架构变更？ |
|---|---|---|---|---|
| **M1** | 表格+分页 | 跨页续表/rowspan/break-inside/widows | 中 | 否（复用现有）|
| **M2** | 布局 | flex-wrap/float/Grid 子集/定位补全 | 中-高 | 否 |
| **M3** | 文本+视觉 | vertical-align/balance/text-fit/conic/blend/mask/corner-shape/svg | 低-中 | 否 |
| **M4** | 输出+字体 | JPEG/WebP/ICO/SVG、Google Fonts helper、var/calc | 低-中 | 否 |
| **M5** | PDF 语义合规 | outline/链接/tagged/PDF-A/PDF-UA/附件 | 高 | **是**（PDF 后端选型）|
| **M6** | 复杂文字 | Arabic shaping + bidi + emoji | 高 | **是**（引入 HarfBuzz）|
| — | 不做 | 动画 / JSX / Tailwind / WASM | — | — |

> 数据来源：takumi README + docs 导航（<https://takumi.kane.tw/docs/>）；PaperSharp 现状来自本仓 README 与对 `invoice/report/table.pdf` 的 pymupdf 实测（文字可选中、Type0 子集字体、`MarkInfo`/`StructTreeRoot` 为 null）。
