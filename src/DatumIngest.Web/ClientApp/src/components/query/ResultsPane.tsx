import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { AlertCircle, Ban, Check, Film, Loader2, Music, Sigma } from 'lucide-react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { MediaPreview } from './MediaPreview';
import { MemoryChip } from './MemoryChip';
import { TraceChip } from './TraceChip';
import { PointCloudCell, SingleValuePointCloud } from './PointCloudCell';
import { MeshCell, SingleValueMesh } from './MeshCell';
import {
  executionsState,
  type CellResult,
  type ExecutionStatus,
  type JsonCell,
  type TabExecution,
  type TraceState,
} from '@/state/execution';
import { panesState, findLeaf } from '@/state/tabs';
import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';

// Plain HTML <table> renderer for streamed query results. One block per
// cell in the batch; the scrollable area lives above an SSMS-style
// status bar pinned to the bottom of the pane. Replaced by the
// virtualised TanStack DataGrid in PR 4 — at that point this file goes
// away.
//
// Cells render in arrival order so a multi-statement script shows the
// procedural progression top-to-bottom.

export function ResultsPane({ leafId }: { leafId: string }) {
  const { t } = useTranslation('query');
  useSnapshot(panesState);
  const { byTabId } = useSnapshot(executionsState);

  const leaf = findLeaf(panesState.root, leafId);
  const activeTabId = leaf?.activeTabId ?? null;
  if (activeTabId === null) return null;
  const exec = byTabId[activeTabId];

  // Hide `exec` cells that produced nothing interesting — DECLARE, SET,
  // most DDL/DML. We still show them when they errored, streamed model
  // chunks, or actually returned rows (some DML supports RETURNING).
  // `select` cells always show so an empty-result-set still surfaces
  // the column header.
  const visibleCells = exec
    ? exec.cells.filter((c) => isVisibleCell(c))
    : [];

  // Pre-run / no-data state — no exec yet, OR a terminal run produced
  // nothing visible. Render the hint plus the status bar in its
  // "Ready" state so the bar's height stays constant across the
  // lifecycle (no layout shift when the first run begins).
  //
  // The pane message differentiates "never run" from "ran and produced
  // nothing visible" — without this, a successful DDL/DML statement
  // shows "Query executed successfully" in the status bar but
  // "Run a query to see results" in the pane, which reads as a
  // contradiction. Streaming and cancelled states leave the pane
  // blank — the status bar's spinner / "Cancelled" label already
  // carries that state.
  if (!exec || (visibleCells.length === 0 && exec.error === null)) {
    let emptyMessage = '';
    if (!exec) emptyMessage = t('resultsEmpty');
    else if (exec.status === 'done') emptyMessage = t('resultsCompleted');
    return (
      <div className="flex h-full flex-col overflow-hidden">
        <div className="text-muted-foreground flex flex-1 items-center justify-center text-xs">
          {emptyMessage}
        </div>
        <StatusBar tabId={activeTabId} exec={(exec as TabExecution | undefined) ?? null} />
      </div>
    );
  }

  // Count cells that actually rendered a data grid. Single grid → it
  // fills the whole pane; two or more → each capped at 50%/min 200 px
  // and scrolls internally, with the outer container scrolling between
  // them. Cells with only errors / chunks (no table) sit at content
  // height regardless.
  const tableCount = visibleCells.filter(
    (c) => c.schema !== null && c.rows.length > 0,
  ).length;
  const tableMode: TableMode = tableCount === 1 ? 'fill' : 'capped';

  return (
    <div className="flex h-full flex-col overflow-hidden">
      {/* Outer scroll container. Each table cell becomes its own scroll
          context (for sticky headers + the cap rule) so this container
          only ever scrolls *between* tables, never *within* one. The
          `bg-table-pane` is the surface visible around / behind the
          grids — slightly darker than the rows in dark mode, the
          regular page bg in light mode. */}
      {/* Flex column so the banner / trace claim their own height via
          `shrink-0` and the fill-mode section can `flex-1 min-h-0` to
          take the remainder. A plain block container made the section's
          `h-full` overflow by the banner's height, producing a second
          scrollbar alongside the inner table. */}
      <div className="bg-table-pane flex min-h-0 flex-1 flex-col overflow-auto">
        {exec.error !== null && (
          <div className="text-destructive border-destructive/40 bg-destructive/10 shrink-0 border-b px-3 py-2 font-mono text-xs whitespace-pre-wrap">
            {exec.status === 'cancelled' ? t('resultsCancelledBanner') : exec.error}
          </div>
        )}
        {visibleCells.map((cell) => (
          // Snapshot types are deep-readonly; the components below treat
          // the cell as read-only data so the cast is a no-op at runtime.
          <CellBlock
            key={cell.cellId}
            cell={cell as CellResult}
            tableMode={tableMode}
          />
        ))}
      </div>
      <StatusBar tabId={activeTabId} exec={exec as TabExecution} />
    </div>
  );
}

type TableMode = 'fill' | 'capped';

function isVisibleCell(cell: {
  cellKind: string;
  rowCount: number;
  error: string | null;
  chunks: readonly { model: string; text: string }[];
}): boolean {
  if (cell.cellKind === 'select') return true;
  if (cell.error !== null) return true;
  if (cell.rowCount > 0) return true;
  if (cell.chunks.length > 0) return true;
  return false;
}

// Frozen placeholder fed to TraceChip when no execution slot exists yet
// (idle tab, never run). Toggling the chip's checkbox routes through
// setTraceOperators which lazily creates the slot — at which point the
// real state takes over on the next render.
const EMPTY_TRACE_STATE: TraceState = {
  enabledOperators: false,
  enabledScalars: false,
  events: [],
  dropped: 0,
  completed: false,
};

