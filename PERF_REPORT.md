# PaperSharp 渲染器 · 性能测试报告

> 对象：`paper-sharp`（AngleSharp.Css + SkiaSharp + 手写布局层的 HTML/CSS → PDF/PNG 渲染器）。
> 方法：可复现基准入口 `src/PaperSharpSamples/Benchmark.cs`（在 PaperSharpSamples 目录下 `dotnet run -c Release -- bench|cold|scale|pgstage|pgenc|pgbuild|pgalloc`），全部数字为本机 `Stopwatch` 实测。
> **除 §5.5、§5.6、§6、§6.8 与 §0.3 / §5.1 的补注外，§0–§5、§7 的数字仍是 2026-09-22 Release `bench 50` 净跑**（无并发负载），原始输出：`src/PaperSharpSamples/bench_20260922.txt`。旧版 Debug + python 满载干扰下的数字已全部替换；唯 §2 冷启动仍是旧 Debug 实测（未重跑）。
> **§5.5 是 2026-09-24 的作者 CSS 层叠定性 + 落地**（物化样式表集合），原始输出：`src/PaperSharpSamples/pgstyle_20260924.csv`；该节已把 §5.1 的 O(n^2.5) 定性为「一处未物化的 LINQ」并消掉。**§5.6 是同一天在其基础上的第二刀**（按元素缓存层叠结果 + `ReadStyle` 双轮并一轮），Playground 全语料 buildDoc 1.76×，原始输出：`src/PaperSharpSamples/pgstyle_20260924_H.csv`、`pgstage_20260924_eager.csv` / `..._cached.csv`。**§5.7 是两刀之后对 buildDoc 残差的解剖**（AngleSharp 53% / 自研 47%，属性读才是 AngleSharp 剩余大头），原始输出：`pgbuild_20260924_split.csv`。**§5.8 是当晚按 §5.7 结论落地的第 1 刀**（每元素属性记忆化视图 `PropCache`，同树 worktree A/B buildDoc −8~−11%），原始输出：`wt_pgstage_20260924_prepropcache.csv` / `wt_pgstage_20260924_propcache.csv`。**§5.9 是第 2 刀**（自研值解析 span 化，22/22 字节全等，重页 buildDoc −7%、全语料 ~−1%），原始输出：`wt_pgstage_20260925_prespan.csv` / `wt_pgstage_20260925_span.csv`。
> **§6 是 2026-09-24 新增的 Playground 语料**（22 个真实页面样例，换自研 PNG 编码器前后对拍），原始输出：`src/PaperSharpSamples/pgstage_20260924_head.csv` / `..._work.csv`。本轮实测已**部分推翻** §0.3 / §5.1 的笼统断言，措辞已按「像素数 vs 元素数」两个轴改写，见 §5.1 末与 §6。

## 0. 结论速览（TL;DR）

1. **PDF 的端到端耗时几乎全在"编码"，不在布局。** `SKDocument` 编码（主要是把 ~1.8MB 字体子集写进文档）占单页 PDF 总时间 **85%–95%**；真正的布局计算（measure+paginate）只有 **1–4ms**。
   > **⚠️ 2026-09-26 已改写**：编码段那 85–95% 的绝对大头是「整嵌一份本次根本用不到的字体」。自研保 GID 的字体裁剪 + 双趟渲染落地后，单页 PDF 中位 228.6 → **31.0 ms**、体积 2464 → **94 KB**，见 §11。本条与下面 §3/§4/§7 的绝对数字均为改前留档。
2. **PNG 比 PDF 快一个量级。** 热态小文档 PNG 中位 ~27–107ms、PDF 中位 ~184–280ms。差异就是 PDF 的字体嵌入 + PDF backend 初始化。
3. **`buildDoc`（逐元素 `GetComputedStyle` 层叠）曾超线性 ~O(n^2.5)——但「它是不是第一瓶颈」取决于两个变量。** 规模扫描里 table 200→2000 行 buildDoc 1.0s→74.4s（×74），flexlist 同步恶化（410ms→21.5s，×52），两种结构同受害 → 成本在**元素个数**，与元素类型无关。**但「上千元素时层叠反超一切」不能笼统外推**：§6 的 22 个真实页面样例（15–501 元素、单幅 3.0–27.9 Mpx）上，改编码器之前编码占端到端 **70%**，元素最多的 m1（501 盒）层叠只有 323ms 而编码 628ms。正确的口径是**两个区间**——① 产物像素数主导（元素数百级、位图百万像素级）→ 编码第一；② 元素数上千 → 层叠第一，且与产物大小无关。本轮把编码压到 14% 之后，①的门槛大幅下移，500 元素级的真实页面（m1 / pricing-*）也已翻转为层叠第一（见 §5.1 末、§6）。这是 M6「自研级联替换 AngleSharp.Css」的核心量化依据（见 §5、§6、§8）。
4. **手写布局层可扩展性良好。** 2000 条目时 measure+position 合计仅 76ms（table）/ 162ms（flexlist），position 段近似线性。
5. **发现并已修复一个真实历史缺陷：表格定位曾是 O(行²)。** `PositionTable` 每行重遍历整棵表树取行盒；修复后 400 行定位 **16.78ms → 0.296ms（≈57×）**，回到线性（见 §5 历史小节）。
6. **吞吐**：单页常规文档（invoice）热态 **4.9 → 42.4 页/秒**（Workstation GC、单线程；提升来自 §11 的字体子集化）。
7. **冷启动首帧**（旧 Debug 数据）：PDF ~630ms、PNG ~64ms；之后进入热态。
8. **✅ 已落地（09-23 那一轮，M6 步骤②+⑥）：展平快照路径换掉逐元素 `GetComputedStyle`。** 同一份 WASM 产物、同一张 964 盒真实站点快照，`precomputed` 渲染 **23.3s → 3.0s（≈7.8×）**，两路 PNG **字节完全相同**（131,980 B）。仅覆盖扩展展平快照；作者 CSS 路径仍是 AngleSharp 全量层叠（见 §5.4；其超线性已于本轮消掉，见 §5.5）。
9. **✅ 已落地（2026-09-24 本轮）：自研并行 PNG 编码器换掉 `SKImage.Encode`。** Playground 口径（22 个真实页面样例）下编码段 **4752 → 339ms（14×）**、全语料端到端 **6761 → 2471ms（2.74×）**，逐像素与 Skia 输出相同、22 样例基准图无需重录。**同时纠正一处旧定性**：Playground 语料上「第一瓶颈」原本是编码不是层叠（见 §0.3 的两区间口径）。
10. **✅ 已落地（2026-09-24 本轮）：作者 CSS 那条路的层叠超线性消掉了，而且没自研级联。** 根因不是「AngleSharp 层叠天生贵」，是它每层叠一个元素、且对该元素的**每个祖先**，都把整份文档的样式表集合（未物化的 LINQ）**重新枚举一遍** → 每元素 O(深度 × 文档大小)。改成「整篇枚举一次、之后吃这份列表」（`Css/EagerStyleCollection.cs`，~40 行，特异度/继承/`!important`/`var()` 全部仍走 AngleSharp 同一批公开方法）：净层叠探针同进程对拍 **19.2×–25.0×**，引擎真实入口 table 2000 行 **74.4s → 5.29s**，规模档回到近线性。逐属性 57,058 次比对 0 差异、盒 JSON 0 差异、31 样例产物像素 0 差异。**但这条买的是元素数轴**：Playground 语料最大 501 盒，端到端只从 2471 → **2091ms（−15%）**；上千元素的文档才是主战场。M6 步骤①③④⑤ 因此不再是消掉超线性的前提（见 §5.5、§8.3）。
11. **✅ 已落地（2026-09-24 同日第二刀，§5.6）：层叠结果按元素缓存 + `ReadStyle` 双轮并一轮。** 物化后 AngleSharp 仍为每个祖先各跑一次全表匹配、且每元素跑 2 轮层叠；用递推式 `R(e)=ComputeCascadedStyle(e, parent: R(最近祖先))` 缓存后**每元素全表匹配恰一次、值折算恰一次**（语义仍一行未替换）。Playground 全语料 `pgstage 9`：buildDoc **1,701 → 964ms（1.76×）**、端到端 **2,893 → 2,001ms（1.45×）**（pricing-mid 2.58×、m1 1.59×）。门：全语料 **1,274,452 次逐属性比对 0 不一致**（含伪元素与 raw 轮）。至此自研级联在性能上已无剩余动机，只剩「移除 AngleSharp.Css 依赖」一条。

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

## 2. 冷启动首帧（每样例一个全新进程，不预热）※ 旧 Debug 数据，09-24 复核时仍未重跑

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
- 本表是 **PDF** 管线的拆解；PNG 出口（Playground / 长图导出）的分段与 22 样例对照见 **§6**，两者瓶颈不同，别混用。
- `paintEncode` 占单页 PDF 总时间 **85%–95%**——对几十~几百元素的小文档它是绝对瓶颈（字体子集写入 + PDF backend）。
- `buildDoc` 在常规样例上 9–58ms（report 元素最多故最高），与编码同量级以下；**但元素上千时反超一切**（见 §5）。
- **布局（measure+paginate）0.6–3.4ms，占比 <2%**——手写布局层不是瓶颈。
- `toBox`（图片解码）除 polish（1.26ms）外几乎为 0（有缓存）。

## 5. 规模扩展与复杂度（合成文档，各 5 次中位）

`dotnet run -c Release -- scale`：table（每行 5 格）与 flexlist（每行一张卡片）两种合成文档，计 `buildDoc / toBox / measure / position` 四段。

### 5.1 buildDoc 超线性 ~O(n^2.5)：层叠随**元素数**爆炸（09-22 净跑核心发现；「是不是第一瓶颈」看本节末的适用边界）

> ✅ **09-24 已消掉，本节数字保留作历史基线**：这条超线性是一处未物化的样式表集合导致的（每元素 × 每祖先各重走一遍全文档），物化后规模档回到线性（800 行 9,405 → **1,486ms**）。§5.5 的净层叠探针在同一档（2000 行 = 10,014 元素）记到改前 25.9s / 改后 1.04s——**绝对值与本节不同源**：那里每元素只算一轮 `GetComputedStyle`，而引擎的 `buildDoc` 每元素要做 2 轮（层叠 + 逐属性兜底）、伪元素再加 2 轮，故本节更高。机制见 **§5.5**。下文「AngleSharp 内部疑似随样式表/元素数重复匹配」当时是推测，现已反编译定性：**随元素数、不随规则数**。

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

**适用边界（2026-09-24 本轮补充，别把这条外推过头）**：上表是**净 buildDoc 循环**（`scale` 模式，同一份 CSS 反复层叠），量的是层叠自身的复杂度。换成 Playground 完整流水线（`pgstage`，22 个真实页面样例、15–501 元素、单幅 3.0–27.9 Mpx）后，501 盒的 m1 层叠只有 **323ms**，而 HEAD 的编码段是 **628ms**——该语料上「第一瓶颈」是**编码**不是层叠。两者不矛盾：**层叠随元素数超线性、编码随产物像素数增长**，谁在前取决于文档落在哪个区间。本轮把编码压掉 14× 之后，m1（306 vs 46ms）、pricing-page（391 vs 48ms）这类数百元素页面也翻转为层叠第一，于是 §8 的 M6 从「大文档才划算」变成「常见页面即划算」（数据见 §6）。

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

### 5.4 展平快照路径改走类名索引层叠（ROADMAP M6 步骤②+⑥，09-23 那轮落地）

**触发点**：扩展导出真实站点（MDN 文档页，快照 964 盒 / HTML 134KB + CSS 62KB）时「引擎渲染中」停在 20 秒级。§5.1 的定位在快照输入上成立——层叠段占引擎时间约八成。

**做法**（`src/PaperSharp/Css/FlatCascade.cs`）：展平快照的规则形状是受约束的（`.ps-N` 单类名、`body`/`html`、`::before`/`::after`，无 at-rule、无组合器、无伪类、无 `!important`），因此可以一次性建 **类名 → 声明** 索引，遍历时按类命中即取值，省掉每元素一次全表 `GetComputedStyle`。任一规则不符合该形状就**整份弃权**退回 AngleSharp——半路混用两套继承链比不加速更糟。AngleSharp 继续负责 DOM 解析与选择器。

**实测（同一进程内 A/B，`dotnet run -c Release --project tools/SnapshotProfile -- ... diff`）**：

| | 层叠 BuildDocument | 端到端 |
|---|---:|---:|
| 老路（AngleSharp 逐元素） | 1,895–2,323 ms | — |
| 新路（FlatCascade） | 249–313 ms | 单遍合计 1.1–1.5 s，其中编码 PNG 74–76% |

**WASM 侧（真实产物，Node 驱动 `renderRaw`，`precomputed:true`，1406px PNG）**：**23.33s / 21.93s → 3.00s / 2.99s**（复跑两轮一致），两路输出 **131,980 B 字节完全相同**。加速后瓶颈回到编码段，与 §0 结论 1 一致。

**双跑对账（M6 步骤⑥，换路径的唯一凭据）**：964 盒逐字段比对差异 **7,183 处**，全量渲染像素差 **0 / 23,706,566**。这个"0"必须排除比较器失明，三条灵敏度对照都看得见：

| 对照 | 差异像素 |
|---|---:|
| body 换高对比底色 | 23,116,782（97.51%） |
| body `margin-left` +40px | 657,480（2.77%），包围盒 x 0–39 |
| 某节点 `position:relative` 位移 200/200 | 3,290,400（13.88%） |

再把 7,183 处差异**按属性逐类整体回填老值**重渲染（35 类，最大一类 938 个节点，同时改声明值与已解析 float）——**每一类都是 0 像素**：这批差异在快照输入下确实惰性。原因有两层：① 展平快照的几何由浏览器算好的 `top/left/width/height` 钉死，margin/padding 不参与这些盒子的定位；② 差异集中在 `top:-320px` 的 skip-link 面板一类画到画布外的块。而差异本身多数是**老路才错**：AngleSharp 自带 UA 表给快照元素注入了浏览器并不存在的幽灵声明（272 处 `a{text-decoration:underline}`、`ul{margin-left:40px}`、`p{margin:16px 0}`），新路按快照语义（缺值即初值）取值。

**顺带修掉两个真实缺陷**：`align-items`/`justify-content` 计算值 `normal` 未归一（布局层只认 `stretch`/`flex-start`，等于这两条规范关键字一直失效）；伪元素的 `box-sizing` 由宿主元素泄漏而来（`capture.js` 现按用值下发）。

**边界**：只覆盖 `precomputed` 展平快照。作者手写 CSS（本地文件、14 个基准样例、`RenderPng` 直接调用）仍走 AngleSharp 全量层叠，§5.1 的 O(n^2.5) 在那条路上依旧成立——M6 步骤①③④⑤（自研 tokenizer、特异度分桶、属性元数据表、值解析接入）仍是待办。

> **09-24 追加·该边界已收窄**：作者 CSS 那条路的超线性**已消掉**，且**没有**动用步骤①③④⑤——根因是一处未物化的样式表集合，改动 ~40 行、层叠语义全交还 AngleSharp。数据与机制见 **§5.5**。

> **09-24 追加·已取证（推翻本段原来的推测）**：本节「编码 PNG 占 74–76%」是 9-23 那份 wasm 的数字，而 §6 的自研编码器**在 WASM 里是负收益**——同一份夹具、生产同参数（`precomputed:true`）实测 3.38s（开）vs 2.53s（`UseFastPng=false`），慢 1.3–1.5×：那条快路的收益主要来自并行，WASM 单线程 + Mono 解释器下逐字节 managed 滤波反而贵过 Skia 的 native zlib。像素两侧 0 差异。数据与机制见 **§6.8**；动作**已落地**（`Api` 静态构造里 `Renderer.UseFastPng = false`，见 **§8.4**）。

### 5.5 作者 CSS 路径：超线性不是「AngleSharp 层叠天生贵」，是一处未物化的 LINQ（2026-09-24 落地，2000 行表格 25.0×）

**结论先说**：§5.1 的 ~O(n^2.5) 不需要自研 CSS tokenizer / 特异度分桶就能消掉。根因是 AngleSharp 每层叠一个元素就把整份文档的样式表集合**重新枚举一遍**，而且对该元素的**每个祖先各枚举一次**。改成「整篇文档枚举一次、之后固定吃这份列表」之后：

| 口径 | 规模档 | 改动前 | 改动后 | 提速 |
|---|---|---:|---:|---:|
| `scale` 的 buildDoc（**引擎真实入口**，每元素实际 2–4 轮层叠） | table 2000 行 | 74,359 ms（09-22） | **5,291 ms** | **14.1×** |
| 同上 | table 800 行 | 9,405 ms（09-22） | **1,486 ms** | 6.3× |
| 同上 | flexlist 2000 条 | 21,306 ms（09-22） | **2,631 ms** | **8.1×** |
| 同上 | flexlist 800 条 | 3,205 ms（09-22） | **830 ms** | 3.9× |
| `pgstyle` 净层叠 A/F 行（**每元素恰好一轮**，同构建同进程内对拍） | table 2000 行（10,014 元素） | 25,912 ms | **1,036 ms** | **25.0×** |
| 同上 | flexlist 2000 条（6,007 元素） | 5,409 ms | **282 ms** | **19.2×** |

**指数确实被改掉了**——同一份 `scale` 净循环折算成 µs/元素：

| 行数 | 800 | 1200 | 1600 | 2000 |
|---:|---:|---:|---:|---:|
| 改前（09-22 折算） | 2,343 | — | — | **7,426** ← 元素数 ×2.5 而耗时 ×3.2，即 §5.1 拟合的 O(n^2.5) |
| 改后（09-24 实测） | 370 | 573 | 653 | **528** |

