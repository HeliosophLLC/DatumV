import { proxy, ref, type Snapshot } from 'valtio';
import { api } from '@/api';
import type {
  CatalogManifest,
  CatalogModel,
  CatalogTaskInfo,
  ModelInstallState,
} from '@/api/generated/openapi-client';

// Snapshot<> deep-readonlys nested arrays/objects, which means a function
// signature taking the raw `CatalogManifest` rejects the snapshot value
// from `useSnapshot(modelsState).manifest`. The filter helpers below are
// pure reads, so we type them against the snapshot view explicitly.
export type CatalogManifestSnapshot = Snapshot<CatalogManifest>;
export type CatalogModelSnapshot = Snapshot<CatalogModel>;
export type CatalogTaskInfoSnapshot = Snapshot<CatalogTaskInfo>;

// State for the Models surface. The manifest is fetched once per app
// session (small JSON, doesn't change without a redeploy); install state
// per model is fetched lazily as cards render. Filters live here so the
// view can be a pure function of state.
//
// `ref(manifest)` wraps the deserialised manifest in a Valtio ref so
// nested arrays/objects don't get proxied (cheaper and avoids "you can't
// mutate this readonly array" gotchas on the generated DTOs).

// Family ordering for the faceted task filter UI. The server-side enum
// values arrive as PascalCase strings ("Text" / "Image" / …); the list
// here drives display order on the filter panel so the section headers
// stay stable across catalog reshuffles.
export const TASK_FAMILY_ORDER: readonly string[] = [
  'Multimodal',
  'ComputerVision',
  'NaturalLanguageProcessing',
  'Audio',
  'Tabular',
];

interface ModelsState {
  manifest: CatalogManifest | null;
  // TaskTypeRegistry mirror, fetched alongside the manifest. Used by the
  // filter panel to (a) group task chips by family and (b) translate
  // task-name strings on model cards into family-coloured badges.
  tasks: readonly CatalogTaskInfo[] | null;
  // catalog id → version string currently active on disk (from
  // <DATUMV_MODELS>/<id>/active). Models without an entry here are not
  // installed; treat absence as "no drift to surface". Compared against
  // `manifest.models[i].versions[0].version` to compute drift.
  activeVersions: Readonly<Record<string, string>>;
  // catalog id → list of version folders present under <DATUMV_MODELS>/<id>/.
  // Drives per-version Install / Activate / Delete affordances in the
  // model card's "Previous versions" disclosure. Absence (no entry, or
  // empty array) means no version folders on disk. Refreshed alongside
  // activeVersions after install / uninstall / activate / delete.
  versionsOnDisk: Readonly<Record<string, readonly string[]>>;
  loading: boolean;
  error: string | null;
  // When true, only show models with an installed-but-outdated active
  // version (drift). Driven by the standalone "Updates" toggle next to
  // the search box; auto-resets when the toggle is hidden (no drifts).
  updatesOnly: boolean;
  // Multi-select install-state filter. When non-empty, only models whose
  // current install state appears here match (OR semantics). Driven by
  // the Installed / Downloaded / Partial toggles in the header.
  installStates: ReadonlySet<ModelInstallState>;
  // Free-text search term; matched against id + displayName + summary +
  // description. Empty string = no search filter.
  query: string;
  // Multi-select task chip filter. Each entry is a task name from
  // TaskTypeRegistry (e.g. "TextEmbedder"). When non-empty, a model
  // matches if any of its tasks appear here (OR semantics — picking
  // "ImageCaptioner" and "TextEmbedder" widens the result set).
  selectedTasks: ReadonlySet<string>;
  // Id of the model whose detail pane is showing in the right column.
  // Null = no selection (show the empty/prompt state).
  selectedId: string | null;
  // modelFamily → markdown card body (or null when probed and not
  // present on the server). Populated lazily by `loadFamilyCard`.
  // Absence of a key = not fetched yet; null value = fetched, no card.
  familyCards: Readonly<Record<string, string | null>>;
}

