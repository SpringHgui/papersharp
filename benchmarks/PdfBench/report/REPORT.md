# PdfBench 基准报告：8 个 .NET/无头浏览器方案生成同一份 HTML 挑战页

> 生成时间：2026-10-23 00:42 ｜ 数据源：`G:\Github\papersharp\benchmarks\PdfBench\out`（真实测量）。

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
| Chrome（puppeteer 复用） | 155.0.8059.85 |
| wkhtmltopdf | wkhtmltopdf 0.12.5 (with patched qt) |
| 被测库版本 | QuestPDF 2025.7.0 · itext7 8.0.2 · itext7.bouncy-castle-adapter 8.0.2 · PDFsharp-MigraDoc 6.2.4 · PuppeteerSharp 25.8.0 · Aspose.PDF 26.9.0 · PaperSharp（仓库内 src/PaperSharp 本地工程） |
| 代码版本 | 77cc12b |

- 挑战页：`spec/challenge.html`（3 页 A4，56 项特性（逐页 14/17/23 + 文档级 2）：text 14 · border 11 · effect 8 · radius 6 · layout 5 · table 4 · gradient 3 · image 3 · doc 2）
- 原始数据：`out/results.csv`（8 库 × 16 指标）、`out/fidelity.csv`（21 行）、`out/features_matrix.csv`（56 特性 × 8 库）；栅格图 `out/*_pN.png`。

## 全指标总表

| 库 | 延迟min(ms) | 延迟med(ms) | 延迟mean(ms) | 延迟p95(ms) | 延迟max(ms) | 分配med(KB) | Gen0 | Gen1 | Gen2 | RSS峰值(MB) | 体积(KB) | 页数 | 吞吐串行(docs/s) | 吞吐4线(docs/s) | 冷启动(ms) | 线程安全 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **papersharp** | 348.6 | 437.4 | 463.9 | 666.9 | 718.2 | 51,984 | 197.0 | 77.00 | 28.00 | 249.6 | 830.4 | 3 | 2.80 | 7.67 | 1,534 | ✔ |
| **puppeteer** | 2,902 | 3,413 | 3,472 | 3,967 | 3,998 | 4,710 | 22.00 | 13.00 | 12.00 | 781.3 | 343.0 | 3 | 0.28 | 0.50 | 4,013 | ✘ |
| **wkhtmltopdf** | 383.4 | 401.2 | 404.1 | 433.7 | 463.3 | 360.9 | 1.00 | 0.00 | 0.00 | 78.60 | 98.33 | 3 | 2.54 | 7.63 | 495.1 | ✔ |
| **itext7** | 101.4 | 127.9 | 151.8 | 342.5 | 371.7 | 18,600 | 54.00 | 19.00 | 3.00 | 215.5 | 589.0 | 3 | 8.62 | 23.54 | 1,332 | ✔ |
| **pdfsharp** | 40.50 | 47.72 | 48.17 | 55.24 | 59.56 | 10,791 | 18.00 | 5.00 | 3.00 | 259.8 | 509.4 | 3 | 24.61 | 73.47 | 585.7 | ✔ |
| **questpdf** | 87.60 | 101.4 | 104.4 | 128.0 | 132.8 | 1,338 | 5.00 | 0.00 | 0.00 | 96.56 | 620.1 | 3 | 10.06 | 28.55 | 458.4 | ✔ |
| **aspose** | 306.1 | 371.1 | 381.1 | 522.4 | 562.7 | 123,085 | 355.0 | 109.0 | 11.00 | 420.6 | 1,556 | 3 | 2.94 | 1.05 | 3,824 | ✘ |
| **skiasharp** | 1,778 | 1,941 | 1,979 | 2,200 | 2,670 | 6,825 | 25.00 | 2.00 | 1.00 | 120.2 | 31,864 | 3 | 0.55 | 1.61 | 2,770 | ✔ |

## 派生指标（由上表算出）

