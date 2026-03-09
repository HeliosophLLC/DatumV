import { proxy } from 'valtio';
import { postNdjson, postNdjsonMultipart } from './ndjson';

// Per-tab execution state. Lives in a side store rather than on the tab
// itself because results / abort handles / status churn at a different
// rate than tab metadata, and we don't want to persist any of it across
// reloads (the tab's `sql` is enough — re-run on demand).
//
// Status lifecycle:
//   idle ──run──► streaming ──(any error event)──► error
//                            │
//                            ├──complete event──► done
//                            └──cancel()────────► cancelled
//
// Each cell in a multi-statement batch gets its own CellResult; a single
// SELECT produces one cell, a CALL or DDL produces one, a procedural
// script may produce many.

export interface ColumnInfo {
  name: string;
  kind: string;
  isArray: boolean;
}

export interface JsonCell {
  kind: string;
  text?: string;
  mime?: string;
  dataB64?: string;
  items?: { mime: string; dataB64: string }[];
}

export interface CellResult {
  cellId: string;
  cellKind: string;
  schema: ColumnInfo[] | null;
  rows: JsonCell[][];
  rowCount: number;
  truncated: boolean;
  elapsedMs: number | null;
  error: string | null;
  // Live model chunks (LLM token stream) appended in arrival order. The
  // UI surfaces them adjacent to the cell that produced them.
  chunks: { model: string; text: string }[];
}

// One memory-residency sample emitted at ~1Hz by the server's
// `MemoryAccountant` while a cell streams. `rowBytes` is what the spill
// budget compares against (GC-resident operator + variable state).
// `arenaBytes` is informational only — mmap, OS-paged, doesn't count.
export interface MemorySample {
  elapsedMs: number;
  rowBytes: number;
  arenaBytes: number;
}

// Per-batch memory profile (one accountant feeds all cells in a batch, so
// samples land here regardless of which cell emitted them). The status-bar
// chip reads `latest`; the click-to-expand popover reads `samples`.
export interface MemoryProfile {
  samples: MemorySample[];
  latest: MemorySample | null;
  peakRowBytes: number;
  budgetBytes: number | null;
}

export type ExecutionStatus =
  | 'idle'
  | 'streaming'
  | 'done'
  | 'error'
  | 'cancelled';

export interface TabExecution {
  status: ExecutionStatus;
  cells: CellResult[];
  /** Top-level batch error (parse error, cancellation, unexpected fault). Per-cell errors live on the cell. */
  error: string | null;
  startedAt: number | null;
  elapsedMs: number | null;
  /** Optional batch-wide trace text (only when the request enabled tracing). */
  trace: string | null;
  /** Memory profile assembled from `memory_sample` events. `null` until the first sample arrives. */
  memoryProfile: MemoryProfile | null;
}

// AbortControllers don't go in the proxy — they're non-serialisable and
// only need to be reachable from a single in-process action. Keep them
// in a plain Map keyed by tab id.
const abortByTab = new Map<string, AbortController>();

interface ExecutionsState {
  byTabId: Record<string, TabExecution>;
}

export const executionsState = proxy<ExecutionsState>({ byTabId: {} });

function freshExecution(): TabExecution {
  return {
    status: 'idle',
    cells: [],
    error: null,
    startedAt: null,
    elapsedMs: null,
    trace: null,
    memoryProfile: null,
  };
}

export function getExecution(tabId: string): TabExecution {
  return executionsState.byTabId[tabId] ?? freshExecution();
}

// ────────── NDJSON event types ──────────

// Mirror DatumIngest.Web.Execution event records (camelCase via the JSON
// serializer). The discriminated union gives exhaustive switch checking
// in the run() loop below.
type StreamEvent =
  | { type: 'session'; id: string }
  | { type: 'cell_started'; cell: string; kind: string; sql: string | null }
  | { type: 'schema'; cell: string; columns: ColumnInfo[] }
  | { type: 'row'; cell: string; cells: JsonCell[] }
  | { type: 'chunk'; cell: string; model: string; text: string }
  | { type: 'truncated'; cell: string; rowCount: number }
  | { type: 'trace'; cell: string; text: string }
  | { type: 'cell_completed'; cell: string; elapsedMs: number }
  | { type: 'complete'; elapsedMs: number }
  | { type: 'error'; cell: string | null; message: string; detail: string | null }
  | {
      type: 'memory_sample';
      cell: string;
      elapsedMs: number;
      rowBytes: number;
      arenaBytes: number;
      peakRowBytes: number;
      budgetBytes: number | null;
    };

