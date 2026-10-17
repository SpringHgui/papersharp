# PdfBench 基准报告：7 个 .NET/无头浏览器方案生成同一份 HTML 挑战页

> 生成时间：2026-09-27 18:54 ｜ 数据源：`G:\Github\papersharp\benchmarks\PdfBench\out`（真实测量）。

## 方法学

- **延迟**：warmup 5 次 + 30 次迭代，取 min/med/mean/p95/max；报告展示 med 柱 + p95 误差线。
- **GC**：`server=false`（workstation GC）；`alloc_med` 为单次生成 `GC.GetTotalAllocatedBytes` 差值中位数（托管分配，非占用）；gen0/1/2 为 30 迭代期间回收次数。
- **隔离**：每个库在**全新子进程**里测（`measure-one`），避免 Skia 等原生内存跨库累积把 `rss_peak` 抬到 GB 级；`rss_peak` 用 50 ms 轮询 Toolhelp32 进程树工作集求和，外部进程选手（puppeteer/wkhtmltopdf）含其 Chrome / wkhtmltopdf 子进程。
- **还原度**：truth 为无头 Chrome 以 `--force-device-scale-factor=2` 截的 2× 页图（裁去窗口白边到 1588px），各库 PDF 用 `raster.ps1`（WinRT PdfDocument）栅格化后逐页对拍 SSIM/MSE/pixel-diff（Δ灰度>10）。
- **特性矩阵**：56 项特性逐项判定 `Y`（原生/正确渲染）`A`（近似/手画）`N`（不支持/占位）`?`（未判定）；页面未实现的特性一律画**占位块**并计入 N。
- **逐格对账（PaperSharp 列改用实测）**：整页 SSIM 会被大面积留白稀释，故另用 `out/cells.json`（无头 Chrome 现场量出的每个特性矩形）把基准图与引擎页图切成 109 个格子逐格算 SSIM。truth 与任何 PDF 栅格之间天然有字体/亚像素噪声，同格 puppeteer（Chrome 自己走一遍 PDF）也只有 0.90~0.94，所以判定用**相对分** Δ = PaperSharp SSIM − puppeteer SSIM：Δ≥−0.03→Y，Δ≥−0.12→A，更差→N。
- **吞吐**：3 秒窗口内完成文档数（docs/s），串行与 4 线程各测一次；`threadsafe`=4 并发输出与串行字节一致。
- **冷启动**：独立进程单次生成总墙钟（含 JIT/字体/许可初始化）。

## 测试环境

| 项 | 值 |
|---|---|
| 操作系统 | Windows 11 |
| CPU | Intel64 Family 6 Model 167 Stepping 1, GenuineIntel / 16 逻辑核 |
| 物理内存 | 32 GB |
| .NET SDK | 10.0.302 |
| Python | 3.14.6 / matplotlib 3.11.2 / numpy 2.5.3 |
| Chrome（puppeteer 复用） | 154.0.8037.57 |
| wkhtmltopdf | wkhtmltopdf 0.12.5 (with patched qt) |
| 被测库版本 | QuestPDF 2025.7.0 · itext7 8.0.2 · itext7.bouncy-castle-adapter 8.0.2 · PDFsharp-MigraDoc 6.2.4 · PuppeteerSharp 25.8.0 · Aspose.PDF 26.9.0 · PaperSharp（仓库内 src/PaperSharp 本地工程） |
| 代码版本 | 29c2fb4 |

- 挑战页：`spec/challenge.html`（3 页 A4，56 项特性（逐页 14/17/23 + 文档级 2）：text 14 · border 11 · effect 8 · radius 6 · layout 5 · table 4 · gradient 3 · image 3 · doc 2）
- 原始数据：`out/results.csv`（7 库 × 16 指标）、`out/fidelity.csv`（21 行）、`out/features_matrix.csv`（56 特性 × 7 库）；栅格图 `out/*_pN.png`。

## 全指标总表

