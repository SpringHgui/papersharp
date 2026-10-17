#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""逐特性（cell）像素对账：Chrome 量出的每个 feature 矩形同时裁自基准图与引擎页图，算差异。

前置：
  PdfBench.Runner cells      → out/cells.json（各特性在所在 .page 内的 CSS px 矩形）
  python fidelity/score.py   → out/<prefix>_pN.png（含 ps_pN.png 与 puppeteer_pN.png）

为什么要参照引擎：truth 与任何 PDF 光栅化结果之间天然有字体渲染/亚像素噪声，同一个格子
puppeteer（就是 Chrome 自己走一遍 PDF）也只能到 0.90~0.94。所以判定用**相对分**：
以参照引擎（默认 puppeteer）在同一格子的 SSIM 为天花板，我们差多少才是真缺口。
  Δ = ours_ssim - ref_ssim：Δ>=-0.03 → Y；Δ>=-0.12 → A；更差 → N。

用法：python fidelity/cells.py [--engine ps] [--ref puppeteer] [--pair N] [--worst N]
                               [--align 8]

--align 会把每个格子在纵向 ±N 设备像素内滑动取最优 SSIM，额外报 aligned 列。用途是**归因**：
纵向漂移（行距/块高与 Chrome 差 ~1px/行、半页累积到 10px）会让一整页的格子一起掉分，
对齐后分数回升到 A/Y 的那些格属于几何错位连带，不是该特性没画对。
"""
from __future__ import annotations

import argparse
import csv
import io
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "out"
PAGE_CSS_W = 794.0          # A4 @ 96dpi：cells.json 的 CSS px 坐标按此折算到页图
TRUTH_CONTENT_W = 1588      # truth 2× 图右侧 112px 是窗口白边，内容恒占左侧 1588
DIFF_LEVEL = 10             # |Δ灰度| > 10 记差异像素

try:
    from skimage.metrics import structural_similarity as _ssim
except Exception:
    _ssim = None


def gray(path: Path) -> np.ndarray:
    return np.asarray(Image.open(path).convert('L')).astype(np.int16)


def ssim(a: np.ndarray, b: np.ndarray) -> float:
    if _ssim is not None:
        return float(_ssim(a, b, data_range=255))
    va, vb = a - a.mean(), b - b.mean()
    d = np.sqrt((va ** 2).mean() * (vb ** 2).mean())
    return float((va * vb).mean() / d) if d > 1e-6 else 1.0


def crop(img: np.ndarray, x: int, y: int, w: int, h: int) -> np.ndarray:
    H, W = img.shape
    return img[max(0, y):min(H, y + h), max(0, x):min(W, x + w)]


def best_shift(truth: np.ndarray, eng: np.ndarray, c: dict, scale_t: float, scale_e: float,
               span_y: int, span_x: int):
    """格尺寸与 (0,0) 时完全一致，只把引擎侧窗口平移 ±span 设备像素取最优 SSIM。

    因为 (0,0) 也在候选里，aligned 恒 >= raw，可直接与同列 ref 比出「去掉几何错位后还差多少」。
    """
    x_t, y_t = int(c['x'] * scale_t), int(c['y'] * scale_t)
    x_e, y_e = int(c['x'] * scale_e), int(c['y'] * scale_e)
    t = crop(truth, x_t, y_t, max(2, int(c['w'] * scale_t)), max(2, int(c['h'] * scale_t)))
    e0 = crop(eng, x_e, y_e, max(2, int(c['w'] * scale_e)), max(2, int(c['h'] * scale_e)))
    h, w = min(t.shape[0], e0.shape[0]), min(t.shape[1], e0.shape[1])
    if h < 4 or w < 4:
        return None, 0, 0
    t = t[:h, :w]
    best, by, bx = -1.0, 0, 0
    for dy in range(-span_y, span_y + 1):
        for dx in range(-span_x, span_x + 1):
            oy, ox = y_e + dy, x_e + dx
            if oy < 0 or ox < 0:
                continue
            sub = eng[oy:oy + h, ox:ox + w]
            if sub.shape != (h, w):
                continue
            s = ssim(t, sub)
            if s > best:
                best, by, bx = s, dy, dx
    return (best, by, bx) if best >= 0 else (None, 0, 0)


def metrics(truth: np.ndarray, eng: np.ndarray, c: dict, scale_t: float, scale_e: float):
    t = crop(truth, int(c['x'] * scale_t), int(c['y'] * scale_t),
             max(2, int(c['w'] * scale_t)), max(2, int(c['h'] * scale_t)))
    e = crop(eng, int(c['x'] * scale_e), int(c['y'] * scale_e),
             max(2, int(c['w'] * scale_e)), max(2, int(c['h'] * scale_e)))
    h, w = min(t.shape[0], e.shape[0]), min(t.shape[1], e.shape[1])
    if h < 3 or w < 3:
        return None
    t, e = t[:h, :w], e[:h, :w]
    return ssim(t, e), float((np.abs(t - e) > DIFF_LEVEL).mean() * 100), t, e


def judge(delta: float) -> str:
    return 'Y' if delta >= -0.03 else ('A' if delta >= -0.12 else 'N')


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--engine', default='ps', help='被评引擎的页图前缀')
    ap.add_argument('--ref', default='puppeteer', help='天花板参照前缀（空串=不参照，按绝对分判）')
    ap.add_argument('--pair', type=int, default=0, help='导出差异最大的 N 张左右对照图')
    ap.add_argument('--worst', type=int, default=40, help='打印最差的 N 个格子')
    ap.add_argument('--align', type=int, default=8,
                    help='纵向对齐搜索半径（设备像素）；0=关掉 aligned 列')
    ap.add_argument('--align-x', type=int, default=2, help='横向对齐搜索半径')
    args = ap.parse_args()

    cells = json.load(io.open(OUT / 'cells.json', encoding='utf-8'))
    need = {c['page'] for c in cells}
    truth = {p: gray(OUT / f'truth_p{p}.png') for p in need}
    engines = {'_ours': {p: gray(OUT / f"{args.engine}_p{p}.png") for p in need}}
    if args.ref:
        engines['_ref'] = {p: gray(OUT / f"{args.ref}_p{p}.png") for p in need}
    scale_t = {p: TRUTH_CONTENT_W / PAGE_CSS_W for p in need}
    scale_e = {p: engines['_ours'][p].shape[1] / PAGE_CSS_W for p in need}

    rows = []
    for c in cells:
        p = c['page']
        ours = metrics(truth[p], engines['_ours'][p], c, scale_t[p], scale_e[p])
        if ours is None:
            continue
        ref = metrics(truth[p], engines['_ref'][p], c, scale_t[p], scale_e[p]) if args.ref else None
        s, diff = ours[0], ours[1]
        delta = s - (ref[0] if ref else 1.0)
        al, ady, adx = (s, 0, 0)
        if args.align or args.align_x:
            best = best_shift(truth[p], engines['_ours'][p], c, scale_t[p], scale_e[p],
                              args.align, args.align_x)
            if best is not None and best[0] > al:
                al, ady, adx = best[0], best[1], best[2]
        rows.append((c['id'], c['group'], p, round(s, 4), round(diff, 2),
                     f'{ref[0]:.4f}' if ref else '  -   ', round(delta, 4),
                     judge(delta), round(al, 4),
                     judge(al - (ref[0] if ref else 1.0)), f'{ady:+d}/{adx:+d}',
                     c, ours[2], ours[3]))

    # 原序留在 csv_rows 里（与 cells.json 行序一致，cellcrop.py 按下标挑格要用它）
    csv_rows = list(rows)
    rows.sort(key=lambda r: r[6])
    with io.open(OUT / 'cells.csv', 'w', encoding='utf-8', newline='') as f:
        wr = csv.writer(f)
        wr.writerow(['feature_id', 'group', 'page', f'{args.engine}_ssim', 'diff_pct',
                     f'{args.ref or "ref"}_ssim', 'delta', 'verdict',
                     'aligned_ssim', 'aligned_verdict', 'shift_y/x'])
        for r in csv_rows:
            wr.writerow(r[:11])

    print(f'{"feature":32s} grp      pg  ours    ref     delta  diff%  verdict  对齐后')
    for r in rows[:args.worst]:
        print(f'{r[0]:32s} {r[1]:8s} {r[2]:3}  {r[3]:.4f}  {r[5]}  '
              f'{r[6]:+.4f}  {r[4]:5.2f}  {r[7]}   {r[9]} ({r[10]})')
    n = [sum(1 for r in rows if r[7] == v) for v in 'YAN']
    na = [sum(1 for r in rows if r[9] == v) for v in 'YAN']
    print(f'— {len(rows)} cells：Y={n[0]} A={n[1]} N={n[2]}（engine={args.engine} ref={args.ref or "无"}，全表 out/cells.csv）')
    if args.align:
        flipped = [r for r in rows if r[7] == 'N' and r[9] in 'YA']
        print(f'  纵向对齐（±{args.align}px）后：Y={na[0]} A={na[1]} N={na[2]}；'
              f'{len(flipped)} 格从 N 升为 A/Y => 这些是几何错位连带，不是该特性没画对')

    if args.pair:
        d = OUT / 'cells'
        d.mkdir(exist_ok=True)
        for i, r in enumerate(rows[:args.pair]):
            t, e = r[12], r[13]
            w = min(t.shape[1], e.shape[1])
            canvas = np.full((min(t.shape[0], e.shape[0]), w * 2 + 8), 255, np.uint8)
            canvas[:, :w] = t[:, :w]
            canvas[:, w + 8:] = e[:canvas.shape[0], :w]
            Image.fromarray(canvas).save(d / f"p{r[2]}_{r[0]}_{i}.png")
        print(f'对照图（左 truth / 右 engine）：out/cells/ 最差 {args.pair} 格')
    return 0


if __name__ == '__main__':
    sys.exit(main())
