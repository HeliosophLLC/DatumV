import { proxy, ref, type Snapshot } from 'valtio';
import { api } from '@/api';
import {
  acquireStreamHub,
  onDatasetDownloadComplete,
  onDatasetDownloadFailed,
  onDatasetDownloadProgress,
  onDatasetDownloadStarted,
  onDatasetIngesting,
  onDatasetIngestProgress,
  onDatasetInstalled,
  onDatasetTableIngested,
} from '@/api/hub';
import { openDialog } from '@/state/dialogs';
import { pushSample, type ProgressSample } from '@/state/progressSamples';
import type {
  CatalogTaskInfo,
  DatasetCatalogManifest,
  DatasetEntry,
  DatasetVariant,
  DatasetInstallState,
} from '@/api/generated/openapi-client';

// Snapshot<> deep-readonlys nested arrays/objects; signatures taking the
// raw types reject the snapshot value from useSnapshot. Filter helpers
// take the snapshot view explicitly.
export type DatasetCatalogManifestSnapshot = Snapshot<DatasetCatalogManifest>;
export type DatasetEntrySnapshot = Snapshot<DatasetEntry>;
export type DatasetVariantSnapshot = Snapshot<DatasetVariant>;

// Canonical modality vocabulary, mirroring
// Heliosoph.DatumV.DatasetLibrary.ModalityRegistry on the backend. Display
// order matches HuggingFace's dataset facet — the sidebar renders
// modalities in this order so authors familiar with HF land without
// re-learning the layout. The actual label per modality lives in
// i18n (`datasets.modality.*`).
export const MODALITY_ORDER: readonly string[] = [
  'Image',
  'Text',
  'Audio',
  'Video',
  'Tabular',
  '3D',
  'Geospatial',
  'Document',
  'TimeSeries',
];

// Kick off the hub connection at module load so server-pushed install
// progress events have a connected client to land on.
void acquireStreamHub().catch(() => {
  // Connection failure surfaces later when an actual operation runs.
});

// In-flight install metadata for one variant. Populated optimistically
// when InstallAsync is queued, refined by SignalR events, cleared on
// terminal success / failure. The discriminated `phase` drives which
// label the card surfaces.
export interface ActiveDatasetInstall {
  // Variant id (the install handle the server keys on). Hub events
  // carry this as `datasetId` for wire compatibility with the existing
  // event records.
  datasetId: string;
  phase: 'starting' | 'downloading' | 'ingesting';
  bytesReadTotal: number;
  bytesTotalAcrossDataset: number;
  fileIndex: number;
  fileCount: number;
  currentFile: string;
  currentTable: string;
  jobIndex: number;
  jobCount: number;
  // Live row count from the in-flight ingest job (current table only).
  // Reset to 0 when a new DatasetIngesting event arrives for the next job.
  // Undefined while downloading / starting.
  rowsWrittenSoFar?: number;
  startedAt: number;
  samples: readonly ProgressSample[];
}

interface DatasetsState {
  manifest: DatasetCatalogManifest | null;
  // Task vocabulary fetched from the model-catalog endpoint. Both the
  // Models and Datasets surfaces lean on the same TaskTypeRegistry — the
  // models tab uses it for the task-filter sidebar, the datasets tab
  // uses it to look up the family for each entry's `suitableForTasks`
  // (drives the left-border accent on the task chips). One fetch per
  // session; cached in a Valtio ref so nested array reads don't proxy.
  tasks: readonly CatalogTaskInfo[] | null;
  // variantId → install state. Bulk-fetched on first load.
  installStates: Record<string, DatasetInstallState> | null;
  // variantId → in-flight install metadata.
  active: Record<string, ActiveDatasetInstall>;
  // variantId → last error string (cleared on next attempt).
  errors: Record<string, string>;
  // variantId → total bytes in *.part files under the raw cache.
  partials: Record<string, number>;
  loading: boolean;
  error: string | null;
  // Free-text search; matches across entry name, summary, description,
  // tags, and any variant displayName/summary.
  query: string;
  // Multi-select modality filter (OR semantics — picking Image + Text
  // widens the result set). Mirrors HuggingFace's facet behavior.
  selectedModalities: ReadonlySet<string>;
  // Multi-select "suitable for task" filter, also OR semantics. Mirrors
  // the model side's task filter — picking ObjectDetector + Segmenter
  // surfaces every dataset that lists either as suitable.
  selectedTasks: ReadonlySet<string>;
  // Name of the entry whose detail pane is showing. Null = no selection.
  selectedEntryName: string | null;
  // Id of the active variant within the selected entry's detail pane.
  // Null = "use the entry's first variant" — the detail pane handles
  // the fallback so first-render with a freshly-selected entry doesn't
  // have to wait on the user picking a tab.
  selectedVariantId: string | null;
  // entry name → markdown card body (or null when probed and not
  // present on the server). Populated lazily by `loadEntryCard`.
  entryCards: Readonly<Record<string, string | null>>;
}