| 库 | 延迟min(ms) | 延迟med(ms) | 延迟mean(ms) | 延迟p95(ms) | 延迟max(ms) | 分配med(KB) | Gen0 | Gen1 | Gen2 | RSS峰值(MB) | 体积(KB) | 页数 | 吞吐串行(docs/s) | 吞吐4线(docs/s) | 冷启动(ms) | 线程安全 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **papersharp** | 209.3 | 238.0 | 258.7 | 341.8 | 371.6 | 55,692 | 212.0 | 46.00 | 26.00 | 159.3 | 709.9 | 3 | 4.62 | 11.18 | 1,070 | ✔ |
| **puppeteer** | 2,720 | 2,769 | 2,802 | 2,988 | 3,158 | 4,499 | 22.00 | 14.00 | 12.00 | 795.2 | 343.0 | 3 | 0.36 | 0.77 | 3,073 | ✘ |
| **wkhtmltopdf** | 349.7 | 369.6 | 372.6 | 404.2 | 431.3 | 265.8 | 1.00 | 0.00 | 0.00 | 77.18 | 98.32 | 3 | 2.73 | 10.04 | 453.7 | ✔ |
| **itext7** | 91.51 | 107.5 | 134.7 | 198.9 | 269.1 | 18,577 | 54.00 | 21.00 | 3.00 | 243.3 | 589.0 | 3 | 10.22 | 27.79 | 951.7 | ✔ |
| **pdfsharp** | 28.14 | 30.00 | 30.35 | 34.92 | 35.79 | 10,769 | 18.00 | 7.00 | 3.00 | 256.6 | 509.4 | 3 | 28.94 | 67.93 | 448.4 | ✔ |
| **questpdf** | 76.02 | 84.47 | 85.52 | 94.33 | 108.2 | 1,317 | 5.00 | 1.00 | 0.00 | 94.72 | 620.1 | 3 | 11.73 | 28.15 | 404.1 | ✘ |
| **aspose** | 240.6 | 274.9 | 322.7 | 686.1 | 822.5 | 122,982 | 355.0 | 107.0 | 11.00 | 435.7 | 1,556 | 3 | 3.86 | 1.03 | 3,141 | ✘ |

## 派生指标（由上表算出）

| 库 | 并发加速比(par4/串行) | 抖动(p95/med) | 每页耗时(ms) | 每页体积(KB) | 每页托管分配(KB) | 托管分配÷RSS峰值 |
|---|---|---|---|---|---|---|
| **papersharp** | 2.42 | 1.44 | 79.33 | 236.6 | 18,564 | 0.34 |
| **puppeteer** | 2.14 | 1.08 | 923.2 | 114.3 | 1,500 | 0.01 |
| **wkhtmltopdf** | 3.68 | 1.09 | 123.2 | 32.77 | 88.59 | 0.00 |
| **itext7** | 2.72 | 1.85 | 35.82 | 196.3 | 6,192 | 0.07 |
| **pdfsharp** | 2.35 | 1.16 | 10.00 | 169.8 | 3,590 | 0.04 |
| **questpdf** | 2.40 | 1.12 | 28.16 | 206.7 | 439.1 | 0.01 |
| **aspose** | 0.27 | 2.50 | 91.64 | 518.7 | 40,994 | 0.28 |

> 加速比≈1 说明加线程没用（外部进程被串行化或库内有锁）；`托管分配/RSS` 远小于 1 说明内存大头在原生侧（Skia/Chrome），光看 .NET 分配会严重低估。

## 图表

### 01 延迟

![01 延迟](charts/01_latency.png)

### 02 内存

![02 内存](charts/02_memory.png)

### 03 输出体积

![03 输出体积](charts/03_size.png)

### 04 吞吐与加速比

![04 吞吐与加速比](charts/04_throughput.png)

### 05 冷启动

![05 冷启动](charts/05_coldstart.png)

### 06 视觉还原度

![06 视觉还原度](charts/06_fidelity.png)

### 07 特性覆盖

![07 特性覆盖](charts/07_features.png)

### 08 五维雷达

![08 五维雷达](charts/08_radar.png)

### 09 第 1 页视觉对照

![09 第 1 页视觉对照](charts/09_compare_p1.png)

### 10 第 2 页视觉对照

![10 第 2 页视觉对照](charts/10_compare_p2.png)

### 11 第 3 页视觉对照

![11 第 3 页视觉对照](charts/11_compare_p3.png)

### 12 逐格对账最差特性

![12 逐格对账最差特性](charts/12_cells.png)

## 还原度汇总（逐页均值，NaN 页跳过）

| 库 | 平均SSIM | 平均MSE | 平均像素差% | 覆盖页数 |
|---|---|---|---|---|
| papersharp | 0.85 | 1,090 | 8.73 | 3 |
| puppeteer | 0.85 | 1,087 | 8.40 | 3 |
| wkhtmltopdf | 0.78 | 2,550 | 23.60 | 3 |
| itext7 | 0.78 | 2,214 | 19.83 | 3 |
| pdfsharp | 0.79 | 2,154 | 16.75 | 3 |
| questpdf | 0.80 | 2,036 | 17.41 | 3 |
| aspose | 0.79 | 2,122 | 18.77 | 3 |

