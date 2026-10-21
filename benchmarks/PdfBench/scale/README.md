# 规模扫描 · HTML→PDF 同类引擎横向

**结论看这里**：[`REPORT.md`](REPORT.md) —— 曲线图、逐档数字表、完整性折扣、口径自证与选型建议。

七家横向报告（[`../report/REPORT.md`](../report/REPORT.md)）回答的是「.NET 里谁能出 PDF」，
其中四家必须把设计稿手抄成 C#，和 PaperSharp 不是一条赛道。**这里只比吃 HTML 的引擎**，
而且比的不是单页快慢，是**文档变大之后谁先垮**：同一份语料按五个规模轴各放大到跨 3–4 个数量级，
每家在 warm（常驻进程）与 cold（每次新进程）两个口径下各测一遍。

## 赛道选手

| 引擎 | 内核 | 接入方式 | warm | cold |
|---|---|---|---|---|
| `papersharp` | 自研 CSS 引擎 + Skia | .NET 常驻 worker（`psworker`） | ✔ | ✔ |
| `puppeteer` | Chromium | Node worker（puppeteer-core + 系统 Chrome） | ✔ | ✔ |
| `playwright` | Chromium | Node worker（playwright + 系统 Chrome） | ✔ | ✔ |
| `chrome-cli` | Chromium | `chrome --headless=new --print-to-pdf` 每档一进程 | n/a | ✔ |
| `wkhtmltopdf` | Qt WebKit | 便携版 CLI 每档一进程 | n/a | ✔ |
| `weasyprint` | WeasyPrint 独立排版引擎 | venv worker（需 Pango/GTK DLL） | ✔ | ✔ |

`playwright` 与 `chrome-cli` 只在 A/B 两轴出场：它们与 `puppeteer` 同一个内核，
把 C/D/E 三轴再复述一遍 Chromium 曲线没有信息量，留着只做「包装层开销」的对照
（见 `sweep.py` 里 `engine_table()` 的 `axes` 白名单）。

## 目录

| 路径 | 作用 |
|---|---|
| `gen.py` | 语料生成器（确定性：同一 (轴,档) 永远逐字节相同）→ `corpus/*.html` + `corpus/manifest.json` |
| `sweep.py` | 测量驱动：自适应迭代数、进程树 RSS 轮询、MediaBox 体检 → `out/scale_raw.csv`（逐次）/ `out/scale_cells.csv`（逐 cell 聚合） |
| `workers/node_worker.mjs` | Chromium 系引擎的 stdio 行协议 worker |
| `workers/py_worker.py` | WeasyPrint worker（import 失败时报告不可用，由驱动退回 CLI） |
| `../PdfBench.Runner/ScaleWorker.cs` | PaperSharp 常驻 worker（`PdfBench.Runner.exe psworker`） |
| `plot.py` | log-log 曲线 + 拟合指数 + 装配 `REPORT.md`（人工解读段在 `conclusions.md`，会被拼进去） |
| `summary.py` | 派生量：固定成本/边际成本（线性拟合）、两家延迟线的**交叉规模**、吞吐、round1↔round2 离散度 |
| `integrity.py` | 完整性通道：每个 (引擎,档) 各存一份 PDF 到 `out/artifacts/`，用抽出的字符数/末页字符数/图像 XObject 判「少画、尾部截断、页数不一致」。**不参与任何时间数字** |
| `diagnose_d.py` | 行级取证：用 pypdf visitor 拿坐标与字号，数「每页几行文本、全文几行」（pypdf 6.x 无 `extract_words()`，只能走 visitor） |
| `probe_wrap.py` / `probe_bisect.py` / `probe_shapes.py` | 三件判别探针：中文会不会折行 / 字体栈影响 / 塌缩阈值与形状。用来把「性能差」与「没干活」分开 |
| `migrate_c_to_x.py` | 一次性迁移：初版 C 语料（每层 padding 累积→可用宽度塌缩）整体改挂 X 轴，旧数据不覆盖 |
| `purge_regime.py` | 按口径作废数据（例：warmvary 早版把复制语料算进计时区，整批剔掉重采） |
| `envsnap.py` | 环境快照 → `out/env.json`：rev + dirty 指纹 + CPU/内存 + 各家版本 + 测量口径 |
| `run_phase2.sh` / `run_phase3.sh` | 补测链（锚点/慢引擎/warmvary/离散度/完整性/语料修正重测），串行避免同机互相污染 |
| `corpus/manifest.json` | 每档的元素数/字符数/字节数/SHA —— 拟合的自变量从这里取，不靠名义档位 |

## 复现

```powershell
cd benchmarks/PdfBench
dotnet build PdfBench.Runner -c Release                 # 提供 psworker
cd scale
python gen.py                                           # 21 档语料（改了 gen.py 才需要重跑）
# 依赖：venv（psutil / pypdf / weasyprint）+ npm（puppeteer-core / playwright）
$py = "..\..\..\..\.scratch\pdfbench-venv\Scripts\python.exe"
& $py sweep.py --engines papersharp,puppeteer,playwright,chrome-cli,wkhtmltopdf,weasyprint
python plot.py                                          # charts/ + REPORT.md
```

