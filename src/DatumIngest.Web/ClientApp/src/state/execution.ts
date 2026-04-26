import { proxy } from 'valtio';
import { postNdjson, postNdjsonMultipart } from './ndjson';
import { installModel } from './downloads';
import { openDialog } from './dialogs';

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
  // Transport encoding for binary payloads (null = raw, "gzip" = the
  // dataB64 decodes to gzip-compressed bytes that need inflation before
  // use). Currently emitted only for kind="pointcloud".
  encoding?: string;
  // Populated for kind="pointcloud". Lets the grid render a metadata-only
  // thumbnail without decoding the blob, and gives the 3D viewer the
  // dimensions it needs to set up BufferAttributes.
  pointCloud?: {
    pointCount: number;
    hasColor: boolean;
    width: number;
    height: number;
    coordinateFrame: string;
  };
  // Populated for kind="mesh". Parallel to pointCloud — gives the grid
  // a metadata-only summary chip and the viewer the shape it needs to
  // set up Three.js BufferAttributes + indices without re-decoding the
  // 48-byte header twice. Bbox arrays are [x, y, z].
  mesh?: {
    vertexCount: number;
    triangleCount: number;
    hasColor: boolean;
    hasNormals: boolean;
    hasUVs: boolean;
    hasTexture: boolean;
    coordinateFrame: string;
    bboxMin: [number, number, number];
    bboxMax: [number, number, number];
  };
  // Populated for kind="numeric_array". `dataB64` carries raw little-
  // endian element bytes; the front-end decodes through the matching
  // TypedArray view ("f32" → Float32Array, "i32" → Int32Array, …).
  // Stats (min/max/mean) are server-computed so the inline chip and
  // single-value card can render without touching the bytes.
  elementKind?: string;
  count?: number;
  // Logical shape — e.g. [4, 4] for a 4×4 matrix, [3, 480, 640] for a
  // CHW tensor. Absent for flat 1-D arrays. `dataB64` is flat row-major
  // (last dimension varies fastest).
  shape?: number[];
  min?: number;
  max?: number;
  mean?: number;
  // Populated for kind="struct". Each entry is itself a JsonCell, so
  // nested numeric arrays / images / sub-structs render with their own
  // dedicated renderer (recursive `<CellValue>`) instead of being
  // flattened into a one-line JSON text body.
  fields?: { name: string; cell: JsonCell }[];
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
  // Device VRAM usage at this sample, system-wide (every process on
  // GPU 0). `null` on hosts without NVIDIA NVML — non-NVIDIA GPU,
  // driver missing, or running on Linux/macOS where the probe doesn't
  // yet support the platform-specific library name.
  vramUsedBytes: number | null;
  vramTotalBytes: number | null;
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

// One completed-span entry streamed from the server's RecentActivityLog
// via trace_sample events. Mirrors the wire shape (`source` is the short
// "op" | "fn" tag the server emits — no need to re-map). `tsMs` is offset
// from the cell-start moment so the popover can render an offset
// timeline without dealing with absolute timestamps. `cellId` is filled
// in on receipt so the trace chip can scope its view to a specific cell
// later — today we render the whole batch.
export interface TraceEntry {
  cellId: string;
  sequence: number;
  tsMs: number;
  source: 'op' | 'fn' | string;
  name: string;
  parent: string | null;
  durationMs: number;
}

// Per-tab trace toggle + accumulated events. `enabledOperators` /
// `enabledScalars` are the per-tab preferences the run() call reads
// when deciding what trace scope to request — they survive across
// runs so toggling them and re-running picks them up. `events` is the
// running list (across all cells in the batch) for the most-recent
// run; cleared on the next run start, or via the popover's Clear
// button. `dropped` is the ring-overflow count summed across all
// cells — non-zero means the trace is partial.
export interface TraceState {
  enabledOperators: boolean;
  enabledScalars: boolean;
  events: TraceEntry[];
  dropped: number;
  completed: boolean;
}

export type ExecutionStatus =
  | 'idle'
  | 'streaming'
  | 'done'
  | 'error'
  | 'cancelled';

// Mirrors DatumIngest.Execution.PreFlightReason (camelCased for JSON wire).
export type PreFlightReason =
  | 'modelNotInstalled'
  | 'pinnedVersionNotInstalled'
  | 'pinnedVersionUnknown';

