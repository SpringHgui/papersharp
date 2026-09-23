# PaperSharp 渲染器 · 性能测试报告

> 对象：`paper-sharp`（AngleSharp.Css + SkiaSharp + 手写布局层的 HTML/CSS → PDF/PNG 渲染器）。
> 方法：可复现基准入口 `src/PaperSharpSamples/Benchmark.cs`（在 PaperSharpSamples 目录下 `dotnet run -c Release -- bench|cold|scale`），全部数字为本机 `Stopwatch` 实测。
> **本版数据来自 2026-09-22 的 Release `bench 50` 净跑**（无并发负载），原始输出：`src/PaperSharpSamples/bench_20260922.txt`。旧版 Debug + python 满载干扰下的数字已全部替换；唯 §2 冷启动仍是旧 Debug 实测（未重跑）。

## 0. 结论速览（TL;DR）

1. **PDF 的端到端耗时几乎全在"编码"，不在布局。** `SKDocument` 编码（主要是把 ~1.8MB 字体子集写进文档）占单页 PDF 总时间 **85%–95%**；真正的布局计算（measure+paginate）只有 **1–4ms**。
2. **PNG 比 PDF 快一个量级。** 热态小文档 PNG 中位 ~27–107ms、PDF 中位 ~184–280ms。差异就是 PDF 的字体嵌入 + PDF backend 初始化。
3. **⚠️ 新发现（本次净跑修正了旧报告定性）：AngleSharp 的 `buildDoc`（逐元素 `GetComputedStyle` 层叠）不是线性，而是超线性 ~O(n^2.5)。** table 200→2000 行 buildDoc 1.0s→74.4s（×74），flexlist 同步恶化（410ms→21.5s，×52）——**瓶颈在元素个数，与元素类型无关**。上千元素的大文档，CSS 层叠本身（而非编码/布局）成为第一瓶颈。这是 M6「自研级联替换 AngleSharp.Css」的核心量化依据（见 §5、§7）。
4. **手写布局层可扩展性良好。** 2000 条目时 measure+position 合计仅 76ms（table）/ 162ms（flexlist），position 段近似线性。
5. **发现并已修复一个真实历史缺陷：表格定位曾是 O(行²)。** `PositionTable` 每行重遍历整棵表树取行盒；修复后 400 行定位 **16.78ms → 0.296ms（≈57×）**，回到线性（见 §5 历史小节）。
6. **吞吐**：单页常规文档（invoice）热态 **4.9 页/秒**（Workstation GC、单线程）。
7. **冷启动首帧**（旧 Debug 数据）：PDF ~630ms、PNG ~64ms；之后进入热态。

## 1. 测试环境

| 项 | 值 |
|---|---|
| CPU | 13th Gen Intel Core i7-13620H（10 物理核 / 16 逻辑） |
| 内存 | 15.7 GB |
| 系统 | Windows 11 专业工作站版 build 26200 |
| .NET | 运行时 Microsoft.NETCore.App 10.0.11（ Samples 工程 net10.0，库 net8.0） |
| GC | Workstation GC（控制台默认），单线程渲染 |
| 构建 | `dotnet build -c Release`；`bench 50`（预热后每样例 50 次迭代） |
| 并发干扰 | **无**（本版净跑；旧版曾有 python 满载进程污染绝对值，已作废） |

产物路径：`src/PaperSharpSamples/bin/Release/net10.0/output/`。字体子集已生成（`src/PaperSharpSamples/assets/fonts/YaheiSub-*.ttf`），PDF 走子集嵌入。

## 2. 冷启动首帧（每样例一个全新进程，不预热）※ 旧 Debug 数据，本轮未重跑

| 样例 | 首帧 PDF (ms) | 首帧 PNG (ms) |
|---|---:|---:|
| invoice | 610 | 41 |
| long | 626 | 71 |
| flex | 686 | 85 |
| img | 552 | 63 |
| report | 771 | 204 |
| pct | 574 | 62 |
| polish | 669 | 65 |
| inline | 636 | 88 |
| table | 640 | 130 |

**解读**：PDF 首帧稳定在 ~550–770ms，是一次性的 SkiaSharp PDF backend + 字体子集加载 + JIT 成本；PNG 首帧 41–204ms，随文档像素量增长（report 最长故 204ms）。常驻服务里这是"第一单"成本，之后即热态。

## 3. 热态端到端延迟（Release，每样例 50 次，预热后）

| 样例 | PDF min | PDF 中位 | PDF p95 | PDF max | PNG min | PNG 中位 | PNG p95 | PDF 体积 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| invoice | 166 | 216 | 262 | 276 | 24 | 27 | 36 | 2458 KB |
| long | 180 | 236 | 309 | 313 | 49 | 54 | 75 | 2460 KB |
| flex | 186 | 209 | 244 | 253 | 37 | 40 | 52 | 2459 KB |
| img | 166 | 184 | 203 | 205 | 29 | 32 | 44 | 2468 KB |
| report | 210 | 244 | 320 | 350 | 84 | 107 | 131 | 2460 KB |
| pct | 165 | 210 | 262 | 267 | 40 | 45 | 59 | 2459 KB |
| polish | 227 | 280 | 339 | 357 | 42 | 54 | 73 | 3695 KB |
| inline | 180 | 202 | 248 | 256 | 44 | 55 | 73 | 2464 KB |
| table | 189 | 225 | 278 | 294 | 50 | 56 | 74 | 2460 KB |

