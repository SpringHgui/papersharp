#!/bin/bash
# 全样例几何对拍汇总：dotnet 出引擎盒 → 无头浏览器量同一文档 → 打印漂移数。
#   bash tools/sweep.sh [样例…]            默认全部非下划线样例，tol 0.5
#   TOL=0.05 bash tools/sweep.sh m1 report  收紧到亚像素
#   JSON=1 bash tools/sweep.sh m7           逐盒明细落 .scratch/sweep/<样例>.json
#
# 两处口径修正（2026-09-29 复核；2026-09-30 被 P0-6 取代，见下）：
# ① ~~两侧排在同一个宽上：把引擎按 W−δ 重排~~ 已由 P0-6 根治：预览文档现在带上 @page 边距
#    （落成 html padding），boxdiff 交给浏览器的视口宽是**整页宽**——本来就是整数，两侧的内容宽
#    自然相等，分数边距不再需要 δ 补偿。旧口径反而会把两侧错开 δ（页宽被改，边距没被改）。
# ② CDP 端口按进程号错开（写死 9411 会被同机其它会话打断，见台账 P2-5），且 --no-build
#    （原来每样例都过一次编译器，26 张白跑十几分钟，还会和并行会话抢 build）。
cd "$(dirname "$0")/.." || exit 1
TOL=${TOL:-0.5}
WIDTH=${WIDTH:-1284}
CDPPORT=${CDPPORT:-$((9860 + $$ % 80))}
OUT=.scratch/sweep
mkdir -p "$OUT"
SAMPLES=${*:-$(ls src/PaperSharpSamples/assets/*.html | xargs -n1 basename | sed 's/\.html//' | grep -v '^_')}

for s in $SAMPLES; do
  dotnet run --project src/PaperSharpSamples -c Release --no-build -- boxes "$s" "$WIDTH" >/dev/null 2>&1
  W=$WIDTH
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
