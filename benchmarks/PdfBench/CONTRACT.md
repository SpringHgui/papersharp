# PdfBench 数据契约（CSV schema）

所有产物写入 `benchmarks/PdfBench/out/`。列名、顺序、单位严格如下，供 report.py 消费。

## results.csv — 性能指标（长表，一行一个指标）
```
adapter,metric,value,unit
```
- `adapter`: papersharp | puppeteer | wkhtmltopdf | itext7 | pdfsharp | questpdf | aspose
- `metric`（每个 adapter 都要产全，缺失写 NaN）:
  - `latency_min` `latency_med` `latency_mean` `latency_p95` `latency_max` — 单位 ms（warmup 5 + 30 迭代）
  - `alloc_med` — 单次生成托管分配中位数，单位 KB（GC.GetTotalAllocatedBytes 差值）
  - `gen0` `gen1` `gen2` — 30 迭代期间 GC 回收次数
  - `rss_peak` — 进程树峰值工作集，单位 MB（50 ms 轮询 Toolhelp32 求和；外部进程选手含其 Chrome / wkhtmltopdf 子进程）
  - `size` — 输出 PDF 体积，单位 KB
  - `pages` — 输出页数
  - `throughput_serial` — 单线程 3 秒内完成文档数，单位 docs/s
  - `throughput_par4` — 4 线程并发 3 秒完成数，docs/s
  - `coldstart` — 独立进程单次生成总墙钟（含 JIT/字体/license 初始化），ms
  - `threadsafe` — 1=4 并发输出与串行字节一致，0=不一致，NaN=不适用
- `unit`: ms | KB | MB | docs/s | count | bool

## fidelity.csv — 视觉还原度（每 adapter × 每页一行）
```
adapter,page,ssim,mse,pixel_diff_pct,ink_coverage_pct
```
- `page`: 1..3
- `ssim`: 0..1，与 truth_pN.png 对齐比较（同裁到 1588px 宽、2× 分辨率）
- `mse`: 均方误差（0..255²）
- `pixel_diff_pct`: 差异像素占比（阈值 ΔE>10）
- `ink_coverage_pct`: 该页非白像素占比（辅助判断"整页没画"退化）
- 页数≠3 的 adapter：超出页与 truth 无对应，ssim 记 NaN 并在 features_matrix 备注

## features_matrix.csv — 特性支持矩阵（宽表，行=特性，列=库）
```
feature_id,group,pages,puppeteer,wkhtmltopdf,itext7,pdfsharp,questpdf,aspose,papersharp
```
- 单元格取值：`Y`(原生/正确渲染) `A`(近似/手画) `N`(不支持/占位块) `?`(未判定)
- feature_id 对齐 spec/features.json

## 栅格化约定
- truth 与各库 PDF 一律用 `raster.ps1`（WinRT PDFDocument）出 `<name>_pN.png`，2× 分辨率。
- report.py 只读上述 3 个 CSV，不碰 PDF。
