# CiBench · 可进 CI 的四方 PDF 性能基准

**PaperSharp / PuppeteerSharp / Microsoft.Playwright / iText 7 pdfHTML** 在同一份语料上的
冷启动、热启动与并发导出横向对比，一条命令出**自包含 HTML 图表报告 + CSV/JSON/Markdown**，
无需 Python、无需图表 CDN、CI artifact 离线可看。四家都不需要 license key，CI 里一定能凑齐四方数据。

> ⚠️ **授权提醒**：iText 7 Core / pdfHTML 是 **AGPLv3 / 商业双授权**，免费可用但带 copyleft 义务。
> 本仓库只把它作为基准工具的**被测依赖**；若要在闭源产品中分发或用于网络服务（SaaS），
> 须遵守 AGPL 开源义务或购买商业授权。不想引入该依赖，用 `-p:IncludeIText=false` 重编译即可彻底移除。

> 与 [`../PdfBench`](../PdfBench/README.md) 的分工：PdfBench 是「.NET 里谁能出 PDF」的八家横向（含还原度、
> CSS 特性矩阵、规模扫描）；CiBench 只做**四家 HTML→PDF 引擎在 CI 里的性能回归**，口径固定、运行快、
> 有退出码与 job summary。两边数字口径不同，不要跨文档相减。

## 快速开始

```bash
# 全量（默认）：冷/热单张各测 20 次取平均；并发 100 / 200 / 300 张
bash benchmarks/CiBench/run.sh

# Windows PowerShell
.\benchmarks\CiBench\run.ps1

# PR 冒烟档：5 次重复 + 100/200 批次
bash benchmarks/CiBench/run.sh --quick

# 只跑两家、自定义档位
bash benchmarks/CiBench/run.sh --engines papersharp,puppeteer --repeats 10 --batches 50,100 --concurrency 4
```

产物在 `benchmarks/CiBench/out/`（已 gitignore）：

| 文件 | 用途 |
|---|---|
| `report.html` | **主交付**：8 张内嵌 SVG 图表 + 全指标表格 + 状态/口径说明，零外部依赖 |
| `results.json` | 机器可读全量数据，可作 `--baseline` 回归基线 |
| `results.csv` | 长表 `engine,phase,batch,metric,value,unit,status`，便于导入表格/时序库 |
| `summary.md` | Markdown 摘要，CI 里自动追加到 `GITHUB_STEP_SUMMARY` |

## 四家库与前置条件

| 引擎 | 渲染内核 | 授权 | 前置条件 |
|---|---|---|---|
| PaperSharp | 纯托管 CSS 排版 + Skia（无浏览器） | 仓库自有 | `src/PaperSharp` 项目引用 |
| PuppeteerSharp 25.8 | 外部 Chromium（CDP 直连） | Apache-2.0，免费 | `PUPPETEER_EXECUTABLE_PATH` 或系统 Chrome/Edge；都没有时自动下载 |
| Microsoft.Playwright 1.64.1 | Chromium（Playwright driver + 外部 Chrome） | Apache-2.0，免费 | 与 PuppeteerSharp **共用同一个系统 Chrome**；都没有时回退到自带 Chromium |
| iText 7 pdfHTML 6.3.4（Core 9.8.0） | 解析型 HTML/CSS 布局（纯托管，无浏览器） | **AGPLv3 / 商业双授权** | 系统需有 CJK 字体（中文语料）；CI 自动补装 `fonts-noto-cjk` |

```bash
export PUPPETEER_EXECUTABLE_PATH=/usr/bin/google-chrome   # 两家 Chromium 引擎共用
```

**选型说明**：原第三家是商业库 IronPdf，缺 `IRONPDF_LICENSE_KEY` 时只能 `skipped`，CI 报告长期只有两家。
换成 Microsoft.Playwright 的理由：微软维护、Apache-2.0、可商用无限制、跨平台、NuGet 用户量大且生态活跃；
对比 wkhtmltopdf 已于 2023 年停止维护、DinkToPdf / Rotativa 只是它的封装，Playwright 在 CI 可维护性和用户量上都明显更好。
第四家 iText pdfHTML 则是**完全不同的一类**：不启动浏览器，自己解析 HTML/CSS 再用 iText Layout 排版，
用来和纯托管自绘的 PaperSharp 形成「同为无浏览器、但排版路径不同」的对照。

- **Windows 注意**：不要用 `chrome --version` 探测版本——Windows 上它会拉起完整浏览器并常驻。
  本项目只读 PE 文件版本元数据（`ChromeLocator.cs`），既不卡住也不污染进程树 RSS。
- Playwright 在 Linux CI 上优先复用 runner 预装 Chrome；只有找不到时才由 workflow 执行
  `playwright.sh install chromium` 兜底，避免每次多下 ~400MB。

## iText 的授权与口径（用之前先读）

