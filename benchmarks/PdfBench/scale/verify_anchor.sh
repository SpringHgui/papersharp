#!/usr/bin/env bash
# 锚点复核：Z1(invoice) 在本 harness 里报 60.8ms，而 PERF_REPORT §11.5 记的是 31.0ms。
# 两种可能，必须分清：
#   (a) 口径不同 —— 自家 bench 直接调 RenderPdf 且 css 走第二参数，本 harness 把 css 内联进
#       <style> 且计的是含落盘的墙钟；
#   (b) 真退化 —— 09-26 之后累积的排版修复（1/64 网格、字距、行盒）把渲染成本吃掉了。
# 跑法：在**没有其它测量在跑**的时候执行（它会占满 CPU）。
set -euo pipefail
cd "$(dirname "$0")/../../.."
dotnet build src/PaperSharpSamples -c Release -v q --nologo
dotnet run -c Release --no-build --project src/PaperSharpSamples -- bench 30 \
  | grep -E '^BENCH_BEGIN|^LAT,(invoice|report|table),PDF|^STAGE,(invoice|report|table),'