## 逐页还原度明细

| 库 | 第1页 SSIM / 像素差% / 落墨% | 第2页 SSIM / 像素差% / 落墨% | 第3页 SSIM / 像素差% / 落墨% |
|---|---|---|---|
| **papersharp** | 0.905 / 5.0 / 7.1 | 0.852 / 7.8 / 13.2 | 0.789 / 13.5 / 28.0 |
| **puppeteer** | 0.896 / 5.2 / 7.3 | 0.856 / 7.2 / 13.0 | 0.797 / 12.8 / 26.7 |
| **wkhtmltopdf** | 0.814 / 13.4 / 12.7 | 0.718 / 26.5 / 15.4 | 0.805 / 31.0 / 7.2 |
| **itext7** | 0.804 / 14.7 / 7.3 | 0.767 / 21.0 / 16.1 | 0.766 / 23.8 / 27.3 |
| **pdfsharp** | 0.823 / 9.7 / 8.0 | 0.789 / 15.6 / 13.4 | 0.754 / 24.9 / 28.4 |
| **questpdf** | 0.847 / 8.4 / 7.7 | 0.814 / 12.7 / 13.1 | 0.747 / 31.1 / 28.2 |
| **aspose** | 0.817 / 10.4 / 7.0 | 0.791 / 19.7 / 14.0 | 0.762 / 26.2 / 25.8 |

> `落墨%` 是该页非白像素占比，用来识别「整页没画 / 画少了」的退化：某库某页的落墨% 明显低于 truth 而像素差% 很高，通常是内容被截断或整块未渲染，而不是画得不像。

> truth 落墨%（基准，越接近越好）：第1页 7.0 ｜ 第2页 12.1 ｜ 第3页 25.9

## 特性覆盖计数

| 库 | Y | A | N | ? | 覆盖率(Y+A) |
|---|---|---|---|---|---|
| papersharp | 42 | 11 | 2 | 1 | 95% |
| puppeteer | 56 | 0 | 0 | 0 | 100% |
| wkhtmltopdf | 29 | 2 | 11 | 14 | 55% |
| itext7 | 32 | 22 | 2 | 0 | 96% |
| pdfsharp | 25 | 31 | 0 | 0 | 100% |
| questpdf | 32 | 22 | 2 | 0 | 96% |
| aspose | 17 | 37 | 2 | 0 | 96% |

## 完整特性矩阵（56 项 × 7 库）

