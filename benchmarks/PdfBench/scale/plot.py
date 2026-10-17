#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""规模扫描出图 + 报告装配。

读 out/scale_cells.csv（逐 cell 聚合）与 corpus/manifest.json，产：
  charts/scale_<轴>_wall.png     —— log-log 延迟曲线（warm 实线 / cold 虚线）
  charts/scale_<轴>_rss.png      —— 同轴 RSS 峰值曲线
  charts/scale_<轴>_perunit.png  —— 单位规模归一（每页 / 每千元素 / 每万字符 / 每张图）
  charts/slopes.png              —— 拟合指数条形图（谁在超线性）
  REPORT.md                      —— 表格 + 曲线 + 拼 conclusions.md

斜率口径：log(y_ms) 对 log(x_size) 最小二乘。截距是「与规模无关的固定开销」（浏览器/解释器
冷启、字体初始化），斜率是「规模翻倍时代价涨几倍」——两个都要报，只报斜率会把冷启动贵的
选手在曲线上洗白。
"""
import csv
import json
import math
import statistics
from collections import defaultdict
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

plt.rcParams["font.sans-serif"] = ["Microsoft YaHei", "SimHei", "DejaVu Sans"]
plt.rcParams["axes.unicode_minus"] = False

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"
CHARTS = HERE / "charts"
CHARTS.mkdir(exist_ok=True)

AXIS_LABEL = {
    "A": ("A · 富样式整页 × N", "页数 N", "pages"),
    "B": ("B · 单张长表 × 行 N", "表格行数 N", "rows"),
    "C": ("C · 深嵌套 + 300 条规则（按元素数拟合）", "元素数 N（祖先深度 10→300）", "elements"),
    "D": ("D · CJK/Latin 混排长文本", "字符数 N", "chars"),
    "E": ("E · 图片张数 × N", "图片张数 N", "images"),
    "X": ("X · 退化宽度：每层 padding 累积（旧 C 语料）", "元素数 N（祖先深度 10→300）", "elements"),
    "Z": ("Z · 锚点：仓库既有样例", "1=invoice 2=challenge 3=report", "sample"),
}
# 单位归一的分母：把「固定开销」除干净，才看得出边际成本
UNIT_OF = {
    "A": ("ms / 页", "pages"),
    "B": ("ms / 千行", None),      # 用 size(rows)/1000
    "C": ("ms / 千元素", "elements"),
    "D": ("ms / 万字", None),      # 用 size(chars)/10000
    "E": ("ms / 十图", None),      # 用 size(images)/10
    "X": ("ms / 千元素", "elements"),
    "Z": ("ms / 页", "pages"),
}
# 拟合自变量：C/X 的名义档位是「深度」，但 reps=4000/depth 让元素数随深度反向变化，
# 拿深度做横轴会把「元素变少带来的加速」误读成「深度变深带来的加速」，所以这两轴按元素数拟合。
FIT_X = {"C": "elements", "X": "elements"}
COLORS = {
    "papersharp": "#d62728",
    "puppeteer": "#1f77b4",
    "playwright": "#2ca02c",
    "chrome-cli": "#7f7f7f",
    "wkhtmltopdf": "#9467bd",
    "weasyprint": "#ff7f0e",
    "weasyprint-cli": "#8c564b",
}


def f(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def load():
    rows = []
    with (OUT / "scale_cells.csv").open(newline="", encoding="utf-8") as fh:
        for r in csv.DictReader(fh):
            r["_med"] = f(r.get("wall_med_ms"))
            r["_rss"] = f(r.get("rss_peak_mb"))
            r["_size"] = int(float(r["size"]))
            r["_elems"] = f(r.get("elements"))
            r["_pages"] = f(r.get("pages"))
            r["_mspp"] = f(r.get("ms_per_page"))
            r["_ok"] = r.get("status") == "ok"
            rows.append(r)
    return rows


def fitx(r, axis):
    """该 cell 在该轴上的自变量取值（C/X 用元素数，其余用名义档位）。"""
    if FIT_X.get(axis) == "elements":
        return r["_elems"] or r["_size"]
    return r["_size"]


def cell_map(rows):
    """{(engine, regime, axis): {x: row}} 取 round=1。x 见 fitx()。"""
    d = defaultdict(dict)
    for r in rows:
        if r.get("round") not in ("1", "", None):
            continue
        d[(r["engine"], r["regime"], r["axis"])][fitx(r, r["axis"])] = r
    return {k: dict(sorted(v.items())) for k, v in d.items()}


def fit_slope(pairs):
    """log-log 最小二乘；返回 (斜率, R², 截距ms)。少于 3 点不拟合。"""
    pts = [(x, y) for x, y in pairs if x and y and x > 0 and y > 0]
    if len(pts) < 3:
        return None, None, None
    lx = [math.log(x) for x, _ in pts]
    ly = [math.log(y) for _, y in pts]
    n = len(pts)
    mx, my = sum(lx) / n, sum(ly) / n
    sxx = sum((a - mx) ** 2 for a in lx)
    sxy = sum((a - mx) * (b - my) for a, b in zip(lx, ly))
    if sxx == 0:
        return None, None, None
    slope = sxy / sxx
    inter = my - slope * mx
    ss_res = sum((b - (inter + slope * a)) ** 2 for a, b in zip(lx, ly))
    ss_tot = sum((b - my) ** 2 for b in ly)
    r2 = 1 - ss_res / ss_tot if ss_tot else None
    return slope, r2, math.exp(inter)


def unit_value(r, axis):
    med = r["_med"]
    if med is None:
        return None
    if axis == "A":
        return med / r["_size"]                      # 每页
    if axis == "B":
        return med / (r["_size"] / 1000.0)           # 每千行
    if axis == "C":
        e = r["_elems"] or 1
        return med / (e / 1000.0)                    # 每千元素
    if axis == "D":
        return med / (r["_size"] / 10000.0)          # 每万字符
    if axis == "E":
        return med / (r["_size"] / 10.0)             # 每十图
    if axis == "X":
        e = r["_elems"] or 1
        return med / (e / 1000.0)
    if axis == "Z":
        return med / (r["_pages"] or 1)
    return med


REGIME_STYLE = {"warm": "-o", "warmvary": "-^", "cold": "--s"}
REF_ENGINE = "chrome-cli"          # 与 integrity.py 同一个参照


def chart_wall(cm, axis, engines, path, ylim=None):
    label, xlabel, _ = AXIS_LABEL[axis]
    regimes = [r for r in ("warm", "warmvary", "cold") if any(k[1] == r for k in cm if k[2] == axis)]
    # 只测了 1 个档位的口径不单独开面板：一个点连不成曲线，画出来只会误导（D 轴 warmvary 就是这样）
    regimes = [r for r in regimes
               if max((len(cm.get((e, r, axis), {})) for e in engines), default=0) >= 2]
    if not regimes:
        regimes = ["warm"]
    fig, axes = plt.subplots(1, max(1, len(regimes)), figsize=(6.2 * len(regimes), 5.0),
                             sharex=True, squeeze=False)
    for ax, regime in zip(axes[0], regimes):
        have = False
        for e in engines:
            pts = cm.get((e, regime, axis))
            if not pts:
                continue
            xs = [s for s, r in pts.items() if r["_med"]]
            ys = [pts[s]["_med"] for s in xs]
            if not xs:
                continue
            have = True
            sl, r2, _ = fit_slope(list(zip(xs, ys)))
            # 拟合指数进图例而不是标在曲线末端：末端标注在冷启动面板里会互相压字（试过错开也不干净）
            ax.plot(xs, ys, REGIME_STYLE[regime], color=COLORS.get(e),
                    label=e if sl is None else f"{e}  n^{sl:.2f}", lw=1.8, ms=5)
        ax.set_xscale("log")
        ax.set_yscale("log")
        ax.set_xlabel(xlabel)
        ax.set_ylabel("单次生成中位延迟 (ms)")
        ax.set_title(f"{regime} 口径" + ("" if have else "（无数据）"), fontsize=10)
        ax.grid(True, which="both", alpha=.25)
        ax.legend(fontsize=8)
    fig.suptitle(f"{label} —— 延迟规模曲线（log-log；图例里的 n^k 是该曲线的拟合指数）", fontsize=11)
    fig.tight_layout(rect=[0, 0, 1, 0.95])
    fig.savefig(path, dpi=120)
    plt.close(fig)


def chart_rss(cm, axis, engines, path):
    label, xlabel, _ = AXIS_LABEL[axis]
    fig, ax = plt.subplots(figsize=(6.6, 4.6))
    for e in engines:
        for regime, style in (("warm", "-o"), ("cold", "--s")):
            pts = cm.get((e, regime, axis))
            if not pts:
                continue
            xs = [s for s, r in pts.items() if r["_rss"]]
            ys = [pts[s]["_rss"] for s in xs]
            if xs:
                ax.plot(xs, ys, style, color=COLORS.get(e), label=f"{e} {regime}", lw=1.6, ms=4)
    ax.set_xscale("log")
    ax.set_yscale("log")
    ax.set_xlabel(xlabel)
    ax.set_ylabel("进程树 RSS 峰值 (MB)")
    ax.set_title(f"{label} —— 内存规模曲线", fontsize=10)
    ax.grid(True, which="both", alpha=.25)
    ax.legend(fontsize=7.5)
    fig.tight_layout()
    fig.savefig(path, dpi=120)
    plt.close(fig)


def chart_perunit(cm, axis, engines, path):
    label, xlabel, _ = AXIS_LABEL[axis]
    unit, _ = UNIT_OF[axis]
    fig, ax = plt.subplots(figsize=(6.6, 4.6))
    for e in engines:
        pts = cm.get((e, "warm", axis)) or cm.get((e, "cold", axis))
        if not pts:
            continue
        xs = sorted(pts)
        ys = [unit_value(pts[s], axis) for s in xs]
        ys = [v for v in ys if v]
        if not xs or not ys:
            continue
        ax.plot(xs[:len(ys)], ys, "-o", color=COLORS.get(e), label=e, lw=1.7, ms=4)
    ax.set_xscale("log")
    ax.set_yscale("log")
    ax.set_xlabel(xlabel)
    ax.set_ylabel(unit)
    ax.set_title(f"{label} —— 边际成本（warm）", fontsize=10)
    ax.grid(True, which="both", alpha=.25)
    ax.legend(fontsize=8)
    fig.tight_layout()
    fig.savefig(path, dpi=120)
    plt.close(fig)


def chart_slopes(cm, engines, path):
    axes = sorted(a for a in {k[2] for k in cm} if a != "Z")   # Z 是分类型锚点，拟合指数无意义
    fig, ax = plt.subplots(figsize=(9.6, 4.8))
    w = 0.8 / max(1, len(axes))
    for i, axis in enumerate(axes):
        xs, labs = [], []
        for e in engines:
            pts = (cm.get((e, "warm", axis)) or cm.get((e, "warmvary", axis))
                   or cm.get((e, "cold", axis)))
            if not pts:
                continue
            sl, r2, _ = fit_slope([(s, r["_med"]) for s, r in pts.items()])
            if sl is None:
                continue
            xs.append(sl)
            labs.append(f"{e}\n{axis}")
        if xs:
            ax.bar([i + (j - len(xs) / 2) * w for j in range(len(xs))], xs, w * 0.9,
                   color=[COLORS.get(l.split("\n")[0], "#333") for l in labs], alpha=.85)
            for j, v in enumerate(xs):
                ax.annotate(f"{v:.2f}", (i + (j - len(xs) / 2) * w, v), ha="center",
                            va="bottom", fontsize=7)
        ax.annotate(f"轴 {axis}", (i, -0.06), ha="center", va="top", fontsize=9,
                    annotation_clip=False)
    ax.axhline(1.0, color="#888", ls=":", lw=1)
    ax.annotate("线性 n¹", (len(axes) - 0.45, 1.02), fontsize=8, color="#666")
    ax.set_xticks([])
    ax.set_ylabel("拟合指数（延迟 ∝ n^k）")
    ax.set_title("各规模轴的拟合指数（越接近 1 越线性，越高越危险）", fontsize=11)
    fig.tight_layout()
    fig.savefig(path, dpi=120)
    plt.close(fig)


def md_table(headers, rowsw):
    out = ["| " + " | ".join(headers) + " |",
           "|" + "|".join(["---"] * len(headers)) + "|"]
    for r in rowsw:
        out.append("| " + " | ".join(str(c) for c in r) + " |")
    return "\n".join(out)


def integrity_section():
    """把完整性通道的判定并进报告：延迟曲线的读法要先过这层折扣。"""
    p = OUT / "integrity.csv"
    if not p.exists():
        return "## 完整性折扣\n\n（未跑 `integrity.py`，本报告的时间数字**没有**完整性折扣依据）\n"
    rows = list(csv.DictReader(p.open(newline="", encoding="utf-8")))
    verd = ["ok", "少画", "少画图像", "页数不一致", "尾部截断", "no-pdf"]

    def cls(v):
        for k in reversed(verd):
            if v.startswith(k):
                return k
        return "ok"

    cnt, flagged = defaultdict(lambda: defaultdict(int)), defaultdict(list)
    refs = {(r["axis"], str(int(float(r["size"])))): r for r in rows
            if r["engine"] == REF_ENGINE}
    for r in rows:
        k = cls(r["verdict"])
        cnt[r["engine"]][k] += 1
        if k != "ok":
            key = (r["axis"], str(int(float(r["size"]))))
            ref = refs.get(key)
            extra = ""
            if ref and k in ("少画", "页数不一致") and float(ref["chars"] or 0) > 0:
                extra = f"(字符 {int(r['chars']) / float(ref['chars']):.0%})"
            flagged[r["engine"]].append(f"{r['axis']}{int(float(r['size']))}:{k}{extra}")
    hdr = ["引擎"] + verd + ["档位合计"]
    body = []
    for e in sorted(cnt):
        tot = sum(cnt[e].values())
        body.append([f"`{e}`"] + [str(cnt[e].get(v, 0)) for v in verd] + [str(tot)])
    out = ["## 完整性折扣（读上面三条曲线之前先看这页）", "",
           f"参照 = `{REF_ENGINE}`（无头 Chrome 直出）。判据是产物里抽出的字符数、图像 XObject 数、"
           "页数、末页字符数；产物都留在 `out/artifacts/`。", "",
           md_table(hdr, body), ""]
    for e in sorted(flagged):
        out += [f"- **`{e}`**：{', '.join(sorted(set(flagged[e])))}"]
    out += ["", "> 结论级提醒：任何一家的延迟只有在**它把这一档内容真画出来了**的时候才能和别家相减。",
            "> `少画`＝产物字符/图像显著少于参照；`页数不一致`＝分页行为不同（可能多画也可能少画）。"]
    return "\n".join(out) + "\n"


def build_report(cm, rows, engines):
    L = ["# HTML→PDF 同类工具 · 规模扫描报告", "",
         "同一份语料（`corpus/*.html`，五条规模轴 A–E + 锚点 Z + 退化宽度探针 X，"
         "每档逐字节相同）喂给所有引擎，比的是**文档变大之后谁先垮**。"
         "warm=常驻进程（服务端形态），cold=每次新进程（CLI 形态），warmvary=常驻但每次换文档。",
         ""]
    # 0 速览
    L += ["## 0. 速览", ""]
    hdr = ["引擎", "赛道", "A1 warm(ms)", "A100 warm(ms)", "A 指数", "B 指数", "C 指数",
           "D 指数", "E 指数", "RSS 峰值(MB)"]
    body = []
    for e in engines:
        a1 = (cm.get((e, "warm", "A"), {}).get(1) or cm.get((e, "cold", "A"), {}).get(1))
        a100 = (cm.get((e, "warm", "A"), {}).get(100) or cm.get((e, "cold", "A"), {}).get(100))
        slopes = []
        for axis in "ABCDE":
            pts = cm.get((e, "warm", axis)) or cm.get((e, "warmvary", axis)) \
                or cm.get((e, "cold", axis))
            sl, _, _ = fit_slope([(s, r["_med"]) for s, r in pts.items()]) if pts else (None,) * 3
            slopes.append("—" if sl is None else f"{sl:.2f}")
        rss = [r["_rss"] for (en, _, _), d in cm.items() if en == e for r in d.values() if r["_rss"]]
        body.append([
            e, CHANNEL.get(e, ""),
            "—" if not a1 or not a1["_med"] else f"{a1['_med']:.0f}",
            "—" if not a100 or not a100["_med"] else f"{a100['_med']:.0f}",
            *slopes,
            "—" if not rss else f"{max(rss):.0f}",
        ])
    L += [md_table(hdr, body), ""]
    L += ["指数拟合口径：log-log 最小二乘，warm 优先（缺 warm 时用 warmvary、再用 cold；"
          "不同口径的行不会混进同一条拟合）。自变量：A/B/D/E 用名义档位，C/X 用元素数（见 README）。"
          "**固定成本 + 边际成本的线性拟合、两家曲线的交叉规模、吞吐与轮间离散度"
          "在 `out/summary.md`（`python summary.py` 重生成）。", ""]

    # 逐轴明细
    for axis in sorted({k[2] for k in cm}):
        label, xlabel, _ = AXIS_LABEL[axis]
        unit, _ = UNIT_OF[axis]
        L += [f"## 轴 {label}", ""]
        if (CHARTS / f"scale_{axis}_wall.png").exists():
            L += [f"![{axis} 延迟](charts/scale_{axis}_wall.png)", ""]
        for regime in ("warm", "warmvary", "cold"):
            have = [e for e in engines if cm.get((e, regime, axis))]
            if not have:
                continue
            L += [f"**{regime} 口径**", ""]
            sizes = sorted({s for e in have for s in cm[(e, regime, axis)]})
            xl = AXIS_LABEL[axis][1].split("（")[0].split(" ")[-1] or "规模"
            hdr = (["规模 " + xl] + [f"{e} ms" for e in have]
                   + [f"{e} {unit}" for e in have] + [f"{e} 实测页数" for e in have])
            body = []
            for s in sizes:
                row = [f"{s:g}"]
                for e in have:
                    r = cm[(e, regime, axis)].get(s)
                    row.append("—" if not r or not r["_med"] else f"{r['_med']:.0f}")
                for e in have:
                    r = cm[(e, regime, axis)].get(s)
                    v = unit_value(r, axis) if r else None
                    row.append("—" if v is None else f"{v:.1f}")
                for e in have:
                    r = cm[(e, regime, axis)].get(s)
                    row.append("—" if not r or not r["_pages"] else f"{r['_pages']:g}")
                body.append(row)
            L += [md_table(hdr, body), ""]
        if (CHARTS / f"scale_{axis}_rss.png").exists():
            L += [f"![{axis} 内存](charts/scale_{axis}_rss.png)",
                  f"![{axis} 边际](charts/scale_{axis}_perunit.png)", ""]

    # 公平性/完整性
    L += ["## 口径体检（横向比之前先看这页）", ""]
    bad = [r for r in rows if r.get("round") in ("1", "") and (not r["_ok"] or (r["_pages"] or 0) <= 0)]
    hdr = ["引擎", "口径", "档", "状态", "n_ok/n", "页数", "MediaBox(pt)", "首个错误"]
    body = []
    seen = set()
    for r in bad:
        key = (r["engine"], r["regime"], r["size"])
        if key in seen:
            continue
        seen.add(key)
        body.append([r["engine"], r["regime"], f"{r['axis']}{r['size']}", r.get("status"),
                     f"{r['n_ok']}/{r['n']}", r.get("pages") or "—",
                     f"{r.get('mw_pt') or '—'}x{r.get('mh_pt') or '—'}",
                     (r.get("err") or "")[:60]])
    L += [md_table(hdr, body) if body else "全部 cell 正常出 PDF。", ""]
    box = sorted({(r["engine"], r.get("mw_pt"), r.get("mh_pt")) for r in rows
                  if r.get("round") in ("1", "") and r.get("mw_pt")})
    L += ["**页面尺寸一致性**（吃同一份 @page 的证据）：", ""]
    for e, w, h in box:
        L += [f"- `{e}`：{w}×{h} pt"]
    L += ["", "![slopes](charts/slopes.png)", ""]

    L += ["", integrity_section(), "## 判据列的优先级", "",
          "`verdict` 按「图像 → 字符 → 页数 → 末页」顺序取第一个越界的：图像轴先看图，"
          "文本轴先看字符。所以 `页数不一致` 不等于「只是分页不同而内容齐全」——"
          "例如同一个 cell 既可能页数差 3 倍、字符也只剩 3 成，那会先被字符判成 `少画`；"
          "反过来 `页数不一致` 的行是字符没越界（≥0.9）但页数越界。所有原始比值都在 "
          "`out/integrity.csv` 里，别只读这一列。", ""]

    env = OUT / "env.json"
    if env.exists():
        d = json.loads(env.read_text(encoding="utf-8"))
        g = d.get("git", {})
        L += ["## 这批数字是在什么状态下量的", "",
              f"- 时间 {d.get('time')}；{d.get('os')}；{d.get('cores')} 核；"
              f"dotnet {d.get('dotnet')} / node {d.get('node')} / python {d.get('python')}",
              f"- 引擎版本：Chrome {d.get('chrome')}、wkhtmltopdf {d.get('wkhtmltopdf')}、"
              f"WeasyPrint {d.get('weasyprint')}",
              f"- PaperSharp：`{g.get('branch')}@{g.get('head')}`（{g.get('head_subject')}），"
              f"**工作树不干净：{g.get('dirty_files')} 个改动文件，其中 {g.get('dirty_src_files')} 个在 `src/`**"
              " —— 本报告的引擎侧数字全部是这棵在途树上的结果，回到提交态要重跑",
              f"- 参赛适配器：{', '.join(d.get('adapters_present', []))}",
              f"- 口径：{json.dumps(d.get('measurement_policy', {}), ensure_ascii=False)}", ""]

    conc = HERE / "conclusions.md"
    if conc.exists():
        L += ["## 解读与取舍", "", conc.read_text(encoding="utf-8").strip(), ""]
    (HERE / "REPORT.md").write_text("\n".join(L), encoding="utf-8")
    print(f"wrote REPORT.md ({len(L)} 段)")


CHANNEL = {
    "papersharp": "自研 CSS 引擎 + Skia",
    "puppeteer": "Chromium（Puppeteer 包装）",
    "playwright": "Chromium（Playwright 包装）",
    "chrome-cli": "Chromium（CLI 直出）",
    "wkhtmltopdf": "Qt WebKit",
    "weasyprint": "WeasyPrint 独立排版引擎",
    "weasyprint-cli": "WeasyPrint CLI",
}


def main():
    rows = load()
    cm = cell_map(rows)
    engines = sorted({k[0] for k in cm}, key=lambda e: (e != "papersharp", e))
    for axis in sorted({k[2] for k in cm}):
        chart_wall(cm, axis, engines, OUT / f"scale_{axis}_wall.png")
        chart_rss(cm, axis, engines, OUT / f"scale_{axis}_rss.png")
        chart_perunit(cm, axis, engines, OUT / f"scale_{axis}_perunit.png")
    # 图表落 charts/ 下，README 里用相对路径引用
    for p in OUT.glob("scale_*.png"):
        p.replace(CHARTS / p.name)
    chart_slopes(cm, engines, CHARTS / "slopes.png")
    build_report(cm, rows, engines)


if __name__ == "__main__":
    main()
