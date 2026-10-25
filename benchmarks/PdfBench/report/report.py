# -*- coding: utf-8 -*-
"""PdfBench 自动图表 + 报告生成器。

读 4 个 CSV（CONTRACT.md schema）：results.csv / fidelity.csv / features_matrix.csv / cells.csv
产出：report/charts/*.png 12 张图 + report/REPORT.md（cells.csv 缺失时逐格图与章节自动跳过）

用法：
    python report.py                      # 自动选数据源：out/results.csv 存在则用真数据，否则用 sample/
    python report.py --data-dir ../out    # 指定数据目录
    python report.py --data-dir sample --charts-subdir charts_sample
"""
import argparse
import collections
import csv
import datetime as _dt
import math
import os
import re
import subprocess
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np  # noqa: F401  (matplotlib 依赖，且部分坐标计算方便)

plt.rcParams["font.sans-serif"] = ["Microsoft YaHei", "SimHei", "DejaVu Sans"]
plt.rcParams["axes.unicode_minus"] = False
plt.rcParams["figure.autolayout"] = False

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, os.pardir))

ADAPTER_ORDER = ["papersharp", "puppeteer", "wkhtmltopdf", "itext7",
                 "pdfsharp", "questpdf", "aspose", "skiasharp"]
COLORS = {
    "papersharp": "#d62728", "puppeteer": "#1f77b4", "wkhtmltopdf": "#2ca02c",
    "itext7": "#9467bd", "pdfsharp": "#8c564b", "questpdf": "#e377c2",
    "aspose": "#17becf", "skiasharp": "#7f7f7f",
}
METRICS = ["latency_min", "latency_med", "latency_mean", "latency_p95",
           "latency_max", "alloc_med", "gen0", "gen1", "gen2", "rss_peak",
           "size", "pages", "throughput_serial", "throughput_par4",
           "coldstart", "threadsafe"]
METRIC_CN = {
    "latency_min": "延迟min(ms)", "latency_med": "延迟med(ms)",
    "latency_mean": "延迟mean(ms)", "latency_p95": "延迟p95(ms)",
    "latency_max": "延迟max(ms)", "alloc_med": "分配med(KB)",
    "gen0": "Gen0", "gen1": "Gen1", "gen2": "Gen2",
    "rss_peak": "RSS峰值(MB)", "size": "体积(KB)", "pages": "页数",
    "throughput_serial": "吞吐串行(docs/s)", "throughput_par4": "吞吐4线(docs/s)",
    "coldstart": "冷启动(ms)", "threadsafe": "线程安全",
}


# ---------------------------------------------------------------- 数据读取
def _f(s):
    """CSV 单元格 → float；NaN/空/坏值一律 None。"""
    if s is None:
        return None
    t = s.strip()
    if not t or t.lower() in ("nan", "na", "n/a", "none", "?", "-"):
        return None
    try:
        v = float(t)
        return None if math.isnan(v) else v
    except ValueError:
        return None


def load_results(path):
    """→ {adapter: {metric: value|None}}；缺行缺值均为 None，不崩。"""
    data = {}
    if not os.path.exists(path):
        return data
    with open(path, newline="", encoding="utf-8-sig") as fh:
        for row in csv.DictReader(fh):
            a = (row.get("adapter") or "").strip()
            m = (row.get("metric") or "").strip()
            if not a or m not in METRICS:
                continue
            data.setdefault(a, {})[m] = _f(row.get("value"))
    return data


def load_fidelity(path):
    """→ {adapter: {page: {ssim,mse,pixel_diff_pct,ink_coverage_pct}}}"""
    data = {}
    if not os.path.exists(path):
        return data
    with open(path, newline="", encoding="utf-8-sig") as fh:
        for row in csv.DictReader(fh):
            a = (row.get("adapter") or "").strip()
            p = _f(row.get("page"))
            if not a or p is None:
                continue
            data.setdefault(a, {})[int(p)] = {
                k: _f(row.get(k)) for k in
                ("ssim", "mse", "pixel_diff_pct", "ink_coverage_pct")}
    return data


def load_cells(path):
    """读 out/cells.csv（fidelity/cells.py 产出）→ {feature_id: {group, rows:[...]}}；无文件 → {}。

    每行是一个 (feature, page) 格：ps_ssim / puppeteer_ssim / delta=ours-ref / verdict。
    """
    by: dict = {}
    if not os.path.exists(path):
        return by
    with open(path, newline="", encoding="utf-8-sig") as fh:
        for row in csv.DictReader(fh):
            fid = (row.get("feature_id") or "").strip()
            if not fid:
                continue
            d = {
                "page": _f(row.get("page")),
                "ours": _f(row.get("ps_ssim")),
                "ref": _f(row.get("puppeteer_ssim") or row.get("ref_ssim")),
                "delta": _f(row.get("delta")),
                "diff_pct": _f(row.get("diff_pct")),
                "verdict": (row.get("verdict") or "?").strip().upper() or "?",
            }
            e = by.setdefault(fid, {"group": (row.get("group") or "").strip(), "rows": []})
            e["rows"].append(d)
    for e in by.values():
        e["rows"].sort(key=lambda r: (r["delta"] is None, r["delta"]))
    return by


def worst_delta(cell):
    ds = [r["delta"] for r in cell["rows"] if r["delta"] is not None]
    return min(ds) if ds else None


def avg_delta(cell):
    ds = [r["delta"] for r in cell["rows"] if r["delta"] is not None]
    return sum(ds) / len(ds) if ds else None


def cell_verdict(cell):
    """一格一判定；特性级取最差格（N>A>Y）。"""
    vs = {r["verdict"] for r in cell["rows"]}
    return next((v for v in ("N", "?", "A", "Y") if v in vs), "?")


FEATURE_NOTES = {}  # load_features 收集的每库一句话备注（features_matrix.csv 的 NOTE 行）


