# Playground 渲染提速 · 交接清单

> 场景：Playground（Blazor Server）里改一下 HTML/CSS 就整页重渲染，用户反馈「有点慢」。
> 本轮已落地的部分见 commit `4cf7b4d`（已推送 origin/master）。下面是**没做完的**，按优先级排。
>
> 数据口径：同一套 harness（`src/PaperSharpSamples/Benchmark.cs` 的 `Playground`，即 `dotnet run -- pgstage <iters> [样例…]`）、
> 同一台机器、与 Playground 同参数（宽 1284 / scale 2 / domPath 开），取 **e2e 中位数**；
> 「前」= `f8e94d2`（未优化的 HEAD 工作树 `D:/Github/ps-base`），「后」= 本轮改动。
> 原始输出已入库：`src/PaperSharpSamples/pgstage_20260924_head.csv` / `..._work.csv`（22 样例 × 9 次迭代）；层叠两刀的「前/后」对拍另存 `pgstage_20260924_eager.csv` / `..._cached.csv`（同一 harness 连跑，「前」= 已含物化那刀的工作树）。

## 已完成（结论与证据，供接手人省事，避免重复踩）

| 杠杆 | 结果 | 证据 |
|---|---|---|
| 自研并行 PNG 编码器 `src/PaperSharp/Imaging/PngEncoder.cs` | 编码合计 **4752 → 339ms（14×）**；全语料 e2e **6761 → 2471ms（2.74×）** | `PngEncoderTests`（与 Skia 逐像素相同、分块不改变像素、rowBytes 必须等于 stride）+ `PngGoldenTests` 全绿，22 样例无需重录基准 |
| `AuthorBoxSpacing` 八条边一趟裁决 | CSS 重的样例略优（m1 buildDoc 323→306ms），聚合持平 | 22 样例引擎盒矩形与 HEAD **逐字节**相同（`tools/selfdiff.sh` → `identical=22 differing=0`） |
| 编码器缓冲改走 `ArrayPool` + ≤1MB strip | 每轮渲染的托管垃圾从几十 MB 降到近零 | 见 `PngEncoder.StripBytes` 注释；像素测试仍全绿 |
| Playground 交互侧 | 去抖 450→250ms；同内容不再重复渲染；页脚把「引擎 ms / 浏览器量测 ms」分开显示 | `Components/Pages/Playground.razor` |
| **作者 CSS 层叠超线性消掉** `src/PaperSharp/Css/EagerStyleCollection.cs`（⚠️ **未提交**，工作树里） | 净层叠同进程对拍 **19.2×–25.0×**；`scale` 引擎入口 table 2000 行 **74.4s → 5.29s**、flexlist 21.3s → 2.63s；Playground 22 样例 e2e **2471 → 2091ms（−15%）** | `pgstyle` 探针 E 行：逐属性 **57,058 次比对 0 不一致**；`boxes flex` 41 盒 JSON 0 差异；31 样例 PNG **像素 0 差异**（唯 `_reph.png` 字节不同而 `getbbox()=None`）。全表与机制见 PERF_REPORT §5.5，原始输出 `pgstyle_20260924.csv` |
| **层叠结果按元素缓存 + `ReadStyle` 双轮并一轮**（同一个 `EagerStyleCollection`，⚠️ 未提交；PERF_REPORT §5.6） | `pgstage 9` 全语料 buildDoc **1,701 → 964ms（1.76×）**、e2e **2,893 → 2,001ms（1.45×）**（pricing-mid 2.58×、pricing-foot 2.11×、m1 1.59×）；`pgstyle` H 行比 F 行低 **7.6–13.5×**（table 500 档到 G 行下界附近） | `pgstyle` H-check：**22 真实样例 × {宿主,::before,::after} × {computed,raw} × 94 属性 = 1,274,452 次比对 0 不一致**（双侧同抛的 16 条跳过、单侧抛计不一致）；非 golden 套件 212/212 全绿。原始输出 `pgstyle_20260924_H.csv`、`pgstage_20260924_eager.csv` / `..._cached.csv`。**golden 套件此刻 30 失败均不归此改动**（15 个旧账 + 15 个 BoxGolden/Png/Pdf + `InlineImageShrinkWrapTests`，逐条落在对端未提交的 `InlineLayout.cs`/`LayoutBuilder.cs` 的 inline-block 基线改动上；层叠输入已被 H-check 证逐条相同 → 按约定不重录 golden，等对端提交后全量重跑） |
| WASM 宿主显式关掉自研 PNG 快路（`Api` 静态构造 `UseFastPng=false`） | 同一快照夹具生产参数 **3.38s → 2.53s**（快路在单线程 + Mono 解释器下是**负收益**） | 产物与 9-23 那份 wasm **SHA-1 一字不差**（`0e2e935d…`）；`node extension/test/bench-snapshot.mjs [--author-css]` |
| **每元素属性记忆化视图 `PropCache`**（`StyleResolver.ReadStyle` 的 `Get`/`Spec` 各包一个惰性 Dictionary 视图，⚠️ 未提交；PERF_REPORT §5.8） | **同树 worktree A/B** `pgstage 9`：buildDoc **1,047 → 928–963ms（−8~−11%）**、e2e **2,173 → ~1,930ms（−11%）**（m1 −18%、pricing-mid/page −17%、m4 −19%）；⚠️ 跨时间段的旧 CSV 对比会显示 0%——当晚对端布局改动自己给 buildDoc 加回约 8%，**这就是「量 PropCache 必须同树 A/B」的实证** | worktree 两臂 22 样例 `boxes` JSON **22/22 字节全等**；非 golden 套件 189/203，14 失败全为对端在途归属（含 23:24 那轮 InlineLayout 新掀动的 img/m1/m5/m9 四个 PngGolden）。原始输出 `wt_pgstage_20260924_prepropcache.csv` / `wt_pgstage_20260924_propcache.csv` |