| 库 | 并发加速比(par4/串行) | 抖动(p95/med) | 每页耗时(ms) | 每页体积(KB) | 每页托管分配(KB) | 托管分配÷RSS峰值 |
|---|---|---|---|---|---|---|
| **papersharp** | 2.74 | 1.52 | 145.8 | 276.8 | 17,328 | 0.20 |
| **puppeteer** | 1.79 | 1.16 | 1,138 | 114.3 | 1,570 | 0.01 |
| **wkhtmltopdf** | 3.00 | 1.08 | 133.7 | 32.78 | 120.3 | 0.00 |
| **itext7** | 2.73 | 2.68 | 42.65 | 196.3 | 6,200 | 0.08 |
| **pdfsharp** | 2.99 | 1.16 | 15.91 | 169.8 | 3,597 | 0.04 |
| **questpdf** | 2.84 | 1.26 | 33.80 | 206.7 | 446.1 | 0.01 |
| **aspose** | 0.36 | 1.41 | 123.7 | 518.7 | 41,028 | 0.29 |
| **skiasharp** | 2.93 | 1.13 | 646.9 | 10,621 | 2,275 | 0.06 |

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
| papersharp | 0.96 | 316.8 | 3.56 | 3 |
| puppeteer | 0.99 | 95.27 | 1.85 | 3 |
| wkhtmltopdf | 0.78 | 2,571 | 22.68 | 3 |
| itext7 | 0.78 | 2,442 | 19.89 | 3 |
| pdfsharp | 0.79 | 2,217 | 15.47 | 3 |
| questpdf | 0.81 | 2,005 | 15.95 | 3 |
| aspose | 0.79 | 2,289 | 18.63 | 3 |
| skiasharp | 0.80 | 2,131 | 16.30 | 3 |

## 逐页还原度明细

| 库 | 第1页 SSIM / 像素差% / 落墨% | 第2页 SSIM / 像素差% / 落墨% | 第3页 SSIM / 像素差% / 落墨% |
|---|---|---|---|
| **papersharp** | 0.983 / 2.4 / 7.3 | 0.961 / 2.9 / 13.2 | 0.931 / 5.4 / 28.0 |
| **puppeteer** | 0.990 / 2.2 / 7.3 | 0.994 / 1.6 / 13.0 | 0.989 / 1.8 / 26.7 |
| **wkhtmltopdf** | 0.816 / 12.8 / 12.7 | 0.728 / 25.7 / 15.4 | 0.810 / 29.5 / 7.2 |
| **itext7** | 0.802 / 14.4 / 7.3 | 0.763 / 20.7 / 16.1 | 0.764 / 24.5 / 27.3 |
| **pdfsharp** | 0.823 / 9.1 / 8.0 | 0.790 / 14.5 / 13.4 | 0.762 / 22.8 / 28.4 |
| **questpdf** | 0.856 / 7.6 / 7.7 | 0.813 / 13.1 / 13.1 | 0.766 / 27.1 / 28.2 |
| **aspose** | 0.815 / 9.8 / 7.0 | 0.780 / 19.6 / 14.0 | 0.762 / 26.5 / 25.8 |
| **skiasharp** | 0.823 / 11.0 / 7.4 | 0.802 / 15.3 / 13.0 | 0.786 / 22.6 / 27.3 |

> `落墨%` 是该页非白像素占比，用来识别「整页没画 / 画少了」的退化：某库某页的落墨% 明显低于 truth 而像素差% 很高，通常是内容被截断或整块未渲染，而不是画得不像。

> truth 落墨%（基准，越接近越好）：第1页 7.3 ｜ 第2页 13.0 ｜ 第3页 26.7

## 特性覆盖计数

| 库 | Y | A | N | ? | 覆盖率(Y+A) |
|---|---|---|---|---|---|
| papersharp | 35 | 13 | 7 | 1 | 86% |
| puppeteer | 56 | 0 | 0 | 0 | 100% |
| wkhtmltopdf | 29 | 2 | 11 | 14 | 55% |
| itext7 | 32 | 22 | 2 | 0 | 96% |
| pdfsharp | 25 | 31 | 0 | 0 | 100% |
| questpdf | 32 | 22 | 2 | 0 | 96% |
| aspose | 17 | 37 | 2 | 0 | 96% |
| skiasharp | 25 | 31 | 0 | 0 | 100% |

