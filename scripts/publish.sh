#!/usr/bin/env bash
# Publishes MCPHub as a single self-contained executable per runtime into artifacts/<rid>/.
# Usage:  ./scripts/publish.sh            (win-x64 + linux-x64)
#         ./scripts/publish.sh win-x64    (one rid)
set -euo pipefail
cd "$(dirname "$0")/.."

rids=("$@")
if [ ${#rids[@]} -eq 0 ]; then
  rids=(win-x64 linux-x64)
fi

for rid in "${rids[@]}"; do
  echo ">> Publishing $rid -> artifacts/$rid"
  dotnet publish src/MCPHub.App/MCPHub.App.csproj -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o "artifacts/$rid"
done

echo "Done. See artifacts/."
