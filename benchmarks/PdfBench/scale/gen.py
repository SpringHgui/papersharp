#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""规模扫描语料生成器（HTML 赛道专用）。

设计约束（改之前先读）：
1. **确定性**：同一 (axis, size) 永远生成逐字节相同的 HTML，且所有引擎吃的是同一个文件。
   所以图片一律内联 data URI（不依赖各引擎对相对路径/base 的解析差异），文本用固定 PRNG。
2. **六条轴**，前五条各扫一个规模旋钮，最后一条是标定锚点：
   A pages   富样式整页 × N          —— 页数与元素数同步线性放大（正常报表）
   B table   单张长表 × 行 N          —— 列宽求解 + 跨页拆分
   C cascade 深层嵌套 + 300 条后代选择器 —— 层叠匹配开销（历史上自研引擎的 O(n²) 雷区）
   D text    CJK 长段落混排 × 字符 N   —— 断行/字距/整形（Skia/HarfBuzz 与 WebKit 的分歧点）
   E image   图片张数 × N              —— 图像嵌入与 PDF 体积
   Z anchor  仓库既有样例（invoice/challenge/report）—— 标定测量路径本身的水分
3. 语料必须**各家都能分页**：A 轴用显式 .page + break-after，B/D/E 轴用自然流，
   页数由引擎自己算（page count 本身就是被测指标，不做人为对齐）。

用法：
    python gen.py                 # 全量生成到 scale/corpus/，写 manifest.json
    python gen.py --axes A,B      # 只生成部分轴