改后 800→2000 行折算约 **O(n^1.1)**；该档每元素仍要做 2 轮层叠（`ReadStyle` 的 `ComputeDeclarations` + 逐属性兜底 `GetDeclarations`），1200–1600 那段上凸未单独定性，与 §6.6 是同一口径问题（10ms 以下的段差不作数），故只报「回到近线性」，不报精确指数。

⚠️ 前两行组（`scale`）的「前」来自 09-22 那轮，跨日绝对值只当量级参考；**同构建、同进程、一次运行内对拍的 A/F 两行才是 19–25× 这条结论的依据**。

**机制**（用 `ilspycmd` 反编译 `AngleSharp.Css` 1.0.0-beta.176 得到，不是推测）：三层展开——

1. `WindowExtensions.GetComputedStyle(window, el, pseudo)` ≡ `window.GetStyleCollection().ComputeDeclarations(el, pseudo)`。
2. `GetStyleCollection(IWindow)` 返回 `new StyleCollection(first.Concat(second), device)`，其中 `second = document.GetStyleSheets().OfType<ICssStyleSheet>()` —— **没有 `ToList()`**，每枚举一次就重走一遍全文档的 `<style>`/`<link>` 收集。
3. `StyleCollection.GetDeclarations(styles, element, pseudo)` 先 `styles.ComputeCascadedStyle(element)`，再 `foreach (IElement item in element.GetAncestors().OfType<IElement>()) styles.ComputeCascadedStyle(item)` —— **每个祖先各一次**，每次都触发第 2 条的重枚举。

净效果：每元素 **O(深度 × 文档大小)**，整篇 O(n²)；再叠上第 3 条里每祖先一次全表匹配，拟合出 §5.1 的 n^2.5。

**探针取证**（`dotnet run -c Release -- pgstyle 1000 2000`，列值为 µs/元素；`STY` 行）：

| 档 | 说明 | flexlist 250 | flexlist 1000 | flexlist 2000 | table 1000 | table 2000 |
|---|---|---:|---:|---:|---:|---:|
| A | 逐元素，**改动前**语义（每元素一轮 `GetComputedStyle`） | 181.3 | 446.7 | 900.5 | 1,147.4 | **2,587.6** |
| B | 只把 `GetStyleCollection()` 外提出循环（最省力那招） | 154.1 | 441.0 | 947.5 | 1,180.1 | 2,542.5 |
| C | 只枚举集合、不层叠 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| D | 同一条老路上把规则从 7 条灌到 207 条 ÷ A（只在 1000 档测） | — | ×1.1 | — | ×1.1 | — |
| F | 逐元素，**改动后**（物化集合，仍一轮） | 48.2 | 49.9 | 46.9 | 66.5 | **103.4** |
| G | 物化 + 只算宿主元素、不算祖先 | 8.3 | 7.5 | 9.1 | 10.0 | **12.9** |

原始输出已入库：`src/PaperSharpSamples/pgstyle_20260924.csv`。

四条读数：

- **B 与 A 同量级（比值 0.75–1.06，无任何指数被消掉）、C = 0.00 ms** —— 「把 `GetStyleCollection()` 提到循环外」这条最省力的做法**治不住**（我最初的假设就是它，被探针当场推翻）。指数不在「取集合」这一步，而在第 3 条的**每祖先各枚举一次**：只有把集合**物化**成列表，那几次枚举才变成免费的数组遍历。
- **A ÷ F 随规模增大而增大**（flexlist 250 → 2000 条：3.8× → 19.2×；table 1000 → 2000 行：17.2× → 25.0×），且 **F 那一列几乎不随规模走**（flexlist 48.2 / 49.9 / 46.9 µs）。改动前是规模函数、改动后是常数 → 消掉的是**随元素数增长的那一项**，不是某个固定开销。
- **D ÷ A ≈ ×1.1** —— 同一条老路上规则数从 7 条灌到 207 条只贵一成：**驱动是 DOM 重走，不是规则数**。据此修正 §9 里「真实网页大样式表只会更糟」那句（见该节）。
- **G ≈ F 的 1/5 ~ 1/8**（table 2000 行 103.4 → 12.9 µs）—— 即使集合已物化，AngleSharp 仍为每个祖先跑一次全表匹配，而同一篇文档里祖先的层叠结果被反复重算。**按元素缓存 `ComputeCascadedStyle` 是下一个 5–8× 的杠杆**（见 §8.3）。

**改动只有两处，且不含任何语义替换**：`src/PaperSharp/Css/EagerStyleCollection.cs`（~40 行，实现 AngleSharp **公开**的 `IStyleCollection`：`IEnumerable<ICssStyleRule>` + `IRenderDevice Device`）；`StyleResolver.BuildDocument` 里 `ctx.Styles = new EagerStyleCollection(window.GetStyleCollection())`，把三处层叠入口（`ReadStyle` 的两轮 + 伪元素 `content`）指向这份物化集合，`Walk`/`ReadStyle` 顺带不再传 `IWindow`。特异度排序（`SortBySpecificity`）、UA/作者/内联覆盖顺序、`!important`、继承、`var()` **全部仍走 AngleSharp 同一批公开扩展方法**——本类只改「规则列表从哪来」。前提是层叠阶段文档与样式表只读（引擎侧成立：`BuildDocument` 之后 DOM 不再变动）。

**正确性门**（当时测试套件被对端在途的布局改动污染，见下面第 4 条，所以不能只靠 golden）：

1. **逐属性对账**：同一份文档、同一批元素，老（懒集合）vs 新（物化）读 94 个属性 × 全部元素 = **57,058 次比对，0 处不同**（探针 E 行）。
2. **盒几何**：`boxes flex` 的 41 个盒逐字段比对，**0 差异**。
3. **产物像素**：31 个样例 PNG 字节比对，只有 `_reph.png` 字节不同，而 PIL `ImageChops.difference(...).getbbox()` 为 `None` → 像素完全一致（PNG 元数据/压缩顺序差异，非渲染差异）。
4. ⚠️ **本次测试套件不构成门**：eager 构型下 8 个失败（`flex`/`m4`/`pct` × PngGolden/BoxGolden/PdfRaster）；把工作树里那一行翻回老语义后失败**变成 9 个**，再跑一次 **11 个**（多出 `m3`/`pricing-foot`/`pricing-page`）。即失败数随对端未提交的 `LayoutBuilder.cs`/`Fixture.cs`/`BoxGoldenTests.cs` 漂移，与本次改动无关。**按约定不重录 golden**，等对端提交后再跑一次全量。
5. **绕开那三个被污染的套件后**：`dotnet test -c Release --filter "FullyQualifiedName!~PngGoldenTests&FullyQualifiedName!~BoxGoldenTests&FullyQualifiedName!~PdfRasterTests"` → **186/186 全绿**，连跑两次一致（含 `AuthorMarginCascadeTests`、`PseudoElementTests`、`OverflowClipTests`、`MarginCollapsingTests` 这些直接吃层叠语义的用例）。

**Playground 语料上的真实收益（`pgstage 5`，22 样例）——比上表小得多，别外推**：buildDoc 合计 **1,544 → 1,238 ms（−20%）**，端到端 **2,471 → 2,091 ms（−15%）**。这条买的是**元素数轴**（§0.3 的两个区间里的第二条），而 Playground 语料最大 501 盒、离 1000+ 的爆炸区还远，所以只有一两成；上千元素的文档（长表格导出、整页快照以外的作者 CSS）才是主战场。改后 buildDoc 最重的样例仍是 pricing-page（373 盒 259.9ms，e2e 384.0）与 m1（501 盒 192.0ms，e2e 307.3）。

**M6 因此重排**：步骤①③④⑤（自研 tokenizer、特异度分桶、属性元数据表、值解析接入）**不再是消掉超线性的前提**。剩下的动机只有两条——彻底移除 `AngleSharp.Css` 依赖、以及拿下 G 行那 5–8×（按元素缓存层叠结果）。**→ 当天稍后已拿下，见 §5.6。**

### 5.6 §8.3 的两刀（按元素缓存 + 双轮并一轮）一次落地：Playground 全语料 buildDoc **1.76×**、e2e **1.45×**（2026-09-24 同日第二轮）

**结论先说**：§5.5 留下的两刀——(a) 物化后 AngleSharp 仍为**每个祖先**各跑一次全表匹配、结果被反复重算（G 行显示可再省 5–8×），(b) `ReadStyle` 每元素跑 **2 轮**层叠（computed + `%`/`calc` 兜底的 raw 声明串）——合在**同一处改动**里消掉了：`EagerStyleCollection`（`src/PaperSharp/Css/EagerStyleCollection.cs`）升级为按元素缓存，于是每元素**全表匹配恰一次、值折算恰一次**。

**机制**（仍不含任何层叠语义替换，全部走 AngleSharp 公开方法）：

- 缓存递推式 `R(e) = ComputeCascadedStyle(e, parent: R(最近祖先))`。它与 AngleSharp 内部「`Set(cascade(e))` 后逐个 `Update(cascade(ai))`」的折叠**等价**：`UpdateDeclarations` 的三条规则（就近优先、只覆写 `IsInherited` 占位、过滤不可继承属性）对逐次折叠可结合。`ComputeCascadedStyle(element, parent)` 本身就是公开扩展方法。
- raw 轮（`Spec()` 要的 `%`/`calc` 未折算声明串）直接吃 computed 轮的缓存条目，双轮并一轮。
- `GetDeclarations` / `ComputeDeclarations` 刻意与 AngleSharp 的**同名扩展方法**同签名——`DocContext.Styles` 字段类型改成具体类后，实例方法遮蔽扩展方法，`StyleResolver` 的调用点一行没动。
- 伪元素与 AngleSharp 同构：声明 = 伪元素自身层叠 ∪ 宿主的**祖先**链（不含宿主），且在替换伪元素**之前**取宿主祖先链；缓存键 = (宿主, 伪元素名)。值折算复刻其私有 `CssComputeContext`（公开的 `ICssComputeContext`，`Resolve` 只认 `--自定义属性`）。

**净层叠探针**（`pgstyle`，同构建同进程 A/F/G/H 对拍，µs/元素中位；原始输出 `src/PaperSharpSamples/pgstyle_20260924_H.csv`）：

| 档 | A 改前 | F 物化（§5.5） | G 物化+只算宿主（参照下界） | **H 本轮（缓存）** | H÷F |
|---|---:|---:|---:|---:|---:|
| flexlist 250（757 元素） | 186.4 | 54.0 | 9.0 | **3.99** | 13.5× |
| flexlist 500（1,507） | 359.0 | 63.7 | 13.7 | **5.93** | 10.7× |
| table 250（1,264） | 369.1 | 88.9 | 11.9 | **10.5** | 8.5× |
| table 500（2,514） | 649.1 | 104.2 | 12.6 | **13.8** | 7.6× |

⚠️ H 行量的是**缓存命中稳态**（预热一轮填缓存后取中位），比引擎单次 `buildDoc` 的「填充+命中」总成本偏乐观——**真实收益以下表的引擎入口为准**。表格档 H≈G，即重复祖先匹配已被压到理论下界附近（flexlist 档 H 甚至低于 G：G 每次调用仍重算宿主层叠，H 直接查表）。

**引擎真实入口**（`pgstage 9`，22 样例，「前」= §5.5 物化版、「后」= 本轮；buildDoc/paint/encode 取 9 次最小值、e2e 取中位；两份 CSV 已入库 `pgstage_20260924_eager.csv` / `..._cached.csv`）：

| 口径 | 前 | 后 | 提速 |
|---|---:|---:|---:|
| buildDoc 列合计 | 1,700.8 ms | **964.2 ms** | **1.76×** |
| e2e 列合计（中位） | 2,893.3 ms | **2,001.0 ms** | **1.45×** |

逐样例（buildDoc，前→后，ms）：pricing-mid 304.4→117.8（**2.58×**）、pricing-foot 91.3→43.3（2.11×）、flex 66.7→33.8（1.97×）、pricing-page 362.4→189.0（1.92×）、m7 30.0→16.6（1.80×）、m1 272.1→171.2（1.59×）、login-page 39.6→27.8、m3 49.2→28.8。两轮累计（`4cf7b4d` 的 2,471 → 现在 **2,001ms**，且这一对是同 harness 连跑）。小样例 inline（buildDoc 24.9→30.5）、invoice（11.4→11.9）微涨——与 §6.6 同源（10ms 级段差无分辨力，且两次运行之间对端仍在改布局），不作为回归证据。

**正确性门**（golden 套件此刻仍被对端在途布局改动污染，见第 3 条）：

1. **H-check 全语料逐属性对拍**：22 个真实样例 × 每个元素 × {宿主, `::before`, `::after`} × {computed 轮, raw 轮} × 94 属性 = **1,274,452 次比对、0 不一致**。异常处理语义：双侧同抛（AngleSharp `CssPeriodicValue.CssText` 已知 NRE 类）跳过、**单侧抛即计为不一致**——共 16 条双侧同抛。探针已并入 `pgstyle`（H 行 + 逐样例累计 H-check 行）。
2. **非 golden 套件 212/212 全绿**（含直接吃层叠语义的 `AuthorMarginCascadeTests`、`PseudoElementTests`、`OverflowClipTests`、`MarginCollapsingTests`）。
3. ⚠️ **golden 套件本次 30 个失败、均不归本改动**：15 个是上一轮已挂账的在途漂移；新增的 15 个 BoxGolden/Png/Pdf + `InlineImageShrinkWrapTests.InlineBlockOnBaseline_doesNotInheritReplacedElementDescent`（Expected 40 / Actual 42.2，正是 inline-block 基线）逐条落在对端**未提交**的 `InlineLayout.cs`（改 `StrutBelow`/`baseByReplaced`）与 `LayoutBuilder.cs` 上。第 1 条已证层叠输出逐条相同 ⇒ 布局的输入没变。**按约定不重录 golden**，等对端提交后全量跑一次。

**M6 后续**：§5.5 末的两个性能动机至此消掉一个半——自研级联只剩「移除 `AngleSharp.Css` 依赖」这一条**依赖收敛**诉求。层叠这条路的固定成本还剩每元素一次值折算 + ~94 属性读 + 建树（buildDoc 仍占 e2e 48%）——**这三块的占比已解剖，见 §5.7：大头是属性读，不是层叠**。

### 5.7 buildDoc 残差解剖：AngleSharp 53% / 自研 47%，且 AngleSharp 那半的大头是**属性读**不是层叠（2026-09-24，`pgbuild` 拆解探针）

§5.6 之后 buildDoc 还剩什么？把 `pgbuild` 探针改造成按**引擎真实路径**（缓存语义）逐段计时：解析 / 全表匹配 / 值折算 / 伪元素上界 / 94 属性读，逐段与 `BuildDocument` 总账对平（自研部分 = 残差）。22 样例合计（引擎总账 1,244ms；⚠️ 该绝对值是 pgbuild 口径的 5 次中位，比 pgstage 的 964ms（9 次最小值）高 ~28%，**占比才是结论**）：

| 段 | 合计 ms | 占 buildDoc | 归谁 |
|---|---:|---:|---|
| 94 属性读 × 全元素（`GetPropertyValue` 拿串） | **385.0** | **31%** | AngleSharp |
| 层叠全表匹配 + 值折算（宿主轮） | 110.4 | 9% | AngleSharp（其中**折算只占 25.7ms/2%**，匹配 84.7ms） |
| 伪元素轮（上界：全元素都命中 scope，只有 6 样例有伪元素规则） | ≤107.8 | ≤9% | AngleSharp |
| HTML+UA+作者 CSS 解析 | 55.9 | 4% | AngleSharp |
| **AngleSharp 小计** | **659.1** | **53%** | |
| 自研值解析（`props+ParseSize − props` 差值） | 187.6 | 15% | 自研 |
| 建树 / 填 `ComputedStyle` / 正则预处理 / DomPath / `ExpandVars`（残差） | 397.8 | 32% | 自研 |
| **自研小计** | **585.4** | **47%** | |

**三条读数**：

1. **「AngleSharp 是不是瓶颈」现在有确切答案：占一半，但不再是那个「算法贵」的一半。** 层叠匹配 + 折算总共只 9%（§5.5/§5.6 两刀把它从超线性压到了线性单价）；AngleSharp 那 53% 里 31 个点是**每元素 94 次 `GetPropertyValue` 字符串读**（m1：79.6ms / 510 元素 ≈ 1.66µs/条，每读一次都在分配串，m1 一轮 14.3MB）。
2. **这直接推翻 §8/P4 里「按 display 少读属性只值 15ms/510 元素」的旧估算**——那 15ms 是按旧探针一行 ~1.1µs 估的，而真实语料上属性读是 ~80ms/510。两档可打的杠杆浮出：① **一趟枚举摊平**——每元素把折算后的声明集合 `foreach` 一次建成 `name→value` 字典（94 次按键查找变 1 次枚举 + 94 次字典命中），风险只在 `GetPropertyValue` 的取值语义（简写展开/自定义属性）是否与枚举项一致，可证伪门现成（H-check 同形状）；② display 分型跳读，回归面比 ① 大（`ComputedStyle` 默认字段问题，见 §8 原注）。
3. 自研那 47% 里最大块是**残差 32%**（Walk + 建树 + 填字段 + DomPath + 正则预处理混在一起，尚未再拆）；`props+ParseSize` 差值说明值解析占 15%。Playground 口径 DomPath 恒开，若按 §6.6 换 GC 分辨力工具再拆残差，DomPath 与正则预处理都有单独可削的空间（生产导出其实不需要 DomPath）。

原始输出：`src/PaperSharpSamples/pgbuild_20260924_split.csv`（`dotnet run -c Release -- pgbuild 5`，探针 `CascadeProbe.Run` 已改为按缓存语义逐段计）。

