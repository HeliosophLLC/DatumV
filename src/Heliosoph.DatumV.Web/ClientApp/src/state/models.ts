import { proxy, ref, type Snapshot } from 'valtio';
import { api } from '@/api';
import type {
  CatalogEntry,
  CatalogManifest,
  CatalogTaskInfo,
  CatalogVariant,
  ModelInstallState,
} from '@/api/generated/openapi-client';

// Snapshot<> deep-readonlys nested arrays/objects, which means a function
// signature taking the raw `CatalogManifest` rejects the snapshot value
// from `useSnapshot(modelsState).manifest`. The filter helpers below are
// pure reads, so we type them against the snapshot view explicitly.
export type CatalogManifestSnapshot = Snapshot<CatalogManifest>;
export type CatalogEntrySnapshot = Snapshot<CatalogEntry>;
export type CatalogVariantSnapshot = Snapshot<CatalogVariant>;
export type CatalogTaskInfoSnapshot = Snapshot<CatalogTaskInfo>;

// Family ordering for the faceted task filter UI.
export const TASK_FAMILY_ORDER: readonly string[] = [
  'Multimodal',
  'ComputerVision',
  'NaturalLanguageProcessing',
  'Audio',
  'Tabular',
];

interface ModelsState {
  manifest: CatalogManifest | null;
  tasks: readonly CatalogTaskInfo[] | null;
  // variantId → version string currently active on disk. Compared
  // against variant.versions[0].version to compute drift.
  activeVersions: Readonly<Record<string, string>>;
  // variantId → list of version folders present under <DATUMV_MODELS>/<variantId>/.
  versionsOnDisk: Readonly<Record<string, readonly string[]>>;
  loading: boolean;
  error: string | null;
  updatesOnly: boolean;
  installStates: ReadonlySet<ModelInstallState>;
  query: string;
  selectedTasks: ReadonlySet<string>;
  // Name of the entry whose detail pane is showing in the right column.
  selectedEntryName: string | null;
  // Variant id of the variant active within the selected entry's detail
  // pane. When the entry has multiple variants, this drives the variant
  // tab strip; for singleton entries this is just `entry.variants[0].id`.
  selectedVariantId: string | null;
  // entryName → markdown card body (or null when probed and not present
  // on the server). Populated lazily by `loadEntryCard`.
  entryCards: Readonly<Record<string, string | null>>;
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
  selectedEntryName: null,
  selectedVariantId: null,
  entryCards: {},
});

