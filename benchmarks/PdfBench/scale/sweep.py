#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""HTML→PDF 同类工具规模扫描驱动。

与七家横向报告（../report/REPORT.md）的分工：那份比的是「.NET 里谁能出 PDF」，
本脚本只比**吃 HTML 的引擎**，而且比的是「文档变大之后谁先垮」——五轴 × warm/cold 两个口径。

口径（写死在这里，别在别处再解释一遍）：
  * warm —— 一个常驻 worker 进程跑完该 cell 的全部迭代：JIT/浏览器/字体缓存只付一次。
            这是「服务端常驻出票/出报表」的真实形态。
  * cold —— 每次生成都重启 worker：含解释器/JIT/浏览器冷启。这是「CLI 一把梭」的形态。
  * CLI-only 引擎（chrome、wkhtmltopdf）没有常驻形态，只出 cold；warm 记 n/a。
  * 每 cell 一个新 worker 进程，不跨引擎复用 —— 与 ../PdfBench.Runner 的 measure-one 同一条理由：
    前一家的原生内存会把后一家的 rss_peak 抬高。
  * RSS 是「worker pid + 全部后代」工作集之和的峰值（Chrome 的多进程树会被算进来）。
  * 顺带记 MediaBox：某家输出的页面尺寸不是 A4，说明它没吃同一份 @page，横向比要先打折。

用法：
    python sweep.py                        # 全部引擎 × 全部档
    python sweep.py --engines papersharp,puppeteer --axes A
    python sweep.py --regimes cold --rounds 2