### 5.8 第 1 刀落地：每元素属性记忆化视图 `PropCache`，同树 A/B 实测 buildDoc **−8~−11%**（2026-09-24 夜）

§5.7/§8 列的首选打法换了个更省的形状落地：**不是**「一趟枚举整个声明集合建字典」（那要求枚举项与 `GetPropertyValue` 的简写展开语义逐串一致，对账面大），而是**惰性记忆化视图**——`StyleResolver.ReadStyle` 里的 `Get`/`Spec` 两条读路径各包一个 `PropCache`（每元素 2 个，内部 `Dictionary<string,string?>` 按需填，值含 `ExpandVars` 后的串；「读抛异常」也缓存为 null→""，与老的逐次 try/catch 吞异常语义一致）。收益来源：`Val` 恒读 Spec+Get、`Raw`/`Lookup` 空串兜底再读，同一视图内同属性第二次起的读全部变字典命中——引擎每元素 ~130 次读里被削掉的是重复的那 ~36 次/属性实例，唯一属性的首读成本不变。

**同树 A/B（关键方法论）**：当晚对端 FontManager/InlineLayout/LayoutBuilder 连改三轮，跨时间段的「改前 CSV vs 改后 CSV」直接被污染成 0%（§5.7 那份 cached CSV 基线已过期）。改用**独立 git worktree 冻结整棵树**，两个臂只差 `PropCache` 那两个 hunk，同一小时窗口里 B1→A1→B2 交错跑 `pgstage 9`：

| 口径（22 样例合计 ms） | A（无 PropCache） | B1 | B2 | Δ |
|---|---:|---:|---:|---|
| buildDoc | 1,046.9 | 962.8 | 927.8 | **−8.0% / −11.4%** |
| e2e 中位 | 2,173.0 | 1,928.8 | 1,945.0 | **−11.2% / −10.5%** |

逐样例（A→B2）：重页面全部兑现——m1 198.8→163.1（−18%）、pricing-mid −17%、pricing-page −17%、m4 −19%、m3 −13%、long −14%；轻样例在 B1 里一度显 regression（flex +25%），B2 复测回落到 +2.6%，判定为冷启动噪声。**对端当晚的布局改动自己给 buildDoc 加回了约 8%**（A 臂 1,047 vs 傍晚 cached 基线 964），所以主树跨日对比看起来「白干」——这正是必须同树 A/B 的实证。

**正确性门**：① worktree 两臂各跑 22 样例 `boxes` JSON，**22/22 字节全等**（几何/样式输出逐位相同）；② 非 golden 测试套件 189/203 过，14 个失败全部为对端在途改动的既有归属（12 个 PngGolden 里 img/m1/m5/m9 四个正是对端 23:24 那轮 InlineLayout 改动新掀动的样例，其余 8 个为傍晚已知集；外加 2 个 InlineImageShrinkWrap 对端几何断言）；③ 未动 `EagerStyleCollection`，H-check 无需重跑。

原始输出：`src/PaperSharpSamples/wt_pgstage_20260924_prepropcache.csv`（A 臂）、`wt_pgstage_20260924_propcache.csv`（B2）、`pgbuild_20260924_propcache.csv`。

### 5.9 第 2 刀落地：自研值解析 span 化，收益比 §5.7 的账面预估**小一个量级**（CSS 重页 buildDoc −7%、全语料 ~−1%）（2026-09-25 凌晨）

`CssValueParser` 十个自研函数（`ToPixels`/`ParseSize`/`ParseColor`/`TryParseNamedColor`/`ParseLineHeight`/`ParseTextAlign`/`ParseFontWeight`/`ParseOpacity`/`ParseFlexNumber` + `ParseFloat(span)` 重载）改走 `ReadOnlySpan<char>` 切片，单位剥离不再产生 `value[..^2]` 子串分配；`EndsWith`/`Equals` 从 culture-sensitive 实例重载换成 `Ordinal(IgnoreCase)`（对 AngleSharp 计算值这种纯 ASCII 小写串逐字符等价）。**公开签名全部不变**，语义边角逐字保真（如 `ParseSize` 百分比分支：数值取**未 trim** 切片、判空用 trim 后切片，`"50 %"` 解析为 0 的行为原样保留）。

**正确性门（同树 worktree 两臂）**：
- 对照臂 B′ = span 文件**只回退我的 9 个方法体**（保留对端混在同一文件里的 calc 指数修复 `ExpDigits`，见下）；实验臂 C′ = 现主树文件。两臂 22 样例 `boxes` JSON **22/22 字节全等**。
- 期间插曲：前一晚的臂间 m4 +30px「疑似回归」经全量 diff + 双跑探针（新旧实现同进程逐调用对账，0 mismatch）+ hybrid 臂三分定位，**确证是对端 `EvalCalc` 指数词法修复**（`0.5em + 8px` 曾被切成 `0.5e`+单位 `m` 致整条 calc 归 0），不是 span 刀。`CssValueParser.cs` 自此也是**混合作者文件**，提交时要按 hunk 拆。
- 非 golden 测试 185/203；5 个非像素失败（InlineImageShrinkWrap ×2、TableSpecifiedColumn ×3）在 B′ 臂（无我的 hunk）+ 对端最新布局文件上**同样失败**，归属对端在研。

**性能量化（同树四轮 pgstage，B1→C1→B2→C2）**：全语料 buildDoc 合计 B2 762.6 → C 755.7/757.4（**−0.8%**）；第一轮 B1 838.5 是冷启动垫高的假 baseline（由此前「B1→C1 −9.9%」的误读纠偏）。对 4 个重样例 15 迭代 ×3 轮取中位：pricing-page 136.0→125.5（**−7.7%**）、pricing-mid 95.1→87.9（**−7.6%**）、pricing-top 52.6→49.1（**−6.7%**）、m1 ≈0%（AngleSharp/inline 主导，无差异）。小样例（login-page +57%、invoice +31%）经 25 迭代 ×2 轮复测回落为噪声/反向小赢，与 §6.6 「10ms 量级分段不可信」的告诫一致。

**判读**：§5.7 那块 161ms（props+ParseSize − props）里约七成是 AngleSharp `GetPropertyValue` 自己的字符串分配，PropCache 命中后自研解析只对**未缓存的首读**起作用——所以 span 化的真实收益是「CSS 值密度高的页面 buildDoc −7%」，不是账面 15%。方向仍赚（字节全等 + 分配下降），保留。下一档杠杆只剩：display 分型跳读（回归面大）、正则预处理/DomPath 门控（残差 32% 未拆）。

## 6. Playground 路径 · 换 PNG 编码器前后对拍（2026-09-24 本轮新增）

**口径**：`dotnet run -c Release --project src/PaperSharpSamples -- pgstage 9`，与 Playground 页面同参数（宽 1284 / `scale:2` / `domPath:true`），22 个真实页面样例 × 9 次迭代。
「前」= `f8e94d2`（未优化的 HEAD，跑在当时位于 `D:/Github/ps-base` 的独立工作树里，⚠️ 该树现已被回收、且里面用的是本轮改过的 harness 副本，详见 §9 局限）；「后」= 本轮改动（commit `4cf7b4d`）。
原始输出已入库：`src/PaperSharpSamples/pgstage_20260924_head.csv` / `..._work.csv`。
**两段口径不同、不可相加对齐**：分段列是 9 次里的**最小值**，`e2e` 列是整条 `RenderRasterWithBoxes` 的**中位数**（所以 `stage_min_sum` 常小于 `e2e_med`；CSV 末列 `collect` 出现负值即此噪声，不代表有负耗时）。

### 6.1 逐样例对照（单位 ms；buildDoc = 层叠段，编码 = 位图→字节段含 `Snapshot`）

| 样例 | Mpx | 盒 | buildDoc 前→后 | 编码 前→后 | **e2e 中位 前→后** | 提速 |
|---|---:|---:|---|---|---|---:|
| invoice | 3.0 | 15 | 6.28 → 11.37 | 79.98 → 5.95 | 93.9 → **23.7** | 3.96× |
| m9 | 3.4 | 22 | 9.66 → 11.35 | 91.96 → 6.39 | 113.4 → **24.5** | 4.63× |
| img | 3.4 | 27 | 11.87 → 35.14 | 92.71 → 6.96 | 121.5 → **57.0** | 2.13× |
| m5 | 4.7 | 21 | 7.88 → 9.12 | 133.54 → 9.01 | 147.9 → **26.5** | 5.58× |
| polish | 4.5 | 25 | 10.99 → 12.16 | 120.31 → 9.01 | 148.2 → **33.5** | 4.42× |
| pct | 4.0 | 36 | 14.22 → 17.25 | 111.16 → 7.85 | 138.0 → **33.6** | 4.11× |
| login-page | 5.6 | 33 | 26.40 → 31.66 | 155.47 → 12.22 | 251.0 → **116.3** | 2.16× |
| long | 5.3 | 35 | 13.00 → 14.31 | 149.13 → 10.96 | 175.4 → **37.7** | 4.65× |
| inline | 3.7 | 39 | 16.68 → 24.40 | 108.70 → 9.61 | 138.5 → **44.7** | 3.10× |
| m4 | 4.6 | 39 | 17.83 → 19.10 | 133.04 → 10.21 | 174.6 → **45.6** | 3.83× |
| flex | 4.1 | 41 | 43.33 → 51.54 | 114.53 → 8.15 | 154.3 → **69.2** | 2.23× |
| m7 | 5.1 | 41 | 18.21 → 19.99 | 137.58 → 10.07 | 215.8 → **88.9** | 2.43× |
| pricing-top | 7.5 | 114 | 94.85 → 108.07 | 200.02 → 17.38 | 321.7 → **151.2** | 2.13× |
| m8 | 5.5 | 32 | 24.07 → 27.16 | 147.39 → 10.05 | 180.2 → **46.2** | 3.90× |
| pricing-foot | 4.2 | 59 | 57.21 → 58.34 | 115.33 → 6.92 | 183.0 → **80.7** | 2.27× |
| table | 3.7 | 66 | 30.80 → 34.06 | 98.58 → 7.22 | 146.7 → **49.3** | 2.98× |
| m3 | 9.7 | 66 | 32.02 → 34.65 | 278.68 → 20.90 | 338.6 → **80.9** | 4.19× |
| m2 | 14.8 | 69 | 37.41 → 40.29 | 406.92 → 31.36 | 474.7 → **104.4** | 4.55× |
| report | 6.4 | 125 | 41.40 → 46.11 | 170.44 → 11.81 | 224.4 → **71.1** | 3.16× |
| pricing-mid | 19.1 | 242 | 234.98 → 240.27 | 522.40 → 33.11 | 820.3 → **322.6** | 2.54× |
| pricing-page | 27.9 | 373 | 375.40 → 391.45 | 756.17 → 47.97 | 1212.8 → **568.5** | 2.13× |
| m1 | 21.2 | 501 | 322.58 → 305.87 | 627.77 → 45.81 | 986.2 → **394.7** | 2.50× |
| **合计** | **171.4** | **2021** | **1447 → 1544** | **4752 → 339（14×）** | **6761 → 2471** | **2.74×** |

**同期落地的第二条杠杆**：`AuthorBoxSpacing` 改为八条边一趟裁决（同一段层叠里逐边定胜者，不再四边各跑一轮）。CSS 重的样例略优（m1 层叠 323→306ms），聚合持平——它的真正价值是把「八条边的特异度裁决」这件事收敛成一处，且引擎盒矩形与 HEAD **逐字节相同**（`bash tools/selfdiff.sh` → `identical=22 differing=0`）。

### 6.2 瓶颈翻转：Playground 语料上，层叠接手成为第一开销

| 段 | 前（HEAD）占 e2e | 后（本轮）占 e2e |
|---|---:|---:|
| 编码 | **70%**（4752/6761） | 14%（339/2471） |
| 层叠 buildDoc | 21%（1447/6761） | **62%**（1544/2471） |
| paint | 5%（328/6761） | 14%（356/2471） |
| measure+position | 0.6%（42/6761） | 2%（49/2471） |

这即是 §0.3 / §5.1 需要改写口径的实证依据：编码没降之前，这份 ≤501 元素的真实语料上是**编码**第一；编码降下来之后单样例层叠占比 62%，500 元素级的 m1 也是层叠第一（306 vs 46ms）。
占比列看着 paint 涨了 3 倍——绝对值 328→356ms 只涨 8%，是分母变小造成的，不是回归。

> **09-24 再补一层（同一天里 §5.5 又落地之后）**：上表的「后」= commit `4cf7b4d`（只换编码器）。再叠上物化样式表集合后，`pgstage 5` 全语料 buildDoc **1,544 → 1,238 ms**、e2e **2,471 → 2,091 ms**，编码占比回升到 16%、层叠占比降到 **59%**——即这份 ≤501 元素的语料上「层叠第一」这个结论**不变**，只是层叠自身没那么贵了；这一刀的真正买主是上千元素的文档（§5.5）。**当天第三层（§5.6，按元素缓存 + 双轮并一轮）**在此之上 `pgstage 9` e2e **2,893 → 2,001 ms**（与 §5.6 同一次连跑对拍，迭代口径不同于上面那行）。

### 6.3 自研 PNG 编码器的设计要点（`src/PaperSharp/Imaging/PngEncoder.cs`）

- **为什么值得自己写**：Skia 的 PNG 编码逐行在 5 种滤波里挑最优（`PNG_ALL_FILTERS`）、再跑最高级 zlib、且全程单线程。m1（21.2 Mpx）单是 `SKImage.Encode(Png,100)` 就 **732ms 中位**（`pgenc 7 m1`；同量级的另一口径是 pgstage HEAD 分段 min 628ms），而该样例整条 e2e 才 986ms。
- **固定 Up 滤波**：省掉逐行滤波搜索。引擎只在 `None`/`Up` 两种里选（`PngFilter` 只留这两档，实测其余三种更慢或不更小）。m1 上 `Up+Optimal` 31.7ms/1104KB，`None+Optimal` 27.8ms/999KB——**这条样例上 `None` 反而更好**，全语料定性见 §6.7。
- **按行分块并行 deflate**：均匀切行成若干块，各块独立 deflate 并以 `Z_SYNC_FLUSH` 收尾，再拼成**同一条** zlib 流；adler32 用 zlib 的合并公式 `adler32_combine` 跨块累加（同一块内各 strip 共用一条流，历史仍可互引；块间不能互引，这就是分块付的压缩率代价）。m1 同滤波同档位：单线程 192.3ms → 16 块 31.7ms（**6×**）。
- **块数写死 8，不跟 `Environment.ProcessorCount` 走（字节确定性）**：块数决定 deflate 历史窗口在哪断开，因而**直接决定输出字节**。跟着核数走，同一份文档在不同机器上会编出不同 PNG，而 `PngGoldenTests` 是逐字节比对的基准门。8 块实测已吃到并行收益的绝大部分（生产默认 8 块下 m1 编码段 45.8ms，含 `Snapshot`）。
- **strip 缓冲压在 1MB 以下走 `ArrayPool`**：`ArrayPool<byte>.Shared` 只缓存 ≤1MB 的 `byte[]`，超过就变成「每次 Rent 现 new、每次 Return 即丢」。2× 长图按 8 分每块就是几 MB，全砸在托管堆上——GC 的账会记到**下一次**渲染的 buildDoc 头上（§6.6 的疑点即同一机制）。
- **无损性是硬门，不是愿望**：输出解码后必须与 Skia 输出逐字节相同。`PngEncoderTests`（逐像素相同 / 分块不改变像素 / `rowBytes` 必须等于 `stride`）+ `PngGoldenTests` 全绿，22 样例基准图零重录。超大图由 `PngEncoder.Supports` 拒绝并退回 Skia。

### 6.4 两条负面结论（同样是资产，别再试第二遍）

1. **「按元素跳过第二轮层叠 `GetDeclarations`」不成立。** 设闸门后实测：闸门只放行 3/505 个元素时，`GetDeclarations` 调用数仍是 505（与不设闸门一样），buildDoc 耗时不变。根因是 AngleSharp 的 computed **对未声明的属性直接返回空串**（不补初始值），于是 `Raw`/`Val` 的「computed 空 → 退声明值」兜底必然再问一次。实现已整体回退，一行不剩。
2. **margin 的 `em` 不能读 computed。** AngleSharp 折 `em` 用的是**渲染设备基线 16px**，不是元素自己的字号：m1 的 `.note{font-size:10pt}` 里 UA 的 `ul,ol{margin:1em 0}` 会被折成 16px 而非 13.333px，每条列表项凭空下坠 2.667px。已钉成单测 `AuthorMarginCascadeTests.UaMarginInEm_usesElementFontSize_notDeviceBase`——别把 `ReadStyle.MarginVal`「简化」成只读 computed。

### 6.5 已否决的方案（都带实测判据）

| 方案 | 否决理由（实测） |
|---|---|
| PNG `quality` 调档 | Skia 对 PNG 的 quality 是**空操作**：`pgenc 5` 跑全 22 样例，q=100/50/10 的产物合计都是 **6,643KB——一字不差**，耗时 5068/5019/5015ms 也全在噪声内（m1 单样例同样三档均 1280KB）。没有可调空间。 |
| JPEG / WebP 预览 | `pgenc` 里 skia-jpeg q=95 全语料 1,270ms（vs PNG 5,068ms；m1 单样例 197 vs 732ms），看着最香，但它**有损**——Playground 的验收门是与浏览器逐像素对拍（`tools/boxdiff.mjs` / `sweep.sh`，聚合漂移面积 22254、全绿盒 557/2021），换有损预览等于把这条门废掉。况且在这类平色页面上它**还更大**（全语料 10,510KB vs 6,643KB），连体积都不是卖点。 |
| 画布 / `SKSurface` 池 | `pgalloc` 实测 m1（85MB 画布）`create` ≈ 0.0ms、fresh-clear 20.9ms **==** reuse-clear 20.9ms（`Clear` 就是按内存带宽刷一遍，与画布新旧无关）；整条链 reuse 77.4 vs fresh 106.4ms，省下的 29ms 只占该样例 e2e（1.06s）的 **2.7%**，代价是常驻一张 85MB 原生缓冲 → 不做。 |

