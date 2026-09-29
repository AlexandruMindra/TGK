#!/usr/bin/env bash
# Publishes tgk-server as a self-contained single-file executable (no .NET install needed on the target).
# Usage: scripts/publish-server.sh [rid ...]    default: linux-x64 win-x64    output: dist/server/<rid>/
set -euo pipefail
cd "$(dirname "$0")/.."

rids=("$@")
[ ${#rids[@]} -gt 0 ] || rids=(linux-x64 win-x64)

for rid in "${rids[@]}"; do
  dotnet publish src/TGK.Server -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none \
    -o "dist/server/$rid"
  echo "Published dist/server/$rid"
done
