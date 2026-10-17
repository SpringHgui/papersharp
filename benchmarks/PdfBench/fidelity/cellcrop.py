"""按特性名导出「浏览器真值 vs 引擎」的格子对照图，用来判一个 N 格到底是没画对还是几何错位。

cells.py 只给数字（Δ / aligned / shift），定位到具体特性时要看图：
    python fidelity/cellcrop.py headings weights           # 按特性名挑格
    python fidelity/cells.py --pair 10                     # 按差分大小挑最差的几格（导到 out/cells/）
产物 out/cell_<特性>_<序>.png，三栏 = 真值 | 引擎 | 差异二值图，按 --zoom 放大。
坐标口径与 cells.py 完全一致（同一份 cells.json + 同一套缩放），所以图上的偏差就是表里的偏差。
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

from cells import PAGE_CSS_W, TRUTH_CONTENT_W, crop, gray  # 同一套换算，别在这儿重抄一遍


def side_by_side(a: np.ndarray, b: np.ndarray, zoom: int) -> Image.Image:
    """两侧等宽拼接：真值 | 引擎 | 差异（|a−b| > 10 记白）。"""
    h = min(a.shape[0], b.shape[0])
    w = min(a.shape[1], b.shape[1])
    a, b = a[:h, :w], b[:h, :w]
    diff = (np.abs(a.astype(np.int16) - b.astype(np.int16)) > 10).astype(np.uint8) * 255
    gap = np.full((h, 4), 128, np.uint8)
    panel = np.hstack([a, gap, b, gap, diff])
    img = Image.fromarray(panel).convert('RGB')
    return img.resize((img.width * zoom, img.height * zoom), Image.NEAREST)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('features', nargs='*', default=[])
    ap.add_argument('--engine', default='ps')
    ap.add_argument('--verdict', choices=['Y', 'A', 'N'], default=None,
                    help='照 cells.csv 的判定挑格（cells.csv 与 cells.json 行序一致）；'
                         '配 --aligned 用对齐后的那一列')
    ap.add_argument('--aligned', action='store_true', help='--verdict 读 aligned_verdict 列')
    ap.add_argument('--out', default=None, help='out/ 目录，默认取本脚本同级 ../out')
    ap.add_argument('--zoom', type=int, default=3)
    args = ap.parse_args()

    out = Path(args.out) if args.out else Path(__file__).resolve().parent.parent / 'out'
    cells = json.load(io.open(out / 'cells.json', encoding='utf-8'))
    if args.verdict:
        col = 'aligned_verdict' if args.aligned else 'verdict'
        csv_rows = list(csv.DictReader(io.open(out / 'cells.csv', encoding='utf-8-sig')))
        assert len(csv_rows) == len(cells), f'cells.csv {len(csv_rows)} 行 / cells.json {len(cells)} 格，不同批'
        picked = [i for i, r in enumerate(csv_rows) if r[col] == args.verdict]
        # 同特性最多留 3 格：table-cell-borders 这类有 30+ 格，全导出来看不过来
        seen: dict[str, int] = {}
        sel = []
        for i in picked:
            c = cells[i]
            if seen.get(c['id'], 0) >= 3:
                continue
            seen[c['id']] = seen.get(c['id'], 0) + 1
            sel.append(c)
        cells, want = sel, {c['id'] for c in sel}
    else:
        want = set(args.features)
        if not want:
            print('要么给特性名，要么给 --verdict', file=sys.stderr)
            return 2
    pages = {c['page'] for c in cells if c['id'] in want}
    truth = {p: gray(out / f'truth_p{p}.png') for p in pages}
    eng = {p: gray(out / f"{args.engine}_p{p}.png") for p in pages}
    scale_t = {p: TRUTH_CONTENT_W / PAGE_CSS_W for p in pages}
    scale_e = {p: eng[p].shape[1] / PAGE_CSS_W for p in pages}

    n = 0
    for c in cells:
        if c['id'] not in want:
            continue
        p = c['page']
        st, se = scale_t[p], scale_e[p]
        w_t, h_t = max(2, int(c['w'] * st)), max(2, int(c['h'] * st))
        w_e, h_e = max(2, int(c['w'] * se)), max(2, int(c['h'] * se))
        a = crop(truth[p], int(c['x'] * st), int(c['y'] * st), w_t, h_t)
        b = crop(eng[p], int(c['x'] * se), int(c['y'] * se), w_e, h_e)
        f = out / f"cell_{c['id']}_{n}.png"
        side_by_side(a, b, args.zoom).save(f)
        print(f'wrote {f} {c["id"]} p{p} css=({c["x"]:.0f},{c["y"]:.0f},{c["w"]:.0f},{c["h"]:.0f})')
        n += 1
    if not n:
        print('cells.json 里没有这些特性：', args.features, file=sys.stderr)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
