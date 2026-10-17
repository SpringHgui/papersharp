#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""消融语料生成器：把 A30 按内容类型逐刀切掉，用 stageprobe 的段差值定位剩余成本。

为什么要它：CascadeProbe 的 props-94x-once 那行是探针自己直调 AngleSharp CSSOM 的模拟循环，
不能当引擎开销读（已经踩过：据此判定「属性读占 buildDoc 72%」，实做下来零收益）。
与其继续猜账面，不如做受控消融——每份变体只动一个变量，元素数与几何保持不变：

  full      原样（对照）
  notext    所有文字换成一个空格 —— 干掉文本测量、整形、字形子集、PDF 文字流
  noimg     图片 src 置空        —— 干掉图像解码与嵌入
  nofx      去掉 box-shadow/渐变  —— 干掉离屏层与滤镜光栅
  nostruct  只留 1 个 .page      —— 页数从 30 掉到 1，看「与页数成正比」的那部分

读法：stageprobe 各段（measure/paginate/recordpass/realencode）在两档之间的差，
就是被切掉那类内容在该段里的成本。元素数不变 ⇒ 层叠/建树那部分应当不动，
动了说明变体没做到单变量，先别信数。
"""
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
CORPUS = HERE / "corpus"
OUT = HERE / "corpus_abl"

IMG = re.compile(r'(src=")data:image/[^"]*(")')
TEXT = re.compile(r'>([^<>]{4,})<')
FX = re.compile(r'(box-shadow:[^;]*;|background:\s*linear-gradient\([^;]*\);?)')


def ablate(html, kind):
    if kind == "full":
        return html
    if kind == "notext":
        # 只替换元素之间的可见文本，保留标签与属性；长度压到 1 个空格
        return TEXT.sub(lambda m: ">\u00a0<", html)
    if kind == "noimg":
        return IMG.sub(lambda m: m.group(1) + "about:blank" + m.group(2), html)
    if kind == "nofx":
        return FX.sub("", html)
    if kind == "nostruct":
        pages = re.split(r'(?=<section class="page")', html)
        head = pages[0]
        return head + (pages[1] if len(pages) > 1 else "") + "</body></html>"
    raise KeyError(kind)


def main():
    src = (CORPUS / "A30.html").read_text(encoding="utf-8")
    OUT.mkdir(exist_ok=True)
    for kind in ("full", "notext", "noimg", "nofx", "nostruct"):
        h = ablate(src, kind)
        p = OUT / f"abl_{kind}.html"
        p.write_text(h, encoding="utf-8", newline="\n")
        tags = len(re.findall(r"<[a-zA-Z]", h))
        imgs = h.count("data:image")
        chars = sum(len(m.group(1)) for m in TEXT.finditer(h))
        print(f"{kind:<9} 标签 {tags:>6,}  图 {imgs:>4,}  可见字符 {chars:>8,}  → {p}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