产物：out/scale_raw.csv（逐次）、out/scale_cells.csv（逐 cell 聚合）、out/sweep.log
"""
import argparse
import csv
import json
import os
import statistics
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path

import psutil
from pypdf import PdfReader

# warmvary 用的 token 池：够一轮迭代不重就行（重了也只是那一次能命中缓存）
uuid_tokens = [uuid.uuid4().hex for _ in range(4096)]

HERE = Path(__file__).resolve().parent
BENCH = HERE.parent
CORPUS = HERE / "corpus"
OUT = HERE / "out"
RUNNER_EXE = BENCH / "PdfBench.Runner" / "bin" / "Release" / "net10.0" / "PdfBench.Runner.exe"
NODE = "node"
VENV_PY = BENCH.parent.parent / ".scratch" / "pdfbench-venv" / "Scripts" / "python.exe"
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
WK = str(Path.home() / r"wkhtmltox\wkhtmltox\bin\wkhtmltopdf.exe")
WEASY_DIR = BENCH.parent.parent / ".scratch" / "onedir" / "weasyprint"
WEASY_BUNDLE = WEASY_DIR / "weasyprint.exe"          # 官方 onedir 包：自带 Pango/GTK 的 CLI 形态
WEASY_DLLS = WEASY_DIR / "_internal"                  # 同一批 DLL 借给 venv 走 import 形态（才有 warm）

JOB_TIMEOUT_FLOOR = 120.0      # 单 job 超时下限（秒）
WARM_TARGET_S = 5.0            # 每个 warm cell 想花的时间预算，用来反推迭代次数
COLD_TARGET_S = 15.0
MAX_ITERS = 25
MIN_ITERS = 3
MAX_CELL_S = 45.0              # 单 cell 硬上限：大档一次生成就要几十秒时，宁可少采样也不要跑不完


def pick_iters(est_s, target_s):
    """按时间预算定采样数：慢档少采、快档多采，但任何 cell 都不许超过 MAX_CELL_S。"""
    est = max(est_s, 0.02)
    n = int(target_s / est)
    n = max(MIN_ITERS, min(n, MAX_ITERS))
    n = max(MIN_ITERS, min(n, max(1, int(MAX_CELL_S / est))))
    return n


# ----------------------------------------------------------------------------- 引擎表
# kind: worker  = stdio 行协议常驻（支持 warm/cold）
#       cli     = 一把梭进程（只有 cold）
def engine_table():
    tbl = {
        "papersharp": {
            "kind": "worker", "cmd": [str(RUNNER_EXE), "psworker"], "engine_arg": None,
            "channel": "自研 CSS 引擎 + Skia",
        },
        "puppeteer": {
            "kind": "worker", "cmd": [NODE, str(HERE / "workers" / "node_worker.mjs")],
            "engine_arg": "puppeteer", "channel": "Chromium printToPDF (.NET/Node 包装)",
        },
        "playwright": {
            "kind": "worker", "cmd": [NODE, str(HERE / "workers" / "node_worker.mjs")],
            "engine_arg": "playwright", "channel": "Chromium printToPDF (Playwright 包装)",
            # Playwright 与 puppeteer 同一个内核，只跑 A/B 两轴做「包装层开销」对照，
            # C/D/E 复述 Chromium 曲线没有信息量。
            "axes": {"A", "B"},
        },
        "weasyprint": {
            "kind": "worker", "cmd": [str(VENV_PY), str(HERE / "workers" / "py_worker.py")],
            "engine_arg": None, "channel": "独立 CSS 排版引擎（Python）",
            # pip 装的 weasyprint 在 Windows 上没有自带 Pango/GTK，import 即炸；
            # 把官方 onedir 的 DLL 目录挂到 PATH 上才拿到 import 形态（CLI 形态只有 cold）。
            "path_prepend": [str(WEASY_DLLS)] if WEASY_DLLS.exists() else None,
        },
        "chrome-cli": {
            "kind": "cli", "channel": "无头 Chrome 直出（无 CDP 包装层）", "axes": {"A", "B"},
        },
        "wkhtmltopdf": {
            "kind": "cli", "channel": "Qt WebKit 老引擎",
        },
    }
    if WEASY_BUNDLE.exists():
        # onedir 包只给 CLI 形态：留着当 worker 起不来时的退路
        tbl["weasyprint-cli"] = {"kind": "cli", "channel": "WeasyPrint CLI（onedir 包）"}
    return tbl


def cli_cmd(name, html, out_pdf):
    """CLI-only 引擎的命令行。参数与 ../PdfBench.Runner/Adapters 里那两家逐项一致。"""
    url = Path(html).as_uri()
    if name == "chrome-cli":
        # 固定一个 profile 目录：每档新建 profile 会把「首次运行写注册表/建缓存」也算进出纸耗时
        profile = OUT / "_chrome_profile"
        profile.mkdir(exist_ok=True)
        return [CHROME, "--headless=new", "--disable-gpu", "--no-sandbox", "--no-first-run",
                "--disable-dev-shm-usage", f"--user-data-dir={profile}",
                "--no-pdf-header-footer", f"--print-to-pdf={out_pdf}", url]
    if name == "wkhtmltopdf":
        return [WK, "--page-size", "A4",
                "--margin-top", "0", "--margin-bottom", "0",
                "--margin-left", "0", "--margin-right", "0",
                "--disable-smart-shrinking", "--encoding", "utf-8",
                "--enable-local-file-access", url, out_pdf]
    if name == "weasyprint-cli":
        return [str(WEASY_BUNDLE), str(html), out_pdf]
    raise KeyError(name)


# ----------------------------------------------------------------------------- RSS 采样
class RssSampler(threading.Thread):
    """每 20ms 取一次「pid + 全部后代」工作集之和，记峰值（MB）。"""

    def __init__(self, pid, interval=0.02):
        super().__init__(daemon=True)
        self.pid, self.interval = pid, interval
        self.peak = 0.0
        self._stop = threading.Event()

    def run(self):
        try:
            me = psutil.Process(self.pid)
        except psutil.NoSuchProcess:
            return
        while not self._stop.is_set():
            total = 0
            try:
                procs = [me] + me.children(recursive=True)
                for p in procs:
                    try:
                        total += p.memory_info().rss
                    except (psutil.NoSuchProcess, psutil.AccessDenied):
                        pass
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
            if total:
                mb = total / (1024.0 * 1024.0)
                if mb > self.peak:
                    self.peak = mb
            time.sleep(self.interval)
        return

    def stop(self):
        self._stop.set()
        self.join(timeout=2.0)
        return self.peak


# ----------------------------------------------------------------------------- worker
class Worker:
    """stdio 行协议 worker。stderr 一律落文件：管道写满会把 worker 卡死。"""

    def __init__(self, cmd, env=None):
        self.proc = subprocess.Popen(
            cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1, env=env)
        self.q = []
        self.cv = threading.Condition()
        t = threading.Thread(target=self._pump, daemon=True)
        t.start()

    def _pump(self):
        for line in self.proc.stdout:
            line = line.strip()
            if not line:
                continue
            with self.cv:
                self.q.append(line)
                self.cv.notify_all()

    def ask(self, obj, timeout):
        rid = obj["id"]
        self.proc.stdin.write(json.dumps(obj) + "\n")
        self.proc.stdin.flush()
        deadline = time.time() + timeout
        while True:
            with self.cv:
                for i, line in enumerate(self.q):
                    try:
                        got = json.loads(line)
                    except Exception:
                        continue
                    if got.get("id") == rid:
                        del self.q[i]
                        return got
                remain = deadline - time.time()
                if remain <= 0:
                    break
                self.cv.wait(min(remain, 0.5))
            if self.proc.poll() is not None:
                raise RuntimeError(f"worker 退出 code={self.proc.returncode}")
        raise TimeoutError(f"worker {timeout:.0f}s 无响应")

    def alive(self):
        return self.proc.poll() is None

    def kill(self):
        try:
            me = psutil.Process(self.proc.pid)
            for c in me.children(recursive=True):
                try:
                    c.kill()
                except psutil.Error:
                    pass
        except psutil.Error:
            pass
        try:
            self.proc.kill()
        except Exception:
            pass


# ----------------------------------------------------------------------------- 单 cell 测量
def make_worker(spec):
    """按 spec 起 worker。path_prepend 用来把 WeasyPrint 的 Pango/GTK DLL 目录塞进 PATH。"""
    env = None
    if spec.get("path_prepend"):
        env = dict(os.environ)
        env["PATH"] = os.pathsep.join(spec["path_prepend"]) + os.pathsep + env.get("PATH", "")
        # Python 3.8+ 不认 PATH，得把同一个目录交给 worker 去 os.add_dll_directory()
        env["WEASY_DLLS"] = spec["path_prepend"][0]
    return Worker(spec["cmd"], env)


def pdf_meta(path):
    """体积 / 页数 / 第一页 MediaBox（pt）。解析不了就记 -1，让人看得见「它没出 PDF」。"""
    try:
        size = Path(path).stat().st_size
    except OSError:
        return None, None, None
    try:
        r = PdfReader(path)
        box = r.pages[0].mediabox
        return size, len(r.pages), (float(box.width), float(box.height))
    except Exception:
        return size, -1, None


def run_cell(name, spec, regime, cell, out_pdf, target_s, rounds):
    """返回逐次记录列表。任何一次失败都如实写进 status，不悄悄跳过。"""
    rows = []
    warmup = None
    est = None
    worker = None
    sampler = None

    def ensure_worker():
        nonlocal worker, sampler
        if worker is not None and worker.alive():
            return worker
        worker = make_worker(spec)
        sampler = RssSampler(worker.proc.pid)
        sampler.start()
        return worker

    def one_job_cold(timeout):
        w = make_worker(spec)
        s = RssSampler(w.proc.pid)
        s.start()
        try:
            j = new_job()
            t0 = time.perf_counter()
            res = w.ask(j, timeout)
            wall = (time.perf_counter() - t0) * 1000.0
            return res, wall, s.stop()
        finally:
            w.kill()

    def job(rid, html_path=None):
        j = {"id": rid, "cmd": "gen", "html": str(html_path or cell["path"]), "out": str(out_pdf)}
        if spec.get("engine_arg"):
            j["engine"] = spec["engine_arg"]
        return j

    seq = [0]

    def vary_write(rid):
        """warmvary：把语料复制一份、末尾追加唯一 token 后再交给引擎。

        warm 口径下所有迭代吃的是同一个文件，跨文档缓存（.NET 字体 face 缓存、
        Chrome 的资源/布局缓存）会白拿红利；服务端真实场景是同一模板换数据，
        缓存命中不了。这一档就是把那部分红利剥掉，剩下的才是纯计算。

        **复制必须发生在计时区外**：早版把 read+write 塞进了 next_job() 的实参求值里，
        于是 wall_ms 里混进了「驱动复制语料」的成本，而且随文档体积线性放大
        （A100 是 1.9MB），把红利读成了 +/− 混杂的假数。
        """
        p = OUT / f"_vary_{name}_{regime}_{rid}.html"
        raw = Path(cell["path"]).read_bytes()
        p.write_bytes(raw + f"\n<!-- {uuid_tokens[rid % len(uuid_tokens)]} -->\n".encode())
        return p

    def new_job():
        """未被计时的 job（预热/试探用）：也照样换 token，保持与正式迭代同形态。"""
        seq[0] += 1
        rid = seq[0]
        return job(rid, vary_write(rid) if regime == "warmvary" else None)

    try:
        if spec["kind"] == "cli":
            # CLI 引擎没有常驻形态：只测 cold
            first = cli_run(name, cell, out_pdf, JOB_TIMEOUT_FLOOR)
            rows.append(dict(base_row(name, regime, cell, 0), **first, rss_peak_mb=first.get("rss")))
            if not first["ok"]:
                return rows
            est = first["wall_ms"] / 1000.0
            iters = pick_iters(est, target_s)
            for it in range(1, iters):
                r = cli_run(name, cell, out_pdf, max(JOB_TIMEOUT_FLOOR, est * 8))
                rows.append(dict(base_row(name, regime, cell, it), **r, rss_peak_mb=r.get("rss")))
                if not r["ok"]:
                    break
            return rows

        # ---- warm：先 1 次试探定迭代数，再 warmup，再正式采样 ----
        timeout = JOB_TIMEOUT_FLOOR
        if regime == "cold":
            res, wall, rss = one_job_cold(timeout)
            est = wall / 1000.0
            iters = pick_iters(est, target_s)
            rows.append(dict(base_row(name, regime, cell, 0), ok=res.get("ok", False),
                             wall_ms=wall, worker_ms=res.get("ms"), err=res.get("err"),
                             rss_peak_mb=rss))
            if not res.get("ok"):
                return rows          # 冷启第一刀就失败：别把 25 次同样的失败烧进账本
            for it in range(1, iters):
                res, wall, rss = one_job_cold(max(timeout, est * 8))
                rows.append(dict(base_row(name, regime, cell, it), ok=res.get("ok", False),
                                 wall_ms=wall, worker_ms=res.get("ms"), err=res.get("err"),
                                 rss_peak_mb=rss))
                if not res.get("ok"):
                    break
            return rows

        w = ensure_worker()
        try:
            probe = w.ask(new_job(), timeout)
        except (TimeoutError, RuntimeError) as e:
            return [dict(base_row(name, regime, cell, 0), ok=False, wall_ms=None,
                         worker_ms=None, err=str(e), rss_peak_mb=sampler.stop())]
        if not probe.get("ok"):
            return [dict(base_row(name, regime, cell, 0), ok=False, wall_ms=None,
                         worker_ms=probe.get("ms"), err=probe.get("err"),
                         rss_peak_mb=sampler.stop())]
        est = (probe.get("ms") or 0) / 1000.0
        iters = pick_iters(est, target_s)
        warmup = 1 if est > 2.0 else 3
        for _ in range(warmup):
            w.ask(new_job(), max(timeout, est * 8))
        for it in range(iters):
            # 计时区外先把这一轮的输入准备好（warmvary 要复制语料），否则量进去的是驱动自己
            seq[0] += 1
            rid = seq[0]
            jp = vary_write(rid) if regime == "warmvary" else None
            t0 = time.perf_counter()
            try:
                res = w.ask(job(rid, jp), max(timeout, est * 8))
            except (TimeoutError, RuntimeError) as e:
                rows.append(dict(base_row(name, regime, cell, it), ok=False,
                                 wall_ms=(time.perf_counter() - t0) * 1000.0,
                                 worker_ms=None, err=str(e), rss_peak_mb=None))
                break
            wall = (time.perf_counter() - t0) * 1000.0
            if jp is not None:
                try:
                    jp.unlink()          # 大档一份副本就近 2MB，别在盘上堆 25 份
                except OSError:
                    pass
            rows.append(dict(base_row(name, regime, cell, it), ok=res.get("ok", False),
                             wall_ms=wall, worker_ms=res.get("ms"), err=res.get("err"),
                             rss_peak_mb=None))
            if not res.get("ok"):
                break
        if sampler:
            peak = sampler.stop()
        else:
            peak = None
        # cell 级 RSS 峰值挂到最后一行（聚合时取该 cell 的 max）
        if rows and peak is not None:
            rows[-1]["rss_peak_mb"] = peak
        return rows
    finally:
        if worker:
            worker.kill()
        for junk in OUT.glob(f"_vary_{name}_{regime}_*.html"):
            try:
                junk.unlink()
            except OSError:
                pass


def cli_run(engine_name, cell, out_pdf, timeout):
    cmd = cli_cmd(engine_name, cell["path"], out_pdf)
    t0 = time.perf_counter()
    try:
        p = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        ps = psutil.Process(p.pid)
        peak = 0.0
        while p.poll() is None:
            try:
                total = ps.memory_info().rss + sum(
                    c.memory_info().rss for c in ps.children(recursive=True))
                peak = max(peak, total / (1024.0 * 1024.0))
            except psutil.Error:
                pass
            time.sleep(0.02)
            if time.perf_counter() - t0 > timeout:
                p.kill()
                return {"ok": False, "wall_ms": (time.perf_counter() - t0) * 1000.0,
                        "worker_ms": None, "err": "timeout", "rss": peak}
        wall = (time.perf_counter() - t0) * 1000.0
        ok = p.returncode == 0 and Path(out_pdf).exists() and Path(out_pdf).stat().st_size > 0
        return {"ok": ok, "wall_ms": wall, "worker_ms": None,
                "err": None if ok else f"exit={p.returncode}", "rss": peak}
    except Exception as e:
        return {"ok": False, "wall_ms": (time.perf_counter() - t0) * 1000.0,
                "worker_ms": None, "err": f"{type(e).__name__}: {str(e)[:200]}", "rss": None}


def base_row(name, regime, cell, it):
    return {"engine": name, "regime": regime, "axis": cell["axis"], "size": cell["size"],
            "file": cell["file"], "iter": it}


# ----------------------------------------------------------------------------- 主流程
def load_cells(axes):
    man = json.loads((CORPUS / "manifest.json").read_text(encoding="utf-8"))
    cells = []
    for f in man["files"]:
        if axes and f["axis"] not in axes:
            continue
        cells.append({
            "axis": f["axis"], "size": f["size"], "file": f["file"],
            "path": CORPUS / f["file"], "sha": f["sha256"], "html_bytes": f["bytes"],
            "meta": f["meta"],
        })
    return cells


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engines", default="")
    ap.add_argument("--axes", default="A,B,C,D,E")
    ap.add_argument("--regimes", default="warm,cold")
    ap.add_argument("--rounds", type=int, default=1)
    ap.add_argument("--cell-only", default="", help="只跑指定档，如 A3,B2000")
    ap.add_argument("--out", default=str(OUT))
    ap.add_argument("--runner", default="",
                    help="覆盖 papersharp worker 的 PdfBench.Runner exe 路径（A/B 双臂对拍用；默认用 BENCH 下的构建产物）")
    args = ap.parse_args()

    if args.runner:
        global RUNNER_EXE
        RUNNER_EXE = Path(args.runner)

    OUT.mkdir(exist_ok=True)
    Path(args.out).mkdir(parents=True, exist_ok=True)
    tbl = engine_table()
    engines = [e.strip() for e in args.engines.split(",") if e.strip()] or list(tbl)
    unknown = [e for e in engines if e not in tbl]
    if unknown:
        print(f"未知引擎 {unknown}；可用：{list(tbl)}", file=sys.stderr)
        return 2
    axes = [a.strip().upper() for a in args.axes.split(",") if a.strip()]
    regimes = [r.strip().lower() for r in args.regimes.split(",") if r.strip()]
    only = {s.strip() for s in args.cell_only.split(",") if s.strip()}
    cells = [c for c in load_cells(axes) if (not only or f"{c['axis']}{c['size']}" in only)]

    raw_path = Path(args.out) / "scale_raw.csv"
    cell_path = Path(args.out) / "scale_cells.csv"
    done = set()
    if raw_path.exists():
        with raw_path.open(newline="", encoding="utf-8") as fh:
            for r in csv.DictReader(fh):
                done.add((r["engine"], r["regime"], r["axis"], r["size"], r.get("round", "1")))

    raw_new = not raw_path.exists()
    raw_f = raw_path.open("a", newline="", encoding="utf-8")
    cell_f = cell_path.open("a", newline="", encoding="utf-8")
    RAW_COLS = ["engine", "regime", "axis", "size", "file", "round", "iter", "wall_ms",
                "worker_ms", "bytes", "pages", "mw_pt", "mh_pt", "ok", "err", "rss_peak_mb"]
    CELL_COLS = ["engine", "regime", "axis", "size", "file", "round", "n", "n_ok", "wall_med_ms",
                 "wall_min_ms", "wall_p95_ms", "wall_max_ms", "rss_peak_mb", "bytes_kb", "pages",
                 "mw_pt", "mh_pt", "elements", "html_kb", "ms_per_page", "ms_per_1k_elem", "status",
                 "sha"]
    raw_w = csv.DictWriter(raw_f, fieldnames=RAW_COLS)
    cell_w = csv.DictWriter(cell_f, fieldnames=CELL_COLS)
    if raw_new:
        raw_w.writeheader()
        cell_w.writeheader()

    print(f"engines={engines}\naxes={axes} regimes={regimes} cells={len(cells)} "
          f"rounds={args.rounds}\n", flush=True)

    for rnd in range(1, args.rounds + 1):
        for cell in cells:
            tag = f"{cell['axis']}{cell['size']}"
            for name in engines:
                spec = tbl[name]
                if spec.get("axes") and cell["axis"] not in spec["axes"]:
                    continue      # 引擎级轴白名单（同内核的包装层不必把五轴全复述一遍）
                for regime in regimes:
                    if spec["kind"] == "cli" and regime in ("warm", "warmvary"):
                        continue      # CLI 引擎没有常驻形态，不硬凑一个假 warm
                    if (name, regime, cell["axis"], str(cell["size"]), str(rnd)) in done:
                        print(f"[skip] {name}/{regime}/{tag} r{rnd}", flush=True)
                        continue
                    out_pdf = OUT / f"_job_{name}_{tag}_{regime}_{rnd}.pdf"
                    t0 = time.time()
                    rows = run_cell(name, spec, regime, cell, out_pdf,
                                    WARM_TARGET_S if regime != "cold" else COLD_TARGET_S, rnd)
                    ok_rows = [r for r in rows if r.get("ok")]
                    walls = sorted(r["wall_ms"] for r in ok_rows if r.get("wall_ms"))
                    rss_vals = [r["rss_peak_mb"] for r in rows if r.get("rss_peak_mb")]
                    size_, pages_, box_ = pdf_meta(out_pdf) if ok_rows else (None, None, None)
                    mw = box_[0] if box_ else None
                    mh = box_[1] if box_ else None
                    for r in ok_rows:
                        r["bytes"], r["pages"], r["mw_pt"], r["mh_pt"] = size_, pages_, mw, mh
                    for r in rows:
                        raw_w.writerow({
                            "engine": name, "regime": regime, "axis": cell["axis"],
                            "size": cell["size"], "file": cell["file"], "round": rnd,
                            "iter": r.get("iter"), "wall_ms": fmt(r.get("wall_ms")),
                            "worker_ms": fmt(r.get("worker_ms")), "bytes": fmt(r.get("bytes")),
                            "pages": r.get("pages", ""), "mw_pt": fmt(r.get("mw_pt")),
                            "mh_pt": fmt(r.get("mh_pt")),
                            "ok": 1 if r.get("ok") else 0,
                            "err": (r.get("err") or "").replace("\n", " ")[:200],
                            "rss_peak_mb": fmt(r.get("rss_peak_mb")),
                        })
                    raw_f.flush()

                    status = "ok" if ok_rows else "fail"
                    if ok_rows and len(ok_rows) < len(rows):
                        status = "partial"
                    med = statistics.median(walls) if walls else None
                    elems = cell["meta"].get("elements")
                    cell_w.writerow({
                        "engine": name, "regime": regime, "axis": cell["axis"],
                        "size": cell["size"], "file": cell["file"], "round": rnd,
                        "n": len(rows), "n_ok": len(ok_rows),
                        "wall_med_ms": fmt(med),
                        "wall_min_ms": fmt(walls[0] if walls else None),
                        "wall_p95_ms": fmt(walls[int(len(walls) * 0.95) - 1] if walls else None),
                        "wall_max_ms": fmt(walls[-1] if walls else None),
                        "rss_peak_mb": fmt(max(rss_vals) if rss_vals else None),
                        "bytes_kb": fmt(size_ / 1024.0 if size_ else None),
                        "pages": pages_ if pages_ is not None else "",
                        "mw_pt": fmt(mw), "mh_pt": fmt(mh),
                        "elements": elems, "html_kb": fmt(cell["html_bytes"] / 1024.0),
                        "ms_per_page": fmt(med / pages_ if med and pages_ and pages_ > 0 else None),
                        "ms_per_1k_elem": fmt(med * 1000.0 / elems if med and elems else None),
                        "status": status, "sha": cell["sha"],
                    })
                    cell_f.flush()
                    try:
                        out_pdf.unlink()
                    except OSError:
                        pass
                    print(f"[{name}/{regime}/{tag}] r{rnd} n={len(ok_rows)}/{len(rows)} "
                          f"med={fmt(med)}ms rss={fmt(max(rss_vals) if rss_vals else None)}MB "
                          f"pages={pages_} box={fmt(mw)}x{fmt(mh)} "
                          f"({time.time()-t0:.0f}s) {status}", flush=True)
    raw_f.close()
    cell_f.close()
    print(f"\nraw  -> {raw_path}\ncell -> {cell_path}")
    return 0


def fmt(v):
    if v is None or v == "":
        return ""
    if isinstance(v, float):
        return f"{v:.2f}"
    return str(v)


if __name__ == "__main__":
    sys.exit(main())
