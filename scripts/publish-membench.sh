#!/usr/bin/env bash
# Builds standalone membench executables (no .NET install needed on the target) into dist/<rid>/.
#   scripts/publish-membench.sh                    # win-x64 win-arm64 linux-x64 linux-arm64
#   scripts/publish-membench.sh win-x64            # just one
# Needs the .NET 10 SDK, or run it in Docker:
#   docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 scripts/publish-membench.sh
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
dotnet=${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}
rids=("$@"); [ ${#rids[@]} -gt 0 ] || rids=(win-x64 win-arm64 linux-x64 linux-arm64)
for rid in "${rids[@]}"; do
    "$dotnet" publish "$here/src/MemBench/MemBench.csproj" -c Release -r "$rid" --self-contained \
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
        -o "$here/dist/$rid" >/dev/null
    f=$(ls "$here/dist/$rid"/membench* | head -1)
    printf "  %-12s %s (%s)\n" "$rid" "${f#$here/}" "$(du -h "$f" | cut -f1)"
done
