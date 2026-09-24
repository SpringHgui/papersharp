#!/bin/bash
# 引擎盒矩形逐字节对拍：当前工作树 vs 未优化的 HEAD 工作树（../ps-base）。
# 用法：bash tools/selfdiff.sh            # 全样例
#       bash tools/selfdiff.sh m1 invoice # 只比给定样例
set -u
cd "$(dirname "$0")/.."
run_in() { # $1 = repo root, $2 = tag, rest = samples
  local root="$1" tag="$2"; shift 2
  local dst="/d/Github/boxdump-$tag"
  mkdir -p "$dst"
  for s in "$@"; do
    ( cd "$root" && dotnet run --no-build --project src/PaperSharpSamples -c Release -- boxes "$s" 1284 >/dev/null 2>&1 )
    src=$(ls "$root"/src/PaperSharpSamples/bin/Release/net*/output/boxes-$s.json 2>/dev/null | head -1)
    if [ -z "$src" ]; then echo "MISS $s ($tag)"; continue; fi
    cp "$src" "$dst/boxes-$s.json"
  done
}
samples=${*:-$(ls src/PaperSharpSamples/assets/*.html | xargs -n1 basename | sed 's/\.html//' | grep -v '^_')}
run_in "D:/Github/ps-base"    head  $samples
run_in "D:/Github/paper-sharp" work  $samples
same=0; diff=0; bad=""
for s in $samples; do
  a=/d/Github/boxdump-head/boxes-$s.json
  b=/d/Github/boxdump-work/boxes-$s.json
  if [ ! -f "$a" ] || [ ! -f "$b" ]; then echo "MISSING $s"; bad="$bad $s(miss)"; continue; fi
  if cmp -s "$a" "$b"; then same=$((same+1)); else diff=$((diff+1)); bad="$bad $s"; fi
done
echo "identical=$same differing=$diff  differing_list:$bad"
