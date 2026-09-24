#!/bin/bash
# 临时：重编译（把 assets 拷进 bin）+ 出盒 + 对拍 triage。用法：./pb.sh <probe-name> [N]
cd "$(dirname "$0")"
dotnet build src/PaperSharpSamples -c Release -v q --nologo >/dev/null 2>&1
dotnet run --project src/PaperSharpSamples -c Release --no-build -- boxes "$1" 1284 >/dev/null 2>&1
node tools/boxtriage.mjs "$1" "${2:-14}"
