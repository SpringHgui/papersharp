#!/usr/bin/env bash
# 规模扫描补测链（一条命令跑完，断了重跑会 skip 已完成 cell）。
# 顺序刻意如此：锚点先出（要给横向曲线定性），再补慢引擎，最后才是不参与计时的完整性通道。
set -euo pipefail
cd "$(dirname "$0")"
PY="G:/Github/papersharp/.scratch/pdfbench-venv/Scripts/python.exe"
FIVE="papersharp,puppeteer,playwright,chrome-cli,wkhtmltopdf"
SIX="papersharp,puppeteer,playwright,chrome-cli,wkhtmltopdf,weasyprint"

echo "### 1/5 生成锚点档 Z（仓库既有样例），并测一遍 —— 用来标定测量路径本身的水分"
python gen.py --axes Z
"$PY" sweep.py --engines "$FIVE" --axes Z --regimes warm,cold

echo "### 2/5 WeasyPrint 全轴（独立排版引擎，主扫时它还在装依赖）"
"$PY" sweep.py --engines weasyprint --regimes warm,cold

echo "### 3/5 warmvary：把「同一份文档反复出」的跨文档缓存红利剥掉"
"$PY" sweep.py --engines papersharp,puppeteer,playwright,weasyprint \
  --cell-only A10,A100,B2000,D200000 --regimes warmvary

echo "### 4/5 离散度复量（round 2，看同机同轮次的抖动有多大）"
"$PY" sweep.py --engines papersharp,puppeteer,wkhtmltopdf,weasyprint \
  --cell-only A1,A30,B2000,C100,D200000 --regimes warm,cold --rounds 2

echo "### 5/5 完整性通道：各出一份 PDF 存档并核对画全没画全（不产时间数字）"
"$PY" integrity.py --engines "$SIX"

echo "### done"