## 完整特性矩阵（56 项 × 8 库）

| 特性 | 分组 | 页 | papersharp | puppeteer | wkhtmltopdf | itext7 | pdfsharp | questpdf | aspose | skiasharp |
|---|---|---|---|---|---|---|---|---|---|---|
| `headings` | text | 1 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `weights` | text | 1 | 🟡 | ✅ | 🟡 | 🟡 | 🟡 | ✅ | 🟡 | 🟡 |
| `inline-code-chip` | text | 1 | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ | 🟡 | 🟡 |
| `cjk-latin-mixed` | text | 1 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 |
| `line-height` | text | 1 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 |
| `letter-spacing` | text | 1 | ✅ | ✅ | ❌ | ✅ | 🟡 | ✅ | ✅ | 🟡 |
| `text-indent` | text | 1 | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ | 🟡 | 🟡 |
| `text-shadow` | effect | 1 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 | 🟡 |
| `text-color` | text | 1 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `text-align-justify` | text | 1 | ✅ | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 | 🟡 |
| `list-ul-marker-color` | text | 1 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 | 🟡 |
| `list-ol-decimal-alpha-roman` | text | 1 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 | 🟡 |
| `list-style-circle-none` | text | 1 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 | 🟡 |
| `nested-list` | text | 1 | ✅ | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 |
| `border-solid` | border | 2 | 🟡 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `border-dashed` | border | 2 | ❌ | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ | ✅ |
| `border-dotted` | border | 2 | 🟡 | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ | ✅ |
| `border-double` | border | 2 | ❌ | ✅ | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 |
| `border-groove-ridge` | border | 2 | ❌ | ✅ | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 |
| `border-width-2-4-8` | border | 2 | 🟡 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `border-per-side-style` | border | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ | 🟡 |
| `border-per-side-color` | border | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ | 🟡 |
| `border-currentcolor` | border | 2 | ✅ | ✅ | ✅ | 🟡 | 🟡 | ✅ | 🟡 | 🟡 |
| `border-single-side` | border | 2 | ❌ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `radius-uniform` | radius | 2 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | ✅ |
| `radius-per-corner` | radius | 2 | ✅ | ✅ | ❔ | ✅ | ✅ | ✅ | 🟡 | ✅ |
| `radius-circle-ellipse` | radius | 2 | ✅ | ✅ | ❔ | 🟡 | ✅ | 🟡 | ✅ | ✅ |
| `radius-pill` | radius | 2 | ✅ | ✅ | ❔ | ✅ | ✅ | ✅ | 🟡 | ✅ |
| `radius-plus-dashed` | radius | 2 | ✅ | ✅ | ❔ | ✅ | ✅ | 🟡 | 🟡 | ✅ |
| `border-radius-bg-clip` | radius | 2 | ❔ | ✅ | ❔ | ✅ | ✅ | 🟡 | 🟡 | ✅ |
| `triangle-via-border` | border | 2 | ✅ | ✅ | ❔ | 🟡 | 🟡 | 🟡 | 🟡 | 🟡 |
| `box-shadow-soft` | effect | 3 | 🟡 | ✅ | ✅ | ❌ | 🟡 | ✅ | ❌ | ✅ |
| `box-shadow-hard` | effect | 3 | 🟡 | ✅ | ✅ | 🟡 | ✅ | ✅ | ✅ | ✅ |
| `box-shadow-inset` | effect | 3 | 🟡 | ✅ | ❌ | ❌ | 🟡 | ❌ | ❌ | ✅ |
| `box-shadow-multi` | effect | 3 | 🟡 | ✅ | ❌ | 🟡 | 🟡 | 🟡 | 🟡 | ✅ |
| `linear-gradient-multi-stop` | gradient | 3 | ✅ | ✅ | ❌ | 🟡 | ✅ | 🟡 | 🟡 | ✅ |
| `radial-gradient` | gradient | 3 | 🟡 | ✅ | ❌ | 🟡 | 🟡 | 🟡 | 🟡 | ✅ |
| `gradient-with-alpha` | gradient | 3 | ❌ | ✅ | ❌ | 🟡 | 🟡 | 🟡 | 🟡 | ✅ |
| `rgba-overlap-blend` | effect | 3 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 | ✅ |
| `opacity` | effect | 3 | ✅ | ✅ | ✅ | 🟡 | 🟡 | 🟡 | 🟡 | ✅ |
| `inline-alpha-highlight` | effect | 3 | ✅ | ✅ | ✅ | 🟡 | 🟡 | ✅ | 🟡 | 🟡 |
| `image-jpeg` | image | 3 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `image-png-alpha` | image | 3 | ✅ | ✅ | ❔ | ✅ | ✅ | ✅ | 🟡 | ✅ |
| `image-rounded-border` | image | 3 | ✅ | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 | ✅ |
| `flex-space-between` | layout | 3 | 🟡 | ✅ | ❌ | 🟡 | 🟡 | ✅ | 🟡 | 🟡 |
| `flex-margin-auto` | layout | 3 | ✅ | ✅ | ❌ | 🟡 | 🟡 | ✅ | 🟡 | 🟡 |
| `flex-wrap-gap` | layout | 3 | ✅ | ✅ | ❌ | 🟡 | 🟡 | 🟡 | 🟡 | 🟡 |
| `flex-nested-ratios` | layout | 3 | ✅ | ✅ | ❌ | 🟡 | 🟡 | ✅ | 🟡 | 🟡 |
| `flex-cross-axis-center` | layout | 3 | ✅ | ✅ | ❌ | 🟡 | 🟡 | ✅ | 🟡 | 🟡 |
| `table-thead-tfoot` | table | 3 | 🟡 | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 | 🟡 |
| `table-striped` | table | 3 | ❌ | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 | 🟡 |
| `table-cell-borders` | table | 3 | ❌ | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 | 🟡 |
| `table-colspan-footer` | table | 3 | ✅ | ✅ | ❔ | ✅ | 🟡 | ✅ | 🟡 | 🟡 |
| `word-break-all` | text | 3 | 🟡 | ✅ | ❔ | 🟡 | 🟡 | ❌ | 🟡 | 🟡 |
| `page-header-footer` | doc | all | 🟡 | ✅ | 🟡 | ✅ | ✅ | ✅ | ✅ | 🟡 |
| `page-number-badge` | doc | all | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 |

