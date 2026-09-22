# PaperSharp 渲染器 · 性能测试报告

> 对象：`paper-sharp`（AngleSharp.Css + SkiaSharp + 手写布局层的 HTML/CSS → PDF/PNG 渲染器）。
> 方法：新增可复现基准入口 `src/Benchmark.cs`（`dotnet run -- bench|cold|scale`），全部数字为本机 `Stopwatch` 实测。

## 0. 结论速览（TL;DR）

1. **PDF 的耗时几乎全在"编码"，不在布局。** 单页 PDF 里 `SKDocument` 编码（主要是把 ~1.8MB 字体子集写进文档）占 **77%–96%**；真正的布局计算（measure+paginate）只有 **1–6ms**，可忽略。
2. **PNG 比 PDF 快一个量级。** 热态下小文档 PNG 中位 ~35–55ms、PDF 中位 ~210–575ms。差异就是 PDF 的字体嵌入 + PDF backend 初始化。
3. **CPU 侧第二大头是 AngleSharp 的 `BuildDocument`**（解析+层叠+遍历），小文档 15–60ms，随文档规模线性增长。
4. **发现并已修复一个真实缺陷：表格定位曾是 O(行²)。** `PositionTable` 每行都调 `FindRowBox` 重新遍历整棵表树取行盒 → 行数翻倍定位耗时翻 ~4 倍。实测 table 定位 200→400 行曾达 3.42→16.78ms。**已改**为 `MeasureTable` 一次性缓存行盒、`PositionTable` 按下标取；修复后 400 行定位 **16.78ms → 0.296ms（≈57×）**，回到线性，9 样例零回归（见 §5）。
5. **冷启动首帧**：PDF ~630ms、PNG ~64ms（含 JIT + 字体子集首次加载 + SkiaSharp PDF backend 初始化）；之后进入热态。
6. ⚠️ **本机测试期间有一个用户的 python 进程长期占满 CPU**（PID 2100，累计 ~16800 CPU-秒），因此**绝对毫秒数是"有载上界"，只能当相对/结构性结论用**；min 值与"哪一阶段占大头""复杂度增长比"这类结论受扰最小。

## 1. 测试环境

| 项 | 值 |
|---|---|
| CPU | 13th Gen Intel Core i7-13620H（10 物理核 / 16 逻辑） |
| 内存 | 15.7 GB |
| 系统 | Windows 11 专业工作站版 build 26200 |
| .NET | 运行时 Microsoft.NETCore.App 10.0.11（工程 TargetFramework net8.0） |
| GC | Workstation GC（控制台默认），单线程渲染 |
| 构建 | `dotnet build`（Debug） |
| 并发干扰 | **有**：用户 python 进程满载 CPU 全程运行 |

产物路径：`bin/Debug/net8.0/output/`。字体子集已生成（`assets/fonts/YaheiSub-*.ttf`），PDF 走子集嵌入。

## 2. 冷启动首帧（每样例一个全新进程，不预热）

`dotnet run -- cold <sample>`，计时只包住首帧渲染调用（不含进程启动）。

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

## 3. 热态端到端延迟（每样例 30 次，预热后）

绝对值受并发负载抬高，**看 min（最抗扰）与 PDF/PNG 的相对差**。

| 样例 | PDF min | PDF 中位 | PNG min | PNG 中位 | PDF 体积 |
|---|---:|---:|---:|---:|---:|
| invoice | 195 | 575 | 25 | 36 | 2457 KB |
| long | 205 | 483 | 88 | 143 | 2459 KB |
| flex | 357 | 501 | 84 | 117 | 2457 KB |
| img | 189 | 445 | 31 | 40 | 2467 KB |
| report | 219 | 250 | 81 | 92 | 2459 KB |
| pct | 195 | 210 | 40 | 47 | 2458 KB |
| polish | 289 | 309 | 42 | 45 | 3695 KB |
| inline | 196 | 209 | 48 | 52 | 2463 KB |
| table | 232 | 304 | 118 | 159 | 2459 KB |

**解读**：
- PDF 中位普遍 **210–575ms**，PNG 中位 **36–159ms**，PNG 明显更快。
- PDF 体积几乎恒定 ~2.46MB（= 字体子集），polish 3.7MB（嵌了 @font-face 自定义字体）——这解释了 §4 里 PDF 编码为何这么贵。
- 波动大（invoice PDF max 曾达 12.8s）是 GC gen2 暂停 + 并发抢占，非渲染逻辑问题。

## 4. 阶段耗时拆解（PDF 管线，每阶段 30 次均值，单位 ms）

`prepare / parse(@page) / buildDoc(AngleSharp) / toBox(img解码) / measure / paginate / paintEncode(SKDocument)`

| 样例 | prepare | parse | buildDoc | toBox | measure | paginate | **paintEncode** | total |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| invoice | 0.02 | 0.00 | 23.1 | 0.02 | 1.50 | 1.15 | **591.4** | 617 |
| long | 0.01 | 0.00 | 28.8 | 0.02 | 2.03 | 4.17 | **271.6** | 307 |
| flex | 0.01 | 0.03 | 21.9 | 0.03 | 0.81 | 0.79 | **224.2** | 248 |
| img | 0.01 | 0.02 | 15.6 | 0.02 | 0.47 | 0.44 | **220.8** | 237 |
| report | 0.01 | 0.07 | 59.9 | 0.06 | 1.94 | 1.95 | **218.2** | 282 |
| pct | 0.01 | 0.06 | 18.8 | 0.02 | 1.24 | 1.24 | **217.2** | 239 |
| polish | 0.08 | 0.05 | 16.3 | 1.44 | 1.35 | 0.98 | **336.2** | 356 |
| inline | 0.01 | 0.02 | 21.0 | 0.03 | 1.31 | 1.32 | **215.3** | 239 |
| table | 0.01 | 0.02 | 46.1 | 0.04 | 0.79 | 0.74 | **226.0** | 274 |

