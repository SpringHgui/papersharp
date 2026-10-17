#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""环境快照：把「这批数字是在什么状态下量出来的」钉成一个文件。

PaperSharp 侧尤其关键：本仓工作树长期有大量在途改动，
不记 rev + dirty 指纹的话，一周后没人能复现这批曲线。
"""
import json
import os
import platform
import subprocess
import sys
from datetime import datetime
from pathlib import Path

import sweep

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def sh(cmd, timeout=30):
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout,
                           encoding="utf-8", errors="replace")
        s = (p.stdout or "").strip()
        if p.returncode != 0 and not s:
            # 别把 stderr 的首行当版本字符串返回：早版就是这么把 NameError 记成版本号的
            return f"<失败 rc={p.returncode}: {(p.stderr or '').strip().splitlines()[:1]}>".replace(
                "[", "").replace("]", "")
        return s.splitlines()[0] if s else ""
    except Exception as e:
        return f"<err {type(e).__name__}>"


def sh_all(cmd, timeout=30):
    """多行输出必须整体拿：早版复用 sh() 只取首行，把 dirty_files 恒算成 1。"""
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout,
                           encoding="utf-8", errors="replace")
        return (p.stdout or "").strip()
    except Exception as e:
        return f"<err {type(e).__name__}>"


def chrome_version():
    """`chrome --version` 在 Windows 上会挂（要拉进程/弹窗口），改问文件版本信息。"""
    return sh(["powershell", "-NoProfile", "-Command",
               f"(Get-Item '{sweep.CHROME}').VersionInfo.ProductVersion"])


def weasy_version():
    """必须走 worker 那套 add_dll_directory，否则 import 失败打出的是故障横幅。"""
    dlls = sweep.WEASY_DLLS
    code = "import weasyprint; print(weasyprint.__version__)"
    # 注意顺序：add_dll_directory 要用 os，所以 os 必须在它之前导入（早版这里 NameError）
    pre = f"import os; os.add_dll_directory(r'{dlls}'); " if Path(dlls).exists() else ""
    return sh([str(sweep.VENV_PY), "-c", pre + code])


def git(*a):
    return sh(["git", "-C", str(ROOT), *a])


def main():
    dirty = sh_all(["git", "-C", str(ROOT), "status", "--porcelain"])
    snap = {
        "time": datetime.now().astimezone().isoformat(timespec="seconds"),
        "os": f"{platform.system()} {platform.release()} build {platform.version()}",
        "cpu": (platform.processor() or "?"),
        "cores": os.cpu_count(),
        "python": sys.version.split()[0],
        "node": sh(["node", "--version"]),
        "dotnet": sh(["dotnet", "--version"]),
        "chrome": chrome_version(),
        "wkhtmltopdf": sh([sweep.WK, "--version"]),
        "weasyprint": weasy_version(),
        "git": {
            "branch": git("rev-parse", "--abbrev-ref", "HEAD"),
            "head": git("rev-parse", "--short", "HEAD"),
            "head_subject": git("log", "-1", "--format=%s"),
            "dirty_files": len([l for l in dirty.splitlines() if l.strip()]),
            "dirty_src_files": len([l for l in dirty.splitlines()
                                    if l[3:].strip().startswith("src/")]),
        },
        "adapters_present": [n for n, s in sweep.engine_table().items()
                             if n != "weasyprint-cli"],
        "measurement_policy": {
            "latency": "驱动侧墙钟（含落盘），median",
            "regimes": ["warm", "warmvary", "cold"],
            "rss": "worker pid + 全部后代工作集峰值",
            "iters": "自适应：目标预算 / 首次探测耗时，cell 级硬上限",
        },
    }
    out = HERE / "out" / "env.json"
    out.parent.mkdir(exist_ok=True)
    out.write_text(json.dumps(snap, ensure_ascii=False, indent=1), encoding="utf-8")
    print(json.dumps(snap, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