export async function loadModelsCatalog(): Promise<void> {
  if (modelsState.loading) return;
  if (modelsState.manifest !== null && modelsState.tasks !== null) return;
  modelsState.loading = true;
  modelsState.error = null;
  try {
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

export async function refreshActiveVersions(): Promise<void> {
  try {
    modelsState.activeVersions = await api.modelCatalog.getActiveVersions();
  } catch (err) {
    console.error('[models] refreshActiveVersions failed', err);
  }
}

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

export function setSelectedEntry(
  entryName: string | null,
  variantId: string | null = null,
): void {
  modelsState.selectedEntryName = entryName;
  modelsState.selectedVariantId = variantId;
}

export function setSelectedVariant(variantId: string): void {
  modelsState.selectedVariantId = variantId;
}

// Look up an entry/variant by a SQL identifier (e.g. `yolox_s`,
// `florence2_caption`). Used by the LS hover "open in Models tab" jump:
// the LS knows the bare identifier; we walk the manifest to find which
// variant declared it. Selects nothing when the identifier isn't in the
// catalog (e.g. user-defined model).
export function selectEntryByIdentifier(identifier: string): void {
  const m = modelsState.manifest;
  if (m === null) return;
  for (const entry of m.entries ?? []) {
    for (const variant of entry.variants ?? []) {
      for (const version of variant.versions ?? []) {
        for (const decl of version.models ?? []) {
          if (decl.identifier === identifier) {
            setSelectedEntry(entry.name ?? null, variant.id ?? null);
            return;
          }
        }
      }
    }
  }
}

/** Select the catalog entry named `entryName` (defaulting to its first
 *  variant) so its detail pane renders in the right column. No-op when the
 *  name isn't in the loaded manifest. Used by in-card links that point at
 *  a sibling model card — see `resolveCardEntryLink`. */
export function openModelEntry(entryName: string): void {
  const m = modelsState.manifest;
  if (m === null) return;
  const entry = (m.entries ?? []).find((e) => e.name === entryName);
  if (!entry) return;
  setSelectedEntry(entry.name ?? null, entry.variants?.[0]?.id ?? null);
}

/**
 * Resolve a markdown link `href` written inside the card whose source path
 * is `fromCardFile` (the linking entry's `cardFile`, e.g.
 * `cards/dpt-large/index.md`) that points at a *sibling model card*. Card
 * authors link by folder slug — `../depth-anything-v2/index.md` — which is
 * distinct from the entry's display `name`, so resolution walks the link
 * against the linking card's directory (honouring `.`/`..`) and matches
 * the result against every entry's `cardFile`. Returns the target entry's
 * `name` (the key `openModelEntry` / `setSelectedEntry` expect), or null
 * for self-links, external/anchor links, non-card paths (e.g. image
 * assets), and links that don't resolve to a known entry's card.
 */
export function resolveCardEntryLink(
  fromCardFile: string | undefined,
  href: string,
): string | null {
  if (!fromCardFile || href.length === 0) return null;
  if (/^[a-z][a-z0-9+.-]*:/i.test(href)) return null; // external protocol
  if (href.startsWith('#')) return null; // same-page anchor
  const hashIdx = href.indexOf('#');
  const rawPath = (hashIdx < 0 ? href : href.slice(0, hashIdx)).replace(/\\/g, '/');
  const fromNorm = fromCardFile.replace(/\\/g, '/');
  // Resolve the link against the linking card's directory.
  const stack = fromNorm.split('/').slice(0, -1);
  for (const seg of rawPath.split('/')) {
    if (seg === '' || seg === '.') continue;
    if (seg === '..') {
      if (stack.length === 0) return null;
      stack.pop();
      continue;
    }
    stack.push(seg);
  }
  const resolved = stack.join('/');
  if (resolved === fromNorm) return null; // self-link
  const manifest = modelsState.manifest;
  if (manifest === null) return null;
  const target = (manifest.entries ?? []).find(
    (e) => (e.cardFile ?? '').replace(/\\/g, '/') === resolved,
  );
  return target?.name ?? null;
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

// Drift on a single variant: the variant is installed (active pointer
// exists) AND active version trails versions[0]. Same semantics as the
// pre-fold drift check but keyed on variant.id.
export function isDriftedVariant(
  variant: CatalogVariantSnapshot,
  activeVersions: Readonly<Record<string, string>>,
): boolean {
  const id = variant.id;
  if (!id) return false;
  const active = activeVersions[id];
  if (!active) return false;
  const latest = variant.versions?.[0]?.version;
  if (!latest) return false;
  return active !== latest;
}

// An entry drifts when any of its variants is drifted. Powers the row-
// level "update available" badge.
export function isDriftedEntry(
  entry: CatalogEntrySnapshot,
  activeVersions: Readonly<Record<string, string>>,
): boolean {
  for (const v of entry.variants ?? []) {
    if (isDriftedVariant(v, activeVersions)) return true;
  }
  return false;
}

// Count drifted variants across the whole manifest. Drives the small
// numeric pill on the Models tab chip.
export function driftedCount(
  manifest: CatalogManifestSnapshot | null,
  activeVersions: Readonly<Record<string, string>>,
): number {
  if (manifest === null) return 0;
  let count = 0;
  for (const entry of manifest.entries ?? []) {
    for (const v of entry.variants ?? []) {
      if (isDriftedVariant(v, activeVersions)) count++;
    }
  }
  return count;
}

// Pure filter — applied at render time. An entry passes when it matches
// the entry-level criteria (tasks, query) AND at least one of its
// variants matches the variant-level criteria (install state, drift).
export function filterEntries(
  manifest: CatalogManifestSnapshot,
  updatesOnly: boolean,
  installStates: ReadonlySet<ModelInstallState>,
  installStateMap: Readonly<Record<string, ModelInstallState>> | null,
  query: string,
  selectedTasks: ReadonlySet<string>,
  activeVersions: Readonly<Record<string, string>>,
): readonly CatalogEntrySnapshot[] {
  const entries = manifest.entries ?? [];
  const needle = query.trim().toLowerCase();
  const taskNeedles = selectedTasks.size === 0
    ? null
    : new Set([...selectedTasks].map((t) => t.toLowerCase()));

  return entries.filter((e) => {
    if (taskNeedles) {
      const entryTasks = e.tasks ?? [];
      const hit = entryTasks.some((t) => taskNeedles.has(t.toLowerCase()));
      if (!hit) return false;
    }
    if (needle.length > 0) {
      const hay = [
        e.name ?? '',
        e.summary ?? '',
        e.description ?? '',
        ...((e.variants ?? []).map((v) => v.id ?? '')),
        ...((e.variants ?? []).map((v) => v.displayName ?? '')),
      ].join(' ').toLowerCase();
      if (!hay.includes(needle)) return false;
    }
    // Variant-level filters: at least one variant must match.
    if (updatesOnly) {
      const anyDrifted = (e.variants ?? []).some(
        (v) => isDriftedVariant(v, activeVersions),
      );
      if (!anyDrifted) return false;
    }
    if (installStates.size > 0) {
      const anyMatch = (e.variants ?? []).some((v) => {
        const s = installStateMap?.[v.id ?? ''];
        return s && installStates.has(s);
      });
      if (!anyMatch) return false;
    }
    return true;
  });
}

// Fetch the entry card markdown for `entryName`. Cached after the first
// call. Returns null when the server has no card for this entry.
export async function loadEntryCard(entryName: string): Promise<string | null> {
  if (entryName in modelsState.entryCards) {
    return modelsState.entryCards[entryName];
  }
  try {
    const response = await window.fetch(
      `/api/model-catalog/entries/${encodeURIComponent(entryName)}/card`,
      { credentials: 'include' },
    );
    if (response.status === 404) {
      modelsState.entryCards = { ...modelsState.entryCards, [entryName]: null };
      return null;
    }
    if (!response.ok) {
      throw new Error(`entry-card fetch failed: ${response.status}`);
    }
    const text = await response.text();
    modelsState.entryCards = { ...modelsState.entryCards, [entryName]: text };
    return text;
  } catch (err) {
    console.error('[models] loadEntryCard failed', err);
    return null;
  }
}

// Per-state counts across the manifest, summed over all variants.
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
  for (const e of manifest.entries ?? []) {
    for (const v of e.variants ?? []) {
      const id = v.id;
      if (!id) continue;
      const state = installStateMap[id];
      if (state) counts[state]++;
    }
  }
  return counts;
}

// Filter the task vocabulary to entries that are actually referenced by
// at least one entry in the manifest. Tasks live at entry level now.
export function tasksWithAssignedModels(
  tasks: readonly CatalogTaskInfoSnapshot[],
  manifest: CatalogManifestSnapshot | null,
): readonly CatalogTaskInfoSnapshot[] {
  if (manifest === null) return [];
  const assigned = new Set<string>();
  for (const e of manifest.entries ?? []) {
    for (const name of e.tasks ?? []) assigned.add(name.toLowerCase());
  }
  return tasks.filter((t) => {
    const name = t.name;
    if (!name) return false;
    return assigned.has(name.toLowerCase());
  });
}

// Groups the task vocabulary into family → ordered-task-list buckets.
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
  for (const [family, bucket] of buckets) {
    ordered.push({ family, tasks: bucket });
  }
  return ordered;
}

// Aggregate install state across an entry's variants. Priority mirrors
// the single-row icon precedence used previously: in-flight > installed
// > downloaded > partial > nothing.
export interface EntryAggregateStatus {
  anyInstalling: boolean;
  anyDownloading: boolean;
  anyInstalled: boolean;
  anyDownloaded: boolean;
  anyPartial: boolean;
  anyDrifted: boolean;
}

export function aggregateEntryStatus(
  entry: CatalogEntrySnapshot,
  installStateMap: Readonly<Record<string, ModelInstallState>> | null,
  activeDownloads: Readonly<Record<string, unknown>>,
  installing: Readonly<Record<string, true>>,
  activeVersions: Readonly<Record<string, string>>,
): EntryAggregateStatus {
  const out: EntryAggregateStatus = {
    anyInstalling: false,
    anyDownloading: false,
    anyInstalled: false,
    anyDownloaded: false,
    anyPartial: false,
    anyDrifted: false,
  };
  for (const v of entry.variants ?? []) {
    const id = v.id ?? '';
    if (activeDownloads[id]) out.anyDownloading = true;
    if (installing[id] === true) out.anyInstalling = true;
    const s = installStateMap?.[id];
    if (s === 'installed') out.anyInstalled = true;
    else if (s === 'downloaded') out.anyDownloaded = true;
    else if (s === 'partial') out.anyPartial = true;
    if (isDriftedVariant(v, activeVersions)) out.anyDrifted = true;
  }
  return out;
}
