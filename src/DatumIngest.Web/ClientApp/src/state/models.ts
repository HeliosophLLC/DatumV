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
  // Free-text search term; matched against id + displayName + description.
  // Empty string = no search filter.
  query: string;
  // Id of the model whose detail pane is showing in the right column.
  // Null = no selection (show the empty/prompt state).
  selectedId: string | null;
}

export const modelsState = proxy<ModelsState>({
  manifest: null,
  loading: false,
  error: null,
  tier: 'all',
  query: '',
  selectedId: null,
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

export function setQuery(query: string): void {
  modelsState.query = query;
}

export function setSelectedId(id: string | null): void {
  modelsState.selectedId = id;
}

export function clearFilters(): void {
  modelsState.tier = 'all';
  modelsState.query = '';
}

// Pure filter — applied at render time. Doesn't mutate state. Caller passes
// the manifest snapshot from useSnapshot.
export function filterModels(
  manifest: CatalogManifestSnapshot,
  tier: TierFilter,
  query: string,
): readonly CatalogModelSnapshot[] {
  const models = manifest.models ?? [];
  const tierIds = tier === 'all'
    ? null
    : new Set(manifest.tiers?.[tier] ?? []);
  const needle = query.trim().toLowerCase();

  return models.filter((m) => {
    if (tierIds && !tierIds.has(m.id ?? '')) return false;
    if (needle.length > 0) {
      const hay = [
        m.id ?? '',
        m.displayName ?? '',
        m.description ?? '',
      ].join(' ').toLowerCase();
      if (!hay.includes(needle)) return false;
    }
    return true;
  });
}
