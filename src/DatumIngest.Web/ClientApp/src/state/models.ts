import { proxy, ref, type Snapshot } from 'valtio';
import { api } from '@/api';
import type {
  CatalogManifest,
  CatalogModel,
  CatalogTaskInfo,
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

export type TierFilter = 'all' | 'starter' | 'recommended' | 'updates';

// Family ordering for the faceted task filter UI. The server-side enum
// values arrive as PascalCase strings ("Text" / "Image" / …); the list
// here drives display order on the filter panel so the section headers
// stay stable across catalog reshuffles.
export const TASK_FAMILY_ORDER: readonly string[] = [
  'Text',
  'Image',
  'Audio',
  'Video',
  'Multimodal',
  'Structured',
];

interface ModelsState {
  manifest: CatalogManifest | null;
  // TaskTypeRegistry mirror, fetched alongside the manifest. Used by the
  // filter panel to (a) group task chips by family and (b) translate
  // task-name strings on model cards into family-coloured badges.
  tasks: readonly CatalogTaskInfo[] | null;
  // catalog id → version string currently active on disk (from
  // <DATUM_MODELS>/<id>/active). Models without an entry here are not
  // installed; treat absence as "no drift to surface". Compared against
  // `manifest.models[i].versions[0].version` to compute drift.
  activeVersions: Readonly<Record<string, string>>;
  loading: boolean;
  error: string | null;
  tier: TierFilter;
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
}

export const modelsState = proxy<ModelsState>({
  manifest: null,
  tasks: null,
  activeVersions: {},
  loading: false,
  error: null,
  tier: 'all',
  query: '',
  selectedTasks: new Set<string>(),
  selectedId: null,
});

export async function loadModelsCatalog(): Promise<void> {
  if (modelsState.loading) return;
  if (modelsState.manifest !== null && modelsState.tasks !== null) return; // already cached
  modelsState.loading = true;
  modelsState.error = null;
  try {
    // Parallel fetch — the three endpoints are independent and small.
    const [manifest, tasks, activeVersions] = await Promise.all([
      api.modelCatalog.getManifest(),
      api.modelCatalog.getTasks(),
      api.modelCatalog.getActiveVersions(),
    ]);
    modelsState.manifest = ref(manifest);
    modelsState.tasks = ref(tasks);
    modelsState.activeVersions = activeVersions;
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

export function setTier(tier: TierFilter): void {
  modelsState.tier = tier;
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
  modelsState.tier = 'all';
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
// the manifest snapshot from useSnapshot. The `updates` tier is special:
// it isn't a manifest.tiers entry but a runtime drift comparison against
// `activeVersions`, so it short-circuits the tier-id intersection below.
export function filterModels(
  manifest: CatalogManifestSnapshot,
  tier: TierFilter,
  query: string,
  selectedTasks: ReadonlySet<string>,
  activeVersions: Readonly<Record<string, string>>,
): readonly CatalogModelSnapshot[] {
  const models = manifest.models ?? [];
  const tierIds = tier === 'all' || tier === 'updates'
    ? null
    : new Set(manifest.tiers?.[tier] ?? []);
  const needle = query.trim().toLowerCase();
  // Case-insensitive task match so manifest typo-mixing (e.g. "textembedder"
  // vs "TextEmbedder") doesn't drop matches. The TS Set is case-sensitive
  // so we lower-case both sides.
  const taskNeedles = selectedTasks.size === 0
    ? null
    : new Set([...selectedTasks].map((t) => t.toLowerCase()));

  return models.filter((m) => {
    if (tierIds && !tierIds.has(m.id ?? '')) return false;
    if (tier === 'updates' && !isDrifted(m, activeVersions)) return false;
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
