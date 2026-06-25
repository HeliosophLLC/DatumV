import { proxy } from 'valtio';
import {
  acquireStreamHub,
  onCudaBundleInstallStarted,
  onCudaBundleDownloadProgress,
  onCudaBundleExtractStarted,
  onCudaBundleExtractProgress,
  onCudaBundleInstalled,
  onCudaBundleInstallFailed,
} from '@/api/hub';
import { pushSample, type ProgressSample } from '@/state/progressSamples';
import { host } from '@/host';
import { openDialog } from '@/state/dialogs';
import { refreshSettings, settingsState, updateSettings } from '@/state/settings';
import { isTornOutWindow } from '@/state/tabs';
import type { GpuStatusDto } from '@/api/generated/hubs/Heliosoph.DatumV.Web.Api';

// Stable id used in the status-bar DownloadsChip rows + error map keys.
// Single-tenant (there's only ever one CUDA bundle install in flight on a
// given host) so a fixed key is fine; the map shape matches the model and
// dataset state modules for the chip's polymorphic adapter pattern.
export const GPU_BUNDLE_ID = 'cuda-runtime';

// GPU acceleration state for the Settings → GPU section. Lifecycle:
//   - refreshStatus(): polled on Settings mount + after install/uninstall.
//   - install():       POSTs /api/gpu/install. Backend streams progress
//                      via SignalR; the module-level handlers below
//                      mirror events into `progress`.
//   - cancelInstall(): POSTs /api/gpu/install/cancel.
//   - uninstall():     DELETEs the installed version.
//   - restartBackend(): IPC to electron main; main respawns the .NET
//                      child against the same catalog so LD_LIBRARY_PATH
//                      picks up the new cache dir.
//
// The .NET endpoints are not in the NSwag-generated OpenAPI client yet
// because the codegen runs from a Windows dev machine — once that's done
// these `fetch` calls can swap to `api.gpu.*`.

// Prime the hub connection on first import — same rationale as
// state/downloads.ts: install events fired into the void if no client
// is connected leave the UI stuck on "Starting…".
void acquireStreamHub().catch(() => {
  // Connection failure surfaces later via an actual operation.
});

export type GpuInstallPhase = 'idle' | 'downloading' | 'extracting' | 'completed' | 'failed';

interface GpuStateShape {
  status: GpuStatusDto | null;
  // Lifecycle of the currently-active install (or completed/failed
  // result of the last one). `idle` covers both "never installed" and
  // "long-completed".
  phase: GpuInstallPhase;
  // Download phase progress.
  bytesDownloaded: number;
  bytesTotal: number;
  // Rolling samples for the rate + ETA display, shared with model
  // download UI via state/progressSamples.
  samples: readonly ProgressSample[];
  // Ms since epoch (Date.now()) when the install started. Used for
  // elapsed display + ETA seed.
  startedAt: number | null;
  // Extract phase progress.
  filesExtracted: number;
  totalFiles: number;
  // Last error (cleared on next install attempt).
  error: string | null;
  // Map of bundle-id → error message, mirrors downloadsState.errors so
  // the DownloadsChip's FailedRow can render a gpu retry/dismiss row
  // alongside model + dataset failures. Single key today (GPU_BUNDLE_ID)
  // but the chip's polymorphic aggregation already keys by id.
  errors: Record<string, string>;
  // True between restartBackend() being invoked and the IPC resolving.
  // The renderer reloads itself as part of the catalog-swap path, so
  // this rarely matters in practice — the UI gets reloaded mid-flight.
  restarting: boolean;
}

export const gpuState = proxy<GpuStateShape>({
  status: null,
  phase: 'idle',
  bytesDownloaded: 0,
  bytesTotal: 0,
  samples: [],
  startedAt: null,
  filesExtracted: 0,
  totalFiles: 0,
  error: null,
  errors: {},
  restarting: false,
});

// ───────────────────────── Backend calls ─────────────────────────

