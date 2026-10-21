#!/bin/bash
# 与 sweep.sh 同法，但按 1px 容差汇总，且只跑指定样例（默认全部非探针样例）。
cd "$(dirname "$0")/.."
TOL=${TOL:-1}
LIST=${*:-$(ls src/PaperSharpSamples/assets/*.html | xargs -n1 basename | sed 's/\.html//' | grep -v '^_' | tr '\n' ' ')}
for s in $LIST; do
  dotnet run --project src/PaperSharpSamples -c Release -- boxes "$s" 1284 >/dev/null 2>&1 || { echo "$s ENGINE-FAIL"; continue; }
  node tools/boxdiff.mjs "$s" --tol "$TOL" 2>&1 | head -1
done
