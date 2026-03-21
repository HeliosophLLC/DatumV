import { proxy, ref } from 'valtio';
import { api } from '@/api';
import type {
  SchemaCatalogDto,
  TableEntryDto,
} from '@/api/generated/openapi-client';
import {
  CatalogChangeKind,
  onCatalogChanged,
  acquireCatalogHub,
} from '@/api/catalogHub';

// Read-only cache for the Catalog Explorer panel. One round trip to
// /api/schema/catalog seeds the whole tree (tables → columns + indexes);
// CatalogHub pushes invalidations and we debounce-refetch.
//
// `tables` is wrapped in `ref(...)` so the deeply-nested DTO arrays
// aren't proxied by Valtio — the panel iterates millions of cells in
// the worst case (many tables × many columns), and per-property proxies
// would be needlessly expensive when nothing inside ever mutates.

export type CatalogStatus = 'idle' | 'loading' | 'ready' | 'error';

interface CatalogExplorerState {
  status: CatalogStatus;
  tables: readonly TableEntryDto[];
  error: string | null;
  // Per-table expansion (key = "schema.name"). Keeps tree state across
  // refetches so a live update from CatalogHub doesn't collapse the
  // user's open branches.
  expandedTables: Record<string, true>;
}

export const catalogExplorerState = proxy<CatalogExplorerState>({
  status: 'idle',
  tables: [],
  error: null,
  expandedTables: {},
});

export function tableKey(table: Pick<TableEntryDto, 'schema' | 'name'>): string {
  return `${table.schema}.${table.name}`;
}

export function toggleTableExpanded(key: string): void {
  if (catalogExplorerState.expandedTables[key]) {
    const next = { ...catalogExplorerState.expandedTables };
    delete next[key];
    catalogExplorerState.expandedTables = next;
  } else {
    catalogExplorerState.expandedTables = {
      ...catalogExplorerState.expandedTables,
      [key]: true,
    };
  }
}

let inflight: Promise<void> | null = null;

export function loadCatalog(force = false): Promise<void> {
  if (inflight) return inflight;
  if (!force && catalogExplorerState.status === 'ready') {
    return Promise.resolve();
  }
  catalogExplorerState.status = 'loading';
  catalogExplorerState.error = null;
  inflight = (async () => {
    try {
      const dto: SchemaCatalogDto = await api.schemaCatalog.getCatalog();
      catalogExplorerState.tables = ref(dto.tables ?? []);
      catalogExplorerState.status = 'ready';
    } catch (err) {
      catalogExplorerState.error = err instanceof Error ? err.message : String(err);
      catalogExplorerState.status = 'error';
    } finally {
      inflight = null;
    }
  })();
  return inflight;
}

// ──────────────────────── Live updates ────────────────────────

// Debounce hub-driven refetches so a burst of DDL (e.g. a script that
// creates ten tables) collapses to one round trip. 250ms gives the
// engine time to broadcast every event in the burst before we ask.
const REFETCH_DEBOUNCE_MS = 250;
let refetchTimer: number | null = null;

function scheduleRefetch(): void {
  if (refetchTimer !== null) window.clearTimeout(refetchTimer);
  refetchTimer = window.setTimeout(() => {
    refetchTimer = null;
    void loadCatalog(true);
  }, REFETCH_DEBOUNCE_MS);
}

const RELEVANT_KINDS: ReadonlySet<CatalogChangeKind> = new Set([
  CatalogChangeKind.SchemaCreated,
  CatalogChangeKind.SchemaDropped,
  CatalogChangeKind.TableCreated,
  CatalogChangeKind.TableAltered,
  CatalogChangeKind.TableDropped,
  CatalogChangeKind.IndexCreated,
  CatalogChangeKind.IndexDropped,
]);

let subscribed = false;
// Call once at app startup (App.tsx mount). Idempotent.
export function subscribeCatalogExplorerToHub(): void {
  if (subscribed) return;
  subscribed = true;
  // Open the hub connection lazily so we receive pushes once it's up;
  // the singleton in catalogHub.ts dedupes if other modules also call.
  void acquireCatalogHub().catch((err) => {
    console.error('[catalogExplorer] hub acquire failed', err);
  });
  onCatalogChanged((event) => {
    if (RELEVANT_KINDS.has(event.kind)) {
      scheduleRefetch();
    }
  });
}