### 6.6 未定性疑点：小样例 buildDoc 变慢（P2，需要专用工具）

`img` 的 buildDoc 在完整流水线里 **11.9 → 35.1ms**（22 样例里唯一涨幅接近 3× 的），但**纯 buildDoc 循环**（`dotnet run -- pgbuild 15 img`）两棵树都是 43–49ms，且 `long`/`m5` 只差 1–3ms。两个口径自相矛盾 → 说明 **pgstage 的分段计时被跨轮 GC / 计时起点污染**（§6.3 那条「编码垃圾记到下一次 buildDoc 头上」是同一机制），现在既不能断定这是真回归、也不能当没有。

- **不是 img 一家**：22 样例的 buildDoc **列合计** 1447 → 1544ms（+6.7%），paint 328→356ms、measure+position 42→49ms 同向微涨。本轮一行层叠代码都没动（`AuthorBoxSpacing` 那处 m1 反而快 17ms），所以这片普涨高度符合**同一个共同因**：上一轮渲染留下的垃圾在下一轮开头被回收，账单记进了当轮的第一段。也就是说——**§6 里 10ms 量级的分段差没有分辨力**，只有列合计与 e2e 中位数可读。
- **影响面**：只出现在 e2e 仍变快的小样例上（img 121.5→57.0、flex 154.3→69.2），不是用户可感知的退化，故先搁置。
- **要定性就得换工具，别再靠 Stopwatch 分段**：BenchmarkDotNet（`[GcServer]` / `[MemoryDiagnoser]`）或 `dotnet-counters monitor --counters System.Runtime`（看 `time-in-gc` 与 gen0 次数）。
- **判据**：关掉 `Renderer.UseFastPng` 跑同一 harness，若 img 的 buildDoc 回到 ~12ms，就是编码器残留的分配路径（`deflated[c]` 与最终 `MemoryStream.ToArray()` 还没池化）。第二嫌疑：`SKSurface.Create` 每轮 13–85MB 非托管分配是否间接触发托管堆回收。

### 6.7 编码器选型：22 样例全语料 `pgenc 5`（每档取中位数后按语料求和）

| 档位 | 编码合计 (ms) | 产物合计 (KB) | 备注 |
|---|---:|---:|---|
| Skia PNG `q=100` | 5,068 | **6,643** | 换编码器前的基线 |
| Skia PNG `q=50` | 5,019 | **6,643** | ← 三档字节**完全相同** |
| Skia PNG `q=10` | 5,015 | **6,643** | ← 时间差也在噪声内：quality 对 PNG 是空操作 |
| Skia JPEG `q=95` | 1,270 | 10,510 | 有损，且这里还更大（含大面积平色） |
| 自研 `Up` + Optimal + 16 块 | 263 | 5,891 | **19× 于 Skia，且体积小 11%** |
| 自研 `Up` + Optimal + 单线程 | 1,563 | 5,885 | 并行净收益 6.0×（分块只付 0.1% 体积） |
| 自研 `Up` + Fastest + 16 块 | 230 | 13,434 | 省 13% 时间，体积翻倍——不值 |
| 自研 `Up` + SmallestSize + 16 块 | 745 | 5,775 | 只再省 2% 体积，慢 2.8×——不值 |
| 自研 `None` + Optimal + 16 块 | **225** | 5,901 | 比在用的 `Up` 快 **15%**，体积打平（+0.2%） |

- 探针里的 `t16` 是 `Environment.ProcessorCount`，**生产默认是写死的 8 块**（§6.3），故本表绝对值低于 §6.1 的编码列；两处口径不要混引。
- **待办（收益已量化、暂不动）**：`None`+Optimal 在全语料上比当前默认的 `Up`+Optimal 快约 15%（225 vs 263ms），体积打平——m1 那条反常（§6.3）不是孤例，而是普遍现象：**这类以纯色/平涂为主的页面，Up 滤波产生的差分并不省字节，却多跑一趟逐像素减法**。但编码段现在只占 e2e 的 14%，改它约等于全链路 2%，**排在 M6 之后**（且会改动 golden 字节，需重录基准，成本远大于收益）。
- 单线程档（1,563ms vs 263ms）是这条杠杆的全部来源：**并行度就是收益本体**，滤波选型只是零头。

### 6.8 WASM（扩展导出）路径：自研编码器在这里**是负收益**，已显式关掉（2026-09-24 实测，推翻 §8.4 原来的猜测）

**同一台机器、同一份快照夹具**（`extension/test/out/snapshot.{html,css}`，932 标签 / 23.7 Mpx）、**与生产同参数**（`precomputed:true`、宽 1406）、Node 驱动 `renderRaw`，每档跑到热态：

| 产物 | PNG 编码走 | PNG s1 热态 | PNG s2 | 体积 | PDF（对照，**不碰** PNG 编码器） |
|---|---|---:|---:|---:|---:|
| 库里那份 9-23 的 wasm | Skia | 2,359ms | 6,095ms | 128 KB | 1,372ms |
| HEAD 重编（当前源码） | **PngEncoder** | 3,383 / 3,380ms | 8,830 / 8,744ms | 129 KB | 1,654 / 1,815ms |
| HEAD 重编 + `UseFastPng=false` | Skia | 2,533ms | 5,959ms | 128 KB | 1,629ms |
| **已落地：`Api` 静态构造显式关掉** | Skia | 2,716 / 2,890ms | 6,535 / 6,493ms | 128 KB | 1,973 / 1,958ms |

- **结论：WASM 上自研编码器比 Skia 慢 1.3–1.5×**（s1 +847ms / +33%，s2 +2,785ms / +47%）。复跑两次一致，不是噪声。
- **机制**：§6.7 那张表说明 .NET 侧的收益 **绝大部分来自并行**（同语料单线程档只有 3.2×）。WASM 是单线程，`Parallel.For` 退化为顺序，于是只剩「省掉 5 滤波搜索」这一项；而我们的替代实现是 **Mono 解释器里的逐字节 managed 滤波循环 + managed `DeflateStream`**，对面是 Skia 的 native C zlib——在解释器里这笔账是负的。
- **正确性一侧干净**：两路 PNG 解码后 **94,826,264 字节 0 差异**（PIL 逐像素）；块结构从 Skia 的 `sBIT` + 17×8192B `IDAT` 变成单块 132,526B `IDAT`——证明链路确实换了、像素没变。
- **顺带发现（未定性）**：与 PNG 完全无关的 PDF 臂也从 1,372ms 涨到 1,629–1,815ms（+250~440ms，含噪声）。9-23→9-24 之间布局/绘制改了 1,900+ 行（`LayoutBuilder` +606、`StyleResolver` +342、`AuthorBoxSpacing` 新增），快照这条 precomputed 路径上的这笔漂移值得单独量一次。
- **动作（✅ 已落地，2026-09-24）**：`src/PaperSharp.Wasm/Api.cs` 加静态构造显式 `Renderer.UseFastPng = false;`（ native 宿主保持默认开），`node extension/build.mjs --publish` 重编扩展产物。落地后复量两遍：**产物 PNG 与库里 9-23 那份 wasm 的输出 SHA-1 一字不差**（`0e2e935d…`，128 KB）——不只是像素 0 差异，整份文件字节回归到改动前，这条快路在 WASM 侧的影响面被完全消除。耗时回到 2.7–2.9s / 6.5s 档；该行比 `UseFastPng=false` 那行仍高 ~10%，同一轮里不碰编码器的 PDF 臂也从 1,629 涨到 1,973ms，属同机不同时刻的漂移，不是编码路径。真机侧另跑了一遍 `node extension/test/e2e-browser.mjs --diag`（无头 Edge 装载扩展、六种格式 + 休眠恢复回归）**全部 ok、0 失败**，证明静态构造里的回退在浏览器运行时里真的生效了。
- **同一份程序集还喂着 npm 包**：`js/papersharp/wasm` 是 9-23 那次拷贝（`PaperSharp.Wasm.wasm` 时间戳停在 09-23），里面**根本没有**自研编码器，所以那条路径本轮没被影响；它下次重编会走同一个 `Api` 静态构造，因此也自动继承这个回退。
- **复现坑一条（已修）**：`extension/test/bench-snapshot.mjs` 原来**没传 `precomputed`**，所以它量的从来不是扩展真正走的那条路——同一份夹具在它下面跑出来是 **28.9s**（作者 CSS 全量层叠，正是 §5.1 的 O(n^2.5)）。现在它默认按生产参数跑（`--author-css` 才切回旧口径），两种口径各跑一遍同一份夹具（生产 / `--author-css`）：整页 **3,965 / 31,404ms（8×）**、scale=2 6,154 / 33,729ms、PDF 1,649 / 29,412ms；`--author-css` 下"前 200 节点"只要 4,068ms——元素数一降层叠立刻塌，正是 §5.1 那条超线性的现场演示。**改这个 harness 或新写驱动时，参数必须与 `extension/offscreen.js:90-102` 对齐。**

## 7. 内存（单页渲染，托管堆增量 + 进程工作集；Release 净跑）

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

> **⚠️ 本节数字已过时（2026-10-27）**：上表那 310–326MB 里约 80MB 是 `TtfSubsetter` 把整张字体表读进托管堆
> 的峰值、另有 30+MB 是同一张物理面被重复持有。改造后同口径**峰值工作集 141MB / 私有字节 89MB**，
> 机制、分阶段归因与保真门见 **§15**。引用内存请以 §15 为准。

## 8. 优化建议（按性价比）

1. **✅ 已修：表格 O(行²) → O(行)。**（§5.3）
2. **✅ 已修（2026-09-24）：PNG 编码换自研并行编码器。**（§6.1/§6.3）编码段 14×、Playground 全语料端到端 2.74×。
3. **✅ 已修（2026-09-24）：作者 CSS 路径的层叠超线性 → 物化样式表集合。**（机制、探针与门见 §5.5）净层叠同进程对拍 **19.2×–25.0×**，引擎真实入口 table 2000 行 74.4s → **5.29s**，规模档回到近线性；改动 ~40 行且**层叠语义一行未替换**（仍由 AngleSharp 做特异度/继承/`!important`/`var()`）。原 M6 的**步骤①③④⑤（自研 tokenizer、特异度分桶、属性元数据表、值解析接入）因此不再是消掉超线性的前提**，六步方案的重排见 ROADMAP M6。
   **剩余的两刀（都已在探针里量化，按性价比排序）**：
   - ~~(a) 按元素缓存层叠结果~~ **✅ 已落地（09-24 同日第二刀，见 §5.6）**：递推式 `R(e)=ComputeCascadedStyle(e, parent: R(最近祖先))` + 每元素/每伪元素缓存，pgstyle H 行实测比 F 行低 **7.6–13.5×**（table 500 档 104.2 → 13.8 µs/元素，已到 G 行下界附近）。
   - ~~(b) 让每个元素只做一轮层叠~~ **✅ 已随 (a) 一并落地（见 §5.6）**：raw 声明串直接吃 computed 轮的缓存条目（缓存的是**折算前**的声明，折算现做），双轮并一轮。与 §6.4 被否决的「启发式跳过第二轮」无关——那条是没缓存时每次都必问一次，这条是问过的必然命中。
   - 两刀合起来引擎真实入口 `pgstage 9` 全语料 buildDoc **1,701 → 964ms（1.76×）**、e2e **2,893 → 2,001ms（1.45×）**。
   - ~~彻底移除 `AngleSharp.Css` 依赖（原 M6 步骤①③④⑤⑥）~~ **性能理由已不存在**（(a)(b) 之后层叠固定成本只剩值折算 + 属性读 + 建树），只剩**依赖收敛**诉求，不必为性能启动。
   - ~~（新，§5.7 取证后的下一档）属性记忆化视图~~ **✅ 已落地（09-24 夜，见 §5.8）**：buildDoc 里 AngleSharp 那 31% 的**重复读**已用每元素 2 个惰性 `PropCache`（`Get`/`Spec` 各一，Dictionary 按需填）削掉，同树 A/B 实测 buildDoc **−8~−11%**。当初设想的「一趟枚举建全量字典」不需要——真正贵的是重复首读而非唯一首读。
   - ~~自研值解析 span 化（§5.7 的 15% 块）~~ **✅ 已落地（09-25 凌晨，见 §5.9）**：字节全部门控通过，但真实收益是 **CSS 重页 buildDoc −7%、全语料 ~−1%**——那块 161ms 里约七成是 AngleSharp 自己的串分配，PropCache 命中后自研解析只吃首读。账面百分比要按「缓存命中率之后」折减再看。
   剩余同量级杠杆回到：display 分型跳读（回归面大）、正则预处理/DomPath 门控（残差 32% 未再拆）。
4. **✅ 已修（2026-09-24）：WASM 导出显式关掉自研 PNG 快路**（数据与机制见 §6.8）：同一份快照夹具、生产同参数下 3.38s（开）vs 2.53s（`UseFastPng=false`），慢 **1.3–1.5×**——那条快路的收益主要来自并行，WASM 单线程 + Mono 解释器里逐字节 managed 滤波反而贵过 Skia 的 native zlib。落地为 `Api` 静态构造里的 `Renderer.UseFastPng = false`，Playground / 服务端这些 native 宿主保持默认开；落地后扩展产物与 9-23 那份 **SHA-1 一字不差**。
5. **降 PDF 编码成本**：`paintEncode` 占小文档 PDF 的 85–95%（§4），本轮只解决了 **PNG** 侧，PDF 侧原样未动。可选：(a) SkiaSharp 3.x 的 PDF 字体子集化；(b) 批量导出复用同一 `SKDocument` 多页；(c) 常驻服务用 PDF 侧字体复用方案。
6. **Playground 真实链路尚未量过**：§6 全部数字来自控制台 harness，SignalR 往返、`/api/playground/image/{token}` 取图、iframe `srcdoc` 装载 + 浏览器量测回传都没测过一次（页脚把「引擎 ms / 浏览器量测 ms」分开显示就是为这一步刚加的）。拿到真实值后才能判断引擎渲染与浏览器量测要不要并行（现 `RenderOnceAsync` 里两者串行）。
7. **GC / 计时口径**：常驻服务可开 `ServerGarbageCollection` + `ConcurrentGarbageCollection` 削 gen2 长尾；更重要的是 §6.6 那个未定性疑点——它意味着**现在的分段数字在 10ms 量级上不可信**，要往下抠 buildDoc 得先换 BenchmarkDotNet 或 `dotnet-counters`。
8. **吞吐**：invoice 4.9 rps（单线程 Workstation GC，09-22 数据；本轮编码器落地后 PNG 侧吞吐上限同步抬高）。渲染管线除 buildDoc 外无共享可变状态的机会较多，多进程/并行分片即可近线性扩容；若先做 M6，单线程上限也会大幅抬高。
9. **PNG 超大文档**：单张长图对 800+ 行会分配几十 MB 位图。编码段本轮已降到与 paint 同量级（pricing-page 27.9 Mpx 编码 48ms），但画布本身仍随像素数线性膨胀——生产里长文档走 PDF 分页。

## 9. 已知测试局限

- 单台机器、单进程、Workstation GC；未做跨平台（mac/Linux）对比与 `dotnet-trace` 级 profiler。
- §5 scale 为合成文档（样式表小、结构规整），真实网页若带**大样式表 + 复杂选择器**，buildDoc 只会更糟（AngleSharp 成本同时随规则数×元素数增长），即 §5.1 是下界而非上界。**⚠️ 09-24 修正「随规则数增长」这半句**：`pgstyle` 的 D 行在同一条老路上把规则从 7 条灌到 207 条（元素数不变），耗时只 **×1.1** → 驱动是 **DOM 被重走的次数**，不是规则数；即大样式表并不会按比例放大 §5.1。该 D 行测于**改前**的懒集合路径，物化之后规则数轴是否变成主导尚未复测（物化后每元素一轮的全表匹配是 O(规则数)，理论上这条会更接近线性于规则数——要动 §8.3(a) 时一并复测）。
- §2 冷启动仍是旧 Debug 数据，Release 下未重跑。
- **§3 的 PNG 列与 §0.6 的吞吐（invoice 4.9 页/秒）同样是换 PNG 编码器之前（09-22）的 `bench 50` 数据，本轮未用新编码器重跑**；要引用「PNG 端到端」请以 §6 的 Playground 口径为准。§4 的 `paintEncode` 是 PDF 段（字体子集 + PDF backend），不走这条 PNG 编码器，故不受影响。
- 测试期已知库非线程安全（`FontManager`/`ImageLoader` 静态缓存），并发吞吐数字需先加锁/每进程隔离后才可信。**本轮并实证了一次后果**：与 `pgenc` 探针同跑时 `PseudoElementTests.No_pseudo_rules_leaves_output_unchanged` 失败（PdfPig 从产出 PDF 里抽出 `"\0\0\0\0\0"` 而非 `"plain"`），单独跑与随后两次净跑均 220/220 全绿——即满载干扰不只放大绝对值，还能让文本抽取出现**错结果**。基准与测试**不要同跑**。
- **§6 的分段列取 9 次最小值、`e2e` 列取中位数**，两个统计量不同源，段和必然小于 e2e（CSV 的 `collect` 列出现负值即此）。10ms 量级以下的分段比较在该口径下没有分辨力，见 §6.6。
- **§6.1「前」那一列来自当时位于 `D:/Github/ps-base` 的 HEAD（`f8e94d2`）独立检出**。两点要记：① 那棵树里的 harness 是**本轮改过的副本**（`Benchmark.cs` / `Program.cs` / `CascadeProbe.cs` / `GoldenPixelCheck.cs`，`EncodeProbe.cs` 被改成空壳）——两树同口径靠的是同一份 harness 源码，库代码未动，但**不能把它当干净 HEAD 引用**；② 09-24 复核时该目录已不存在（仓库现已在 `G:/Github/papersharp`，`git worktree list` 只剩主树），对拍环境是一次性的。**要重做对拍**：`git worktree add ../ps-base f8e94d2`，再把当前 `src/PaperSharpSamples/` 的探针文件同步过去（否则两边量的不是同一件事）。

