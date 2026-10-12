#!/bin/bash
# 像素级重影对拍：无头浏览器 2x 截图 → 引擎 2x 光栅逐像素求差。
#   ./pdiff.sh <sample>     单样例：打印每条位移带（附落在线上的元素路径），落 output/pixdiff-<sample>{,-e1,-b1}.png
#   ./pdiff.sh              全样例：每行一条 GHOST 汇总
cd "$(dirname "$0")"
EXE=src/PaperSharpSamples/bin/Release/net10.0/PaperSharpSamples.exe
OUT=src/PaperSharpSamples/bin/Release/net10.0/output
shot_of() { local s=$1;
  $EXE boxes "$s" 1284 >/dev/null 2>&1
  for t in 1 2 3; do
    # 端口随机：上一次无头 Edge 的僵尸进程占着 9411 时，新实例换了端口而 pollJson 连到僵尸 → 假失败
    node tools/boxdiff.mjs "$s" --dsf 2 --port $((10000 + RANDOM % 50000)) --shot "$OUT/browser-$s.png" >/dev/null 2>&1 && return 0
  done; return 1; }

if [ $# -gt 0 ]; then
  shot_of "$1" || { echo "$1 ERR 截图失败"; exit 1; }
  $EXE pixdiff "$1" "$OUT/browser-$1.png" 1284
else
  for f in src/PaperSharpSamples/assets/*.html; do
    s=$(basename "$f" .html); case "$s" in _*) continue;; esac
    shot_of "$s" && $EXE pixdiff "$s" "$OUT/browser-$s.png" 1284 2>&1 | grep "^GHOST" || echo "GHOST $s ERR"
  done
fi