def load_features(path):
    """→ rows: [{feature_id, group, pages, cells:{adapter: 'Y'/'A'/'N'/'?'}}]"""
    rows = []
    FEATURE_NOTES.clear()
    if not os.path.exists(path):
        return rows
    with open(path, newline="", encoding="utf-8-sig") as fh:
        rdr = csv.reader(fh)
        header = next(rdr, None)
        if not header:
            return rows
        cols = {name.strip(): i for i, name in enumerate(header)}
        for raw in rdr:
            if not raw or not any(raw):
                continue
            fid = (raw[cols.get("feature_id", 0)] or "").strip()
            if fid.upper() == "NOTE":
                for a in ADAPTER_ORDER:
                    i = cols.get(a)
                    if i is not None and i < len(raw) and raw[i].strip():
                        FEATURE_NOTES[a] = raw[i].strip()
                continue
            cells = {}
            for a in ADAPTER_ORDER:
                i = cols.get(a)
                if i is not None and i < len(raw):
                    cells[a] = (raw[i] or "?").strip().upper() or "?"
            rows.append({"feature_id": raw[cols.get("feature_id", 0)] if raw else "",
                         "group": raw[cols.get("group", 1)] if len(raw) > 1 else "",
                         "pages": raw[cols.get("pages", 2)] if len(raw) > 2 else "",
                         "cells": cells})
    return rows


def ordered_present(adapters_data, need=None):
    """按 ADAPTER_ORDER 输出存在的 adapter；need=metric 列表则该指标须非 None。"""
    out = []
    for a in ADAPTER_ORDER:
        d = adapters_data.get(a)
        if not d:
            continue
        if need and any(d.get(m) is None for m in need):
            continue
        out.append(a)
    return out