function StatusBar({ tabId, exec }: { tabId: string; exec: TabExecution | null }) {
  const { t } = useTranslation('query');

  // Live timer during streaming. The exec's startedAt is fixed; we re-
  // render once a second so the "Executing…" duration ticks. After
  // termination we lock to exec.elapsedMs (the server's measurement,
  // not our client clock). Pre-run state shows 0ms.
  const status = exec?.status ?? 'idle';
  const startedAt = exec?.startedAt ?? null;
  const elapsedMsLocked = exec?.elapsedMs ?? 0;
  const [tickMs, setTickMs] = useState<number>(() =>
    status === 'streaming' && startedAt !== null
      ? Date.now() - startedAt
      : elapsedMsLocked,
  );
  // Depend on the primitive `status` and `startedAt` only — NOT on the
  // whole `exec` snapshot. Valtio returns a fresh snapshot reference on
  // every memory_sample event (the profile's samples array mutates at
  // 1Hz). If `exec` is in the deps, the effect re-runs on each sample,
  // clears the just-scheduled setInterval, and the duration timer never
  // ticks. Status + startedAt are the actual signals that warrant
  // reconciling the timer.
  useEffect(() => {
    if (status !== 'streaming' || startedAt === null) return;
    const id = window.setInterval(() => {
      setTickMs(Date.now() - startedAt);
    }, 1000);
    return () => window.clearInterval(id);
  }, [status, startedAt]);

  // Idle path: no execution has started yet (or the slot was cleared
  // when the tab closed and reopened). Show a "Ready" message + 0ms
  // duration + 0 rows so the bar's three-panel layout stays stable
  // from first paint through every subsequent run. `status` and
  // `startedAt` were already declared above for the ticker effect.
  const hasError =
    exec !== null &&
    (exec.error !== null || exec.cells.some((c) => c.error !== null));
  const totalRows = exec
    ? exec.cells.reduce((sum, c) => sum + c.rowCount, 0)
    : 0;
  const elapsedMs = status === 'streaming' ? tickMs : elapsedMsLocked;

  let leftMessage: string;
  if (status === 'idle') leftMessage = t('statusBarReady');
  else if (status === 'streaming') leftMessage = t('statusBarRunning');
  else if (status === 'cancelled') leftMessage = t('statusBarCancelled');
  else if (hasError) leftMessage = t('statusBarError');
  else leftMessage = t('statusBarSuccess');

  // Panels separated by a faint divider in the foreground colour:
  // status message (grows), optional memory chip, duration, row count.
  // Each owns its padding so the dividers sit cleanly between sections.
  // All panels truncate on overflow rather than wrap — `min-w-0` on the
  // grow-panel is required to let its content shrink below its natural
  // width (flex items default to min-width: auto, which prevents
  // ellipsis on long status messages).
  const memoryProfile = exec?.memoryProfile ?? null;
  // Trace chip is always present (idle tabs included) so the user can
  // flip Trace on *before* the first run, not just after one. When no
  // execution slot exists yet we feed it a fresh placeholder state;
  // toggling the checkbox calls setTraceOperators which calls
  // ensureExecution() so the real slot lands on first click.
  const trace = exec?.trace ?? EMPTY_TRACE_STATE;
  return (
    <div className="bg-status-bar text-status-bar-foreground border-border flex shrink-0 items-stretch overflow-hidden border-t text-xs">
      <div className="flex min-w-0 flex-1 items-center gap-1.5 px-3 py-1">
        <StatusIcon status={status} hasError={hasError} />
        <span className="truncate">{leftMessage}</span>
      </div>
      <TraceChip tabId={tabId} trace={trace} />
      {memoryProfile && memoryProfile.latest && (
        <MemoryChip profile={memoryProfile} status={status} />
      )}
      <div className="border-status-bar-foreground/25 flex shrink-0 items-center whitespace-nowrap border-l px-3 py-1 font-mono">
        {formatDuration(elapsedMs)}
      </div>
      <div className="border-status-bar-foreground/25 flex shrink-0 items-center whitespace-nowrap border-l px-3 py-1 font-mono">
        {t('cellRowCount', { rows: totalRows })}
      </div>
    </div>
  );
}

function StatusIcon({
  status,
  hasError,
}: {
  status: ExecutionStatus;
  hasError: boolean;
}) {
  // Iconography is decoupled from the bar's yellow background — the
  // shape carries the meaning, the colour reinforces it. Green check
  // for clean success; red alert when any cell errored; muted ban for
  // cancelled; the toolbar's spinner during run. Idle gets no icon —
  // the "Ready" label is enough and adding a neutral glyph would
  // visually outweigh its informational value.
  if (status === 'idle') return null;
  if (status === 'streaming') {
    return <Loader2 className="size-3.5 shrink-0 animate-spin" />;
  }
  if (status === 'cancelled') {
    return <Ban className="size-3.5 shrink-0 opacity-70" />;
  }
  if (hasError) {
    return <AlertCircle className="size-3.5 shrink-0 text-red-700 dark:text-red-600" />;
  }
  return <Check className="size-3.5 shrink-0 text-green-700 dark:text-green-600" />;
}