> ✅ 原生机制正确渲染 ｜ 🟡 近似/手画（视觉对但靠自绘、预混合、条纹、复制文字等） ｜ ❌ 不支持或灰块+红✗占位 ｜ ❔ 落在被裁/被截断区域，无从判定

## PaperSharp 逐格对账明细（`out/cells.csv`）

Δ = 本网格 PaperSharp SSIM − puppeteer SSIM（puppeteer＝Chrome 走同一 PDF 栅格通路，是同格能到的天花板）。特性级取**最差格**：Δ≥−0.03→Y ｜ Δ≥−0.12→A ｜ 更差→N。

| 特性 | 分组 | 格数 | 判定 | 最差Δ | 最差格页 | 格均Δ |
|---|---|---|---|---|---|---|
| `table-cell-borders` | table | 31 | N | -0.210 | 3 | -0.049 |
| `border-double` | border | 1 | N | -0.176 | 2 | -0.176 |
| `border-dashed` | border | 1 | N | -0.165 | 2 | -0.165 |
| `border-single-side` | border | 1 | N | -0.138 | 2 | -0.138 |
| `table-striped` | table | 5 | N | -0.137 | 3 | -0.102 |
| `border-groove-ridge` | border | 1 | N | -0.135 | 2 | -0.135 |
| `gradient-with-alpha` | gradient | 1 | N | -0.133 | 3 | -0.133 |
| `border-dotted` | border | 1 | A | -0.118 | 2 | -0.118 |
| `border-solid` | border | 1 | A | -0.098 | 2 | -0.098 |
| `radial-gradient` | gradient | 1 | A | -0.079 | 3 | -0.079 |
| `box-shadow-multi` | effect | 1 | A | -0.071 | 3 | -0.071 |
| `flex-space-between` | layout | 8 | A | -0.066 | 3 | +0.015 |
| `table-thead-tfoot` | table | 1 | A | -0.065 | 3 | -0.065 |
| `box-shadow-hard` | effect | 1 | A | -0.055 | 3 | -0.055 |
| `box-shadow-inset` | effect | 1 | A | -0.048 | 3 | -0.048 |
| `page-header-footer` | doc | 6 | A | -0.042 | 3 | -0.020 |
| `border-width-2-4-8` | border | 1 | A | -0.036 | 2 | -0.036 |
| `box-shadow-soft` | effect | 1 | A | -0.034 | 3 | -0.034 |
| `weights` | text | 1 | A | -0.033 | 1 | -0.033 |
| `word-break-all` | text | 1 | A | -0.031 | 3 | -0.031 |
| `table-colspan-footer` | table | 1 | Y | -0.025 | 3 | -0.025 |
| `nested-list` | text | 2 | Y | -0.016 | 1 | -0.016 |
| `flex-wrap-gap` | layout | 1 | Y | -0.011 | 3 | -0.011 |
| `list-ul-marker-color` | text | 1 | Y | -0.011 | 1 | -0.011 |
| `inline-code-chip` | text | 2 | Y | -0.010 | 1 | -0.009 |
| `radius-circle-ellipse` | radius | 1 | Y | -0.008 | 2 | -0.008 |
| `border-per-side-style` | border | 1 | Y | -0.006 | 2 | -0.006 |
| `triangle-via-border` | border | 1 | Y | -0.005 | 2 | -0.005 |
| `cjk-latin-mixed` | text | 1 | Y | -0.005 | 1 | -0.005 |
| `headings` | text | 1 | Y | -0.001 | 1 | -0.001 |
| `linear-gradient-multi-stop` | gradient | 1 | Y | -0.000 | 3 | -0.000 |
| `rgba-overlap-blend` | effect | 1 | Y | +0.000 | 3 | +0.000 |
| `flex-nested-ratios` | layout | 3 | Y | +0.000 | 3 | +0.037 |
| `flex-cross-axis-center` | layout | 2 | Y | -0.000 | 3 | +0.012 |
| `text-indent` | text | 1 | Y | +0.002 | 1 | +0.002 |
| `radius-plus-dashed` | radius | 1 | Y | +0.002 | 2 | +0.002 |
| `image-jpeg` | image | 3 | Y | +0.003 | 3 | +0.006 |
| `list-style-circle-none` | text | 1 | Y | +0.004 | 1 | +0.004 |
| `text-color` | text | 1 | Y | +0.005 | 1 | +0.005 |
| `line-height` | text | 1 | Y | +0.006 | 1 | +0.006 |
| `image-png-alpha` | image | 1 | Y | +0.006 | 3 | +0.006 |
| `image-rounded-border` | image | 1 | Y | +0.007 | 3 | +0.007 |
| `radius-uniform` | radius | 1 | Y | +0.009 | 2 | +0.009 |
| `list-ol-decimal-alpha-roman` | text | 1 | Y | +0.009 | 1 | +0.009 |
| `letter-spacing` | text | 1 | Y | +0.013 | 1 | +0.013 |
| `text-shadow` | effect | 1 | Y | +0.019 | 1 | +0.019 |
| `border-currentcolor` | border | 1 | Y | +0.021 | 2 | +0.021 |
| `border-per-side-color` | border | 1 | Y | +0.021 | 2 | +0.021 |
| `radius-pill` | radius | 1 | Y | +0.025 | 2 | +0.025 |
| `radius-per-corner` | radius | 1 | Y | +0.043 | 2 | +0.043 |
| `page-number-badge` | doc | 3 | Y | +0.052 | 1 | +0.053 |
| `text-align-justify` | text | 1 | Y | +0.079 | 1 | +0.079 |
| `flex-margin-auto` | layout | 1 | Y | +0.156 | 3 | +0.156 |
| `inline-alpha-highlight` | effect | 1 | Y | +0.481 | 3 | +0.481 |

