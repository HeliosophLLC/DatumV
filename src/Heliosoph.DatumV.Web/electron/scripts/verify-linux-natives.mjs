#!/usr/bin/env node
// Post-publish guard for the Linux AppImage. Asserts that every native
// library the backend dlopens at runtime is present in build/backend/
// before electron-builder seals it into the squashfs.
//
// Catches three classes of regression that release-gate tests don't:
//   1. Path drift in stage-linux-natives.mjs (lands in wrong subdir).
//   2. Engine csproj RID-gated package refs silently removed/renamed.
//   3. Filter rules in electron-builder.yml accidentally excluding .so.
//
// Extend REQUIRED whenever the engine takes on a new native dependency.
// Each entry is a bare library filename. The .NET native loader resolves a
// dlopen from two places, and different upstream packages land in different
// ones, so we accept EITHER:
//   - the app base directory (build/backend/) — where a self-contained,
//     single-file RID publish FLATTENS a package's single native .so
//     (Microsoft.ML.OnnxRuntime, SkiaSharp, OpenCvSharp all land here);
//   - runtimes/linux-x64/native/ — where packages that ship a per-RID
//     native tree keep their libs, and where stage-linux-natives.mjs drops
//     the FFmpeg / libtiff5 blobs it fetches out-of-band.
// Checking both mirrors the loader's own search order, so the guard tracks
// what actually loads at runtime rather than a presumed layout.
//
// Skips on non-Linux hosts: the dist:linux:* chain is only meaningful on
// Linux/WSL, and the publish output for Windows won't contain these libs.
import { existsSync } from 'node:fs';
import { platform } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..', 'build', 'backend');

// Directories the .NET loader probes, relative to build/backend/. A required
// lib satisfies the check if it exists in any of them.
const SEARCH_DIRS = ['.', 'runtimes/linux-x64/native'];

const REQUIRED = [
  // FFmpeg 7.1 ABI — staged by stage-linux-natives.mjs from BtbN into
  // runtimes/linux-x64/native/.
  'libavutil.so.59',
  'libavcodec.so.61',
  'libavformat.so.61',
  'libswresample.so.5',
  // ONNX Runtime CPU — Microsoft.ML.OnnxRuntime; flattened to the base dir.
  'libonnxruntime.so',
  // SkiaSharp — SkiaSharp.NativeAssets.Linux.NoDependencies; base dir.
  'libSkiaSharp.so',
  // OpenCvSharp — OpenCvSharp4.official.runtime.linux-x64.slim; base dir.
  'libOpenCvSharpExtern.so',
  // libtiff5 — transitive dlopen of libOpenCvSharpExtern.so. Staged from the
  // jammy security .deb (Ubuntu 24.04 only ships libtiff6) into runtimes/.
  'libtiff.so.5',
];

if (platform() !== 'linux') {
  console.log(`>> skipping verify-linux-natives on ${platform()}`);
  process.exit(0);
}

const missing = REQUIRED.filter(
  name => !SEARCH_DIRS.some(dir => existsSync(path.resolve(ROOT, dir, name))),
);
if (missing.length) {
  console.error('Linux publish output is missing native libs:');
  for (const m of missing) console.error(`  - ${m} (searched: ${SEARCH_DIRS.join(', ')})`);
  console.error('\nFix: re-run stage-linux-natives.mjs or check the engine csproj RID gates.');
  process.exit(1);
}
console.log(`>> verified ${REQUIRED.length} native libs present in build/backend`);
