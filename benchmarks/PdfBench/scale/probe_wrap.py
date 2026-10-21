#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""折行判别探针：20 段「必定需要多行」的中文段落，看引擎在 PDF 里留下多少。

区分两种完全不同的丢法：
  A. 每段只留第一行（后续折行没画）  —— 全文行数 ≈ 段数
  B. 整段丢失（有的段完全没出现）    —— 段头计数 < 20，但出现的段是完整的
判据都从这里读：段头「段N：」出现次数、文本行数、每段字符数、与 Chrome 同文档对照。

两个字体栈各跑一遍：纯中文栈 / D 轴那种「Latin 字体打头 + 中文兜底」的栈。
第一轮结果是纯中文栈下四家逐字节级一致（40 行 / 1931 字 / 留存 104%），
所以 D 轴的塌缩不可能是「引擎不会折中文」，只能出在字体栈解析那一侧。
"""
import re
import sys
from pathlib import Path

import pypdf

import sweep

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"
PARA = ("本合同项下的货款结算以实际过磅净重为准，磅单经双方确认后作为结算依据。"
        "如遇质量异议，应在收货之日起三个工作日内书面提出，逾期视为验收合格。"
        "增值税专用发票随货同行，税率按国家现行规定执行。")

STACKS = {
    "cjk-first": '"Microsoft YaHei",sans-serif',
    "latin-first(=D 轴)": '"Helvetica Neue", Arial, "Microsoft YaHei", sans-serif',
}


def css_for(stack):
    return ("@page{size:A4;margin:0}*{box-sizing:border-box}"
            f"body{{margin:0;font:14px/1.6 {stack}}}p{{margin:6px 0}}")


def build_probe(stack, n=20):
    tag = "cjk" if stack.startswith('"Microsoft') else "lat"
    p = OUT / f"_probe_wrap_{tag}.html"
    body = "".join(f"<p>段{i + 1}：{PARA}</p>" for i in range(n))
    p.write_text(f'<!doctype html><html><head><meta charset="utf-8">'
                 f"<style>{css_for(stack)}</style></head><body>{body}</body></html>",
                 encoding="utf-8")
    return p, n, len(PARA)


def run(eng, probe, out_pdf):
    tbl = sweep.engine_table()
    spec = tbl[eng]
    if spec["kind"] == "cli":
        ok, err = sweep.cli_run(eng, {"path": probe, "engine": eng}, out_pdf, 120.0)["ok"], ""
        return ok
    w = sweep.make_worker(spec)
    job = {"id": 1, "cmd": "gen", "html": str(Path(probe).resolve()), "out": str(out_pdf)}
    if spec.get("engine_arg"):
        job["engine"] = spec["engine_arg"]
    try:
        res = w.ask(job, 120.0)
        return bool(res.get("ok"))
    finally:
        w.kill()


def main():
    n = 20
    for label, stack in STACKS.items():
        probe, n, plen = build_probe(label, n)
        print(f"\n--- 字体栈 {label}: {stack}")
        print(f"    探针：{n} 段 × 每段 {plen} 字（A4 页宽 794px / 14px 字 ≈ 每行 50 字 → 每段应折 2 行）")
        tag = "cjk" if label.startswith("cjk") else "lat"
        for eng in ["papersharp", "puppeteer", "weasyprint", "wkhtmltopdf"]:
            out = OUT / f"_probe_wrap_{eng}_{tag}.pdf"
            if not run(eng, probe, out):
                print(f"    {eng:<12} 出图失败")
                continue
            rd = pypdf.PdfReader(str(out))
            txt = "\n".join((p.extract_text() or "") for p in rd.pages)
            lines = [l for l in txt.split("\n") if l.strip()]
            heads = len(re.findall(r"段\d+：", txt))
            stripped = txt.replace("\n", "")
            print(f"    {eng:<12} 页数={len(rd.pages):>3}  段头={heads}/{n}  行数={len(lines):>4}"
                  f"  字符={len(stripped):>6}  应有≈{n * 2} 行 / {n * plen} 字"
                  f"  留存率={100.0 * len(stripped) / (n * plen):.0f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