// One catalog-known model reference the user wrote that isn't ready to run.
// Mirrors PreFlightModelRequirementWire on the server.
export interface PreFlightModelRequirement {
  typedReference: string;
  identifier: string;
  catalogEntryId: string;
  version: string | null;
  versionPinned: boolean;
  reason: PreFlightReason;
  approxSizeMb: number | null;
  siblingIdentifiers: string[];
  entryDeprecated: boolean;
  supersededBy: string | null;
  versionDeprecated: boolean;
  versionDeprecationReason: string | null;
}

// One likely-typo function reference the user wrote.
export interface PreFlightSuggestion {
  typedName: string;
  suggestion: string;
}

// Structured payload received via the `preflight_required` NDJSON event.
// Drives the install / typo-fix modal in the results pane.
export interface PreFlightBlock {
  message: string;
  models: PreFlightModelRequirement[];
  suggestions: PreFlightSuggestion[];
}

export interface TabExecution {
  status: ExecutionStatus;
  cells: CellResult[];
  /** Top-level batch error (parse error, cancellation, unexpected fault). Per-cell errors live on the cell. */
  error: string | null;
  startedAt: number | null;
  elapsedMs: number | null;
  /** Memory profile assembled from `memory_sample` events. `null` until the first sample arrives. */
  memoryProfile: MemoryProfile | null;
  /**
   * Per-tab trace state. Enabled flags are user-controlled preferences
   * that persist across runs (toggling them re-runs into the new scope);
   * `events`/`dropped`/`completed` reset on each run.
   */
  trace: TraceState;
  /**
   * Set by the `preflight_required` event when plan-time pre-flight
   * blocks the run (models need install or function reference is a
   * likely typo). Non-null pins the results pane to the pre-flight
   * modal; cleared via {@link dismissPreFlight} or the next run start.
   */
  preFlight: PreFlightBlock | null;
}

// AbortControllers don't go in the proxy — they're non-serialisable and
// only need to be reachable from a single in-process action. Keep them
// in a plain Map keyed by tab id.
const abortByTab = new Map<string, AbortController>();

// Per-tab row-event coalescing buffer. `row` events arrive at whatever
// rate the NDJSON stream flushes (often many per ms for a wide-table
// SELECT). Mutating the valtio proxy once per row triggers a re-render
// per row, which combined with column re-measurement freezes the UI
// for streams of a few thousand rows. We instead buffer pending rows
// per (tabId, cellId) here and flush them on a setTimeout cadence —
// one mutation, one notification, one render per interval.
//
// Coalesce interval is adaptive. We start at one frame (~16 ms) so the
// UI feels live for cheap streams. After each flush we measure how
// long the commit-and-paint took (rAF fires after React commits its
// render) and grow the interval up to MAX_COALESCE_MS when commits
// blow through FRAME_BUDGET_HIGH_MS — heavy rows (dozens of images,
// point clouds, big float arrays) end up batching at ~2 Hz with rows
// flowing in chunks rather than freezing the tab. When commits get
// cheap again we shrink the interval back toward the floor.
//
// The buffer lives outside the proxy on purpose (non-serialisable, no
// reason to deep-wrap). Terminal events (`complete`, `error`,
// `cell_completed`) flush synchronously so the final state is correct
// the moment the caller observes it.
const MIN_COALESCE_MS = 16;
const MAX_COALESCE_MS = 500;
// Commit-and-paint duration (measured from flush start to the
// following rAF) above which we grow the coalesce interval.
const FRAME_BUDGET_HIGH_MS = 50;
// Below this we shrink, gradually returning to near-live flush rate
// once the per-row cost drops (e.g. the stream switches from media
// cells to scalars).
const FRAME_BUDGET_LOW_MS = 24;

interface PendingRows {
  cells: Map<string, JsonCell[][]>;
  timerHandle: number | null;
  coalesceMs: number;
}
const pendingByTab = new Map<string, PendingRows>();

function getPending(tabId: string): PendingRows {
  let p = pendingByTab.get(tabId);
  if (!p) {
    p = { cells: new Map(), timerHandle: null, coalesceMs: MIN_COALESCE_MS };
    pendingByTab.set(tabId, p);
  }
  return p;
}