export const datasetsState = proxy<DatasetsState>({
  manifest: null,
  tasks: null,
  installStates: null,
  active: {},
  errors: {},
  partials: {},
  loading: false,
  error: null,
  query: '',
  selectedModalities: new Set<string>(),
  selectedTasks: new Set<string>(),
  selectedEntryName: null,
  selectedVariantId: null,
  entryCards: {},
});

export async function loadDatasetsCatalog(): Promise<void> {
  if (datasetsState.loading) return;
  if (datasetsState.manifest !== null && datasetsState.installStates !== null) return;
  datasetsState.loading = true;
  datasetsState.error = null;
  try {
    // Task vocabulary comes from /api/model-catalog/tasks — same
    // TaskTypeRegistry both surfaces consult. Fetch it alongside the
    // dataset manifest so the suitableForTasks chips can resolve their
    // family accent without the user having to visit the Models tab
    // first.
    const [manifest, states, partials, tasks] = await Promise.all([
      api.datasetCatalog.getManifest(),
      api.datasetCatalog.getStates(),
      api.datasetCatalog.getAllPartialBytes(),
      api.modelCatalog.getTasks(),
    ]);
    datasetsState.manifest = ref(manifest);
    datasetsState.tasks = ref(tasks);
    datasetsState.installStates = { ...states };
    datasetsState.partials = { ...partials };
  } catch (err) {
    datasetsState.error = err instanceof Error ? err.message : String(err);
  } finally {
    datasetsState.loading = false;
  }
}

export async function refreshInstallStates(): Promise<void> {
  try {
    const states = await api.datasetCatalog.getStates();
    datasetsState.installStates = { ...states };
  } catch (err) {
    console.error('[datasets] refreshInstallStates failed', err);
  }
}

export async function refreshPartials(): Promise<void> {
  try {
    const partials = await api.datasetCatalog.getAllPartialBytes();
    datasetsState.partials = { ...partials };
  } catch (err) {
    console.error('[datasets] refreshPartials failed', err);
  }
}

export function setQuery(query: string): void {
  datasetsState.query = query;
}

export function toggleModality(modality: string): void {
  const next = new Set(datasetsState.selectedModalities);
  if (next.has(modality)) next.delete(modality);
  else next.add(modality);
  datasetsState.selectedModalities = next;
}

export function clearModalities(): void {
  if (datasetsState.selectedModalities.size === 0) return;
  datasetsState.selectedModalities = new Set<string>();
}

export function toggleTask(task: string): void {
  const next = new Set(datasetsState.selectedTasks);
  if (next.has(task)) next.delete(task);
  else next.add(task);
  datasetsState.selectedTasks = next;
}

export function clearTasks(): void {
  if (datasetsState.selectedTasks.size === 0) return;
  datasetsState.selectedTasks = new Set<string>();
}

export function clearFilters(): void {
  datasetsState.query = '';
  if (datasetsState.selectedModalities.size > 0) {
    datasetsState.selectedModalities = new Set<string>();
  }
  if (datasetsState.selectedTasks.size > 0) {
    datasetsState.selectedTasks = new Set<string>();
  }
}

// Select an entry. Resets the variant slot so the detail renders the
// entry's first variant by default — the user can explicitly pick a tab
// afterwards via `setSelectedVariantId`.
export function setSelectedEntry(name: string | null): void {
  datasetsState.selectedEntryName = name;
  datasetsState.selectedVariantId = null;
}

export function setSelectedVariantId(variantId: string | null): void {
  datasetsState.selectedVariantId = variantId;
}

export function dismissError(variantId: string): void {
  delete datasetsState.errors[variantId];
}

// Forget the in-flight install entry for a variant. Used by the status
// chip's stalled-row dismiss button so the user can hide a hung install
// from the chip without waiting for the server to time out. Mirrors
// `dismissActiveDownload` on the model side.
export function dismissActiveInstall(variantId: string): void {
  delete datasetsState.active[variantId];
}