# ---------------------------------------------------------------- 通用绘图
def _save(fig, outdir, name):
    os.makedirs(outdir, exist_ok=True)
    p = os.path.join(outdir, name)
    fig.savefig(p, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print("  wrote", p)
    return name


def _barlabels(ax, xs, vals, fmt="{:.0f}", dy=None):
    for x, v in zip(xs, vals):
        if v is None:
            continue
        ax.annotate(fmt.format(v), (x, v), ha="center", va="bottom",
                    fontsize=8, xytext=(0, 2), textcoords="offset points")


# ---------------------------------------------------------------- 图 ①—⑧
def chart_latency(res, outdir):
    ad = ordered_present(res, ["latency_med"])
    if not ad:
        return None
    med = [res[a]["latency_med"] for a in ad]
    p95 = [res[a].get("latency_p95") for a in ad]
    err_lo = [0.0] * len(ad)  # 误差线只向上画到 p95
    err_hi = [max((p95[i] - med[i]) if p95[i] is not None else 0.0, 0.0)
              for i in range(len(ad))]
    fig, ax = plt.subplots(figsize=(9, 5))
    x = np.arange(len(ad))
    ax.bar(x, med, 0.58, color=[COLORS.get(a, "#777") for a in ad], alpha=0.85,
           label="延迟中位数")
    ax.errorbar(x, med, yerr=[err_lo, err_hi], fmt="none", ecolor="#333",
                capsize=5, elinewidth=1.3, label="至 p95")
    ax.set_yscale("log")
    ax.set_xticks(x, ad, rotation=20, ha="right", fontsize=9)
    ax.set_ylabel("延迟（ms，对数轴）")
    ax.set_title("① 单文档生成延迟：med 柱 + p95 误差线")
    ax.grid(axis="y", ls=":", alpha=0.5, which="both")
    ax.legend(fontsize=8)
    _barlabels(ax, x, med, "{:,.0f}")
    return _save(fig, outdir, "01_latency.png")


def chart_memory(res, outdir):
    fig, axes = plt.subplots(1, 2, figsize=(11, 4.6))
    for ax, metric, title, ylab in (
            (axes[0], "alloc_med", "托管分配中位数", "KB"),
            (axes[1], "rss_peak", "进程树 RSS 峰值", "MB")):
        ad = ordered_present(res, [metric])
        if not ad:
            ax.text(0.5, 0.5, f"无 {metric} 数据", ha="center", va="center",
                    transform=ax.transAxes)
            continue
        vals = [res[a][metric] for a in ad]
        x = np.arange(len(ad))
        ax.bar(x, vals, 0.58, color=[COLORS.get(a, "#777") for a in ad], alpha=0.85)
        ax.set_xticks(x, ad, rotation=35, ha="right", fontsize=8)
        ax.set_ylabel(ylab)
        ax.set_title(title)
        ax.grid(axis="y", ls=":", alpha=0.5)
        _barlabels(ax, x, vals, "{:,.0f}")
    fig.suptitle("② 内存双联柱：托管分配(KB) 与 RSS 峰值(MB)")
    return _save(fig, outdir, "02_memory.png")


def chart_size(res, outdir):
    ad = ordered_present(res, ["size"])
    if not ad:
        return None
    vals = [res[a]["size"] for a in ad]
    pages = [res[a].get("pages") for a in ad]
    fig, ax = plt.subplots(figsize=(9, 4.8))
    x = np.arange(len(ad))
    ax.bar(x, vals, 0.58, color=[COLORS.get(a, "#777") for a in ad], alpha=0.85)
    for i, (v, pg) in enumerate(zip(vals, pages)):
        note = f"{v:,.1f}KB" + (f"\n({pg:.0f}页)" if pg is not None else "")
        ax.annotate(note, (i, v), ha="center", va="bottom", fontsize=8,
                    xytext=(0, 2), textcoords="offset points")
    ax.set_xticks(x, ad, rotation=20, ha="right", fontsize=9)
    ax.set_ylabel("输出体积（KB）")
    ax.set_ylim(0, max(vals) * 1.25)
    ax.set_title("③ 输出 PDF 体积（含页数标注）")
    ax.grid(axis="y", ls=":", alpha=0.5)
    return _save(fig, outdir, "03_size.png")


def chart_throughput(res, outdir):
    ad = ordered_present(res, ["throughput_serial", "throughput_par4"])
    if not ad:
        return None
    s = [res[a]["throughput_serial"] for a in ad]
    p4 = [res[a]["throughput_par4"] for a in ad]
    speed = [(b / a) if a else None for a, b in zip(s, p4)]
    x = np.arange(len(ad))
    w = 0.36
    fig, ax = plt.subplots(figsize=(9.5, 5))
    ax.bar(x - w / 2, s, w, label="串行 docs/s", color="#4c72b0", alpha=0.9)
    ax.bar(x + w / 2, p4, w, label="4 线程 docs/s", color="#dd8452", alpha=0.9)
    ax.set_xticks(x, ad, rotation=20, ha="right", fontsize=9)
    ax.set_ylabel("吞吐（docs/s，3 秒窗口）")
    ax.set_title("④ 吞吐：串行 vs 4 线程 + 加速比折线")
    ax.grid(axis="y", ls=":", alpha=0.5)
    ax2 = ax.twinx()
    sx = [xi for xi, v in zip(x, speed) if v is not None]
    sv = [v for v in speed if v is not None]
    ax2.plot(sx, sv, "o-", color="#1a1a1a", lw=1.6, ms=5, label="加速比(×)")
    for xi, v in zip(sx, sv):
        ax2.annotate(f"{v:.1f}×", (xi, v), ha="center", va="bottom", fontsize=8,
                     xytext=(0, 4), textcoords="offset points")
    ax2.set_ylabel("加速比（par4 / serial）", color="#1a1a1a")
    ax2.set_ylim(0, (max(sv) if sv else 4) * 1.35)
    h1, l1 = ax.get_legend_handles_labels()
    h2, l2 = ax2.get_legend_handles_labels()
    ax.legend(h1 + h2, l1 + l2, fontsize=8, loc="upper left")
    return _save(fig, outdir, "04_throughput.png")


def chart_coldstart(res, outdir):
    ad = ordered_present(res, ["coldstart"])
    if not ad:
        return None
    vals = [res[a]["coldstart"] for a in ad]
    fig, ax = plt.subplots(figsize=(9, 4.8))
    x = np.arange(len(ad))
    ax.bar(x, vals, 0.58, color=[COLORS.get(a, "#777") for a in ad], alpha=0.85)
    ax.set_xticks(x, ad, rotation=20, ha="right", fontsize=9)
    ax.set_ylabel("墙钟（ms）")
    ax.set_title("⑤ 冷启动：独立进程单次生成总墙钟（含 JIT/字体/许可初始化）")
    ax.grid(axis="y", ls=":", alpha=0.5)
    _barlabels(ax, x, vals, "{:,.0f}")
    return _save(fig, outdir, "05_coldstart.png")


def chart_fidelity(fide, outdir):
    if not fide:
        return None
    means = {}
    for a, pages in fide.items():
        vs = [p.get("ssim") for p in pages.values() if p.get("ssim") is not None]
        if vs:
            means[a] = sum(vs) / len(vs)
    fig, axes = plt.subplots(2, 1, figsize=(10, 8.6),
                             gridspec_kw={"height_ratios": [1, 1.15]})
    # 上：每库平均 SSIM
    ad = [a for a in ADAPTER_ORDER if a in means]
    ax = axes[0]
    if ad:
        vals = [means[a] for a in ad]
        x = np.arange(len(ad))
        ax.bar(x, vals, 0.58, color=[COLORS.get(a, "#777") for a in ad], alpha=0.85)
        ax.set_xticks(x, ad, rotation=20, ha="right", fontsize=9)
        ax.set_ylim(0, 1.08)
        ax.set_ylabel("平均 SSIM")
        ax.set_title("每库平均 SSIM（对 truth_pN.png，NaN 页跳过）", fontsize=10)
        ax.grid(axis="y", ls=":", alpha=0.5)
        _barlabels(ax, x, vals, "{:.3f}")
    else:
        ax.text(0.5, 0.5, "无 SSIM 数据", ha="center", va="center",
                transform=ax.transAxes)
    # 下：逐页分组柱（仅 truth 有的 1..3 页；4+ 页 SSIM=NaN 自动跳过）
    ax = axes[1]
    page_ids = sorted({p for pages in fide.values() for p, d in pages.items()
                       if d.get("ssim") is not None})
    page_ids = [p for p in page_ids if p in (1, 2, 3)] or page_ids[:3]
    if ad and page_ids:
        n = len(page_ids)
        w = 0.8 / max(n, 1)
        x = np.arange(len(ad))
        for j, p in enumerate(page_ids):
            vals = [fide[a].get(p, {}).get("ssim") for a in ad]
            xs = x - 0.4 + w / 2 + j * w
            ax.bar(xs, [v if v is not None else 0 for v in vals], w * 0.92,
                   alpha=0.55 + 0.35 * (j / max(n - 1, 1)),
                   color="#4c72b0", label=f"第 {p} 页")
        ax.set_xticks(x, ad, rotation=20, ha="right", fontsize=9)
        ax.set_ylim(0, 1.08)
        ax.set_ylabel("SSIM")
        ax.set_title("逐页 SSIM 分组柱（缺页/NaN 显示为空）", fontsize=10)
        ax.grid(axis="y", ls=":", alpha=0.5)
        ax.legend(fontsize=8)
    fig.suptitle("⑥ 视觉还原度（truth = Chrome 2× 栅格）")
    return _save(fig, outdir, "06_fidelity.png")


def coverage_counts(feats):
    """→ {adapter: {Y,A,N,?,total}}"""
    out = {}
    for row in feats:
        for a, v in row["cells"].items():
            d = out.setdefault(a, {"Y": 0, "A": 0, "N": 0, "?": 0, "total": 0})
            d[v if v in ("Y", "A", "N") else "?"] += 1
            d["total"] += 1
    return out


def chart_features(feats, outdir):
    cnt = coverage_counts(feats)
    ad = [a for a in ADAPTER_ORDER if a in cnt]
    if not ad:
        return None
    fig, ax = plt.subplots(figsize=(9, 5))
    x = np.arange(len(ad))
    bottoms = np.zeros(len(ad))
    for key, color, label in (("Y", "#2e7d32", "Y 原生/正确"),
                              ("A", "#f9a825", "A 近似/手画"),
                              ("N", "#c62828", "N 不支持/占位"),
                              ("?", "#9e9e9e", "? 未判定")):
        vals = np.array([cnt[a][key] for a in ad], dtype=float)
        if vals.sum() == 0:
            continue
        ax.bar(x, vals, 0.58, bottom=bottoms, color=color, alpha=0.9, label=label)
        for xi, (v, b) in enumerate(zip(vals, bottoms)):
            if v > 0:
                ax.annotate(f"{v:.0f}", (xi, b + v / 2), ha="center", va="center",
                            fontsize=8, color="white")
        bottoms += vals
    ax.set_xticks(x, ad, rotation=20, ha="right", fontsize=9)
    ax.set_ylabel("特性数量")
    ax.set_title(f"⑦ 特性覆盖堆叠柱（共 {len(feats)} 项，按 spec/features.json 分组）")
    ax.legend(fontsize=8, loc="upper right")
    ax.grid(axis="y", ls=":", alpha=0.5)
    return _save(fig, outdir, "07_features.png")


def _norm(vals):
    """min-max 归一化到 [0,1]；全等/单值 → 0.5；None → 0。"""
    nums = [v for v in vals if v is not None and v > 0]
    if not nums:
        return [0.0 if v is None else 0.5 for v in vals]
    lo, hi = min(nums), max(nums)
    out = []
    for v in vals:
        if v is None or v <= 0:
            out.append(0.0)
        elif hi == lo:
            out.append(0.5)
        else:
            out.append((v - lo) / (hi - lo))
    return out


def radar_scores(res, fide, feats):
    """→ (adapter 列表, {维度: [0..1 分数]})；倒数型维度先取 1/x 再归一。"""
    cnt = coverage_counts(feats)
    means = {}
    for a, pages in fide.items():
        vs = [p.get("ssim") for p in pages.values() if p.get("ssim") is not None]
        if vs:
            means[a] = sum(vs) / len(vs)
    ad = [a for a in ADAPTER_ORDER
          if (res.get(a) and any(v is not None for v in res[a].values()))]
    if not ad:
        return [], {}

    def inv(metric):
        return [(1.0 / res[a][metric]) if (res.get(a) or {}).get(metric) else None
                for a in ad]
    dims = {
        "速度(1/延迟)": _norm(inv("latency_med")),
        "省内存(1/RSS)": _norm(inv("rss_peak")),
        "小体积(1/KB)": _norm(inv("size")),
        "还原度SSIM": _norm([means.get(a) for a in ad]),
        "特性覆盖": _norm([((cnt.get(a, {}).get("Y", 0) + cnt.get(a, {}).get("A", 0))
                            / cnt[a]["total"]) if a in cnt and cnt[a]["total"]
                           else None for a in ad]),
    }
    return ad, dims


def chart_radar(res, fide, feats, outdir):
    ad, dims = radar_scores(res, fide, feats)
    if not ad:
        return None
    labels = list(dims.keys())
    ang = np.linspace(0, 2 * np.pi, len(labels), endpoint=False).tolist()
    ang_c = ang + ang[:1]
    fig = plt.figure(figsize=(8.2, 7.4))
    ax = fig.add_subplot(111, polar=True)
    for i, a in enumerate(ad):
        vals = [dims[k][i] for k in labels] + [dims[labels[0]][i]]
        ax.plot(ang_c, vals, "-o", lw=1.5, ms=4, color=COLORS.get(a, "#777"),
                alpha=0.75, label=a)
        ax.fill(ang_c, vals, color=COLORS.get(a, "#777"), alpha=0.06)
    ax.set_xticks(ang)
    ax.set_xticklabels(labels, fontsize=10)
    ax.set_ylim(0, 1)
    ax.set_yticks([0.25, 0.5, 0.75, 1.0])
    ax.set_yticklabels(["0.25", "0.50", "0.75", "1.00"], fontsize=7, color="#888")
    ax.grid(ls=":", alpha=0.6)
    ax.set_title("⑧ 五维雷达（组内 min-max 归一化，1=该维最优；面积非面积语义）",
                 pad=22, fontsize=11)
    ax.legend(fontsize=8, loc="upper right", bbox_to_anchor=(1.28, 1.1))
    return _save(fig, outdir, "08_radar.png")


# ---------------------------------------------------------------- REPORT.md
def _fmt(v, metric):
    if v is None:
        return "—"
    if metric == "threadsafe":
        return {1.0: "✔", 0.0: "✘"}.get(v, "?")
    if metric == "pages":
        return f"{v:.0f}"
    if abs(v) >= 1000:
        return f"{v:,.0f}"
    if abs(v) >= 100:
        return f"{v:.1f}"
    return f"{v:.2f}"


# ---------------------------------------------------------------- 环境信息
def _cmd(args, timeout=10):
    try:
        r = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
        lines = (r.stdout or r.stderr or "").strip().splitlines()
        return lines[0].strip() if lines else ""
    except Exception:
        return ""


def _total_ram_gb():
    try:
        import ctypes

        class MEMORYSTATUSEX(ctypes.Structure):
            _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                        ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                        ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                        ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                        ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]

        m = MEMORYSTATUSEX()
        m.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
        ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m))
        return m.ullTotalPhys / 1024 ** 3
    except Exception:
        return None


