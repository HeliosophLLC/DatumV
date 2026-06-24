#!/usr/bin/env bash
# Compute SHA-256s of both platform CUDA bundles, write a fresh
# manifest.json, and upload it to R2 via rclone (remote name `r2`,
# bucket `cuda-binaries`).
#
# Usage:
#   bash publish-cuda-manifest.sh [version]
#
# Run after build-cuda-bundle.sh + build-cuda-bundle.ps1 have produced
# both .tar.zst files under electron/build/cuda-bundle/. Defaults to
# 1.0.0; pass a different version to publish a new bundle iteration.
#
# Prereqs:
#   - rclone configured with an `r2` remote (see ../../../../README or
#     the conversation history).
#   - The bundle URLs match cuda-cdn.heliosoph.com (CDN_BASE below). If
#     you re-point the custom domain, update CDN_BASE here.
set -euo pipefail

# Optional `--no-upload` to skip rclone and just write the manifest.
# Useful when you'd rather upload from a different machine / shell.
UPLOAD=1
ARGS=()
for arg in "$@"; do
  if [ "$arg" = "--no-upload" ]; then
    UPLOAD=0
  else
    ARGS+=("$arg")
  fi
done
set -- "${ARGS[@]}"

VERSION="${1:-1.0.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUNDLE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)/build/cuda-bundle"
BUCKET="cuda-binaries"
CDN_BASE="https://cuda-cdn.heliosoph.com"

LINUX_BUNDLE="$BUNDLE_DIR/cuda-runtime-linux-x64-v$VERSION.tar.zst"
WIN_BUNDLE="$BUNDLE_DIR/cuda-runtime-win-x64-v$VERSION.tar.zst"

for f in "$LINUX_BUNDLE" "$WIN_BUNDLE"; do
  if [ ! -f "$f" ]; then
    echo "[manifest] ERROR: missing $f" >&2
    echo "[manifest] Run build-cuda-bundle.{sh,ps1} for both platforms first." >&2
    exit 1
  fi
done
required=(sha256sum stat jq)
if [ "$UPLOAD" = 1 ]; then required+=(rclone); fi
for tool in "${required[@]}"; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "[manifest] ERROR: $tool not on PATH." >&2
    if [ "$tool" = "rclone" ]; then
      echo "[manifest] Either install rclone in WSL (apt-get install -y rclone, then copy" >&2
      echo "[manifest] /mnt/c/Users/<you>/AppData/Roaming/rclone/rclone.conf to ~/.config/rclone/)" >&2
      echo "[manifest] OR re-run with --no-upload and upload manifest.json from Windows yourself." >&2
    fi
    exit 1
  fi
done

linux_sha=$(sha256sum "$LINUX_BUNDLE" | awk '{print $1}')
linux_size=$(stat -c '%s' "$LINUX_BUNDLE")
win_sha=$(sha256sum "$WIN_BUNDLE" | awk '{print $1}')
win_size=$(stat -c '%s' "$WIN_BUNDLE")

# extracted_size_bytes is informational (used by the Settings UI as a
# disk-usage hint). We don't have a recorded value from the build —
# approximate as size * 1.5 (zstd -19 on CUDA libs typically achieves
# 0.55-0.65 ratio).
linux_extracted=$((linux_size * 3 / 2))
win_extracted=$((win_size * 3 / 2))

MANIFEST="$BUNDLE_DIR/manifest.json"
jq -n \
  --arg version "$VERSION" \
  --arg linux_url "$CDN_BASE/cuda-runtime-linux-x64-v$VERSION.tar.zst" \
  --arg linux_sha "$linux_sha" \
  --argjson linux_size "$linux_size" \
  --argjson linux_extracted "$linux_extracted" \
  --arg win_url "$CDN_BASE/cuda-runtime-win-x64-v$VERSION.tar.zst" \
  --arg win_sha "$win_sha" \
  --argjson win_size "$win_size" \
  --argjson win_extracted "$win_extracted" \
  '{
    version: $version,
    platforms: {
      "linux-x64": {
        url: $linux_url,
        sha256: $linux_sha,
        size_bytes: $linux_size,
        extracted_size_bytes: $linux_extracted
      },
      "win-x64": {
        url: $win_url,
        sha256: $win_sha,
        size_bytes: $win_size,
        extracted_size_bytes: $win_extracted
      }
    }
  }' > "$MANIFEST"

echo "[manifest] wrote $MANIFEST"
cat "$MANIFEST"
echo

if [ "$UPLOAD" = 1 ]; then
  echo "[manifest] uploading manifest.json to r2:$BUCKET"
  rclone copy --progress --s3-no-check-bucket "$MANIFEST" "r2:$BUCKET/"
  echo "[manifest] done. Verify at $CDN_BASE/manifest.json"
else
  echo "[manifest] --no-upload: skipping rclone. Upload yourself with e.g.:"
  echo "  rclone copy --progress --s3-no-check-bucket \"$MANIFEST\" r2:$BUCKET/"
fi
