import { proxy } from 'valtio';
import { api } from '@/api';
import {
  acquireStreamHub,
  onModelDownloadComplete,
  onModelDownloadFailed,
  onModelDownloadProgress,
  onModelDownloadStarted,
} from '@/api/hub';
import { openDialog } from '@/state/dialogs';
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
  loading: boolean;
}

export const downloadsState = proxy<DownloadsState>({
  state: null,
  active: {},
  errors: {},
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
//
// If the install returns 412 (license required), this function opens the
// license-acceptance dialog, awaits the user's choice, accepts the
// license, and retries the install — all transparent to the caller.
// `modelDisplayName` is optional but improves the dialog UX ("Required
// to download Realistic Vision V6" reads better than the bare id).
export async function installModel(
  modelId: string,
  modelDisplayName?: string,
): Promise<void> {
  delete downloadsState.errors[modelId];

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
    console.error('[downloads] install failed', modelId, err);
    const licenseId = readLicenseRequired(err);
    if (licenseId) {
      await handleLicenseRequired(modelId, licenseId, modelDisplayName);
      return;
    }
    downloadsState.errors[modelId] = describeError(err);
  }
}

// Walked through the 412-license flow: open the license dialog, await the
// user's accept/decline, and either retry or record the rejection. Each
// step writes to downloadsState so the card UI reflects what's happening
// without the caller having to thread context through.
async function handleLicenseRequired(
  modelId: string,
  licenseId: string,
  modelDisplayName: string | undefined,
): Promise<void> {
  const { result } = openDialog<{ accepted: boolean }>({
    kind: 'confirmLicense',
    payload: {
      licenseId,
      modelDisplayName: modelDisplayName ?? '',
    },
  });
  const decision = await result;
  if (!decision?.accepted) {
    // User declined or closed the dialog — surface as a soft error on
    // the card. Clicking Download again will re-prompt.
    downloadsState.errors[modelId] = 'License declined';
    return;
  }
  try {
    await api.modelCatalog.acceptLicense(licenseId);
  } catch (err) {
    console.error('[downloads] acceptLicense failed', licenseId, err);
    downloadsState.errors[modelId] = describeError(err);
    return;
  }
  // License is accepted; retry the install. installModel handles its own
  // optimistic-state setup again.
  await installModel(modelId, modelDisplayName);
}

export async function uninstallModel(modelId: string): Promise<void> {
  try {
    await api.modelCatalog.uninstall(modelId);
    if (downloadsState.state) {
      downloadsState.state[modelId] = 'notInstalled';
    }
    delete downloadsState.errors[modelId];
  } catch (err) {
    console.error('[downloads] uninstall failed', modelId, err);
    downloadsState.errors[modelId] = describeError(err);
  }
}

// NSwag emits SwaggerException that sometimes doesn't satisfy
// `instanceof Error` (depending on transpile target / class semantics),
// so `String(err)` returns "[object Object]" instead of the message.
// This helper walks the most common error shapes: Error.message,
// SwaggerException-style { message, status, response }, plain strings,
// finally falling back to a JSON dump so the UI never shows
// "[object Object]" again.
function describeError(err: unknown): string {
  if (err == null) return 'Unknown error';
  if (typeof err === 'string') return err;
  if (typeof err === 'object') {
    const o = err as Record<string, unknown>;
    if (typeof o.message === 'string' && o.message.length > 0) {
      const status = typeof o.status === 'number' ? ` (HTTP ${o.status})` : '';
      return o.message + status;
    }
    if (typeof o.response === 'string' && o.response.length > 0) {
      return o.response;
    }
    try { return JSON.stringify(err); } catch { /* fall through */ }
  }
  return String(err);
}

// NSwag's `throwException` helper throws `result` *directly* when the
// generated client has a typed response object for the status code.
// Our `Install` action's 412 is declared with `ProblemDetails` as the
// result type, so the JSON body — `{ error, licenseId, message }` —
// becomes the rejected value instead of being wrapped in a
// SwaggerException. That's why a plain `status`/`response` check
// silently misses it.
//
// We check both shapes:
//   1. Body thrown directly: `{ error: 'license_not_accepted', licenseId }`
//      (what NSwag actually does today for declared status codes).
//   2. SwaggerException-wrapped: `{ status: 412, response: '<raw json>' }`
//      (fallback for status codes without a declared response type).
function readLicenseRequired(err: unknown): string | null {
  if (!err || typeof err !== 'object') return null;
  const o = err as Record<string, unknown>;

  // Shape 1: NSwag threw the parsed body directly.
  if (o.error === 'license_not_accepted' && typeof o.licenseId === 'string') {
    return o.licenseId;
  }

  // Shape 2: SwaggerException with raw response string.
  if (o.status === 412 && typeof o.response === 'string') {
    try {
      const parsed = JSON.parse(o.response);
      if (parsed?.error === 'license_not_accepted' && typeof parsed?.licenseId === 'string') {
        return parsed.licenseId;
      }
    } catch {
      /* fall through */
    }
  }

  return null;
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
  // Native toast on Electron. Best-effort — fire and forget; failure
  // (e.g. user disabled notifications) is silent.
  if (window.electronHost?.isElectron) {
    void window.electronHost.notify({
      title: 'Download complete',
      body: `${event.modelId} is installed`,
    });
  }
});

onModelDownloadFailed((event) => {
  delete downloadsState.active[event.modelId];
  downloadsState.errors[event.modelId] = event.error ?? 'Download failed';
});