// Optimistic install of a variant. License-required (412) retries
// through the same flow as the model side. The dataset (entry-level)
// display name is passed through so the license dialog can name the
// dataset rather than the raw variant id.
export async function installVariant(
  variantId: string,
  datasetDisplayName?: string,
): Promise<void> {
  delete datasetsState.errors[variantId];

  datasetsState.active[variantId] = {
    datasetId: variantId,
    phase: 'starting',
    bytesReadTotal: 0,
    bytesTotalAcrossDataset: 0,
    fileIndex: 0,
    fileCount: 0,
    currentFile: '',
    currentTable: '',
    jobIndex: 0,
    jobCount: 0,
    startedAt: Date.now(),
    samples: [],
  };

  try {
    await acquireStreamHub();
    await api.datasetCatalog.install(variantId);
  } catch (err) {
    delete datasetsState.active[variantId];
    console.error('[datasets] install failed', variantId, err);
    const licenseId = readLicenseRequired(err);
    if (licenseId) {
      await handleLicenseRequired(variantId, licenseId, datasetDisplayName);
      return;
    }
    datasetsState.errors[variantId] = describeError(err);
  }
}

async function handleLicenseRequired(
  variantId: string,
  licenseId: string,
  datasetDisplayName: string | undefined,
): Promise<void> {
  const { result } = openDialog<{ accepted: boolean }>({
    kind: 'confirmLicense',
    payload: {
      licenseId,
      modelDisplayName: datasetDisplayName ?? '',
    },
  });
  const decision = await result;
  if (!decision?.accepted) {
    datasetsState.errors[variantId] = 'License declined';
    return;
  }
  try {
    await api.datasetCatalog.acceptLicense(licenseId);
  } catch (err) {
    console.error('[datasets] acceptLicense failed', licenseId, err);
    datasetsState.errors[variantId] = describeError(err);
    return;
  }
  await installVariant(variantId, datasetDisplayName);
}

export async function uninstallVariant(variantId: string): Promise<void> {
  try {
    await api.datasetCatalog.uninstall(variantId);
    if (datasetsState.installStates) {
      datasetsState.installStates[variantId] = 'notDownloaded';
    }
    delete datasetsState.errors[variantId];
  } catch (err) {
    console.error('[datasets] uninstall failed', variantId, err);
    datasetsState.errors[variantId] = describeError(err);
  }
}

// Pure filter — applied at render time. Combines the free-text search
// with the modality multi-select. The free-text search matches across
// entry-level fields plus any variant's display name / summary so a
// query like "test2017" surfaces the parent COCO 2017 entry. The
// modality filter is OR — picking Image + Text widens the result set,
// matching HuggingFace's facet behavior.
export function filterEntries(
  manifest: DatasetCatalogManifestSnapshot,
  query: string,
  selectedModalities: ReadonlySet<string>,
  selectedTasks: ReadonlySet<string>,
): readonly DatasetEntrySnapshot[] {
  const entries = manifest.datasets ?? [];
  const needle = query.trim().toLowerCase();
  // Modality + task matching is case-insensitive so a manifest with
  // mixed casing (lower-cased "image", or "objectdetector") still
  // matches a sidebar chip with the canonical PascalCase form.
  const modalityNeedles = selectedModalities.size === 0
    ? null
    : new Set([...selectedModalities].map((m) => m.toLowerCase()));
  const taskNeedles = selectedTasks.size === 0
    ? null
    : new Set([...selectedTasks].map((t) => t.toLowerCase()));
  return entries.filter((e) => {
    if (modalityNeedles) {
      const entryModalities = (e.modalities ?? []).map((m) => m.toLowerCase());
      if (!entryModalities.some((m) => modalityNeedles.has(m))) return false;
    }
    if (taskNeedles) {
      const entryTasks = (e.suitableForTasks ?? []).map((t) => t.toLowerCase());
      if (!entryTasks.some((t) => taskNeedles.has(t))) return false;
    }
    if (needle.length === 0) return true;
    const hay = [
      e.name ?? '',
      e.summary ?? '',
      e.description ?? '',
      ...(e.modalities ?? []),
      ...(e.suitableForTasks ?? []),
      ...(e.variants ?? []).flatMap((v) => [
        v.id ?? '',
        v.displayName ?? '',
        v.summary ?? '',
      ]),
    ]
      .join(' ')
      .toLowerCase();
    return hay.includes(needle);
  });
}