两条**负面结论**（同样是资产，别再试第二遍）：

1. **「按元素跳过第二轮层叠 `GetDeclarations`」不成立。** 设过闸门后实测：闸门只放行 3/505 个元素时，`GetDeclarations` 调用数仍是 505（与不设闸门时一样），buildDoc 耗时不变。原因是 AngleSharp 的 computed **对未声明的属性直接返回空串**（不补初始值），于是 `Raw`/`Val` 的「computed 空 → 退声明值」兜底必然再问一次。实现已整体回退，一行不剩。**⚠️ 09-24 后续更正**：被推翻的只是「**无缓存**时按元素启发式跳过」这个形状；「双轮并一轮」本身在**按元素缓存**下成立（raw 轮直接命中 computed 轮缓存的折算前声明，问过的必然命中）——已随 §5.6 落地，别再把两者混为一谈。
2. **margin 的 em 不能读 computed。** AngleSharp 折 `em` 用的是**渲染设备基线 16px**，不是元素自己的字号：m1 的 `.note{font-size:10pt}` 里 UA 的 `ul,ol{margin:1em 0}` 会被折成 16px 而非 13.333px，每条列表项凭空下坠 2.667px。已钉成单测 `AuthorMarginCascadeTests.UaMarginInEm_usesElementFontSize_notDeviceBase`，别把 `ReadStyle.MarginVal` 「简化」成只读 computed。

## 未完成

### P1 · ~~PERF_REPORT.md 还没写本轮内容~~ ✅ 已完成（2026-09-24）

已按下面四条要求落进 `PERF_REPORT.md`：新增 **§6 Playground 路径**（6.1 逐样例对照表 + 两份 CSV 引用、6.2 瓶颈翻转占比、6.3 编码器设计要点、6.4 两条负面结论、6.5 已否决方案、6.6 未定性疑点、6.7 全语料编码器选型表），原 §6–§9 顺延为 §7–§10；**§0.3 与 §5.1 的「层叠是第一瓶颈」已改写成「像素数 vs 元素数」两区间口径**；§8 建议与 §9 局限同步。README 的「性能」小节与「AngleSharp 坑位备忘」（补了 2 条本轮实测）、ROADMAP M6 的**收益门槛**一并改成新口径。

复核时顺手纠正的三处**旧数字错误**（都带实测凭据，别改回去）：

| 位置 | 原文 | 实测 |
|---|---|---|
| `PngEncoder.cs` 类注释 | m1 `SKImage.Encode(Png,100)` 2224ms | `pgenc 7 m1` **732ms 中位**；pgstage HEAD 分段 min 628ms。2224 复现不出来，已按实测改写 |
| `PngEncoder.cs` `MaxChunks` 注释 | 「m1 单线程 642ms → 8 块 601ms，差别不在核数」 | 并行才是收益本体：`pgenc 5` 全语料同滤波同档位 **单线程 1563ms → 16 块 263ms（6×）** |
| `PngEncoder.cs` 类注释 | 自研比 Skia「3–8× 快」 | 全语料合计 **19×**（5068ms → 263ms），且产物还更小（5891KB vs 6643KB） |

