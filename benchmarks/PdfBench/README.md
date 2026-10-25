# PdfBench — .NET PDF 生成库横向基准

同一份 3 页 A4 的 HTML/CSS 挑战页，交给 8 个方案各出一版 PDF，比**速度 / 内存 / 体积 / 吞吐 / 冷启动 / 并发安全 / 视觉还原度 / CSS 特性覆盖**，自动出图出报告。

**结论看这里**：[`report/REPORT.md`](report/REPORT.md) —— 完整测试报告（环境、全指标与派生指标、12 张图含逐页 truth×8 视觉对照、24 行逐页还原度、56×8 特性矩阵、人工解读与选型建议）。

> **和 [`scale/`](scale/README.md) 的分工**：这份是「.NET 里谁能出 PDF」的横向（8 家，含 4 家必须手抄设计稿的
> API 通道库 + 1 家把画布单独拎出来的裸画布基线 skiasharp，单份 3 页挑战页）。**只比吃 HTML 的引擎、且比文档变大之后谁先垮**的是
> [`scale/`](scale/README.md)：6 家 HTML 引擎 × 五轴 24 档语料 × warm/warmvary/cold 三口径，
> 外加一条不参与计时的完整性通道。两边的 `papersharp` 数字口径不同（这里 3 页挑战页、那里 1~100 页），
> 不要跨文档相减。

## 目录

| 路径 | 作用 |
|---|---|
| `spec/challenge.html` | 挑战页（唯一真源，56 项特性各带 `data-f="<id>"`）；`spec/features.json` 是特性清单 |
| `out/truth_*` | 无头 Chrome 出的基准 PDF 与 2× 页图（还原度的对照物） |
| `PdfBench.Runner/Adapters/*.cs` | 8 位选手，各实现 `IPdfAdapter`（反射自动发现，加库不用改公共文件）；`skiasharp` 是裸画布基线 |
| `PdfBench.Runner/Measure.cs` | 测量内核：`measure` → `out/results.csv` |
| `raster.ps1` | WinRT PDF → PNG（96dpi，逐页） |
| `fidelity/score.py` | 增量栅格 + SSIM/MSE/pixel-diff → `out/fidelity.csv`、`out/features_matrix.csv` |
| `report/report.py` | matplotlib 出 `report/charts/*.png` + 生成 `REPORT.md`；`report/conclusions.md` 是人工解读段，会被拼进去 |
| `CONTRACT.md` | 三份 CSV 的严格 schema（生产者/消费者之间的契约） |

## 复现

```powershell
cd benchmarks/PdfBench
dotnet build PdfBench.Runner -c Debug
$exe = "PdfBench.Runner/bin/Debug/net10.0/PdfBench.Runner.exe"

& $exe                                   # 只生成：out/<lib>.pdf ×8 + 汇总表
& $exe measure                           # 全指标 → out/results.csv（每库一个子进程，数分钟）
python fidelity/score.py                 # 栅格 + 还原度 → out/fidelity.csv / features_matrix.csv
python report/report.py                  # 图表 + REPORT.md
```

基准本身（`out/truth_*` 已入库，改过 `challenge.html` 才需要重出）：

```powershell
chrome --headless=new --disable-gpu --no-pdf-header-footer `
  --print-to-pdf="$PWD\out\truth_chrome.pdf" "file:///$($PWD.Path.Replace('\','/'))/spec/challenge.html"
chrome --headless=new --disable-gpu --force-device-scale-factor=2 --window-size=850,3400 `
  --screenshot="$PWD\out\truth_full2x.png" "file:///.../spec/challenge.html"
```

`out/truth_pN.png`（1700×2247，逐页 2× 图）由 `truth_full2x.png` 按内容页边界切出——注意**不是**整图三等分（6800/3 ≠ 2247），Chrome 窗口底部留白会让等分切歪，重切时以每页标题行的实际像素边界为准。

## 依赖

- .NET 10 SDK；NuGet 包见 `PdfBench.Runner.csproj`（QuestPDF 2025.7 / itext7 8.0.2 / PDFsharp-MigraDoc 6.2.4 / PuppeteerSharp 25.8 / Aspose.PDF 26.9）。
- 本机 Chrome（puppeteer 适配器直接复用 `C:\Program Files\Google\Chrome\Application\chrome.exe`）。
- wkhtmltopdf 便携版：`~/wkhtmltox/wkhtmltox/bin/wkhtmltopdf.exe`（0.12.5 mxe-cross-win64，官方 GitHub release 没有 Windows 包，只能拿 7z 版用 `System32\tar.exe` 解）。
- Python 3 + numpy（必需）、scikit-image（可选，缺了会走内置 numpy SSIM）、matplotlib（出图）。

## 测量口径（改代码前必读）

- **每库必须在全新子进程测**：同进程连测 8 家会让 Skia 原生内存累积，`rss_peak` 虚高到 3–4 GB；隔离后才是真实的 79–781 MB。
- `threadsafe` = 4 并发产物与串行产物**剥离 `/CreationDate`、`/ID`、Xmp 日期后逐字节比对**；iText 的字体子集随机前缀（`/ABCDEF+`）也要剥，否则假 0。
- 每份文档写到独立临时路径：PDFsharp 偶发不释放句柄，同路径覆写会 IOException。
- `out/<lib>.pdf` 是"展示用"的一次生成，与测量用的临时产物不是同一个文件；**Aspose 两次生成的体积能差 10%**（水印/子集前缀），体积榜以 `results.csv` 为准。
- 还原度的天花板不是 1.0，也别把绝对值当"像不像"：truth 来自 Chrome、对照图来自 WinRT 栅格，天然有 ≤2px 错位；而 `puppeteer` 与 truth 同源（都出自 Chrome 排版），整页 SSIM 高到 0.99 只说明它是**上限**，不是"还原得最好"。逐格对账时 puppeteer 自己在同一格也只有 0.90~0.94（见 `fidelity/cells.py` 的相对分口径）。

## 加一位新选手

`PdfBench.Runner/Adapters/XxxAdapter.cs` 实现 `IPdfAdapter`（`Name` 用小写、和 CSV 里的 adapter 名一致），`Generate(ctx, outPdfPath)` 里把 `spec/challenge.html` 变成 PDF；不支持的特性按约定画**灰块 + 红 ✗ 占位**并保持版面流，然后在 `fidelity/score.py` 的特性表里补一行判定。
