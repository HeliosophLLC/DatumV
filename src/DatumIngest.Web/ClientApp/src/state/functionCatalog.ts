import { proxy, ref } from 'valtio';
import { api } from '@/api';
import type {
  ModelDto,
  ProcedureDto,
  ScalarFunctionDto,
  UdfDto,
} from '@/api/generated/openapi-client';
import {
  CatalogChangeKind,
  onCatalogChanged,
  acquireCatalogHub,
} from '@/api/catalogHub';

// Read-only cache for the catalog endpoints surfaced by
// `FunctionCatalogController`:
//
//   - `GET /api/functions/scalar` — built-in scalar functions (the big one).
//   - `GET /api/functions/udfs`   — SQL UDFs declared with `CREATE FUNCTION`.
//   - `GET /api/functions/models` — SQL-defined models declared with
//     `CREATE MODEL`. Each may carry an `ImplementsTask` task-contract
//     name from `TaskTypeRegistry` — used as the grouping key in the
//     picker so users browse by capability rather than by model name.
//
// All three are fetched once per session — the engine doesn't mutate
// any of these registries at runtime today, so a single cached snapshot
// is enough. `ref(entries)` wraps each deserialised array in a Valtio
// ref so the nested DTO structure isn't proxied.
//
// `expandedSections` is a single flat map covering every collapsible
// node in the picker. Keys are stable string ids:
//   - top-level sections:   `models`, `udfs`, `functions`
//   - function categories:  `functions:Numeric`, `functions:String`, …
//   - model task buckets:   `models:TextEmbedder`, `models:(no task)`, …
// Search (≥ 2 chars) force-expands any group with a match regardless
// of what's in this map.

export type CatalogStatus = 'idle' | 'loading' | 'ready' | 'error';

interface FunctionCatalogState {
  scalarStatus: CatalogStatus;
  scalars: ScalarFunctionDto[];
  scalarError: string | null;

  udfStatus: CatalogStatus;
  udfs: UdfDto[];
  udfError: string | null;

  modelStatus: CatalogStatus;
  models: ModelDto[];
  modelError: string | null;

  procedureStatus: CatalogStatus;
  procedures: ProcedureDto[];
  procedureError: string | null;

  /** Flat expansion map across every collapsible node in the picker. */
  expandedSections: Record<string, true>;
}

/**
 * Default-expansion preset. Models is a curated catalog that's going to
 * grow large; defaulting it closed keeps the picker calm. UDFs are
 * sparse and likely user-authored, worth expanding by default. Functions
 * is the legacy big-list; its categories stay collapsed-by-default
 * inside the section.
 */
const DEFAULT_EXPANDED: Record<string, true> = {
  udfs: true,
  functions: true,
};

export const functionCatalogState = proxy<FunctionCatalogState>({
  scalarStatus: 'idle',
  scalars: [],
  scalarError: null,
  udfStatus: 'idle',
  udfs: [],
  udfError: null,
  modelStatus: 'idle',
  models: [],
  modelError: null,
  procedureStatus: 'idle',
  procedures: [],
  procedureError: null,
  expandedSections: { ...DEFAULT_EXPANDED },
});

export function toggleSectionExpanded(key: string): void {
  if (functionCatalogState.expandedSections[key]) {
    const next = { ...functionCatalogState.expandedSections };
    delete next[key];
    functionCatalogState.expandedSections = next;
  } else {
    functionCatalogState.expandedSections = {
      ...functionCatalogState.expandedSections,
      [key]: true,
    };
  }
}

/**
 * Convenience for callers that previously toggled categories under the
 * old `expandedCategories` map. Lives here so the picker doesn't have
 * to know about the `functions:` prefix scheme directly.
 */
export function toggleCategoryExpanded(category: string): void {
  toggleSectionExpanded(`functions:${category}`);
}

export async function loadScalarFunctions(): Promise<void> {
  if (functionCatalogState.scalarStatus === 'loading') return;
  if (functionCatalogState.scalarStatus === 'ready') return;
  functionCatalogState.scalarStatus = 'loading';
  functionCatalogState.scalarError = null;
  try {
    const response = await api.functionCatalog.listScalar();
    functionCatalogState.scalars = ref(response.functions ?? []);
    functionCatalogState.scalarStatus = 'ready';
  } catch (err) {
    functionCatalogState.scalarError = err instanceof Error ? err.message : String(err);
    functionCatalogState.scalarStatus = 'error';
  }
}