**本轮新查出的一条候选（未动代码）**：`pgenc 5` 跑完 22 样例，`None`+Optimal 比在用的 `Up`+Optimal **快 15%（225 vs 263ms）而体积打平**。这类平色页面 Up 滤波不省字节却多跑一趟减法。但编码段只占 e2e 14%，改它 ≈ 全链路 2%，**且要重录全部 golden 字节**——排在 M6 之后，详见 PERF_REPORT §6.7。

### P2 · 一个没定性的疑点：小样例 buildDoc 变慢

（已写进 PERF_REPORT §6.6，此处保留动作与判据）

`img` 的 buildDoc 在完整流水线里 12ms(HEAD) → 29~41ms(本轮)，但**纯 buildDoc 循环**（`dotnet run -- pgbuild 15 img`）两树都是 43~49ms，且 `long`/`m5` 只差 1~3ms。两个口径自相矛盾，说明 pgstage 的**分段计时被跨轮 GC / 计时起点污染**，现在还不能断定这是真回归——但也不能当没有。

**补一条本轮复核**：这不是 img 一家的事——22 样例的 buildDoc **列合计** 1447 → 1544ms（+6.7%），paint 328 → 356ms、measure+position 42 → 49ms 也同向微涨。本轮一行层叠代码都没改（除 `AuthorBoxSpacing` 的八边一趟，而 m1 反而快了 17ms），所以这一片普涨高度符合「上一轮的 GC 垃圾记到下一轮的段里」这一个共同因。**别把它当噪声放过**：它同时意味着 §6 里 10ms 量级的分段差全都没有分辨力。

- 影响面：只有 e2e 仍变快的小样例（img 122→57ms、flex 154→69ms），不是用户可感知的退化，所以先搁置。
- 要定性，需要能出 GC 暂停归因的工具，**别再靠 Stopwatch 分段**：BenchmarkDotNet（`[GcServer]`/`[MemoryDiagnoser]`）或 `dotnet-counters monitor --counters System.Runtime`（看 `time-in-gc` 与 gen0 次数）。判据：把 `Renderer.UseFastPng` 关掉跑同一 harness，若 img 的 buildDoc 回到 ~12ms，就是编码器残留的分配路径（`deflated[c]` 与最终 `MemoryStream.ToArray()` 还没池化）。
- 待查的第二嫌疑：`SKSurface.Create` 每轮 13~85MB 非托管分配是否间接触发托管堆回收。

### P3 · Playground 真实链路从没端到端量过

本轮所有数字都来自控制台 harness。**没人在浏览器里量过一次**：SignalR 往返、`/api/playground/image/{token}` 取图、iframe `srcdoc` 装载 + settle + 回传矩形（`tools/boxdiff.mjs` 量过同类形状，含浏览器冷启约 1.1~1.9s/次）。页脚的「浏览器量测 ms」是这一轮才加的，**需要跑一次看真实值**。

- 具体动作：`dotnet run --project src/PaperSharpBlazorApp` → 打开 `/playground` → 选 m1 / pricing-page → 改一个字符，记页脚两个数。
- 如果「浏览器」那一半和引擎那一半同量级（几百 ms），就值得做**两半并行**：现在 `RenderOnceAsync` 里 `Svc.RenderAsync` 与 `loadPreview` 是串行的。做法是先用一个只解析 @page 的小helper 算出 ContentWidth（口径必须与 `Renderer.RasterLayout` 同源，否则两侧排版宽不等会造出整幅假漂移，`pct` 样例实测过 91px 这个坑），再让引擎的 `Task.Run` 与 JS 往返重叠。**注意**：图先出、diff 后出这个渐进体验别丢，改并行时要保住。
- 顺带决定：Playground 的 scale 是否需要跟着 `devicePixelRatio`。盒矩形是 CSS px、与 scale 无关，只有肉眼叠图需要高分辨率；现在恒 2× 意味着 pricing-page 一次 27.9 Mpx。

### P4 · 候选但未取证的引擎侧优化

