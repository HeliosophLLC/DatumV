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
// Each entry is a path relative to build/backend/. The .NET native loader
// searches runtimes/<rid>/native/ first, then the app base directory, so
// either location is valid; mirror whichever the upstream NuGet uses.
//
// Skips on non-Linux hosts: the dist:linux:* chain is only meaningful on
// Linux/WSL, and the publish output for Windows won't contain these libs.
import { existsSync } from 'node:fs';
import { platform } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..', 'build', 'backend');

const REQUIRED = [
  // FFmpeg 7.1 ABI — staged by stage-linux-natives.mjs from BtbN.
  'runtimes/linux-x64/native/libavutil.so.59',
  'runtimes/linux-x64/native/libavcodec.so.61',
  'runtimes/linux-x64/native/libavformat.so.61',
  'runtimes/linux-x64/native/libswresample.so.5',
  // ONNX Runtime CPU — comes from Microsoft.ML.OnnxRuntime's RID gate.
  'runtimes/linux-x64/native/libonnxruntime.so',
  // SkiaSharp — comes from SkiaSharp.NativeAssets.Linux.NoDependencies.
  'runtimes/linux-x64/native/libSkiaSharp.so',
  // OpenCvSharp — comes from OpenCvSharp4.official.runtime.linux-x64.slim.
  'runtimes/linux-x64/native/libOpenCvSharpExtern.so',
];

if (platform() !== 'linux') {
  console.log(`>> skipping verify-linux-natives on ${platform()}`);
  process.exit(0);
}

const missing = REQUIRED.filter(p => !existsSync(path.resolve(ROOT, p)));
if (missing.length) {
  console.error('Linux publish output is missing native libs:');
  for (const m of missing) console.error(`  - ${m}`);
  console.error('\nFix: re-run stage-linux-natives.mjs or check the engine csproj RID gates.');
  process.exit(1);
}
console.log(`>> verified ${REQUIRED.length} native libs present in build/backend`);
