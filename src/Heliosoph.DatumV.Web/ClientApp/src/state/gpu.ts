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
import type { GpuStatusDto } from '@/api/generated/hubs/Heliosoph.DatumV.Web.Api';

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
  void refreshGpuStatus();
});

onCudaBundleInstallFailed((e) => {
  gpuState.phase = 'failed';
  gpuState.error = e.error;
});
