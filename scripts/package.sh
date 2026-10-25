#!/usr/bin/env bash
# Builds the mod and packs it as dist/MoonDoes-CRT-Vision-v<version>-linux-x86_64.zip,
# with the mod folder and the end-user README inside.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
"$ROOT/scripts/build.sh"
VERSION="$(python3 -c "import json; print(json.load(open('$ROOT/mod/mod.json'))['version'])")"
STAGE="$ROOT/dist/MoonDoes-CRT-Vision"
ZIP="$ROOT/dist/MoonDoes-CRT-Vision-v$VERSION-linux-x86_64.zip"

rm -rf "$STAGE" "$ZIP"
mkdir -p "$STAGE"
cp -a "$ROOT/dist/crtvision" "$STAGE/"
cp "$ROOT/docs/INSTALL.md" "$STAGE/README.md"
cp "$ROOT/docs/LIGHT-SOURCES.md" "$STAGE/LIGHT-SOURCES.md"
(cd "$ROOT/dist" && python3 -m zipfile -c "$ZIP" MoonDoes-CRT-Vision)
echo "==> Package: $ZIP"
