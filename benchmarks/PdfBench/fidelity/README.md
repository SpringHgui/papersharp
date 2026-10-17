# fidelity — 视觉还原度评分管线

`python fidelity/score.py` 一键重跑：栅格化（增量）→ 对齐 → 逐页指标 → 两份 CSV。

## 产物

| 文件 | 说明 |
|---|---|
| `out/fidelity.csv` | `adapter,page,ssim,mse,pixel_diff_pct,ink_coverage_pct`（schema 见 `../CONTRACT.md`） |
| `out/features_matrix.csv` | 56 个 feature_id × 7 库的 Y/A/N/? 矩阵 + 末尾 NOTE 行（页数/截断备注） |

## 对齐方法

1. **栅格化**：各库 `out/<lib>.pdf` 用 `raster.ps1`（WinRT PdfDocument）出 `<prefix>_pN.png`；
   页图已存在且比 PDF 新则跳过。实测输出为 2× 分辨率（≈1587×2245），与 truth 同量级。
   前缀沿用历史命名：papersharp→`ps`、wkhtmltopdf→`wk`，其余与库同名。
2. **裁剪**：`truth_pN.png`（1700×2247）右侧 112px 是 Chrome 窗口白边，先裁到左侧 **1588px** 宽。
3. **缩放（写死一个方向）**：truth 裁切后用 LANCZOS **缩放到引擎页尺寸**（如 1588×2247 → 1587×2245，
   近乎恒等）；引擎页不重采样。
4. **灰度化**：双方 PIL `"L"`（BT.601 luma），float64 计算。
5. **指标**：
   - `ssim`：优先 scikit-image（11×11 高斯窗，σ=1.5，data_range=255）；skimage 不可用时
     自动回退到内置 numpy 同参数实现（`ssim_numpy`），两实现结果一致到小数点后 4 位。
   - `mse`：灰度均方误差（0..255²）。
   - `pixel_diff_pct`：|Δ灰度| > 10 的像素占比。
   - `ink_coverage_pct`：该页灰度 < 245（非白）像素占比，用于识别"整页没画"退化。
   - 页数与 truth(3 页) 不对齐的页：ssim/mse/pixel_diff 记 `NaN`，超出页仍算自身 ink_coverage，
     并在矩阵 NOTE 行备注。

## 特性矩阵判定规则

- `Y` 库原生机制正确渲染 / `A` 近似或手画（视觉对但靠自绘、预混合、条纹、复制文字等） /
  `N` 不支持或"灰块+红✗"占位 / `?` 未判定（多为内容被裁/截断看不到）。
- API 库（itext7/pdfsharp/questpdf/aspose）：以 `../PdfBench.Runner/Adapters/*.cs` 源码为准
  （questpdf 适配器头部自带 ✅/🟡/ 清单；itext7/pdfsharp/aspose 的 `CanvasBox`/`Placeholder`/`Fail()`
  占位约定），再用页图核对。
- HTML 通道库（papersharp/puppeteer/wkhtmltopdf）：纯看栅格图与 truth 对应区域。

## 每库一句话总评

| 库 | 平均 SSIM | 总评 |
|---|---|---|
| puppeteer | 0.850 | Chrome 直渲即基准本身，全 56 项 Y，残差只来自截图/栅格化 2px 级错位。 |
| questpdf | 0.803 | 表格/阴影/图片圆角走原生 API，但边框线型/渐变/列表大量靠动态 Svg 手画，inset 阴影与 break-all 占位。 |
| aspose | 0.790 | 全页 Graph+绝对定位手画（自研断行引擎），文字族特性原生，柔影/内阴影灰块✗，alpha 图预平铺，另带试用评估水印。 |
| pdfsharp | 0.788 | 纯 XGraphics 手工排版最工整，无一处占位✗，但径向渐变/柔影/opacity 全靠预混合近似，布局逐像素手算。 |
| itext7 | 0.779 | Layout 流式排版覆盖文字/边框/表格原生能力最全，渐变用 64 段条纹近似，外阴影模糊与内阴影两处灰块✗。 |
| wkhtmltopdf | 0.779 | Qt WebKit 老内核：无 flex（四格/三列全部纵堆）、无 CSS 渐变（3.2 整块空白）、letter-spacing 失效，且 p2/p3 尾部内容被截断。 |
| papersharp | 0.747 | 自研引擎文字/渐变/阴影/表格已具雏形，但每页右侧约 10% 被裁、列表 marker/编号缺失、text-indent/text-shadow/double 边框/三角形/图片圆角裁切未实现、页脚与页码缺失（当前版 3 页；早期 4 页含空白首页已重生成）。 |

## 已知噪声

- truth 来自 Chrome 窗口 2× 截图，与 WinRT 栅格存在 ≤2px 的整体错位与重采样差，puppeteer 的
  SSIM≈0.85 即该噪声上限，横向比较各库时应以此为尺度。
- 基准运行期间其他会话可能重生成 `out/*.pdf`；score.py 按 mtime 自动增量重栅格，结果以运行时
  PDF 内容为准。