只跑局部：`--axes A,B`、`--cell-only A3,B2000`、`--regimes cold`、`--engines papersharp`、`--rounds 2`（离散度复量）。
`out/` 下已存在的 (engine, regime, axis, size, round) 会被 `[skip]`，所以断了可以直接重跑续上。

## 口径（改代码前必读）

- **warm = 一个常驻 worker 跑完该 cell 全部迭代**：JIT、字体缓存、浏览器只付一次，
  这是服务端批量出报表/出票据的真实形态。
  **cold = 每次生成重启 worker**：含解释器/浏览器冷启。CLI-only 那两家只有 cold。
  **warmvary = 常驻 worker，但每次喂一份带唯一 token 的副本**：把「同一份文档反复出」才会命中的
  跨文档缓存（.NET 的字体 face 缓存、Chrome 的资源缓存）剥掉。服务端真正跑的是「同一模板换数据」，
  所以 warm 与 warmvary 的差就是**缓存红利的上限**，两者都要报。
- **每个 cell 一个新 worker 进程，不跨引擎复用**——与 `../PdfBench.Runner` 的 `measure-one` 同一条理由：
  上一家的原生内存会把下一家的 RSS 抬上去。
- **RSS 峰值** = worker pid + 全部后代进程工作集之和的采样最大值（20ms 一轮）；
  Chrome 的多进程树会被完整算进来。
- **延迟一律取驱动侧墙钟**（发 job 到收到回执），**含 PDF 落盘**：`papersharp` 的 worker 内部计时
  （`worker_ms`）在 `RenderPdf` 返回处就停了，不含 `WriteAllBytes`，而 Chromium 的 `page.pdf({path})`
  含写盘——所以 `worker_ms` 只用来看「IPC/写盘占了多少」，横向比只看 `wall_med_ms`。
- **迭代数自适应**：先探一次估耗时，再按 `WARM_TARGET_S`/`COLD_TARGET_S` 反推，
  单 cell 硬上限 `MAX_CELL_S`。所以不同档的 `n` 不同（快档 25 次、慢档 3 次），
  比较用中位数而不是总和。
- **PDF 参数逐项对齐**：A4、`printBackground`、四边 0、`preferCSSPageSize`、`networkidle`；
  `chrome-cli` 用 `--no-pdf-header-footer`，`wkhtmltopdf` 用四边 0 + `--disable-smart-shrinking`，
  与 `../PdfBench.Runner/Adapters/` 里那两家一致。换参数=换口径，历史数字作废。
- **页数是测出来的，不是设的**：A 轴的单元页用 `.page + .page { break-before: page }`
  （用 `break-after` 会在末尾多挤一张空白页，把自变量污染成 N+1）。
  每家实际页数与 `MediaBox` 都记进 CSV，页面尺寸不是 A4 的，横向比要先打折。
- **失败如实入账**：超时/崩溃/0 页都会写成 `status=fail|partial` 带 `err`，不悄悄跳过。
  曲线缺一个点通常意味着那家在那个档根本没出图，而不是「慢到没测」。

## 依赖（都是本机装法，写下来免得下次从头踩）

- `.NET`：`../PdfBench.Runner` 编译出的 `psworker` 子命令（Release）。
- Node：`scale/node_modules` 里的 `puppeteer-core` + `playwright`（装 playwright 时
  `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`，三家都指本机 Chrome，不下载第二份浏览器）。
- Python 驱动侧 venv（`.scratch/pdfbench-venv`）：`psutil`（进程树 RSS）+ `pypdf`（页数/文本抽取）。
- **WeasyPrint 在 Windows 上的可用形态**：pip 装的包只含 Python 侧，import 时必炸
  （`cannot load library 'libgobject-2.0-0'`）。可用解法 = 下载官方 `weasyprint-windows-onedir.zip`
  （GitHub release，国内直连极慢，走 `gh-proxy.com` 镜像 32MB 秒下），
  再把解压出的 `_internal` 目录交给 worker 调 `os.add_dll_directory()`。
  **坑**：Python 3.8+ 之后，把那 78 个 DLL 全拷到 `python.exe` 旁边**没用**（实测仍然加载失败），
  只认 `add_dll_directory`。CLI 形态（`onedir/weasyprint.exe`）自带依赖，但只有 cold。

## 已知局限

- 语料是自造的合成文档，轴是刻意拉出来的（B 轴的长表、C 轴的深嵌套都是压力室，不是典型业务页）；
  它回答「怎么放大」，不回答「像不像」——还原度看 `../report/REPORT.md` 的 SSIM 与特性矩阵。
- `wkhtmltopdf` 停在 Qt WebKit 4.x（2016 后的 CSS 它不认识），它在 C/D/E 上的快有一部分来自
  「不认识就跳过」，不是同一件事做得更快。
- Chromium 三家共用系统 Chrome，版本随本机更新漂移；报告里记了 `chrome-cli` 那次的版本指纹。
- 单次测量在一台机器上跑，未做多机分布；`--rounds 2` 只给同轮离散度。