- **✅ 已取证并推翻、且已修（本轮 09-24）：WASM 上自研 PNG 编码器是负收益，已在 WASM 宿主显式关掉。** 我原先推测「§5.4 那 3.0s 里编码占 74–76%，换上新编码器能白捡 3×」——**实测相反**：同一份快照夹具、与生产同参数（`precomputed:true`、宽 1406、Node `renderRaw`），开快路 **3.38s** vs `UseFastPng=false` 的 **2.53s**（s2 档 8.74s vs 5.96s），**慢 1.3–1.5×**；像素两侧 `94,826,264` 字节 0 差异，块结构确认换了（Skia 的 `sBIT`+17×8192B `IDAT` → 单块 132,526B）。机制：.NET 侧那份收益 **绝大部分来自并行**（§6.7 单线程档只剩 3.2×），WASM 单线程 + Mono 解释器里逐字节 managed 滤波与 managed `DeflateStream` 打不过 Skia 的 native zlib。
  - **动作（✅ 已落）**：`src/PaperSharp.Wasm/Api.cs` 静态构造里 `Renderer.UseFastPng = false;`，native 宿主（Playground / 服务端）保持默认开。已 `node extension/build.mjs --publish` 重编并复量两遍：**扩展产物 PNG 与 9-23 那份 wasm 的输出 SHA-1 一字不差**（`0e2e935d…`），耗时回到 2.7–2.9s / 6.5s 档。
  - **量法（已修）**：`extension/test/bench-snapshot.mjs` 原先**没传 `precomputed`**，量的是作者 CSS 全量层叠那条，跟扩展真实路径差一个数量级。现在它默认走生产参数，`--author-css` 切回旧口径；同一夹具两档实测 3,965ms vs 31,404ms（8×）。参数以 `extension/offscreen.js:90-102` 为准，别在驱动里漏 `precomputed`。
  - **顺带一笔未定性**：与 PNG 无关的 PDF 臂也从 1.37s 涨到 **1.63–1.97s**（9-23→9-24 之间布局/绘制改了 1,900+ 行；同一台机器不同时刻漂了 ±20%，所以这条要先解决计时分辨力才能定性，与 P2 同源）。快照这条路上是否也吃到了 Playground 那批改动带来的回归，值得单独对拍一次（`tools/SnapshotProfile` 或同一夹具跑 `pdf` 档）。
- **表格 `colMin` / 最小内容宽度惰性化**：一直怀疑，从未测到它在热点上。规矩照旧——先埋可证伪探针取证（`pgbuild` 那类分段行不够，要能区分 measure 里的表格列宽计算），再动代码。
- **按 display 类型少读属性**：现在每元素读约 94 条 computed。**⚠️ 09-24 实测更新（PERF_REPORT §5.7）**：`pgbuild` 拆解探针量到这段在真实语料上是 **385ms/22 样例 = buildDoc 的 31%**（m1：79.6ms/510 元素 ≈ 1.66µs/条、14.3MB/轮分配），旧注释里「收益约 15ms/510」的估算作废。优先试**更安全的同目标打法**：每元素把折算后的声明集合**一趟枚举建成 `name→value` 字典**（94 次查找摊成 1 次枚举），门用 H-check 同形状对账（简写展开/自定义属性语义须逐串一致）；跳读方案本身回归面大（`ComputedStyle` 几十个「不读=默认」字段未必等于浏览器初值）。
- **✅ 已取证并已修（本轮 09-24）：作者 CSS 路径的层叠超线性。** 这一档原先记成「M6 自研级联替换 AngleSharp，量级最大但要另开里程碑」；实测**不需要自研**：AngleSharp 的样式表集合是未物化 LINQ，且每层叠一个元素会对该元素**每个祖先各枚举一次** → 每元素 O(深度 × 文档大小)。物化一次即拿到 19–25×（机制、探针 A/B/C/D/F/G、四道正确性门全在 PERF_REPORT §5.5）。
  - **踩过的弯路值得记**：我最初的假设是「把 `GetStyleCollection()` 外提到循环外」，探针 B 行证明它**等于没做**（B ≈ A，0.75–1.06×）；必须**物化**。
  - **✅ 同一探针里剩下的两刀已落地（同日第二刀，PERF_REPORT §5.6）**：(a) 按元素缓存 `ComputeCascadedStyle` 递推 + (b) 双轮并一轮，一起做掉——H 行比 F 行低 **7.6–13.5×**，引擎 `pgstage 9` buildDoc **1.76×**、e2e **1.45×**，门是 1,274,452 次逐属性比对 0 不一致。**层叠这条路的性能理由至此用尽**：buildDoc 剩下的（964ms、占 e2e 48%）是每元素一次值折算 + ~94 属性读 + 建树的固定成本，再往下要么自研级联（只剩依赖收敛诉求）、要么走 §8.5/§8.6 那两条非层叠杠杆。
  - **未定性的一条留着**：物化之后 table 1000→2000 行 F 行仍从 66.5 涨到 103.4 µs/元素，即每元素一轮里还有随规模走的成分（怀疑是每祖先一次全表匹配，与 (a) 同源），没单独取证。