export const modelsState = proxy<ModelsState>({
  manifest: null,
  tasks: null,
  activeVersions: {},
  versionsOnDisk: {},
  loading: false,
  error: null,
  updatesOnly: false,
  installStates: new Set<ModelInstallState>(),
  query: '',
  selectedTasks: new Set<string>(),
  selectedId: null,
  familyCards: {},
});

export async function loadModelsCatalog(): Promise<void> {
  if (modelsState.loading) return;
  if (modelsState.manifest !== null && modelsState.tasks !== null) return; // already cached
  modelsState.loading = true;
  modelsState.error = null;
  try {
    // Parallel fetch — the four endpoints are independent and small.
    const [manifest, tasks, activeVersions, versionsOnDisk] = await Promise.all([
      api.modelCatalog.getManifest(),
      api.modelCatalog.getTasks(),
      api.modelCatalog.getActiveVersions(),
      api.modelCatalog.getOnDiskVersions(),
    ]);
    modelsState.manifest = ref(manifest);
    modelsState.tasks = ref(tasks);
    modelsState.activeVersions = activeVersions;
    modelsState.versionsOnDisk = versionsOnDisk;
  } catch (err) {
    modelsState.error = err instanceof Error ? err.message : String(err);
  } finally {
    modelsState.loading = false;
  }
}

// Refresh the active-version map. Called after an install completes or
// an uninstall lands so the drift badges flip without a manual reload.
// Bypasses the `loading` guard — the manifest + task vocabulary don't
// need re-fetching, only the per-id active pointer changed.
export async function refreshActiveVersions(): Promise<void> {
  try {
    modelsState.activeVersions = await api.modelCatalog.getActiveVersions();
  } catch (err) {
    // Non-fatal: a refresh failure leaves the previous map in place, so
    // the worst case is a stale drift badge until the next reload.
    console.error('[models] refreshActiveVersions failed', err);
  }
}

// Refresh the on-disk-versions map. Called after install / uninstall /
// activate / delete-version so the model card's previous-versions
// disclosure reflects which folders exist without a manual reload.
export async function refreshVersionsOnDisk(): Promise<void> {
  try {
    modelsState.versionsOnDisk = await api.modelCatalog.getOnDiskVersions();
  } catch (err) {
    console.error('[models] refreshVersionsOnDisk failed', err);
  }
}

export function setUpdatesOnly(value: boolean): void {
  modelsState.updatesOnly = value;
}

export function toggleInstallState(state: ModelInstallState): void {
  const next = new Set(modelsState.installStates);
  if (next.has(state)) next.delete(state);
  else next.add(state);
  modelsState.installStates = next;
}

export function setQuery(query: string): void {
  modelsState.query = query;
}

export function setSelectedId(id: string | null): void {
  modelsState.selectedId = id;
}

export function toggleTask(taskName: string): void {
  const next = new Set(modelsState.selectedTasks);
  if (next.has(taskName)) next.delete(taskName);
  else next.add(taskName);
  modelsState.selectedTasks = next;
}

export function clearSelectedTasks(): void {
  if (modelsState.selectedTasks.size === 0) return;
  modelsState.selectedTasks = new Set<string>();
}

export function clearFilters(): void {
  modelsState.updatesOnly = false;
  modelsState.installStates = new Set<ModelInstallState>();
  modelsState.query = '';
  modelsState.selectedTasks = new Set<string>();
}

// Drift = the entry is installed (active pointer exists) AND the active
// version is older than the catalog's newest declared version
// (versions[0]). Warn-only signal — drift never blocks. Models with no
// `versions[]` (defensive null guard for future-proofing) or no active
// entry simply don't surface a badge.
export function isDrifted(
  model: CatalogModelSnapshot,
  activeVersions: Readonly<Record<string, string>>,
): boolean {
  const id = model.id;
  if (!id) return false;
  const active = activeVersions[id];
  if (!active) return false; // not installed → nothing to surface
  const latest = model.versions?.[0]?.version;
  if (!latest) return false;
  return active !== latest;
}