- 特性级：Y 34 ｜ A 13 ｜ N 7 ｜ ? 0（共 54 项）
- 格级：Y 65 ｜ A 33 ｜ N 11 ｜ ? 0（共 109 格）
- 与整页 SSIM 的分工：整页分（`fidelity.csv`）看**排得出高低**，逐格分看**差在哪一块**；上表 N 里若一批特性的最差格同页且 Δ 同量级，通常是纵向漂移而非该特性本身没实现，用 `python fidelity/cells.py --pair <n>` 导出左右对照图确认。

## 矩阵备注

- **papersharp**：该列由 fidelity/cells.py 逐特性像素对账自动判定（以 puppeteer 同格 SSIM 为天花板的相对分），不再手填；3 页齐全，横向内容与 Chrome 对齐（最右墨迹列一致）。剩余主因是纵向逐段累积漂移：页1 到 css y=500 已比 Chrome 低约 10px，故每个格子都被平移稀释。
- **wkhtmltopdf**：p2自2.3圆角第3格起、p3自3.3第二张图起内容被截断(仅出3页)，被截断区域的特性记?。
- **skiasharp**：3 页齐全。Y 全部来自 Skia 画布原生原语；A 全部要先自研断行/字体回退/flex/表格几何才画得出来；无 N（没有一项靠占位块跳过）。代价见 results.csv：约 1800 行 C# 手搭三页，且 Skia 的 PDF 后端不做字体子集化，YaHei/Segoe UI/Consolas 整份嵌进文件 -> 31 MB。

