## 解读与取舍

### 1. 两条赛道不能直接混着排名

7 位选手分两类，代价结构完全不同：

- **HTML 通道**（papersharp / puppeteer / wkhtmltopdf）：直接吃 `challenge.html`，CSS 覆盖度是引擎自带能力，改设计稿零成本。
- **API 通道**（itext7 / pdfsharp / questpdf / aspose）：必须把这份挑战页**逐块手抄成 C#**。它们的高 Y 里有很大比例其实是 `A`（近似/自绘）——pdfsharp 25Y/31A、aspose 17Y/37A、itext7 与 questpdf 各 32Y/22A。也就是说这 4 家"看起来还原了"，靠的是 1200 行级的手工坐标计算和 `Svg()` 手绘，需求一变就要改代码。

所以特性覆盖一栏，真正公平的比较只在 HTML 通道内部：**papersharp 36Y+3A（70%）> wkhtmltopdf 29Y+2A（55%）**，puppeteer 56Y 是基准本身。

> 覆盖率把 `A` 计入了，所以 ⑦/⑧ 两图上 pdfsharp、aspose 在"特性覆盖"轴顶格——那是**手抄工分**的量，不是 CSS 能力的量；看这两张图时请配合上表的 Y/A 拆分。

### 2. 各维度冠军

- **速度**：`pdfsharp` 30 ms 中位、par4 68 docs/s，比第二名 questpdf（84 ms）还快近 3 倍——纯 XGraphics 直写、无布局引擎，代价是全部坐标手算。HTML 通道里 `papersharp` 238 ms 明显优于 wkhtmltopdf 370 ms 和 puppeteer 2769 ms。
- **托管分配**：`wkhtmltopdf` 266 KB（只是起外部进程）、`questpdf` 1.3 MB 最省；`aspose` 120 MB/文档、`papersharp` 54 MB 最重。注意 `alloc_med` 是托管分配量，和 RSS 不是一回事——`papersharp` 托管分配排第 2 高，RSS 峰值反而只有 159 MB（第 3 低），因为大头在 Skia 原生侧。
- **常驻内存**：`wkhtmltopdf` 77 MB、`questpdf` 95 MB 最轻；`puppeteer` 795 MB（含 Chrome 进程树）最重。
- **体积**：`wkhtmltopdf` 98 KB 最小（几乎不嵌字体），`puppeteer` 343 KB（Chrome 子集化最勤，10 段字体流最大 115 KB）。`aspose` 1556 KB 含试用水印。`papersharp` 710 KB 里有约 614 KB 是 6 段嵌入字体流（最大单段 257 KB）——整字体直嵌、没裁子集，是体积第一项可回收的开销。
- **冷启动**：`questpdf` 404 ms ≈ `pdfsharp` 448 ms ≈ `wkhtmltopdf` 454 ms；`puppeteer` 3073 ms 与 `aspose` 3141 ms 慢一个数量级（前者要拉起 Chrome，后者要初始化许可与字体缓存）。
- **还原度**：`puppeteer` 0.850 是**噪声上限**（truth 就是 Chrome 2× 截图，对 WinRT 栅格天然有 ≤2px 错位+重采样差），其余 6 家挤在 0.747–0.803 之间，差距不到 0.06，说明这套 SSIM 尺度只适合排序、不适合宣称"绝对像"。

### 3. 并发这一栏要单独看

`threadsafe` 是"4 并发产物与串行产物剥离时间戳/`/ID` 后逐字节比对"：

- ✔ `itext7` / `pdfsharp` / `papersharp` / `wkhtmltopdf`（每文档独立进程，天然安全）。
- ✘ `puppeteer`（同一 Chrome 实例共享，par4 只有 0.77 docs/s，几乎无加速）、`aspose`（par4 1.03 **低于**串行 3.86，疑全局锁，多线程反而更慢）、`questpdf`（剥离易变字段后字节仍不可复现，未定位到根因）。

服务端场景里 aspose 这一条基本否决"加线程提吞吐"的路线。

### 4. PaperSharp（本项目自研引擎）现状

综合排名不在第一，但**是唯一一个"自研 CSS 引擎"能跑到 3 页全渲染 + 并发字节可复现 + RSS 最低档**的选手。已确认的缺口按影响排序：

1. **视口比 A4 宽且左对齐**：每页**左边距整条丢失**（标题从 x=0 起排），同时**右侧约 10% 被裁**——表格"数量/单价/合计"三列、flex 第 4 格、合计金额 ¥6,654.50 的末位都被切在页外。这是单点布局缺陷，也是 SSIM 垫底（0.747）的主因，而非特性缺失；4 项特性因落在被裁区无从判定。
2. 列表 marker 与有序编号缺失、页脚与页码缺失。
3. `text-indent`、`text-shadow`、`double` 边框、border 三角形、图片圆角裁切未实现（共 13 项 N）。

好消息：本轮重测时该 PDF 已被并行会话重新生成为 3 页、无空白首页（早期版本 4 页且首页全白），评分按最新版如实算。

### 5. 名单说明

- **IronPDF 已剔除**：2025.9.1 一调用即抛 `LicensingException: Production License Required`，试用需在线激活；它还会向项目注入 global using，污染其他适配器的命名空间。
- **Aspose.PDF 以试用版参赛**：每页左上角带评估水印，且体积/分配数据含水印开销，横向比较时需打折看。
- 未纳入：SkiaSharp 裸画、PdfPig、NetEase/PDF-Sharp 之类，名单按用户指定执行。

### 6. 选型建议（一句话版）

| 场景 | 建议 |
|---|---|
| 只要"和网页一模一样"、不在乎拉起浏览器 | puppeteer（还原上限，但 2.8 s / 795 MB / 不可并发） |
| 高并发服务端批量出报表、样式可控 | pdfsharp（30 ms / 68 docs/s）或 questpdf（84 ms / 1.3 MB 分配），接受手工排版 |
| 要 CSS 语义、又要托管内自研可控 | papersharp（先修视口裁切，再补列表/页脚），当前 238 ms 已快过 wkhtmltopdf 与 puppeteer |
| 只要文件小、静态老式布局 | wkhtmltopdf（98 KB / 77 MB），但无 flex、无 CSS 渐变，尾部内容会截断 |
