#!/bin/bash
# 绘制层对拍汇总（Chrome 真值 vs 引擎，逐像素）：补齐 tools/sweep.sh 只量盒矩形的盲区。
#
#   bash tools/pixelsweep.sh                 # 全部非下划线样例
#   bash tools/pixelsweep.sh login-page m9   # 只跑指定样例
#   WIDTH=1284 KEEP=1 bash tools/pixelsweep.sh
#
# 为什么要这一条（2026-09-28 取证，见 docs/chrome-parity-todo.md P1-4）：
# 盒级对拍证明每个元素的矩形对上了，但「盒矩形不变而像素变了」的类别——圆角、阴影、渐变、
# blend、伪元素装饰、边框描边——它一个都看不见。login-page 当时是 33 盒全绿 / 0 漂移，
# 页面上却有三处肉眼可见的错（伪元素声明冒充宿主、绝对定位伪元素不成盒、闸门吃掉整条规则）。
#
# 口径与手工对拍逐字同构：dotnet boxes（引擎 2x + 预览文档）→ boxdiff --dsf 2 --shot
# （无头浏览器 2x 截图 + body 原点 meta）→ pixdiff（裁到 body 原点、先搜整数平移再逐像素）。
# 每样例一行：2x 强差 % · 1x 可见重影 % · 重影评分 · 判定。差异三联图落在 .scratch/pixelsweep/。
cd "$(dirname "$0")/.." || exit 1
WIDTH=${WIDTH:-1284}
OUTDIR=.scratch/pixelsweep
mkdir -p "$OUTDIR"
SAMPLES=${*:-$(ls src/PaperSharpSamples/assets/*.html | xargs -n1 basename | sed 's/\.html//' | grep -v '^_')}

for s in $SAMPLES; do
  dotnet run --project src/PaperSharpSamples -c Release --no-build -- boxes "$s" "$WIDTH" >/dev/null 2>&1
  node tools/boxdiff.mjs "$s" --dsf 2 --shot "$OUTDIR/shot-$s.png" >/dev/null 2>&1
  raw="$OUTDIR/$s.txt"
  dotnet run --project src/PaperSharpSamples -c Release --no-build -- pixdiff "$s" "$OUTDIR/shot-$s.png" "$WIDTH" > "$raw" 2>&1
  # 三块指标各取各的：2x 强差在「2x 强差 …」行、1x 可见与判定同行、重影评分在 GHOST 行
  strong=$(grep -oE "2x 强差 [0-9,]+/[0-9,]+ = [0-9.]+%" "$raw" | tail -1 | grep -oE "= [0-9.]+%" | tr -d '= %')
  vis=$(grep -oE "1x 可见重影 [0-9,]+/[0-9,]+ = [0-9.]+%" "$raw" | tail -1 | grep -oE "= [0-9.]+%" | tr -d '= %')
  verdict=$(grep -oE "→ (干净|轻微|可接受|偏重|严重)" "$raw" | tail -1 | sed 's/→ //')
  bands=$(grep -oE "bands=[0-9]+/[0-9]+" "$raw" | tail -1 | cut -d= -f2)     # 真位移带（可修的那类）
  printf '%-16s 2x强差 %-6s%%  1x可见 %-6s%%  位移带 %-8s %s\n' "$s" "${strong:-?}" "${vis:-?}" "${bands:-?}" "${verdict:-未判定}"
done
