#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PdfBench 视觉还原度评分管线。

流程：
 1. 对 out/<lib>.pdf 逐个跑 raster.ps1 出 <prefix>_pN.png（页图已存在且比 PDF 新则跳过）。
 2. 对齐：truth_pN.png（Chrome 2x，1700x2247，右侧 112px 为窗口白边）先裁到 1588 宽，
    再 LANCZOS 缩放到引擎页尺寸（WinRT 96dpi ≈ 794x1123）。方向固定为 truth -> 引擎页。
    双方转灰度（PIL "L"，ITU-R BT.601 luma）。
 3. 每页算 SSIM（优先 scikit-image，缺失时用内置 11x11 高斯窗 numpy 实现）、MSE、
    pixel_diff_pct（|Δ|>10 像素占比）、ink_coverage_pct（灰度<245 算墨，即非白像素占比）。
 4. 写 out/fidelity.csv（schema 见 CONTRACT.md）。
 5. 写 out/features_matrix.csv：逐 feature_id x 逐库的 Y/A/N/? 判定。
    判定依据 = Adapters/*.cs 源码约定（灰块+红✗=N、手画/预混合=A、库原语=Y）
    + 用栅格页图与 truth 对应区域目视核对。HTML 通道库（papersharp/puppeteer/wkhtmltopdf）
    纯看栅格图。拿不准记 ?。

用法：python fidelity/score.py
"""

from __future__ import annotations

import csv
import json
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "out"
RASTER_PS1 = ROOT / "raster.ps1"
FEATURES_JSON = ROOT / "spec" / "features.json"

TRUTH_CROP_W = 1588          # truth 2x 图中页面内容占左侧 1588px，右侧 112px 是窗口白边
TRUTH_PAGES = 3              # truth 基准页数（features.json pages=3）
DIFF_THRESHOLD = 10          # |Δ灰度| > 10 记差异像素
INK_WHITE_LEVEL = 245        # 灰度 < 245 算墨（非白）
NAN = float("nan")

# adapter -> 栅格页图前缀（历史命名，ps=papersharp, wk=wkhtmltopdf）
LIBS: dict[str, str] = {
    "papersharp": "ps",
    "puppeteer": "puppeteer",
    "wkhtmltopdf": "wk",
    "itext7": "itext7",
    "pdfsharp": "pdfsharp",
    "questpdf": "questpdf",
    "aspose": "aspose",
}

# 矩阵列顺序（CONTRACT.md 固定）
MATRIX_COLS = ["puppeteer", "wkhtmltopdf", "itext7", "pdfsharp", "questpdf", "aspose", "papersharp"]


# ---------------------------------------------------------------- raster ----

def page_count(prefix: str) -> int:
    """已栅格页数：从 p1 起连续存在的 <prefix>_pN.png 个数。"""
    n = 0
    while (OUT / f"{prefix}_p{n + 1}.png").exists():
        n += 1
    return n


def ensure_rasters() -> None:
    """对每个库 PDF，若页图缺失或比 PDF 旧则跑 raster.ps1 重新出图。"""
    for lib, prefix in LIBS.items():
        pdf = OUT / f"{lib}.pdf"
        if not pdf.exists():
            print(f"[skip] {pdf.name} 不存在", file=sys.stderr)
            continue
        p1 = OUT / f"{prefix}_p1.png"
        fresh = p1.exists() and all(
            (OUT / f"{prefix}_p{i + 1}.png").stat().st_mtime >= pdf.stat().st_mtime
            for i in range(page_count(prefix))
        )
        if fresh:
            print(f"[raster] {lib}: {page_count(prefix)} 页已最新，跳过")
            continue
        cmd = [
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(RASTER_PS1),
            "-pdf", str(pdf.resolve()),
            "-prefix", str((OUT / prefix).resolve()),
        ]
        print(f"[raster] {lib}: {' '.join(cmd[3:])}")
        r = subprocess.run(cmd, capture_output=True, text=True)
        print(r.stdout.strip())
        if r.returncode != 0:
            print(f"[raster] {lib} 失败: {r.stderr.strip()}", file=sys.stderr)


# ---------------------------------------------------------------- metrics ----

def load_gray(path: Path) -> np.ndarray:
    return np.asarray(Image.open(path).convert("L"), dtype=np.float64)


def load_truth_page(n: int, target_hw: tuple[int, int]) -> np.ndarray:
    """truth 裁掉右侧窗口白边后缩放到引擎页尺寸（统一方向：truth -> 引擎页）。"""
    img = Image.open(OUT / f"truth_p{n}.png").convert("L")
    img = img.crop((0, 0, TRUTH_CROP_W, img.height))
    img = img.resize((target_hw[1], target_hw[0]), Image.LANCZOS)
    return np.asarray(img, dtype=np.float64)


def _gaussian_kernel(size: int = 11, sigma: float = 1.5) -> np.ndarray:
    x = np.arange(size) - size // 2
    g = np.exp(-(x ** 2) / (2 * sigma ** 2))
    g /= g.sum()
    return g


def _filter1d(img: np.ndarray, k: np.ndarray) -> np.ndarray:
    """可分离高斯（边界 replicate），先横向后纵向。"""
    pad = len(k) // 2
    h, w = img.shape
    tmp = np.pad(img, ((0, 0), (pad, pad)), mode="edge")
    out = np.zeros_like(img)
    for i, kv in enumerate(k):
        out += kv * tmp[:, i:i + w]
    tmp = np.pad(out, ((pad, pad), (0, 0)), mode="edge")
    out2 = np.zeros_like(img)
    for i, kv in enumerate(k):
        out2 += kv * tmp[i:i + h, :]
    return out2


def ssim_numpy(a: np.ndarray, b: np.ndarray) -> float:
    """11x11 高斯窗 SSIM（Wang et al. 2004 默认参数，data_range=255）。"""
    k = _gaussian_kernel(11, 1.5)
    c1, c2 = (0.01 * 255) ** 2, (0.03 * 255) ** 2
    mu_a, mu_b = _filter1d(a, k), _filter1d(b, k)
    aa, bb, ab = _filter1d(a * a, k), _filter1d(b * b, k), _filter1d(a * b, k)
    va, vb, vab = aa - mu_a * mu_a, bb - mu_b * mu_b, ab - mu_a * mu_b
    num = (2 * mu_a * mu_b + c1) * (2 * vab + c2)
    den = (mu_a ** 2 + mu_b ** 2 + c1) * (va + vb + c2)
    return float((num / den).mean())


try:
    from skimage.metrics import structural_similarity as _sk_ssim

    def ssim(a: np.ndarray, b: np.ndarray) -> float:
        return float(_sk_ssim(a, b, data_range=255.0, gaussian_weights=True,
                              use_sample_covariance=False))
    SSIM_IMPL = "skimage"
except ImportError:  # cp314 无轮子时的 numpy 后备
    ssim = ssim_numpy  # type: ignore
    SSIM_IMPL = "numpy(11x11 gaussian)"


def page_metrics(truth: np.ndarray, eng: np.ndarray) -> dict[str, float]:
    return {
        "ssim": ssim(truth, eng),
        "mse": float(((truth - eng) ** 2).mean()),
        "pixel_diff_pct": float((np.abs(truth - eng) > DIFF_THRESHOLD).mean() * 100.0),
        "ink_coverage_pct": float((eng < INK_WHITE_LEVEL).mean() * 100.0),
    }


def score_fidelity() -> list[dict]:
    rows: list[dict] = []
    for lib, prefix in LIBS.items():
        n_pages = page_count(prefix)
        for page in range(1, max(TRUTH_PAGES, n_pages) + 1):
            row = {"adapter": lib, "page": page}
            eng_png = OUT / f"{prefix}_p{page}.png"
            truth_png = OUT / f"truth_p{page}.png"
            if page <= n_pages and page <= TRUTH_PAGES and truth_png.exists():
                eng = load_gray(eng_png)
                truth = load_truth_page(page, eng.shape[:2])
                row.update(page_metrics(truth, eng))
            else:
                # 页数不对齐：与 truth 无对应页可比的，超出 truth 的页只算自身墨量
                row.update({"ssim": NAN, "mse": NAN, "pixel_diff_pct": NAN,
                            "ink_coverage_pct": NAN})
                if page > TRUTH_PAGES and eng_png.exists():
                    eng = load_gray(eng_png)
                    row["ink_coverage_pct"] = float((eng < INK_WHITE_LEVEL).mean() * 100.0)
            rows.append(row)
    return rows


def write_fidelity_csv(rows: list[dict]) -> Path:
    path = OUT / "fidelity.csv"
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["adapter", "page", "ssim", "mse", "pixel_diff_pct", "ink_coverage_pct"])
        for r in rows:
            w.writerow([r["adapter"], r["page"],
                        f"{r['ssim']:.4f}" if r["ssim"] == r["ssim"] else "NaN",
                        f"{r['mse']:.1f}" if r["mse"] == r["mse"] else "NaN",
                        f"{r['pixel_diff_pct']:.2f}" if r["pixel_diff_pct"] == r["pixel_diff_pct"] else "NaN",
                        f"{r['ink_coverage_pct']:.2f}" if r["ink_coverage_pct"] == r["ink_coverage_pct"] else "NaN"])
    return path


# ------------------------------------------------------- features matrix ----
# 判定依据（逐库）：
#   puppeteer   : 系统 Chrome 直渲 challenge.html，三页与 truth 目视一致（SSIM≈1 佐证）→ 全 Y。
#   wkhtmltopdf : Qt WebKit 老内核。看 wk_p1..3：无 flex（四格对齐/列表三列全部纵向堆叠）→ flex-* 全 N；
#                 无 CSS 渐变（3.2 渐变条区域整块空白）→ gradient-* 全 N；inset/多重阴影未落地 → N；
#                 letter-spacing 不生效 → N；small-caps/light 字重缺失 → weights 记 A；
#                 p2 自 2.3 圆角第 3 格起、p3 自 3.3 第 2 图起内容被截断（仅 3 页），相关项记 ?。
#   itext7      : IText7Adapter.cs。Layout 原语(Paragraph/Table/Border/字体特性)=Y；
#                 CanvasBox 自绘(椭圆/三角/渐变条纹/阴影复制)=A；灰块+红✗ 占位(外阴影模糊/内阴影)=N。
#   pdfsharp    : PdfSharpMigraDocAdapter.cs。纯 XGraphics 手画：线型/圆角/裁剪用画布原语=Y，
#                 小字体/字距/列表/表格/布局逐字符手工排布=A，径向渐变/柔影/alpha 全部预混合近似=A。
#   questpdf    : QuestPdfAdapter.cs 头部注释自带 ✅/🟡/ 清单（Svg 手画=A，占位=N）。
#   aspose      : AsposePdfAdapter.cs。全页 Graph + 绝对定位文字（自研断行引擎）：TextState 原生
#                 文字特性=Y，其余手画=A；Fail() 灰块+红✗(外阴影模糊/内阴影/三重阴影模糊层)=N；
#                 PNG alpha 与圆角图用 GDI+ 预平铺=A。
#   papersharp  : 自研 HTML 引擎。该列不手填——由 fidelity/cells.py 逐特性像素对账推导
#                 （out/cells.csv，Δ = 本格 SSIM − puppeteer 同格 SSIM：>=-0.03 Y / >=-0.12 A / 否则 N）。
#                 JUDGMENTS 里 papersharp 的取值仅在 cells.csv 缺失时兜底。

JUDGMENTS: dict[str, dict[str, str]] = {
    # feature_id: {lib: Y/A/N/?}
    "headings":                {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "Y", "papersharp": "Y"},
    "weights":                 {"puppeteer": "Y", "wkhtmltopdf": "A", "itext7": "A", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "A"},
    "inline-code-chip":        {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "cjk-latin-mixed":         {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "Y", "papersharp": "Y"},
    "line-height":             {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "Y", "papersharp": "Y"},
    "letter-spacing":          {"puppeteer": "Y", "wkhtmltopdf": "N", "itext7": "Y", "pdfsharp": "A", "questpdf": "Y", "aspose": "Y", "papersharp": "Y"},
    "text-indent":             {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "N"},
    "text-shadow":             {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "A", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "N"},
    "text-color":              {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "Y", "papersharp": "Y"},
    "text-align-justify":      {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "Y", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "?"},
    "list-ul-marker-color":    {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "A", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "N"},
    "list-ol-decimal-alpha-roman": {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "A", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "N"},
    "list-style-circle-none":  {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "A", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "N"},
    "nested-list":             {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "A"},
    "border-solid":            {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "Y", "papersharp": "Y"},
    "border-dashed":           {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "A", "aspose": "Y", "papersharp": "Y"},
    "border-dotted":           {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "A", "aspose": "Y", "papersharp": "Y"},
    "border-double":           {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "A", "aspose": "A", "papersharp": "N"},
    "border-groove-ridge":     {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "A", "aspose": "A", "papersharp": "N"},
    "border-width-2-4-8":      {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "Y", "papersharp": "Y"},
    "border-per-side-style":   {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "A", "aspose": "Y", "papersharp": "Y"},
    "border-per-side-color":   {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "A", "aspose": "Y", "papersharp": "Y"},
    "border-currentcolor":     {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "A", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "border-single-side":      {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "Y", "papersharp": "Y"},
    "radius-uniform":          {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "radius-per-corner":       {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "radius-circle-ellipse":   {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "A", "pdfsharp": "Y", "questpdf": "A", "aspose": "Y", "papersharp": "Y"},
    "radius-pill":             {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "radius-plus-dashed":      {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "Y", "pdfsharp": "Y", "questpdf": "A", "aspose": "A", "papersharp": "Y"},
    "border-radius-bg-clip":   {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "Y", "pdfsharp": "Y", "questpdf": "A", "aspose": "A", "papersharp": "?"},
    "triangle-via-border":     {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "A", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "N"},
    "box-shadow-soft":         {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "N", "pdfsharp": "A", "questpdf": "Y", "aspose": "N", "papersharp": "Y"},
    "box-shadow-hard":         {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "A", "pdfsharp": "Y", "questpdf": "Y", "aspose": "Y", "papersharp": "Y"},
    "box-shadow-inset":        {"puppeteer": "Y", "wkhtmltopdf": "N", "itext7": "N", "pdfsharp": "A", "questpdf": "N", "aspose": "N", "papersharp": "N"},
    "box-shadow-multi":        {"puppeteer": "Y", "wkhtmltopdf": "N", "itext7": "A", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "N"},
    "linear-gradient-multi-stop": {"puppeteer": "Y", "wkhtmltopdf": "N", "itext7": "A", "pdfsharp": "Y", "questpdf": "A", "aspose": "A", "papersharp": "Y"},
    "radial-gradient":         {"puppeteer": "Y", "wkhtmltopdf": "N", "itext7": "A", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "?"},
    "gradient-with-alpha":     {"puppeteer": "Y", "wkhtmltopdf": "N", "itext7": "A", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "?"},
    "rgba-overlap-blend":      {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "A", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "Y"},
    "opacity":                 {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "A", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "Y"},
    "inline-alpha-highlight":  {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "A", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "image-jpeg":              {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "Y", "papersharp": "Y"},
    "image-png-alpha":         {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "image-rounded-border":    {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "Y", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "N"},
    "flex-space-between":      {"puppeteer": "Y", "wkhtmltopdf": "N", "itext7": "A", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "flex-margin-auto":        {"puppeteer": "Y", "wkhtmltopdf": "N", "itext7": "A", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "flex-wrap-gap":           {"puppeteer": "Y", "wkhtmltopdf": "N", "itext7": "A", "pdfsharp": "A", "questpdf": "A", "aspose": "A", "papersharp": "Y"},
    "flex-nested-ratios":      {"puppeteer": "Y", "wkhtmltopdf": "N", "itext7": "A", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "flex-cross-axis-center":  {"puppeteer": "Y", "wkhtmltopdf": "N", "itext7": "A", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "table-thead-tfoot":       {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "Y", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "table-striped":           {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "Y", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "table-cell-borders":      {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "Y", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "table-colspan-footer":    {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "Y", "pdfsharp": "A", "questpdf": "Y", "aspose": "A", "papersharp": "Y"},
    "word-break-all":          {"puppeteer": "Y", "wkhtmltopdf": "?", "itext7": "A", "pdfsharp": "A", "questpdf": "N", "aspose": "A", "papersharp": "N"},
    "page-header-footer":      {"puppeteer": "Y", "wkhtmltopdf": "A", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "Y", "papersharp": "A"},
    "page-number-badge":       {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "Y", "pdfsharp": "Y", "questpdf": "Y", "aspose": "Y", "papersharp": "N"},
}

# 页数不对齐 / 内容截断等备注（写入矩阵末尾 NOTE 行，列结构不变）
MATRIX_NOTES = {
    "papersharp": "该列由 fidelity/cells.py 逐特性像素对账自动判定（以 puppeteer 同格 SSIM 为天花板的相对分），"
                  "不再手填；3 页齐全，横向内容与 Chrome 对齐（最右墨迹列一致）。"
                  "剩余主因是纵向逐段累积漂移：页1 到 css y=500 已比 Chrome 低约 10px，故每个格子都被平移稀释。",
    "wkhtmltopdf": "p2自2.3圆角第3格起、p3自3.3第二张图起内容被截断(仅出3页)，"
                   "被截断区域的特性记?。",
}


def measured_papersharp() -> dict[str, str]:
    """papersharp 列改由 out/cells.csv 推导（fidelity/cells.py：逐特性格子像素对账，以 puppeteer 同格
    SSIM 为天花板算相对分），不再靠肉眼；一个特性占多格时取最差那格。"""
    p = OUT / "cells.csv"
    if not p.exists():
        return {}
    rank = {"Y": 0, "A": 1, "N": 2, "?": 3}
    agg: dict[str, str] = {}
    with p.open(encoding="utf-8") as f:
        for row in csv.DictReader(f):
            fid, v = row["feature_id"], row.get("verdict", "?")
            if v not in rank:
                continue
            if fid not in agg or rank[v] > rank[agg[fid]]:
                agg[fid] = v
    return agg


def write_matrix_csv() -> Path:
    spec = json.loads(FEATURES_JSON.read_text(encoding="utf-8"))
    measured = measured_papersharp()
    path = OUT / "features_matrix.csv"
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["feature_id", "group", "pages"] + MATRIX_COLS)
        for feat in spec["features"]:
            fid = feat["id"]
            j = dict(JUDGMENTS.get(fid, {}))
            if fid in measured:
                j["papersharp"] = measured[fid]
            w.writerow([fid, feat["group"], feat["page"]] + [j.get(col, "?") for col in MATRIX_COLS])
        note_row = ["NOTE", "note", "-"]
        for col in MATRIX_COLS:
            note_row.append(MATRIX_NOTES.get(col, ""))
        w.writerow(note_row)
    return path


# ---------------------------------------------------------------- main ----

def main() -> int:
    ensure_rasters()
    rows = score_fidelity()
    csv_path = write_fidelity_csv(rows)
    mtx_path = write_matrix_csv()
    print(f"[ok] SSIM 实现: {SSIM_IMPL}")
    print(f"[ok] {csv_path}")
    print(f"[ok] {mtx_path}")
    # 简报：每库平均 SSIM + 特性计数
    import statistics
    per_lib: dict[str, list[float]] = {}
    for r in rows:
        if r["ssim"] == r["ssim"]:
            per_lib.setdefault(r["adapter"], []).append(r["ssim"])
    for lib in LIBS:
        v = per_lib.get(lib, [])
        print(f"  {lib:12s} mean SSIM = {statistics.fmean(v):.4f}" if v else f"  {lib:12s} mean SSIM = NaN")
    counts = {c: {"Y": 0, "A": 0, "N": 0, "?": 0} for c in MATRIX_COLS}
    measured = measured_papersharp()
    for fid, j in JUDGMENTS.items():
        eff = dict(j)
        if fid in measured:
            eff["papersharp"] = measured[fid]
        for c in MATRIX_COLS:
            counts[c][eff.get(c, "?")] += 1
    for c in MATRIX_COLS:
        print(f"  {c:12s} Y/A/N/? = {counts[c]['Y']}/{counts[c]['A']}/{counts[c]['N']}/{counts[c]['?']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
