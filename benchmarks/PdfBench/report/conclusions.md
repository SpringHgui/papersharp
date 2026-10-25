## 解读与取舍

### 1. 三条赛道不能直接混着排名

8 位选手分三类，代价结构完全不同：

- **HTML 通道**（papersharp / puppeteer / wkhtmltopdf）：直接吃 `challenge.html`，CSS 覆盖度是引擎自带能力，改设计稿零成本——适配器只有 12~55 行。
- **API 通道**（itext7 / pdfsharp / questpdf / aspose）：必须把这份挑战页**逐块手抄成 C#**（860~1180 行）。它们的高 Y 里有很大比例其实是 `A`（近似/自绘）——pdfsharp 25Y/31A、aspose 17Y/37A、itext7 与 questpdf 各 32Y/22A。也就是说这 4 家"看起来还原了"，靠的是 1200 行级的手工坐标计算和 `Svg()` 手绘，需求一变就要改代码。
- **裸画布基线**（skiasharp，1832 行 / 4 个文件）：本轮新增。把 PaperSharp 的下层——Skia 画布本身——单独拎出来当选手：没有 HTML、没有 CSS、没有盒模型、没有布局、没有分页语义，连断行和中西字体回退都要自己数度量。它衡量的是"不用任何文档引擎、直接从画布手搭三页 A4"要付多少代码、多少毫秒、多大体积。判据与 API 通道同口径：Skia 有原生原语的（圆角矩形、`PathEffect` 虚线、`MaskFilter` 模糊、`ClipPath` 裁切、`SKShader` 渐变、`SaveLayer` alpha 合成）记 Y，凡是自己数度量才画得出来的记 A。**结果 25Y/31A/0N，与 pdfsharp 逐项持平，且同样没有一项靠灰块+红叉占位蒙过去**——裸画布的能力上限够用，付的是工。

所以特性覆盖一栏，真正公平的比较只在 HTML 通道内部：**papersharp 35Y+13A（86%）> wkhtmltopdf 29Y+2A（55%）**，puppeteer 56Y 是基准本身。

> 覆盖率把 `A` 计入了，所以 ⑦/⑧ 两图上 pdfsharp、aspose、skiasharp 在"特性覆盖"轴顶格——那是**手抄工分**的量，不是 CSS 能力的量；看这两张图时请配合上表的 Y/A 拆分。

### 2. 各维度冠军

- **速度**：`pdfsharp` 48 ms 中位、par4 73 docs/s，比第二名 questpdf（101 ms）还快一倍——纯 XGraphics 直写、无布局引擎，代价是全部坐标手算。HTML 通道里 `papersharp` 437 ms 与 `wkhtmltopdf` 401 ms 基本持平，都远快于 `puppeteer` 3413 ms。`skiasharp` 1941 ms 排倒数第二：断行靠逐字符 `MeasureText`，比任何布局引擎都贵。
- **托管分配**：`wkhtmltopdf` 0.35 MB（只是起外部进程）、`questpdf` 1.3 MB 最省；`skiasharp` 6.7 MB 第 3——裸画布没有样式树、没有盒模型对象，托管侧反而最干净；`papersharp` 51 MB、`aspose` 120 MB 最重。注意 `alloc_med` 是托管分配量，和 RSS 不是一回事——`papersharp` 托管分配排第 2 高，RSS 峰值反而只有 250 MB（第 4 低），因为大头在 Skia 原生侧。
- **常驻内存**：`wkhtmltopdf` 79 MB、`questpdf` 97 MB、`skiasharp` 120 MB 最轻三档；`puppeteer` 781 MB（含 Chrome 进程树）最重。
- **体积**：`wkhtmltopdf` 98 KB 最小（几乎不嵌字体），`puppeteer` 343 KB（Chrome 子集化最勤）。`papersharp` 830 KB（大头是嵌入字体流）。`aspose` 1556 KB 含试用水印。**`skiasharp` 31.1 MB ≈ 其余 7 家之和的 12 倍**：Skia 的 PDF 后端不做字体子集化，`msyh.ttc` / Segoe UI / Consolas 整份嵌进文件。同一后端、同一台机器，PaperSharp 把这份文件压到 830 KB（**38×**）——子集化这一层值多少，一目了然。
- **冷启动**：`questpdf` 458 ms ≈ `wkhtmltopdf` 495 ms ≈ `pdfsharp` 586 ms；`itext7` 1332 ms ≈ `papersharp` 1534 ms；`skiasharp` 2770 ms（加载 Skia 原生库 + 从磁盘读三份完整字体）；`aspose` 3824 ms 与 `puppeteer` 4013 ms 慢一个数量级（前者要初始化许可与字体缓存，后者要拉起 Chrome）。
- **还原度**：`puppeteer` 0.9911 是**噪声上限**（truth 就是 Chrome 出的图，本轮重栅格几乎重合），`papersharp` 0.9583 紧随；其余 6 家挤在 0.776–0.812，差距不到 0.04：questpdf 0.8115 > skiasharp 0.8038 > pdfsharp 0.7914 > aspose 0.7857 > wkhtmltopdf 0.7846 > itext7 0.7764。这套 SSIM 尺度只适合排序、不适合宣称"绝对像"。

### 3. 并发这一栏要单独看

`threadsafe` 是"4 并发产物与串行产物剥离时间戳/`/ID` 后逐字节比对"：

- ✔ `itext7` / `pdfsharp` / `papersharp` / `questpdf` / `skiasharp` / `wkhtmltopdf`（最后一家每文档独立进程，天然安全）。
- ✘ `puppeteer`（同一 Chrome 实例共享，par4 只有 0.50 docs/s，几乎无加速）、`aspose`（par4 1.05 **低于**串行 2.94，疑全局锁，多线程反而更慢）。