function flushPendingRows(tabId: string): void {
  const pending = pendingByTab.get(tabId);
  if (!pending) return;
  if (pending.timerHandle !== null) {
    window.clearTimeout(pending.timerHandle);
    pending.timerHandle = null;
  }
  if (pending.cells.size === 0) return;
  const exec = executionsState.byTabId[tabId];
  if (!exec) {
    pending.cells.clear();
    return;
  }
  const t0 = performance.now();
  for (const [cellId, rows] of pending.cells) {
    const cell = findCell(exec, cellId);
    if (!cell) continue;
    // Single bulk push per cell per flush. push(...rows) is fine for the
    // batch sizes we expect; if it ever grew into the tens of thousands
    // range we'd switch to assigning a fresh array.
    cell.rows.push(...rows);
    cell.rowCount = cell.rows.length;
  }
  pending.cells.clear();

  // Adaptive backoff. rAF fires at the start of the next frame, after
  // React has applied this mutation's render — so the delta from t0 is
  // a decent proxy for the commit cost of the batch we just flushed.
  // Skipped in environments without rAF (jsdom in tests); the interval
  // stays pinned at MIN_COALESCE_MS, which is what the tests assume.
  if (typeof requestAnimationFrame !== 'undefined') {
    requestAnimationFrame(() => {
      const frameCost = performance.now() - t0;
      if (frameCost > FRAME_BUDGET_HIGH_MS) {
        pending.coalesceMs = Math.min(pending.coalesceMs * 2, MAX_COALESCE_MS);
      } else if (
        frameCost < FRAME_BUDGET_LOW_MS &&
        pending.coalesceMs > MIN_COALESCE_MS
      ) {
        pending.coalesceMs = Math.max(
          Math.floor(pending.coalesceMs / 2),
          MIN_COALESCE_MS,
        );
      }
    });
  }
}

function scheduleRowFlush(tabId: string): void {
  const pending = getPending(tabId);
  if (pending.timerHandle !== null) return;
  pending.timerHandle = window.setTimeout(() => {
    pending.timerHandle = null;
    flushPendingRows(tabId);
  }, pending.coalesceMs);
}

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
    memoryProfile: null,
    trace: freshTraceState(),
    preFlight: null,
  };
}

function freshTraceState(): TraceState {
  return {
    enabledOperators: false,
    enabledScalars: false,
    events: [],
    dropped: 0,
    completed: false,
  };
}

/**
 * Toggles the operators-trace preference for `tabId`. Affects the
 * `trace` argument on the next runTab() call; in-flight runs aren't
 * retroactively rescoped. When neither operators nor scalars is enabled,
 * the server's listener stays detached and no trace events fire.
 */
export function setTraceOperators(tabId: string, enabled: boolean): void {
  ensureExecution(tabId);
  executionsState.byTabId[tabId].trace.enabledOperators = enabled;
}

/** Mirror of {@link setTraceOperators} for the per-row scalar trace. */
export function setTraceScalars(tabId: string, enabled: boolean): void {
  ensureExecution(tabId);
  executionsState.byTabId[tabId].trace.enabledScalars = enabled;
}

/**
 * Clears the captured trace events for `tabId` without changing the
 * enabled preference. Backs the popover's Clear button so the user can
 * empty the timeline mid-session without rerunning the query.
 */
export function clearTrace(tabId: string): void {
  const exec = executionsState.byTabId[tabId];
  if (!exec) return;
  exec.trace.events = [];
  exec.trace.dropped = 0;
  exec.trace.completed = exec.status !== 'streaming';
}

function ensureExecution(tabId: string): void {
  if (!executionsState.byTabId[tabId]) {
    executionsState.byTabId[tabId] = freshExecution();
  }
}

export function getExecution(tabId: string): TabExecution {
  return executionsState.byTabId[tabId] ?? freshExecution();
}

// Map the server's PascalCase reason enum to the camelCase wire form
// the UI uses. The JSON serializer emits the enum name as a string;
// defensively lower-camel the first letter so a future server-side
// rename doesn't silently strand us on `undefined`.
function normalisePreFlightReason(raw: string): PreFlightReason {
  const camel = raw.length > 0 ? raw[0].toLowerCase() + raw.slice(1) : raw;
  switch (camel) {
    case 'modelNotInstalled':
    case 'pinnedVersionNotInstalled':
    case 'pinnedVersionUnknown':
      return camel;
    default:
      // Unknown reason — fall back to the closest UX (offer install).
      // Logged once so a new server enum value doesn't silently behave
      // as ModelNotInstalled forever.
      console.warn('[execution] unknown PreFlightReason', raw);
      return 'modelNotInstalled';
  }
}

/**
 * Clears the pre-flight block for `tabId` and returns the tab to idle.
 * Backs the Cancel button on the install modal — the editor's text is
 * untouched, the user can fix typos / cancel outright / re-run after a
 * separate install kicked off out-of-band.
 */
export function dismissPreFlight(tabId: string): void {
  const exec = executionsState.byTabId[tabId];
  if (!exec || !exec.preFlight) return;
  exec.preFlight = null;
  exec.error = null;
  exec.status = 'idle';
}