export async function loadUdfs(): Promise<void> {
  if (functionCatalogState.udfStatus === 'loading') return;
  if (functionCatalogState.udfStatus === 'ready') return;
  functionCatalogState.udfStatus = 'loading';
  functionCatalogState.udfError = null;
  try {
    const response = await api.functionCatalog.listUdfs();
    functionCatalogState.udfs = ref(response.udfs ?? []);
    functionCatalogState.udfStatus = 'ready';
  } catch (err) {
    functionCatalogState.udfError = err instanceof Error ? err.message : String(err);
    functionCatalogState.udfStatus = 'error';
  }
}

export async function loadModels(): Promise<void> {
  if (functionCatalogState.modelStatus === 'loading') return;
  if (functionCatalogState.modelStatus === 'ready') return;
  functionCatalogState.modelStatus = 'loading';
  functionCatalogState.modelError = null;
  try {
    const response = await api.functionCatalog.listModels();
    functionCatalogState.models = ref(response.models ?? []);
    functionCatalogState.modelStatus = 'ready';
  } catch (err) {
    functionCatalogState.modelError = err instanceof Error ? err.message : String(err);
    functionCatalogState.modelStatus = 'error';
  }
}

export async function loadProcedures(force = false): Promise<void> {
  if (functionCatalogState.procedureStatus === 'loading') return;
  if (!force && functionCatalogState.procedureStatus === 'ready') return;
  functionCatalogState.procedureStatus = 'loading';
  functionCatalogState.procedureError = null;
  try {
    const response = await api.functionCatalog.listProcedures();
    functionCatalogState.procedures = ref(response.procedures ?? []);
    functionCatalogState.procedureStatus = 'ready';
  } catch (err) {
    functionCatalogState.procedureError = err instanceof Error ? err.message : String(err);
    functionCatalogState.procedureStatus = 'error';
  }
}

/** Fires all three loaders in parallel. Convenient for the picker's mount effect. */
export function loadFunctionCatalog(): Promise<void> {
  return Promise.all([loadScalarFunctions(), loadUdfs(), loadModels()]).then(
    () => undefined,
  );
}

// ──────────────────────── Live updates ────────────────────────

// Procedures + UDFs panel debounce. Procedural-UDF creation can come in
// bursts (a setup script running CREATE FUNCTION/PROCEDURE several
// times); 250ms collapses the burst before refetching.
const REFETCH_DEBOUNCE_MS = 250;
let procRefetchTimer: number | null = null;
let udfRefetchTimer: number | null = null;

function scheduleProcedureRefetch(): void {
  if (procRefetchTimer !== null) window.clearTimeout(procRefetchTimer);
  procRefetchTimer = window.setTimeout(() => {
    procRefetchTimer = null;
    void loadProcedures(true);
  }, REFETCH_DEBOUNCE_MS);
}

function scheduleUdfRefetch(): void {
  if (udfRefetchTimer !== null) window.clearTimeout(udfRefetchTimer);
  udfRefetchTimer = window.setTimeout(() => {
    udfRefetchTimer = null;
    // `loadUdfs` is no-op after first ready load; force a refetch.
    functionCatalogState.udfStatus = 'idle';
    void loadUdfs();
  }, REFETCH_DEBOUNCE_MS);
}

const PROCEDURE_KINDS: ReadonlySet<CatalogChangeKind> = new Set([
  CatalogChangeKind.ProcedureCreated,
  CatalogChangeKind.ProcedureAltered,
  CatalogChangeKind.ProcedureDropped,
]);

const UDF_KINDS: ReadonlySet<CatalogChangeKind> = new Set([
  CatalogChangeKind.FunctionCreated,
  CatalogChangeKind.FunctionAltered,
  CatalogChangeKind.FunctionDropped,
]);

let routinesSubscribed = false;
// Subscribe the Procedures/UDFs panel to live catalog updates. Idempotent;
// call once at app startup.
export function subscribeRoutinesToHub(): void {
  if (routinesSubscribed) return;
  routinesSubscribed = true;
  void acquireCatalogHub().catch((err) => {
    console.error('[functionCatalog] hub acquire failed', err);
  });
  onCatalogChanged((event) => {
    if (PROCEDURE_KINDS.has(event.kind)) {
      scheduleProcedureRefetch();
    }
    if (UDF_KINDS.has(event.kind)) {
      scheduleUdfRefetch();
    }
  });
}
