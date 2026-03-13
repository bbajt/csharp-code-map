#!/usr/bin/env bash
set -euo pipefail

VERSION=${1:-1.0.0}
echo "Building CodeMap v${VERSION}"

# Pack as .NET global tool
echo "=== Packing .NET global tool ==="
dotnet pack src/CodeMap.Daemon -c Release -p:Version=${VERSION} -o dist/nupkg

# Self-contained binaries (no trimming — Roslyn uses runtime reflection)
for RID in win-x64 linux-x64 osx-arm64; do
  echo "=== Publishing ${RID} ==="
  dotnet publish src/CodeMap.Daemon -c Release -r ${RID} \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:PublishTrimmed=false \
    -p:Version=${VERSION} \
    -o dist/${RID}
done

echo ""
echo "=== Build complete ==="
echo "Tool package:  dist/nupkg/codemap-mcp.${VERSION}.nupkg"
echo "Windows x64:   dist/win-x64/CodeMap.Daemon.exe"
echo "Linux x64:     dist/linux-x64/CodeMap.Daemon"
echo "macOS ARM64:   dist/osx-arm64/CodeMap.Daemon"