### P5 · 环境与工具收尾 —— ✅ 已核实：一次性环境已随仓库迁盘消失（2026-09-24 复核）

本清单原先写的 `D:/Github/ps-base`（HEAD 对拍工作树，里面塞过改过的 harness 副本）、`D:/Github/boxdump-head`、`D:/Github/boxdump-work`、`D:/Github/StyleResolver.mine.bak` **都已不存在**：`ls /d/` 无 `Github` 目录，`git worktree list` 只剩主树 `G:/Github/papersharp d3c669c [master]`。仓库现在在 G: 盘，D: 那套路径整体没了——**没有需要删的东西了**，也别再去找那份「干净 HEAD 引用」。

- 遗留影响一条：`tools/selfdiff.sh` 两行 `run_in` 的目标**全写在已经不存在的 D: 上**（第 19 行 `D:/Github/ps-base` 当基线、第 20 行 `D:/Github/paper-sharp` 当工作树），现在跑必失败。要用「引擎盒矩形逐字节对拍」这道硬门，得把第 20 行改回当前仓库根、并按需 `git worktree add ../ps-base <baseline-sha>`，再把当前 `src/PaperSharpSamples/` 的探针文件同步过去（否则两边量的不是同一件事）。
- 顺带：`src/PaperSharpSamples/Program.cs` 里 `goldcheck` 的注释仍写着「跑完即可删除本模式」，与「五个探针长期保留」的决定相反，留待下次清理。
- 另记一条环境事实：基准与测试**不要同跑**。本轮与 `pgenc` 同跑时 `PseudoElementTests.No_pseudo_rules_leaves_output_unchanged` 失败（PdfPig 抽出 `"\0\0\0\0\0"` 而非 `"plain"`），单独跑与随后两次净跑均 220/220 全绿（PERF_REPORT §9）。

## 复现清单

```bash
# 全样例分段 + e2e（Playground 口径）
dotnet run -c Release --project src/PaperSharpSamples -- pgstage 9

# 引擎盒矩形与 HEAD 逐字节对拍（改布局/层叠后的硬门）
bash tools/selfdiff.sh                      # 全样例；也可跟样例名

# 回归门（⚠️ 09-24 复核：该树上有并发改动时**不是**全绿，见下面的归属说明）
dotnet test tests/PaperSharp.Tests/PaperSharp.Tests.csproj -c Release

# 层叠超线性来源 + 改动前后逐属性对账（§5.5 的 A/B/C/D/F/G 与 E 门即此）
dotnet run -c Release --project src/PaperSharpSamples -- pgstyle 1000 2000   # ⚠️ A/B 两行量的仍是改前语义，2000 档一次 20+ s
dotnet run -c Release --project src/PaperSharpSamples -- scale 2000          # 引擎真实入口的规模档（默认 100 200 400 800）

# 与浏览器对拍（精度成果别丢：聚合漂移面积 22254、全绿盒 557/2021）
bash tools/sweep.sh && node tools/compare.mjs <before.txt> <after.txt>

# 扩展 / WASM 这条（§6.8 的数字即来自此）；换分支后必须先重编，否则量的是旧产物
node extension/build.mjs --publish
node extension/test/bench-snapshot.mjs              # 生产同参数（precomputed）
node extension/test/bench-snapshot.mjs --author-css # 作者手写 CSS 那条全量层叠（慢 8×）
```

判回归前先 `git status` 确认没有别人的并发改动（基准哈希与页数是事实，会被来回翻）。

**09-24 的一起归属实例（别重复踩）**：层叠物化改动接上后 `dotnet test` 报 **8 失败**（`flex`/`m4`/`pct` × PngGolden/BoxGolden/PdfRaster）。把工作树里那一行翻回**改前**语义再跑，失败**变成 9 个**、第三次 **11 个**（多出 `m3`/`pricing-foot`/`pricing-page`）——失败数随对端未提交的 `LayoutBuilder.cs`/`Fixture.cs`/新增 `BoxGoldenTests.cs`+`goldens/boxes/` 漂移，**与层叠改动无关**。所以当时正确性靠的是上面那三道自建门（57,058 次逐属性、盒 JSON、PNG 像素），**没有 `UPDATE_GOLDENS`**；等对端提交后再跑一次全量并归档结果。
