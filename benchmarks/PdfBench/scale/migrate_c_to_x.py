#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把初版 C 语料的产物整体改挂到 X 轴（一次性迁移，可重复执行、幂等）。

背景：初版 C 轴每层嵌套 div 带 `padding:2px`，深度到 100 层时累计内边距把可打宽度压成负数，
各家在这种退化情形下行为完全不同（PaperSharp 1 页 vs Chrome 40 页），
这条轴量的就不再是「层叠匹配」而是「谁怎么处理零宽」。
gen.py 里的 C 已经改成干净嵌套；这里把**已经测过的旧数据**整体挪到 X 轴留着——
那批页数的分歧本身是个有价值的健壮性发现，不该被覆盖掉。

跑法（必须在 sweep 不在写 CSV 的时候跑）：
    python migrate_c_to_x.py          # 执行
    python migrate_c_to_x.py --check  # 只看会改什么
"""
import csv
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
CORPUS = HERE / "corpus"
OUT = HERE / "out"


def patch_manifest(check):
    mp = CORPUS / "manifest.json"
    man = json.loads(mp.read_text(encoding="utf-8"))
    moved = 0
    for e in man["files"]:
        if e["axis"] == "C":
            newfile = f"X{e['size']}.html"
            if not (CORPUS / f"C{e['size']}.html").exists():
                continue                      # 已经迁过（或从没生成过）
            if (CORPUS / newfile).exists() and not check:
                print(f"  跳过：{newfile} 已存在，不动旧数据")
                continue
            if not check:
                (CORPUS / f"C{e['size']}.html").rename(CORPUS / newfile)
            e["axis"] = "X"
            e["name"] = "cascade-degenerate"
            e["file"] = newfile
            moved += 1
    if not check:
        mp.write_text(json.dumps(man, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"manifest: 迁移 {moved} 条 C→X")


def patch_csv(path, check):
    if not path.exists():
        print(f"  缺文件 {path.name}，跳过")
        return
    rows = list(csv.DictReader(path.open(newline="", encoding="utf-8")))
    cols = list(rows[0].keys()) if rows else []
    n = sum(1 for r in rows if r.get("axis") == "C")
    for r in rows:
        if r.get("axis") == "C":
            r["axis"] = "X"
    if not check and n:
        with path.open("w", newline="", encoding="utf-8") as fh:
            w = csv.DictWriter(fh, fieldnames=cols)
            w.writeheader()
            w.writerows(rows)
    print(f"{path.name}: 改写 {n} 行 C→X")


def main():
    check = "--check" in sys.argv
    print(("检查模式：" if check else "执行：") + "初版 C 语料 → X 轴")
    patch_manifest(check)
    patch_csv(OUT / "scale_raw.csv", check)
    patch_csv(OUT / "scale_cells.csv", check)
    if not check:
        print("下一步：python gen.py --axes C  （重出干净 C 语料），再 sweep --axes C 重测")


if __name__ == "__main__":
    main()
