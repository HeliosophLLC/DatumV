#!/usr/bin/env bash
# Builds a CUDA runtime bundle (.tar.zst) for upload to R2 / external CDN,
# consumed by the in-app GPU support installer at first launch.
#
# Usage: bash build-cuda-bundle.sh [version]
#   version defaults to 1.0.0; bumps go in the bundle filename so the
#   app can pin to a specific version via its embedded manifest.
#
# Output: ../build/cuda-bundle/cuda-runtime-linux-x64-v<version>.tar.zst
#         (prints sha256 + size for the manifest entry)
set -euo pipefail

VERSION="${1:-1.0.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REQS="$SCRIPT_DIR/cuda-bundle/requirements.txt"
OUT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)/build/cuda-bundle"
BUNDLE="$OUT_DIR/cuda-runtime-linux-x64-v$VERSION.tar.zst"

for tool in pip3 zstd tar sha256sum numfmt; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "[bundle] ERROR: $tool not on PATH." >&2
    if [ "$tool" = "zstd" ]; then
      echo "[bundle] Install via: apt-get install zstd" >&2
    elif [ "$tool" = "pip3" ]; then
      echo "[bundle] Install via: apt-get install python3-pip" >&2
    fi
    exit 1
  fi
done

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "[bundle] pip install --target=$STAGE -r $REQS"
pip3 install --quiet --target="$STAGE" -r "$REQS"

# Flatten .so files into a dedicated lib/ — the tarball's top-level layout
# is what the in-app installer extracts into the runtime cache dir.
mkdir -p "$STAGE/lib"
echo "[bundle] flattening .so files into staging dir"
find "$STAGE/nvidia" -path '*/lib/*' -name '*.so*' -exec cp -Pf {} "$STAGE/lib/" \;

# Same trim list as stage-cuda-libs.sh — keep them in sync.
echo "[bundle] trimming unused libraries"
shopt -s nullglob
for pat in libcusolverMg.so* libnvrtc.alt.so* libnvrtc-builtins.alt.so*; do
  for f in "$STAGE/lib"/$pat; do
    echo "  - $(basename "$f")"
    rm -f "$f"
  done
done
shopt -u nullglob

mkdir -p "$OUT_DIR"
echo "[bundle] tar + zstd -19 (high compression, runs ~1-2 min)"
# -C "$STAGE/lib" + "." → tar entries are paths-relative-to-lib, no leading
# directory component. Extracting into <cache>/cuda-runtime-vX/ drops the
# .so files straight into that dir, ready for LD_LIBRARY_PATH.
tar -C "$STAGE/lib" -cf - . | zstd -T0 -19 -o "$BUNDLE" 2>&1 | tail -3

SHA=$(sha256sum "$BUNDLE" | awk '{print $1}')
SIZE=$(stat -c '%s' "$BUNDLE")
EXTRACTED=$(du -sb "$STAGE/lib" | awk '{print $1}')

echo ""
echo "=== bundle ready ==="
echo "  path:           $BUNDLE"
echo "  size:           $(numfmt --to=iec "$SIZE")  ($SIZE bytes)"
echo "  extracted:      $(numfmt --to=iec "$EXTRACTED")"
echo "  sha256:         $SHA"
echo ""
echo "=== manifest entry ==="
cat <<EOF
"linux-x64": {
  "url": "https://<your-r2-domain>/cuda-runtime-linux-x64-v$VERSION.tar.zst",
  "sha256": "$SHA",
  "size_bytes": $SIZE,
  "extracted_size_bytes": $EXTRACTED
}
EOF
echo ""
echo "=== upload to R2 ==="
echo "  wrangler r2 object put <bucket>/cuda-runtime-linux-x64-v$VERSION.tar.zst --file=\"$BUNDLE\""
