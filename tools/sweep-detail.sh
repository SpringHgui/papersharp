#!/bin/bash
# 全样例对拍 + 逐盒明细落盘（一次 pass 出汇总行与 JSON 明细，供按根因归类用）。
#   bash tools/sweep-detail.sh [容差] [样例…]     默认 tol=1、全部非探针样例
# 明细落 .scratch/sweep-<t>/  <name>.json / 汇总 .scratch/sweep-<t>/ALL.txt
cd "$(dirname "$0")/.."
TOL=${1:-1}; shift || true
LIST=${*:-$(ls src/PaperSharpSamples/assets/*.html | xargs -n1 basename | sed 's/\.html//' | grep -v '^_' | tr '\n' ' ')}
OUT=".scratch/sweep-$TOL"
mkdir -p "$OUT"
: > "$OUT/ALL.txt"
for s in $LIST; do
  dotnet run --project src/PaperSharpSamples -c Release -- boxes "$s" 1284 >/dev/null 2>&1 || { echo "$s ENGINE-FAIL" | tee -a "$OUT/ALL.txt"; continue; }
  node tools/boxdiff.mjs "$s" --tol "$TOL" --json > "$OUT/$s.json" 2>"$OUT/$s.err" || { echo "$s DIFF-FAIL $(head -c 120 "$OUT/$s.err")" | tee -a "$OUT/ALL.txt"; continue; }
  # 汇总从同一份 JSON 里取，不再重复起浏览器
  node -e "
  const d=JSON.parse(require('fs').readFileSync('$OUT/$s.json','utf8')).summary;
  const l='$s @ ${TOL}px  ok '+d.ok+' 漂移 '+d.drift+' 引擎独 '+d.engineOnly+' 浏览器独 '+d.browserOnly+' 最坏 '+d.worst.toFixed(1)+'px';
  console.log(l); require('fs').appendFileSync('$OUT/ALL.txt', l+'\n');
  "
done
cat "$OUT/ALL.txt"
echo "明细目录：$OUT"