| 特性 | 分组 | 页 | papersharp | puppeteer | wkhtmltopdf | itext7 | pdfsharp | questpdf | aspose |
|---|---|---|---|---|---|---|---|---|---|
| `headings` | text | 1 | 🟡 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `weights` | text | 1 | ❌ | ✅ | 🟡 | 🟡 | 🟡 | ✅ | 🟡 |
| `inline-code-chip` | text | 1 | ❌ | ✅ | ✅ | ✅ | 🟡 | ✅ | 🟡 |
| `cjk-latin-mixed` | text | 1 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `line-height` | text | 1 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `letter-spacing` | text | 1 | ✅ | ✅ | ❌ | ✅ | 🟡 | ✅ | ✅ |
| `text-indent` | text | 1 | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ | 🟡 |
| `text-shadow` | effect | 1 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 |
| `text-color` | text | 1 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `text-align-justify` | text | 1 | ✅ | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 |
| `list-ul-marker-color` | text | 1 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 |
| `list-ol-decimal-alpha-roman` | text | 1 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 |
| `list-style-circle-none` | text | 1 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 |
| `nested-list` | text | 1 | ✅ | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 |
| `border-solid` | border | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `border-dashed` | border | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ |
| `border-dotted` | border | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ |
| `border-double` | border | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | 🟡 |
| `border-groove-ridge` | border | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | 🟡 |
| `border-width-2-4-8` | border | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `border-per-side-style` | border | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ |
| `border-per-side-color` | border | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ |
| `border-currentcolor` | border | 2 | ✅ | ✅ | ✅ | 🟡 | 🟡 | ✅ | 🟡 |
| `border-single-side` | border | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `radius-uniform` | radius | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 |
| `radius-per-corner` | radius | 2 | ✅ | ✅ | ❔ | ✅ | ✅ | ✅ | 🟡 |
| `radius-circle-ellipse` | radius | 2 | ✅ | ✅ | ❔ | 🟡 | ✅ | 🟡 | ✅ |
| `radius-pill` | radius | 2 | ✅ | ✅ | ❔ | ✅ | ✅ | ✅ | 🟡 |
| `radius-plus-dashed` | radius | 2 | ✅ | ✅ | ❔ | ✅ | ✅ | 🟡 | 🟡 |
| `border-radius-bg-clip` | radius | 2 | ❔ | ✅ | ❔ | ✅ | ✅ | 🟡 | 🟡 |
| `triangle-via-border` | border | 2 | ✅ | ✅ | ❔ | 🟡 | 🟡 | 🟡 | 🟡 |
| `box-shadow-soft` | effect | 3 | ✅ | ✅ | ✅ | ❌ | 🟡 | ✅ | ❌ |
| `box-shadow-hard` | effect | 3 | ✅ | ✅ | ✅ | 🟡 | ✅ | ✅ | ✅ |
| `box-shadow-inset` | effect | 3 | 🟡 | ✅ | ❌ | ❌ | 🟡 | ❌ | ❌ |
| `box-shadow-multi` | effect | 3 | ✅ | ✅ | ❌ | 🟡 | 🟡 | 🟡 | 🟡 |
| `linear-gradient-multi-stop` | gradient | 3 | ✅ | ✅ | ❌ | 🟡 | ✅ | 🟡 | 🟡 |
| `radial-gradient` | gradient | 3 | 🟡 | ✅ | ❌ | 🟡 | 🟡 | 🟡 | 🟡 |
| `gradient-with-alpha` | gradient | 3 | ✅ | ✅ | ❌ | 🟡 | 🟡 | 🟡 | 🟡 |
| `rgba-overlap-blend` | effect | 3 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 |
| `opacity` | effect | 3 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 |
| `inline-alpha-highlight` | effect | 3 | ✅ | ✅ | ✅ | 🟡 | 🟡 | ✅ | 🟡 |
| `image-jpeg` | image | 3 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `image-png-alpha` | image | 3 | ✅ | ✅ | ❔ | ✅ | ✅ | ✅ | 🟡 |
| `image-rounded-border` | image | 3 | ✅ | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 |
| `flex-space-between` | layout | 3 | 🟡 | ✅ | ❌ | 🟡 | 🟡 | ✅ | 🟡 |
| `flex-margin-auto` | layout | 3 | 🟡 | ✅ | ❌ | 🟡 | 🟡 | ✅ | 🟡 |
| `flex-wrap-gap` | layout | 3 | ✅ | ✅ | ❌ | 🟡 | 🟡 | 🟡 | 🟡 |
| `flex-nested-ratios` | layout | 3 | 🟡 | ✅ | ❌ | 🟡 | 🟡 | ✅ | 🟡 |
| `flex-cross-axis-center` | layout | 3 | ✅ | ✅ | ❌ | 🟡 | 🟡 | ✅ | 🟡 |
| `table-thead-tfoot` | table | 3 | ✅ | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 |
| `table-striped` | table | 3 | 🟡 | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 |
| `table-cell-borders` | table | 3 | 🟡 | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 |
| `table-colspan-footer` | table | 3 | 🟡 | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 |
| `word-break-all` | text | 3 | ✅ | ✅ | ❔ | 🟡 | 🟡 | ❌ | 🟡 |
| `page-header-footer` | doc | all | 🟡 | ✅ | 🟡 | ✅ | ✅ | ✅ | ✅ |
| `page-number-badge` | doc | all | 🟡 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

> ✅ 原生机制正确渲染 ｜ 🟡 近似/手画（视觉对但靠自绘、预混合、条纹、复制文字等） ｜ ❌ 不支持或灰块+红✗占位 ｜ ❔ 落在被裁/被截断区域，无从判定

## PaperSharp 逐格对账明细（`out/cells.csv`）

Δ = 本网格 PaperSharp SSIM − puppeteer SSIM（puppeteer＝Chrome 走同一 PDF 栅格通路，是同格能到的天花板）。特性级取**最差格**：Δ≥−0.03→Y ｜ Δ≥−0.12→A ｜ 更差→N。