"""

import argparse
import base64
import hashlib
import json
import os
import random
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]                      # scale → PdfBench → benchmarks → 仓库根
SPEC_ASSETS = HERE.parent / "spec" / "assets"
SAMPLE_ASSETS = ROOT / "src" / "PaperSharpSamples" / "assets"
CORPUS = HERE / "corpus"

# 每档的规模点。刻意用 1/3/10/30/100 这种跨量级点：log-log 斜率要够长的杠杆臂才拟合得稳。
AXES = {
    "A": {"name": "pages",   "points": [1, 3, 10, 30, 100]},
    "B": {"name": "table",   "points": [100, 500, 2000, 8000]},
    "C": {"name": "cascade", "points": [10, 30, 100, 300]},
    "D": {"name": "text",    "points": [10000, 50000, 200000, 500000]},
    "E": {"name": "image",   "points": [10, 30, 100, 300]},
    "Z": {"name": "anchor",  "points": [1, 2, 3]},   # 仓库既有样例，用来核对测量路径本身
    # X = 初版 C 语料（每层 div 带 2px padding）。深到 100 层时累计内边距把可用宽度压成负数，
    # 各家在这种退化宽度下的行为天差地别（见 X 档的页数列），量不到「层叠匹配」本身。
    # 保留它是因为它顺手暴露了一个真实的健壮性分歧，但它不是规模轴，别拿它的延迟画曲线。
    "X": {"name": "cascade-degenerate", "points": [10, 30, 100, 300]},
}

# Z 轴锚点：档位编号 → (样例名, 基目录, 期望参照值出处)。
# 参照值写死在此处只为「口径体检」用，报告里引用时以 PERF_REPORT / ../report/REPORT.md 为准。
Z_SAMPLES = {
    1: ("invoice", SAMPLE_ASSETS),
    2: ("challenge", HERE.parent / "spec"),
    3: ("report", SAMPLE_ASSETS),
}
Z_REF = {1: "PERF_REPORT §11.5 invoice 31.0ms", 2: "../report/REPORT.md papersharp 238ms",
         3: "PERF_REPORT §11.5 report 64.8ms"}

BASE_CSS = """
  @page { size: A4; margin: 0; }
  * { box-sizing: border-box; }
  html, body { margin: 0; padding: 0; }
  body { font: 14px/1.6 "Helvetica Neue", Arial, "Microsoft YaHei", sans-serif;
         color: #212529; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
  .page { width: 210mm; height: 296mm; padding: 14mm 12mm; background: #fff;
          overflow: hidden; }
  /* 分页用 break-before（除首页外）而不是 break-after：后者会在最后一节后
     多挤出一张空白页，页数就不再等于单元数了（A 轴的自变量会被这个 off-by-one 污染）。
     296mm 而不是 297mm：留 1mm 余量，避免舍入把单元撑成两页。 */
  .page + .page { break-before: page; page-break-before: always; }
  .row { display: flex; align-items: center; gap: 12px; }
  .grow { flex: 1 1 auto; }
  h1 { font-size: 26px; margin: 0 0 4px; }
  h2 { font-size: 17px; margin: 18px 0 8px; padding-bottom: 4px; border-bottom: 2px solid #0d6efd; }
  .muted { color: #6c757d; font-size: 12px; }
  .chip { display: inline-block; padding: 2px 10px; border-radius: 10px; background: #e7f1ff;
          color: #0a58ca; font-size: 12px; }
  table { width: 100%; border-collapse: collapse; font-size: 12.5px; }
  th, td { border: 1px solid #ced4da; padding: 5px 8px; text-align: left; }
  th { background: #f1f3f5; font-weight: 600; }
  td.num { text-align: right; font-variant-numeric: tabular-nums; }
  tbody tr:nth-child(even) td { background: #fafbfc; }
  tfoot td { background: #e9ecef; font-weight: 700; }
  .card { border: 1px solid #dee2e6; border-radius: 8px; padding: 10px 12px;
          background: linear-gradient(135deg, #f8f9ff 0%, #eef2ff 100%);
          box-shadow: 0 1px 3px rgba(13, 110, 253, .12); }
  .bar { height: 10px; border-radius: 5px; background: #dee2e6; overflow: hidden; }
  .bar > i { display: block; height: 100%; background: linear-gradient(90deg, #0d6efd, #6610f2); }
  footer { margin-top: 16px; padding-top: 8px; border-top: 1px dashed #adb5bd; font-size: 11px;
           color: #6c757d; display: flex; justify-content: space-between; }
"""

CJK_POOL = (
    "本合同项下的货款结算以实际过磅净重为准，磅单经双方确认后作为结算依据。"
    "如遇质量异议，应在收货之日起三个工作日内书面提出，逾期视为验收合格。"
    "增值税专用发票随货同行，税率按照国家现行规定执行。"
    "双方约定交货地点为需方指定仓库，运输费用由供方承担。"
    "计量器具须经法定检定合格并在有效期内，称重过程应有双方在场地。"
)
LATIN_POOL = (
    "The quick brown fox jumps over the lazy dog while the invoice total is being "
    "recomputed against the verified net weight recorded at the checkpoint. "
)


def data_uri(path: Path) -> str:
    raw = path.read_bytes()
    mime = "image/jpeg" if path.suffix.lower() in (".jpg", ".jpeg") else "image/png"
    return f"data:{mime};base64,{base64.b64encode(raw).decode()}"


def load_assets():
    """内联用的图片。缺图时退化成 1x1 PNG，保证语料仍能生成（只是 E 轴失真）。"""
    out = {}
    for name in ("photo.jpg", "logo-alpha.png", "dot.png"):
        p = SPEC_ASSETS / name
        out[name] = data_uri(p) if p.exists() else (
            "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4"
            "2mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")
    return out


def money(rng):
    return f"{rng.randint(1, 90) * 100 + rng.randint(0, 99):,}.{rng.randint(10, 99)}"


def one_rich_page(rng, idx, assets, name):
    """A 轴的单元页：flex 头 + 表格 + 卡片栅格 + 进度条 + 图文 + 页脚，约 200 个元素。"""
    rows = "".join(
        f"<tr><td>{name}-{idx}-{r:02d}</td><td>{rng.choice(['热轧带钢','冷轧板卷','镀锌卷','中厚板'])}</td>"
        f"<td class='num'>{rng.randint(1, 40):,}.{rng.randint(0,9)}</td>"
        f"<td class='num'>{money(rng)}</td>"
        f"<td class='num'>{money(rng)}</td></tr>"
        for r in range(12))
    cards = "".join(
        f"<div class='card grow'><div class='muted'>指标 {c+1}</div>"
        f"<div style='font-size:20px;font-weight:700'>{money(rng)}</div>"
        f"<div class='bar'><i style='width:{rng.randint(20, 95)}%'></i></div></div>"
        for c in range(4))
    paras = "".join(
        f"<p>{CJK_POOL[:rng.randint(80, 160)]} {LATIN_POOL[:rng.randint(60, 140)]}</p>"
        for _ in range(3))
    return f"""
<section class="page">
  <header class="row"><h1 class="grow">结算单 {name}-{idx:03d}</h1>
    <span class="chip">已核重</span><img src="{assets['logo-alpha.png']}" width="72" alt=""></header>
  <div class="muted">单据编号 BH-2026-{idx:05d} · 制表 2026-09-26 · 金额单位 元</div>
  <h2>明细</h2>
  <table><thead><tr><th>批号</th><th>品名</th><th>数量（吨）</th><th>单价</th><th>合计</th></tr></thead>
    <tbody>{rows}</tbody>
    <tfoot><tr><td colspan="2">合计</td><td class="num">{rng.randint(100, 900)}</td><td></td>
      <td class="num">¥{money(rng)}</td></tr></tfoot></table>
  <h2>关键指标</h2>
  <div class="row">{cards}</div>
  <h2>说明</h2>
  {paras}
  <div class="row"><img src="{assets['photo.jpg']}" width="180" alt="">
    <div class="grow"><p>右侧文字与图片混排，用于触发基线对齐与行盒换行：{CJK_POOL[:110]}</p></div></div>
  <footer><span>第 {idx} 页 / 共 N 页</span><span>制表人：自动化基准</span><span>BH-2026</span></footer>
</section>"""


def doc_A(k, assets):
    rng = random.Random(1000 + k)
    body = "".join(one_rich_page(rng, i + 1, assets, "P") for i in range(k))
    return body, {"pages_declared": k, "repeat_unit": "富样式 A4 整页"}


def doc_B(k, assets):
    """B 轴：单张长表，自然流分页。行数=k，每行 6 列。"""
    rng = random.Random(2000 + k)
    head = ("<tr><th>行号</th><th>批号</th><th>品名</th><th class='num'>数量</th>"
            "<th class='num'>单价</th><th class='num'>金额</th></tr>")
    rows = "".join(
        f"<tr><td>{r+1}</td><td>LOT-{r % 997:03d}</td>"
        f"<td>{rng.choice(['Q235B 热轧卷','SPRC 冷轧板','DX51D 镀锌','Q355NE 高建钢'])}</td>"
        f"<td class='num'>{rng.randint(1, 80):,}.{rng.randint(0,9)}</td>"
        f"<td class='num'>{money(rng)}</td><td class='num'>{money(rng)}</td></tr>"
        for r in range(k))
    body = (f"<h1>长表基准 B（{k} 行）</h1>"
            f"<table><thead>{head}</thead><tbody>{rows}</tbody></table>")
    return body, {"rows": k}


def _cascade(k, assets, layer_style):
    """C/X 两轴共用：深度 k 的嵌套 + 每块 8 个叶子段落 + 300 条规则。

    reps = 4000/depth 是刻意的：让**元素总数近似恒定**、只把「祖先链长度」当变量，
    这样测出来的是选择器匹配沿祖先链回溯的代价（层叠实现差异最大的地方），
    而不是又被元素数带偏。真实元素数以 manifest 里 elements 字段为准，
    拟合用它而不是名义档位。

    layer_style 是两轴唯一的区别："" = 干净嵌套（C 轴，量层叠），
    " style='padding:2px'" = 每层吃 2px 内边距（X 轴，深到 100 层时可打宽度被压成负数）。
    """
    depth = k
    inner = "".join(f"<p class='leaf'>深度 {depth} 叶子 {i}：{CJK_POOL[:40]}</p>" for i in range(8))
    tags = "".join(f"<div class='n{i % 25}'{layer_style}>" for i in range(depth))
    close = "</div>" * depth
    blocks = tags + inner + close
    reps = max(1, 4000 // max(depth, 1))
    body = (f"<h1>层叠基准（depth={k}, reps={reps}{', padding 退化' if layer_style else ''}）</h1>"
            + blocks * reps)
    css_extra = "\n".join(
        f".wrap .n{i%25} > div .leaf{i%7}{{ font-size:{12 + (i % 5)}px; }}" if i % 3 == 0 else
        f"div.n{i%25} p.leaf{{ color:#{(i*997%256):02x}{(i*613%256):02x}{(i*331%256):02x}; }}"
        for i in range(300))
    return body, {"depth": k, "reps": reps}, css_extra


def doc_C(k, assets):
    return _cascade(k, assets, "")


def doc_X(k, assets):
    return _cascade(k, assets, " style='padding:2px'")


def doc_D(k, assets):
    """D 轴：CJK + Latin + 数字混排的长文本，字符数≈k。测断行、字距、整形。"""
    rng = random.Random(4000 + k)
    target = k
    got = 0
    parts = []
    n = 0
    while got < target:
        n += 1
        if n % 3 == 0:
            s = LATIN_POOL[:rng.randint(120, 220)]
        elif n % 3 == 1:
            s = CJK_POOL[:rng.randint(90, 190)]
        else:
            s = f"合计 {rng.randint(1000, 99999):,}.{rng.randint(10, 99)} 元 / {rng.randint(1, 999)} 吨 " + CJK_POOL[:60]
        parts.append(f"<p>{s}</p>")
        got += len(s)
    body = f"<h1>文本基准 D（约 {k} 字符）</h1>" + "".join(parts)
    return body, {"chars": got}


def doc_E(k, assets):
    """E 轴：k 张照片 + 圆角/缩放，测图像解码、下采样与 PDF 嵌入。"""
    cells = "".join(
        f"<img class='ph' src=\"{assets['photo.jpg']}\" width='160' height='106' alt=''>"
        for _ in range(k))
    body = (f"<h1>图片基准 E（{k} 张）</h1>"
            "<style>.ph{border-radius:6px;margin:3px;border:1px solid #ccc}</style>" + cells)
    return body, {"images": k}


def doc_Z(k, assets):
    """Z 轴锚点：直接把仓库里已有的样例搬进来（invoice / challenge / report）。

    存在的意义不是又一个规模点，而是**给测量路径本身做标定**：
    这几份样例在 PERF_REPORT / 七家横向报告里已有 PaperSharp 的端到端中位数，
    本 harness 测出来若与那个数差出一截，差值就是「驱动/IPC/worker 常驻」掺进来的水分，
    横向曲线要先扣掉它再读。

    样例原来靠 `RenderPdf(html, css)` 的第二参传 CSS；worker 只吃一个文件，
    所以这里把 `{name}.css` 原样内联成 `<style>`、把相对路径的图片内联成 data URI。
    除「样式来源从参数变成文档内 <style>」外不改动内容，这一条差异在报告里如实标注。
    """
    name, base = Z_SAMPLES[k]
    frag = (base / f"{name}.html").read_text(encoding="utf-8")
    cssp = base / f"{name}.css"
    css = cssp.read_text(encoding="utf-8") if cssp.exists() else ""

    def inline_refs(text):
        def sub_attr(m):
            rel = m.group(2)
            if rel.startswith(("http:", "https:", "data:", "//", "#")):
                return m.group(0)
            p = (base / rel).resolve()
            if not p.exists():
                return m.group(0)
            return f'{m.group(1)}"{data_uri(p)}"{m.group(3)}'

        def sub_url(m):
            rel = m.group(1).strip("\"'")
            p = (base / rel).resolve()
            return f"url({data_uri(p)})" if p.exists() else m.group(0)

        text = re.sub(r'(src=")([^"]+)(")', sub_attr, text)
        text = re.sub(r'(href=")([^"]+\.(?:png|jpe?g|gif|svg))(")', sub_attr, text)
        return re.sub(r"url\(([^)]+)\)", sub_url, text)

    css = inline_refs(css)
    frag = inline_refs(frag)
    low = frag.lstrip().lower()
    if "<head" in low:
        full = re.sub(r"(<head[^>]*>)", lambda m: m.group(1) + f"<style>{css}</style>",
                      frag, count=1, flags=re.I)
    elif low.startswith(("<!doctype", "<html")):
        full = re.sub(r"(<html[^>]*>)", lambda m: m.group(1) + f"<head><meta charset='utf-8'>"
                      f"<style>{css}</style></head>", frag, count=1, flags=re.I)
    else:
        full = ('<!doctype html><html><head><meta charset="utf-8">'
                f"<style>{css}</style></head><body>{frag}</body></html>")
    return full, {"sample": name, "ref": Z_REF[k], "verbatim": True}, ""


DOC_BUILDERS = {"A": doc_A, "B": doc_B, "C": doc_C, "D": doc_D, "E": doc_E, "Z": doc_Z,
                "X": doc_X}


def build(axis, size, assets):
    """统一成 (body, meta, css_extra)：只声明两元组的轴补空 CSS。"""
    out = DOC_BUILDERS[axis](size, assets)
    if len(out) == 2:
        body, meta = out
        css_extra = ""
    else:
        body, meta, css_extra = out
    return body, meta, css_extra


START_TAG = re.compile(r"<[a-zA-Z][^>]*>")


def render(axis, size, assets):
    body, meta, css_extra = build(axis, size, assets)
    if meta.get("verbatim"):
        # 锚点样例：仓库里是什么样就什么样，不套 BASE_CSS、不加外壳（否则参照值不可比）
        html = body
    else:
        html = f"""<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>scale {axis}-{size}</title>
<style>{BASE_CSS}{css_extra}</style></head>
<body class="wrap">{body}</body></html>
"""
    # 元素数按起始标签数计（含 body/html/style）：它是 log-log 拟合的自变量，必须与语料同源计量
    meta = dict(meta)
    meta["elements"] = len(START_TAG.findall(html))
    meta["css_rules"] = css_extra.count("{") + (0 if meta.get("verbatim") else BASE_CSS.count("{"))
    return html, meta


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--axes", default="A,B,C,D,E")
    ap.add_argument("--clean", action="store_true")
    args = ap.parse_args()

    if args.clean and CORPUS.exists():
        for f in CORPUS.glob("*.html"):
            f.unlink()

    assets = load_assets()
    want = [a.strip().upper() for a in args.axes.split(",") if a.strip()]
    # manifest 是按轴合并的：只生成部分轴时，别把别的轴从清单里抹掉
    # （sweep.py 与 plot.py 都以 manifest 为准，抹掉=那些档凭空消失）
    manifest = {"generated_by": "gen.py", "assets_inline": True, "files": []}
    prev = CORPUS / "manifest.json"
    if prev.exists():
        try:
            old = json.loads(prev.read_text(encoding="utf-8"))
            manifest["files"] = [e for e in old.get("files", []) if e.get("axis") not in want]
        except Exception:
            pass
    CORPUS.mkdir(exist_ok=True)
    for axis in want:
        for size in AXES[axis]["points"]:
            html, meta = render(axis, size, assets)
            p = CORPUS / f"{axis}{size}.html"
            p.write_text(html, encoding="utf-8", newline="\n")
            raw = p.read_bytes()
            manifest["files"].append({
                "axis": axis, "name": AXES[axis]["name"], "size": size,
                "file": p.name, "bytes": len(raw),
                "sha256": hashlib.sha256(raw).hexdigest()[:16],
                "meta": meta,
            })
            print(f"  {p.name:<16} {len(raw)/1024:8.1f} KB  elem={meta['elements']:>8,}  "
                  f"sha {manifest['files'][-1]['sha256']}")
    (CORPUS / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"corpus: {len(manifest['files'])} files -> {CORPUS}")


if __name__ == "__main__":
    main()