// Formats elapsed milliseconds as `MM:SS.mmm`. Sub-second precision is
// what matters most for query timing (a 200ms vs 800ms difference is
// meaningful; 1h vs 2h almost never is for an editor session), so the
// hours panel from the prior format is gone and milliseconds get a
// full 3-digit panel instead. Minutes still pads to two digits so the
// width stays stable even at sub-minute durations. Always-positive —
// negative would mean a clock-skew bug upstream.
function formatDuration(ms: number): string {
  const clamped = Math.max(0, ms);
  const totalSeconds = Math.floor(clamped / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  const millis = Math.floor(clamped % 1000);
  const pad2 = (n: number) => String(n).padStart(2, '0');
  const pad3 = (n: number) => String(n).padStart(3, '0');
  return `${pad2(minutes)}:${pad2(seconds)}.${pad3(millis)}`;
}

function CellBlock({
  cell,
  tableMode,
}: {
  cell: CellResult;
  tableMode: TableMode;
}) {
  const hasTable = cell.schema !== null && cell.rows.length > 0;
  // Single-value detection: 1 row × 1 column, and this is the only
  // table in the pane (`tableMode === 'fill'`). Function-tab runs that
  // return a single SELECT result land here, but the path is generic —
  // a SQL tab that runs `SELECT 'hello'` gets the same treatment.
  // Multi-row or multi-cell results stay in the data grid below.
  const isSingleValue =
    hasTable
    && tableMode === 'fill'
    && cell.rows.length === 1
    && cell.rows[0].length === 1
    && cell.schema!.length === 1;
  // Section is a flex column so the table area can `flex-1` into the
  // remaining height after errors / chunks. The section's own height
  // is constrained only when it carries a table — non-table cells
  // (error-only, chunk-only) keep auto height so they don't reserve a
  // 200 px slab they wouldn't use.
  const heightClass = hasTable
    ? tableMode === 'fill'
      ? 'min-h-0 flex-1'
      : 'min-h-[200px] max-h-[50%]'
    : '';
  return (
    <section
      className={cn(
        'border-border flex flex-col border-b last:border-b-0',
        heightClass,
      )}
    >
      {cell.error !== null && (
        <div className="text-destructive border-destructive/40 bg-destructive/10 shrink-0 border-b px-3 py-2 font-mono text-xs whitespace-pre-wrap">
          {cell.error}
        </div>
      )}
      {cell.chunks.length > 0 && (
        <pre className="bg-muted/30 border-border max-h-48 shrink-0 overflow-y-auto border-b p-2 font-mono text-xs whitespace-pre-wrap">
          {cell.chunks.map((c) => c.text).join('')}
        </pre>
      )}
      {isSingleValue ? (
        <SingleValueView
          cell={cell.rows[0][0]}
          column={cell.schema![0]}
        />
      ) : (
        hasTable && <CellTable cell={cell} />
      )}
    </section>
  );
}

// ────────── Single-value rendering ──────────
//
// When the result is exactly one row × one column, we render the cell
// at full pane size rather than in a 1×1 grid. The user gets:
//   - large image / audio player / video player for media cells, no
//     click-to-open modal indirection;
//   - a centered card with the value as a large monospace block for
//     scalars / JSON / null.
// The column name + type still surfaces in a small header so the user
// keeps the schema context they'd otherwise lose by hiding the grid.

function SingleValueView({
  cell,
  column,
}: {
  cell: JsonCell;
  column: { name: string; kind: string; isArray: boolean };
}) {
  return (
    <div className="bg-table-pane flex min-h-0 flex-1 flex-col overflow-hidden">
      <div className="border-border bg-muted flex shrink-0 items-center gap-1.5 border-b px-3 py-1 text-xs font-medium">
        <span className="truncate font-mono">{column.name}</span>
        <Badge variant="muted" className="shrink-0 font-mono text-[10px] leading-none">
          {column.kind}
          {column.isArray ? '[]' : ''}
        </Badge>
      </div>
      <div className="flex min-h-0 flex-1 items-center justify-center overflow-auto p-4">
        <SingleValueBody cell={cell} />
      </div>
    </div>
  );
}

/**
 * Single-cell image: image fitted to the pane by default, click anywhere
 * (or the "View full resolution" button) to open the modal at native
 * pixel size. The modal's content area scrolls when the image overflows,
 * so a 2048×2048 image renders 1:1 with horizontal + vertical scroll
 * rather than being downscaled to fit a viewport.
 */
function SingleValueImage({ cell }: { cell: JsonCell }) {
  const [open, setOpen] = useState(false);
  const src = dataUriOf(cell);
  const bytes = bytesFromBase64(cell.dataB64);
  const title = `${cell.mime ?? 'image'} · ${formatBytes(bytes)}`;
  return (
    <div className="relative flex h-full w-full items-center justify-center">
      <img
        src={src}
        alt=""
        onClick={() => setOpen(true)}
        title={title}
        className="hover:ring-primary max-h-full max-w-full cursor-zoom-in rounded-xs object-contain hover:ring-1"
      />
      <button
        type="button"
        onClick={() => setOpen(true)}
        className={cn(
          'bg-background/80 text-foreground border-border absolute bottom-2 right-2 rounded-md border px-2 py-1 text-xs',
          'hover:bg-background cursor-pointer outline-none transition-colors focus:ring-2 focus:ring-ring',
        )}
      >
        View full resolution
      </button>
      <MediaPreview open={open} onClose={() => setOpen(false)} title={title}>
        {/* No max-w/max-h: the modal's overflow-auto content area
            scrolls so the user can pan around at native pixel size. */}
        <img src={src} alt="" />
      </MediaPreview>
    </div>
  );
}

function SingleValueBody({ cell }: { cell: JsonCell }) {
  if (cell.kind === 'null') {
    return (
      <span className="text-muted-foreground font-mono text-2xl italic">
        null
      </span>
    );
  }
  if (
    cell.kind === 'media'
    || cell.kind === 'image'
    || cell.kind === 'audio'
    || cell.kind === 'video'
  ) {
    const mime = cell.mime ?? '';
    if (mime.startsWith('image/') || cell.kind === 'image') {
      return <SingleValueImage cell={cell} />;
    }
    if (mime.startsWith('audio/') || cell.kind === 'audio') {
      return (
        <audio
          src={dataUriOf(cell)}
          controls
          className="w-full max-w-[600px]"
        />
      );
    }
    if (mime.startsWith('video/') || cell.kind === 'video') {
      return (
        <video
          src={dataUriOf(cell)}
          controls
          className="max-h-full max-w-full"
        />
      );
    }
    const bytes = bytesFromBase64(cell.dataB64);
    return (
      <span className="text-muted-foreground font-mono text-sm">
        [{cell.mime ?? 'binary'}, {formatBytes(bytes)}]
      </span>
    );
  }
  if (cell.kind === 'media_array' && cell.items) {
    return (
      <div className="grid w-full max-h-full grid-cols-[repeat(auto-fill,minmax(160px,1fr))] gap-2 overflow-auto">
        {cell.items.map((it, i) => (
          <img
            key={i}
            src={`data:${it.mime};base64,${it.dataB64}`}
            alt=""
            loading="lazy"
            className="bg-muted/40 h-40 w-full rounded-xs object-contain"
          />
        ))}
      </div>
    );
  }
  if (cell.kind === 'pointcloud') {
    return <SingleValuePointCloud cell={cell} />;
  }
  if (cell.kind === 'mesh') {
    return <SingleValueMesh cell={cell} />;
  }
  if (cell.kind === 'numeric_array') {
    return <SingleValueNumericArray cell={cell} />;
  }
  // Scalar / JSON / catchall. Use a pre-wrap block so multi-line JSON
  // bodies keep their formatting; large font so the value is readable
  // without zooming.
  return (
    <pre
      className={cn(
        'text-foreground max-w-full overflow-auto font-mono text-2xl leading-relaxed',
        'whitespace-pre-wrap break-words text-center',
      )}
    >
      {cell.text ?? ''}
    </pre>
  );
}

// Per-row pixel height for the virtualiser. Matches the rendered row's
// content + padding (text-xs line-height ~16px, py-1 = 4+4) so estimated
// and actual size agree and `virtualRow.size` is correct without a
// measureElement round-trip.
const ROW_HEIGHT = 28;
// Row height when the grid contains at least one image / video / media-
// array cell. Gives image thumbnails meaningful screen real estate
// (≈ h-16 plus padding) so the user can read a content-detection result
// at a glance instead of squinting at 20px squares.
const ROW_HEIGHT_MEDIA = 80;

/**
 * True when the cell renders as an image or video thumbnail — what
 * benefits from the tall-row mode. Audio cells stay short because
 * they're a glyph + size label, not a thumbnail. `media_array` is
 * almost always image-array in practice and renders thumbnails too.
 */
function isImageOrVideoCell(cell: JsonCell): boolean {
  if (cell.kind === 'image' || cell.kind === 'video') return true;
  if (cell.kind === 'media') {
    const mime = cell.mime ?? '';
    return mime.startsWith('image/') || mime.startsWith('video/');
  }
  if (cell.kind === 'media_array') return true;
  // PointCloud cells render as a chip with metadata + click-to-open; the
  // tall row mode gives the chip room to show grid dimensions inline.
  if (cell.kind === 'pointcloud') return true;
  // Mesh cells use the same chip + click-to-open pattern.
  if (cell.kind === 'mesh') return true;
  return false;
}

function rowsContainImageOrVideo(rows: readonly JsonCell[][]): boolean {
  for (const row of rows) {
    for (const cell of row) {
      if (isImageOrVideoCell(cell)) return true;
    }
  }
  return false;
}
// Bounds for content-based column sizing. Narrow integer columns can
// shrink to 60 px (room for ~6 digits); pathologically wide columns
// (long strings, JSON blobs) cap at 400 px so they don't push every
// other column off-screen. User-resize is a follow-up.
const COL_MIN_WIDTH = 60;
const COL_MAX_WIDTH = 400;
// Width of the leading row-number gutter, styled like the header. Tight
// on purpose — fits ~4 digits comfortably, longer counts truncate with
// an ellipsis. Sticky-left so it stays visible during horizontal scroll.
const ROW_NUMBER_WIDTH = 30;
// Padding allowance baked into each measured column: `px-2` left + right
// = 16 px, plus 1 px for the right border so the text doesn't kiss the
// divider.
const COL_PADDING = 18;
// How many rows we sample when measuring content widths. Streaming
// queries can produce 1000s of rows; measuring all of them every chunk
// gets expensive. 200 is enough to catch outliers without the cost.
const SAMPLE_ROWS = 200;
// Extra width budget for the column-name + type-badge pair in the
// header. The badge text itself varies (`STRING` vs `FLOAT32[]`) but
// rarely exceeds ~10 chars; a flat allowance keeps measurement cheap
// and avoids re-measuring on theme / font swaps.
const HEADER_BADGE_BUDGET = 60;

// Module-level canvas reused across column measurements. Recreating the
// canvas + context per call is the slow part of `measureText`; reusing
// keeps per-cell measurement to a single call into the rasteriser.
let measureCanvas: HTMLCanvasElement | null = null;
let measureCtx: CanvasRenderingContext2D | null = null;

function getMeasureCtx(): CanvasRenderingContext2D | null {
  if (typeof document === 'undefined') return null;
  if (measureCtx) return measureCtx;
  measureCanvas = document.createElement('canvas');
  const ctx = measureCanvas.getContext('2d');
  if (!ctx) return null;
  // Matches the rendered cell font: `font-mono` at `text-xs` (12 px).
  // Canvas can't resolve CSS variables, so we go with the literal
  // generic family — the few pixels of width drift from the real
  // bundled font are absorbed by COL_PADDING.
  ctx.font = '12px ui-monospace, monospace';
  measureCtx = ctx;
  return ctx;
}

function textWidth(text: string): number {
  const ctx = getMeasureCtx();
  if (!ctx) return text.length * 7;
  return ctx.measureText(text).width;
}

function cellTextForMeasure(cell: JsonCell): string {
  if (cell.kind === 'null') return 'null';
  // Media cells render as a small icon / thumbnail + size label. We
  // don't need to measure precisely — a flat allowance covers the
  // visual width without recomputing per-blob.
  if (
    cell.kind === 'media' ||
    cell.kind === 'image' ||
    cell.kind === 'audio' ||
    cell.kind === 'video'
  ) {
    return '[media, 9999 KB]';
  }
  if (cell.kind === 'media_array' && cell.items) {
    return `[${cell.items.length} items]`;
  }
  if (cell.kind === 'numeric_array') {
    // Measurement string mirrors the rendered chip — "f32[147456] · [0.00..9.99]".
    // Just an upper-bound approximation; the chip is short enough that exact
    // measurement isn't worth a per-cell render walk.
    return `${cell.elementKind ?? '?'}[${cell.count ?? 0}] · [0.0000..9.9999]`;
  }
  return cell.text ?? '';
}

function measureColumnWidth(
  columnName: string,
  rows: readonly JsonCell[][],
  colIdx: number,
): number {
  // Header: column name + space for the type badge (rendered next to
  // the name; flat allowance below).
  let widest = textWidth(columnName) + HEADER_BADGE_BUDGET;

  const sampleCount = Math.min(rows.length, SAMPLE_ROWS);
  for (let i = 0; i < sampleCount; i++) {
    const c = rows[i][colIdx];
    if (!c) continue;
    const w = textWidth(cellTextForMeasure(c));
    if (w > widest) widest = w;
  }
  return Math.max(COL_MIN_WIDTH, Math.min(COL_MAX_WIDTH, Math.ceil(widest + COL_PADDING)));
}

function cellTooltip(cell: JsonCell): string | undefined {
  // Tooltip mirrors the rendered text, so the user can hover a
  // truncated cell to read the full value. Returning undefined for
  // null cells suppresses the "null" hover (the italic-rendered cell
  // already labels itself). Media cells have their own per-element
  // titles set inside the renderers; the cell-wrapper title here is
  // a fallback only — undefined so it doesn't shadow the inner one.
  if (cell.kind === 'null') return undefined;
  if (
    cell.kind === 'media' ||
    cell.kind === 'image' ||
    cell.kind === 'audio' ||
    cell.kind === 'video' ||
    cell.kind === 'media_array' ||
    cell.kind === 'numeric_array'
  ) {
    return undefined;
  }
  return cell.text ?? undefined;
}

function CellTable({ cell }: { cell: CellResult }) {
  // Per-cell scroll container that also drives the virtualiser. Sticky
  // `<header>` and absolutely-positioned virtualised rows both live
  // inside it, so vertical scroll moves rows past the pinned header
  // and horizontal scroll moves header + rows together (their grid
  // templates match, so columns stay aligned).
  const scrollRef = useRef<HTMLDivElement>(null);

  // Detect image/video content in the grid to decide whether to use
  // the tall row mode. Sample-bounded for the same reason `colWidths`
  // is — a streaming query with 1000s of rows shouldn't pay an O(rows)
  // scan on every appended row. SAMPLE_ROWS is plenty to spot the
  // first media cell, after which the dep stabilises.
  const sampleSize = Math.min(cell.rows.length, SAMPLE_ROWS);
  const largeMedia = useMemo(
    () => rowsContainImageOrVideo(cell.rows.slice(0, sampleSize)),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [cell.schema, sampleSize],
  );
  const rowHeight = largeMedia ? ROW_HEIGHT_MEDIA : ROW_HEIGHT;

  const rowVirtualizer = useVirtualizer({
    count: cell.rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => rowHeight,
    overscan: 12,
  });

  // Content-based column widths. Memoised on the schema + sample-bounded
  // row count: we only ever look at the first SAMPLE_ROWS rows, so once
  // the stream has produced that many rows the dep stops changing and we
  // hold a stable width set for the rest of the run. Without the cap, a
  // 2000-row stream would re-measure on every appended row (O(cols *
  // SAMPLE_ROWS) per render = millions of canvas measureText calls).
  // `sampleSize` was already computed above for the media-detection
  // memo; reused here so both checks stabilise on the same boundary.
  const colWidths = useMemo(() => {
    if (cell.schema === null) return [] as number[];
    return cell.schema.map((col, idx) =>
      measureColumnWidth(col.name, cell.rows, idx),
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cell.schema, sampleSize]);

  if (cell.schema === null) return null;

  // Leading row-number gutter + data columns. Sticky-left only on the
  // gutter; data columns scroll freely.
  const gridTemplateColumns = [
    `${ROW_NUMBER_WIDTH}px`,
    ...colWidths.map((w) => `${w}px`),
  ].join(' ');
  const totalWidth = ROW_NUMBER_WIDTH + colWidths.reduce((s, w) => s + w, 0);
  const virtualRows = rowVirtualizer.getVirtualItems();

  return (
    <div
      ref={scrollRef}
      className="bg-table-pane min-h-0 flex-1 overflow-auto text-xs"
    >
      {/* Sticky header. Sits at top of scroll container; a real
          `border-b` draws the bottom rule (each header cell's bg-muted
          would paint over the previous inset-box-shadow trick). The
          leading corner cell (above the row-number gutter) is also
          sticky-left + bumped z so it stays pinned through both axes. */}
      <div
        className="bg-muted border-border sticky top-0 z-20 grid border-b font-medium"
        style={{ gridTemplateColumns, minWidth: totalWidth }}
      >
        {/* Corner cell over the row-number gutter — empty. */}
        <div
          className="border-border bg-muted sticky left-0 z-30 border-r"
          aria-hidden="true"
        />
        {cell.schema.map((col) => (
          <div
            key={col.name}
            className="border-border bg-muted flex min-w-0 cursor-default items-center gap-1.5 border-r px-2 py-1 select-none last:border-r-0"
            title={`${col.kind}${col.isArray ? '[]' : ''}`}
          >
            <span className="truncate">{col.name}</span>
            <Badge variant="muted" className="shrink-0 font-mono text-[10px] leading-none">
              {col.kind}
              {col.isArray ? '[]' : ''}
            </Badge>
          </div>
        ))}
      </div>

      {/* Virtual rows container — total scroll height comes from the
          virtualiser; each row is absolutely positioned via translateY.
          minWidth matches the header so horizontal scroll lines up. */}
      <div
        className="relative"
        style={{
          height: rowVirtualizer.getTotalSize(),
          minWidth: totalWidth,
        }}
      >
        {virtualRows.map((virtualRow) => {
          const row = cell.rows[virtualRow.index];
          const rowNumber = virtualRow.index + 1;
          return (
            <div
              key={virtualRow.key}
              data-index={virtualRow.index}
              className="border-border bg-table-row absolute inset-x-0 grid border-b"
              style={{
                gridTemplateColumns,
                height: virtualRow.size,
                transform: `translateY(${virtualRow.start}px)`,
              }}
            >
              {/* Row-number gutter. Styled like a header cell
                  (bg-muted + font-medium) and sticky-left so it acts as
                  the row's identity column during horizontal scroll. */}
              <div
                className="border-border bg-muted text-muted-foreground sticky left-0 z-10 flex min-w-0 items-center justify-end border-r px-1.5 font-medium tabular-nums select-none"
                title={String(rowNumber)}
              >
                <span className="truncate">{rowNumber}</span>
              </div>
              {row.map((c, colIdx) => (
                <div
                  key={colIdx}
                  title={cellTooltip(c)}
                  className={cn(
                    'border-border min-w-0 border-r px-2 py-1 font-mono last:border-r-0',
                    largeMedia
                      ? 'flex items-center overflow-hidden'
                      : 'truncate align-top',
                  )}
                >
                  <CellValue cell={c} largeMedia={largeMedia} />
                </div>
              ))}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function CellValue({
  cell,
  largeMedia = false,
}: {
  cell: JsonCell;
  largeMedia?: boolean;
}) {
  if (cell.kind === 'null') {
    return <span className="text-muted-foreground italic">null</span>;
  }
  // The server lumps every single-blob media value into `kind: "media"`
  // and discriminates by the `mime` prefix. The legacy `image` /
  // `audio` / `video` kind names aren't emitted today — left in the
  // dispatch as a forward-compat hook in case the server ever splits
  // them out.
  if (cell.kind === 'media' || cell.kind === 'image' || cell.kind === 'audio' || cell.kind === 'video') {
    const mime = cell.mime ?? '';
    if (mime.startsWith('image/') || cell.kind === 'image') return <ImageCell cell={cell} largeMedia={largeMedia} />;
    if (mime.startsWith('audio/') || cell.kind === 'audio') return <AudioCell cell={cell} />;
    if (mime.startsWith('video/') || cell.kind === 'video') return <VideoCell cell={cell} largeMedia={largeMedia} />;
    // Unknown mime → still surface that it's a blob the user could
    // download rather than rendering empty.
    return <BinaryCell cell={cell} />;
  }
  if (cell.kind === 'media_array' && cell.items) return <MediaArrayCell cell={cell} largeMedia={largeMedia} />;
  if (cell.kind === 'pointcloud') return <PointCloudCell cell={cell} largeMedia={largeMedia} />;
  if (cell.kind === 'mesh') return <MeshCell cell={cell} largeMedia={largeMedia} />;
  if (cell.kind === 'numeric_array') return <NumericArrayCell cell={cell} />;
  return <span>{cell.text ?? ''}</span>;
}

function BinaryCell({ cell }: { cell: JsonCell }) {
  const bytes = bytesFromBase64(cell.dataB64);
  return (
    <span className="text-muted-foreground" title={`${cell.mime ?? 'binary'} · ${formatBytes(bytes)}`}>
      [{cell.mime ?? 'binary'}, {formatBytes(bytes)}]
    </span>
  );
}

// ────────── Typed-media renderers ──────────
//
// All four use base64 data URIs (`data:${mime};base64,${dataB64}`) sourced
// from the server's WebCellFormatter. Each cell shows a compact inline
// preview (image thumbnail, audio/video glyph) and a click handler that
// opens the full media in a centered <MediaPreview> portal. The 28 px
// row height stays intact — the inline form fits in `h-6`. Per-cell
// `useState` for `open` is fine even at scale because virtualisation
// only mounts visible cells.

function bytesFromBase64(dataB64: string | undefined): number {
  return dataB64 ? Math.floor((dataB64.length * 3) / 4) : 0;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function dataUriOf(cell: JsonCell): string {
  return `data:${cell.mime ?? 'application/octet-stream'};base64,${cell.dataB64 ?? ''}`;
}

function ImageCell({ cell, largeMedia = false }: { cell: JsonCell; largeMedia?: boolean }) {
  const [open, setOpen] = useState(false);
  const src = dataUriOf(cell);
  const bytes = bytesFromBase64(cell.dataB64);
  return (
    <>
      <img
        src={src}
        alt=""
        loading="lazy"
        onClick={() => setOpen(true)}
        title={`image · ${formatBytes(bytes)}`}
        className={cn(
          'hover:ring-primary inline-block max-w-full cursor-zoom-in rounded-xs object-contain hover:ring-1',
          largeMedia ? 'h-16' : 'h-5',
        )}
      />
      <MediaPreview
        open={open}
        onClose={() => setOpen(false)}
        title={`image · ${cell.mime ?? 'unknown'} · ${formatBytes(bytes)}`}
      >
        <img src={src} alt="" className="max-h-[80vh] max-w-[80vw] object-contain" />
      </MediaPreview>
    </>
  );
}

function AudioCell({ cell }: { cell: JsonCell }) {
  const [open, setOpen] = useState(false);
  const src = dataUriOf(cell);
  const bytes = bytesFromBase64(cell.dataB64);
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title={`audio · ${cell.mime ?? 'unknown'} · ${formatBytes(bytes)}`}
        className="text-muted-foreground hover:text-foreground inline-flex cursor-pointer items-center gap-1"
      >
        <Music className="size-3.5" />
        <span>{formatBytes(bytes)}</span>
      </button>
      <MediaPreview
        open={open}
        onClose={() => setOpen(false)}
        title={`audio · ${cell.mime ?? 'unknown'} · ${formatBytes(bytes)}`}
      >
        <audio src={src} controls className="w-[40vw] min-w-[320px]" />
      </MediaPreview>
    </>
  );
}

function VideoCell({ cell, largeMedia = false }: { cell: JsonCell; largeMedia?: boolean }) {
  const [open, setOpen] = useState(false);
  const src = dataUriOf(cell);
  const bytes = bytesFromBase64(cell.dataB64);
  // Larger row → render a clickable inline preview frame instead of the
  // glyph-only button. The video doesn't autoplay (no `autoPlay`) — the
  // user opens the modal for playback. `preload="metadata"` gets enough
  // of the file for the browser to render a poster frame.
  if (largeMedia) {
    return (
      <>
        <button
          type="button"
          onClick={() => setOpen(true)}
          title={`video · ${cell.mime ?? 'unknown'} · ${formatBytes(bytes)}`}
          className="hover:ring-primary cursor-zoom-in rounded-xs hover:ring-1"
        >
          <video
            src={src}
            preload="metadata"
            muted
            className="h-16 max-w-full object-contain"
          />
        </button>
        <MediaPreview
          open={open}
          onClose={() => setOpen(false)}
          title={`video · ${cell.mime ?? 'unknown'} · ${formatBytes(bytes)}`}
        >
          <video src={src} controls className="max-h-[80vh] max-w-[80vw]" />
        </MediaPreview>
      </>
    );
  }
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title={`video · ${cell.mime ?? 'unknown'} · ${formatBytes(bytes)}`}
        className="text-muted-foreground hover:text-foreground inline-flex cursor-pointer items-center gap-1"
      >
        <Film className="size-3.5" />
        <span>{formatBytes(bytes)}</span>
      </button>
      <MediaPreview
        open={open}
        onClose={() => setOpen(false)}
        title={`video · ${cell.mime ?? 'unknown'} · ${formatBytes(bytes)}`}
      >
        <video src={src} controls className="max-h-[80vh] max-w-[80vw]" />
      </MediaPreview>
    </>
  );
}

// ────────── Numeric array (binary transport) ──────────
//
// Server-side `WebCellFormatter` switches large fixed-width numeric
// arrays from JSON-text (`[0.123, 0.456, ...]`) to a `numeric_array`
// cell carrying raw little-endian bytes in `dataB64` plus pre-computed
// min/max/mean. The grid chip below renders the stats without ever
// touching the bytes — that's the point of the wire-format change.
// Click opens a modal that decodes the bytes into a typed-array view
// and shows the first chunk inline; full inspection is deferred to a
// future "View all values" expansion if the user needs it.

function formatStat(value: number): string {
  if (!Number.isFinite(value)) return value.toString();
  const abs = Math.abs(value);
  if (abs !== 0 && (abs < 0.001 || abs >= 1e6)) return value.toExponential(2);
  return value.toFixed(4).replace(/\.?0+$/, '');
}

function numericArrayTitle(cell: JsonCell): string {
  const k = cell.elementKind ?? '?';
  const n = cell.count ?? 0;
  const parts = [`${k}[${n.toLocaleString()}]`];
  if (cell.min !== undefined && cell.max !== undefined) {
    parts.push(`min ${formatStat(cell.min)}`, `max ${formatStat(cell.max)}`);
  }
  if (cell.mean !== undefined) parts.push(`mean ${formatStat(cell.mean)}`);
  return parts.join(' · ');
}

function bytesPerElement(elementKind: string | undefined): number {
  switch (elementKind) {
    case 'bool':
    case 'u8':
    case 'i8':
      return 1;
    case 'u16':
    case 'i16':
      return 2;
    case 'u32':
    case 'i32':
    case 'f32':
      return 4;
    case 'u64':
    case 'i64':
    case 'f64':
      return 8;
    default:
      return 0;
  }
}

/**
 * Decodes the first `maxCount` elements of the array into a regular
 * Number[]. We slice the base64 input so we don't allocate the full
 * typed array — for a 1M-element Float32 cell that's an 8 KB decode
 * instead of 4 MB. Returns null when the element kind isn't supported
 * or `dataB64` is missing.
 */
function decodeNumericArrayHead(cell: JsonCell, maxCount: number): number[] | null {
  if (!cell.dataB64 || !cell.elementKind) return null;
  const bpe = bytesPerElement(cell.elementKind);
  if (bpe === 0) return null;
  const wantBytes = Math.min(maxCount, cell.count ?? 0) * bpe;
  // base64 → bytes; only decode the prefix we actually need. base64 is
  // groups of 4 chars → 3 bytes, so round up to the next group boundary.
  const groups = Math.ceil(wantBytes / 3);
  const headChars = Math.min(cell.dataB64.length, groups * 4);
  const binStr = atob(cell.dataB64.substring(0, headChars));
  const bytes = new Uint8Array(binStr.length);
  for (let i = 0; i < binStr.length; i++) bytes[i] = binStr.charCodeAt(i);
  // Trim to a whole-element boundary so the typed view doesn't read
  // half an element off the end.
  const usable = Math.floor(bytes.length / bpe) * bpe;
  const buffer = bytes.buffer.slice(0, usable);
  const take = Math.min(maxCount, usable / bpe);
  switch (cell.elementKind) {
    case 'bool':
      return Array.from(new Uint8Array(buffer, 0, take), (v) => (v ? 1 : 0));
    case 'u8':
      return Array.from(new Uint8Array(buffer, 0, take));
    case 'i8':
      return Array.from(new Int8Array(buffer, 0, take));
    case 'u16':
      return Array.from(new Uint16Array(buffer, 0, take));
    case 'i16':
      return Array.from(new Int16Array(buffer, 0, take));
    case 'u32':
      return Array.from(new Uint32Array(buffer, 0, take));
    case 'i32':
      return Array.from(new Int32Array(buffer, 0, take));
    case 'f32':
      return Array.from(new Float32Array(buffer, 0, take));
    case 'f64':
      return Array.from(new Float64Array(buffer, 0, take));
    case 'u64':
      // BigUint64Array → Number; precision-lossy near 2^53 but
      // acceptable for inspection-only display.
      return Array.from(new BigUint64Array(buffer, 0, take), (v) => Number(v));
    case 'i64':
      return Array.from(new BigInt64Array(buffer, 0, take), (v) => Number(v));
    default:
      return null;
  }
}

function NumericArrayCell({ cell }: { cell: JsonCell }) {
  const [open, setOpen] = useState(false);
  const title = numericArrayTitle(cell);
  const kind = cell.elementKind ?? '?';
  const count = cell.count ?? 0;
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title={title}
        className="text-muted-foreground hover:text-foreground inline-flex cursor-pointer items-center gap-1"
      >
        <Sigma className="size-3.5" />
        <span className="font-mono">
          {kind}[{count.toLocaleString()}]
        </span>
        {cell.min !== undefined && cell.max !== undefined && (
          <span className="text-foreground/60 font-mono">
            [{formatStat(cell.min)}..{formatStat(cell.max)}]
          </span>
        )}
      </button>
      <MediaPreview open={open} onClose={() => setOpen(false)} title={title}>
        <NumericArrayInspector cell={cell} />
      </MediaPreview>
    </>
  );
}

function SingleValueNumericArray({ cell }: { cell: JsonCell }) {
  return (
    <div className="flex max-h-full w-full max-w-3xl flex-col gap-4 overflow-auto p-4">
      <NumericArrayInspector cell={cell} />
    </div>
  );
}

/**
 * Stats grid + first-N values preview. Used by both the modal (opened
 * from the inline chip) and the single-value pane. The preview decode
 * is bounded — we never materialise the full array on first render.
 */
function NumericArrayInspector({ cell }: { cell: JsonCell }) {
  const PREVIEW_COUNT = 64;
  const kind = cell.elementKind ?? '?';
  const count = cell.count ?? 0;
  const preview = useMemo(
    () => decodeNumericArrayHead(cell, PREVIEW_COUNT),
    [cell],
  );
  const stats: { label: string; value: string }[] = [
    { label: 'kind', value: kind },
    { label: 'count', value: count.toLocaleString() },
  ];
  if (cell.min !== undefined) stats.push({ label: 'min', value: formatStat(cell.min) });
  if (cell.max !== undefined) stats.push({ label: 'max', value: formatStat(cell.max) });
  if (cell.mean !== undefined) stats.push({ label: 'mean', value: formatStat(cell.mean) });
  return (
    <div className="flex flex-col gap-3">
      <div className="grid grid-cols-[auto,1fr] gap-x-4 gap-y-1 text-sm">
        {stats.map((s) => (
          <div key={s.label} className="contents">
            <div className="text-muted-foreground font-mono">{s.label}</div>
            <div className="font-mono">{s.value}</div>
          </div>
        ))}
      </div>
      {preview && preview.length > 0 && (
        <div className="flex flex-col gap-1">
          <div className="text-muted-foreground text-xs">
            first {preview.length.toLocaleString()} of {count.toLocaleString()}
          </div>
          <pre className="bg-muted/40 border-border max-h-96 overflow-auto rounded-xs border p-2 font-mono text-xs whitespace-pre-wrap">
            {preview.map((v) => formatStat(v)).join(', ')}
            {count > preview.length ? ', …' : ''}
          </pre>
        </div>
      )}
    </div>
  );
}

function MediaArrayCell({ cell, largeMedia = false }: { cell: JsonCell; largeMedia?: boolean }) {
  const [open, setOpen] = useState(false);
  const items = cell.items ?? [];
  // Render the first up-to-3 thumbnails inline; click any to open the
  // full grid in the preview. Falls back to a count badge when items
  // aren't image-like (audio/video arrays are theoretical today).
  const previewItems = items.slice(0, 3);
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title={`${items.length} items`}
        className="hover:text-foreground inline-flex cursor-pointer items-center gap-1"
      >
        {previewItems.map((it, i) => (
          <img
            key={i}
            src={`data:${it.mime};base64,${it.dataB64}`}
            alt=""
            loading="lazy"
            className={cn(
              'inline-block rounded-xs object-cover',
              largeMedia ? 'h-16 w-16' : 'h-5 w-5',
            )}
          />
        ))}
        <span className="text-muted-foreground">{items.length}</span>
      </button>
      <MediaPreview
        open={open}
        onClose={() => setOpen(false)}
        title={`${items.length} items`}
      >
        <div className="grid max-h-[80vh] grid-cols-[repeat(auto-fill,minmax(140px,1fr))] gap-2 overflow-auto">
          {items.map((it, i) => (
            <img
              key={i}
              src={`data:${it.mime};base64,${it.dataB64}`}
              alt=""
              loading="lazy"
              className="bg-muted/40 h-32 w-full rounded-xs object-contain"
            />
          ))}
        </div>
      </MediaPreview>
    </>
  );
}
