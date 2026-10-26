#!/bin/bash
# Copyright (C) 2026 JeepNWilly
# Convertr_svc: runs (1) the shared Convertr.Core test suite from the sibling Convertr repository and (2) the Convertr_svc tests.
# The shared engine is located like the build does it: CONVERTR_CORE_DIR, otherwise the external/Convertr submodule, otherwise a sibling ../Convertr checkout.
set -e
cd "$(dirname "$0")/.."
if [ -z "${CONVERTR_CORE_DIR:-}" ]; then
  for c in external/Convertr/src/Convertr.Core ../Convertr/source/src/Convertr.Core ../Convertr/src/Convertr.Core; do
    if [ -f "$c/Convertr.Core.csproj" ]; then CONVERTR_CORE_DIR="$(cd "$c" && pwd)"; break; fi
  done
fi
CORE="${CONVERTR_CORE_DIR:-external/Convertr/src/Convertr.Core}"
if [ ! -f "$CORE/Convertr.Core.csproj" ]; then
  echo "ERROR: Convertr.Core not found at '$CORE'. Set CONVERTR_CORE_DIR to the folder that contains Convertr.Core.csproj." >&2
  exit 2
fi
export CONVERTR_CORE_DIR="$CORE"
echo "shared Core: $CORE"
dotnet test "$CORE/../../tests/Convertr.Tests" -c Release --nologo -v q
dotnet test tests/Convertr.Svc.Tests -c Release --nologo -v q
