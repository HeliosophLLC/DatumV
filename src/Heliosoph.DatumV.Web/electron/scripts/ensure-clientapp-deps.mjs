#!/usr/bin/env node
// Pre-launch guard for the dev loop (`electron:dev`).
//
// electron:dev starts the Vite dev server and Electron *concurrently*, and
// both assume ClientApp/node_modules is already populated — the Vite branch
// invokes the `vite` bin directly. A missing or half-written tree (a fresh
// clone, an interrupted install, or an npm op that ran while files were
// locked) makes `vite` unresolvable and the whole launch dies with
// "'vite' is not recognized".
//
// The dev backend no longer installs ClientApp deps (it runs with
// SkipClientApp=true so its `npm ci` can't wipe node_modules out from under
// the running Vite server), so nothing else heals a broken tree. This script
// fills that gap. It runs SERIALLY, before the concurrent phase, so it's safe
// to `npm ci` (which wipes + reinstalls) here — Vite isn't running yet.
//
// Reinstall logic mirrors the csproj ClientAppInstall target's Inputs/Outputs
// (package.json / package-lock.json vs node_modules/.install-stamp), and it
// shares the same stamp file so a subsequent `dotnet build` sees the install
// as current and doesn't redo it.
import { existsSync, statSync, openSync, closeSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const clientApp = path.resolve(__dirname, '..', '..', 'ClientApp');
const nodeModules = path.join(clientApp, 'node_modules');
// The extensionless `vite` shim is created on every platform (alongside
// vite.cmd / vite.ps1 on Windows), so its presence is a good proxy for
// "node_modules is installed and its bins are linked".
const viteBin = path.join(nodeModules, '.bin', 'vite');
const stamp = path.join(nodeModules, '.install-stamp');
const lock = path.join(clientApp, 'package-lock.json');
const pkg = path.join(clientApp, 'package.json');

function reasonToInstall() {
  if (!existsSync(viteBin)) return 'node_modules missing or not bin-linked';
  if (!existsSync(stamp)) return 'install stamp missing';
  const stampMtime = statSync(stamp).mtimeMs;
  if (existsSync(lock) && statSync(lock).mtimeMs > stampMtime) return 'package-lock.json changed since last install';
  if (existsSync(pkg) && statSync(pkg).mtimeMs > stampMtime) return 'package.json changed since last install';
  return null;
}

const reason = reasonToInstall();
if (!reason) {
  console.log('>> ClientApp deps up to date; skipping install');
  process.exit(0);
}

console.log(`>> Installing ClientApp deps (${reason})`);
// npm ci installs strictly from the lock and never mutates it — keeping the
// committed lock stable across platforms (Windows `npm install` would prune
// the Linux-only @emnapi wasm-fallback nodes and break the Linux release CI).
//
// shell: true is required: on Windows `npm` is npm.cmd, and modern Node
// refuses to spawn .cmd/.bat directly without a shell (CVE-2024-27980). Pass
// the whole command as one string (not command + args array) so Node doesn't
// emit DEP0190 — the deprecation only fires for an args array under shell.
// The command is a static literal, so there's no injection surface.
const result = spawnSync('npm ci --no-audit --no-fund', {
  cwd: clientApp,
  stdio: 'inherit',
  shell: true,
});
if (result.status !== 0) {
  console.error('>> ClientApp `npm ci` failed — is package-lock.json in sync with package.json?');
  process.exit(result.status ?? 1);
}
// Refresh the shared stamp (npm ci wiped the old one along with node_modules).
closeSync(openSync(stamp, 'w'));
console.log('>> ClientApp deps installed');