## 10. 复现方法

```bat
cd src/PaperSharpSamples
dotnet build -c Release
dotnet run -c Release -- cold invoice   :: 冷启动首帧（单样例单进程）
dotnet run -c Release -- bench 50       :: 热态延迟 + 阶段拆解 + 内存 + 规模 + 吞吐（⚠️ scale 含 2000 行档，全程 ~15 分钟，建议后台跑）
dotnet run -c Release -- scale          :: 仅布局复杂度扫描
```

**Playground 口径与取证探针**（都是取证入口，不进生产代码，建议长期保留）：

| 命令 | 量什么 |
|---|---|
| `dotnet run -c Release -- pgstage [iters] [样例…]` | Playground 同参数（宽 1284 / `scale:2` / `domPath:true`）下逐阶段拆解 + 整条 e2e，输出 `PG,…` CSV 行（§6 的全部数字即来自此，默认 7 次） |
| `dotnet run -c Release -- pgenc [iters] [样例…]` | **只**比编码段：Skia PNG q=100/50/10、Skia JPEG、自研 `PngEncoder` 的（滤波 × deflate 档位 × 并行度）组合，每档都解回来与 Skia 解码结果**逐字节**比对（只看体积会骗人） |
| `dotnet run -c Release -- pgbuild [iters] [样例…]` | buildDoc **内部**拆解（净层叠循环，不带画布与编码），用来区分「层叠真变慢」与「跨轮 GC 记到层叠头上」（§6.6 疑点即靠它翻的案）。09-24 起按 §5.6 缓存语义逐段计：`cascade-rawfill`（匹配）/ `fold-delta`（折算）/ `pseudo-upper`（伪元素上界）/ `props-94x-once`（94 属性读）/ `split-angle` / `split-ours`（§5.7 表即此）|
| `dotnet run -c Release -- pgstyle [行数…]` | **AngleSharp 层叠超线性的来源探针 + 改动前后逐属性对账**，输出 `STY` 行（§5.5/§5.6 的表即此，默认 `250 500 1000 2000`）。A=现状 / B=外提 `GetStyleCollection` / C=只枚举 / D=灌 207 条规则 / F=物化后 / G=物化且不算祖先 / **H=按元素缓存（§5.6；量的是缓存命中稳态，引擎总账看 pgstage）** / E=对账（必须 0）/ **H-check=全 22 真实样例 × {宿主,::before,::after} × {computed,raw} × 94 属性对账（必须 0；双侧同抛跳过、单侧抛计不一致）**。⚠️ A/B 两行量的仍是**改前的懒集合语义**，2000 行档一次就要 20+ s → 全程数分钟，大档建议显式 `pgstyle 1000 2000` 并后台跑 |
| `dotnet run -c Release -- scale [行数…]` | 布局/层叠规模扫描（引擎真实入口 `BuildDocument` + `ToBox` + `Measure` + `Position`），默认 `100 200 400 800`；**要复现 §5.1/§5.5 的 2000 行档就显式给 `scale 2000`**（改前那一档 5 次迭代要十几分钟，改后约 30 秒） |
| `dotnet run -c Release -- pgalloc [iters] [样例…]` | 画布生命周期四拆：`create` / 复用-clear / 新建-clear / reuse-e2e vs fresh-e2e —— 画布池值不值得做只看这张表（§6.5 第三行） |
| `dotnet run -c Release -- goldcheck` | 一次性迁移取证：产物与 `tests/PaperSharp.Tests/goldens` 逐像素对账（换编码器前后的门） |
| `bash tools/selfdiff.sh [样例…]` | 两棵树跑 `boxes <样例>` 再 `cmp` JSON——**改布局/层叠后的硬门**（要求引擎盒矩形逐字节相同）。⚠️ 两处 `run_in` 的目标路径写死在已经不存在的 `D:/Github/ps-base` 与 `D:/Github/paper-sharp` 上（§9 最后一条），重跑前要改回当前仓库根并重新 `git worktree add` 基线树 |

**WASM / 扩展导出这条（§6.8 的数字即来自此）**：

```bash
node extension/build.mjs --publish     # dotnet publish src/PaperSharp.Wasm -c Release + 拷产物进 extension/
node extension/test/bench-snapshot.mjs              # 生产同参数（precomputed），夹具 extension/test/out/snapshot.{html,css}
node extension/test/bench-snapshot.mjs --author-css # 作者手写 CSS 那条 AngleSharp 全量层叠路（慢 8×，§5.1 的现场）
```

跑之前记得 `extension/papersharp/wasm` 是与源码同步的构建输入（已 gitignore），换分支后必须重跑 `--publish`，否则量的是旧产物。

```bash
# 回归门（本轮 09-24 净跑 220/220 全绿，无需 UPDATE_GOLDENS）
dotnet test tests/PaperSharp.Tests/PaperSharp.Tests.csproj -c Release
# 与浏览器对拍（精度成果别丢：聚合漂移面积 22254、全绿盒 557/2021）
bash tools/sweep.sh && node tools/compare.mjs <before.txt> <after.txt>
```

基准代码：`src/PaperSharpSamples/Benchmark.cs`（`Playground` / `CanvasLifecycle`）+ `EncodeProbe.cs` + `CascadeProbe.cs` + `GoldenPixelCheck.cs`；入口分发在 `Program.cs`。输出为机器可读 CSV 行（`LAT` / `STAGE` / `MEM` / `SCALE` / `THROUGHPUT` / `PG` / `ENC` / `ALC` / `BLD`）。最新一次净跑输出：`src/PaperSharpSamples/bench_20260922.txt`（§3–§7）与 `src/PaperSharpSamples/pgstage_20260924_head.csv` / `..._work.csv`（§6）。
**跑基准前别同时跑测试或另一个探针**（§9 第 5 条有实例），跑回归门之前先 `git status` 确认没有别人的并发改动。

---

## 11. PDF 字体子集化：速度 + 体积同一个根因，一次拿下（2026-09-26 本轮）

### 11.1 根因（实测，不是推断）

**Skia 的 PDF 后端对字体零子集化**：整份字体文件原样嵌进 PDF，再 deflate 一遍。2.88.9 与 3.119.2 行为一致，
`SKDocumentPdfMetadata` 也没暴露任何相关开关——升级与调参两条路都堵死，只能自己裁。

代价按字体字节数线性放大，两个探针实测：

| 输入 | 嵌的字体 | PDF 体积 | paintEncode |
|---|---|---:|---:|
| 43 个拉丁字符 | Segoe UI（系统，未子集化） | 530 KB | — |
| 同上 | msyh.ttc（12 MB 全量） | 12 MB | 952 ms |
| 常规单页样例 | 打包的 GB2312 子集字体 ×2 face（各 1.8–2.5 MB） | ≥2.4 MB | 170–290 ms（占端到端 **85–95%**） |

也就是说 §4 那句「PDF 的端到端耗时几乎全在编码」的真正内容是：**在编码一份每次都用不到的字体**。
体积同理——纯文字发票 2.4 MB、report 5.4 MB、含 emoji 的 m7 18.7 MB、UI 还原页 login-page 23 MB。

### 11.2 落地：保 GID 的 glyf/loca 裁剪器

`src/PaperSharp/Pdf/TtfSubsetter.cs`。只重建 `glyf` + `loca`（必要时 short/long 格式互转）与 `head`
（`indexToLocFormat`、`checkSumAdjustment`），**其余表逐字节原样复制**，且**字形 ID 不重编号**——
所以 `cmap` / `hhea` / `GPOS` / `GSUB` / 度量 / shaping 全部零漂移，HarfBuzz 那条路不需要任何配套改动。
（在此之上再补两刀与绘制无关的字节：§11.5 的死表清单、`hmtx`/`vmtx` 尾部收口——后者会改 `hhea`，
但保留 GID 的度量逐字节不变，由 `TtfSubsetterTests` 的「同 face 逐字面宽度 + 栅格全等」把门。）
复合字形做部件闭包（`ARGS_WORDS` / `WEIGHT_1X` 等标志逐字节解析，坏数据直接放弃裁剪）。
无 `glyf`（CFF）或读表异常 → 返回 null，调用方保持整嵌，**绝不出坏字体**。

### 11.3 落地：双趟渲染 + 校验回退

`src/PaperSharp/Renderer.cs::RenderPdf` + `src/PaperSharp/Pdf/PdfGlyphUsage.cs`：

1. **录制趟**：同一套 `PagePainter.PaintPage` / `MarginBoxRenderer.Render` 往一张 scratch 位图画布空跑，
   经 `PdfGlyphUsage.DrawText` / `DrawShaped` 记下每张 face 真正画过哪些字形（参数与真绘趟逐字相同，
   含页码/总页数，否则录出来的集合会偏）。
2. `BuildSubsets()`：按用字集合裁 face，进程级缓存（键 = 原 face 引用 + 排序后的 GID 数组，上限 48）。
3. **真绘趟**：绘制点把 face 换成子集，Skia 只嵌这点字节；同时继续逐字录制，
   见到第 1 趟没记到的字形即置 `Violated` → 扔掉子集、原字体整趟重绘。

选「拿同一套绘制代码空跑」而不是「手工遍历盒子树收集文字」，是因为后者一旦漏掉某处绘制点就是
**静默空白字形**；前者的失败模式是 `Violated` 变红退回整嵌——只慢，不错。PNG/SVG 路径不设 `Current`，
所有钩子直接透传，绘制行为零改动。

### 11.4 两个静默坑（都只改像素、不抛异常）

1. **彩色字体不能裁。** `seguiemj.ttf` 裁出的字节里 `COLR`/`CPAL` 明明还在，但经 `SKTypeface.FromData`
   重建后不再被识别为彩色字体：Skia 光栅**什么都不画**，PDF 读回来退化成单色线稿。m7 的 emoji
   从彩色变黑白描边、栅格 diff 0.29% 才暴露。现在 `IsColorFont` 命中 `COLR`/`CBDT`/`sbix`/`SVG ` 即跳过裁剪。
2. **记账本 `cur` 必须 `[ThreadStatic]`。** 写成普通 static 时，xUnit 并行跑 `PdfRasterTests` 会把录制态
   漏进 PNG 路径（钩子只判 `cur == null`），一次改动炸出 35 个失败、其中一半是 `PngGoldenTests`——
   看着像"我改了绘制"，其实是线程污染。

### 11.5 效果（Release `bench 30`，同一台机器；改前 `bench_pdf_baseline.txt` → 改后 `src/PaperSharpSamples/bench_pdf_20260926_final.txt`）

| 样例 | PDF 中位 ms | PDF 体积 KB |
|---|---:|---:|
| invoice | 228.6 → **31.0**（7.4×） | 2464 → **94**（26×） |
| long | 199.1 → **52.0**（3.8×） | 2465 → **112**（22×） |
| flex | 209.9 → **33.9**（6.2×） | 2465 → **101**（24×） |
| img | 224.8 → **27.5**（8.2×） | 2474 → **100**（25×） |
| report | 466.0 → **64.8**（7.2×） | 5447 → **188**（29×） |
| pct | 300.4 → **38.7**（7.8×） | 3946 → **151**（26×） |
| polish | 282.6 → **38.6**（7.3×） | 3706 → **131**（28×） |
| inline | 220.0 → **34.3**（6.4×） | 2992 → **136**（22×） |
| table | 243.9 → **43.6**（5.6×） | 2988 → **118**（25×） |
| **单线程吞吐** | **5.1 → 42.4 页/秒**（8.3×） | 托管堆增量 23.5 → 5.5 MB |

体积上字体之后还有第二刀，都在 `TtfSubsetter` 里：

- **扔掉与绘制无关的死表**：`PRKT`（微软私有，雅黑里 176 KB）、`hdmx`（设备像素度量表，雅黑里 **501 KB**）、
  `LTSH`、`MERG`、`meta`、`DSIG`。这些在「保 GID 原样拷贝」的初版里是逐字节跟着走的，
  纯文字 PDF 剪完字形后剩下的**就是它们**（report 改前 267 KB → 改后 122 KB）。**不碰** `VDMX`（部分渲染器据此取 CJK 设备行高）。
- **hmtx/vmtx 收口到最后一个保留字形**：这两张表是「每 GID 一条」的定长数组，不跟着收的话，
  114 个用字的发票要背 6763 条度量。折叠语义用表原生的「`numberOfHMetrics` 之后共用最后一条」表达，
  中间空洞不动（只收尾部）。login-page 的 `hmtx` 117 KB / `cmap` 101 KB 即此。

样例之外的极端口径（`html <名>` 产物）：m7 18.7 → **6.8 MB**（emoji 那张 COLR 字体仍整嵌，见 §11.6）、
login-page 23.2 → **0.29 MB**、pricing-page 23.2 → **0.43 MB**、fontvs 77.8 → **6.8 MB**。

**门**：全量测试 274 例 272 绿（含 `PdfRasterTests` 17/17 逐像素、`PngGoldenTests` 12/12、`ConcurrencyTests` 全绿），
基准 PNG **无需重录**。新增 `tests/PaperSharp.Tests/TtfSubsetterTests.cs` 7 例：5 张打包字体逐字面
「cmap→GID 相同 + 宽度相同 + 48×48 栅格逐字节相同」、裁剪体积、以及端到端 invoice < 1.4 MB。
（仅剩 `BoxGoldenTests` 的 fontvs / framer-motion 两例红，属并发的 flex 尺寸改动，与字体链路无关。）

### 11.6 改完之后的新瓶颈

- `paintEncode` 冷跑（无字形缓存）仍在 160–300 ms 量级；热态端到端已降到 31–65 ms，
  与 PNG 出口（15–35 ms）拉近到 2× 以内。剩余是 PDF 后端本身 + 位图嵌入。
- **彩色字体（emoji）是唯一没被裁动的大块**：`seguiemj.ttf` 的 `COLR` 表 7.2 MB（v1 格式，
  coverage 覆盖全部 64883 个字形），m7 那 6.8 MB 里 6.5 MB 是它。要收需要解析 COLR v1 的
  LayerList / BaseGlyphList 做图层闭包——收益只覆盖「含 emoji 的页」，本轮按性价比未做。
  实测确认：`FromData` 灌**原样字节**彩色照常（3973 彩色像素），是裁剪让图层 GID 掉出闭包才变黑白，
  所以这条是「可解」而不是「无解」。
- **位图**（`RasterDpi=72` / `EncodingQuality=90` 直接进 PDF）是 UI 还原类页面的下一个体积杠杆。
- 剩余的字体字节大头是 `cmap`（login-page 101 KB）与 `GPOS`/`GSUB`，同样需要真正的表重建才能收，
  deflate 后收益 ~10–30 KB / 页，本轮未做。
- 编码压下来之后，`buildDoc`（层叠）在重页上重新成为第一瓶颈（§0.3 的两区间口径再次成立）。
12. **✅ 已落地（2026-09-26 本轮，§11）：PDF 字体子集化——速度与体积是同一个根因。** Skia 的 PDF 后端**完全不子集化字体**（2.88.9 / 3.119.2 实测一致，也无参数可调），每次渲染整嵌 1.8–2.5 MB 的 CJK 子集字体再 deflate，这就是 §4 里 paintEncode 85–95% 的全部内容。自研「保 GID 的 glyf/loca 裁剪器」（其余表逐字节原样复制 → cmap/hmtx/GPOS/GSUB 与 shaping 零漂移）+ 双趟渲染（同一套 PagePainter 先往 scratch 画布空跑录字形，真绘趟换子集 face 并逐字校验，见未录到的字形即整趟作废退回原字体重绘）：invoice **7.4× 提速 / 26× 减体积**、report 7.2×/29×、吞吐 5.1 → 42.4 页/秒、托管堆增量 23.5 → 5.5 MB；UI 还原页 login-page 23.2 → 0.29 MB。门：全量 272/274 绿、**PNG 与 PDF 栅格基准一张都不用重录**。两条只改像素不抛异常的坑（彩色字体裁不得、记账本必须 `[ThreadStatic]`）见 §11.4。

---

## 12. 跨引擎赛道定位后的瓶颈重排（2026-10-03，`benchmarks/PdfBench/scale/`）

背景：规模扫描把 PaperSharp 放进「吃 HTML 的引擎」这条赛道量了一遍（6 家 × 五轴 × warm/cold，
结论在 `benchmarks/PdfBench/scale/REPORT.md`）。A 轴拟合出 **固定成本 174 ms + 边际 51.6 ms/页**，
而 Chromium 是 1586~1876 ms + 11.8~16 ms/页 —— **固定成本我们最低、每页成本我们最高**，
35~48 页被反超。要拿第一，要动的是边际成本，不是启动成本。

### 12.1 先记两条负结果（别再试第二遍）

- **「属性读占 buildDoc 72%」是假的。** `CascadeProbe` 的 `props-94x-once`（A30 ≈ 406 ms）是探针
  自己直调 AngleSharp CSSOM 的**模拟循环**，且没复用引擎的按元素缓存，不能当引擎开销读。
  照它做了一刀「PropCache 一次枚举建表 + 简写名白名单回落」，同树 A/B 实测 **927→919 ms，零收益**——
  因为**这一刀在途工作早就做完了**（`StyleResolver.PropCache.BuildMap` + `ShorthandOnAsk`）。
  同一棵树的对照：HEAD 的 buildDoc 927 ms → 在途树 566 ms（**−39%**），差值就是那把建表缓存。
