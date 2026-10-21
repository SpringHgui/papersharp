#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""按口径删除测量行（用于作废有明显口径缺陷的那批数据，而不是留着装看不见）。

用法：python purge_regime.py warmvary
把 scale_raw.csv / scale_cells.csv 里该 regime 的行剔掉；sweep.py 的续跑判定就不再跳过这些 cell。
"""
import csv
import sys
from pathlib import Path

OUT = Path(__file__).resolve().parent / "out"


def purge(path, regime):
    if not path.exists():
        return 0
    rows = list(csv.DictReader(path.open(newline="", encoding="utf-8")))
    cols = list(rows[0].keys()) if rows else []
    keep = [r for r in rows if r.get("regime") != regime]
    n = len(rows) - len(keep)
    if n:
        with path.open("w", newline="", encoding="utf-8") as fh:
            w = csv.DictWriter(fh, fieldnames=cols)
            w.writeheader()
            w.writerows(keep)
    return n


def main():
    regime = sys.argv[1] if len(sys.argv) > 1 else ""
    if not regime:
        print("用法: python purge_regime.py <regime>")
        return 2
    for name in ("scale_raw.csv", "scale_cells.csv"):
        n = purge(OUT / name, regime)
        print(f"{name}: 剔除 {n} 行 regime={regime}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