// ────────── Actions ──────────

/**
 * Wire shape for a single `$name` binding in the multipart envelope.
 * Mirrors the server's `ParameterJson` DTO: inline scalars use `value`,
 * binary kinds use `ref` to name a sibling multipart part. See
 * `DatumIngest.Web.Dtos.Execution.ParameterJson` for the canonical
 * definition.
 */
export interface ParameterBinding {
  kind: string;
  value?: unknown;
  ref?: string;
}

/** Options for the multipart-bodied variant of `runTab`. */
export interface RunMultipartOpts {
  /** Named parameter bindings, with `ref` parameters pointing at `files` entries. */
  parameters: Record<string, ParameterBinding>;
  /**
   * Files to attach as multipart parts, keyed by the part name referenced
   * in a `parameters[name].ref` binding. The key must match the `ref` —
   * the server looks up parts by name and rejects unresolved refs with a
   * 400 carrying the offending parameter name.
   */
  files: Record<string, File>;
}

/**
 * Kicks off an NDJSON stream against `/api/query/stream` for the given
 * tab + SQL. If another run is in flight for the tab, this is a no-op —
 * the user has to cancel the running one first. Returns when the stream
 * terminates (complete or error) or the run is cancelled.
 *
 * `opts` is for the function-tab path: when provided, the request goes
 * out as `multipart/form-data` with a JSON `request` part plus one
 * binary part per entry in `opts.files`. Omit it for plain SQL runs.
 *
 * Mutation discipline: every state change goes through
 * `executionsState.byTabId[tabId]` rather than a captured local ref.
 * Valtio wraps the value at assignment time but the original
 * (pre-assignment) reference stays unwrapped — mutations on it don't
 * notify subscribers, so React never re-renders. Always re-access
 * through the proxy.
 */
export async function runTab(
  tabId: string,
  sql: string,
  opts?: RunMultipartOpts,
): Promise<void> {
  const existing = executionsState.byTabId[tabId];
  if (existing && existing.status === 'streaming') {
    return; // already running; cancel first if you want to restart
  }

  const abort = new AbortController();
  abortByTab.set(tabId, abort);

  executionsState.byTabId[tabId] = {
    status: 'streaming',
    cells: [],
    error: null,
    startedAt: Date.now(),
    elapsedMs: null,
    trace: null,
    memoryProfile: null,
  };

  let terminated = false;
  try {
    const iter = opts
      ? postNdjsonMultipart<StreamEvent>(
          '/api/query/stream',
          buildMultipartBody(sql, opts),
          abort.signal,
        )
      : postNdjson<StreamEvent>(
          '/api/query/stream',
          { sql, maxRows: 1000, trace: false },
          abort.signal,
        );

    for await (const event of iter) {
      applyEvent(tabId, event);
      if (event.type === 'complete') {
        const exec = executionsState.byTabId[tabId];
        if (exec) {
          exec.status = exec.status === 'error' ? 'error' : 'done';
          exec.elapsedMs = event.elapsedMs;
          terminated = true;
        }
      }
    }
  } catch (err) {
    const exec = executionsState.byTabId[tabId];
    if (exec) {
      if ((err as { name?: string }).name === 'AbortError') {
        exec.status = 'cancelled';
        exec.error = 'cancelled';
      } else {
        exec.status = 'error';
        exec.error = err instanceof Error ? err.message : String(err);
      }
    }
    terminated = true;
  } finally {
    abortByTab.delete(tabId);
    const exec = executionsState.byTabId[tabId];
    if (exec) {
      if (!terminated && exec.status === 'streaming') {
        // Stream ended without a `complete` event (shouldn't happen,
        // but pin the status so the UI doesn't get stuck on streaming).
        exec.status = 'done';
      }
      if (exec.elapsedMs === null && exec.startedAt !== null) {
        exec.elapsedMs = Date.now() - exec.startedAt;
      }
    }
  }
}

/**
 * Builds the multipart body for a function-tab run. The `request` part
 * carries the same JSON envelope a SQL run would send; one extra part
 * is appended per File in `opts.files`, named by the multipart part
 * name the server looks up.
 */
function buildMultipartBody(sql: string, opts: RunMultipartOpts): FormData {
  const envelope = {
    sql,
    maxRows: 1000,
    trace: false,
    parameters: opts.parameters,
  };
  const formData = new FormData();
  formData.append(
    'request',
    new Blob([JSON.stringify(envelope)], { type: 'application/json' }),
  );
  for (const [name, file] of Object.entries(opts.files)) {
    formData.append(name, file, file.name);
  }
  return formData;
}