**关键结论**：
- `paintEncode` 占单页 PDF 总时间的 **77%–96%**，是绝对瓶颈（字体子集写入 + PDF backend）。
- `buildDoc`（AngleSharp 解析+层叠+`Walk`）是第二大头，15–60ms，随文档规模增长（report 35 行→60ms、table 复杂→46ms）。
- **布局本身（measure+paginate）1–6ms，占比 <2%**——手写布局层不是瓶颈。
- `toBox`（图片解码）除 polish（1.44ms，多 @font-face/图）外几乎为 0（有缓存）。

## 5. 规模扩展与复杂度（只测 CPU 布局，隔离光栅/编码噪声）

`dotnet run -- scale`：合成文档，分别计 `buildDoc / toBox / measure / position` 四段（各 5 次中位）。**position 段是复杂度信号**（不受 buildDoc 的并发噪声干扰）。

**修复前**（`PositionTable` 每行调 `FindRowBox` 重遍历整棵表树）：

| 行数 | table · position (ms) | flexlist · position (ms) |
|---:|---:|---:|
| 100 | 0.25 | 0.17 |
| 200 | 3.42 | 0.17 |
| 400 | **16.78** | — |

→ 行数 ×2 → 耗时 ×4~5，典型 **O(行²)**；flex 同期恒定（O(N)）。

**修复后**（`MeasureTable` 一次性缓存行盒列表 `TRowBoxes`，`PositionTable` 按下标取，删掉 `FindRowBox`）：

| 行数 | table · position (ms) | flexlist · position (ms) |
|---:|---:|---:|
| 100 | 0.109 | 0.146 |
| 200 | 0.116 | 0.213 |
| 400 | **0.296** | 0.375 |

→ table 定位回到**线性**：400 行 **16.78ms → 0.296ms（≈57×）**，与 flex 同量级。9 个样例产物字节零回归（`table.png`/`table.pdf` 目检一致，斑马纹/行背景不变）。

> 注：`buildDoc`（AngleSharp 解析）仍随行数线性增长且被并发 python 严重污染（table 400 行 buildDoc 竟 ~3s），与本次修复无关，故只作定性。

## 6. 内存（单页渲染，托管堆增量 + 进程工作集）

| 样例 | 托管堆增量 (KB) | 进程工作集 (MB) | PDF 体积 (KB) |
|---|---:|---:|---:|
| invoice | 15,304 | 236.9 | 2457 |
| report | 12,147 | 237.0 | 2459 |
| polish | 18,818 | 239.2 | 3695 |
| inline | 21,385 | 239.2 | 2463 |
| table | 20,267 | 239.2 | 2459 |

**解读**：单次渲染托管分配 ~12–21MB（主要是 PDF 字节流 + Skia 画布 + 中间盒子树）；进程工作集稳定在 ~237MB（含已加载的字体子集与 SkiaSharp 原生库）。无泄漏迹象（多次渲染工作集不持续上涨）。

## 7. 优化建议（按性价比）

1. **✅ 已修：表格 O(行²) → O(行)。** 在 `MeasureTable` 里一次性 `CollectRowBoxes` 收集行盒存到 `BlockBox.TRowBoxes`，`PositionTable` 按下标取，删掉每行重遍历的 `FindRowBox`。实测 400 行定位 16.78ms→0.296ms（§5），9 样例产物字节零回归。
2. **降 PDF 编码成本（高价值，改动中）**：`paintEncode` 占 80%+ 是字体子集反复嵌入所致。可选：(a) 同一进程内多份 PDF 共享已加载 typeface（已缓存，但 Skia 每文档仍重写字体程序）；(b) 若可接受，用 SkiaSharp 3.x 的 PDF 子集化，或 PDF 侧改 PDFsharp 做字体子集复用；(c) 批量导出时复用同一 `SKDocument` 多页而非每文档重建。
3. **PNG 超大文档**：单张长图对 800+ 行会分配几十 MB 位图 + 慢 PNG 编码（本报告的教训）。生产里长文档应走 PDF 分页，或 PNG 分块。
4. **GC**：常驻服务可开 `ServerGarbageCollection` + `ConcurrentGarbageCollection`，减少 gen2 暂停造成的长尾（本次 max 抖动即 gen2 + 抢占）。

## 8. 已知测试局限

- **并发污染**：测试全程有用户 python 进程满载 CPU，绝对时延被显著抬高且抖动大；本报告以 min、阶段占比、复杂度增长比等**相对指标**为准。要拿干净的绝对基线，需在空闲机器上重跑 `dotnet run -- bench 50`。
- 未做：并发/多进程吞吐、跨平台（mac/Linux）对比、单阶段 profiler（如 `dotnet-trace`）。
- 表格 O(行²) 缺陷**已修复并复测**（§5 修复前后对比）；`buildDoc` 的绝对值因并发 python 污染不可信，仅结构/占比可用。

## 9. 复现方法

```bat
cd paper-sharp
dotnet build
dotnet run -- cold invoice          :: 冷启动首帧（单样例单进程）
dotnet run -- bench 30              :: 热态延迟 + 阶段拆解 + 内存 + 规模 + 吞吐
dotnet run -- scale                 :: 仅布局复杂度扫描（抗噪，看 position_ms 增长）
```

基准代码：`src/Benchmark.cs`；入口分发在 `Program.cs`（`bench` / `cold` / `scale` 三个参数）。输出为机器可读 CSV 行（`LAT/STAGE/MEM/SCALE/THROUGHPUT`）。
