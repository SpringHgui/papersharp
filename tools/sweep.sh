#!/bin/bash
# 全样例几何对拍汇总：dotnet 出引擎盒 → 无头浏览器量同一文档 → 打印漂移数。
#   bash tools/sweep.sh [样例…]            默认全部非下划线样例，tol 0.5
#   TOL=0.05 bash tools/sweep.sh m1 report  收紧到亚像素
#   JSON=1 bash tools/sweep.sh m7           逐盒明细落 .scratch/sweep/<样例>.json
#
# 两处口径修正（2026-09-29 复核）：
# ① 两侧排在**同一个宽**上。boxdiff 交给浏览器的视口宽是 `Math.round(contentWidth)`（CDP 的 width
#    只能是整数），引擎却按分数宽排（16mm 边距 → 1163.0552）。差的那 δ 让 mm 边距页**每个块**都差一点
#    宽，占当时残余漂移的 35%（report 35/35、pct 24/24、polish 15/17 全是这么来的）。这里把引擎按
#    W−δ 重排一遍，让它的 contentWidth 正好落到浏览器那个整数上：修后 report/pct/polish 直接归零。
# ② CDP 端口按进程号错开（写死 9411 会被同机其它会话打断，见台账 P2-5），且 --no-build
#    （原来每样例都过一次编译器，26 张白跑十几分钟，还会和并行会话抢 build）。
cd "$(dirname "$0")/.." || exit 1
TOL=${TOL:-0.5}
WIDTH=${WIDTH:-1284}
CDPPORT=${CDPPORT:-$((9860 + $$ % 80))}
OUT=.scratch/sweep
mkdir -p "$OUT"
BJ=src/PaperSharpSamples/bin/Release/net10.0/output
SAMPLES=${*:-$(ls src/PaperSharpSamples/assets/*.html | xargs -n1 basename | sed 's/\.html//' | grep -v '^_')}

for s in $SAMPLES; do
  dotnet run --project src/PaperSharpSamples -c Release --no-build -- boxes "$s" "$WIDTH" >/dev/null 2>&1
  W=$(python -c "
import json,io
try:
    d=json.load(io.open('$BJ/boxes-$s.json',encoding='utf-8')); cw=d.get('contentWidth') or $WIDTH
except Exception:
    cw=$WIDTH
print('$WIDTH' if abs(cw-round(cw))<0.002 else round($WIDTH-(cw-round(cw)),4))")
  if [ "$W" != "$WIDTH" ]; then
    dotnet run --project src/PaperSharpSamples -c Release --no-build -- boxes "$s" "$W" >/dev/null 2>&1
  fi
  if [ "$JSON" = "1" ]; then
    node tools/boxdiff.mjs "$s" --port "$CDPPORT" --tol "$TOL" --json > "$OUT/$s.json" 2>/dev/null
    for try in 1 2 3; do [ -s "$OUT/$s.json" ] && break; sleep 2
      node tools/boxdiff.mjs "$s" --port "$CDPPORT" --tol "$TOL" --json > "$OUT/$s.json" 2>/dev/null; done
    head -1 "$OUT/$s.json" >/dev/null
    python -c "
import json,io
d=json.load(io.open('$OUT/$s.json',encoding='utf-8'))
s=d['summary']
print(f\"{s['drift']:>5} 漂移 / {s['ok']+s['drift']:<5} 盒 · 单边 {s['engineOnly']}+{s['browserOnly']} · 最坏 {s['worst']:.2f}px  (宽 $W)\")" | sed "s/^/$s /"
  else
    node tools/boxdiff.mjs "$s" --port "$CDPPORT" --tol "$TOL" 2>&1 | head -1
  fi
done