async function presentPreFlightDialog(
  tabId: string,
  block: PreFlightBlock,
): Promise<void> {
  // Electron IPC uses structured clone, which can't serialise the
  // Valtio-tracked object that lands on `exec.preFlight`. Round-trip
  // through JSON before handing it across the IPC boundary so the
  // dialog renderer receives a fresh plain payload.
  const cloneable: PreFlightBlock = JSON.parse(JSON.stringify(block));
  const { result } = openDialog<{ install: boolean }>({
    kind: 'preflightRequired',
    payload: { block: cloneable },
  });
  const decision = await result;
  // The per-tab block may have been cleared in the meantime (user
  // navigated away, tab closed, fresh run started, or a newer
  // pre-flight dialog already resolved). Only act when the slot is
  // still ours.
  const exec = executionsState.byTabId[tabId];
  if (!exec?.preFlight) return;
  if (decision?.install) {
    installPreFlightModels(tabId);
  } else {
    dismissPreFlight(tabId);
  }
}

/**
 * Kicks off bare installs for every catalog entry flagged
 * `ModelNotInstalled` in the pre-flight payload (deduplicated by
 * catalog entry id) and dismisses the modal immediately. Downloads
 * run in the background via the existing model-download SignalR hub;
 * the user observes progress through the status bar / models view
 * and re-submits the query when ready.
 *
 * Pinned-version requirements (`PinnedVersionNotInstalled` /
 * `PinnedVersionUnknown`) are intentionally NOT auto-installed here:
 * the HTTP surface for pinned installs isn't wired yet, and bare
 * installing the recommended version would leave the user's pinned
 * reference still unresolved on the next submit. Those rows render
 * informationally in the modal; the user installs them from the
 * model card's previous-versions disclosure when that ships.
 */
