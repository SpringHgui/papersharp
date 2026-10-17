#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""D 轴页数塌缩的取证：到底是「少画了」还是「画到页外去了」。

现象（scale_cells.csv，D 轴页数）：
    10k 字符  PS 2  页 | Chrome 5  Weasy 5  wkhtml 5
    50k       PS 9     |          25      25      24
   200k       PS 27    |          97      99      95
   500k       PS 74    |         241     246     237
三家彼此吻合（±4%），只有 PaperSharp 稳定停在它们的 ~30%，且比例随规模走。

pypdf 抽出来的字符数已经把「只是折行方式不同」排除了：D50000 参照能抽到 47237 字
（语料 50000 的 94%），PaperSharp 只有 19584（39%）——**内容根本没进 PDF 内容流**，
不是画到页外被裁掉。剩下的问题是「在哪一步丢的」。

本脚本用 visitor_text 拿坐标与字号，回答两件事：
  1. 每页有多少个文本行（按 tm 的 y 分组）、最远伸到 x 几 pt —— 若 x 都 ≤ 页宽，
     说明折行本身是对的，不是横向溢出；
  2. 全文行数 ÷ 语料里的 `<p>` 段数 —— 若 ≈ 1，就是**每段只留下第一行、后续行没画**。
     （150 字/段 留 56 字/行 ≈ 37%，与抽到的 39% 对得上。）

pypdf 6.x 的 PageObject 没有 extract_words()，所以只能走 visitor；字号取访问者给的
font_size，CJK Advance 按 1em 估、Latin 按 0.55em 估，只为判「有没有越页宽」，不作度量。

用法：python diagnose_d.py            # 量 out/artifacts/ 里已存的 D50000 产物
      python diagnose_d.py --cell D200000
"""
import argparse
import re
import sys
from collections import defaultdict
from pathlib import Path

import pypdf

HERE = Path(__file__).resolve().parent
ART = HERE / "out" / "artifacts"
CORPUS = HERE / "corpus"


def page_text_map(path, max_pages=3):
    r = pypdf.PdfReader(path)
    n = len(r.pages)
    idxs = sorted({round(i * (n - 1) / (max_pages - 1)) for i in range(max_pages)}) if n > 1 else [0]
    report = []
    for i in idxs:
        page = r.pages[i]
        pw = float(page.mediabox.width)
        lines = {}                       # y 桶 -> {"x0":, "chars":, "fs":, "x1":}

        def visit(text, cm, tm, font_dict, font_size):
            if not text or not text.strip():
                return
            x = float(tm[4])
            fs = float(font_size or 8)
            y = round(float(tm[5]) / 2) * 2
            # 行尾估算：CJK 近似 1em，其他按 0.55em。只用来判「有没有越过页宽」，不当度量用。
            est = sum(fs * (1.0 if ord(ch) > 0x2E7F else 0.55) for ch in text)
            b = lines.get(y)
            if b is None:
                lines[y] = {"x0": x, "chars": len(text.strip()), "fs": fs, "x1": x + est}
            else:
                b["x0"] = min(b["x0"], x)
                b["chars"] += len(text.strip())
                b["fs"] = max(b["fs"], fs)
                b["x1"] = max(b["x1"], x + est)

        try:
            page.extract_text(visitor_text=visit)
        except Exception as e:
            report.append((i, pw, 0, 0, 0, f"visit 失败 {type(e).__name__}: {str(e)[:60]}"))
            continue
        ys = sorted(lines)
        nlines = len(ys)
        chars = sum(lines[y]["chars"] for y in ys)
        maxx = max((lines[y]["x1"] for y in ys), default=0.0)
        over = sum(1 for y in ys if lines[y]["x1"] > pw + 2)
        report.append((i, pw, nlines, chars, maxx,
                       f"{over}/{nlines} 行越页宽" if nlines else "无文本"))
    return n, report


def para_count(cell):
    p = CORPUS / f"{cell}.html"
    if not p.exists():
        return None
    html = p.read_text(encoding="utf-8")
    return len(re.findall(r"<p[ >]", html))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cell", default="D50000")
    args = ap.parse_args()
    np_ = para_count(args.cell)
    print(f"语料 {args.cell}：<p> 段数 = {np_}（若「每段只画第一行」成立，全文行数应≈此数）")
    engines = ["papersharp", "puppeteer", "weasyprint", "wkhtmltopdf"]
    for e in engines:
        p = ART / f"{e}_{args.cell}.pdf"
        if not p.exists():
            print(f"{e:<12} 缺产物 {p.name}（先跑 integrity.py）")
            continue
        n, rep = page_text_map(p)
        tot_lines = sum(r[2] for r in rep)
        per_page = tot_lines / len(rep) if rep else 0
        print(f"\n=== {e}：{n} 页，抽样 {len(rep)} 页共 {tot_lines} 行"
              f"（每页均 {per_page:.1f} 行；全篇粗估 {per_page * n:.0f} 行）===")
        for i, pw, nl, ch, mx, note in rep:
            print(f"  p{i+1}: 页宽 {pw:.1f}pt  行数 {nl:>4}  字数 {ch:>6}  "
                  f"最远 x1≈{mx:>8.1f}pt  {note}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
