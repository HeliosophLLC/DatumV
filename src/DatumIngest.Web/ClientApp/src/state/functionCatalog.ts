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
}

export const functionCatalogState = proxy<FunctionCatalogState>({
  status: 'idle',
  entries: [],
  error: null,
});

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
