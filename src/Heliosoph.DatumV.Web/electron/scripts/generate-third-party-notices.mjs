#!/usr/bin/env node
// Generates THIRD-PARTY-NOTICES.txt at the repo root, aggregating
// license/copyright information for every dependency that ships in a
// DatumV installer.
//
// Coverage:
//   1. .NET deps from the Web project (transitive) — captured via
//      `dotnet-project-licenses` (added in dotnet-tools.json).
//   2. npm deps from `electron/` (production-only, the shell ships
//      these inside the asar bundle).
//   3. npm deps from `ClientApp/` (production-only, used by Vite to
//      build wwwroot/).
//   4. Special manual sections: the Llama Community License text,
//      sourced from licenses/llama-3.1-community.md (required by
//      Meta's terms whenever the app facilitates Llama use), and the
//      Stability AI Community License text, sourced from
//      licenses/stability-ai-community.md (required by Stability's
//      §4.a whenever the app makes SD Turbo / SDXL Turbo available,
//      including the verbatim "Notice" attribution line).
//
// Output is plain text, deduplicated, sorted alphabetically within
// each section. Run via `npm run generate:notices` from
// `electron/`. CI may invoke the same script.

import { execSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ELECTRON_DIR = path.resolve(__dirname, '..');
const REPO_ROOT = path.resolve(ELECTRON_DIR, '..', '..', '..');
const CLIENT_APP_DIR = path.resolve(ELECTRON_DIR, '..', 'ClientApp');
const WEB_CSPROJ = path.resolve(ELECTRON_DIR, '..', 'Heliosoph.DatumV.Web.csproj');
const OUTPUT = path.join(REPO_ROOT, 'THIRD-PARTY-NOTICES.txt');
const LLAMA_LICENSE_FILE = path.join(REPO_ROOT, 'licenses', 'llama-3.1-community.md');
const STABILITY_LICENSE_FILE = path.join(REPO_ROOT, 'licenses', 'stability-ai-community.md');

function log(msg) {
  process.stdout.write(`[notices] ${msg}\n`);
}

function header(title) {
  const bar = '='.repeat(76);
  return `\n${bar}\n${title}\n${bar}\n\n`;
}

function subheader(title) {
  return `\n${'-'.repeat(60)}\n${title}\n${'-'.repeat(60)}\n\n`;
}

// ───────────────────────── .NET deps ─────────────────────────

// Hardcoded license fallback for NuGet packages whose nuspec ships
// the license as a `File` reference rather than an SPDX expression
// (dotnet-project-licenses 3.0-alpha can't follow these). All values
// verified manually against the upstream repo. Update as deps shift.
const DOTNET_LICENSE_OVERRIDES = new Map([
  ['Microsoft.ML.OnnxRuntime.Gpu', 'MIT'],
  ['Microsoft.ML.OnnxRuntime.Gpu.Linux', 'MIT'],
  ['Microsoft.ML.OnnxRuntime.Gpu.Windows', 'MIT'],
  ['Microsoft.ML.OnnxRuntime.Managed', 'MIT'],
  ['Microsoft.ML.OnnxRuntime', 'MIT'],
  ['Microsoft.ML.OnnxRuntime.DirectML', 'MIT'],
  // K4os.Compression.LZ4 nuspec uses a raw GitHub URL — it's MIT.
  ['K4os.Compression.LZ4', 'MIT'],
  ['K4os.Compression.LZ4.Streams', 'MIT'],
  ['K4os.Hash.xxHash', 'MIT'],
  // murmurhash nuspec ships a raw URL; license is MS-PL.
  ['murmurhash', 'MS-PL'],
]);

function collectDotnetLicenses() {
  log('Restoring dotnet tools...');
  execSync('dotnet tool restore', { cwd: REPO_ROOT, stdio: 'inherit' });

  // The Web project's dependency graph differs between GpuVariants
  // (cuda pulls ORT.Gpu + LLamaSharp.Backend.Cuda12.Windows; standard
  // pulls ORT.DirectML + LLamaSharp.Backend.Vulkan + Backend.Cpu).
  // dotnet-project-licenses walks whatever the *last restore* materialised
  // into project.assets.json, so we restore each variant in turn, collect
  // its deps, then merge — tagging variant-exclusive entries so the
  // notices file accurately reflects both installers.
  const cudaEntries = collectForVariant('cuda');
  log(`  cuda variant: ${cudaEntries.length} packages`);

  const standardEntries = collectForVariant('standard');
  log(`  standard variant: ${standardEntries.length} packages`);

  const cudaKeys = new Set(cudaEntries.map((e) => `${e.name}@${e.version}`));
  const standardKeys = new Set(standardEntries.map((e) => `${e.name}@${e.version}`));

  const merged = [];
  for (const e of cudaEntries) {
    const key = `${e.name}@${e.version}`;
    if (standardKeys.has(key)) {
      merged.push(e); // shared between both variants
    } else {
      merged.push({ ...e, variantTag: 'cuda' });
    }
  }
  for (const e of standardEntries) {
    const key = `${e.name}@${e.version}`;
    if (!cudaKeys.has(key)) {
      merged.push({ ...e, variantTag: 'standard' });
    }
  }
  return dedupeSorted(merged);
}

function collectForVariant(variant) {
  log(`Restoring Web project for variant=${variant}...`);
  execSync(
    `dotnet restore "${WEB_CSPROJ}" -p:GpuVariant=${variant}`,
    { cwd: REPO_ROOT, stdio: ['ignore', 'inherit', 'inherit'] },
  );

  log(`Running dotnet-project-licenses for variant=${variant}...`);
  // -t walks transitive deps; -o json emits to stdout (the alpha v3
  // CLI dropped the older -j/-f flag pair in favor of typed output).
  // The tool exits non-zero whenever any package has ValidationErrors
  // (File-license metadata it can't parse), even though it still
  // prints full JSON. Use spawnSync so a non-zero status doesn't kill
  // us; the overrides table handles the missing-license cases below.
  const result = spawnSync(
    'dotnet',
    ['dotnet-project-licenses', '-i', WEB_CSPROJ, '-t', '-o', 'json'],
    {
      cwd: REPO_ROOT,
      encoding: 'utf8',
      maxBuffer: 32 * 1024 * 1024,
      shell: false,
    },
  );
  if (!result.stdout || result.stdout.trim().length === 0) {
    throw new Error(
      `dotnet-project-licenses produced no stdout for variant=${variant} ` +
      `(status=${result.status}):\n${result.stderr ?? ''}`,
    );
  }
  const data = JSON.parse(result.stdout);
  return data.map((p) => ({
    name: p.PackageId,
    version: p.PackageVersion,
    license: resolveDotnetLicense(p),
    url: p.PackageProjectUrl || '',
    authors: '',
    copyright: '',
  }));
}

function resolveDotnetLicense(p) {
  // PackageId-keyed override beats anything else — fixes the File-
  // license metadata that the alpha tool can't parse.
  const override = DOTNET_LICENSE_OVERRIDES.get(p.PackageId);
  if (override) return override;
  // Some nuspecs ship a raw http URL in `License` instead of an SPDX
  // expression (LicenseInformationOrigin === 1). Surface the URL so a
  // reader can follow it; better than dropping the entry.
  const lic = p.License;
  if (lic && typeof lic === 'string') return lic;
  return 'Unknown';
}

// ───────────────────────── npm deps ─────────────────────────

function collectNpmLicenses(dir, label) {
  log(`Running license-checker-rseidelsohn in ${label}...`);
  // --production: skip devDependencies (build-time only, never shipped).
  // shell:true is load-bearing on Windows — node_modules/.bin/<name>
  // resolves to a .cmd shim that Node can't spawn directly without
  // cmd.exe wrapping it. On Linux/macOS, shell:true is harmless here.
  const checker = path.join(
    ELECTRON_DIR,
    'node_modules',
    '.bin',
    'license-checker-rseidelsohn',
  );
  const result = spawnSync(
    `"${checker}"`,
    ['--production', '--json', '--start', `"${dir}"`],
    {
      encoding: 'utf8',
      maxBuffer: 32 * 1024 * 1024,
      shell: true,
    },
  );
  if (result.status !== 0 || !result.stdout) {
    throw new Error(
      `license-checker failed in ${label} (status=${result.status}):\n${result.stderr ?? '(no stderr)'}`,
    );
  }
  const data = JSON.parse(result.stdout);
  const entries = Object.entries(data)
    .map(([nameVer, info]) => {
      // license-checker keys look like "package@1.2.3"; split on the LAST
      // '@' to handle scoped packages (`@scope/name@1.2.3`).
      const at = nameVer.lastIndexOf('@');
      const name = nameVer.slice(0, at);
      const version = nameVer.slice(at + 1);
      return {
        name,
        version,
        license: info.licenses || 'Unknown',
        url: info.repository || '',
        authors: info.publisher || '',
        copyright: info.copyright || '',
      };
    })
    .filter((e) => !e.name.startsWith('heliosoph-datumv-'));
  return dedupeSorted(entries);
}

// ───────────────────────── helpers ─────────────────────────

function dedupeSorted(entries) {
  const byKey = new Map();
  for (const e of entries) {
    const key = `${e.name}@${e.version}`;
    if (!byKey.has(key)) byKey.set(key, e);
  }
  return [...byKey.values()].sort((a, b) =>
    a.name.localeCompare(b.name) || a.version.localeCompare(b.version),
  );
}

// Detect LGPL / GPL / AGPL family licenses. We need a distinct list
// for the source-on-request notice — LGPL is technically a "lesser"
// variant of GPL but both carry the same downstream source-disclosure
// obligation, so they're flagged together.
function isCopyleftLicense(licStr) {
  return /\b[LA]?GPL\b/i.test(String(licStr ?? ''));
}

// Manually-curated section for binaries we ship that NuGet / npm doesn't
// track. Today this covers two sets:
//   1. Microsoft Visual C++ 2015-2022 Redistributable runtime DLLs that
//      Heliosoph.DatumV.Web.csproj copies from System32 (vcruntime140.dll
//      etc). Required by LLamaSharp's native binaries; bundling lets
//      users skip the VC++ Redist installer step. Microsoft permits
//      app-local redistribution under the redistributable code license
//      terms.
//   2. The Khronos Vulkan loader (vulkan-1.dll) that electron-builder.yml
//      copies from `node_modules/electron/dist/`. Required by
//      LLamaSharp.Backend.Vulkan's ggml-vulkan.dll; bundling sidesteps
//      machines where AMD/NVIDIA driver installs didn't drop the loader
//      DLL to System32.
function formatBundledRuntimesSection() {
  return (
    `DatumV ships the following system-level runtime libraries inside\n` +
    `the installer alongside the .NET binary. They are not tracked by\n` +
    `NuGet or npm — they're sourced from the build host's System32 or\n` +
    `from Electron's bundled distribution and copied into\n` +
    `resources/backend/ at build time.\n\n` +
    subheader('Microsoft Visual C++ 2015-2022 Redistributable runtime files') +
    `Files shipped:\n` +
    `  - vcruntime140.dll              - vcruntime140_1.dll\n` +
    `  - msvcp140.dll                  - msvcp140_1.dll\n` +
    `  - msvcp140_2.dll                - msvcp140_atomic_wait.dll\n` +
    `  - msvcp140_codecvt_ids.dll      - concrt140.dll\n` +
    `  - vcomp140.dll\n\n` +
    `Copyright (c) Microsoft Corporation. All rights reserved.\n` +
    `Licensed under the Microsoft Software License Terms for Visual C++\n` +
    `Redistributable for Visual Studio 2015-2022.\n` +
    `  https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files\n\n` +
    `Source: copied at build time from the build host's\n` +
    `%SystemRoot%\\System32\\ on a machine with the Microsoft Visual\n` +
    `C++ 2015-2022 Redistributable installed. No modifications.\n` +
    subheader('Khronos Vulkan loader (vulkan-1.dll)') +
    `Files shipped:\n` +
    `  - vulkan-1.dll\n\n` +
    `Copyright (c) The Khronos Group Inc.\n` +
    `Licensed under the Apache License, Version 2.0.\n` +
    `  https://www.apache.org/licenses/LICENSE-2.0\n` +
    `  https://github.com/KhronosGroup/Vulkan-Loader\n\n` +
    `Source: this app's bundled copy is the build from the Electron\n` +
    `runtime distribution (node_modules/electron/dist/vulkan-1.dll),\n` +
    `which itself sources from the upstream Khronos Vulkan-Loader\n` +
    `project. No modifications.\n`
  );
}

function formatCopyleftNote(entries) {
  if (entries.length === 0) return '';
  const list = entries
    .map((e) => `  - ${e.name} ${e.version} (${e.license})`)
    .join('\n');
  return (
    `\n\nNOTE FOR LGPL / GPL COMPONENTS\n` +
    `${'-'.repeat(60)}\n\n` +
    `The following packages are licensed under the GNU Lesser General\n` +
    `Public License (LGPL) or General Public License (GPL):\n\n` +
    list +
    '\n\n' +
    `DatumV uses these libraries via dynamic linking — the unmanaged\n` +
    `native binaries are loaded by .NET P/Invoke at runtime and are\n` +
    `not statically linked into the DatumV executable. The complete\n` +
    `source code for each library is available from its upstream\n` +
    `project URL listed in the per-package entries above. To exercise\n` +
    `your right under the LGPL to use DatumV with a modified version\n` +
    `of any of these libraries, contact support@heliosoph.net for\n` +
    `relinking instructions.\n`
  );
}

function formatEntry(e) {
  const lines = [`${e.name} ${e.version}`];
  if (e.license) lines.push(`  License: ${normalizeLicense(e.license)}`);
  if (e.url) lines.push(`  URL: ${e.url}`);
  if (e.authors) lines.push(`  Authors: ${e.authors}`);
  if (e.copyright) lines.push(`  Copyright: ${e.copyright}`);
  if (e.variantTag) lines.push(`  Ships in: ${e.variantTag} installer variant only`);
  return lines.join('\n');
}

function normalizeLicense(raw) {
  if (Array.isArray(raw)) return raw.join(', ');
  return String(raw);
}

function formatSection(entries) {
  return entries.map(formatEntry).join('\n\n');
}

// ───────────────────────── main ─────────────────────────

function main() {
  const dotnetEntries = collectDotnetLicenses();
  log(`Collected ${dotnetEntries.length} .NET packages`);

  const electronEntries = collectNpmLicenses(ELECTRON_DIR, 'electron/');
  log(`Collected ${electronEntries.length} Electron shell npm packages`);

  const clientAppEntries = collectNpmLicenses(CLIENT_APP_DIR, 'ClientApp/');
  log(`Collected ${clientAppEntries.length} ClientApp npm packages`);

  const llamaLicense = fs.existsSync(LLAMA_LICENSE_FILE)
    ? fs.readFileSync(LLAMA_LICENSE_FILE, 'utf8')
    : '(Llama 3.1 Community License not found at licenses/llama-3.1-community.md)';

  const stabilityLicense = fs.existsSync(STABILITY_LICENSE_FILE)
    ? fs.readFileSync(STABILITY_LICENSE_FILE, 'utf8')
    : '(Stability AI Community License not found at licenses/stability-ai-community.md)';

  const now = new Date().toISOString();
  const out =
    `DatumV THIRD-PARTY NOTICES\n` +
    `Generated: ${now}\n` +
    `\n` +
    `This file aggregates license and copyright notices for every\n` +
    `third-party component that ships with the DatumV installer,\n` +
    `plus the Llama and Stability AI community licenses for the AI\n` +
    `model platforms the app is built with.\n` +
    `\n` +
    `Models downloaded through the in-app catalog at runtime are\n` +
    `NOT covered here — their licenses are presented at install\n` +
    `time by the model installer dialog and stored under licenses/\n` +
    `in this repo.\n` +
    header('SECTION 1 — LLAMA') +
    `DatumV is built with Llama.\n\n` +
    `Use of Llama models is subject to Meta's Llama 3.1 Community\n` +
    `License and Acceptable Use Policy. The full license text follows\n` +
    `below; the AUP is published at\n` +
    `  https://llama.meta.com/llama3/use-policy/\n\n` +
    llamaLicense.trim() + '\n' +
    header('SECTION 2 — STABILITY AI') +
    `Powered by Stability AI.\n\n` +
    `This Stability AI Model is licensed under the Stability AI Community\n` +
    `License, Copyright © Stability AI Ltd. All Rights Reserved.\n\n` +
    `The Stable Diffusion image models offered through the in-app catalog\n` +
    `(SD Turbo and SDXL Turbo) are governed by the Stability AI Community\n` +
    `License. The full license text follows below; the Acceptable Use\n` +
    `Policy it incorporates is published at\n` +
    `  https://stability.ai/use-policy\n\n` +
    stabilityLicense.trim() + '\n' +
    header('SECTION 3 — BUNDLED SYSTEM RUNTIME LIBRARIES') +
    formatBundledRuntimesSection() +
    header('SECTION 4 — .NET / NUGET DEPENDENCIES') +
    `The DatumV engine (.NET 10) bundles the following NuGet packages.\n` +
    `License text for each license family (MIT, Apache-2.0, BSD-*,\n` +
    `LGPL-*) follows in Section 6 below.\n\n` +
    `DatumV ships in two installer variants — \`cuda\` (NVIDIA stack) and\n` +
    `\`standard\` (DirectML/Vulkan/CPU cross-vendor stack). Most packages are\n` +
    `shared between both variants and are listed unannotated. The handful\n` +
    `of packages that ship in only one variant carry a "Ships in: X variant\n` +
    `only" line at the bottom of their entry.\n\n` +
    formatSection(dotnetEntries) +
    formatCopyleftNote(dotnetEntries.filter((e) => isCopyleftLicense(e.license))) +
    header('SECTION 5 — NODE / NPM DEPENDENCIES') +
    subheader('Electron shell (electron/)') +
    formatSection(electronEntries) +
    subheader('Renderer SPA (ClientApp/)') +
    formatSection(clientAppEntries) +
    header('SECTION 6 — STANDARD LICENSE TEXTS') +
    `Per-package licenses above identify the license family. The full\n` +
    `text of each is reproduced from the canonical source:\n\n` +
    `  MIT:        https://opensource.org/licenses/MIT\n` +
    `  Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0\n` +
    `  BSD-2:      https://opensource.org/licenses/BSD-2-Clause\n` +
    `  BSD-3:      https://opensource.org/licenses/BSD-3-Clause\n` +
    `  LGPL-2.1:   https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html\n` +
    `  LGPL-3.0:   https://www.gnu.org/licenses/lgpl-3.0.html\n` +
    `  ISC:        https://opensource.org/licenses/ISC\n`;

  fs.writeFileSync(OUTPUT, out, 'utf8');
  log(`Wrote ${OUTPUT}`);
  log(`  Total entries: ${dotnetEntries.length + electronEntries.length + clientAppEntries.length}`);
}

try {
  main();
} catch (err) {
  process.stderr.write(`[notices] FAILED: ${err.message}\n`);
  if (err.stack) process.stderr.write(err.stack + '\n');
  process.exit(1);
}
