#!/usr/bin/env node
// Stages Linux-only native libraries into the dotnet publish output so the
// Linux AppImage carries everything it needs to dlopen at first call.
//
// Why: Sdcb.FFmpeg publishes a Windows native NuGet (.runtime.windows-x64)
// but no Linux equivalent — the upstream guidance is "apt install ffmpeg"
// on the host, which fails for AppImage users on distros without FFmpeg 7
// (libavutil.so.59) in their repos. Bundling the BtbN/FFmpeg-Builds shared
// build inside the AppImage is the portable answer.
//
// Layout: build/backend/runtimes/linux-x64/native/lib*.so* — matches the
// NuGet RID convention the .NET native loader probes first, ahead of the
// app base directory and LD_LIBRARY_PATH.
//
// Version pin: n7.1 branch on the rolling `latest` tag. Tracks 7.1.x
// patches without flipping ABI versions. Sdcb.FFmpeg.runtime.windows-x64
// is pinned at 7.1.0 — keep these aligned to avoid Win/Linux ABI skew.
// Bump both together when Sdcb ships a release targeting FFmpeg 8.x.
//
// Skips on non-Linux hosts: the dist:linux:* chain is only valid on Linux
// (or WSL) anyway because electron-builder needs Linux tools for AppImage,
// but the guard makes the script safe to call from anywhere.
import { createWriteStream, mkdirSync } from 'node:fs';
import { rm, mkdtemp, readdir, copyFile } from 'node:fs/promises';
import { tmpdir, platform } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { pipeline } from 'node:stream/promises';
import { execFileSync } from 'node:child_process';

const URL = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n7.1-latest-linux64-lgpl-shared-7.1.tar.xz';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const NATIVE_DIR = path.resolve(__dirname, '..', 'build', 'backend', 'runtimes', 'linux-x64', 'native');

async function main() {
  if (platform() !== 'linux') {
    console.log(`>> skipping stage-linux-natives on ${platform()}`);
    return;
  }

  mkdirSync(NATIVE_DIR, { recursive: true });

  const work = await mkdtemp(path.join(tmpdir(), 'ffmpeg-stage-'));
  const tarPath = path.join(work, 'ffmpeg.tar.xz');

  try {
    console.log(`>> downloading ${URL}`);
    const res = await fetch(URL);
    if (!res.ok) throw new Error(`HTTP ${res.status} fetching ffmpeg tarball`);
    await pipeline(res.body, createWriteStream(tarPath));

    console.log(`>> extracting to ${work}`);
    execFileSync('tar', ['-xJf', tarPath, '-C', work, '--strip-components=1'], { stdio: 'inherit' });

    const libDir = path.join(work, 'lib');
    const entries = (await readdir(libDir)).filter(n => n.startsWith('lib') && n.includes('.so'));
    if (entries.length === 0) throw new Error(`no .so files in ${libDir} — tarball layout changed?`);

    console.log(`>> copying ${entries.length} libs to ${NATIVE_DIR}`);
    for (const name of entries) {
      await copyFile(path.join(libDir, name), path.join(NATIVE_DIR, name));
    }
    console.log('>> staged');
  } finally {
    await rm(work, { recursive: true, force: true });
  }
}

main().catch(err => { console.error(err); process.exit(1); });