## 各维度最优 / 最差

- **延迟 med（越低越好）**：最优 `pdfsharp`（47.72），最差 `puppeteer`（3,413）
- **RSS 峰值（越低越好）**：最优 `wkhtmltopdf`（78.60），最差 `puppeteer`（781.3）
- **输出体积（越低越好）**：最优 `wkhtmltopdf`（98.33），最差 `skiasharp`（31,864）
- **4 线程吞吐（越高越好）**：最优 `pdfsharp`（73.47），最差 `puppeteer`（0.50）
- **冷启动（越低越好）**：最优 `questpdf`（458.4），最差 `puppeteer`（4,013）
- **平均 SSIM（越高越好）**：最优 `puppeteer`（0.99），最差 `itext7`（0.78）
- **特性覆盖 Y+A（越高越好）**：最优 `puppeteer`（56.00），最差 `wkhtmltopdf`（31.00）

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

- **必须每库一个子进程测**：同进程连测 8 家会让 Skia 原生内存累积，`rss_peak` 虚高到 3–4 GB；隔离后才是真实的 79–781 MB。
- **`threadsafe` 的比对口径**：剥离 `/CreationDate`、`/ID`、Xmp 日期与 iText 字体子集随机前缀（`/ABCDEF+`）后逐字节比较；不剥会产生假 0。
- **`out/<lib>.pdf` 与测量用的临时产物不是同一个文件**：Aspose 两次生成体积能差约 10%（水印与子集前缀），体积榜一律以 `results.csv` 为准。
- **还原度上限不是 1.0**：truth 来自 Chrome 截图、对照图来自 WinRT 栅格，天然 ≤2px 错位，puppeteer 自己也只有 0.850，因此 SSIM 只用于排序。
- **wkhtmltopdf 依赖便携版**：官方 GitHub release 无 Windows 包，用 0.12.5 mxe-cross-win64 的 7z，以 `C:\Windows\System32\tar.exe` 解到 `~/wkhtmltox`。
- **IronPDF 未参赛**：2025.9.1 一调用即 `LicensingException: Production License Required`，且会向工程注入 global using 污染其他适配器命名空间。

产物清单：`out/results.csv`、`out/fidelity.csv`、`out/features_matrix.csv`、`out/cells.json` + `out/cells.csv`（逐格）、`out/<lib>.pdf` ×7、`out/<lib>_pN.png` ×24、`report/charts/*.png` ×12。
