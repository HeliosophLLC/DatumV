import { proxy, ref } from 'valtio';
import { api } from '@/api';
import type { ScalarFunctionDto } from '@/api/generated/openapi-client';

// Read-only cache for `GET /api/functions/scalar`. Fetched once per app
// session — the catalog is bounded by the registered scalar set, which
// changes only across server upgrades. The Execute-Function form consumes
// this directly via `useSnapshot`.
//
// `ref(entries)` wraps the deserialised array in a Valtio ref so the
// nested DTO structure (signatures → parameters → accepted kinds) isn't
// proxied. Cheaper, and the generated DTOs are declared with optional
// fields the Valtio proxy would otherwise warn about.

interface FunctionCatalogState {
  status: 'idle' | 'loading' | 'ready' | 'error';
  entries: ScalarFunctionDto[];
  error: string | null;
  /**
   * Picker UI state: which category sections are currently expanded. Keyed
   * by category name so a `true` value means "expanded". Lives on the
   * catalog proxy (rather than per-tab) so the user's pick-a-category
   * navigation survives switching between function tabs in the same
   * session. Cleared on reload — expansion isn't worth persisting.
   *
   * The search behaviour overrides this: when the search term has ≥ 2
   * characters, every rendered category force-expands so matches are
   * visible without an extra click.
   */
  expandedCategories: Record<string, true>;
}

export const functionCatalogState = proxy<FunctionCatalogState>({
  status: 'idle',
  entries: [],
  error: null,
  expandedCategories: {},
});

export function toggleCategoryExpanded(category: string): void {
  if (functionCatalogState.expandedCategories[category]) {
    const next = { ...functionCatalogState.expandedCategories };
    delete next[category];
    functionCatalogState.expandedCategories = next;
  } else {
    functionCatalogState.expandedCategories = {
      ...functionCatalogState.expandedCategories,
      [category]: true,
    };
  }
}

/**
 * Fetches the scalar function catalog on first call. Subsequent calls
 * are no-ops while loading or after a successful load; on error,
 * subsequent calls retry.
 */
export async function loadFunctionCatalog(): Promise<void> {
  if (functionCatalogState.status === 'loading') return;
  if (functionCatalogState.status === 'ready') return;
  functionCatalogState.status = 'loading';
  functionCatalogState.error = null;
  try {
    const response = await api.functionCatalog.listScalar();
    const entries = response.functions ?? [];
    functionCatalogState.entries = ref(entries);
    functionCatalogState.status = 'ready';
  } catch (err) {
    functionCatalogState.error = err instanceof Error ? err.message : String(err);
    functionCatalogState.status = 'error';
  }
}