export function installPreFlightModels(tabId: string): void {
  const exec = executionsState.byTabId[tabId];
  if (!exec?.preFlight) return;
  const seen = new Set<string>();
  for (const m of exec.preFlight.models) {
    if (m.reason !== 'modelNotInstalled') continue;
    if (seen.has(m.catalogEntryId)) continue;
    seen.add(m.catalogEntryId);
    // Fire-and-forget. installModel writes to downloadsState so the
    // status bar / model card pick up progress; failures land on
    // downloadsState.errors for the same surfaces.
    void installModel(m.catalogEntryId);
  }
  dismissPreFlight(tabId);
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
      vramUsedBytes?: number | null;
      vramTotalBytes?: number | null;
    }
  | {
      type: 'trace_sample';
      cell: string;
      entries: {
        sequence: number;
        tsMs: number;
        source: 'op' | 'fn' | string;
        name: string;
        parent: string | null;
        durationMs: number;
      }[];
      dropped: number;
    }
  | {
      type: 'trace_complete';
      cell: string;
      totalEntries: number;
      totalDropped: number;
    }
  | {
      type: 'preflight_required';
      message: string;
      models: {
        typedReference: string;
        identifier: string;
        catalogEntryId: string;
        version: string | null;
        versionPinned: boolean;
        reason: string;
        approxSizeMb: number | null;
        siblingIdentifiers: string[];
        entryDeprecated: boolean;
        supersededBy: string | null;
        versionDeprecated: boolean;
        versionDeprecationReason: string | null;
      }[];
      suggestions: { typedName: string; suggestion: string }[];
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

  // Carry the user's trace preferences forward across runs. The
  // operators/scalars toggles persist; only the per-run accumulation
  // resets. Preserving the existing TraceState reference would leak
  // event arrays — re-create with the same flags so each run starts
  // with an empty timeline.
  const priorTrace = existing?.trace ?? freshTraceState();
  executionsState.byTabId[tabId] = {
    status: 'streaming',
    cells: [],
    error: null,
    startedAt: Date.now(),
    elapsedMs: null,
    memoryProfile: null,
    trace: {
      enabledOperators: priorTrace.enabledOperators,
      enabledScalars: priorTrace.enabledScalars,
      events: [],
      dropped: 0,
      completed: false,
    },
    preFlight: null,
  };

  // Wire-shape trace argument. Mirrors server-side TraceOptionsJson:
  // omit entirely when nothing is enabled so the server takes its
  // zero-listener fast path; otherwise emit only the flags that are on.
  const traceEnvelope = priorTrace.enabledOperators || priorTrace.enabledScalars
    ? { operators: priorTrace.enabledOperators, scalars: priorTrace.enabledScalars }
    : undefined;

  let terminated = false;
  try {
    const iter = opts
      ? postNdjsonMultipart<StreamEvent>(
          '/api/query/stream',
          buildMultipartBody(sql, opts, traceEnvelope),
          abort.signal,
        )
      : postNdjson<StreamEvent>(
          '/api/query/stream',
          { sql, maxRows: 1000, trace: traceEnvelope },
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
    // Make sure any rows still sitting in the rAF buffer land before
    // we flip status to cancelled/error — otherwise the user sees the
    // terminal banner pop up with a partial table that's still missing
    // its last frame of streamed rows.
    flushPendingRows(tabId);
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
    flushPendingRows(tabId);
    pendingByTab.delete(tabId);
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
function buildMultipartBody(
  sql: string,
  opts: RunMultipartOpts,
  trace: { operators: boolean; scalars: boolean } | undefined,
): FormData {
  const envelope = {
    sql,
    maxRows: 1000,
    trace,
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
  const pending = pendingByTab.get(tabId);
  if (pending && pending.timerHandle !== null) {
    window.clearTimeout(pending.timerHandle);
  }
  pendingByTab.delete(tabId);
  delete executionsState.byTabId[tabId];
}

// ────────── Event application ──────────

function applyEvent(tabId: string, event: StreamEvent): void {
  const exec = executionsState.byTabId[tabId];
  if (!exec) return;
  // Preserve arrival order: any non-row event that depends on rows
  // already having landed (truncated/cell_completed flags, error
  // banners, completion timestamps) must see the buffered rows in
  // the proxy first. Skipped for `row` events themselves — those
  // append to the buffer and the rAF tick handles the flush.
  if (event.type !== 'row') flushPendingRows(tabId);
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
      // Hot path. Buffer into the per-tab side map; the rAF flush
      // applies all rows accumulated this frame in a single proxy
      // mutation. See `flushPendingRows` / `scheduleRowFlush` above
      // for the rationale.
      const pending = getPending(tabId);
      let buf = pending.cells.get(event.cell);
      if (!buf) {
        buf = [];
        pending.cells.set(event.cell, buf);
      }
      buf.push(event.cells);
      scheduleRowFlush(tabId);
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

    case 'trace_sample': {
      // Append-only — entries arrive in time order from the server. The
      // sequence field is informational (lets the popover de-dupe in
      // case the server's drain ever returns overlapping windows; today
      // it doesn't, but the cursor design tolerates it).
      const incoming = event.entries.map((e) => ({
        cellId: event.cell,
        sequence: e.sequence,
        tsMs: e.tsMs,
        source: e.source,
        name: e.name,
        parent: e.parent,
        durationMs: e.durationMs,
      }));
      exec.trace.events.push(...incoming);
      exec.trace.dropped += event.dropped;
      break;
    }

    case 'trace_complete': {
      // The server's running totals are authoritative — they include
      // sample-time drops we may already have accumulated piecewise, so
      // overwrite rather than add. Marks the popover state as final
      // (the Clear button is still clickable; further runs reset the
      // whole state in run()).
      exec.trace.dropped = event.totalDropped;
      exec.trace.completed = true;
      break;
    }

    case 'preflight_required': {
      // Plan-time pre-flight blocked the run. The server emits this in
      // place of an error event (followed by complete), so we have to
      // pin status='error' here — otherwise the terminal-status branch
      // in runTab would flip to 'done' and the pane would show the
      // empty-results hint instead of the pre-flight modal.
      const block: PreFlightBlock = {
        message: event.message,
        models: event.models.map((m) => ({
          ...m,
          reason: normalisePreFlightReason(m.reason),
        })),
        suggestions: event.suggestions.map((s) => ({ ...s })),
      };
      exec.preFlight = block;
      exec.status = 'error';
      exec.error = event.message;
      // Spawn the modal Electron child window. We fire-and-forget here
      // (applyEvent is synchronous) and dispatch install / dismiss on
      // the resolved choice. The dialog runs in its own renderer, so it
      // can't reach downloadsState directly — kick the installs from
      // this side via installPreFlightModels.
      void presentPreFlightDialog(tabId, block);
      break;
    }

    case 'memory_sample': {
      const sample: MemorySample = {
        elapsedMs: event.elapsedMs,
        rowBytes: event.rowBytes,
        arenaBytes: event.arenaBytes,
        // JsonIgnoreCondition.WhenWritingNull omits VRAM fields when the
        // probe is unavailable. Normalise undefined → null so downstream
        // renderers can check with a single `!== null`.
        vramUsedBytes: event.vramUsedBytes ?? null,
        vramTotalBytes: event.vramTotalBytes ?? null,
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