- **授权**：`itext7.pdfhtml` / `itext.pdfhtml` / `itext` 均为 AGPLv3（或商业授权）且 `requireLicenseAcceptance=true`。
  免费可用，但 AGPL 有「披露全部源码」「网络服务也触发」等义务。本仓库仅在基准工具里引用，
  **不随 PaperSharp 的 NuGet 包分发**（`src/PaperSharp` 未引用 iText）。
  不想让这个依赖出现在构建里：`dotnet build ... -p:IncludeIText=false`，此时 `itext` 引擎会给出明确 `skipped` 原因。
- **口径**：pdfHTML 的 CSS 支持面比 Blink 窄（不支持 flex/grid 等），所以**耗时数字不代表视觉还原度**，
  产物体积也不能拿来和其他三家比「谁画得更全」。要看还原度请用 `../PdfBench/fidelity`。
- **中文**：`ITextEngine` 进程级探测一个可加载的 CJK 字体（TTC 要用 `路径,索引` 寻址，
  且必须检查 `AddFont` 的返回值——失败时它返回 false 而不抛异常），再用 `FontSet` 的 alias
  把语料声明的 `font-family` 显式指向该字体，每个 worker 独立注册；找不到字体才回退
  `AddSystemFonts()` 全盘扫描。**不这么做 iText 会静默退回 Helvetica、把中文全部丢掉**
  （产物只有 3KB），所以报告里的「平均产物体积」也是发现字体缺失的哨兵指标。
  Linux CI 由 workflow 的 `Install CJK fonts (iText)` 步骤补 `fonts-noto-cjk`。

## 可选：让 IronPdf 参赛

IronPdf 已从默认组合移除，但仍保留为显式可选引擎。它需商业授权（官网提供免费 30 天试用 key，
无需信用卡、发到邮箱），无 key 时 2026.11.x 会先加载随包 Chromium（本机冷启动约 8.7s）再抛
`LicensingException: Production License Required`，连 `--allow-unlicensed-ironpdf` 也拿不到正式产物。
基准绝不拿试用/水印产物凑数。

```bash
# 1) 编译时打开开关（默认 false，避免白还原几百 MB 原生包）
dotnet build benchmarks/CiBench/CiBench.csproj -c Release -p:IncludeIronPdf=true

# 2) 配 key 并显式指定引擎
export IRONPDF_LICENSE_KEY="你的-key"        # CI 里配成同名 Repository Secret
bash benchmarks/CiBench/run.sh --engines papersharp,puppeteer,playwright,itext,ironpdf --strict
```

授权码只从环境变量读，绝不写进源码或 `results.json`。`--strict` 可确认没有任何引擎被跳过
（任一 `skipped`/`failed` 退出码 3）。未打开编译开关就指定 `--engines ironpdf` 会得到明确的
`skipped` 原因，不会静默少跑。

## 测量口径（改代码前必读）

| 项 | 定义 |
|---|---|
| 语料 | `spec/invoice.html`：单页 A4 中文发票，内嵌 CSS、无外部图片/字体/脚本，四家完全一致 |
| 公平性 | 四家都**从同一 HTML 字符串重新排版**；产出 `byte[]` 后由测量内核统一 `File.WriteAllBytes`，**计时含落盘、不含删临时文件**。Chromium 两家每次重新 `SetContent + 导出`，iText 每次重新解析 HTML，不吃「页面已排好版」的缓存红利 |
| PDF 参数 | A4、`printBackground`、四边 0 margin、`preferCSSPageSize`（Chromium 两家）；CSS 内 `@page { size:A4; margin:18mm }`（iText 走 CSS @page） |
| 冷启动 | **每次都是全新子进程**：CLR 启动 + JIT + 字体/浏览器/driver 初始化 + 渲染 + 落盘。父进程用墙钟计时，默认 20 次取平均，同时给 p50/p95/标准差/最慢一次 |
| 热启动 | 常驻进程，先并发预热（每 worker 2 次，不计入），再连续导出单张 20 次逐次计时取平均 |
| 并发批次 | 热态下以 **固定并发 worker 数**（默认 `min(CPU,8)`）消费 N 张队列任务；N = 100/200/300。`平均耗时 = 批次总墙钟 / N`，`吞吐 = 成功数 / 总秒数`。固定并发而非 N 路全开，避免 300 个 Chrome 页面把机器打穿 |
| RSS | **本进程 + 全部后代进程**工作集之和的采样峰值（20~25ms 一轮），Windows 走 Toolhelp32、Linux 走 `/proc/*/stat`；Chrome 与 Playwright driver 子进程树会被完整算进来。峰值取**最近 3 次采样的中位数**：Windows 的 `th32ParentProcessID` 是快照值，Chrome 子进程频繁生灭时 PID 复用可能让某一轮把无关大进程误挂进树里，出现「25ms 涨 3 倍又立刻回落」的假尖峰；中位数只抑制这种单点抖动，持续上升的真实峰值照常记录，抑制次数会打到 stderr |
| 隔离 | 冷启动每次新进程；热态每引擎一个独立子进程，避免上一家原生内存/Skia 字体缓存污染下一家 RSS |
| 失败 | 超时/异常/skipped 一律如实入账，图表显示 `N/A`，不补零不插值。子进程超时会 Kill 整棵进程树 |

