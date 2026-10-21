#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WeasyPrint worker（规模扫描用），协议与 workers/node_worker.mjs 一致。

两种接入方式，按可用性自动选：
  1. import weasyprint —— 需要 Pango/GTK 的 DLL 在 PATH 上（官方 windows-onedir 包解压后即是）。
     这条路支持 warm：一次 import，常驻文档渲染循环。
  2. 不可 import 时由 driver 退回 CLI 口径（每档一个进程），cold-only。

统计 PDF 页数直接读 render 出来的 pages，避免再引一个 PDF 解析器。
"""
import json
import os
import sys
from pathlib import Path


def prep_native_libs():
    """Windows 上先把 Pango/GTK 所在目录注册进 DLL 搜索路径。

    Python 3.8 起 ctypes/cffi 不再顺着 PATH 或 exe 目录找依赖：
    实测把 78 个 DLL 全拷到 python.exe 旁边**仍然加载失败**，
    只有 os.add_dll_directory() 这条路走得通。别再把时间花在拷 DLL 上。
    """
    if os.name != "nt":
        return
    cand = os.environ.get("WEASY_DLLS")
    if cand and Path(cand).exists():
        os.add_dll_directory(cand)


prep_native_libs()

RESULT = {"ok": False}


def emit(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def main():
    try:
        import weasyprint  # noqa: F401
    except Exception as e:  # DLL 缺失等：报告不可用，让 driver 退回 CLI
        emit({"id": 0, "ok": False, "err": f"import failed: {type(e).__name__}: {str(e)[:200]}"})
        return 3

    from weasyprint import HTML
    ver = getattr(weasyprint, "__version__", "?")

    while True:
        line = sys.stdin.readline()
        if not line:
            break
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except Exception as e:
            emit({"id": -9, "ok": False, "err": f"bad json: {e}"})
            continue
        cmd = req.get("cmd")
        if cmd == "quit":
            emit({"id": req.get("id", -1), "ok": True, "bye": True})
            break
        if cmd == "stat":
            emit({"id": req.get("id", 0), "ok": True, "engines": {"weasyprint": ver},
                  "python": sys.version.split()[0]})
            continue
        if cmd != "gen":
            emit({"id": req.get("id", -1), "ok": False, "err": f"未知 cmd {cmd}"})
            continue

        import time
        t0 = time.perf_counter()
        try:
            # 保持 WeasyPrint 默认（含 96dpi 基准与 @page 解析）；语料的 @page 已声明 A4/0 边距，
            # 与其他选手的 preferCSSPageSize 语义对齐。
            doc = HTML(filename=req["html"]).render()
            doc.write_pdf(req["out"])
            pages = len(doc.pages)
            ms = (time.perf_counter() - t0) * 1000.0
            import os
            emit({"id": req["id"], "ok": True, "ms": round(ms, 3),
                  "bytes": os.path.getsize(req["out"]), "pages": pages, "err": None})
        except Exception as e:
            ms = (time.perf_counter() - t0) * 1000.0
            emit({"id": req["id"], "ok": False, "ms": round(ms, 3),
                  "err": f"{type(e).__name__}: {str(e)[:300]}"})
    return 0


if __name__ == "__main__":
    sys.exit(main())