// Modalities that at least one entry in the manifest declares, in
// canonical order. Used by the sidebar so chips for empty modalities
// don't render and confuse the user — clicking them would always
// empty the list. Mirrors `tasksWithAssignedModels` on the model side.
export function modalitiesInManifest(
  manifest: DatasetCatalogManifestSnapshot | null,
): readonly string[] {
  if (manifest === null) return [];
  const declared = new Set<string>();
  for (const e of manifest.datasets ?? []) {
    for (const m of e.modalities ?? []) declared.add(m.toLowerCase());
  }
  return MODALITY_ORDER.filter((m) => declared.has(m.toLowerCase()));
}

// Per-modality count of entries across the manifest. Drives the small
// badge after each modality label in the sidebar.
export function modalityCounts(
  manifest: DatasetCatalogManifestSnapshot | null,
): Readonly<Record<string, number>> {
  const counts: Record<string, number> = {};
  if (manifest === null) return counts;
  for (const e of manifest.datasets ?? []) {
    for (const m of e.modalities ?? []) {
      counts[m] = (counts[m] ?? 0) + 1;
    }
  }
  return counts;
}

// Filters the task vocabulary to those at least one entry declares as
// `suitableForTasks`. Mirrors the model side's `tasksWithAssignedModels`
// — keeps the sidebar from showing chips that would empty the list
// when clicked.
export function tasksDeclaredByEntries(
  tasks: readonly CatalogTaskInfo[] | null,
  manifest: DatasetCatalogManifestSnapshot | null,
): readonly CatalogTaskInfo[] {
  if (tasks === null || manifest === null) return [];
  const declared = new Set<string>();
  for (const e of manifest.datasets ?? []) {
    for (const t of e.suitableForTasks ?? []) declared.add(t.toLowerCase());
  }
  return tasks.filter((t) => {
    const name = t.name;
    if (!name) return false;
    return declared.has(name.toLowerCase());
  });
}

// Per-task count of entries across the manifest. Drives the badge
// after each task label in the sidebar.
export function taskCounts(
  manifest: DatasetCatalogManifestSnapshot | null,
): Readonly<Record<string, number>> {
  const counts: Record<string, number> = {};
  if (manifest === null) return counts;
  for (const e of manifest.datasets ?? []) {
    for (const t of e.suitableForTasks ?? []) {
      counts[t] = (counts[t] ?? 0) + 1;
    }
  }
  return counts;
}

// Fetch the entry's card markdown. Cached after the first call. Returns
// null when the server has no card for this entry or the request fails.
export async function loadEntryCard(entryName: string): Promise<string | null> {
  if (entryName in datasetsState.entryCards) {
    return datasetsState.entryCards[entryName];
  }
  try {
    const response = await window.fetch(
      `/api/dataset-catalog/entries/${encodeURIComponent(entryName)}/card`,
      { credentials: 'include' },
    );
    if (response.status === 404) {
      datasetsState.entryCards = { ...datasetsState.entryCards, [entryName]: null };
      return null;
    }
    if (!response.ok) {
      throw new Error(`entry-card fetch failed: ${response.status}`);
    }
    const text = await response.text();
    datasetsState.entryCards = { ...datasetsState.entryCards, [entryName]: text };
    return text;
  } catch (err) {
    console.error('[datasets] loadEntryCard failed', err);
    return null;
  }
}

// URL helpers — the renderer constructs these inline.
export function heroImageUrl(entryName: string): string {
  return `/api/dataset-catalog/entries/${encodeURIComponent(entryName)}/hero`;
}