服务端场景里 aspose 这一条基本否决"加线程提吞吐"的路线。`skiasharp` 串行 0.55 → par4 1.61（2.9×）说明 Skia 的文档对象之间没有全局锁——PaperSharp 的并发字节可复现是继承自这一层，不是自己挣来的。

### 4. 裸画布基线：PaperSharp 的每一层各值多少

同一台机器、同一个 Skia PDF 后端、同一份三页挑战页，唯一的区别是有没有上层：

| | skiasharp（裸画布） | papersharp（HTML 引擎） | 比值 |
|---|---|---|---|
| 适配器代码 | 1832 行 / 4 个文件 | 12 行 | 153× |
| 中位延迟 | 1941 ms | 437 ms | 4.4× |
| 托管分配 | 6.7 MB | 51.0 MB | 0.13×（裸画布更省） |
| RSS 峰值 | 120 MB | 250 MB | 0.48×（裸画布更省） |
| PDF 体积 | 31.1 MB | 830 KB | 38× |
| 冷启动 | 2770 ms | 1534 ms | 1.8× |
| 特性矩阵 | 25Y / 31A / 0N | 35Y / 13A / 7N | — |
| 还原度 SSIM | 0.804 | 0.958 | — |
| 改一次设计稿 | 重算三页坐标 | 改 HTML | — |

三句话结论：

1. **布局层买的是代码量**：12 行 vs 1832 行。这是 PaperSharp 存在的全部理由，也是它相对 pdfsharp / questpdf 那类"用 C# DSL 手写文档"方案的同一项优势——那 4 家的 860~1180 行付的是同一笔账，只是各家 API 更顺手一点。
2. **子集化买的是体积**：31.1 MB → 830 KB。裸 Skia 整字体直嵌，中文文档动辄几十 MB；这一层与 HTML 无关，是 PaperSharp 在 Skia 之上补出来的。
3. **托管层不是白拿的，但也没那么贵**：裸画布省掉了样式树和盒模型对象（托管分配只有 1/7.6、RSS 只有 1/2），代价是延迟 4.4×、冷启动 1.8×。自研布局引擎真正的开销不在托管堆上，而在"把 CSS 语义算对"这件事本身；反过来说，裸画布省下的内存，是用每改一次设计稿就要重画一遍坐标换来的。

### 5. PaperSharp（本项目自研引擎）现状

综合排名不在第一，但**是唯一一个"自研 CSS 引擎"能跑到 3 页全渲染 + 并发字节可复现 + SSIM 第二**的选手。本轮该列已改由 `out/cells.csv` 逐格像素对账自动判定（以 puppeteer 同格 SSIM 为天花板的相对分），结果 35Y / 13A / 7N / 1?：

1. ~~视口裁切~~ 已修：页 1 的墨迹横向范围与 truth 完全一致（x∈[106,1482] 两家同值），早期"左边距整条丢失、右侧约 10% 出页"的缺陷不再成立，SSIM 由上一轮的 0.747 升到 0.958。
2. 7 项 N 集中在**1~2px 级的短程几何**：`border-dashed` / `border-double` / `border-groove-ridge` / 单边边框、表格斑马纹与单元格线、带 alpha 的线性渐变。上一轮名单里的列表 marker、有序编号、页码胶囊、内阴影、flex 分配本轮都不再判 N——缺口从"特性没实现"变成了"细线画不准"。
3. 1 项 `?`（`border-radius-bg-clip`）是逐格对账拿不到判定，不等于不支持。另有 13 项 A 里含 `page-header-footer`、`border-width-2-4-8` 等——纵向每行 ~1px 的累积漂移（±8px 对齐实验后 6 项 N 回升为 A/Y）把整页格子一起稀释，属几何错位连带，不是该特性没画对。

### 6. 名单说明

- **IronPDF 已剔除**：2025.9.1 一调用即抛 `LicensingException: Production License Required`，试用需在线激活；它还会向项目注入 global using，污染其他适配器的命名空间。
- **Aspose.PDF 以试用版参赛**：每页左上角带评估水印，且体积/分配数据含水印开销，横向比较时需打折看。
- **SkiaSharp 2.88.9 本轮以裸画布基线参赛**：它正是 PaperSharp 出 PDF 用的下层（`SKDocument.CreatePdf`），用来量化"没有文档引擎"要付的代价。它从本机 `C:\Windows\Fonts` 按文件载入雅黑 / Segoe UI / Consolas，换一台没有这些字体的机器结果会变——这是所有 Windows 字体依赖选手的共同口径，不是新缺陷。
- 未纳入：PdfPig、NetEase/PDF-Sharp 之类，名单按用户指定执行。

### 7. 选型建议（一句话版）

| 场景 | 建议 |
|---|---|
| 只要"和网页一模一样"、不在乎拉起浏览器 | puppeteer（还原上限，但 3.4 s / 781 MB / 不可并发） |
| 高并发服务端批量出报表、样式可控 | pdfsharp（48 ms / 73 docs/s）或 questpdf（101 ms / 1.3 MB 分配），接受手工排版 |
| 要 CSS 语义、又要托管内自研可控 | papersharp（437 ms、SSIM 0.958、并发可复现），再补虚线/双线边框与表格细线那 7 项 |
| 只要文件小、静态老式布局 | wkhtmltopdf（98 KB / 79 MB），但无 flex、无 CSS 渐变，尾部内容会截断 |
| 只要一张画布、文档结构自己扛 | skiasharp（25Y/31A 全靠 1832 行手工几何，31 MB 未裁字体）——等于把 PaperSharp 的上层自己重造一遍，除非需求就是"只有画布" |
