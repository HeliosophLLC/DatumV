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
  // Per-subfolder expansion (key = subfolder node key, e.g. "tc:public.users"
  // for the Columns subfolder under public.users). Tracked separately
  // from `expandedTables` so subfolder state survives a parent-table
  // collapse-and-reopen — feels like VS Code's tree.
  expandedSubfolders: Record<string, true>;
  // ──── Selection model (VS Code parity, mirroring state/files.ts) ────
  //
  // Keys distinguish row kinds since the tree has five node types:
  //   table:           "t:<schema>.<name>"
  //   columns folder:  "tc:<schema>.<name>"
  //   indexes folder:  "ti:<schema>.<name>"
  //   column:          "c:<schema>.<name>:<column>"
  //   index:           "i:<schema>.<name>:<index>"
  //
  // The colon stem is fine — SQL identifiers don't typically contain
  // colons, and quoted identifiers that do would already be edge-case.
  selectedKeys: Record<string, true>;
  anchorKey: string | null;
  focusedKey: string | null;
}

export const catalogExplorerState = proxy<CatalogExplorerState>({
  status: 'idle',
  tables: [],
  error: null,
  expandedTables: {},
  expandedSubfolders: {},
  selectedKeys: {},
  anchorKey: null,
  focusedKey: null,
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

/**
 * Collapses every table + subfolder in the tree. Selection / focus stay
 * put — collapsing parent rows doesn't change which row the keyboard
 * cursor is on.
 */
export function collapseAllCatalog(): void {
  catalogExplorerState.expandedTables = {};
  catalogExplorerState.expandedSubfolders = {};
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
  CatalogChangeKind.ViewCreated,
  CatalogChangeKind.ViewAltered,
  CatalogChangeKind.ViewDropped,
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

// ──────────────────────── Key helpers ────────────────────────
//
// Composite keys keep schema/table/child identity in one string so the
// selection set and visible-order array can be heterogeneous without
// extra discriminators. Same approach VS Code's tree view uses for its
// element identity.

export function tableNodeKey(schema: string, name: string): string {
  return `t:${schema}.${name}`;
}

export function columnsFolderKey(schema: string, table: string): string {
  return `tc:${schema}.${table}`;
}

export function indexesFolderKey(schema: string, table: string): string {
  return `ti:${schema}.${table}`;
}

export function columnNodeKey(
  schema: string,
  table: string,
  column: string,
): string {
  return `c:${schema}.${table}:${column}`;
}

export function indexNodeKey(
  schema: string,
  table: string,
  index: string,
): string {
  return `i:${schema}.${table}:${index}`;
}

/**
 * Returns the parent node key for a given key:
 *   column  → its Columns subfolder (tc:)
 *   index   → its Indexes subfolder (ti:)
 *   tc:/ti: → the owning table (t:)
 *   t:      → null (top-level)
 */
export function parentNodeKey(key: string): string | null {
  if (key.startsWith('c:')) {
    const lastColon = key.lastIndexOf(':');
    return 'tc:' + key.substring(2, lastColon);
  }
  if (key.startsWith('i:')) {
    const lastColon = key.lastIndexOf(':');
    return 'ti:' + key.substring(2, lastColon);
  }
  if (key.startsWith('tc:') || key.startsWith('ti:')) {
    return 't:' + key.substring(3);
  }
  return null;
}

// ──────────────────────── Visible-order projection ────────────────────────

// Structural shape for the walker — wide enough to accept the
// deep-readonly snapshot Valtio produces from useSnapshot. The walker
// only needs schema/name and child-name strings; declaring the minimum
// surface keeps the helper free of readonly/mutable friction.
interface TableLike {
  readonly schema?: string;
  readonly name?: string;
  readonly columns?: readonly { readonly name?: string }[];
  readonly indexes?: readonly { readonly name?: string }[];
}

/**
 * Returns the ordered list of currently-visible row keys: tables, each
 * expanded table's Columns and Indexes subfolders, and each expanded
 * subfolder's children. Drives Shift+click range selection and
 * arrow-key navigation.
 */
export function collectCatalogVisibleKeys(
  tables: readonly TableLike[],
  expandedTables: Readonly<Record<string, true>>,
  expandedSubfolders: Readonly<Record<string, true>>,
): string[] {
  const out: string[] = [];
  for (const table of tables) {
    const schema = table.schema ?? '';
    const name = table.name ?? '';
    out.push(tableNodeKey(schema, name));
    if (!expandedTables[`${schema}.${name}`]) continue;

    const colsKey = columnsFolderKey(schema, name);
    out.push(colsKey);
    if (expandedSubfolders[colsKey]) {
      for (const col of table.columns ?? []) {
        if (col.name) out.push(columnNodeKey(schema, name, col.name));
      }
    }

    const idxKey = indexesFolderKey(schema, name);
    out.push(idxKey);
    if (expandedSubfolders[idxKey]) {
      for (const idx of table.indexes ?? []) {
        if (idx.name) out.push(indexNodeKey(schema, name, idx.name));
      }
    }
  }
  return out;
}

// ──────────────────────── Selection actions ────────────────────────

/** Replace the selection with a single key. Sets anchor = focus = key. */
export function selectCatalogNode(key: string): void {
  catalogExplorerState.selectedKeys = { [key]: true };
  catalogExplorerState.anchorKey = key;
  catalogExplorerState.focusedKey = key;
}

/** Ctrl/Cmd+click: toggle membership; clear anchor when emptying. */
export function toggleCatalogSelectedNode(key: string): void {
  const next = { ...catalogExplorerState.selectedKeys };
  if (next[key]) {
    delete next[key];
  } else {
    next[key] = true;
  }
  catalogExplorerState.selectedKeys = next;
  catalogExplorerState.anchorKey = next[key] ? key : null;
  catalogExplorerState.focusedKey = key;
}

/** Shift+click: range select from anchor (or focus) to key. Visible-only. */
export function extendCatalogSelectionTo(
  key: string,
  visibleOrder: readonly string[],
): void {
  const anchor =
    catalogExplorerState.anchorKey ??
    catalogExplorerState.focusedKey ??
    key;
  const anchorIdx = visibleOrder.indexOf(anchor);
  const targetIdx = visibleOrder.indexOf(key);
  if (anchorIdx === -1 || targetIdx === -1) {
    selectCatalogNode(key);
    return;
  }
  const [lo, hi] =
    anchorIdx <= targetIdx ? [anchorIdx, targetIdx] : [targetIdx, anchorIdx];
  const next: Record<string, true> = {};
  for (let i = lo; i <= hi; i++) {
    next[visibleOrder[i]] = true;
  }
  catalogExplorerState.selectedKeys = next;
  catalogExplorerState.focusedKey = key;
  // Anchor intentionally unchanged.
}

/** Up/Down arrow nav. `extend` mirrors Shift+arrow. */
export function moveCatalogFocus(
  direction: 'up' | 'down',
  visibleOrder: readonly string[],
  extend: boolean,
): void {
  if (visibleOrder.length === 0) return;
  const current = catalogExplorerState.focusedKey;
  let idx = current ? visibleOrder.indexOf(current) : -1;
  if (idx === -1) {
    idx = direction === 'up' ? visibleOrder.length - 1 : 0;
  } else {
    idx =
      direction === 'up'
        ? Math.max(0, idx - 1)
        : Math.min(visibleOrder.length - 1, idx + 1);
  }
  const next = visibleOrder[idx];
  if (extend) {
    extendCatalogSelectionTo(next, visibleOrder);
  } else {
    selectCatalogNode(next);
  }
}

/** Toggle expansion of a Columns/Indexes subfolder (key starts tc:/ti:). */
export function toggleCatalogSubfolderExpanded(key: string): void {
  const next = { ...catalogExplorerState.expandedSubfolders };
  if (next[key]) {
    delete next[key];
  } else {
    next[key] = true;
  }
  catalogExplorerState.expandedSubfolders = next;
}

/**
 * Right arrow. Cases (mirrors the file panel):
 *   * Focused leaf (column / index): no-op.
 *   * Focused collapsed table: expand it.
 *   * Focused expanded table: descend into its Columns subfolder.
 *   * Focused collapsed subfolder: expand it.
 *   * Focused expanded subfolder: descend into its first child.
 */
export function expandOrDescendCatalogFocused(): void {
  const focused = catalogExplorerState.focusedKey;
  if (!focused) return;

  if (focused.startsWith('t:')) {
    // t:<schema>.<name> → <schema>.<name>
    const stem = focused.substring(2);
    if (!catalogExplorerState.expandedTables[stem]) {
      catalogExplorerState.expandedTables = {
        ...catalogExplorerState.expandedTables,
        [stem]: true,
      };
      return;
    }
    // Already expanded → descend to Columns subfolder row.
    selectCatalogNode('tc:' + stem);
    return;
  }

  if (focused.startsWith('tc:') || focused.startsWith('ti:')) {
    if (!catalogExplorerState.expandedSubfolders[focused]) {
      catalogExplorerState.expandedSubfolders = {
        ...catalogExplorerState.expandedSubfolders,
        [focused]: true,
      };
      return;
    }
    // Already expanded → descend to first child.
    const stem = focused.substring(3); // strip "tc:" or "ti:"
    const dotIdx = stem.indexOf('.');
    if (dotIdx === -1) return;
    const schema = stem.substring(0, dotIdx);
    const name = stem.substring(dotIdx + 1);
    const table = catalogExplorerState.tables.find(
      (tt) => (tt.schema ?? '') === schema && (tt.name ?? '') === name,
    );
    if (!table) return;
    if (focused.startsWith('tc:')) {
      const firstCol = table.columns?.[0];
      if (firstCol?.name) selectCatalogNode(columnNodeKey(schema, name, firstCol.name));
    } else {
      const firstIdx = table.indexes?.[0];
      if (firstIdx?.name) selectCatalogNode(indexNodeKey(schema, name, firstIdx.name));
    }
  }
}

/**
 * Left arrow.
 *   * Focused expanded table: collapse it.
 *   * Focused expanded subfolder: collapse it.
 *   * Focused leaf or collapsed subfolder: ascend to parent.
 *   * Focused collapsed top-level table: no-op.
 */
export function collapseOrAscendCatalogFocused(): void {
  const focused = catalogExplorerState.focusedKey;
  if (!focused) return;

  if (focused.startsWith('t:')) {
    const stem = focused.substring(2);
    if (catalogExplorerState.expandedTables[stem]) {
      const next = { ...catalogExplorerState.expandedTables };
      delete next[stem];
      catalogExplorerState.expandedTables = next;
    }
    return;
  }

  if (focused.startsWith('tc:') || focused.startsWith('ti:')) {
    if (catalogExplorerState.expandedSubfolders[focused]) {
      const next = { ...catalogExplorerState.expandedSubfolders };
      delete next[focused];
      catalogExplorerState.expandedSubfolders = next;
      return;
    }
    // Collapsed subfolder → ascend to parent table.
    const parent = parentNodeKey(focused);
    if (parent) selectCatalogNode(parent);
    return;
  }

  // Leaf (c:/i:) → ascend to subfolder.
  const parent = parentNodeKey(focused);
  if (parent) selectCatalogNode(parent);
}

/** Clears selection (and the anchor); keeps the focus where it was. */
export function clearCatalogSelection(): void {
  catalogExplorerState.selectedKeys = {};
  catalogExplorerState.anchorKey = null;
}