## 指标清单

- **冷启动**：平均 / min / p50 / p95 / max / 标准差、成功次数、进程树 RSS 峰值均值、产物体积
- **热启动**：平均 / p50 / p95 / 标准差、每张托管分配、RSS 峰值、GC 次数、产物体积
- **并发批次（每档）**：总墙钟、平均耗时/张、吞吐 docs/s、单张延迟 p50/p95/max、
  进程树 RSS 峰值与结束值、托管分配总量、GC、成功/失败数、平均产物体积
- **派生**：冷/热加速比；`--baseline` 回归门禁

图表共 8 张：冷启动单张耗时、热启动单张耗时（冷热量级差一个数量级，各自独立定标才看得清）、
批次平均耗时、批次 RSS 峰值、批次吞吐、批次 P95 延迟、批次托管分配、批次总墙钟。

## CLI 参数

```
--repeats N            冷/热单张重复次数（默认 20）
--batches A,B,C        并发档（默认 100,200,300）
--concurrency N        固定 worker 数（默认 min(CPU,8)）
--html PATH            语料（默认 spec/invoice.html）
--out DIR              产物目录
--engines A,B          只跑指定引擎（默认 papersharp,puppeteer,playwright,itext；可选 ironpdf）
--quick                = --repeats 5 + --batches 100,200 + 并发≤4
--strict               任一引擎 skipped/failed 即退出码 3
--baseline PATH        与上次 results.json 对比
--max-regression P     允许劣化百分比（默认 15）
--gate-engines A,B     门禁约束的引擎（默认 papersharp，避免第三方库波动弄红 CI）
--allow-unlicensed-ironpdf   仅对可选引擎 ironpdf 生效
--report-only          不重跑测量，用已有 out/results.json 重出报告
-h / --help
```

退出码：`0` 通过 / `2` 参数或顶层异常 / `3` strict 下有引擎缺席 / `4` 回归超阈值。

## CI

workflow：`.github/workflows/pdf-ci-bench.yml`

- `pull_request`（改动 `src/PaperSharp/**`、`benchmarks/CiBench/**`）：`--quick` 冒烟
- `workflow_dispatch`：可选 `quick` / `strict` / `regression_gate`
- `schedule`：每周一 03:00 UTC 跑全量
- 自动 `setup-dotnet 10.0.x`、补装 CJK 字体（iText 用）、复用 runner 预装 Chrome
  （找不到时才装 Playwright Chromium）、上传 `out/` artifact（保留 30 天）、把 `summary.md` 写进 job summary
- 默认四家不需要任何 secret；`IRONPDF_LICENSE_KEY` 只在显式跑可选引擎时才会被读取

手动回归门禁：

```bash
cp benchmarks/CiBench/out/results.json benchmarks/CiBench/baseline.json   # 提交一份可信基线
# 之后 CI 手动触发勾选 regression_gate，或直接：
bash benchmarks/CiBench/run.sh --baseline benchmarks/CiBench/baseline.json --max-regression 15
```

门禁只比较 `cold.meanMs`、`warm.meanMs`、各档 `batchN.avgMsPerDoc`；基线与本次的
`repeats`/`batches` 口径不一致时会跳过，避免拿 quick 档和 full 档硬比。

## 已知局限

- 语料是单页静态发票，回答「模板→PDF 的服务端吞吐」，不回答复杂页面还原度（看 PdfBench 的 SSIM/特性矩阵）。
- 四家的**渲染内核不等价**：浏览器两家跑 Blink，PaperSharp 自绘，iText 解析型布局；
  横比的是「同一语料下各自出一张 PDF 的成本」，不是同一排版结果的实现快慢。
- 并发采用固定 worker 池，不是 N 路同时开火；想测极限并发可调 `--concurrency`，但 300 路 Chrome 极易 OOM。
- PuppeteerSharp 与 Playwright 各共享一个 browser、每 worker 独占一个 page；这是真实服务端形态，
  不是各库的最优理论上限。两者都是 Chromium，差异主要在 driver / 进程管理封装层。
- iText 每个 worker 独占 `FontProvider + ConverterProperties`，未做跨文档缓存复用（与其它引擎口径一致）。
- Playwright 在 Linux runner 上的 driver 依赖、以及 workflow 的 CJK 字体安装步骤，
  尚未在真实 GitHub Actions 上实跑验证（本机为 Windows）；若报缺系统库，
  可在 workflow 里补 `playwright.sh install-deps chromium`。
- 单机单次测量，未做多机分布；CI 机器型号更换会让绝对数字漂移，用 `--baseline` 看相对变化更可靠。
- RSS 是 25ms 间隔的采样值且经 3 点中位平滑，适合容量规划但不等价于精确瞬时峰值；极短促的尖峰会被平滑掉。