def _nuget_versions():
    csproj = os.path.join(ROOT, "PdfBench.Runner", "PdfBench.Runner.csproj")
    out = []
    if not os.path.exists(csproj):
        return out
    txt = open(csproj, encoding="utf-8-sig").read()
    for m in re.finditer(r'<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"', txt):
        if m.group(1) == "PDFtoImage":
            continue  # 栅格化备用包，未参赛
        out.append(f"{m.group(1)} {m.group(2)}")
    if "PaperSharp.csproj" in txt:
        out.append("PaperSharp（仓库内 src/PaperSharp 本地工程）")
    return out


def _file_version(path):
    try:
        ps = ("(Get-Item -LiteralPath '%s').VersionInfo.ProductVersion" % path.replace("'", "''"))
        v = _cmd(["powershell", "-NoProfile", "-Command", ps])
        return v
    except Exception:
        return ""


def collect_env():
    """→ [(标签, 值)]；取不到的项值为空串，渲染成 —。"""
    import platform
    rows = []
    osname = f"{platform.system()} {platform.release()}"
    try:
        osname += f" (build {platform.windows_version().build})"
    except Exception:
        pass
    rows.append(("操作系统", osname))
    cpu = os.environ.get("PROCESSOR_IDENTIFIER", "")
    rows.append(("CPU", f"{cpu} / {os.cpu_count()} 逻辑核" if cpu else f"{os.cpu_count()} 逻辑核"))
    ram = _total_ram_gb()
    rows.append(("物理内存", f"{ram:.0f} GB" if ram else ""))
    rows.append((".NET SDK", _cmd(["dotnet", "--version"])))
    rows.append(("Python", f"{platform.python_version()} / matplotlib {matplotlib.__version__} / numpy {np.__version__}"))
    chrome = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
    rows.append(("Chrome（puppeteer 复用）",
                 _file_version(chrome) if os.path.exists(chrome) else ""))
    wk = os.path.expanduser(r"~\wkhtmltox\wkhtmltox\bin\wkhtmltopdf.exe")
    rows.append(("wkhtmltopdf", _cmd([wk, "--version"]) if os.path.exists(wk) else ""))
    rows.append(("被测库版本", " · ".join(_nuget_versions())))
    rows.append(("代码版本", _cmd(["git", "-C", ROOT, "rev-parse", "--short", "HEAD"])))
    return rows


