#!/usr/bin/env bash
# Builds the linux-x64 self-contained single-file release and the tarball in dist/ (box or any Linux with the .NET 10 SDK).
# Copyright (C) 2026 JeepNWilly. Free software under the GNU GPL v3 or later; no warranty.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
if [ -z "${CONVERTR_CORE_DIR:-}" ]; then
  for c in external/Convertr/src/Convertr.Core ../Convertr/source/src/Convertr.Core ../Convertr/src/Convertr.Core; do
    if [ -f "$c/Convertr.Core.csproj" ]; then CONVERTR_CORE_DIR="$(cd "$c" && pwd)"; break; fi
  done
fi
export CONVERTR_CORE_DIR="${CONVERTR_CORE_DIR:-$PWD/external/Convertr/src/Convertr.Core}"
VER="$(tr -d '[:space:]' < VERSION)"
OUT="dist/publish"; STAGE="dist/convertr-svc-$VER-linux-x64"
rm -rf "$OUT" "$STAGE"
dotnet publish src/Convertr.Svc -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "$OUT" -nologo -v q
mkdir -p "$STAGE/scripts" "$STAGE/deploy" "$STAGE/docs"
cp "$OUT/convertr-svc" "$STAGE/convertr-svc"
cp scripts/install.sh scripts/uninstall.sh "$STAGE/scripts/"
cp deploy/* "$STAGE/deploy/"
mkdir -p "$STAGE/tools"; cp tools/*.sh "$STAGE/tools/" 2>/dev/null || true
cp README.md CHANGELOG.md "$STAGE/" 2>/dev/null || true
cp docs/*.md "$STAGE/docs/"
[ -f LICENSE ] && cp LICENSE "$STAGE/" || true
chmod +x "$STAGE/convertr-svc" "$STAGE/scripts/"*.sh "$STAGE/tools/"*.sh 2>/dev/null || true
tar -C dist --owner=0 --group=0 -czf "dist/convertr-svc-$VER-linux-x64.tar.gz" "convertr-svc-$VER-linux-x64"
( cd dist && sha256sum "convertr-svc-$VER-linux-x64.tar.gz" | tee "convertr-svc-$VER-linux-x64.tar.gz.sha256" )
ls -l "dist/convertr-svc-$VER-linux-x64.tar.gz"
