# Playground 渲染提速 · 交接清单

> 场景：Playground（Blazor Server）里改一下 HTML/CSS 就整页重渲染，用户反馈「有点慢」。
> 本轮已落地的部分见 commit `4cf7b4d`（已推送 origin/master）。下面是**没做完的**，按优先级排。
>
> 数据口径：同一套 harness（`src/PaperSharpSamples/Benchmark.cs` 的 `Playground`，即 `dotnet run -- pgstage <iters> [样例…]`）、
> 同一台机器、与 Playground 同参数（宽 1284 / scale 2 / domPath 开），取 **e2e 中位数**；
> 「前」= `f8e94d2`（未优化的 HEAD 工作树 `D:/Github/ps-base`），「后」= 本轮改动。
> 原始输出已入库：`src/PaperSharpSamples/pgstage_20260924_head.csv` / `..._work.csv`（22 样例 × 9 次迭代）。

## 已完成（结论与证据，供接手人省事，避免重复踩）

| 杠杆 | 结果 | 证据 |
|---|---|---|
| 自研并行 PNG 编码器 `src/PaperSharp/Imaging/PngEncoder.cs` | 编码合计 **4752 → 339ms（14×）**；全语料 e2e **6761 → 2471ms（2.74×）** | `PngEncoderTests`（与 Skia 逐像素相同、分块不改变像素、rowBytes 必须等于 stride）+ `PngGoldenTests` 全绿，22 样例无需重录基准 |
| `AuthorBoxSpacing` 八条边一趟裁决 | CSS 重的样例略优（m1 buildDoc 323→306ms），聚合持平 | 22 样例引擎盒矩形与 HEAD **逐字节**相同（`tools/selfdiff.sh` → `identical=22 differing=0`） |
| 编码器缓冲改走 `ArrayPool` + ≤1MB strip | 每轮渲染的托管垃圾从几十 MB 降到近零 | 见 `PngEncoder.StripBytes` 注释；像素测试仍全绿 |
| Playground 交互侧 | 去抖 450→250ms；同内容不再重复渲染；页脚把「引擎 ms / 浏览器量测 ms」分开显示 | `Components/Pages/Playground.razor` |

两条**负面结论**（同样是资产，别再试第二遍）：

1. **「按元素跳过第二轮层叠 `GetDeclarations`」不成立。** 设过闸门后实测：闸门只放行 3/505 个元素时，`GetDeclarations` 调用数仍是 505（与不设闸门时一样），buildDoc 耗时不变。原因是 AngleSharp 的 computed **对未声明的属性直接返回空串**（不补初始值），于是 `Raw`/`Val` 的「computed 空 → 退声明值」兜底必然再问一次。实现已整体回退，一行不剩。
2. **margin 的 em 不能读 computed。** AngleSharp 折 `em` 用的是**渲染设备基线 16px**，不是元素自己的字号：m1 的 `.note{font-size:10pt}` 里 UA 的 `ul,ol{margin:1em 0}` 会被折成 16px 而非 13.333px，每条列表项凭空下坠 2.667px。已钉成单测 `AuthorMarginCascadeTests.UaMarginInEm_usesElementFontSize_notDeviceBase`，别把 `ReadStyle.MarginVal` 「简化」成只读 computed。

## 未完成

### P1 · PERF_REPORT.md 还没写本轮内容（**下一次动手前先做这个**）

`PERF_REPORT.md` 的数据停在 2026-09-22，且 §0.3 / §5 的定性已被本轮实测部分推翻，需要改：

- 新增一节 Playground 路径：上面那张对照表 + 两份 CSV 的引用；编码器设计要点（固定 Up 滤波、按行分块 + `Z_SYNC_FLUSH` 拼流、`adler32_combine`、块数写死 8 而非跟核数走的**字节确定性**理由）。
- **§0.3「buildDoc 超线性 ~O(n^2.5)，上千元素时 CSS 层叠是第一瓶颈」需要复核口径**：那是 `bench` 里净 buildDoc 循环的结论，本轮在 Playground 完整流水线里 m1（501 盒 / 510 元素）的 buildDoc 只有 ~320ms，而 HEAD 的编码是 628ms——「谁是第一瓶颈」取决于产物尺寸（像素数）与元素数两个变量，报告里要写清楚各自适用的区间，别再笼统断言。
- 补两条负面结论（上面那两条），以及「已否决的方案」清单：PNG quality 调档（Skia 对 PNG 的 quality 是空操作，q=10…100 输出一字不差）、JPEG/WebP 预览（比对口径要逐像素）、画布/`SKSurface` 池（`pgalloc` 实测 create≈0ms、fresh-clear 20.9 == reuse-clear 20.9，只有整条 e2e 复用才省，代价是常驻 85MB → 决定不做）。

### P2 · 一个没定性的疑点：小样例 buildDoc 变慢

`img` 的 buildDoc 在完整流水线里 12ms(HEAD) → 29~41ms(本轮)，但**纯 buildDoc 循环**（`dotnet run -- pgbuild 15 img`）两树都是 43~49ms，且 `long`/`m5` 只差 1~3ms。两个口径自相矛盾，说明 pgstage 的**分段计时被跨轮 GC / 计时起点污染**，现在还不能断定这是真回归——但也不能当没有。