# ---------------------------------------------------------------- 挑战页构成
def spec_summary():
    """读 spec/features.json → '3 页 A4 · 56 项特性：text 14 · border 15 …'。"""
    p = os.path.join(ROOT, "spec", "features.json")
    try:
        import json
        with open(p, encoding="utf-8") as fh:
            d = json.load(fh)
    except Exception:
        return "见 `spec/features.json`"
    groups = {}
    for f in d.get("features", []):
        groups[f.get("group", "?")] = groups.get(f.get("group", "?"), 0) + 1
    per_page = {}
    for f in d.get("features", []):
        per_page[f.get("page", "?")] = per_page.get(f.get("page", "?"), 0) + 1
    g = " · ".join(f"{k} {v}" for k, v in sorted(groups.items(), key=lambda t: -t[1]))
    nums = sorted(k for k in per_page if k != "all")
    pp = "逐页 " + "/".join(str(per_page[k]) for k in nums)
    if "all" in per_page:
        pp += f" + 文档级 {per_page['all']}"
    return (f"{d.get('pages', '?')} 页 {d.get('pageSize', '')}，{len(d.get('features', []))} 项特性"
            f"（{pp}）：{g}")


# ---------------------------------------------------------------- 逐页视觉对照图
RASTER_PREFIX = {"papersharp": "ps", "wkhtmltopdf": "wk"}


def _truth_ink(pages):
    """truth_pN.png 的非白像素占比（灰度 <245），供逐页明细对照。"""
    out = {}
    for p in pages:
        f = os.path.join(ROOT, "out", f"truth_p{p}.png")
        if not os.path.exists(f):
            continue
        try:
            img = np.asarray(plt.imread(f), dtype=np.float64)
            if img.ndim == 3:
                img = img[..., :3].mean(axis=2)
            if img.max() <= 1.0:  # matplotlib 会把 8-bit PNG 归一化到 0..1
                img *= 255.0
            out[p] = float((img < 245).mean() * 100)
        except Exception:
            pass
    return out


def chart_page_compare(page, charts_dir):
    """第 page 页：truth + 各库栅格图横向拼图 → 09/10/11_compare_pN.png。"""
    out_root = os.path.join(ROOT, "out")
    truth = os.path.join(out_root, f"truth_p{page}.png")
    if not os.path.exists(truth):
        raise FileNotFoundError(truth)
    tiles = [("truth (Chrome 基准)", truth, "#333333")]
    for a in ADAPTER_ORDER:
        p = os.path.join(out_root, f"{RASTER_PREFIX.get(a, a)}_p{page}.png")
        if os.path.exists(p):
            tiles.append((a, p, COLORS.get(a, "#333333")))
    n = len(tiles)
    cols = 4
    rows = math.ceil(n / cols)
    fig, axes = plt.subplots(rows, cols, figsize=(3.4 * cols, 4.8 * rows), dpi=100)
    fig.suptitle(f"第 {page} 页视觉对照（truth vs {n - 1} 个方案）", fontsize=14)
    axes = np.atleast_1d(axes).ravel()
    for ax in axes[n:]:
        ax.axis("off")
    for ax, (label, path, color) in zip(axes, tiles):
        ax.imshow(plt.imread(path))
        ax.set_title(label, fontsize=11, color=color)
        ax.axis("off")
    fig.tight_layout(rect=(0, 0, 1, 0.96))
    fn = f"{8 + page:02d}_compare_p{page}.png"
    path = os.path.join(charts_dir, fn)
    fig.savefig(path, dpi=100)
    plt.close(fig)
    print("  wrote", path)
    return fn


def chart_worst_cells(cells, outdir, top=14):
    """⑫ 每特性最差格的 Δ（ours − puppeteer 天花板）横条图，最差在上。"""
    items = [(f, c) for f, c in cells.items() if worst_delta(c) is not None]
    if not items:
        return None
    items.sort(key=lambda t: worst_delta(t[1]))
    items = items[:top][::-1]
    labels = [f"{f} [{cell_verdict(c)}]" for f, c in items]
    worst = [worst_delta(c) for _, c in items]
    means = [avg_delta(c) for _, c in items]
    x = np.arange(len(items))
    fig, ax = plt.subplots(figsize=(9.5, 0.42 * len(items) + 2.1))
    ax.barh(x - 0.19, worst, 0.36, color="#c62828", alpha=0.88, label="最差格 Δ")
    ax.barh(x + 0.19, means, 0.36, color="#f9a825", alpha=0.88, label="格均 Δ")
    ax.axvline(-0.03, color="#2e7d32", ls="--", lw=1, label="Y 线 −0.03")
    ax.axvline(-0.12, color="#616161", ls=":", lw=1, label="A 线 −0.12")
    for xi, v in zip(x, worst):
        ax.annotate(f"{v:+.3f}", (v, xi - 0.19), ha="right" if v < 0 else "left",
                    va="center", fontsize=7.5, xytext=(-3 if v < 0 else 3, 0),
                    textcoords="offset points")
    ax.set_yticks(x, labels, fontsize=8)
    ax.set_xlabel("Δ = 本网格 PaperSharp SSIM − puppeteer SSIM（负=比 Chrome 上限差）")
    ax.set_title(f"⑫ 逐格对账：最差的 {len(items)} 项特性"
                 f"（共 {len(cells)} 项 / {sum(len(c['rows']) for c in cells.values())} 格）")
    ax.grid(axis="x", ls=":", alpha=0.5)
    ax.legend(fontsize=7.5, loc="lower right")
    ax.set_xlim(min(min(worst), -0.12) * 1.25, 0.06)
    return _save(fig, outdir, "12_cells.png")