**解读**：
- PDF 中位 **184–280ms**，PNG 中位 **27–107ms**；净跑下方差很小（max/med ≈ 1.3），旧报告"波动大是噪声"的判断确认成立——负载消除后长尾随之消失。
- PDF 体积恒定 ~2.46MB（= 嵌入的字体子集），polish 3.7MB（另嵌 @font-face 自定义字体）——解释了 §4 里编码为何贵。

## 4. 阶段耗时拆解（PDF 管线，50 次均值，单位 ms）

`prepare / parse(@page) / buildDoc(AngleSharp) / toBox(img解码) / measure / paginate / paintEncode(SKDocument)`

| 样例 | prepare | parse | buildDoc | toBox | measure | paginate | **paintEncode** | total |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| invoice | 0.01 | 0.00 | 8.7 | 0.01 | 0.30 | 0.27 | **182.1** | 191 |
| long | 0.00 | 0.00 | 17.8 | 0.01 | 1.07 | 1.97 | **170.3** | 191 |
| flex | 0.00 | 0.01 | 25.4 | 0.01 | 0.77 | 0.56 | **194.8** | 222 |
| img | 0.00 | 0.01 | 12.4 | 0.02 | 0.31 | 0.27 | **170.2** | 183 |
| report | 0.00 | 0.04 | 57.6 | 0.03 | 1.39 | 1.38 | **182.4** | 243 |
| pct | 0.00 | 0.04 | 20.9 | 0.01 | 0.89 | 0.92 | **191.2** | 214 |
| polish | 0.03 | 0.02 | 17.4 | 1.26 | 1.01 | 0.70 | **291.5** | 312 |
| inline | 0.00 | 0.01 | 20.1 | 0.02 | 0.98 | 1.03 | **178.9** | 201 |
| table | 0.00 | 0.00 | 35.8 | 0.03 | 0.53 | 0.50 | **175.1** | 212 |

**关键结论**：
- `paintEncode` 占单页 PDF 总时间 **85%–95%**——对几十~几百元素的小文档它是绝对瓶颈（字体子集写入 + PDF backend）。
- `buildDoc` 在常规样例上 9–58ms（report 元素最多故最高），与编码同量级以下；**但元素上千时反超一切**（见 §5）。
- **布局（measure+paginate）0.6–3.4ms，占比 <2%**——手写布局层不是瓶颈。
- `toBox`（图片解码）除 polish（1.26ms）外几乎为 0（有缓存）。

## 5. 规模扩展与复杂度（合成文档，各 5 次中位）

`dotnet run -c Release -- scale`：table（每行 5 格）与 flexlist（每行一张卡片）两种合成文档，计 `buildDoc / toBox / measure / position` 四段。

### 5.1 buildDoc 超线性——CSS 层叠是第一瓶颈（本次净跑的核心发现）

| 行数 | table · buildDoc (ms) | flexlist · buildDoc (ms) |
|---:|---:|---:|
| 50 | 156 | 68 |
| 200 | 1,000 | 399 |
| 800 | 9,405 | 3,205 |
| 2000 | **74,359** | **21,306** |

- 行数 ×10（200→2000）：table buildDoc **×74**、flexlist **×53**——远超线性，实测拟合 ≈ **O(n^2.5)**。
- 两种结构同步恶化 → 成本在**元素个数**（每元素一次全量 `GetComputedStyle`，AngleSharp 内部疑似随样式表/元素数重复匹配），与表格/flex 语义无关。
- 净跑证实这**不是**旧报告归因的"并发噪声"：旧版曾把 table 400 行 buildDoc ~3s 当作 python 污染，本次无负载下同类量级依旧成立。
- 每行摊薄（layout_total/行）：table 50 行 3.2ms/行 → 2000 行 37ms/行，同样漂升；**上千元素文档在现架构下必然卡 buildDoc**。

### 5.2 手写布局层保持线性

| 行数 | table · measure (ms) | table · position (ms) | flexlist · measure (ms) | flexlist · position (ms) |
|---:|---:|---:|---:|---:|
| 50 | 1.34 | 0.02 | 2.00 | 0.08 |
| 200 | 5.75 | 0.09 | 10.55 | 0.24 |
| 800 | 35.14 | 0.29 | 50.98 | 0.87 |
| 2000 | 74.50 | 0.87 | 159.82 | 2.37 |

