#!/usr/bin/env bash
# Regenerates every raster brand asset from the SVG sources.
# SVGs are the single source of truth; everything under brand/export/ is derived.
# Requires: Inkscape (SVG rasteriser) and ImageMagick (ICO packer).
set -euo pipefail
cd "$(dirname "$0")"

INKSCAPE="${INKSCAPE:-/c/Program Files/Inkscape/bin/inkscape}"
MAGICK="${MAGICK:-magick}"
OUT=export
mkdir -p "$OUT"

render () { # svg out w h
  "$INKSCAPE" "$1" --export-type=png --export-filename="$2" -w "$3" -h "$4" >/dev/null 2>&1
}

# App icon sizes. Below 48px the simplified two-arc mark reads far better.
for s in 16 24 32; do render logo-mark-small.svg "$OUT/icon-$s.png" $s $s; done
for s in 48 64 128 256 512 1024; do render logo-mark.svg "$OUT/icon-$s.png" $s $s; done

# Windows .ico — this one IS tracked, the app links against it.
"$MAGICK" "$OUT/icon-16.png" "$OUT/icon-24.png" "$OUT/icon-32.png" \
          "$OUT/icon-48.png" "$OUT/icon-64.png" "$OUT/icon-128.png" \
          "$OUT/icon-256.png" app.ico

# Lockups for the site / README / about box.
render logo-horizontal.svg      "$OUT/logo-horizontal.png"      760 180
render logo-horizontal-dark.svg "$OUT/logo-horizontal-dark.png" 760 180
render logo-horizontal.svg      "$OUT/logo-horizontal@2x.png"  1520 360

echo "brand assets rebuilt -> $OUT/ and app.ico"