- 影响面：只有 e2e 仍变快的小样例（img 122→57ms、flex 154→69ms），不是用户可感知的退化，所以先搁置。
- 要定性，需要能出 GC 暂停归因的工具，**别再靠 Stopwatch 分段**：BenchmarkDotNet（`[GcServer]`/`[MemoryDiagnoser]`）或 `dotnet-counters monitor --counters System.Runtime`（看 `time-in-gc` 与 gen0 次数）。判据：把 `Renderer.UseFastPng` 关掉跑同一 harness，若 img 的 buildDoc 回到 ~12ms，就是编码器残留的分配路径（`deflated[c]` 与最终 `MemoryStream.ToArray()` 还没池化）。
- 待查的第二嫌疑：`SKSurface.Create` 每轮 13~85MB 非托管分配是否间接触发托管堆回收。

### P3 · Playground 真实链路从没端到端量过

本轮所有数字都来自控制台 harness。**没人在浏览器里量过一次**：SignalR 往返、`/api/playground/image/{token}` 取图、iframe `srcdoc` 装载 + settle + 回传矩形（`tools/boxdiff.mjs` 量过同类形状，含浏览器冷启约 1.1~1.9s/次）。页脚的「浏览器量测 ms」是这一轮才加的，**需要跑一次看真实值**。

- 具体动作：`dotnet run --project src/PaperSharpBlazorApp` → 打开 `/playground` → 选 m1 / pricing-page → 改一个字符，记页脚两个数。
- 如果「浏览器」那一半和引擎那一半同量级（几百 ms），就值得做**两半并行**：现在 `RenderOnceAsync` 里 `Svc.RenderAsync` 与 `loadPreview` 是串行的。做法是先用一个只解析 @page 的小helper 算出 ContentWidth（口径必须与 `Renderer.RasterLayout` 同源，否则两侧排版宽不等会造出整幅假漂移，`pct` 样例实测过 91px 这个坑），再让引擎的 `Task.Run` 与 JS 往返重叠。**注意**：图先出、diff 后出这个渐进体验别丢，改并行时要保住。
- 顺带决定：Playground 的 scale 是否需要跟着 `devicePixelRatio`。盒矩形是 CSS px、与 scale 无关，只有肉眼叠图需要高分辨率；现在恒 2× 意味着 pricing-page 一次 27.9 Mpx。

### P4 · 候选但未取证的引擎侧优化

- **表格 `colMin` / 最小内容宽度惰性化**：一直怀疑，从未测到它在热点上。规矩照旧——先埋可证伪探针取证（`pgbuild` 那类分段行不够，要能区分 measure 里的表格列宽计算），再动代码。
- **按 display 类型少读属性**：现在每元素读约 94 条 computed（`props-94x-once` 实测 ~1.1µs/条）。风险在于 `ComputedStyle` 有几十个字段是「不读=取默认」，而默认值未必等于浏览器初始值（例如 `AlignItems` 未设时布局要按 stretch 处理），要逐字段审，回归面大、收益约 15ms/510 元素，**没排上**。
- M6 自研级联替换 AngleSharp：仍是量级最大的那一档，但那是另一个里程碑。

### P5 · 环境与工具收尾

- `D:/Github/ps-base` 是 HEAD 的对拍工作树，里面塞了我改过的 harness 副本（`Benchmark.cs`/`Program.cs`/`CascadeProbe.cs`/`GoldenPixelCheck.cs`，并把 `EncodeProbe.cs` 改成了空壳）。**它不该被当成干净 HEAD 引用**。留着可继续做 A/B；不要了就 `git -C D:/Github/paper-sharp worktree remove ../ps-base --force`。
- 仓库外的一次性文件：`D:/Github/boxdump-head`、`D:/Github/boxdump-work`（`tools/selfdiff.sh` 的产物目录）、`D:/Github/StyleResolver.mine.bak`（回退 lever-3 时的备份，已无用）。
- `tools/selfdiff.sh` 是本轮新加的对拍工具（两棵树跑 `boxes <样例>` 再 `cmp` JSON），已入库；用法见文件头注释。注意它假设 HEAD 工作树在 `../ps-base`，路径写死在两处 `run_in` 调用里。
- samples 里新增的 `pgstage` / `pgenc` / `pgbuild` / `pgalloc` / `goldcheck` 五个模式建议**长期保留**（都是取证入口，不进生产代码），报告里应逐个给一行说明。

## 复现清单

```bash
# 全样例分段 + e2e（Playground 口径）
dotnet run -c Release --project src/PaperSharpSamples -- pgstage 9

# 引擎盒矩形与 HEAD 逐字节对拍（改布局/层叠后的硬门）
bash tools/selfdiff.sh                      # 全样例；也可跟样例名

# 回归门（当前 220/220 全绿，无需 UPDATE_GOLDENS）
dotnet test tests/PaperSharp.Tests/PaperSharp.Tests.csproj -c Release

# 与浏览器对拍（精度成果别丢：聚合漂移面积 22254、全绿盒 557/2021）
bash tools/sweep.sh && node tools/compare.mjs <before.txt> <after.txt>
```

判回归前先 `git status` 确认没有别人的并发改动（基准哈希与页数是事实，会被来回翻）。