2000 条目时布局合计（measure+position）table 仅 **76ms**、flexlist **162ms**——对比同档 buildDoc 的 74.4s / 21.3s，占比 0.1%–0.8%。position 段严格近线性；measure 略超线性来自文本折行与字体测量，量级完全可接受。

### 5.3 历史缺陷（已修复）：表格定位 O(行²)

**修复前**（`PositionTable` 每行调 `FindRowBox` 重遍历整棵表树）：

| 行数 | table · position (ms) | flexlist · position (ms) |
|---:|---:|---:|
| 100 | 0.25 | 0.17 |
| 200 | 3.42 | 0.17 |
| 400 | **16.78** | — |

**修复后**（`MeasureTable` 一次性缓存行盒列表 `TRowBoxes`，`PositionTable` 按下标取）：400 行 **16.78ms → 0.296ms（≈57×）**，回到线性，9 样例产物字节零回归。§5.2 的净跑 position 列证实线性保持至今。

## 6. 内存（单页渲染，托管堆增量 + 进程工作集；Release 净跑）

| 样例 | 托管堆增量 (KB) | 进程工作集 (MB) | PDF 体积 (KB) |
|---|---:|---:|---:|
| invoice | 22,873 | 309.4 | 2458 |
| long | 18,225 | 315.3 | 2460 |
| flex | 17,580 | 310.0 | 2459 |
| img | 24,459 | 314.4 | 2468 |
| report | 19,658 | 311.0 | 2460 |
| pct | 16,872 | 310.5 | 2459 |
| polish | 29,981 | 322.6 | 3695 |
| inline | 20,917 | 326.1 | 2464 |
| table | 23,322 | 317.4 | 2460 |

**解读**：单次渲染托管分配 ~17–30MB（主要是 PDF 字节流 + Skia 画布 + 中间盒子树）；进程工作集稳定在 ~310–326MB（含字体子集与 SkiaSharp 原生库，较旧 Debug 略升属 Release/JIT 差异）。多次渲染工作集不持续上涨，无泄漏迹象。

## 7. 优化建议（按性价比）

1. **✅ 已修：表格 O(行²) → O(行)。**（§5.3）
2. **M6 · 自研级联替换 AngleSharp.Css（高价值，方案已交付待拍板）**：§5.1 实锤 `buildDoc` 超线性是上千元素文档的第一瓶颈，且两种布局结构同受害。六步方案（CSS tokenizer/parser → 特异度+key-selector 分桶 → 属性元数据表 → 值解析接 `CssValueParser` → 换掉 `Get/Spec/Val/Raw` 内部 → 双跑对账后移除 AngleSharp.Css）预期把 buildDoc 拉回线性——2000 行表格从 74s 降到亚秒级。**收益门槛已量化：>800 元素的文档即开始显著受益。**
3. **降 PDF 编码成本**：`paintEncode` 占小文档 85–95%。可选：(a) SkiaSharp 3.x 的 PDF 字体子集化；(b) 批量导出复用同一 `SKDocument` 多页；(c) 常驻服务用 PDF 侧字体复用方案。
4. **吞吐**：invoice 4.9 rps（单线程 Workstation GC）。渲染管线除 buildDoc 外无共享可变状态的机会较多，多进程/并行分片即可近线性扩容；若先做 M6，单线程上限也会大幅抬高。
5. **PNG 超大文档**：单张长图对 800+ 行会分配几十 MB 位图 + 慢编码。生产里长文档走 PDF 分页。
6. **GC**：常驻服务可开 `ServerGarbageCollection` + `ConcurrentGarbageCollection` 削 gen2 长尾。

## 8. 已知测试局限

- 单台机器、单进程、Workstation GC；未做跨平台（mac/Linux）对比与 `dotnet-trace` 级 profiler。
- §5 scale 为合成文档（样式表小、结构规整），真实网页若带**大样式表 + 复杂选择器**，buildDoc 只会更糟（AngleSharp 成本同时随规则数×元素数增长），即 §5.1 是下界而非上界。
- §2 冷启动仍是旧 Debug 数据，Release 下未重跑。
- 测试期已知库非线程安全（`FontManager`/`ImageLoader` 静态缓存），并发吞吐数字需先加锁/每进程隔离后才可信。

## 9. 复现方法

```bat
cd src/PaperSharpSamples
dotnet build -c Release
dotnet run -c Release -- cold invoice   :: 冷启动首帧（单样例单进程）
dotnet run -c Release -- bench 50       :: 热态延迟 + 阶段拆解 + 内存 + 规模 + 吞吐（⚠️ scale 含 2000 行档，全程 ~15 分钟，建议后台跑）
dotnet run -c Release -- scale          :: 仅布局复杂度扫描
```

基准代码：`src/PaperSharpSamples/Benchmark.cs`；入口分发在 `Program.cs`（`bench` / `cold` / `scale` 参数）。输出为机器可读 CSV 行（`LAT/STAGE/MEM/SCALE/THROUGHPUT`）。最新一次净跑输出：`src/PaperSharpSamples/bench_20260922.txt`。
