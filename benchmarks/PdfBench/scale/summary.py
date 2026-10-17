#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 scale_cells.csv 里算出报告要引用的派生量（不是画图，是拿数）。

三件曲线图上看不准的事：
  1. **固定成本 + 边际成本**：延迟 ≈ a + b·规模。对数-对数拟合只给指数，给不出
     「一页值多少毫秒」和「不干活也要多少毫秒」，而服务端选型真正卡的是这两个数。
  2. **交叉点**：PaperSharp 固定成本低、Chromium 边际成本低，两条线一定相交。
     报告要说清「从第几页起翻过去」，不能停在「谁比谁快」。
  3. **重复轮次离散度**：round 1 vs round 2 的中位数偏离，给出这批数的可信带宽。

用法：python summary.py            # 打到 stdout（markdown 片段，可直接进 conclusions.md）
"""
import csv
import statistics
import sys
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"


def f(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def load():
    rows = list(csv.DictReader((OUT / "scale_cells.csv").open(newline="", encoding="utf-8")))
    for r in rows:
        r["med"] = f(r.get("wall_med_ms"))
        r["size_n"] = int(float(r["size"]))
    return rows


def best_regime(rows, engine, axis):
    """同一 (engine, axis) 优先取 warm，其次 warmvary，最后 cold。"""
    for reg in ("warm", "warmvary", "cold"):
        pts = {r["size_n"]: r["med"] for r in rows
               if r["engine"] == engine and r["axis"] == axis and r["regime"] == reg
               and r["med"] is not None}
        if len(pts) >= 2:
            return reg, pts
    return None, {}


def linfit(pts):
    """最小二乘 y = a + b·x；返回 (固定成本 a, 边际 b, 拟合 RMSE%)。"""
    xs = sorted(pts)
    ys = [pts[x] for x in xs]
    n = len(xs)
    mx, my = sum(xs) / n, sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    if sxx == 0:
        return None, None, None
    b = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx
    a = my - b * mx
    err = [abs((a + b * x) - y) / max(y, 1e-9) for x, y in zip(xs, ys)]
    return a, b, 100.0 * sum(err) / len(err)


def cross(p1, p2):
    """两条 延迟=a+b·x 直线的交点（x 可为负 → 表示在该轴范围内不相交）。"""
    a1, b1, _ = linfit(p1)
    a2, b2, _ = linfit(p2)
    if None in (a1, b1, a2, b2) or b1 == b2:
        return None
    return (a2 - a1) / (b1 - b2)


def main():
    rows = load()
    engines = sorted({r["engine"] for r in rows})
    print("### 固定成本 / 边际成本（对数无关的线性拟合，取各家最优口径）\n")
    print("| 引擎 | 轴 | 口径 | 固定成本 a (ms) | 边际 b (ms/单位) | 线性拟合平均误差 |")
    print("|---|---|---|---:|---:|---:|")
    fits = {}
    for e in engines:
        for axis in ("A", "B", "D", "E"):
            reg, pts = best_regime(rows, e, axis)
            if not pts or len(pts) < 3:
                continue
            a, b, err = linfit(pts)
            if a is None:
                continue
            fits[(e, axis)] = (a, b, pts)
            unit = {"A": "页", "B": "行", "D": "千字", "E": "图"}[axis]
            bshow = b * 1000 if axis == "D" else b
            blab = "ms/千字" if axis == "D" else f"ms/{unit}"
            print(f"| {e} | {axis} | {reg} | {a:.0f} | {bshow:.3f} {blab} | {err:.0f}% |")
    print("\n### 交叉点（正数=在该轴档位内会翻过去；负数=扫描范围内不交叉）\n")
    print("| 对 | 轴 | 交叉规模 | 判读 |")
    print("|---|---|---:|---|")
    for axis in ("A", "B"):
        other = "puppeteer" if "puppeteer" in {k[0] for k in fits} else None
        for e2 in [x for x in engines if x != "papersharp"]:
            if ("papersharp", axis) in fits and (e2, axis) in fits:
                xv = cross(fits[("papersharp", axis)][2], fits[(e2, axis)][2])
                if xv is None:
                    continue
                unit = {"A": "页", "B": "行"}[axis]
                verdict = ("扫描区间内不交叉" if xv <= 0
                           else f"{e2} 在 {xv:.0f} {unit} 之后反超")
                print(f"| papersharp vs {e2} | {axis} | {xv:.0f} {unit} | {verdict} |")

    print("\n### 吞吐（docs/s = 1000 / 单次中位延迟）\n")
    cells = [("A", 1), ("A", 10), ("A", 100), ("B", 2000)]
    print("| 引擎 | " + " | ".join(f"{a}{s}" for a, s in cells) + " |")
    print("|---" * (len(cells) + 1) + "|")
    for e in engines:
        out = []
        for axis, size in cells:
            hit = [r for r in rows if r["engine"] == e and r["axis"] == axis
                   and r["size_n"] == size and r["med"] is not None]
            hit.sort(key=lambda r: {"warm": 0, "warmvary": 1, "cold": 2}[r["regime"]])
            out.append(f"{1000.0 / hit[0]['med']:.2f}" if hit else "—")
        print(f"| {e} | " + " | ".join(out) + " |")

    print("\n### 重复轮次离散度（round 1 vs round 2 中位数偏离，同机同口径）\n")
    print("| 引擎 | 档 | 口径 | r1 (ms) | r2 (ms) | 偏离 |")
    print("|---|---|---|---:|---:|---:|")
    seen = defaultdict(dict)
    for r in rows:
        if r["med"] is not None:
            seen[(r["engine"], r["axis"], r["size_n"], r["regime"])][r["round"]] = r["med"]
    for k, v in sorted(seen.items()):
        if "1" in v and "2" in v:
            dev = 100.0 * (v["2"] - v["1"]) / v["1"]
            print(f"| {k[0]} | {k[1]}{k[2]} | {k[3]} | {v['1']:.0f} | {v['2']:.0f} | {dev:+.0f}% |")
    return 0


if __name__ == "__main__":
    sys.exit(main())