async function fetchGpu<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await window.fetch(`/api/gpu${path}`, {
    ...init,
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
  });
  if (!res.ok) {
    let detail = '';
    try {
      detail = await res.text();
    } catch {
      // best-effort
    }
    throw new Error(`gpu ${init?.method ?? 'GET'} ${path} → ${res.status}${detail ? `: ${detail}` : ''}`);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export async function refreshGpuStatus(): Promise<void> {
  try {
    const status = await fetchGpu<GpuStatusDto>('/status');
    gpuState.status = status;
    // If the backend reports an install is in flight but our local
    // phase is `idle`, sync to `downloading` — covers refresh after a
    // page reload while an install was running.
    if (status.isInstalling && gpuState.phase === 'idle') {
      gpuState.phase = 'downloading';
      gpuState.startedAt ??= Date.now();
    }
  } catch (err) {
    console.error('[gpu] refresh failed', err);
  }
}

export async function installCuda(): Promise<void> {
  if (gpuState.phase === 'downloading' || gpuState.phase === 'extracting') return;
  gpuState.error = null;
  gpuState.bytesDownloaded = 0;
  gpuState.bytesTotal = gpuState.status?.availableEntry?.sizeBytes ?? 0;
  gpuState.filesExtracted = 0;
  gpuState.totalFiles = 0;
  gpuState.samples = [];
  gpuState.startedAt = Date.now();
  gpuState.phase = 'downloading';
  try {
    await fetchGpu<void>('/install', { method: 'POST' });
  } catch (err) {
    gpuState.phase = 'failed';
    gpuState.error = err instanceof Error ? err.message : String(err);
  }
}

export async function cancelInstall(): Promise<void> {
  try {
    await fetchGpu<void>('/install/cancel', { method: 'POST' });
  } catch (err) {
    console.error('[gpu] cancel failed', err);
  }
}

export async function uninstallCuda(version: string): Promise<void> {
  try {
    await fetchGpu<void>(`/installed/${encodeURIComponent(version)}`, { method: 'DELETE' });
    await refreshGpuStatus();
  } catch (err) {
    gpuState.error = err instanceof Error ? err.message : String(err);
  }
}

// DownloadsChip dismiss hooks. Match the names from state/downloads +
// state/datasets so the chip can route by source tag without per-source
// branching at the call site.
export function dismissError(_id: string): void {
  gpuState.errors = {};
  gpuState.error = null;
  if (gpuState.phase === 'failed') gpuState.phase = 'idle';
}

export function dismissActiveInstall(_id: string): void {
  // For an in-flight install, "dismiss" means cancel — we don't want to
  // leave partial downloads racing in the background after the user
  // explicitly closed the chip row.
  cancelInstall();
  gpuState.phase = 'idle';
  gpuState.bytesDownloaded = 0;
  gpuState.bytesTotal = 0;
  gpuState.samples = [];
}

export async function restartBackend(): Promise<void> {
  gpuState.restarting = true;
  try {
    await host.restartBackend();
  } finally {
    // In practice the catalog-swap flow reloads the renderer before
    // this resolves, so the flag mostly matters for the dev case where
    // the IPC is mocked.
    gpuState.restarting = false;
  }
}

// ───────────────────────── Hub event handlers ─────────────────────────

onCudaBundleInstallStarted((e) => {
  gpuState.phase = 'downloading';
  gpuState.bytesDownloaded = 0;
  gpuState.bytesTotal = e.totalBytes;
  gpuState.samples = [];
  gpuState.startedAt = Date.now();
  gpuState.error = null;
});

onCudaBundleDownloadProgress((e) => {
  gpuState.bytesDownloaded = e.bytesDownloaded;
  if (e.totalBytes > gpuState.bytesTotal) gpuState.bytesTotal = e.totalBytes;
  gpuState.samples = pushSample(gpuState.samples, e.bytesDownloaded);
});

onCudaBundleExtractStarted(() => {
  gpuState.phase = 'extracting';
});

onCudaBundleExtractProgress((e) => {
  gpuState.filesExtracted = e.filesExtracted;
  gpuState.totalFiles = e.totalFiles;
});

onCudaBundleInstalled(() => {
  gpuState.phase = 'completed';
  // Clear chip-relevant fields so the DownloadsChip drops the active /
  // installing row immediately. The Settings → GPU section still reads
  // the installed version via status (re-fetched below).
  gpuState.bytesDownloaded = 0;
  gpuState.bytesTotal = 0;
  gpuState.filesExtracted = 0;
  gpuState.totalFiles = 0;
  gpuState.samples = [];
  void refreshGpuStatus();
  // Pop the "Restart backend?" prompt — only on the main window so
  // torn-out tab windows don't double-fire. promptRestart guards against
  // re-firing within the same renderer.
  if (!isTornOutWindow) {
    void promptRestart();
  }
});

onCudaBundleInstallFailed((e) => {
  gpuState.phase = 'failed';
  gpuState.error = e.error;
  gpuState.errors = { [GPU_BUNDLE_ID]: e.error };
});

// ───────────────────────── Restart-after-install prompt ─────────────────────────

let restartPromptShown = false;

async function promptRestart(): Promise<void> {
  if (restartPromptShown) return;
  restartPromptShown = true;
  const handle = openDialog<{ action: 'restart' | 'later' }>({
    kind: 'gpuRestartPrompt',
    payload: {},
  });
  const result = await handle.result;
  if (result?.action === 'restart') {
    void restartBackend();
  }
  // 'later' / null (X-close) → user can restart from Settings → GPU later.
}

// ───────────────────────── First-launch prompt ─────────────────────────

// One-shot guard so a re-mount of the main app shell doesn't re-fire the
// prompt within the same renderer process. Across launches the persistence
// signal is settings.gpuInstallPromptDismissed.
let promptCheckRan = false;
let wrongBuildPromptCheckRan = false;

// Called once on app mount. Surfaces a modal asking the user to install
// GPU support iff every gate passes: this is the cuda build variant, an
// NVIDIA driver is detected, the GPU is actually CUDA-compatible (CC 7.0+),
// the bundle isn't already installed, and the user hasn't previously
// chosen "Don't ask again." Otherwise no-op so the app boots straight to
// the workspace.
export async function maybePromptGpuInstall(): Promise<void> {
  if (promptCheckRan) return;
  promptCheckRan = true;

  // Make sure both inputs are loaded; refreshSettings is also called by
  // App.tsx but it's idempotent and cheap.
  await Promise.all([refreshGpuStatus(), refreshSettings()]);

  const status = gpuState.status;
  if (status === null) return;
  if (!status.variantSupportsCuda) return;
  if (!status.hasNvidiaDriver) return;
  if (!status.cudaCompatible) return;
  if (status.installedVersion !== null && status.installedVersion !== undefined) return;
  if (status.availableEntry === null || status.availableEntry === undefined) return;
  if (settingsState.gpuInstallPromptDismissed) return;

  const handle = openDialog<{ action: 'install' | 'later' | 'never' }>({
    kind: 'gpuInstallPrompt',
    payload: {
      gpuName: status.nvidiaGpuName ?? 'NVIDIA GPU',
      sizeBytes: status.availableEntry.sizeBytes,
    },
  });
  const result = await handle.result;
  if (result?.action === 'install') {
    // Fire-and-forget. Progress flows through state/gpu.ts SignalR
    // subscribers; user can watch in Settings → GPU.
    void installCuda();
  } else if (result?.action === 'never') {
    // gpuInstallPromptDismissed isn't in the NSwag-generated
    // SettingsPatchDto until a Windows build re-runs GenerateApi. Cast
    // through `unknown` so the patch typechecks today; remove once regen
    // ships.
    void updateSettings({
      gpuInstallPromptDismissed: true,
    } as unknown as Parameters<typeof updateSettings>[0]);
  }
  // 'later' (and `null` from window X-close) leave the dismissed flag
  // alone — the prompt will fire again on the next launch.
}

// Mirror of maybePromptGpuInstall for the inverse case: this is the
// cuda build but the machine has no usable NVIDIA GPU (either no
// driver, or the GPU is older than the cudaCompatible floor). The
// user almost certainly downloaded the wrong installer — point them
// at the Standard one, which would actually give them GPU acceleration
// on this hardware via DirectML (Windows) or Vulkan (Linux + LLMs).
//
// Mutually exclusive with maybePromptGpuInstall by construction: that
// one requires hasNvidiaDriver && cudaCompatible, this one requires
// !hasNvidiaDriver || !cudaCompatible. Calling both is safe — only
// one will ever fire.
export async function maybePromptGpuWrongBuild(): Promise<void> {
  if (wrongBuildPromptCheckRan) return;
  wrongBuildPromptCheckRan = true;

  await Promise.all([refreshGpuStatus(), refreshSettings()]);

  const status = gpuState.status;
  if (status === null) return;
  // Only relevant on the cuda build — the standard build has no "wrong
  // build" condition; it works on any GPU (or falls back to CPU silently).
  if (!status.variantSupportsCuda) return;
  // If the platform isn't supported at all (e.g. ARM) there's no
  // alternative build to recommend either. Skip.
  if (status.platform === null || status.platform === undefined) return;
  // Happy path: NVIDIA driver + compatible GPU. maybePromptGpuInstall
  // handles this case — we have nothing to warn about here.
  if (status.hasNvidiaDriver && status.cudaCompatible) return;
  if (settingsState.gpuWrongBuildPromptDismissed) return;

  const reason: 'noDriver' | 'incompatibleArch' = status.hasNvidiaDriver
    ? 'incompatibleArch'
    : 'noDriver';

  const handle = openDialog<{ action: 'open' | 'later' | 'never' }>({
    kind: 'gpuWrongBuildPrompt',
    payload: {
      reason,
      gpuName: status.nvidiaGpuName ?? null,
      cc: status.nvidiaComputeCapability ?? null,
    },
  });
  const result = await handle.result;
  if (result?.action === 'never') {
    void updateSettings({
      gpuWrongBuildPromptDismissed: true,
    } as unknown as Parameters<typeof updateSettings>[0]);
  }
  // 'open' (already kicked the browser open inside the dialog) and
  // 'later' / null both leave the dismissed flag alone — the prompt
  // fires again next launch until the user explicitly opts out.
}