| 特性 | 分组 | 格数 | 判定 | 最差Δ | 最差格页 | 格均Δ |
|---|---|---|---|---|---|---|
| `inline-code-chip` | text | 2 | N | -0.282 | 1 | -0.242 |
| `weights` | text | 1 | N | -0.188 | 1 | -0.188 |
| `table-cell-borders` | table | 31 | A | -0.080 | 3 | -0.045 |
| `radial-gradient` | gradient | 1 | A | -0.077 | 3 | -0.077 |
| `table-striped` | table | 5 | A | -0.069 | 3 | -0.060 |
| `box-shadow-inset` | effect | 1 | A | -0.059 | 3 | -0.059 |
| `table-colspan-footer` | table | 1 | A | -0.054 | 3 | -0.054 |
| `page-header-footer` | doc | 6 | A | -0.053 | 2 | -0.008 |
| `headings` | text | 1 | A | -0.051 | 1 | -0.051 |
| `flex-space-between` | layout | 8 | A | -0.041 | 3 | -0.017 |
| `flex-nested-ratios` | layout | 3 | A | -0.035 | 3 | -0.006 |
| `page-number-badge` | doc | 3 | A | -0.033 | 3 | +0.075 |
| `flex-margin-auto` | layout | 1 | A | -0.031 | 3 | -0.031 |
| `box-shadow-multi` | effect | 1 | Y | -0.029 | 3 | -0.029 |
| `border-groove-ridge` | border | 1 | Y | -0.028 | 2 | -0.028 |
| `radius-circle-ellipse` | radius | 1 | Y | -0.027 | 2 | -0.027 |
| `border-double` | border | 1 | Y | -0.027 | 2 | -0.027 |
| `radius-pill` | radius | 1 | Y | -0.023 | 2 | -0.023 |
| `word-break-all` | text | 1 | Y | -0.018 | 3 | -0.018 |
| `box-shadow-hard` | effect | 1 | Y | -0.013 | 3 | -0.013 |
| `table-thead-tfoot` | table | 1 | Y | -0.013 | 3 | -0.013 |
| `border-currentcolor` | border | 1 | Y | -0.012 | 2 | -0.012 |
| `border-single-side` | border | 1 | Y | -0.009 | 2 | -0.009 |
| `triangle-via-border` | border | 1 | Y | -0.008 | 2 | -0.008 |
| `border-per-side-color` | border | 1 | Y | -0.006 | 2 | -0.006 |
| `image-jpeg` | image | 3 | Y | -0.003 | 3 | -0.001 |
| `image-rounded-border` | image | 1 | Y | -0.003 | 3 | -0.003 |
| `border-per-side-style` | border | 1 | Y | -0.003 | 2 | -0.003 |
| `border-dashed` | border | 1 | Y | -0.002 | 2 | -0.002 |
| `box-shadow-soft` | effect | 1 | Y | -0.002 | 3 | -0.002 |
| `flex-cross-axis-center` | layout | 2 | Y | -0.002 | 3 | -0.001 |
| `cjk-latin-mixed` | text | 1 | Y | -0.002 | 1 | -0.002 |
| `linear-gradient-multi-stop` | gradient | 1 | Y | -0.000 | 3 | -0.000 |
| `rgba-overlap-blend` | effect | 1 | Y | -0.000 | 3 | -0.000 |
| `radius-plus-dashed` | radius | 1 | Y | +0.001 | 2 | +0.001 |
| `image-png-alpha` | image | 1 | Y | +0.001 | 3 | +0.001 |
| `flex-wrap-gap` | layout | 1 | Y | +0.001 | 3 | +0.001 |
| `radius-per-corner` | radius | 1 | Y | +0.002 | 2 | +0.002 |
| `border-dotted` | border | 1 | Y | +0.004 | 2 | +0.004 |
| `radius-uniform` | radius | 1 | Y | +0.005 | 2 | +0.005 |
| `list-ol-decimal-alpha-roman` | text | 1 | Y | +0.005 | 1 | +0.005 |
| `inline-alpha-highlight` | effect | 1 | Y | +0.006 | 3 | +0.006 |
| `text-color` | text | 1 | Y | +0.007 | 1 | +0.007 |
| `text-shadow` | effect | 1 | Y | +0.007 | 1 | +0.007 |
| `nested-list` | text | 2 | Y | +0.010 | 1 | +0.012 |
| `border-width-2-4-8` | border | 1 | Y | +0.011 | 2 | +0.011 |
| `letter-spacing` | text | 1 | Y | +0.011 | 1 | +0.011 |
| `border-solid` | border | 1 | Y | +0.017 | 2 | +0.017 |
| `text-indent` | text | 1 | Y | +0.018 | 1 | +0.018 |
| `text-align-justify` | text | 1 | Y | +0.018 | 1 | +0.018 |
| `list-style-circle-none` | text | 1 | Y | +0.024 | 1 | +0.024 |
| `list-ul-marker-color` | text | 1 | Y | +0.024 | 1 | +0.024 |
| `gradient-with-alpha` | gradient | 1 | Y | +0.044 | 3 | +0.044 |
| `line-height` | text | 1 | Y | +0.088 | 1 | +0.088 |