export function entryCardAssetUrl(entryName: string, relativePath: string): string {
  const clean = relativePath.replace(/^\.\//, '');
  return `/api/dataset-catalog/entries/${encodeURIComponent(entryName)}/card/assets/${clean}`;
}

// Resolves the currently-active variant for the user's entry selection.
// When no variant is explicitly picked, falls back to the entry's first
// variant. Used by the detail pane + install buttons so the active-tab
// fallback lives in one place.
export function resolveActiveVariant(
  entry: DatasetEntrySnapshot,
  selectedVariantId: string | null,
): DatasetVariantSnapshot | null {
  const variants = entry.variants ?? [];
  if (variants.length === 0) return null;
  if (selectedVariantId !== null) {
    const match = variants.find((v) => v.id === selectedVariantId);
    if (match) return match;
  }
  return variants[0];
}

// Mirror of the same helpers in state/downloads.ts. Duplicating rather
// than lifting them into a shared module — the two consumers don't share
// any other code.
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

function readLicenseRequired(err: unknown): string | null {
  if (!err || typeof err !== 'object') return null;
  const o = err as Record<string, unknown>;
  if (o.error === 'license_not_accepted' && typeof o.licenseId === 'string') {
    return o.licenseId;
  }
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

onDatasetDownloadStarted((event) => {
  const existing = datasetsState.active[event.datasetId];
  datasetsState.active[event.datasetId] = {
    datasetId: event.datasetId,
    phase: 'downloading',
    bytesReadTotal: 0,
    bytesTotalAcrossDataset: event.totalBytes ?? 0,
    fileIndex: 0,
    fileCount: event.fileCount ?? 0,
    currentFile: '',
    currentTable: '',
    jobIndex: 0,
    jobCount: 0,
    startedAt: existing?.startedAt ?? Date.now(),
    samples: existing?.samples ?? [],
  };
});

onDatasetDownloadProgress((event) => {
  const existing = datasetsState.active[event.datasetId];
  const nextBytes = event.bytesReadTotal ?? existing?.bytesReadTotal ?? 0;
  datasetsState.active[event.datasetId] = {
    datasetId: event.datasetId,
    phase: 'downloading',
    bytesReadTotal: nextBytes,
    bytesTotalAcrossDataset:
      event.bytesTotalAcrossDataset ?? existing?.bytesTotalAcrossDataset ?? 0,
    fileIndex: event.fileIndex ?? existing?.fileIndex ?? 0,
    fileCount: event.fileCount ?? existing?.fileCount ?? 0,
    currentFile: event.currentFile ?? existing?.currentFile ?? '',
    currentTable: existing?.currentTable ?? '',
    jobIndex: existing?.jobIndex ?? 0,
    jobCount: existing?.jobCount ?? 0,
    startedAt: existing?.startedAt ?? Date.now(),
    samples: pushSample(existing?.samples, nextBytes),
  };
});

onDatasetDownloadComplete((event) => {
  const existing = datasetsState.active[event.datasetId];
  if (existing) {
    datasetsState.active[event.datasetId] = { ...existing, phase: 'ingesting' };
  }
  delete datasetsState.partials[event.datasetId];
});

onDatasetIngesting((event) => {
  const existing = datasetsState.active[event.datasetId];
  datasetsState.active[event.datasetId] = {
    datasetId: event.datasetId,
    phase: 'ingesting',
    bytesReadTotal: existing?.bytesReadTotal ?? 0,
    bytesTotalAcrossDataset: existing?.bytesTotalAcrossDataset ?? 0,
    fileIndex: existing?.fileIndex ?? 0,
    fileCount: existing?.fileCount ?? 0,
    currentFile: existing?.currentFile ?? '',
    currentTable: event.currentTable ?? '',
    jobIndex: event.jobIndex ?? 0,
    jobCount: event.jobCount ?? 0,
    // Per-table counter — reset whenever the server moves to the next job.
    rowsWrittenSoFar: 0,
    startedAt: existing?.startedAt ?? Date.now(),
    samples: existing?.samples ?? [],
  };
});

onDatasetIngestProgress((event) => {
  const existing = datasetsState.active[event.datasetId];
  if (!existing) return;
  datasetsState.active[event.datasetId] = {
    ...existing,
    rowsWrittenSoFar: event.rowsWrittenSoFar ?? existing.rowsWrittenSoFar ?? 0,
  };
});

onDatasetTableIngested((event) => {
  void event;
});

onDatasetInstalled((event) => {
  delete datasetsState.active[event.datasetId];
  delete datasetsState.errors[event.datasetId];
  if (datasetsState.installStates) {
    datasetsState.installStates[event.datasetId] = 'installed';
  }
  if (window.electronHost?.isElectron) {
    void window.electronHost.notify({
      title: 'Dataset installed',
      body: `${event.datasetId} is ready`,
    });
  }
});

onDatasetDownloadFailed((event) => {
  delete datasetsState.active[event.datasetId];
  datasetsState.errors[event.datasetId] = event.error ?? 'Install failed';
  void refreshPartials();
});