- **「每页重复嵌子集字体」不成立。** A30 产物 1070 KB ÷ 30 页 ≈ 35 KB/页 看着像每页嵌一次，
  数 PDF 里的 `/FontFile2` 只有 **3 份**（全文档共享）。体积那本账在 §11，不在这里。

### 12.2 消融法定位的真瓶颈：文本

语料 = `scale/corpus/A30.html`（30 页 / 3846 元素 / 39258 可见字符 / 60 张内联图），
工具 = `PdfBench.Runner stageprobe <html>`（复刻 `Renderer.RenderPdf` 主链路逐项计时），
变体 = `scale/ablate.py`（每份只动一个变量，标签数恒定）。单位 ms，5 次取中位：

| 阶段 | full | notext | noimg | nofx | 文本成本 |
|---|---:|---:|---:|---:|---:|
| buildDoc（层叠+建树） | 566 | 574 | 585 | 559 | **0**（与文本无关） |
| measure | 225 | 49 | 226 | 240 | **176（78%）** |
| paginate | 219 | 43 | 213 | 217 | **176（80%）** |
| recordpass（录制趟，光栅） | 192 | 86 | 203 | 183 | **106（55%）** |
| realencode（真绘 PDF） | 353 | 184 | 331 | 280 | **169（48%）** |
| 产物 KB | 1051 | 728 | 1035 | 801 | |

**文本相关合计 ≈ 627 ms = 全链路 39%**；图片几乎不花钱（noimg ≈ full）；阴影/渐变只在真绘里值 73 ms。
根因是结构性的：**整形只发生在绘制期**（`TextPainter` → `TextShaper.DrawShaped/WidthOf`），
布局期只量宽不记 glyph，于是同一段文字至少被 HarfBuzz 跑三遍——
measure 一遍、录制趟一遍、真绘趟一遍。

### 12.3 按收益排序的三刀（含天花板与判据）

| # | 刀 | 目标段 | 预期 | 判据 |
|---|---|---|---|---|
| ① | **整形/度量结果留在布局树上**，paginate/录制趟/真绘趟复用，不再重算 | measure+record+encode 的文本部分 | **−300~350 ms @A30 ≈ −11 ms/页** | 全量测试 + `tools/pixelsweep.sh` 逐像素门 + PDF 字节对账（去掉时间戳）必须不变 |
| ② | **干掉录制趟**：字形集合改由布局树的 shaped run 直接汇总，不再往 scratch 画整页 | recordpass 192 ms | **−190 ms ≈ −6 ms/页** | 现方案的存在理由是「杜绝收集逻辑漏枚举某处文字」（`PdfGlyphUsage` 类注释），所以必须留 `Violated` 兜底 + 全语料「录制集合 == 真绘集合」对账门 |
| ③ | **自研层叠替掉 AngleSharp.Css**（ROADMAP M6，此前因「性能理由用尽」降级） | buildDoc 566 ms | 上限约 **−300 ms ≈ −10 ms/页** | M6 原有六步计划；`pgstyle` 的逐属性对账门（H-check 不一致必须为 0） |

三刀全中：51.6 → **约 25 ms/页**，交叉点从 35 页推到 110 页以上，A100 ≈ 2.7 s 与 playwright 打平。
**要在全尺寸上稳赢还差第四刀**（真绘趟 11.8 ms/页 是 Skia PDF 后端，Chromium 也含同类成本），
所以「排名第一」的诚实说法是：**①+② 落地即可在 ≤110 页全区间第一，覆盖票据/报表主场景；
>110 页的长文档仍需第四刀或承认 Chromium 的边际优势。**

### 12.4 前置条件（不修则以上数字全部失真）

`scale/REPORT.md` §7 量到两种跨页失效：**深嵌套块链 depth≥30 时页数塌成 1 页**（内容全塞一页、
纵向被裁，字符仍 93~105%），**长文本流（D 轴）只有参照 25~41% 的字符进了 PDF**。
A 轴（显式 `break-before` 分页）与 B 轴（表格跨页）完整性全绿，所以上面的边际成本数字可信；
但 D/C 两轴的延迟是「只画了三成内容」量出来的，**不能用来主张性能**。修分页是性能工作的前置。

### 12.5 刀①+刀② 已落在分支（`perf/shaped-run-cache`，HEAD 起 4 个 commit）

**先撤回两个我自己产出的错数**（都记下来，免得下次再信）：

- §12.1 说的「属性读占 buildDoc 72%」来自 `CascadeProbe` 的 `props-94x-once`，那是探针自己
  直调 AngleSharp CSSOM 的**模拟循环**，不是引擎开销。据此做的第一刀零收益。
- 「刀① 让 measure −70% / paginate −67% / 合计 −30%」也是假的：worktree 版探针把
  `StyleResolver.BuildDocument` 的视口宽用了默认 1024，而真实路径传 `spec.ContentW`——
  两条路径量的**不是同一份布局**（页数都不一样），段值自然对不上端到端。
  段归因只有在与 `RenderPdf` 逐参对齐时才可用。

真正落地的两刀（同一宿主、同一协议下证明**输出逐字节不变**）：

| 刀 | 内容 | 等价性证据 |
|---|---|---|
| ① | 度量结果 + `SKShaper` 挂进 `RenderScope` 做请求级缓存 | 与 HEAD 对跑 25/25 样例字节一致 |
| ② | 录制趟只走文本：scratch 画布 1×1，跳过装饰/图像/SVG/图层/裁剪变换 | 同上；且门抓出一个真 bug（见下） |

刀② 的对账门当场抓出我自己的错误：判据写成 `Recording` 就跳装饰，而 **真绘趟里 `Recording`
也一直是 true**（要继续逐字校验漏记）——那样会把真绘的背景边框一起跳掉，产物页面 `/Contents`
比参照短 167 字节。正确判据是 `Recording && !SubsetsBuilt`。这个错是测试抓的，不是推出来的。

**性能效果：本机当前测不出，本提交不主张任何加速数字。** 测量期间系统负载 31%、
前台有游戏客户端在跑，同臂两轮中位自散 4~30%；独立进程 + 每臂 20 次 + 两轮换序得到
m1 −1.5%、abl_full 与 report 落在噪声内。按 §12.2 的消融，可省上限是录制趟的非文本部分
（约全文 8%），要等安静机器上用 `scale/sweep.py` 复量再定。→ **2026-10-06 已复量兑现：
A 轴边际成本 49.7 → 36.3 ms/页（−27%）、双臂产物逐字节相同，见 §12.9。**

全量测试：50 红 / 349 绿（新增 17 条全过），红名单与 HEAD 基线**逐条相同**。

### 12.6 已确认并已修（A 任务，分支 `fix/bundled-fonts`）：打包字体目录锚在入口程序集上，消费方静默走系统字体

现象（写黄金哈希门时撞出来的，与 §12.5 的两刀无关，HEAD 一样）：同一份 `invoice.html`
在控制台宿主出 278,219 B，在 `dotnet test` 宿主出 96,383 B，差 2.9 倍。

**根因一句话**：`FontManager.FontDir = Path.Combine(AppContext.BaseDirectory, "assets", "fonts")`
—— 目录按**入口程序集**旁边找；`PaperSharp.csproj` 只 Pack 了 README/LICENSE，**字体根本没进包**。
所以对任何 NuGet 消费方，这个目录永远不存在，而 `"Microsoft YaHei"` / `"微软雅黑"` / `YaheiSub` /
`system-ui` / `sans-serif` / monospace 全都命中 `YaheiSubsetFamilies`/`NotoSubsetFamilies` 的
「优先用打包子集」分支 —— 文件不在就**静默退回系统同名字体**，没有警告、没有异常、产物照样合法。

代码注释写着这条设计的目的是「PDF 体积 + 跨平台一致」；实测这两项目前对消费方都不成立。

可证伪验证（已做）：只把 `assets/fonts/*.ttf` 复制进 probe 自己的 `AppContext.BaseDirectory`，
不改一行代码，产物即从 278,219 B 变 96,365 B（与 testhost 的 96,383 B 同量级）。

五份样例的实测影响面（页数全部一致，体积不是）：

| 样例 | 无字体（= 消费方实际所得） | 有字体（= 仓库测试配置） | 倍数 |
|---|---:|---:|---:|
| m1 | 4,002,249 B / 10 页 | 315,539 B / 10 页 | **12.7×** |
| table | 401,668 B / 1 页 | 121,830 B / 1 页 | 3.3× |
| report | 561,137 B / 2 页 | 193,128 B / 2 页 | 2.9× |
| invoice | 278,219 B / 1 页 | 96,365 B / 1 页 | 2.9× |
| polish | 313,457 B / 2 页 | 134,165 B / 2 页 | 2.3× |

**最要紧的不是体积，是测试覆盖错位**：像素/结构金标全部是在「字体在场」的 testhost 里录的，
也就是说 CI 只跑过消费方永远不会走的那条分支；消费方走的分支**一条测试都没有**。
页数在这 5 份样例上没变，但字形度量是否逐位一致我没验（要判定得在无字体宿主下重跑像素门）。

修法三选（**都需要拍板，本会话没动产品代码**）：

1. **把字体装进包**（改 `PaperSharp.csproj` 的 Pack/GeneratePackageOnBuild，或嵌成 managed resource
   彻底去掉路径依赖）—— 正面解决，代价是包体积 +数 MB，且所有消费方产物会变（变小、字面可能变）。
2. **多基准解析**：`AppContext.BaseDirectory` 之外再试 `typeof(FontManager).Assembly` 所在目录，
   并开放 `FontManager.BundledFontDirectory` 让宿主显式指定。改动小，但 NuGet 场景下库目录里也没有字体，
   所以它只治「仓库内部各宿主不一致」，治不了消费方。
3. **至少让它不再静默**：暴露一个只读诊断（这次到底用了打包子集还是系统回退），
   缺文件时给一次性信号。不改变任何产物，纯止血。

我的建议是 1 + 3 一起做，2 顺手；但在字体是否随包分发这件事上涉及包体积与「消费方产物会变」，
属于跨模块决策，等你定。

### 12.7 各宿主的字体在场情况（决定数字能不能互校）

| 宿主 / 工程 | `AppContext.BaseDirectory/assets/fonts` | 该工程产出的数字属于哪套配置 |
|---|---|---|
| `PdfBench.Runner`（§12 与 scale 扫描的全部测量） | **无** | 系统字体 = NuGet 消费方实际所得 |
| `PaperSharpSamples`（§3 / §4 / §11.5 的 `bench 30`） | 5 份 | 打包子集 |
| `PaperSharp.Tests`（像素与结构金标） | 5 份 | 打包子集 |

**顺带纠正 §12.5 早期版本对锚点差值的解释**：Z1(invoice) 在本 harness 量到 worker 内 60.8 ms、
自家 `bench 30` 量到 35.5 ms，当时归因给「CSS 从参数改成内联 `<style>` 的路径差」——**错的**。
真正原因是两边量的不是同一套字体源：一边是 30,209 字形、15 MB 的系统雅黑，一边是 1.2 MB 的预裁子集。
跨引擎比较仍自洽（PaperSharp 与 Chromium 都在系统字体下量，且这就是消费方的真实配置），
但**本报告与 §11.5 之间不可互校绝对值**。合并趋势前先对齐宿主，或在 `PdfBench.Runner` 的 bin 下
也放一份 `assets/fonts` 再重测。

### 12.8 字体修复的落地状态与合并注意事项

分支 `fix/bundled-fonts`（HEAD 起 2 个 commit：`19168bb` 嵌资源 + `9c55e22` 同步与漂移门）。

**实测效果**（无 `assets/fonts` 的控制台宿主，即 NuGet 消费方的真实形态）：

| 样例 | 修前 | 修后 | 倍数 |
|---|---:|---:|---:|
| m1 | 4,002,249 B | 315,539 B | **12.7×** |
| table | 401,668 B | 121,830 B | 3.3× |
| report | 561,137 B | 193,128 B | 2.9× |
| invoice | 278,219 B | 96,365 B | 2.9× |
| polish | 313,457 B | 134,165 B | 2.3× |

**测试宿主切到内嵌来源后产物一字未变**：全量 50 红 / 341 绿，红名单与 HEAD 逐条相同
（含像素与结构金标）。这条比任何新写的对账测试都强——它就是「文件来源 vs 内嵌来源」的等价证明。

新增 9 条测试钉住：来源必须是 `embedded`、PRKT 字距表必须仍挂得上（`AttachFromBytes`，
不能被 `AttachFromTables` 顶替）、不得出现 `mixed:` 来源、消费方视角的产物体积量级、
以及库内副本与烤字体脚本产出逐字节一致（漂移门，已自证退回必红）。

**合并时必做两件事**：

1. 在途那批重烤过的字体（`NotoSansSCSub-*` 已从 2,467,864 B 变成 2,331,520 B）
   必须同步进 `src/PaperSharp/Fonts/`，否则出厂字体 ≠ 被验证的字体——
   漂移门会红，这是设计如此，别去改测试。跑一次
   `python src/PaperSharpSamples/tools/subset-font.py bake` 即可自动同步。
2. 本分支与 `perf/shaped-run-cache` 都基于 HEAD，`FontManager.cs` / `KernTable.cs`
   在途改动很大（FontManager +287 行），三方合并时以在途版为底、把这两刀的语义重放过去。

包体代价：`PaperSharp.dll` 与 nupkg 各 +8.3 MB。字体本来就必须随包分发（OFL 还要求带许可证），
以前只是没发而已——所以这不是新增成本，是补上漏发。

### 12.9 安静机复量刀①+刀②：边际成本 49.7 → 36.3 ms/声明页（−27%），字节零漂移（2026-10-06）

§12.5 落地时「本机测不出」的两刀，在安静机器（负载 9–19%；§12.5 当时 31% + 游戏客户端）上用
`scale/sweep.py` 的 psworker 通道正式复量。协议：**双臂各自独立构建**——BASE = 干净 be10324
检出（新 worktree `.perf-base-wt`），ARM = `perf/shaped-run-cache`（6f70e15）；两臂的
PdfBench.Runner 源码逐字节同一份（共享树 harness 引用了在途树的 `PageSpec.PtPerPx/PdfPageW`，
已把 StageProbe 回移植成 be10324 API，两副本 diff 为空），ProjectReference 各自 worktree 的引擎；
A 轴 warm、每臂两轮、**ABBA 换序**（base r1 → arm r1 → arm r2 → base r2），每轮独立 out 目录；
runner bin 无 `assets/fonts` → 系统字体（与 §12 跨引擎扫描同配置）。原始数据
`.scratch/ab/{base,arm}/{r1,r2}/scale_raw.csv`，拟合脚本 `.scratch/ab/analyze.py`。

| cell | 声明页 | base med(ms) | arm med(ms) | Δ |
|---|---:|---:|---:|---:|
| A1 | 1 | 170.5 | 166.5 | −2.3% |
| A3 | 3 | 363.3 | 317.7 | −12.5% |
| A10 | 10 | 625.8 | 631.4 | +0.9%（n=3，噪声） |
| A30 | 30 | 1,699.1 | 1,321.5 | −22.2% |
| A100 | 100 | 5,128.7 | 3,818.4 | −25.5% |

线性拟合（声明页数轴）：base **165.1 + 49.74 ms/页**（把已发布的 174 + 51.6 复现出来了，
口径互证 ✓）、arm **207.2 + 36.25 ms/页** → **边际 −27.1%（−13.5 ms/页）**。截距 +42 ms
在 5 点拟合的噪声内（A1 两臂只差 −2.3%），主张落在 b 上。A30/A100 的同臂轮间漂移 ±3~28%
（ABBA 取均后方向一致）；A10 是唯一无增益的档（小样例 n=3，不可分辨）。
**对 Chromium 系的交叉点：playwright 37.5 → 56.4 页、chrome-cli 44.6 → 79.9 页、
puppeteer 50.7 → 82.4 页**（wkhtmltopdf 依旧第 4~7 页反超，但它不画 flex，见 scale REPORT §6 折扣）。
⚠️ §12.3 当时按「①−11 + ②−6」推的「≤110 页全区间第一」用的是投影上界；实测 −13.5 ms/页
意味着只靠 ①+② 对 playwright 的稳赢区间是 **≤56 页**，要逼近 110 页还得刀③（自研层叠，
buildDoc 在 A30 上仍有 793 ms）或刀④。

**stageprobe A30 分段归因**（5 次中位；探针已回移植 be10324 API 并镜像其录制趟——无
`canvas.Scale(PtPerPx)`、scratch 直接 `PageW×PageH`、`Enhance` 四参）：

| 段 | base | arm | Δ |
|---|---:|---:|---:|
| measure | 203.9 | 100.8 | **−50%** |
| paginate | 200.2 | 72.7 | **−64%** |
| recordpass(RASTER) | 165.2 | 64.4 | **−61%** |
| realencode(PDF) | 276.0 | 191.3 | **−31%** |
| buildDoc | 853.8 | 792.6 | −7%（噪声内，与文本无关，符合 §12.2） |
| 段合计 | 1,853.3 | 1,272.5 | −31% |

**字节门**：同宿主同协议（psworker、系统字体、`RenderPdf(html,"",A4,baseDir)`）下
A1/A30/A100 双臂产物**逐字节相同**（仅 `/CreationDate` 秒级时间戳差，归一后 0 diff），
三档页数一致。脚本 `.scratch/ab/bytecheck.py`。

