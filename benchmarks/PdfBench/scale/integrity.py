#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""完整性通道：给每个 (引擎, 档) 各出一份 PDF 存档，并量化「它到底画了多少」。

为什么单开一条通道，而不是塞进 sweep.py：
sweep 那一趟量的是**时间**，任何额外动作（落盘、解析、抽文本）都会混进被测区间；
这一趟只出一次图、只做内容核对，不参与任何延迟数字。

它要回答的是一个很具体的质疑：**「某家比 Chrome 快 8 倍」到底是做得快，还是没做**。
判据用 PDF 里抽出来的字符数（与 chrome-cli 同一档的产物对比），配合页数与末页字符：

  chars_ratio  < 0.9   该档内容明显少画（引擎不认这段 CSS/结构，直接跳过）
  pages_ratio  != 1    分页不一致（要么没分页全挤一页，要么溢出成更多页）
  last_chars_ratio < 0.5  尾部截断（老 WebKit 的经典毛病）

产物：out/artifacts/<engine>_<档>.pdf + out/integrity.csv
"""
import argparse
import csv
import statistics
import sys
import time
from pathlib import Path

import pypdf

import sweep

HERE = Path(__file__).resolve().parent
OUT = sweep.OUT
ART = OUT / "artifacts"
REF_ENGINE = "chrome-cli"          # 参照：无头 Chrome 直出（本项目的目标口径就是 printToPDF）


def gen_one(engine, spec, cell, out_pdf):
    """出一次图。CLI 走进程，worker 走常驻（这份产物与时间无关，怎么快怎么来）。"""
    if spec["kind"] == "cli":
        r = sweep.cli_run(engine, cell, out_pdf, 300.0)
        return r["ok"], r.get("err")
    w = sweep.make_worker(spec)
    try:
        job = {"id": 1, "cmd": "gen", "html": str(cell["path"]), "out": str(out_pdf)}
        if spec.get("engine_arg"):
            job["engine"] = spec["engine_arg"]
        res = w.ask(job, 300.0)
        return bool(res.get("ok")), res.get("err")
    except Exception as e:
        return False, str(e)[:200]
    finally:
        w.kill()


def pdf_image_profile(path):
    """(页面上画出的图像 XObject 次数, 文档内去重后的图像数)。

    E 轴整轴没有文本，字符判据对它瞎；只能数 PDF 里真正被引用的图像对象。
    前者抓「有没有画」，后者抓「同一张图嵌了几份」——300 张重复照片下
    这两家能差出三个数量级（去重的只嵌一份，不去的按页复制）。
    """
    drawn = 0
    names = set()
    try:
        r = pypdf.PdfReader(path)
        for p in r.pages:
            xo = (p.get("/Resources") or {}).get("/XObject")
            if xo is None:
                continue
            try:
                items = xo.get_object().items()
            except Exception:
                continue
            for key, val in items:
                try:
                    o = val.get_object()
                except Exception:
                    continue
                if o.get("/Subtype") == "/Image":
                    drawn += 1
                    names.add((key, o.get("/Width"), o.get("/Height"),
                               len(o.get_data()) if hasattr(o, "get_data") else 0))
    except Exception:
        return 0, 0
    return drawn, len(names)


def pdf_text_profile(path, max_sample=8):
    """(页数, 外推全文字符数, 末页字符数, 抽样平均每页字符数, 实际抽样页数)。

    A100/B8000/D500000 这些档整篇抽字要几分钟，而这里只需要「有没有少画」的量级判断，
    所以按均匀抽样 8 页、用「抽样均值 × 页数」外推。参照侧同样外推，比值才可比；
    末页单独精确抽（截断只会发生在末页，这个不能用抽样糊过去）。解析失败返回全 0。
    """
    try:
        r = pypdf.PdfReader(path)
        n = len(r.pages)
        if n == 0:
            return 0, 0, 0, 0, 0

        def chars_of(idx):
            try:
                return len((r.pages[idx].extract_text() or "").strip())
            except Exception:
                return 0

        idxs = sorted({round(i * (n - 1) / (max_sample - 1)) for i in range(max_sample)}) if n > 1 \
            else [0]
        per = [chars_of(i) for i in idxs]
        avg = sum(per) / len(per)
        return n, int(avg * n), per[-1], avg, len(idxs)
    except Exception:
        return 0, 0, 0, 0, 0


def stale(artifact, cell_path, force):
    """产物是否该重出。

    踩过的坑：`--force` 原先只作用在选手产物上，参照（chrome-cli）却按「文件已存在就复用」，
    于是语料改了、选手重测了，参照还是上一代语料画的页 —— C 轴一度被这套陈旧参照
    判成「puppeteer 页数 0.83×」，实际是新语料页数本来就少了 3 倍。
    现在一律比对语料文件 mtime：语料比产物新，产物就是旧的，无条件重出。
    """
    if force:
        return True
    if not artifact.exists():
        return True
    try:
        return Path(cell_path).stat().st_mtime > artifact.stat().st_mtime
    except OSError:
        return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engines", default="")
    ap.add_argument("--axes", default="A,B,C,D,E,Z")
    ap.add_argument("--cells", default="", help="逗号分隔档位，如 A3,B2000；空=该轴全档")
    ap.add_argument("--force", action="store_true", help="已存在的产物也重出")
    args = ap.parse_args()

    ART.mkdir(parents=True, exist_ok=True)
    tbl = sweep.engine_table()
    engines = [e.strip() for e in args.engines.split(",") if e.strip()] or list(tbl)
    axes = [a.strip().upper() for a in args.axes.split(",") if a.strip()]
    only = {s.strip() for s in args.cells.split(",") if s.strip()}
    cells = [c for c in sweep.load_cells(axes) if not only or f"{c['axis']}{c['size']}" in only]

    out_csv = OUT / "integrity.csv"
    fresh = not out_csv.exists()
    fh = out_csv.open("a", newline="", encoding="utf-8")
    cols = ["engine", "axis", "size", "file", "ok", "err", "pages", "chars", "last_chars",
            "chars_per_page", "pages_sampled", "imgs_drawn", "imgs_unique", "kb",
            "ref_pages", "ref_chars", "ref_last_chars", "ref_imgs",
            "pages_ratio", "chars_ratio", "last_chars_ratio", "imgs_ratio", "verdict"]
    w = csv.DictWriter(fh, fieldnames=cols)
    if fresh:
        w.writeheader()

    # 参照必须先有：chrome-cli 缺席时先给它补一份，否则所有 ratio 都是空
    refs = {}
    for c in cells:
        p = ART / f"{REF_ENGINE}_{c['axis']}{c['size']}.pdf"
        if REF_ENGINE in tbl and stale(p, c["path"], args.force):
            ok, err = gen_one(REF_ENGINE, tbl[REF_ENGINE], c, p)
            if not ok:
                print(f"参照缺失 {c['axis']}{c['size']}: {err}", file=sys.stderr)
        if p.exists():
            tp = pdf_text_profile(p)
            ip = pdf_image_profile(p)
            refs[(c["axis"], c["size"])] = (tp, ip)
        else:
            refs[(c["axis"], c["size"])] = ((0, 0, 0, 0), (0, 0))

    for name in engines:
        spec = tbl[name]
        if spec.get("axes") and not any(c["axis"] in spec["axes"] for c in cells):
            continue
        for c in cells:
            tag = f"{c['axis']}{c['size']}"
            p = ART / f"{name}_{tag}.pdf"
            if not stale(p, c["path"], args.force):
                tp = pdf_text_profile(p)
                ip = pdf_image_profile(p)
                ok, err = True, ""
            else:
                t0 = time.time()
                ok, err = gen_one(name, spec, c, p)
                tp = pdf_text_profile(p) if ok else (0, 0, 0, 0, 0)
                ip = pdf_image_profile(p) if ok else (0, 0)
                print(f"[{name}/{tag}] ok={ok} {time.time()-t0:.1f}s "
                      f"pages={tp[0]} chars={tp[1]} last={tp[2]} imgs={ip[0]}/{ip[1]} {err or ''}",
                      flush=True)
            pages, chars, last, cpp, _ns = tp
            drawn, uniq = ip
            (rp, rc, rl, *_x), (rd, _ru) = refs.get((c["axis"], c["size"]),
                                                   ((0, 0, 0, 0, 0), (0, 0)))
            pr = pages / rp if rp else None
            cr = chars / rc if rc else None
            lr = last / rl if rl else None
            ir = drawn / rd if rd else None
            verdict = "ok"
            if not ok:
                verdict = "no-pdf"
            elif rd and ir is not None and ir < 0.5:
                verdict = "少画图像"
            elif rc and cr is not None and cr < 0.9:
                verdict = "少画"
            elif rp and pr is not None and abs(pr - 1) > 0.001:
                verdict = f"页数不一致({'+' if pr > 1 else '-'}{pr:.2f}×)"
            elif rl and lr is not None and lr < 0.5:
                verdict = "尾部截断"
            w.writerow({
                "engine": name, "axis": c["axis"], "size": c["size"], "file": p.name,
                "ok": int(ok), "err": (err or "").replace("\n", " ")[:150],
                "pages": pages, "chars": chars, "last_chars": last, "chars_per_page": round(cpp), "pages_sampled": _ns,
                "imgs_drawn": drawn, "imgs_unique": uniq,
                "kb": round(p.stat().st_size / 1024.0, 1) if p.exists() else 0,
                "ref_pages": rp, "ref_chars": rc, "ref_last_chars": rl, "ref_imgs": rd,
                "pages_ratio": round(pr, 3) if pr is not None else "",
                "chars_ratio": round(cr, 3) if cr is not None else "",
                "last_chars_ratio": round(lr, 3) if lr is not None else "",
                "imgs_ratio": round(ir, 3) if ir is not None else "",
                "verdict": verdict,
            })
            fh.flush()
    fh.close()
    print(f"-> {out_csv}, 产物在 {ART}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
