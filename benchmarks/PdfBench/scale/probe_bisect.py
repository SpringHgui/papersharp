#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""塌缩阈值探针：固定语料样式（直接吃 gen.py 的 BASE_CSS，不另写一份），只改段落数。

已经排除掉的解释：
  * 「引擎不会折中文」—— 20 段纯中文探针下四家逐字符一致（留存 104%）；
  * 「font-family 以 Latin 打头导致 CJK 走兜底不折行」—— 两个字体栈对照，结果完全相同；
  * 「样式表太复杂」—— 用的就是 D 轴那份 BASE_CSS。
剩下的自变量只有「文档长度/是否跨页」。所以这里扫 n：
  n=20  → 约 1 页（已知正常）
  n=50/100/200 → 逐档看 PaperSharp 的字符留存率什么时候塌。
判据：从 PDF 里抽出的字符数 ÷ 语料里的正文字符数（PDF 会带少量结构化空白，故 104% 是满值）。
"""
import sys
from pathlib import Path

import pypdf

import gen
import sweep

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"
PARA = gen.CJK_POOL          # 与 D 轴同一池子
NS = [20, 50, 100, 200]


def build(n):
    body = "".join(f"<p>段{i + 1}：{PARA[:90 + (i % 4) * 20]}</p>" for i in range(n))
    html = (f'<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">'
            f"<style>{gen.BASE_CSS}</style></head><body class=\"wrap\">"
            f"<h1>阈值探针 n={n}</h1>{body}</body></html>")
    p = OUT / f"_probe_n{n}.html"
    p.write_text(html, encoding="utf-8", newline="\n")
    src_chars = sum(len(PARA[:90 + (i % 4) * 20]) for i in range(n))
    return p, src_chars


def render(eng, probe, out_pdf):
    spec = sweep.engine_table()[eng]
    if spec["kind"] == "cli":
        return bool(sweep.cli_run(eng, {"path": probe, "engine": eng}, out_pdf, 120.0)["ok"])
    w = sweep.make_worker(spec)
    job = {"id": 1, "cmd": "gen", "html": str(Path(probe).resolve()), "out": str(out_pdf)}
    if spec.get("engine_arg"):
        job["engine"] = spec["engine_arg"]
    try:
        return bool(w.ask(job, 120.0).get("ok"))
    finally:
        w.kill()


def measure(out_pdf, src_chars):
    r = pypdf.PdfReader(str(out_pdf))
    txt = "\n".join((p.extract_text() or "") for p in r.pages)
    return len(r.pages), len(txt.replace("\n", "").replace(" ", ""))


def main():
    print(f"{'n':>5} {'引擎':<12} {'页数':>4} {'抽出字符':>9} {'语料字符':>9} {'留存':>6}")
    for n in NS:
        probe, src = build(n)
        for eng in ["papersharp", "puppeteer"]:
            out = OUT / f"_probe_n{n}_{eng}.pdf"
            if not render(eng, probe, out):
                print(f"{n:>5} {eng:<12} 出图失败")
                continue
            pages, got = measure(out, src)
            print(f"{n:>5} {eng:<12} {pages:>4} {got:>9} {src:>9} {100.0 * got / src:>5.0f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