def build_report(res, fide, feats, outdir, charts, data_src, is_sample, cells=None):
    cells = cells or {}
    ad = [a for a in ADAPTER_ORDER if a in res]
    L = []
    L.append(f"# PdfBench 基准报告：{len(ad)} 个 .NET/无头浏览器方案生成同一份 HTML 挑战页")
    L.append("")
    src_note = ("**数据源：`report/sample/` 假数据（开发自测用，结论无效）。**"
                if is_sample else f"数据源：`{data_src}`（真实测量）。")
    L.append(f"> 生成时间：{_dt.datetime.now():%Y-%m-%d %H:%M} ｜ {src_note}")
    L.append("")
    L.append("## 方法学")
    L.append("")
    L.append("- **延迟**：warmup 5 次 + 30 次迭代，取 min/med/mean/p95/max；报告展示 med 柱 + p95 误差线。")
    L.append("- **GC**：`server=false`（workstation GC）；`alloc_med` 为单次生成 `GC.GetTotalAllocatedBytes` 差值中位数（托管分配，非占用）；gen0/1/2 为 30 迭代期间回收次数。")
    L.append("- **隔离**：每个库在**全新子进程**里测（`measure-one`），避免 Skia 等原生内存跨库累积把 `rss_peak` 抬到 GB 级；`rss_peak` 用 50 ms 轮询 Toolhelp32 进程树工作集求和，外部进程选手（puppeteer/wkhtmltopdf）含其 Chrome / wkhtmltopdf 子进程。")
    L.append("- **还原度**：truth 为无头 Chrome 以 `--force-device-scale-factor=2` 截的 2× 页图（裁去窗口白边到 1588px），各库 PDF 用 `raster.ps1`（WinRT PdfDocument）栅格化后逐页对拍 SSIM/MSE/pixel-diff（Δ灰度>10）。")
    L.append(f"- **特性矩阵**：{len(feats)} 项特性逐项判定 `Y`（原生/正确渲染）`A`（近似/手画）`N`（不支持/占位）`?`（未判定）；页面未实现的特性一律画**占位块**并计入 N。")
    if cells:
        ng = sum(len(c["rows"]) for c in cells.values())
        L.append(f"- **逐格对账（PaperSharp 列改用实测）**：整页 SSIM 会被大面积留白稀释，故另用 `out/cells.json`"
                 f"（无头 Chrome 现场量出的每个特性矩形）把基准图与引擎页图切成 {ng} 个格子逐格算 SSIM。"
                 "truth 与任何 PDF 栅格之间天然有字体/亚像素噪声，同格 puppeteer（Chrome 自己走一遍 PDF）也只有 0.90~0.94，"
                 "所以判定用**相对分** Δ = PaperSharp SSIM − puppeteer SSIM：Δ≥−0.03→Y，Δ≥−0.12→A，更差→N。")
    L.append("- **吞吐**：3 秒窗口内完成文档数（docs/s），串行与 4 线程各测一次；`threadsafe`=4 并发输出与串行字节一致。")
    L.append("- **冷启动**：独立进程单次生成总墙钟（含 JIT/字体/许可初始化）。")
    L.append("")
    if not is_sample:
        L.append("## 测试环境")
        L.append("")
        L.append("| 项 | 值 |")
        L.append("|---|---|")
        for k, v in collect_env():
            L.append(f"| {k} | {v or '—'} |")
        L.append("")
        L.append(f"- 挑战页：`spec/challenge.html`（{spec_summary()}）")
        L.append(f"- 原始数据：`out/results.csv`（{len(ad)} 库 × 16 指标）、`out/fidelity.csv`（21 行）、"
                 "`out/features_matrix.csv`（56 特性 × 8 库）；栅格图 `out/*_pN.png`。")
        L.append("")
    L.append("## 全指标总表")
    L.append("")
    if ad:
        L.append("| 库 | " + " | ".join(METRIC_CN[m] for m in METRICS) + " |")
        L.append("|---" * (len(METRICS) + 1) + "|")
        for a in ad:
            row = [f"**{a}**"] + [_fmt(res[a].get(m), m) for m in METRICS]
            L.append("| " + " | ".join(row) + " |")
    else:
        L.append("_（results.csv 无数据）_")
    L.append("")
    # 派生指标
    if ad:
        def ratio(a, num, den):
            x, y = res[a].get(num), res[a].get(den)
            return x / y if x and y else None
        L.append("## 派生指标（由上表算出）")
        L.append("")
        L.append("| 库 | 并发加速比(par4/串行) | 抖动(p95/med) | 每页耗时(ms) | 每页体积(KB) | 每页托管分配(KB) | 托管分配÷RSS峰值 |")
        L.append("|---|---|---|---|---|---|---|")
        for a in ad:
            pg = res[a].get("pages") or None
            med = res[a].get("latency_med")
            p95 = res[a].get("latency_p95")
            row = [
                _fmt(ratio(a, "throughput_par4", "throughput_serial"), ""),
                _fmt((p95 / med) if p95 and med else None, ""),
                _fmt((med / pg) if med and pg else None, ""),
                _fmt((res[a].get("size") / pg) if res[a].get("size") and pg else None, ""),
                _fmt((res[a].get("alloc_med") / pg) if res[a].get("alloc_med") and pg else None, ""),
                _fmt((res[a].get("alloc_med") / 1024 / res[a]["rss_peak"])
                     if res[a].get("alloc_med") and res[a].get("rss_peak") else None, ""),
            ]
            L.append(f"| **{a}** | " + " | ".join(row) + " |")
        L.append("")
        L.append("> 加速比≈1 说明加线程没用（外部进程被串行化或库内有锁）；`托管分配/RSS` 远小于 1 说明内存大头在原生侧（Skia/Chrome），"
                 "光看 .NET 分配会严重低估。")
        L.append("")
    L.append("## 图表")
    L.append("")
    for fn, cap in charts:
        if fn:
            L.append(f"### {cap}")
            L.append("")
            L.append(f"![{cap}](charts/{fn})")
            L.append("")
    # 还原度均值表
    L.append("## 还原度汇总（逐页均值，NaN 页跳过）")
    L.append("")
    cnt_keys = ("ssim", "mse", "pixel_diff_pct")
    if fide:
        L.append("| 库 | 平均SSIM | 平均MSE | 平均像素差% | 覆盖页数 |")
        L.append("|---|---|---|---|---|")
        for a in [x for x in ADAPTER_ORDER if x in fide]:
            m = {}
            for k in cnt_keys:
                vs = [p[k] for p in fide[a].values() if p.get(k) is not None]
                m[k] = sum(vs) / len(vs) if vs else None
            npg = sum(1 for p in fide[a].values() if p.get("ssim") is not None)
            L.append(f"| {a} | {_fmt(m['ssim'], '')} | {_fmt(m['mse'], '')} | "
                     f"{_fmt(m['pixel_diff_pct'], '')} | {npg} |")
    L.append("")
    if fide:
        pages = sorted({p for v in fide.values() for p in v})
        L.append("## 逐页还原度明细")
        L.append("")
        L.append("| 库 | " + " | ".join(f"第{p}页 SSIM / 像素差% / 落墨%" for p in pages) + " |")
        L.append("|---" + "|---" * len(pages) + "|")
        for a in [x for x in ADAPTER_ORDER if x in fide]:
            row = []
            for p in pages:
                d = fide[a].get(p)
                row.append("—" if not d or d.get("ssim") is None else
                           f"{d['ssim']:.3f} / {d['pixel_diff_pct']:.1f} / {d['ink_coverage_pct']:.1f}")
            L.append(f"| **{a}** | " + " | ".join(row) + " |")
        L.append("")
        L.append("> `落墨%` 是该页非白像素占比，用来识别「整页没画 / 画少了」的退化：某库某页的落墨% 明显低于 truth "
                 "而像素差% 很高，通常是内容被截断或整块未渲染，而不是画得不像。")
        tin = _truth_ink(pages)
        if tin:
            L.append("")
            L.append("> truth 落墨%（基准，越接近越好）："
                     + " ｜ ".join(f"第{p}页 {v:.1f}" for p, v in tin.items()))
        L.append("")
    cc = coverage_counts(feats)
    if feats:
        L.append("## 特性覆盖计数")
        L.append("")
        L.append("| 库 | Y | A | N | ? | 覆盖率(Y+A) |")
        L.append("|---|---|---|---|---|---|")
        for a in [x for x in ADAPTER_ORDER if x in cc]:
            d = cc[a]
            cov = (d["Y"] + d["A"]) / d["total"] if d["total"] else None
            L.append(f"| {a} | {d['Y']} | {d['A']} | {d['N']} | {d['?']} | "
                     f"{cov:.0%} |" if cov is not None else f"| {a} | {d['Y']} | {d['A']} | {d['N']} | {d['?']} | — |")
    L.append("")
    if feats:
        present = [x for x in ADAPTER_ORDER if x in (feats[0]["cells"] if feats else {})]
        L.append(f"## 完整特性矩阵（56 项 × {len(ADAPTER_ORDER)} 库）")
        L.append("")
        L.append("| 特性 | 分组 | 页 | " + " | ".join(present) + " |")
        L.append("|---|---|---|" + "---|" * len(present))
        mark = {"Y": "✅", "A": "🟡", "N": "❌", "?": "❔"}
        for r in feats:
            L.append("| `{}` | {} | {} | ".format(r["feature_id"], r["group"], r["pages"])
                     + " | ".join(mark.get(r["cells"].get(a, "?"), "❔") for a in present) + " |")
        L.append("")
        L.append("> ✅ 原生机制正确渲染 ｜ 🟡 近似/手画（视觉对但靠自绘、预混合、条纹、复制文字等） ｜ "
                 "❌ 不支持或灰块+红✗占位 ｜ ❔ 落在被裁/被截断区域，无从判定")
        L.append("")
    if cells:
        L.append("## PaperSharp 逐格对账明细（`out/cells.csv`）")
        L.append("")
        L.append("Δ = 本网格 PaperSharp SSIM − puppeteer SSIM（puppeteer＝Chrome 走同一 PDF 栅格通路，是同格能到的天花板）。"
                 "特性级取**最差格**：Δ≥−0.03→Y ｜ Δ≥−0.12→A ｜ 更差→N。")
        L.append("")
        L.append("| 特性 | 分组 | 格数 | 判定 | 最差Δ | 最差格页 | 格均Δ |")
        L.append("|---|---|---|---|---|---|---|")
        ordered = sorted(cells.items(), key=lambda t: (worst_delta(t[1]) is None,
                                                       worst_delta(t[1])))
        for fid, c in ordered:
            w = worst_delta(c)
            bad = c["rows"][0] if c["rows"] else {}
            a = avg_delta(c)
            L.append("| `{}` | {} | {} | {} | {} | {} | {} |".format(
                fid, c["group"], len(c["rows"]), cell_verdict(c),
                f"{w:+.3f}" if w is not None else "—",
                int(bad["page"]) if bad.get("page") is not None else "—",
                f"{a:+.3f}" if a is not None else "—"))
        v = collections.Counter(cell_verdict(c) for c in cells.values())
        ncell = collections.Counter(r["verdict"] for c in cells.values() for r in c["rows"])
        L.append("")
        L.append(f"- 特性级：Y {v.get('Y', 0)} ｜ A {v.get('A', 0)} ｜ N {v.get('N', 0)}"
                 f" ｜ ? {v.get('?', 0)}（共 {len(cells)} 项）")
        L.append(f"- 格级：Y {ncell.get('Y', 0)} ｜ A {ncell.get('A', 0)} ｜ N {ncell.get('N', 0)}"
                 f" ｜ ? {ncell.get('?', 0)}（共 {sum(len(c['rows']) for c in cells.values())} 格）")
        L.append("- 与整页 SSIM 的分工：整页分（`fidelity.csv`）看**排得出高低**，逐格分看**差在哪一块**；"
                 "上表 N 里若一批特性的最差格同页且 Δ 同量级，通常是纵向漂移而非该特性本身没实现，"
                 "用 `python fidelity/cells.py --pair <n>` 导出左右对照图确认。")
        L.append("")
    if FEATURE_NOTES:
        L.append("## 矩阵备注")
        L.append("")
        for a in [x for x in ADAPTER_ORDER if x in FEATURE_NOTES]:
            L.append(f"- **{a}**：{FEATURE_NOTES[a]}")
        L.append("")
    L.append("## 各维度最优 / 最差")
    L.append("")

    def pick(label, vals, best_low, note=""):
        items = [(a, v) for a, v in vals.items() if v is not None]
        if not items:
            return
        bv, wv = (min, max) if best_low else (max, min)
        b = bv(items, key=lambda t: t[1])
        w = wv(items, key=lambda t: t[1])
        L.append(f"- **{label}**：最优 `{b[0]}`（{_fmt(b[1], label)}），"
                 f"最差 `{w[0]}`（{_fmt(w[1], label)}）{note}")

    means = {}
    for a, pages in fide.items():
        vs = [p.get("ssim") for p in pages.values() if p.get("ssim") is not None]
        if vs:
            means[a] = sum(vs) / len(vs)
    pick("延迟 med（越低越好）", {a: res[a].get("latency_med") for a in ad}, True)
    pick("RSS 峰值（越低越好）", {a: res[a].get("rss_peak") for a in ad}, True)
    pick("输出体积（越低越好）", {a: res[a].get("size") for a in ad}, True)
    pick("4 线程吞吐（越高越好）", {a: res[a].get("throughput_par4") for a in ad}, False)
    pick("冷启动（越低越好）", {a: res[a].get("coldstart") for a in ad}, True)
    pick("平均 SSIM（越高越好）", means, False)
    pick("特性覆盖 Y+A（越高越好）",
         {a: (cc[a]["Y"] + cc[a]["A"]) for a in cc}, False)
    L.append("")
    concl = os.path.join(os.path.dirname(os.path.abspath(__file__)), "conclusions.md")
    if not is_sample and os.path.exists(concl):
        with open(concl, encoding="utf-8") as fh:
            L.append(fh.read().rstrip())
            L.append("")
    else:
        L.append("> TODO：人工结论段——各维度冠军的取舍建议。")
        L.append("")
    if not is_sample:
        L.append("## 附录：复现与口径")
        L.append("")
        L.append("```powershell")
        L.append("cd benchmarks/PdfBench")
        L.append("dotnet build PdfBench.Runner -c Debug")
        L.append("$exe = \"PdfBench.Runner/bin/Debug/net10.0/PdfBench.Runner.exe\"")
        L.append("& $exe measure            # → out/results.csv（每库一个子进程）")
        L.append("python fidelity/score.py  # → out/fidelity.csv + out/features_matrix.csv")
        if cells:
            L.append("& $exe cells              # → out/cells.json（无头 Chrome 现场量每个特性矩形，只准不改渲染）")
            L.append("python fidelity/cells.py  # → out/cells.csv（逐格 Δ，喂上面 PaperSharp 列的实测判定）")
        L.append("python report/report.py   # → report/charts/*.png + REPORT.md（本文件）")
        L.append("```")
        L.append("")
        L.append(f"- **必须每库一个子进程测**：同进程连测 {len(ADAPTER_ORDER)} 家会让 Skia 原生内存累积，`rss_peak` 虚高到 3–4 GB；"
                 "隔离后才是真实的 79–781 MB。")
        L.append("- **`threadsafe` 的比对口径**：剥离 `/CreationDate`、`/ID`、Xmp 日期与 iText 字体子集随机前缀"
                 "（`/ABCDEF+`）后逐字节比较；不剥会产生假 0。")
        L.append("- **`out/<lib>.pdf` 与测量用的临时产物不是同一个文件**：Aspose 两次生成体积能差约 10%"
                 "（水印与子集前缀），体积榜一律以 `results.csv` 为准。")
        L.append("- **还原度上限不是 1.0**：truth 来自 Chrome 截图、对照图来自 WinRT 栅格，天然 ≤2px 错位，"
                 "puppeteer 自己也只有 0.850，因此 SSIM 只用于排序。")
        L.append("- **wkhtmltopdf 依赖便携版**：官方 GitHub release 无 Windows 包，用 0.12.5 mxe-cross-win64 的 7z，"
                 "以 `C:\\Windows\\System32\\tar.exe` 解到 `~/wkhtmltox`。")
        L.append("- **IronPDF 未参赛**：2025.9.1 一调用即 `LicensingException: Production License Required`，"
                 "且会向工程注入 global using 污染其他适配器命名空间。")
        L.append("")
        L.append("产物清单：`out/results.csv`、`out/fidelity.csv`、`out/features_matrix.csv`、"
                 "`out/cells.json` + `out/cells.csv`（逐格）、"
                 "`out/<lib>.pdf` ×7、`out/<lib>_pN.png` ×24、`report/charts/*.png` ×12。")
        L.append("")
    path = os.path.join(outdir, "REPORT.md")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(L))
    print("  wrote", path)


