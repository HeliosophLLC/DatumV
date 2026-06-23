#!/usr/bin/env bash
# Builds the Linux AppImage from WSL using the mounted Windows checkout.
#
# Two cross-OS hazards are managed by stash/restore:
#   1. node_modules (in electron/ and ClientApp/) — Linux/Windows native
#      binaries differ, so each OS needs its own install.
#   2. NuGet packages.lock.json files — the Windows .NET SDK and Linux
#      .NET SDK ship Microsoft.NET.ILLink.Tasks at the same version but
#      with different on-disk hashes (different SDK packagings). With
#      `RestorePackagesWithLockFile=true` enabled, a Linux restore using
#      a Windows-generated lockfile fails with NU1403. We back up the
#      Windows lockfiles, force-evaluate to rewrite them with Linux
#      hashes, run the build, and restore the originals on exit.
#
# Backups use a `.win` suffix and are gitignored. Cleanup runs on EXIT
# (success or failure) so an aborted build still leaves the source tree
# in its Windows-ready state.
set -euo pipefail

REPO=/mnt/c/Users/Albert/source/repos/DatumIngest
WEB="$REPO/src/Heliosoph.DatumV.Web"
ELECTRON="$WEB/electron"
CLIENTAPP="$WEB/ClientApp"

# Every project that has a NuGet lockfile contributing to the Web project's
# transitive graph. Restoring the Web csproj with --force-evaluate will
# rewrite all four.
NUGET_LOCKFILES=(
  "$REPO/src/Heliosoph.DatumV/packages.lock.json"
  "$REPO/src/Heliosoph.DatumV.LanguageServer/packages.lock.json"
  "$REPO/src/Heliosoph.DatumV.Parsing/packages.lock.json"
  "$REPO/src/Heliosoph.DatumV.Web/packages.lock.json"
)
# npm lockfile that gets rewritten when `npm ci` runs on Linux (rollup's
# OS-specific optional native packages differ from the Windows install).
NPM_LOCKFILE="$CLIENTAPP/package-lock.json"

stash_dir() {
  local d="$1"
  if [ -d "$d/node_modules" ] && [ ! -e "$d/node_modules.win" ]; then
    echo ">> stashing $d/node_modules -> node_modules.win"
    mv "$d/node_modules" "$d/node_modules.win"
  fi
}

restore_dir() {
  local d="$1"
  if [ -e "$d/node_modules.win" ]; then
    echo ">> restoring $d/node_modules.win -> node_modules"
    rm -rf "$d/node_modules"
    mv "$d/node_modules.win" "$d/node_modules"
  fi
}

# Lockfiles are small text files; we `cp` to a .win backup (preserving the
# Windows-original in place is fine — restore overwrites the live file on
# exit). The `[ ! -e .win ]` guard means re-running this after a crashed
# previous build still uses the genuine Windows original, not the Linux
# version left behind.
stash_lockfile() {
  local f="$1"
  if [ -f "$f" ] && [ ! -e "$f.win" ]; then
    echo ">> stashing $(basename "$f") -> $(basename "$f").win"
    cp "$f" "$f.win"
  fi
}

restore_lockfile() {
  local f="$1"
  if [ -e "$f.win" ]; then
    echo ">> restoring $(basename "$f").win -> $(basename "$f")"
    mv "$f.win" "$f"
  fi
}

cleanup() {
  local rc=$?
  echo ""
  echo "=== cleanup (exit $rc): restoring Windows node_modules + lockfiles ==="
  restore_dir "$ELECTRON"
  restore_dir "$CLIENTAPP"
  for f in "${NUGET_LOCKFILES[@]}" "$NPM_LOCKFILE"; do
    restore_lockfile "$f"
  done
  exit $rc
}
trap cleanup EXIT INT TERM

echo "=== stash Windows node_modules + lockfiles ==="
stash_dir "$ELECTRON"
stash_dir "$CLIENTAPP"
for f in "${NUGET_LOCKFILES[@]}" "$NPM_LOCKFILE"; do
  stash_lockfile "$f"
done

# One-time: stage-cuda-libs.sh needs pip3 to fetch NVIDIA's redistributable
# wheels. apt only runs the install once; subsequent builds short-circuit.
if ! command -v pip3 >/dev/null 2>&1; then
  echo "=== installing python3-pip (one-time, required by stage-cuda-libs.sh) ==="
  DEBIAN_FRONTEND=noninteractive apt-get install -y python3-pip
fi

# Rewrite NuGet lockfiles with Linux-side package hashes. Without this,
# generate:notices' dotnet restore (and the build's dotnet publish) fails
# NU1403 hash validation. --force-evaluate keeps the version pins, just
# refreshes the recorded hashes from what was actually restored.
echo "=== dotnet restore --force-evaluate (Linux NuGet hashes) ==="
dotnet restore "$WEB/Heliosoph.DatumV.Web.csproj" -p:GpuVariant=cuda --force-evaluate

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
