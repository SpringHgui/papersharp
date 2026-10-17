#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""形状探针：把「跨页续排」这件事拆成三种互不相干的形状，看塌缩跟着谁走。

probe_bisect 的发现是：纯文本流文档里 PaperSharp 的页数停在 3，字符留存率
103%→93%→46%→23% 随段数下滑，而 Chrome 一路 103%。但「段落数」这个自变量太粗，
分不清到底是 (a) 页数上限、(b) 单段内部跨页续排放不下、还是 (c) 文本量本身触发了什么。
三种形状各自只钉住一个变量：

  big-p     一个 30000 字的 `<p>`（无重复文本） —— 单段跨页续排
  many-div  80 个 height:380px 的块（每块几十字） —— 纯块级跨页，几乎不含长文本
  long-p    80 个各自 500 字的 `<p>` —— 常规多段跨页（和 D 轴最像）

判据用「页数 ÷ Chrome 同文档页数」与「抽出字符 ÷ 语料字符」两把尺：
块级形状没有文本可抽，所以只看页数；文本形状两把尺一起看。
"""
import sys
from pathlib import Path

import pypdf

import gen
import sweep

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"


def doc_big_p():
    # 用固定但互不重复的文本填一个超长段落：避免任何「相同字符串」带来的巧合
    body = "".join(f"第{i}句：货款结算以实际过磅净重为准，磅单经双方确认后作为结算依据。"
                   for i in range(300))
    return f"<h1>big-p</h1><p>{body}</p>", len(body)


def doc_many_div():
    body = "".join(f"<div style='height:380px;border:1px solid #ccc'>块 {i}</div>"
                   for i in range(80))
    return f"<h1>many-div</h1>{body}", 80 * 4


def doc_long_p():
    body = "".join(f"<p>第{i}段：{'增值税专用发票随货同行，税率按国家现行规定执行。' * 4}</p>"
                   for i in range(80))
    return f"<h1>long-p</h1>{body}", len(body)


SHAPES = {"big-p": doc_big_p, "many-div": doc_many_div, "long-p": doc_long_p}


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


def main():
    print(f"{'形状':<10} {'引擎':<12} {'页数':>4} {'抽出字符':>9} {'页比(÷Chrome)':>14}")
    for name, build in SHAPES.items():
        inner, src = build()
        html = (f'<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">'
                f"<style>{gen.BASE_CSS}</style></head><body class=\"wrap\">{inner}</body></html>")
        probe = OUT / f"_shape_{name}.html"
        probe.write_text(html, encoding="utf-8", newline="\n")
        ref_pages = None
        for eng in ["puppeteer", "papersharp"]:
            out = OUT / f"_shape_{name}_{eng}.pdf"
            if not render(eng, probe, out):
                print(f"{name:<10} {eng:<12} 出图失败")
                continue
            r = pypdf.PdfReader(str(out))
            txt = "\n".join((p.extract_text() or "") for p in r.pages)
            chars = len(txt.replace("\n", "").replace(" ", ""))
            ratio = ""
            if eng == "puppeteer":
                ref_pages = len(r.pages)
                ratio = "1.00（基准）"
            elif ref_pages:
                ratio = f"{len(r.pages) / ref_pages:.2f}"
            print(f"{name:<10} {eng:<12} {len(r.pages):>4} {chars:>9} {ratio:>14}"
                  f"{'   语料字符≈' + str(src) if eng == 'puppeteer' else ''}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