# ---------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data-dir", default=None,
                    help="含 results/fidelity/features_matrix 三个 CSV 的目录；"
                         "默认 out/ 有真数据用 out/，否则用 sample/")
    ap.add_argument("--out-dir", default=HERE)
    ap.add_argument("--charts-subdir", default="charts")
    args = ap.parse_args()

    real = os.path.join(ROOT, "out", "results.csv")
    if args.data_dir:
        data_dir = os.path.abspath(args.data_dir)
    elif os.path.exists(real):
        data_dir = os.path.join(ROOT, "out")
    else:
        data_dir = os.path.join(HERE, "sample")
    outdir = os.path.abspath(args.out_dir)
    charts_dir = os.path.join(outdir, args.charts_subdir)
    is_sample = os.path.normpath(data_dir).lower() == os.path.normpath(
        os.path.join(HERE, "sample")).lower()
    print(f"[report] data-dir={data_dir}  out={charts_dir}")

    res = load_results(os.path.join(data_dir, "results.csv"))
    fide = load_fidelity(os.path.join(data_dir, "fidelity.csv"))
    feats = load_features(os.path.join(data_dir, "features_matrix.csv"))
    cells = load_cells(os.path.join(data_dir, "cells.csv"))
    print(f"[report] results:{len(res)}库 fidelity:{len(fide)}库 features:{len(feats)}项 "
          f"cells:{len(cells)}项/{sum(len(c['rows']) for c in cells.values())}格")

    specs = [
        ("01 延迟", lambda: chart_latency(res, charts_dir)),
        ("02 内存", lambda: chart_memory(res, charts_dir)),
        ("03 输出体积", lambda: chart_size(res, charts_dir)),
        ("04 吞吐与加速比", lambda: chart_throughput(res, charts_dir)),
        ("05 冷启动", lambda: chart_coldstart(res, charts_dir)),
        ("06 视觉还原度", lambda: chart_fidelity(fide, charts_dir)),
        ("07 特性覆盖", lambda: chart_features(feats, charts_dir)),
        ("08 五维雷达", lambda: chart_radar(res, fide, feats, charts_dir)),
    ]
    for pg in (1, 2, 3):
        specs.append((f"{8 + pg:02d} 第 {pg} 页视觉对照",
                      lambda pg=pg: chart_page_compare(pg, charts_dir)))
    if cells:
        specs.append(("12 逐格对账最差特性", lambda: chart_worst_cells(cells, charts_dir)))
    charts, failed = [], 0
    for cap, fn in specs:
        try:
            charts.append((fn(), cap))
        except Exception as e:  # 单图失败不影响其余
            failed += 1
            print(f"[report] WARN 图表 {cap} 生成失败: {type(e).__name__}: {e}",
                  file=sys.stderr)
            charts.append((None, cap))
    build_report(res, fide, feats, outdir, charts, data_src=data_dir,
                 is_sample=is_sample, cells=cells)
    ok = sum(1 for f, _ in charts if f)
    print(f"[report] done: {ok}/{len(specs)} charts, {failed} failed")
    return 0 if ok > 0 else 1


if __name__ == "__main__":
    sys.exit(main())