// Count of drifted installs across the whole manifest. Drives the small
// numeric pill on the Models tab chip.
export function driftedCount(
  manifest: CatalogManifestSnapshot | null,
  activeVersions: Readonly<Record<string, string>>,
): number {
  if (manifest === null) return 0;
  let count = 0;
  for (const model of manifest.models ?? []) {
    if (isDrifted(model, activeVersions)) count++;
  }
  return count;
}

// Pure filter — applied at render time. Doesn't mutate state. Caller passes
// the manifest snapshot from useSnapshot. `updatesOnly` is a runtime drift
// comparison against `activeVersions` (not a manifest field).
export function filterModels(
  manifest: CatalogManifestSnapshot,
  updatesOnly: boolean,
  installStates: ReadonlySet<ModelInstallState>,
  installStateMap: Readonly<Record<string, ModelInstallState>> | null,
  query: string,
  selectedTasks: ReadonlySet<string>,
  activeVersions: Readonly<Record<string, string>>,
): readonly CatalogModelSnapshot[] {
  const models = manifest.models ?? [];
  const needle = query.trim().toLowerCase();
  // Case-insensitive task match so manifest typo-mixing (e.g. "textembedder"
  // vs "TextEmbedder") doesn't drop matches. The TS Set is case-sensitive
  // so we lower-case both sides.
  const taskNeedles = selectedTasks.size === 0
    ? null
    : new Set([...selectedTasks].map((t) => t.toLowerCase()));

  return models.filter((m) => {
    if (updatesOnly && !isDrifted(m, activeVersions)) return false;
    if (installStates.size > 0) {
      const id = m.id ?? '';
      const state = installStateMap?.[id];
      if (!state || !installStates.has(state)) return false;
    }
    if (needle.length > 0) {
      const hay = [
        m.id ?? '',
        m.displayName ?? '',
        m.summary ?? '',
        m.description ?? '',
      ].join(' ').toLowerCase();
      if (!hay.includes(needle)) return false;
    }
    if (taskNeedles) {
      const modelTasks = m.tasks ?? [];
      const hit = modelTasks.some((t) => taskNeedles.has(t.toLowerCase()));
      if (!hit) return false;
    }
    return true;
  });
}

// One row in the model browser's list pane. Either a single catalog
// entry (`single`) or a multi-variant family (`family`) — the list
// renders the same way for both, but family rows expand into a variant
// picker inside the detail pane.
export type ModelGroup =
  | { readonly kind: 'single'; readonly entry: CatalogModelSnapshot }
  | {
      readonly kind: 'family';
      readonly family: string;
      readonly entries: readonly CatalogModelSnapshot[];
      readonly lead: CatalogModelSnapshot;
    };

// Buckets the filtered model list by `modelFamily`. Family buckets that
// end up with exactly one surviving entry collapse back to `single`
// rows so the user doesn't see a meaningless "1 variant" pill when a
// filter trims a family down. Each bucket appears at the position of
// its first member so the catalog's authoring order is preserved.
export function groupByModelFamily(
  models: readonly CatalogModelSnapshot[],
): readonly ModelGroup[] {
  type Bucket = { family: string | null; entries: CatalogModelSnapshot[] };
  const ordered: Bucket[] = [];
  const byFamily = new Map<string, Bucket>();
  for (const m of models) {
    const f = m.modelFamily ?? null;
    if (f === null) {
      ordered.push({ family: null, entries: [m] });
      continue;
    }
    let bucket = byFamily.get(f);
    if (!bucket) {
      bucket = { family: f, entries: [m] };
      byFamily.set(f, bucket);
      ordered.push(bucket);
    } else {
      bucket.entries.push(m);
    }
  }
  return ordered.map((b): ModelGroup => {
    if (b.family === null || b.entries.length === 1) {
      return { kind: 'single', entry: b.entries[0] };
    }
    return { kind: 'family', family: b.family, entries: b.entries, lead: b.entries[0] };
  });
}

