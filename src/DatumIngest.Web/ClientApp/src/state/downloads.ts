import { proxy } from 'valtio';
import { api } from '@/api';
import {
  acquireStreamHub,
  onModelDownloadComplete,
  onModelDownloadFailed,
  onModelDownloadProgress,
  onModelDownloadStarted,
  onModelInstalled,
  onModelInstalling,
  onUvDownloadStarted,
  onUvDownloadProgress,
  onUvDownloadComplete,
  onPythonInstallStarted,
  onPythonInstallProgress,
  onPythonInstallComplete,
  onVenvInstallStarted,
  onVenvInstallProgress,
  onVenvInstallComplete,
  onPythonEnvironmentFailed,
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

export interface ProgressSample {
  // performance.now() timestamp in ms.
  t: number;
  bytes: number;
}

export interface ActiveDownload {
  modelId: string;
  bytesReadTotal: number;
  bytesTotalAcrossModel: number;
  fileIndex: number;
  fileCount: number;
  currentFile: string;
  // ms since epoch (Date.now()) — used for elapsed display.
  startedAt: number;
  // Sliding window of recent progress events. The renderer derives the
  // instantaneous rate from (last - first) of this window, which tracks
  // current speed instead of the lifetime average. Capped at SAMPLE_CAP.
  // Readonly so the type lines up with valtio's deeply-readonly snapshot;
  // we replace the array wholesale on each progress event (pushSample
  // returns a new one), never mutating in place.
  samples: readonly ProgressSample[];
}

const SAMPLE_CAP = 12;
// Drop samples older than this so a long pause (e.g. file open / hash
// verify between files) doesn't pollute the rate window with stale data.
const SAMPLE_MAX_AGE_MS = 8_000;

function pushSample(prev: readonly ProgressSample[] | undefined, bytes: number): ProgressSample[] {
  const now = performance.now();
  const fresh = (prev ?? []).filter((s) => now - s.t < SAMPLE_MAX_AGE_MS);
  fresh.push({ t: now, bytes });
  if (fresh.length > SAMPLE_CAP) fresh.splice(0, fresh.length - SAMPLE_CAP);
  return fresh;
}

// Sub-step status shown inline on the model card during the post-download
// install phase, for kind:"python" catalog entries. `kind` discriminates
// the stage and drives the label; `detail` carries free-form text from
// the install backend (uv wheel name, pip cache hit count, etc.). When
// `bytesProcessed` is present the UI renders a percentage bar; otherwise
// it shows an indeterminate spinner.
export type PythonInstallStepKind = 'uv-download' | 'python-install' | 'venv-install';

export interface PythonInstallStep {
  kind: PythonInstallStepKind;
  // Stage label from the backend ("downloading", "extracting", "linking from cache").
  stage: string;
  // Free-form per-stage detail (wheel name, version, etc).
  detail?: string;
  bytesProcessed?: number;
  totalBytes?: number;
}

interface DownloadsState {
  // Lazily loaded; null until refreshDownloads completes.
  state: Record<string, ModelInstallState> | null;
  active: Record<string, ActiveDownload>;
  // Set of model ids currently in the post-download install phase
  // (CatalogModel.installSql is running). Entries appear on OnModelInstalling
  // and disappear on OnModelInstalled / OnModelDownloadFailed. Distinct
  // from `active` so the UI can render an "installing…" spinner without
  // the byte-progress chrome.
  installing: Record<string, true>;
  // Last error per model (transient — cleared on next attempt).
  errors: Record<string, string>;
  // Per-model on-disk partial bytes (sum of *.part files in the model
  // directory). Populated by refreshDownloads. Used by the Models view to
  // surface Resume/Restart affordances; absence means zero.
  partials: Record<string, number>;
  // Machine-scoped uv + python install steps. One-time per host; populated
  // by hub events independent of any specific model. The model card
  // surfaces these on whichever entry is currently installing, since
  // that's what triggered them.
  pythonHostStep: PythonInstallStep | null;
  // Per-model venv install step, keyed by catalog id (== VenvName).
  // Appears on OnVenvInstallStarted, refines on OnVenvInstallProgress,
  // disappears on OnVenvInstallComplete / OnPythonEnvironmentFailed.
  venvSteps: Record<string, PythonInstallStep>;
  loading: boolean;
}

export const downloadsState = proxy<DownloadsState>({
  state: null,
  active: {},
  installing: {},
  errors: {},
  partials: {},
  pythonHostStep: null,
  venvSteps: {},
  loading: false,
});

// Filesystem-only refresh; doesn't touch HF. Cheap enough to call from
// hub event handlers (failure, mid-download checkpoint) without a guard.
export async function refreshPartials(): Promise<void> {
  try {
    const partials = await api.modelCatalog.getAllPartialBytes();
    downloadsState.partials = { ...partials };
  } catch (err) {
    console.error('[downloads] failed to refresh partials', err);
  }
}

export async function refreshDownloads(): Promise<void> {
  if (downloadsState.loading) return;
  downloadsState.loading = true;
  try {
    // Probe + partial-bytes scan are independent; run them concurrently.
    // Probe touches HF for revisions whose dirs exist; partial-bytes is
    // filesystem-only and effectively instant. Combined wait is dominated
    // by Probe.
    const [states, partials] = await Promise.all([
      api.modelCatalog.getStates(),
      api.modelCatalog.getAllPartialBytes(),
    ]);
    downloadsState.state = { ...states };
    downloadsState.partials = { ...partials };
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
    startedAt: Date.now(),
    samples: [],
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

// Wipe any partial bytes for a model on disk, clear local partial-state,
// and kick off a fresh install. Used by the "Restart" affordance on the
// model card; complements installModel's natural cross-session resume by
// giving the user an escape hatch when partials look stuck or wrong.
export async function restartDownload(
  modelId: string,
  modelDisplayName?: string,
): Promise<void> {
  try {
    await api.modelCatalog.deletePartials(modelId);
    delete downloadsState.partials[modelId];
  } catch (err) {
    console.error('[downloads] deletePartials failed', modelId, err);
    downloadsState.errors[modelId] = describeError(err);
    return;
  }
  await installModel(modelId, modelDisplayName);
}

export function dismissDownloadError(modelId: string): void {
  delete downloadsState.errors[modelId];
}

// Locally drop an in-flight active entry. Server-side cancel doesn't exist
// today, so this only clears the UI badge — if the underlying download was
// merely stalled (e.g. wifi blip) and bytes resume later, the next progress
// event will re-create the entry. Useful for clearing a download whose
// `OnModelDownloadFailed` event got swallowed by a SignalR disconnect.
export function dismissActiveDownload(modelId: string): void {
  delete downloadsState.active[modelId];
}

export async function uninstallModel(modelId: string): Promise<void> {
  try {
    await api.modelCatalog.uninstall(modelId);
    if (downloadsState.state) {
      downloadsState.state[modelId] = 'notDownloaded';
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
  const existing = downloadsState.active[event.modelId];
  downloadsState.active[event.modelId] = {
    modelId: event.modelId,
    bytesReadTotal: 0,
    bytesTotalAcrossModel: event.totalBytes ?? 0,
    fileIndex: 0,
    fileCount: event.fileCount ?? 0,
    currentFile: '',
    // Keep the optimistic startedAt if installModel set one — the user
    // perceives "the download started when I clicked", not when the server
    // bound a file handle. Only synthesise one if we somehow missed the
    // optimistic create (e.g. SignalR landed before installModel returned).
    startedAt: existing?.startedAt ?? Date.now(),
    samples: existing?.samples ?? [],
  };
});

onModelDownloadProgress((event) => {
  // Server may emit progress for a model we don't have an `active` entry
  // for if our state was reset (e.g. hot reload). Add it.
  const existing = downloadsState.active[event.modelId];
  const nextBytes = event.bytesReadTotal ?? existing?.bytesReadTotal ?? 0;
  downloadsState.active[event.modelId] = {
    modelId: event.modelId,
    bytesReadTotal: nextBytes,
    bytesTotalAcrossModel: event.bytesTotalAcrossModel ?? existing?.bytesTotalAcrossModel ?? 0,
    fileIndex: event.fileIndex ?? existing?.fileIndex ?? 0,
    fileCount: event.fileCount ?? existing?.fileCount ?? 0,
    currentFile: event.currentFile ?? existing?.currentFile ?? '',
    startedAt: existing?.startedAt ?? Date.now(),
    samples: pushSample(existing?.samples, nextBytes),
  };
});

onModelDownloadComplete((event) => {
  delete downloadsState.active[event.modelId];
  delete downloadsState.errors[event.modelId];
  // Any .part files have been renamed into place by the server; the
  // Resume/Restart affordances would be stale until the next refresh.
  delete downloadsState.partials[event.modelId];
  if (downloadsState.state) {
    // Files-on-disk phase done. For entries with no installSql this is
    // terminal and the server-side probe would have returned Installed;
    // optimistically reflect Installed locally too. Entries with installSql
    // will fire OnModelInstalling next and we'll demote to Downloaded
    // until OnModelInstalled lifts it back.
    downloadsState.state[event.modelId] = 'installed';
  }
  // Native toast on Electron. Best-effort — fire and forget; failure
  // (e.g. user disabled notifications) is silent.
  if (window.electronHost?.isElectron) {
    void window.electronHost.notify({
      title: 'Download complete',
      body: `${event.modelId} is downloaded`,
    });
  }
});

onModelInstalling((event) => {
  downloadsState.installing[event.modelId] = true;
  // Demote the optimistic OnComplete-set Installed back to Downloaded so
  // the UI doesn't flash "Installed" between Complete and Installed.
  if (downloadsState.state) {
    downloadsState.state[event.modelId] = 'downloaded';
  }
});

onModelInstalled((event) => {
  delete downloadsState.installing[event.modelId];
  if (downloadsState.state) {
    downloadsState.state[event.modelId] = 'installed';
  }
  if (window.electronHost?.isElectron) {
    void window.electronHost.notify({
      title: 'Install complete',
      body: `${event.modelId} is ready to use`,
    });
  }
});

onModelDownloadFailed((event) => {
  delete downloadsState.active[event.modelId];
  delete downloadsState.installing[event.modelId];
  delete downloadsState.venvSteps[event.modelId];
  downloadsState.errors[event.modelId] = event.error ?? 'Download failed';
  // The .part is still on disk and probably bigger than what we last
  // recorded. Refresh the partials map so the Resume button shows the
  // current size; fire-and-forget — failures during the refresh are
  // already logged inside.
  void refreshPartials();
});

// ─── Python environment install events ─────────────────────────
//
// uv + python install events are machine-scoped (no modelId). They land
// during the venv install for whichever python-kind model the user just
// clicked. The model card cross-references `pythonHostStep` whenever it
// renders the "Installing…" indicator, so the active uv/python phase
// shows up under the right card without us threading a fake key.

onUvDownloadStarted((event) => {
  downloadsState.pythonHostStep = {
    kind: 'uv-download',
    stage: 'downloading',
    detail: `uv ${event.version}`,
    bytesProcessed: 0,
    totalBytes: event.totalBytes,
  };
});

onUvDownloadProgress((event) => {
  if (downloadsState.pythonHostStep?.kind !== 'uv-download') return;
  downloadsState.pythonHostStep = {
    ...downloadsState.pythonHostStep,
    bytesProcessed: event.bytesDownloaded,
    totalBytes: event.totalBytes,
  };
});

onUvDownloadComplete(() => {
  if (downloadsState.pythonHostStep?.kind === 'uv-download') {
    downloadsState.pythonHostStep = null;
  }
});

onPythonInstallStarted((event) => {
  downloadsState.pythonHostStep = {
    kind: 'python-install',
    stage: 'starting',
    detail: `Python ${event.version}`,
  };
});

onPythonInstallProgress((event) => {
  if (downloadsState.pythonHostStep?.kind !== 'python-install') return;
  downloadsState.pythonHostStep = {
    ...downloadsState.pythonHostStep,
    stage: event.stage,
    bytesProcessed: event.bytesProcessed,
    totalBytes: event.totalBytes,
  };
});

onPythonInstallComplete(() => {
  if (downloadsState.pythonHostStep?.kind === 'python-install') {
    downloadsState.pythonHostStep = null;
  }
});

onVenvInstallStarted((event) => {
  downloadsState.venvSteps[event.venvName] = {
    kind: 'venv-install',
    stage: 'starting',
    detail: `${event.requirements.length} requirement${event.requirements.length === 1 ? '' : 's'}`,
  };
});

onVenvInstallProgress((event) => {
  const existing = downloadsState.venvSteps[event.venvName];
  downloadsState.venvSteps[event.venvName] = {
    kind: 'venv-install',
    stage: event.stage || existing?.stage || 'installing',
    detail: event.detail || existing?.detail,
  };
});

onVenvInstallComplete((event) => {
  delete downloadsState.venvSteps[event.venvName];
});

onPythonEnvironmentFailed((event) => {
  // Surface to the per-model error slot when we know which venv failed;
  // host-scoped failures (uv-download, python-install) land on whichever
  // model triggered the install — the next OnModelDownloadFailed will
  // overwrite this with the more model-specific message, so leaving the
  // host step cleared is enough.
  downloadsState.pythonHostStep = null;
  if (event.venvNameOrEmpty) {
    delete downloadsState.venvSteps[event.venvNameOrEmpty];
    downloadsState.errors[event.venvNameOrEmpty] =
      event.error || `${event.stage} failed`;
  }
});

// ───────────────────────── Derived stats helpers ─────────────────────────

// Instantaneous transfer rate in bytes/sec, derived from the sliding
// progress sample window. Returns null when there isn't enough data (one
// sample or all samples within the same ~tick) so callers can render a
// placeholder instead of "0 B/s" or "Infinity".
export function computeRateBytesPerSec(samples: readonly ProgressSample[]): number | null {
  if (samples.length < 2) return null;
  const first = samples[0];
  const last = samples[samples.length - 1];
  const dtSec = (last.t - first.t) / 1000;
  if (dtSec <= 0) return null;
  const dBytes = last.bytes - first.bytes;
  if (dBytes <= 0) return null;
  return dBytes / dtSec;
}

// Seconds remaining at the current rate, or null when unknown
// (no total, no rate, or already complete).
export function computeEtaSeconds(d: ActiveDownload, nowRate: number | null): number | null {
  if (nowRate == null || nowRate <= 0) return null;
  const remaining = d.bytesTotalAcrossModel - d.bytesReadTotal;
  if (remaining <= 0) return null;
  return remaining / nowRate;
}
