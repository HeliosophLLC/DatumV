#!/usr/bin/env bash
# Temporary helper to build the Linux AppImage from WSL using the mounted
# Windows checkout. Stashes Windows node_modules, installs Linux deps,
# runs dist:linux:cuda, restores Windows node_modules on exit.
set -euo pipefail

REPO=/mnt/c/Users/Albert/source/repos/DatumIngest
WEB="$REPO/src/Heliosoph.DatumV.Web"
ELECTRON="$WEB/electron"
CLIENTAPP="$WEB/ClientApp"

stash() {
  local d="$1"
  if [ -d "$d/node_modules" ] && [ ! -e "$d/node_modules.win" ]; then
    echo ">> stashing $d/node_modules -> node_modules.win"
    mv "$d/node_modules" "$d/node_modules.win"
  fi
}

restore() {
  local d="$1"
  if [ -e "$d/node_modules.win" ]; then
    echo ">> restoring $d/node_modules.win -> node_modules"
    rm -rf "$d/node_modules"
    mv "$d/node_modules.win" "$d/node_modules"
  fi
}

cleanup() {
  local rc=$?
  echo ""
  echo "=== cleanup (exit $rc): restoring Windows node_modules ==="
  restore "$ELECTRON"
  restore "$CLIENTAPP"
  exit $rc
}
trap cleanup EXIT INT TERM

echo "=== stash Windows node_modules ==="
stash "$ELECTRON"
stash "$CLIENTAPP"

echo "=== npm ci in ClientApp ==="
cd "$CLIENTAPP"
npm ci

echo "=== npm ci in electron ==="
cd "$ELECTRON"
npm ci

echo "=== npm run dist:linux:cuda ==="
npm run dist:linux:cuda

echo "=== build complete; out/ contents ==="
ls -la "$ELECTRON/out" || true