- 特性级：Y 41 ｜ A 11 ｜ N 2 ｜ ? 0（共 54 项）
- 格级：Y 65 ｜ A 41 ｜ N 3 ｜ ? 0（共 109 格）
- 与整页 SSIM 的分工：整页分（`fidelity.csv`）看**排得出高低**，逐格分看**差在哪一块**；上表 N 里若一批特性的最差格同页且 Δ 同量级，通常是纵向漂移而非该特性本身没实现，用 `python fidelity/cells.py --pair <n>` 导出左右对照图确认。

## 矩阵备注

- **papersharp**：该列由 fidelity/cells.py 逐特性像素对账自动判定（以 puppeteer 同格 SSIM 为天花板的相对分），不再手填；3 页齐全，横向内容与 Chrome 对齐（最右墨迹列一致）。剩余主因是纵向逐段累积漂移：页1 到 css y=500 已比 Chrome 低约 10px，故每个格子都被平移稀释。
- **wkhtmltopdf**：p2自2.3圆角第3格起、p3自3.3第二张图起内容被截断(仅出3页)，被截断区域的特性记?。

## 各维度最优 / 最差

- **延迟 med（越低越好）**：最优 `pdfsharp`（30.00），最差 `puppeteer`（2,769）
- **RSS 峰值（越低越好）**：最优 `wkhtmltopdf`（77.18），最差 `puppeteer`（795.2）
- **输出体积（越低越好）**：最优 `wkhtmltopdf`（98.32），最差 `aspose`（1,556）
- **4 线程吞吐（越高越好）**：最优 `pdfsharp`（67.93），最差 `puppeteer`（0.77）
- **冷启动（越低越好）**：最优 `questpdf`（404.1），最差 `aspose`（3,141）
- **平均 SSIM（越高越好）**：最优 `puppeteer`（0.85），最差 `wkhtmltopdf`（0.78）
- **特性覆盖 Y+A（越高越好）**：最优 `puppeteer`（56.00），最差 `wkhtmltopdf`（31.00）

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

## 附录：复现与口径

```powershell
cd benchmarks/PdfBench
dotnet build PdfBench.Runner -c Debug
$exe = "PdfBench.Runner/bin/Debug/net10.0/PdfBench.Runner.exe"
& $exe measure            # → out/results.csv（每库一个子进程）
python fidelity/score.py  # → out/fidelity.csv + out/features_matrix.csv
& $exe cells              # → out/cells.json（无头 Chrome 现场量每个特性矩形，只准不改渲染）
python fidelity/cells.py  # → out/cells.csv（逐格 Δ，喂上面 PaperSharp 列的实测判定）
python report/report.py   # → report/charts/*.png + REPORT.md（本文件）
```

- **必须每库一个子进程测**：同进程连测 7 家会让 Skia 原生内存累积，`rss_peak` 虚高到 3–4 GB；隔离后才是真实的 77–795 MB。
- **`threadsafe` 的比对口径**：剥离 `/CreationDate`、`/ID`、Xmp 日期与 iText 字体子集随机前缀（`/ABCDEF+`）后逐字节比较；不剥会产生假 0。
- **`out/<lib>.pdf` 与测量用的临时产物不是同一个文件**：Aspose 两次生成体积能差约 10%（水印与子集前缀），体积榜一律以 `results.csv` 为准。
- **还原度上限不是 1.0**：truth 来自 Chrome 截图、对照图来自 WinRT 栅格，天然 ≤2px 错位，puppeteer 自己也只有 0.850，因此 SSIM 只用于排序。
- **wkhtmltopdf 依赖便携版**：官方 GitHub release 无 Windows 包，用 0.12.5 mxe-cross-win64 的 7z，以 `C:\Windows\System32\tar.exe` 解到 `~/wkhtmltox`。
- **IronPDF 未参赛**：2025.9.1 一调用即 `LicensingException: Production License Required`，且会向工程注入 global using 污染其他适配器命名空间。

产物清单：`out/results.csv`、`out/fidelity.csv`、`out/features_matrix.csv`、`out/cells.json` + `out/cells.csv`（逐格）、`out/<lib>.pdf` ×7、`out/<lib>_pN.png` ×24、`report/charts/*.png` ×12。
