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
# 无头浏览器调试端口默认 9411 是**固定**的：同机多个会话同时跑 boxdiff 会互相打断 CDP，
# 表现就是 "Execution context was destroyed" 或量到半张图（实测 fluent-docs 在并发下时好时坏）。
# 这里默认按进程号错开（变量名不叫 PORT——bash 里 PORT 是登录终端端口的内建变量），保证一轮 sweep 的浏览器真值不被别人踩。
CDPPORT=${CDPPORT:-$((9500 + $$ % 400))}
OUTDIR=.scratch/pixelsweep
mkdir -p "$OUTDIR"
SAMPLES=${*:-$(ls src/PaperSharpSamples/assets/*.html | xargs -n1 basename | sed 's/\.html//' | grep -v '^_')}

# 参照物默认关掉 LCD 次像素着色（NO_LCD=0 可恢复原口径）。
# 为什么（2026-09-29 取证）：Windows 上 Chrome/Edge 给屏幕文字默认走 ClearType，字干两侧带 R≠B 彩边，
# 而引擎（以及任何 PDF/打印输出）是灰度 AA。pixdiff 取三通道最大差，于是每根字干边缘都被记成强差：
# long 一页实测 彩边率 浏览器 51.5% / 引擎 16.0%，关掉之后 1x 可见重影 44.8% → 24.4%（彩边率两边 16.3% 对齐）。
# 本引擎的目标介质是 PDF/PNG，那里根本没有次像素彩边，所以参照物也不该带它。
export NO_LCD=${NO_LCD:-1}

for s in $SAMPLES; do
  dotnet run --project src/PaperSharpSamples -c Release --no-build -- boxes "$s" "$WIDTH" >/dev/null 2>&1
  node tools/boxdiff.mjs "$s" --dsf 2 --port "$CDPPORT" --shot "$OUTDIR/shot-$s.png" >/dev/null 2>&1
  raw="$OUTDIR/$s.txt"
  dotnet run --project src/PaperSharpSamples -c Release --no-build -- pixdiff "$s" "$OUTDIR/shot-$s.png" "$WIDTH" > "$raw" 2>&1
  # 三块指标各取各的：2x 强差在「2x 强差 …」行、1x 可见与判定同行、重影评分在 GHOST 行
  strong=$(grep -oE "2x 强差 [0-9,]+/[0-9,]+ = [0-9.]+%" "$raw" | tail -1 | grep -oE "= [0-9.]+%" | tr -d '= %')
  vis=$(grep -oE "1x 可见重影 [0-9,]+/[0-9,]+ = [0-9.]+%" "$raw" | tail -1 | grep -oE "= [0-9.]+%" | tr -d '= %')
  verdict=$(grep -oE "→ (干净|轻微|可接受|偏重|严重)" "$raw" | tail -1 | sed 's/→ //')
  bands=$(grep -oE "bands=[0-9]+/[0-9]+" "$raw" | tail -1 | cut -d= -f2)     # 真位移带（可修的那类）
  # 两条「不可比」判定，先于位移带读：pixdiff 的位移带是在**对齐之后**数的，
  # ① 两层**墨尽行**差 >2 CSS px → 比的不是同一份文档（一边多画/少画了一截）。
  #    注意别拿位图总高判：引擎长图含 @page 上下边距、浏览器截图含视口留白，两边都是空白，
  #    总高差会把一大批其实可比的页误标成「高度不可比」（report / m1 就是这么被误伤的）。
  # ② 全局整数平移没收敛（SAD 明显 <100%）→ 残余的整体 1px 偏移会被逐条边重复计成「位移带」。
  hh=$(grep -oE "墨尽行 引擎 [0-9.]+ vs 浏览器 [0-9.]+" "$raw" | tail -1)
  eh=$(echo "$hh" | grep -oE "引擎 [0-9.]+" | cut -d' ' -f2); bh=$(echo "$hh" | grep -oE "浏览器 [0-9.]+" | cut -d' ' -f2)
  sad=$(grep -oE "全局补偿 dx[+-]?[0-9.]* dy[+-]?[0-9.]* \(SAD [0-9]+%\)" "$raw" | tail -1 | grep -oE "SAD [0-9]+" | grep -oE "[0-9]+")
  flag=""
  [ -n "$eh" ] && [ -n "$bh" ] && [ "$(python -c "print(int(abs($eh-$bh)>2))")" = "1" ] && flag="墨尽不齐(${eh}vs${bh})"
  [ -n "$sad" ] && [ "$sad" -lt 95 ] && flag="$flag 对齐未收敛(SAD${sad}%)"
  printf '%-16s 2x强差 %-6s%%  1x可见 %-6s%%  位移带 %-8s %-10s %s\n' "$s" "${strong:-?}" "${vis:-?}" "${bands:-?}" "${verdict:-未判定}" "$flag"
done
