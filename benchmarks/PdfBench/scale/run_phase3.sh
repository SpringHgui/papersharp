#!/usr/bin/env bash
# 第三轮：语料修正后的重测 + 锚点复核。串行执行（同机并发测量会互相污染）。
set -uo pipefail
cd "$(dirname "$0")"
PY="G:/Github/papersharp/.scratch/pdfbench-venv/Scripts/python.exe"
SIX="papersharp,puppeteer,playwright,chrome-cli,wkhtmltopdf,weasyprint"

echo "### 0/6 锚点复核：自家 bench 复量 invoice/report/table（判定 Z 轴差值是口径还是退化）"
bash verify_anchor.sh 2>&1 | tee out/verify_anchor.txt | tail -10

echo "### 0.5 清掉被污染的 warmvary 行（早版把「复制语料」放进了计时区）"
"$PY" purge_regime.py warmvary

echo "### 1/6 把初版 C 语料（padding 塌宽）整体迁到 X 轴"
python migrate_c_to_x.py

echo "### 2/6 重出干净 C 语料"
python gen.py --axes C

echo "### 3/6 重测 C 轴（干净层叠轴）"
"$PY" sweep.py --engines "$SIX" --axes C --regimes warm,cold

echo "### 4/6 重测 warmvary"
"$PY" sweep.py --engines papersharp,puppeteer,playwright,weasyprint \
  --cell-only A10,A100,B2000,D200000 --regimes warmvary

echo "### 5/6 完整性通道重出 C/X 两轴产物（语料变了，旧产物必须 --force 重生成）"
"$PY" integrity.py --engines "$SIX" --axes C,X --force

echo "### 6/6 环境快照（rev + dirty 指纹 + 各家版本）"
"$PY" envsnap.py > out/envsnap.txt 2>&1; tail -3 out/envsnap.txt

echo "### done"
