#!/bin/bash
# 全样例对拍汇总：dotnet 出引擎盒 → 无头浏览器量同一文档 → 打印漂移数
cd "$(dirname "$0")/.."
for s in $(ls src/PaperSharpSamples/assets/*.html | xargs -n1 basename | sed 's/\.html//' | grep -v '^_'); do
  dotnet run --project src/PaperSharpSamples -c Release -- boxes "$s" 1284 >/dev/null 2>&1
  out=$(node tools/boxdiff.mjs "$s" 2>&1 | head -1)
  echo "$out"
done
