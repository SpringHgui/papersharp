#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""integrity.csv 去重：同一 (engine, axis, size) 只保留最后一次判定。

背景：phase 2 用**初版 C 语料**跑过一次完整性，phase 3 把 C 语料修正后 `--force` 又跑了一次，
两批行都追加在同一个文件里。不去重，报告里会同时出现「puppeteer C10 页数 0.83×（旧语料对照）」
和「puppeteer C10 ok（新语料）」，读者无从分辨哪条是当前的。

同时给 scale_raw / scale_cells 做同键重复检查（只报不删）：
这两张表由 migrate_c_to_x.py + purge_regime.py 保证同键唯一，出现重复说明接线又错了。
"""
import csv
import sys
from collections import Counter
from pathlib import Path

OUT = Path(__file__).resolve().parent / "out"


def dedupe(path, key):
    rows = list(csv.DictReader(path.open(newline="", encoding="utf-8")))
    cols = list(rows[0].keys())
    last = {}
    for r in rows:
        last[tuple(r[k] for k in key)] = r          # 后写的覆盖先写的
    kept = list(last.values())
    if len(kept) != len(rows):
        with path.open("w", newline="", encoding="utf-8") as fh:
            w = csv.DictWriter(fh, fieldnames=cols)
            w.writeheader()
            w.writerows(kept)
    print(f"{path.name}: {len(rows)} → {len(kept)} 行（去掉 {len(rows)-len(kept)} 条同键旧判定）")


def check_unique(path, key):
    if not path.exists():
        return
    rows = list(csv.DictReader(path.open(newline="", encoding="utf-8")))
    c = Counter(tuple(r[k] for k in key) for r in rows)
    dup = {k: v for k, v in c.items() if v > 1}
    print(f"{path.name}: {len(rows)} 行，同键重复 {len(dup)} 组"
          + (f" ← 接线错了：{list(dup.items())[:3]}" if dup else "（干净）"))


def main():
    dedupe(OUT / "integrity.csv", ["engine", "axis", "size"])
    check_unique(OUT / "scale_cells.csv", ["engine", "regime", "axis", "size", "round"])
    check_unique(OUT / "scale_raw.csv", ["engine", "regime", "axis", "size", "round", "iter"])
    return 0


if __name__ == "__main__":
    sys.exit(main())