// Fetch the family card markdown for `family`. Cached in modelsState
// after the first call — the card body doesn't change without a server
// restart. Returns null when the server has no card for this family
// (no entry declared a familyCardFile) or the request failed.
export async function loadFamilyCard(family: string): Promise<string | null> {
  if (family in modelsState.familyCards) {
    return modelsState.familyCards[family];
  }
  try {
    const response = await window.fetch(
      `/api/model-catalog/family-cards/${encodeURIComponent(family)}`,
      { credentials: 'include' },
    );
    if (response.status === 404) {
      modelsState.familyCards = { ...modelsState.familyCards, [family]: null };
      return null;
    }
    if (!response.ok) {
      throw new Error(`family-card fetch failed: ${response.status}`);
    }
    const text = await response.text();
    modelsState.familyCards = { ...modelsState.familyCards, [family]: text };
    return text;
  } catch (err) {
    console.error('[models] loadFamilyCard failed', err);
    return null;
  }
}

// Per-state counts across the manifest — drives the `Installed (N)` etc.
// toggle labels and the "hide Partial when 0" rule. Unknown / missing
// install states (model not yet probed) don't contribute to any bucket.
export function installStateCounts(
  manifest: CatalogManifestSnapshot | null,
  installStateMap: Readonly<Record<string, ModelInstallState>> | null,
): Readonly<Record<ModelInstallState, number>> {
  const counts: Record<ModelInstallState, number> = {
    notDownloaded: 0,
    partial: 0,
    downloaded: 0,
    installed: 0,
  };
  if (manifest === null || installStateMap === null) return counts;
  for (const m of manifest.models ?? []) {
    const id = m.id;
    if (!id) continue;
    const state = installStateMap[id];
    if (state) counts[state]++;
  }
  return counts;
}

// Filter the task vocabulary to entries that are actually referenced by
// at least one model in the manifest. The sidebar uses this so users
// don't see filter chips that would empty the model list when clicked.
// Comparison is case-insensitive to match `filterModels`.
export function tasksWithAssignedModels(
  tasks: readonly CatalogTaskInfoSnapshot[],
  manifest: CatalogManifestSnapshot | null,
): readonly CatalogTaskInfoSnapshot[] {
  if (manifest === null) return [];
  const assigned = new Set<string>();
  for (const m of manifest.models ?? []) {
    for (const name of m.tasks ?? []) assigned.add(name.toLowerCase());
  }
  return tasks.filter((t) => {
    const name = t.name;
    if (!name) return false;
    return assigned.has(name.toLowerCase());
  });
}

// Groups the task vocabulary into family → ordered-task-list buckets for
// the filter panel. Families appear in TASK_FAMILY_ORDER; unknown families
// (added server-side before the front-end catches up) trail at the end so
// nothing is silently dropped.
export function groupTasksByFamily(
  tasks: readonly CatalogTaskInfoSnapshot[],
): readonly { family: string; tasks: readonly CatalogTaskInfoSnapshot[] }[] {
  const buckets = new Map<string, CatalogTaskInfoSnapshot[]>();
  for (const t of tasks) {
    const family = t.family ?? 'Other';
    let bucket = buckets.get(family);
    if (!bucket) {
      bucket = [];
      buckets.set(family, bucket);
    }
    bucket.push(t);
  }
  const ordered: { family: string; tasks: readonly CatalogTaskInfoSnapshot[] }[] = [];
  for (const family of TASK_FAMILY_ORDER) {
    const bucket = buckets.get(family);
    if (bucket) {
      ordered.push({ family, tasks: bucket });
      buckets.delete(family);
    }
  }
  // Any leftover families (server added new ones) tail-appended in
  // insertion order so nothing disappears silently.
  for (const [family, bucket] of buckets) {
    ordered.push({ family, tasks: bucket });
  }
  return ordered;
}