**⚠️ 复量时新发现：干净 be10324 上 A 轴页数翻倍**（A1→2、A3→6、A10→20、A30→60、A100→200，
恰好 2×——阈值型溢出的特征：每个「A4 整页」单元多溢出一点点、整单元翻成两页），
**双臂同病**。而已发布的跨引擎扫描页数是 1/3/10/30/100 全对——`out/env.json` 记录它跑在
be10324 + **183 个脏文件**上：**在途的对拍修复恰好修好了已提交历史里的这处分页回归**。
三条影响：① 本节 Δb 双臂同病，增量有效；② 绝对交叉点与 Chromium 比较混入了 2× 页数的
页级开销（页眉/页脚/BeginPage ×2），对拍收敛后要重测一遍；③ **perf 分支合并必须以在途
对拍修复为底**（§12.8 已预告 FontManager/KernTable，此处再加 Paginator/PageSpec 一族），
否则合并产物自带翻页回归。

**B 轴（B500/B2000 长表格，同协议 ABBA）——收益远小于 A 轴**：B500 base 1,202 vs arm
1,205 ms（**+0.3%，持平**）；B2000 base 5,002 vs arm 4,270 ms（**−14.6%**，但同臂轮间漂移
±12~19%、每档 n=3×2，判读「有正收益、置信度弱」）。注意 B 轴页数**没有**翻倍（B500→21 页、
B2000→81 页），翻页回归是 A 轴「整页单元」语料特有的阈值溢出。机制上说得通：刀①的整形
缓存吃**重复文本**，表格逐行文本近乎独特、命中率低；刀②省的装饰录制在表格里占比也小。
**结论：①+② 的 −27% 是 A 轴（富样式整页/文本流）的数字，长表格场景别外推。**

**顺带一条（对拍在途树还压着没提交的 buildDoc 收益）**：§12.1 记在途树 A30 buildDoc 566 ms，
本节干净 be10324 的 stageprobe 量到 853.8 ms——在途对拍改动里还叠着 ~−34% 的 buildDoc
（PropCache 建表那族）。这既是对拍分支必须尽快收敛的又一个理由，也会削减刀③的剩余空间
（评估刀③前先用在途树重跑基线）。

---

## 13. PDF 体积刀①：cmap 收口 + name 瘦身（2026-10-06，分支 `perf/pdf-size-cmap`）

### 13.1 盘点（`.scratch/sizeknife/inventory3.py`，解 FlateDecode 后逐 sfnt 表）

七家横向报告说「剩余体积是 6 段嵌入字体流」，§11.6 说「大头是 cmap 与 GPOS/GSUB」——
盘点推翻了后半句。**GPOS/GSUB 在我们的嵌入流里几乎为零**（打包子集 face 各 16~38 B，
系统 face 也只有 0.4~2.4 KB），真正的大头按序是：

| 表 | z1 打包子集 face | z1 系统 face | 说明 |
|---|---:|---:|---|
| **cmap** | 34,176/34,296 B（44%/31%） | 102,716/103,666 B（48%/27%） | 「每码位一条」的全量映射：发票只用 ~30 字，却背 7,938（打包）/30,209（系统）条 |
| hmtx+vmtx+loca | 55,666 B | 321,572 B | 全是 **GID 槽位表**：保 GID 设计下按「最后一个用字」收尾，中间空洞压不掉 |
| glyf | 14,072 B | 34,504 B | 真正画字的轮廓，只占 3~11% |

两个配置的嵌入流原始字节：z1.bun 186,608 B（文档 96,365 B 的 194%，压缩后 ~90 KB），
z1.sys 598,196 B（文档 278,219 B 的 215%）。**invoice 的体积就是字体本身**，其它都是零头。

### 13.2 落地（`TtfSubsetter.cs`，保 GID 设计不动）

- **cmap 收口**：把每张子表重建为「只含保留字形」的映射。格式 4（BMP 段，delta 编码重建 +
  0xFFFF 哨兵）、格式 12（分组重建）、格式 6（连续表）、格式 0（原位清 0）；
  2/8/10/13/14 等罕见格式原样保留；任何解析意外 → **整张 cmap 退回原样**，绝不赌半个 cmap。
  保留字形的 (码位→GID) 逐位不变；未保留码位语义等同「字形不存在」（GetGlyph→0）。
  引擎永远用原字体做查询/整形（子集 face 只出现在 PDF 嵌入与按 GID 绘制），渲染零影响。
- **name 瘦身**：只留 0/1/2/3/4/6/16/17 号记录（家族名原样，FamilyName 断言吃它），
  其余许可证备注/供应商 URL 整条删（系统雅黑 4.3 KB → 2.7 KB）。
- **没有动**：loca/hmtx/vmtx（GID 槽位表，见 §13.4 的重编号方案）、kern、VDMX、fpgm/cvt/prep、
  GPOS/GSUB/GDEF（本来就近乎为零，不值得引入 lookup 重映射风险）。

### 13.3 效果（同一宿主、同参数，改前 `.scratch/sizeknife/before/` vs 改后 `after/`）

| 样例 | 配置 | 改前 | 改后 | Δ | 参照 |
|---|---|---:|---:|---:|---|
| Z1 invoice | 打包子集 | 96,365 B | **47,519 B** | **−50.7%** | Chromium 同文 ~50 KB → **反超** |
| Z1 invoice | 系统字体 | 278,219 B | 120,258 B | −56.8% | |
| login-page | 系统字体 | 306,571 B | 147,711 B | −51.8% | |
| login-page | 打包子集 | 306,571 B | 147,711 B | =（该样例自带 @font-face，不走子集族）| |
| m7 emoji | 双配置 | 7.33 MB | 7.19 MB | −1.9%（COLR 未动，本表为刀①单独口径；同日刀②落地后 −92.6%，见 §13.6）| |
| A100 长文档 | — | 2,471 KB（发布值）| 待收敛后随 scale 重测 | — | wkhtmltopdf 1,377 KB |

cmap 实测：YaheiSub-Bold 34,176 → **436 B**，系统雅黑 102,716 → **764 B**（fmt4+fmt12 双子表都重建）。

**门**：`TtfSubsetterTests` 7/7 全绿（5 张打包字体「FamilyName + 全探测集 GetGlyph 逐位 +
逐字面 MeasureText + 48×48 栅格逐字节」）；全量测试红名单见 §13.5。

### 13.4 剩余的刀（按字节排序）

1. ~~**COLR v1 图层闭包（m7 的 6.5 MB，全场最大单件）**~~ **✅ 同日第二刀已落地，见 §13.6。**
2. **GID 重编号（真子集，改设计面大，需先拍板）**：loca/hmtx/vmtx 三张 GID 槽位表合计
   占打包 face 的 91%（z1.bun 43,824 B 里 39,790 B）。重编号后 invoice 可从 47.5 KB 再降到
   ~20 KB 档，但会推翻 §11 的「GID 不变」设计：glyf 复合引用、cmap、kern、GPOS/GSUB 覆盖、
   以及 `PdfGlyphUsage` 双趟绘制的 GID 语义都要跟着重映射，`TtfSubsetterTests` 的
   GetGlyph 逐位断言也要改语义。收益只对「嵌入后不再查询」成立，需要单独一轮设计+对账。
   §13.6 之后追加：emoji face 剪完 COLR/glyf 后，剩余体积的 60% 是 hmtx 258 KB + CPAL 262 KB
   + loca 130 KB——重编号与 CPAL 剪枝对含 emoji 文档同样成立。
3. 位图重采样（UI 还原类页面）与 `fpgm/cvt/prep`（hinting 字节码，Adobe 阅读器在用，慎动）——低优先。

### 13.5 测试基线（实测）

`.subset-wt`（be10324 + 本刀）全量套件：**50 红 / 332 绿（总数 382）**；干净 be10324
（`.perf-base-wt`，无本刀）同套件 **50 红 / 332 绿**，两边失败清单**按测试名逐条相同**
（diff 时须剥掉 xUnit 的时长后缀，`[654 ms]`/`[1 s]` 两种形态都别漏）。这 50 条红是
对拍在途改动的既有归属（金标未重录；`PdfRasterTests`/`PngGoldenTests`/`BoxGoldenTests`
占大头），与本刀无关——本刀只改 cmap/name，文本抽取实测前后一致（z1 396 字符逐位相同）。
合并次序依旧服从 §12.8/§12.9：以对拍在途修复为底。本刀已提交：
`perf/pdf-size-cmap` 分支 `2b30322`。

### 13.6 体积刀②：COLR v1 图层闭包——m7 −92.6%，逐像素全等（2026-10-06 同日第二刀，commit `8c0f5d6`）

彩色字体此前被 `PdfGlyphUsage` 的闸口整张跳过：seguiemj 以 **12.9 MB 原样整嵌**
（COLR 7.4 MB + glyf 4.7 MB 全量轮廓 + CPAL/槽位表）。本刀让 COLR v1 走真正的图层闭包：

- **解析器以一手定义为准**：字段布局逐格式对照 fontTools `otData.py`（子 Paint 偏移是
  **Offset24** 自 paint 表首、`PaintColrLayers.firstLayerIndex` 是 **uint32**、
  `BaseGlyphPaintRecord` 的 paint 偏移经 seguiemj 实测为**从 BaseGlyphList 表首起算**——
  微软规范的转述页与 fonttools 的注释各说错了一次，实测为准）。
- **BFS 可达**：used base → Paint 图（ColrLayers 连续区间 / ColrGlyph 跨 base 引用 /
  PaintGlyph 轮廓字形）；图层字形并进 glyf 闭包——**根治 §11.4「彩色字体裁不得」**
  （当年是图层 GID 掉出闭包 → 彩色层空字形 → emoji 变黑白线稿）。
- **重写**：LayerList 按原下标升序重编号（连续区间映射保持连续）；BaseGlyphList 与 v0
  平行数组同步剪枝；ClipList 尾拷（保 GID 设计下引用恒有效）；可变彩色（VarIndexMap/
  VarStore 非零）与一切解析意外 → 整 face 放弃裁剪维持整嵌，**绝不出半张 COLR**。
- 闸口收窄为 **CBDT/sbix/SVG 整嵌**；COLR 交给裁剪器。

**⚠️ 逐像素门顺带抓出一个先于本刀存在的录制缺口**（对拍表现为阿拉伯文整词消失）：
真绘趟是拿**子集 face 重新 shape** 的，HarfBuzz 第一步仍查 cmap——录制只记 hb 输出的
上下文变体形，而阿拉伯文 cmap 指向的独立形不在闭包、被 §13.2 的 cmap 收口剪掉 →
真绘趟码位→字形直接查不到。修复 = `DrawShaped` 录制侧补 `RecordString(tf, text)`。
**教训：录制闭包 = hb 输出字形 ∪ 输入码位的 cmap 目标字形**——「嵌入后不再查询」这个
假设对字符串绘制路径不成立，因为查询发生在真绘趟的子集 face 上。

**效果**（同宿主同参数，系统/打包双配置）：

| 样例 | 配置 | 刀前 | 刀后 | Δ |
|---|---|---:|---:|---:|
| m7（emoji） | 打包 | 7,135,890 B | **529,734 B** | **−92.6%** |
| m7（emoji） | 系统 | 7,328,956 B | 677,788 B | −90.8% |
| z1 invoice | 打包 | 96,365 B | 47,519 B | −50.7%（§13.3，录制修复后不变）|
| login-page | 系统 | 306,571 B | 147,711 B | −51.8% |

emoji face 明细：COLR 7,420,756 → **40,019 B**、glyf 4,676,996 → **19,056 B**、
loca 自动降回短格式；剩余 60% 是 hmtx/CPAL/loca 槽位表（→ §13.4 第 2 条）。

**门**：m7/z1/login 系统配置**跨引擎逐像素 0 差异**（300dpi 全页，pdfcmp 工具
`.scratch/sizeknife/pdfcmp/`；同引擎自比对 0 差异证明栅格确定性）；
`TtfSubsetterTests` 7/7；全量套件 50 红与 be10324 基线**逐条相同**。
合并提醒：本刀动了 `PdfGlyphUsage.cs`——与 `perf/shaped-run-cache` 的刀②（录制趟只走文本）
同文件，三方合并时以在途版为底、两刀语义都重放。

### 13.7 两刀合回主干（2026-10-06，工作树重放，未提交）+ 合并态全量重测

**合并方式**：master 工作树压着 186 个对拍在途文件（含 `PdfGlyphUsage.cs` 的 FaceKey
身份去重重构——顺带修掉「同一字面裁 33 份各嵌一次、占 m1 全文积 86%」的体积 bug），
FF merge 会被脏文件挡住，也不该替对拍会话提交半成品。所以走**外科式重放**：
`TtfSubsetter.cs`（主干侧干净）整文件拷入；`PdfGlyphUsage.cs` 的三个 hunk（闸口改名、
COLR 放行注释、`DrawShaped` 录制补 `RecordString`）逐段重放到 FaceKey 重构版上
（闸口行与 be10324 逐字相同，重放无冲突）。**两刀的评审单元仍是分支 commit
`2b30322`/`8c0f5d6`；主干侧待对拍收敛后与 shaping 刀（`perf/shaped-run-cache`）一并正式提交。**

**门（合并前后同树对拍）**：全量套件 **48 红 / 439 绿 / 487 总 → 48 红 / 439 绿 / 487 总，
红名单逐条相同**（合并前基线 `/tmp/premerge_red.txt`、合并后 `/tmp/postmerge_red.txt`，
diff 只差摘要行用时）。439 绿里含对拍在途态自己的像素/结构金标——即体积两刀在
「对拍口径」下也没有改变任何渲染结果。

**合并态体积**（`.scratch/sizeknife/mergedout/`，与 §13.6 分支数对比）：
z1.bun 47,680 B（分支 47,519 ✓）、z1.sys 120,416（120,258 ✓）、m7.sys 677,573（677,788 ✓）、
m7.bun 585,953（分支 529,734，**+56 KB 差在主干侧 Noto 子集是在途重烤版**——
§12.8 漂移门要同步的那批字体，非本刀行为差异）。

**合并态 A 轴重测**（warm × 2 轮，系统字体，与跨引擎扫描同口径；
`.scratch/ab/mergedA/`）——两个 headline：

1. **§12.9 的分页翻倍回归在合并态消失**：A1/A3/A10/A30/A100 页数 = **1/3/10/30/100 全对**
   （证实该回归只存在于干净 be10324，在途对拍修复正是它的解药）。
2. **边际成本 wall = 197.1 + 38.62 ms/页**（worker 口径 186.9 + 38.53，两口径一致）——
   对比已发布的 174 + 51.6：**边际 −25.2%**。构成：在途对拍的 buildDoc −34%（§12.9 末）
   + 分页修复去掉 2× 页级开销 + 体积两刀砍掉的字体嵌入/deflate 时间（体积刀同时是速度刀）。
   shaping 刀①+②（`perf/shaped-run-cache`，实测 −27.1%）**尚未并入**，投影并入后
   b ≈ 28 ms/页。
3. **交叉点**：vs playwright **52 页**（发布口径 35 页）、chrome-cli 71、puppeteer 74；
   投影并入 shaping 刀后 vs playwright **~85 页**。固定成本 197 vs 发布 174 的 +23 ms
   在对拍在途态的会话噪声内（§8：±10% 底噪 + 本轮 439 绿含新改动）。

**遗留**：B 轴（长表格）合并态未重测（shaping 刀对 B 轴本就无效已证，表格专项是独立议题）；
`perf/shaped-run-cache` 的正式合并、字体漂移门（§12.8）仍排在主干侧对拍收敛之后。

**合并态完整性通道（同日补跑，`integrity.py --engines papersharp --force`，26 档去重）**：
ok 10 / 少画 8 / 少画图像 2 / 页数不一致 8——**与发布时逐格同分布**。A 轴全 ok（翻倍确系
干净 be10324 独有）；**C/X 深嵌套塌页（C30 起 pages=1 vs 参照 35/11/4）与 D 轴长文本丢字
（留存 25~41%）在合并态依旧存在**——§12.4 说的「修分页是性能工作前置」对 C/D 两轴仍然成立，
在途对拍修复没有覆盖它们。B2000/B8000 的「页数不一致」是 ±1~3% 的舍入级差异，非缺陷。

### 13.8 C/X 塌页根因（2026-10-07 诊断，未修复；天真修法已证伪）

**症状**：C 轴 depth≥30 的语料整篇塌成 1 页，但字符几乎全在（未修版 C30 = 39,710 字符）——
内容全部叠画在第 1 页同一游标位置，被页面裁掉。

**受控实验**（`.scratch/cxdiag` 独立诊断工程 + `.scratch/cxprobe` 夹具，psworker 直渲）：

| 变量 | 结果 |
|---|---|
| 元素数（reps 40→1000，depth 30 固定） | **无关**，正常出 20/58/143 页 |
| 纯深度（margin 固定） | m4 死于 d45、m8 死于 d30、m16 死于 d18、m24 死于 d14 |
| 无段落 margin 的固定高度块（h 变体） | depth 30 **不塌**（16 页） |

阈值精确满足 **d × margin ≥ 链高**（链高 = 8 行 × (19+m)）——机制实锤：
`Ctx._flowPrev` 是跨 Fragment 递归**共享且不重置**的兄弟游标（Paginator.cs:81），
容器首子的 `CurY -= AbsorbMargin(_flowPrev, box)`（:113）于是拿「父层的上一个兄弟」做坍缩；
而单链嵌套里每层的 `EffTop` 都含着孙辈逃逸上来的同一份 margin（:920 用 EffTop 而非
Style.MarginTop——这本是对拍修过的正确口径），于是**同一段逃逸边距在每一层被重复吸收一次**，
净流高 = 链高 − d×margin，一旦 ≤0，链块对游标零推进、全部叠在第 1 页。
这同时解释合成（4px×45 层）与语料（16px×30 层）两组看似无关的阈值。

