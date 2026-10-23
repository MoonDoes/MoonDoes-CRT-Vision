#!/usr/bin/env bash
# Builds libcrtvision_gl.so and assembles the ready-to-install mod folder in
# dist/crtvision (copy it into the game's mods/ folder).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CARGO="${CARGO:-$HOME/.cargo/bin/cargo}"
[ -x "$CARGO" ] || CARGO=cargo
OUT="$ROOT/dist/crtvision"

echo "==> Building libcrtvision_gl.so (librashader OpenGL runtime)"
(cd "$ROOT/native/crtvision-gl" && "$CARGO" build --release)

echo "==> Assembling $OUT"
rm -rf "$OUT"
mkdir -p "$OUT/native" "$OUT/presets" "$OUT/LICENSES"
cp "$ROOT/mod/mod.json" "$ROOT/mod/crtvision.json" "$OUT/"
cp -a "$ROOT/mod/source" "$OUT/"
cp "$ROOT/native/crtvision-gl/target/release/libcrtvision_gl.so" "$OUT/native/"
strip --strip-unneeded "$OUT/native/libcrtvision_gl.so" 2>/dev/null || true
cp -a "$ROOT/presets/." "$OUT/presets/"
cp "$ROOT/LICENSE" "$OUT/LICENSES/GPL-3.0.txt"
cp "$ROOT/LICENSES/MPL-2.0.txt" "$OUT/LICENSES/MPL-2.0-librashader.txt"
cp "$ROOT/CREDITS.md" "$OUT/LICENSES/CREDITS.md"
echo "==> Ready: $OUT"
