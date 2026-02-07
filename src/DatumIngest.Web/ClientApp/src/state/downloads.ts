import { proxy } from 'valtio';
import { api } from '@/api';
import {
  acquireStreamHub,
  onModelDownloadComplete,
  onModelDownloadFailed,
  onModelDownloadProgress,
  onModelDownloadStarted,
} from '@/api/hub';
import type { ModelInstallState } from '@/api/generated/openapi-client';

// Kick off the hub connection as soon as this module loads. The server
// broadcasts download events to Clients.All — if no client is connected
// at the moment the install is queued, those events are emitted into the
// void and the UI never transitions out of "Starting download…". Calling
// acquireStreamHub() here primes the connection on first ModelsView
// mount; installModel still awaits it for the rare cases where the click
// races the connection.
void acquireStreamHub().catch(() => {
  // Connection failure surfaces later when an actual operation runs.
});

// Per-model install state + active download progress.
//
// `state` is keyed by modelId → ModelInstallState (notInstalled / partial /
// installed). Populated once on first refreshDownloads() call (bulk probe)
// and mutated in place by the SignalR event handlers as downloads run.
//
// `active` is keyed by modelId → ActiveDownload for in-flight downloads.
// Entries appear on OnModelDownloadStarted, update on each
// OnModelDownloadProgress, and disappear on OnModelDownloadComplete /
// OnModelDownloadFailed. Views render a progress bar for any model
// that has an entry here, regardless of `state`.

export interface ActiveDownload {
  modelId: string;
  bytesReadTotal: number;
  bytesTotalAcrossModel: number;
  fileIndex: number;
  fileCount: number;
  currentFile: string;
}

interface DownloadsState {
  // Lazily loaded; null until refreshDownloads completes.
  state: Record<string, ModelInstallState> | null;
  active: Record<string, ActiveDownload>;
  // Last error per model (transient — cleared on next attempt).
  errors: Record<string, string>;
  // Models with the license-required precondition that need a one-shot
  // acceptance before retrying. Cleared when retry succeeds.
  needsLicenseAcceptance: Record<string, string>; // modelId → licenseId
  loading: boolean;
}

export const downloadsState = proxy<DownloadsState>({
  state: null,
  active: {},
  errors: {},
  needsLicenseAcceptance: {},
  loading: false,
});

export async function refreshDownloads(): Promise<void> {
  if (downloadsState.loading) return;
  downloadsState.loading = true;
  try {
    const states = await api.modelCatalog.getStates();
    downloadsState.state = { ...states };
  } catch (err) {
    console.error('[downloads] failed to refresh states', err);
  } finally {
    downloadsState.loading = false;
  }
}

// Optimistic: mark the model as downloading immediately so the UI shows
// progress placeholder. The server will emit OnModelDownloadStarted right
// after, which will refine the byte counters; until then we render the
// "Starting…" state from `active` having an entry with zero bytes.
export async function installModel(modelId: string): Promise<void> {
  delete downloadsState.errors[modelId];
  delete downloadsState.needsLicenseAcceptance[modelId];

  downloadsState.active[modelId] = {
    modelId,
    bytesReadTotal: 0,
    bytesTotalAcrossModel: 0,
    fileIndex: 0,
    fileCount: 0,
    currentFile: '',
  };

  try {
    // Ensure the hub is connected before the server starts emitting events.
    // Idempotent — fast path when already connected, awaits the in-flight
    // start() promise if a connection is racing this install click.
    await acquireStreamHub();
    await api.modelCatalog.install(modelId);
  } catch (err) {
    delete downloadsState.active[modelId];
    handleInstallError(modelId, err);
  }
}

// Two-step license-required flow: POST accept-license, then retry install.
// This is the temporary v1 path until the proper modal-with-license-text
// dialog round lands.
export async function acceptLicenseAndInstall(modelId: string): Promise<void> {
  const licenseId = downloadsState.needsLicenseAcceptance[modelId];
  if (!licenseId) {
    // Nothing pending — just try the install (caller likely raced).
    return installModel(modelId);
  }
  try {
    await api.modelCatalog.acceptLicense(licenseId);
    delete downloadsState.needsLicenseAcceptance[modelId];
    delete downloadsState.errors[modelId];
    await installModel(modelId);
  } catch (err) {
    handleInstallError(modelId, err);
  }
}

export async function uninstallModel(modelId: string): Promise<void> {
  try {
    await api.modelCatalog.uninstall(modelId);
    if (downloadsState.state) {
      downloadsState.state[modelId] = 'notInstalled';
    }
    delete downloadsState.errors[modelId];
    delete downloadsState.needsLicenseAcceptance[modelId];
  } catch (err) {
    downloadsState.errors[modelId] = err instanceof Error ? err.message : String(err);
  }
}

// NSwag generates a SwaggerException with `status` (HTTP code) and
// `response` (raw body). We inspect both to distinguish the 412
// license-not-accepted path from other failures.
function handleInstallError(modelId: string, err: unknown): void {
  // Parse 412 license-not-accepted shape:
  // { error: 'license_not_accepted', licenseId: '<id>', message: '<text>' }
  const status = readErrorField(err, 'status');
  const response = readErrorField(err, 'response');
  if (status === 412 && typeof response === 'string') {
    try {
      const parsed = JSON.parse(response);
      if (parsed?.error === 'license_not_accepted' && typeof parsed?.licenseId === 'string') {
        downloadsState.needsLicenseAcceptance[modelId] = parsed.licenseId;
        downloadsState.errors[modelId] = parsed.message ?? 'License acceptance required';
        return;
      }
    } catch {
      // fall through to generic error
    }
  }
  downloadsState.errors[modelId] = err instanceof Error ? err.message : String(err);
}

function readErrorField(err: unknown, key: string): unknown {
  if (err && typeof err === 'object' && key in err) {
    return (err as Record<string, unknown>)[key];
  }
  return undefined;
}

// ───────────────────────── Hub event wiring ─────────────────────────

onModelDownloadStarted((event) => {
  downloadsState.active[event.modelId] = {
    modelId: event.modelId,
    bytesReadTotal: 0,
    bytesTotalAcrossModel: event.totalBytes ?? 0,
    fileIndex: 0,
    fileCount: event.fileCount ?? 0,
    currentFile: '',
  };
});

onModelDownloadProgress((event) => {
  // Server may emit progress for a model we don't have an `active` entry
  // for if our state was reset (e.g. hot reload). Add it.
  const existing = downloadsState.active[event.modelId];
  downloadsState.active[event.modelId] = {
    modelId: event.modelId,
    bytesReadTotal: event.bytesReadTotal ?? existing?.bytesReadTotal ?? 0,
    bytesTotalAcrossModel: event.bytesTotalAcrossModel ?? existing?.bytesTotalAcrossModel ?? 0,
    fileIndex: event.fileIndex ?? existing?.fileIndex ?? 0,
    fileCount: event.fileCount ?? existing?.fileCount ?? 0,
    currentFile: event.currentFile ?? existing?.currentFile ?? '',
  };
});

onModelDownloadComplete((event) => {
  delete downloadsState.active[event.modelId];
  delete downloadsState.errors[event.modelId];
  if (downloadsState.state) {
    downloadsState.state[event.modelId] = 'installed';
  }
});

onModelDownloadFailed((event) => {
  delete downloadsState.active[event.modelId];
  downloadsState.errors[event.modelId] = event.error ?? 'Download failed';
});
