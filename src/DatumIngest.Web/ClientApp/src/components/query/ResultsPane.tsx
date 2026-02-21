import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { AlertCircle, Ban, Check, Loader2 } from 'lucide-react';
import {
  executionsState,
  type CellResult,
  type ExecutionStatus,
  type JsonCell,
  type TabExecution,
} from '@/state/execution';
import { tabsState } from '@/state/tabs';
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

export function ResultsPane() {
  const { t } = useTranslation('query');
  const { activeTabId } = useSnapshot(tabsState);
  const { byTabId } = useSnapshot(executionsState);

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
  // nothing visible. Render the hint, no status bar (StatusBar hides
  // itself on idle anyway).
  if (!exec || (visibleCells.length === 0 && exec.error === null)) {
    return (
      <div className="flex h-full flex-col overflow-hidden">
        <div className="text-muted-foreground flex flex-1 items-center justify-center text-xs">
          {t('resultsEmpty')}
        </div>
        {exec && <StatusBar exec={exec as TabExecution} />}
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
          only ever scrolls *between* tables, never *within* one. */}
      <div className="flex-1 overflow-auto">
        {exec.error !== null && (
          <div className="text-destructive border-destructive/40 bg-destructive/10 border-b px-3 py-2 font-mono text-xs whitespace-pre-wrap">
            {exec.error}
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
        {exec.trace !== null && (
          <pre className="text-muted-foreground border-t p-2 font-mono text-xs whitespace-pre-wrap">
            {exec.trace}
          </pre>
        )}
      </div>
      <StatusBar exec={exec as TabExecution} />
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

function StatusBar({ exec }: { exec: TabExecution }) {
  const { t } = useTranslation('query');

  // Live timer during streaming. The exec's startedAt is fixed; we re-
  // render once a second so the "Executing…" duration ticks. After
  // termination we lock to exec.elapsedMs (the server's measurement,
  // not our client clock).
  const [tickMs, setTickMs] = useState<number>(() =>
    exec.status === 'streaming' && exec.startedAt !== null
      ? Date.now() - exec.startedAt
      : exec.elapsedMs ?? 0,
  );
  useEffect(() => {
    if (exec.status !== 'streaming' || exec.startedAt === null) return;
    const start = exec.startedAt;
    const id = window.setInterval(() => {
      setTickMs(Date.now() - start);
    }, 1000);
    return () => window.clearInterval(id);
  }, [exec.status, exec.startedAt]);

  if (exec.status === 'idle') return null;

  const hasError =
    exec.error !== null || exec.cells.some((c) => c.error !== null);
  const totalRows = exec.cells.reduce((sum, c) => sum + c.rowCount, 0);
  const elapsedMs =
    exec.status === 'streaming' ? tickMs : exec.elapsedMs ?? tickMs;

  let leftMessage: string;
  if (exec.status === 'streaming') leftMessage = t('statusBarRunning');
  else if (exec.status === 'cancelled') leftMessage = t('statusBarCancelled');
  else if (hasError) leftMessage = t('statusBarError');
  else leftMessage = t('statusBarSuccess');

  // Three panels separated by a faint divider in the foreground colour:
  // status message (grows), duration (fixed), row count (fixed). Each
  // owns its padding so the dividers sit cleanly between sections. All
  // panels truncate on overflow rather than wrap — `min-w-0` on the
  // grow-panel is required to let its content shrink below its natural
  // width (flex items default to min-width: auto, which prevents
  // ellipsis on long status messages).
  return (
    <div className="bg-status-bar text-status-bar-foreground border-border flex shrink-0 items-stretch overflow-hidden border-t text-xs">
      <div className="flex min-w-0 flex-1 items-center gap-1.5 px-3 py-1">
        <StatusIcon status={exec.status} hasError={hasError} />
        <span className="truncate">{leftMessage}</span>
      </div>
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
  // cancelled; the toolbar's spinner during run.
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

// Formats elapsed milliseconds as `HHh MMm SSs`. Uses Intl.DurationFormat
// when the runtime exposes it (Chromium ≥ 129, Electron ≥ 33) and falls
// back to a manual padded format otherwise so the bar renders the same
// shape on older runtimes. Always-positive — negative would mean a
// clock-skew bug upstream.
function formatDuration(ms: number): string {
  const totalSeconds = Math.max(0, Math.floor(ms / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  // Try Intl.DurationFormat first — gives locale-aware unit labels in
  // non-English runtimes once that lands. `narrow` style emits "h/m/s"
  // suffixes which match the user-requested shape.
  type DurationFormatCtor = new (
    locale: string,
    options: { style: 'narrow' },
  ) => { format(input: { hours: number; minutes: number; seconds: number }): string };
  const ctor = (Intl as unknown as { DurationFormat?: DurationFormatCtor }).DurationFormat;
  if (ctor) {
    try {
      const fmt = new ctor('en', { style: 'narrow' });
      const formatted = fmt.format({ hours, minutes, seconds });
      // Intl emits no leading zeros; the spec wants `00h 00m 00s`, so
      // when the result lacks them, fall through to the manual path.
      if (/^\d{2}h \d{2}m \d{2}s$/.test(formatted)) return formatted;
    } catch {
      /* fall through to manual formatter */
    }
  }

  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(hours)}h ${pad(minutes)}m ${pad(seconds)}s`;
}

function CellBlock({
  cell,
  tableMode,
}: {
  cell: CellResult;
  tableMode: TableMode;
}) {
  const hasTable = cell.schema !== null && cell.rows.length > 0;
  // Section is a flex column so the table area can `flex-1` into the
  // remaining height after errors / chunks. The section's own height
  // is constrained only when it carries a table — non-table cells
  // (error-only, chunk-only) keep auto height so they don't reserve a
  // 200 px slab they wouldn't use.
  const heightClass = hasTable
    ? tableMode === 'fill'
      ? 'h-full'
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
      {hasTable && (
        // Per-cell scroll container. Sticky `<th>`s pin against this
        // element's top, not the outer pane — so each grid keeps its
        // header glued in place independently as the outer scrolls
        // between grids.
        <div className="min-h-0 flex-1 overflow-auto">
          <CellTable cell={cell} />
        </div>
      )}
    </section>
  );
}

function CellTable({ cell }: { cell: CellResult }) {
  if (cell.schema === null) return null;
  // No outer wrapper: the parent ResultsPane scroll container owns both
  // axes, and sticky `<th>` elements pin against it. `border-collapse:
  // collapse` would lose the header's bottom-border once it scrolls
  // past, so we use `box-shadow:inset 0 -1px 0` via the `shadow-…`
  // arbitrary class on the header — that stays glued to the cell even
  // when sticky. Per-row borders + alternating bg give the grid feel.
  return (
    <table className="w-full border-collapse text-xs">
      <thead>
        <tr>
          {cell.schema.map((col) => (
            <th
              key={col.name}
              className="border-border bg-muted sticky top-0 z-10 border-r px-2 py-1 text-left text-xs font-medium last:border-r-0 [box-shadow:inset_0_-1px_0_var(--border)]"
              title={`${col.kind}${col.isArray ? '[]' : ''}`}
            >
              <div className="flex items-center gap-1.5">
                <span>{col.name}</span>
                <Badge variant="muted" className="font-mono text-[10px] leading-none">
                  {col.kind}
                  {col.isArray ? '[]' : ''}
                </Badge>
              </div>
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {cell.rows.map((row, rowIdx) => (
          <tr
            key={rowIdx}
            className={cn(
              'border-border border-b last:border-b-0',
              rowIdx % 2 === 1 && 'bg-muted/10',
            )}
          >
            {row.map((c, colIdx) => (
              <td
                key={colIdx}
                className="border-border border-r px-2 py-1 align-top font-mono last:border-r-0"
              >
                <CellValue cell={c} />
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function CellValue({ cell }: { cell: JsonCell }) {
  if (cell.kind === 'null') {
    return <span className="text-muted-foreground italic">null</span>;
  }
  // Typed-media renderers land in PR 5 — for now show a compact hint so
  // the user knows there's a blob there.
  if (cell.kind === 'image' || cell.kind === 'audio' || cell.kind === 'video') {
    const bytes = cell.dataB64 ? Math.floor((cell.dataB64.length * 3) / 4) : 0;
    return <span className="text-muted-foreground">[{cell.kind}, {bytes} bytes]</span>;
  }
  if (cell.kind === 'media_array' && cell.items) {
    return <span className="text-muted-foreground">[{cell.items.length} items]</span>;
  }
  return <span>{cell.text ?? ''}</span>;
}
