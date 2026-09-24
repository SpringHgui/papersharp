#!/bin/bash
cd "$(dirname "$0")"
for s in "$@"; do
  dotnet run --project src/PaperSharpSamples -c Release --no-build -- boxes "$s" 1284 >/dev/null 2>&1
  node tools/boxstats.mjs "$s"
done