/**
 * Cancels the in-flight run for `tabId`, if any. Idempotent — a no-op
 * when the tab has nothing running.
 */
export function cancelTab(tabId: string): void {
  const abort = abortByTab.get(tabId);
  if (abort) abort.abort();
}

/**
 * Clears the execution slot for `tabId`. Use when closing a tab so the
 * map doesn't keep stale references.
 */
export function disposeTabExecution(tabId: string): void {
  cancelTab(tabId);
  delete executionsState.byTabId[tabId];
}

// ────────── Event application ──────────

function applyEvent(tabId: string, event: StreamEvent): void {
  const exec = executionsState.byTabId[tabId];
  if (!exec) return;
  switch (event.type) {
    case 'session':
      // Session id is informational — nothing to display yet. Could
      // surface in a "running" tooltip later.
      break;

    case 'cell_started': {
      exec.cells.push({
        cellId: event.cell,
        cellKind: event.kind,
        schema: null,
        rows: [],
        rowCount: 0,
        truncated: false,
        elapsedMs: null,
        error: null,
        chunks: [],
      });
      break;
    }

    case 'schema': {
      const cell = findCell(exec, event.cell);
      if (cell) cell.schema = event.columns;
      break;
    }

    case 'row': {
      const cell = findCell(exec, event.cell);
      if (!cell) break;
      cell.rows.push(event.cells);
      cell.rowCount = cell.rows.length;
      break;
    }

    case 'chunk': {
      const cell = findCell(exec, event.cell);
      if (!cell) break;
      cell.chunks.push({ model: event.model, text: event.text });
      break;
    }

    case 'truncated': {
      const cell = findCell(exec, event.cell);
      if (cell) cell.truncated = true;
      break;
    }

    case 'trace':
      exec.trace = event.text;
      break;

    case 'cell_completed': {
      const cell = findCell(exec, event.cell);
      if (cell) cell.elapsedMs = event.elapsedMs;
      break;
    }

    case 'error': {
      // Loose null/undefined check: the server's JSON serializer omits
      // null fields entirely (DefaultIgnoreCondition.WhenWritingNull),
      // so a batch-level error arrives as `{type, message, detail}`
      // with no `cell` key at all — accessing `event.cell` yields
      // `undefined`, not `null`. `!=` matches both.
      if (event.cell != null) {
        const cell = findCell(exec, event.cell);
        if (cell) cell.error = event.message;
      } else {
        exec.error = event.message;
        exec.status = 'error';
      }
      break;
    }

    case 'complete':
      // Terminal — handled by the caller so the status / elapsedMs swap
      // happens once, after applyEvent returns. We leave it as a no-op
      // here so exhaustive checks across the union still hold.
      break;

    case 'memory_sample': {
      const sample: MemorySample = {
        elapsedMs: event.elapsedMs,
        rowBytes: event.rowBytes,
        arenaBytes: event.arenaBytes,
      };
      // Server's JsonIgnoreCondition.WhenWritingNull omits the budget
      // field entirely when the budget is null. JSON.parse hands us
      // `undefined` for an omitted property; normalise to `null` on
      // receipt so downstream `!== null` checks behave consistently.
      const budgetBytes = event.budgetBytes ?? null;
      if (!exec.memoryProfile) {
        exec.memoryProfile = {
          samples: [sample],
          latest: sample,
          peakRowBytes: event.peakRowBytes,
          budgetBytes,
        };
      } else {
        exec.memoryProfile.samples.push(sample);
        exec.memoryProfile.latest = sample;
        exec.memoryProfile.peakRowBytes = event.peakRowBytes;
        // Budget can become known mid-stream if the server only computes it
        // after the first plan step. Take the non-null value once it appears.
        if (budgetBytes !== null && exec.memoryProfile.budgetBytes === null) {
          exec.memoryProfile.budgetBytes = budgetBytes;
        }
      }
      break;
    }
  }
}

function findCell(exec: TabExecution, cellId: string): CellResult | undefined {
  // Cells are pushed in order; reverse scan finds the most recent (and
  // therefore likely current) cell faster than a forward find.
  for (let i = exec.cells.length - 1; i >= 0; i--) {
    if (exec.cells[i].cellId === cellId) return exec.cells[i];
  }
  return undefined;
}