**天真修法已证伪**（记下来免得再试第二遍）：在 `Fragment` 入口 `_flowPrev = null`——
合成案例全部修好（d46→9 页、d30×133→30 页、套件红数不变 50），但**语料 C30 从
「1 页 39,710 字符」恶化成「1 页 760 字符」——内容直接丢失**。说明逃逸组坍缩的正确记账
不是「容器入口断开」这么简单：一段逃逸 margin 与外部兄弟的坍缩只该发生一次、且发生在
正确的层；把游标清零会连带杀掉合法的那一次，链块被推到页外。正确修法方向：给
「逃逸 margin 组」做幂等记账（同一组只吸收一次，例如在 MeasureBlock 落一个
`EdgeGroupOwner` 标记，AbsorbMargin 只在组的顶层生效），而不是动游标。

**X 轴是另一条机制**：X100 字符只剩 2.5%——那是每层 2px padding × 100 层把可打宽度压成
负数的退化（gen.py 注释里就是故意造的第二种病），与本条的 margin 累积无关，需单独分诊。

**复现套件**：`dotnet run --project .scratch/cxdiag -- <p|pw|h> <margin> <depth> <reps>`
（打印链的 TotalHeight/EscapeTop/复刻 FlowHeight + Paginate 页数）；
语料直渲走 `PdfBench.Runner.exe psworker` + corpus/C30.html。

---

## 14. 合并态性能研究：下一刀该往哪打（2026-10-07）

### 14.1 分段归因（合并态引擎，stageprobe 5/3/5 次中位；⚠️ stageprobe 口径 ≠ sweep worker_ms，只用占比与 ms/页做归因）

| 段 | A30（30 页富样式） | B2000（59 页长表格） | Z1（1 页发票） |
|---|---:|---:|---:|
| **buildDoc** | 853.8 ms（28.5/页，**37%**） | **2,832 ms（48.0/页，58%）** | 14.2 ms |
| measure | 351.9（11.7/页） | 501.6 | 1.8 |
| paginate | 321.8（10.7/页） | 436.4 | 1.3 |
| recordpass | 295.5（9.9/页） | 560.6 | 1.6 |
| realencode | 458.9（15.3/页） | 699.1 | 30.8 |
| 文本链合计（measure..realencode） | **63%** | 42% | — |

读法：**A 轴的账在文本链**（shaping 刀①+② 正是打这里，实测 −27%，仍未并树）；
**B 轴的账在 buildDoc**（58%、48 ms/页——长表格 4.6× 落后 playwright 的主因）；
单页发票只剩 realencode（30.8ms）为大头——已是最小文档的地板。

### 14.2 刀③（自研层叠替换 AngleSharp）前提证伪

docsplit/splitprobe 三连（B2000，14,017 元素）：
- **AngleSharp 裸 HTML 解析 = 25~99 ms**——不是它。
- **原生姿势的 `GetStyleCollection().ComputeDeclarations` 逐元素循环 = 12 ms/元素**
  （§5.5 那个未物化 + 每祖先重枚举的怪兽；14k 元素要 170 秒）——而引擎现在整条
  BuildDocument 才 **167 µs/元素**：**物化集合 + 按元素缓存 + PropCache 已经把这条路的
  常数项砍掉 ~70×**。自研替换能赢回的只剩常数里的一小块，M6 的「性能理由」正式归零。
- domPath 生产默认关（BuildDocument 第 6 参），不在账上。
- ⇒ B2000 那 2.8 s 花在**我们自己的每元素一遍**：物化层叠轮 + Walk + ComputedStyle 填充 +
  值解析的合账（§5.7 当年「自研 47%、残差 32% 未拆」的那块）。

### 14.3 未决：层叠轮 vs Walk 的最后一刀分账（实验被噪声吞了）

在 `.perf-base-wt` 用 env 桩（`PS_SKIP_CASCADE=1` → ComputeDeclarations 直接返回 null，
Walk 骨架照跑）做 A/B：层叠开 ③=2,338~3,822 ms、层叠跳 ③=2,609~4,261 ms——**并发污染下
两臂区间重叠，判不动**（桩已撤除、工作树已还原）。安静机上重跑 `.scratch/splitprobe`
（已裁掉 170 秒的 B 臂）即可定案：若跳层叠臂显著更低 → 下一刀做**层叠数据结构**
（特异度分桶/按规则预筛）；若持平 → 下一刀做 **Walk/ComputedStyle 填充瘦身**
（94 槽结构、值解析、每元素分配）。这是 B 轴 48 ms/页 的最后一公里。

### 14.4 冷启动旁证（同一批 max/med 差）

Z1 首迭代 buildDoc max=395 ms vs med=14 ms、B2000 max/med≈1.3——**冷启动 ~1 s 的大头是
JIT 而非字体/Skia**（§2 那笔 09-22 的 Debug 旧账方向得改）。候选杠杆：PublishReadyToRun
（发布形态）或分层预编译调参；未做。

### 14.5 下一刀优先级（本轮研究后的重排）

1. **并树 shaping 刀①+②**（`perf/shaped-run-cache`）——唯一已实测（−27% 边际）未落袋的收益。
2. **14.3 的分账实验**（安静机 10 分钟）→ 决定「层叠数据结构」vs「Walk 瘦身」谁是 B 轴那 48 ms/页 的刀。
3. **C/X 塌页修复**（§13.8 根因已定位，逃逸组幂等记账方向）——它同时是 B/C/D 轴数字可信度的前置。
4. M6 自研层叠：**从性能清单上划掉**（14.2），只保留依赖收敛这一个非性能动机。

## 15. 内存：单次导出的 300MB 从哪来，砍到哪（2026-10-27 本轮）

### 15.1 问题的口径

§7 那批老数据（invoice/long/flex/img/report/pct/polish/inline/table，单页渲染）**进程工作集
309.4–326.1 MB**——这就是「生成 PDF 占内存 300M 左右」的原始出处。注意它是**工作集**，不是托管堆
（托管堆增量只有 17–30 MB），所以先要把这 300 MB 拆开，否则会一直往托管堆上找错地方。

本轮全部用 `tools/MemStress` 记账（`PrivateMemorySize64` / `WorkingSet64` / `GC.GetTotalMemory` /
`GC.GetTotalAllocatedBytes` 四个口径分开记），模板用 `benchmarks/PdfBench/spec/challenge.html`。

### 15.2 分阶段归因（改造前，首份，`PS_MEMTRACE=1` 探针）

| 阶段 | priv | ws | 托管堆 | 累计分配 |
|---|---:|---:|---:|---:|
| 进程起点 | — | — | — | — |
| dom-parse（AngleSharp） | 34.2 | 67.4 | 5.0 | 10.8 |
| walk（层叠 + 建样式树） | 55.5 | 94.6 | 22.1 | 34.9 |
| buildLayout（盒树 + 测量） | 61.8 | 102.6 | 26.7 | 53.2 |
| recordingPass | 67.7 | 110.6 | 26.6 | 67.6 |
| **subsets（字体子集化）** | **148** | — | — | 73.1 |

结论只有一条：**`subsets` 一步把 priv 从 68 拽到 148 MB**，其余阶段加起来不到 40 MB。
根因在 `TtfSubsetter` 的老实现——它把 21 张表**全部**读成 `Dictionary<uint, byte[]>` 塞进托管堆再拼装，
系统雅黑一张面就是 **18.6 MB 表字节**，每多一张 CJK 面就多一份，且这些表进 LOH。

配套的活字面盘点（`PS_FACEPROBE=1`）同时暴露了第二笔账：**13 张活字面合计 89.5 MB 表字节**，
其中 4 张是雅黑（2×18.68 + 2×15.98 = 69.3 MB）——**同一张物理面被存了多份**：
- 同一张正立面同时以 `s0`（upright）和 `s2`（italic 请求）两个键各存一份（§15.3 刀②）；
- 同一张面又被 family 别名各存一份（`Microsoft YaHei` / `微软雅黑` / `system-ui`）；
- 雅黑 Light 那张 11.61 MB 的面**只用来读 4 个度量数字**（§15.3 刀③）。

### 15.3 五刀

**刀① · 字体子集化改流式（`Pdf/TtfSubsetter.cs`）**
新增内部类 `SfntFont`：优先用 `SKTypeface.OpenStream(out int ttcIndex)` 按 sfnt/TTC 目录做**表视图**，
失败退回 `GetTableData`。真要改写的小表只有 9 张
（`head/maxp/hmtx/vmtx/hhea/vhea/cmap/name/COLR`），`glyf`/`loca` **不进** `Rewritable`——
`ComputeClosure` 逐字形按 `ReadInto` 读复合组件，`BuildSfnt` 一次分配**精确大小**的输出缓冲，
`glyf` 按保留字形**逐段直拷**、其余表 `ReadInto` 直传。
效果：`open/loca/closure/tables/built` 全程 <0.5 MB，`subsets` 一步的 priv 增量从 **+80 MB 降到个位数**。

**刀② · 物理面身份归一 + 去重（新增 `Layout/FaceIdentity.cs`，`FontManager.Canonicalize`）**
`FaceKey(Family, Weight, Width, Slant, GlyphCount, HeadSum)` 用 `ConditionalWeakTable` 挂在 `SKTypeface` 上；
关键一行是**倾斜档归一**——`FontManager.FaceIsSlanted(t) ? (int)t.FontSlant : (int)Upright`。
不归一的话，同一张正立面会以 `s0/s1/s2` 存三份各 18 MB（CSS 写 `font-style:italic` 时 Skia 常常
仍把正立面还给你，键却已经分叉）。`ResolveFallback` 的系统命中分支在 `KernTable.AttachFromTables`
之前调 `Canonicalize`：同物理面已有一份就 `Dispose` 新的、返回老的。

**刀③ · 雅黑 Light 只借度量（`FontManager.ResolveYaheiLight`）**
`_yaheiLight` 从「持有整张面」改成「只存 5 个 float」（ascent/descent/lineGap/normalLh/xHeight），
面当场 `Dispose`；`LightCjkExtents` 改用 `(Px(light.Ascent*sizePx), Px(light.Descent*sizePx))`。
省下 11.61 MB 常驻，度量口径一字未变（§15.4 的栅格门就是验它）。

**刀④ · PDF 编码不再整份字符串化（`Renderer.EncodePdfPages` + `Pdf/PdfEnhancer.cs`）**
- `MemoryStream.TryGetBuffer` 取段，去掉 `ToArray()` 的整份多拷贝；
- `PdfEnhancer` 里新增 `readonly struct PdfBytes`，`LastIndexOf/IndexOf/IndexAfter/SkipWs/ParseLongAt/
  ExtractDict/ParseXrefTable/ReadObject/CollectPages` 全走字节扫描，只把对象字典变成小字符串——
  原来 `Latin1.GetString(整份 PDF)` 是 2.4 MB → 4.8 MB 的 UTF-16 常驻。

**刀⑤ · 层叠缓存时效化（`EagerStyleCollection.Release`）**
`_merged` 里每张声明都是 AngleSharp 的**全量长手属性表**，challenge.html 265 个元素 ≈ **24 MB**，
却只在 `Walk` + 伪元素落树期间有读者（`ctx` 是 `BuildDocument` 的局部量，返回值 `ComputedStyle`
树不反向引用任何声明对象）。在 `PseudoContent.Apply` / `Animation.Apply` 之后 `Release()` 掉四张表，
保留 `_rules`（万一有迟到的层叠调用重算依然正确，Release 因此幂等、不改语义）。
实测收益偏小（`subsets` 处 priv 74.4 → 71.5 MB），因为 `GC.GetTotalMemory(false)` 在回收前不缩，
这刀的真正价值是**留住这 24 MB 的生存期从「整条管线」缩短到「Walk」**。

> **顺手记一笔被否决的刀**：曾试过「叶子元素不进 `_merged`」（缓存只对后代有用，叶子读了就丢）。
> 实测**更差**——伪元素那两条路径会再次向宿主 `Merged()`，而宿主多为叶子，重算一次级联（每张 ~90 KB
> 全表匹配）的代价超过省下的滞留：walk 分配 34.9 → **35.5 MB**、`subsets` priv 71.5 → **72.4 MB**。
> 已回退，理由写进 `EagerStyleCollection.Merged` 的注释里，避免下次再试。

### 15.4 两个口径的现状（同一台机器、`--docs 3 --content same`、三次取中位）

判读前先记住**进程地板**：`<html><body><p>hello</p></body></html>` 这种极简文档，
priv **43–45 MB** / ws **85–87 MB**——那是 .NET 运行时 + JIT + SkiaSharp 原生库的底，
任何「还能再降多少」的问题都要先减掉它。

| 口径 | 版本 | 峰值 priv | 峰值 ws | 峰值 heap | Full GC 后 priv（原生侧） |
|---|---|---:|---:|---:|---:|
| **不打包子集**（宿主未配字体，中文走系统整份雅黑） | 改前 | 211.7 | 306.3 | 134.1 | — |
| | 改后 | **88.5** | **141.5** | **22.6** | 76.3（≈67.2） |
| **打包子集**（产品形态，`--bundled assets/fonts`） | 改前 | 65.9–69.9 | 119.6–122.7 | 17.5–19.0 | 55.9–65.5（≈51.5–61.0） |
| | 改后 | 70.8–71.9 | 118.9–119.7 | 14.9–16.9 | 56.2–58.0（≈51.7–53.5） |

读法：
- **最坏口径 ws 306 → 141 MB（−54%）、priv 212 → 89 MB（−58%）、堆 134 → 23 MB（−83%）**，
  这是本轮真正的收益所在（用户报的 300MB 就是这个口径）。
- 打包口径两版**在噪声内相等**：峰值 priv 差 ~2 MB、ws 差 ~3 MB，而 Full GC 后的**原生侧两边同为
  ~52–53 MB**——差别只落在采样瞬间的堆大小上。符合预期：子集面本身只有 2–3 MB，
  老实现「把 21 张表读进堆」在这里本来就只花几 MB，刀①打的是它在大面上的账。
- 打包口径吞吐 344–346 ms/doc（改前 526 ms/doc 是不打包口径，不可直接比）。

**剩余构成（打包口径，Full GC 后 priv 57 MB）**：地板 ~43 MB + 渲染期工作集 ~14 MB。
`ws − priv ≈ 53 MB` 的差额基本是**被映射的字体文件**（雅黑三张 46 MB：regular 18.68 + bold 15.98 +
light 11.61；Noto 两张 4.44；Segoe/Consolas 其余）。实测「把 11 张面全清掉后 priv 降 0.0 MB」——
即这些页是 file-mapped 的**干净共享页**，算在 ws 里、但可以随系统压力被回收，不是泄漏。

### 15.5 保真门（判定「零行为变化」的三道）

1. **端到端 PDF 逐字节**：26 个样例（`benchmarks/PdfBench/spec` 那套）出 PDF + PDFium 栅格首页 +
   PdfPig 抽文本，刀①②③④⑤ 前后 **samples 26 / diffs 0**，且增量段确实生成（`/Outlines`、`/Prev`、
   两个 `%%EOF` 都在）。清理探针后复跑再对一次，仍然 **0 diff**。
2. **子集字节**：Noto×2 / MicrosoftYaHei / SegoeUI / Consolas / Arial 各取 7/60/400 字形集裁剪落盘对拍，
   新旧**仅 `head.checkSumAdjustment` 4 字节不同**（老实现该字段恒 0，从不回填），体积与全部表逐字节一致。
3. **测试（全量，含 Slow）**：498 总计 → **464 通过 / 34 失败**（耗时 33 s）。
   失败项按类归并只有两族：`PngGoldenTests` 17 + `PdfRasterTests` 17。
   在基线上（`git stash push -- src/PaperSharp`）跑同一批得到**同样 34 失败 / 464 通过**，
   且把两侧失败用例名逐条 `diff` **完全一致（0 行差异）**——环境性既有失败，非本轮引入。

   > 顺手把这两族失败的**成因**也量化了（为避免下次再重新怀疑一遍）：
   > 34 个 golden 都是「解码后 RGBA 逐字节全等」判据，本机字体栈与基准生成机不同即红。
   > 用 `goldcheck` 模式对 17 个 PNG 逐像素统计（794 宽 @2x）：
   > - **13 个尺寸完全一致**，差异 2.0%–10.8%（`img` 最高 10.8%），全落在字形边缘 → 纯栅格差；
   > - **3 个高度不同**：`m1` +75 px、`polish` +39 px、`m4` +2 px → 字体度量差异改了行高/换行；
   > - **`m3` 98.8% 像素不同**，取样点 golden 全是 `RGBA(0,0,0,0)`——统计出该基准
   >   **98.3% 的像素 alpha=0**（其余 16 个基准都是 0.0%）。**`goldens/png/m3.png` 本身是张空图**，
   >   属基准损坏，与本轮改动无关，也不该用 `UPDATE_GOLDENS` 顺手覆盖（那会连真实回归一起掩盖）。

### 15.6 后续可选项（都不在关键路径上）

1. **把打包子集配起来**（收益最大、零代码）：`FontManager.BundledFontDirectory` 指到
   `assets/fonts`，中文的 priv 直接少 ~20 MB、吞吐快 35%。本轮给 `MemStress` 加了
   `--bundled <dir>` 就是为了让这条口径可复现——**默认不启用**，因为「宿主没配字体」是更常见的
   真实场景，也是用户报 300MB 的那个场景。
2. **`walk` 那 24 MB 分配**：AngleSharp 每元素一张全量长手属性表 + 折算，已是 §14.2 结论里
   「自研层叠只剩依赖收敛动机」的那块常数。要动就得按 §14.3 的分账实验先定案。
3. **Skia 缓存主动回收**（`SKGraphics.PurgeCaches`）：常驻服务在每 N 份后调一次可压 ws，
   但会牺牲重复渲染复用，未做。
