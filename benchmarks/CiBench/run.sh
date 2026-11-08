#!/usr/bin/env bash
# CiBench 一键运行（bash / Git Bash / Linux CI）
#   ./run.sh                     全量：冷/热各 20 次 + 100/200/300 并发
#   ./run.sh --quick            快速档
#   ./run.sh --engines papersharp,puppeteer --repeats 5
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$HERE/CiBench.csproj"
DLL="$HERE/bin/Release/net10.0/CiBench.dll"

dotnet build "$PROJ" -c Release
dotnet "$DLL" "$@"
