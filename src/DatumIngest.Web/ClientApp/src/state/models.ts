import { proxy, ref, type Snapshot } from 'valtio';
import { api } from '@/api';
import type {
  CatalogManifest,
  CatalogModel,
} from '@/api/generated/openapi-client';

// Snapshot<> deep-readonlys nested arrays/objects, which means a function
// signature taking the raw `CatalogManifest` rejects the snapshot value
// from `useSnapshot(modelsState).manifest`. The filter helpers below are
// pure reads, so we type them against the snapshot view explicitly.
export type CatalogManifestSnapshot = Snapshot<CatalogManifest>;
export type CatalogModelSnapshot = Snapshot<CatalogModel>;

// State for the Models surface. The manifest is fetched once per app
// session (small JSON, doesn't change without a redeploy); install state
// per model is fetched lazily as cards render. Filters live here so the
// view can be a pure function of state.
//
// `ref(manifest)` wraps the deserialised manifest in a Valtio ref so
// nested arrays/objects don't get proxied (cheaper and avoids "you can't
// mutate this readonly array" gotchas on the generated DTOs).

export type TierFilter = 'all' | 'starter' | 'recommended';

interface ModelsState {
  manifest: CatalogManifest | null;
  loading: boolean;
  error: string | null;
  tier: TierFilter;
  // Single-select task; null = all tasks.
  task: string | null;
  // Multi-select tags; empty = no tag filter (matches every model).
  tags: string[];
}

export const modelsState = proxy<ModelsState>({
  manifest: null,
  loading: false,
  error: null,
  tier: 'all',
  task: null,
  tags: [],
});

export async function loadModelsCatalog(): Promise<void> {
  if (modelsState.loading) return;
  if (modelsState.manifest !== null) return; // already cached
  modelsState.loading = true;
  modelsState.error = null;
  try {
    const manifest = await api.modelCatalog.getManifest();
    modelsState.manifest = ref(manifest);
  } catch (err) {
    modelsState.error = err instanceof Error ? err.message : String(err);
  } finally {
    modelsState.loading = false;
  }
}

export function setTier(tier: TierFilter): void {
  modelsState.tier = tier;
}

export function setTask(task: string | null): void {
  modelsState.task = task;
}

export function toggleTag(tag: string): void {
  const idx = modelsState.tags.indexOf(tag);
  if (idx === -1) {
    modelsState.tags.push(tag);
  } else {
    modelsState.tags.splice(idx, 1);
  }
}

export function clearFilters(): void {
  modelsState.tier = 'all';
  modelsState.task = null;
  modelsState.tags = [];
}

// Pure filter — applied at render time. Doesn't mutate state. Caller passes
// the manifest snapshot from useSnapshot.
export function filterModels(
  manifest: CatalogManifestSnapshot,
  tier: TierFilter,
  task: string | null,
  tags: readonly string[],
): readonly CatalogModelSnapshot[] {
  const models = manifest.models ?? [];
  const tierIds = tier === 'all'
    ? null
    : new Set(manifest.tiers?.[tier] ?? []);

  return models.filter((m) => {
    if (tierIds && !tierIds.has(m.id ?? '')) return false;
    if (task !== null && m.task !== task) return false;
    if (tags.length > 0) {
      const modelTags = m.tags ?? [];
      // ALL selected tags must be present (AND semantics) — feels more
      // useful than OR for narrowing a long list.
      for (const required of tags) {
        if (!modelTags.includes(required)) return false;
      }
    }
    return true;
  });
}

// Derive the unique task / tag sets from the manifest for filter chips.
export function collectTasks(manifest: CatalogManifestSnapshot): readonly string[] {
  const set = new Set<string>();
  for (const m of manifest.models ?? []) {
    if (m.task) set.add(m.task);
  }
  return [...set].sort();
}

export function collectTags(manifest: CatalogManifestSnapshot): readonly string[] {
  const set = new Set<string>();
  for (const m of manifest.models ?? []) {
    for (const t of m.tags ?? []) set.add(t);
  }
  return [...set].sort();
}
