import { useMemo, useState } from 'react';
import { Popover } from '@base-ui/react/popover';
import { useTranslation } from 'react-i18next';
import {
  type TraceState,
  type TraceEntry,
  clearTrace,
  setTraceOperators,
  setTraceScalars,
} from '../../state/execution';

// Status-bar trace controls: a persistent "Trace" checkbox the user can
// flip on for the next run, paired with a click-to-open chip showing the
// captured-event count + sparkline. Both halves share one popover with
// the scope toggles + scrollable event timeline + clear/copy/download.
//
// Layout decisions:
//   - Checkbox always visible (tabs spend most time idle; the toggle is
//     the discoverable entry point even before any data exists).
//   - Chip portion (count + sparkline) appears only after the first
//     trace_sample arrives — same "earned" rule the memory chip uses.
//   - Clicking either half opens the popover. The checkbox's label-as-
//     button doesn't toggle the popover; the checkbox toggles tracing
//     and the chip half opens detail.

const SPARKLINE_BUCKET_MS = 100; // events-per-bucket for the rate sparkline

// Grid template for the popover's event table.
//   ts:        ~5.5ch covers "12345.6" (1m+ runs); right-aligned.
//   source:    fixed 3.5ch — fits "op" / "fn" with room for future tags.
//   name:      flex column, minmax(0, ...) so truncation works.
//   parent:    flex column, narrower than name (parents are usually the
//              owning batch like "ProjectOperator.batch[3]" so a 1fr is
//              plenty); minmax(0, ...) again for truncation.
//   duration:  ~7ch covers "999.99ms" with right alignment.
const TRACE_GRID_TEMPLATE =
  '5.5rem 3.5rem minmax(0, 2fr) minmax(0, 1fr) 5.5rem';

export interface TraceChipProps {
  tabId: string;
  trace: TraceState;
}

export function TraceChip({ tabId, trace }: TraceChipProps) {
  const { t } = useTranslation('query');

  const eventCount = trace.events.length;
  const sparkBuckets = useMemo(
    () => bucketEventsPerInterval(trace.events, SPARKLINE_BUCKET_MS),
    [trace.events],
  );

  // The checkbox is rendered as a stand-alone control so clicking the
  // glyph doesn't also bubble up to the popover-trigger button.
  function onCheckboxChange(e: React.ChangeEvent<HTMLInputElement>) {
    setTraceOperators(tabId, e.target.checked);
  }

  return (
    <div className="border-status-bar-foreground/25 flex shrink-0 items-stretch whitespace-nowrap border-l font-mono">
      {/* Checkbox half — always visible. */}
      <label
        className="text-status-bar-foreground hover:bg-status-bar-foreground/10 flex shrink-0 cursor-pointer items-center gap-1.5 px-3 py-1"
        title={t('traceToggleTooltip')}
      >
        <input
          type="checkbox"
          checked={trace.enabledOperators}
          onChange={onCheckboxChange}
          className="cursor-pointer"
          aria-label={t('traceToggleLabel')}
        />
        <span>{t('traceToggleLabel')}</span>
      </label>

      {/* Chip half — always visible so the popover is reachable even
          before the first run (the user needs to flip Scalars on from
          inside the popover, which the previous "hide when empty" rule
          made impossible without first running a throwaway query). */}
      <Popover.Root>
        <Popover.Trigger
          className="border-status-bar-foreground/25 text-status-bar-foreground hover:bg-status-bar-foreground/10 flex shrink-0 cursor-pointer items-center gap-1.5 border-l px-3 py-1"
          aria-label={t('traceChipTooltip')}
        >
          <span>{t('traceChipEvents', { count: eventCount })}</span>
          {eventCount > 0 && <MiniSparkline buckets={sparkBuckets} />}
          {trace.dropped > 0 && (
            <span className="text-amber-700 dark:text-amber-500">
              {t('traceChipDropped', { count: trace.dropped })}
            </span>
          )}
        </Popover.Trigger>
          <Popover.Portal>
            {/* z-index lives on the positioner, not just the popup, so
                the entire positioned subtree (including the popup) sits
                above the results table's sticky header (which itself
                creates a z-20 stacking context inside the scroll
                container). Base UI's positioner manages its own
                position/transform but doesn't apply z-index by default,
                which is why a popup-only z-50 falls behind the header. */}
            <Popover.Positioner
              side="top"
              align="end"
              sideOffset={6}
              className="z-[100]"
            >
              <Popover.Popup className="bg-popover text-popover-foreground border-border w-[48rem] rounded-md border p-3 shadow-md">
                <TracePopoverBody tabId={tabId} trace={trace} />
              </Popover.Popup>
            </Popover.Positioner>
        </Popover.Portal>
      </Popover.Root>
    </div>
  );
}

interface TracePopoverBodyProps {
  tabId: string;
  trace: TraceState;
}

function TracePopoverBody({ tabId, trace }: TracePopoverBodyProps) {
  const { t } = useTranslation('query');
  const [filter, setFilter] = useState('');

  // Filter is a case-insensitive substring match on `name` OR `parent`
  // OR `source` — covers the natural ways a user would look something
  // up ("scan", "infer", "fn"). Memoised on (events, filter) so typing
  // doesn't re-walk the entire array on each keystroke for the common
  // "trace is small" case.
  const filtered = useMemo(() => {
    if (!filter) return trace.events;
    const needle = filter.toLowerCase();
    return trace.events.filter((e) =>
      e.name.toLowerCase().includes(needle)
      || (e.parent !== null && e.parent.toLowerCase().includes(needle))
      || e.source.toLowerCase().includes(needle),
    );
  }, [trace.events, filter]);

  function onScalarsToggle(e: React.ChangeEvent<HTMLInputElement>) {
    setTraceScalars(tabId, e.target.checked);
  }

  function onOperatorsToggle(e: React.ChangeEvent<HTMLInputElement>) {
    setTraceOperators(tabId, e.target.checked);
  }

  function copyAsText() {
    const text = trace.events
      .map((e) => formatTraceLine(e))
      .join('\n');
    navigator.clipboard.writeText(text).catch(() => {
      // Clipboard may be unavailable (insecure context, permissions
      // denied). Silent failure — the user can re-try or use download
      // as a fallback.
    });
  }

  function downloadAsNdjson() {
    const ndjson = trace.events
      .map((e) => JSON.stringify(e))
      .join('\n');
    const blob = new Blob([ndjson], { type: 'application/x-ndjson' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `trace-${new Date().toISOString().replace(/[:.]/g, '-')}.ndjson`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="text-sm font-medium select-none">{t('tracePopoverTitle')}</div>

      {/* Scope toggles. The Operators toggle here mirrors the status-
          bar checkbox; both write to the same state. Scalars-on is the
          much-larger trace so we keep it opt-in inside the popover. */}
      <div className="flex flex-row gap-3 select-none">
        <label className="flex cursor-pointer items-center gap-1.5 text-xs">
          <input
            type="checkbox"
            checked={trace.enabledOperators}
            onChange={onOperatorsToggle}
            className="cursor-pointer"
          />
          <span>{t('traceScopeOperators')}</span>
        </label>
        <label className="flex cursor-pointer items-center gap-1.5 text-xs">
          <input
            type="checkbox"
            checked={trace.enabledScalars}
            onChange={onScalarsToggle}
            className="cursor-pointer"
          />
          <span>{t('traceScopeScalars')}</span>
        </label>
      </div>

      <input
        type="text"
        value={filter}
        onChange={(e) => setFilter(e.target.value)}
        placeholder={t('traceFilterPlaceholder')}
        className="border-border bg-background w-full rounded-xs border px-2 py-1 font-mono text-xs"
      />

      {trace.dropped > 0 && (
        <div className="bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-200 rounded-xs px-2 py-1 text-xs">
          {t('traceDroppedBanner', { count: trace.dropped })}
        </div>
      )}

      {/* CSS-grid table. Fixed-width gutter columns (ts/src/duration)
          keep numbers and the source tag aligned; name/parent share
          the remaining horizontal space with `minmax(0, 1fr)` so they
          can `truncate` instead of pushing the layout wide. The header
          row uses `sticky top-0` so it stays visible while the body
          scrolls — the outer container is the scroll context. */}
      <div className="border-border bg-muted/40 max-h-80 min-h-32 overflow-auto rounded-xs border font-mono text-[11px]">
        {filtered.length === 0 ? (
          <div className="text-muted-foreground p-2">
            {trace.events.length === 0 && !trace.completed
              ? t('traceWaiting')
              : t('traceEmpty')}
          </div>
        ) : (
          <div className="grid" style={{ gridTemplateColumns: TRACE_GRID_TEMPLATE }}>
            <div className="bg-muted text-muted-foreground border-border sticky top-0 z-10 border-b px-2 py-1 text-right text-[10px] font-medium tracking-wide uppercase">
              {t('traceColTs')}
            </div>
            <div className="bg-muted text-muted-foreground border-border sticky top-0 z-10 border-b px-2 py-1 text-[10px] font-medium tracking-wide uppercase">
              {t('traceColSource')}
            </div>
            <div className="bg-muted text-muted-foreground border-border sticky top-0 z-10 border-b px-2 py-1 text-[10px] font-medium tracking-wide uppercase">
              {t('traceColName')}
            </div>
            <div className="bg-muted text-muted-foreground border-border sticky top-0 z-10 border-b px-2 py-1 text-[10px] font-medium tracking-wide uppercase">
              {t('traceColParent')}
            </div>
            <div className="bg-muted text-muted-foreground border-border sticky top-0 z-10 border-b px-2 py-1 text-right text-[10px] font-medium tracking-wide uppercase">
              {t('traceColDuration')}
            </div>
            {filtered.map((e) => (
              <TraceRow key={`${e.cellId}:${e.sequence}`} entry={e} />
            ))}
          </div>
        )}
      </div>

      <div className="text-muted-foreground flex flex-row items-center gap-3 text-[11px] select-none">
        <span>{t('traceFooter', { count: trace.events.length })}</span>
        <button
          type="button"
          onClick={copyAsText}
          className="hover:text-foreground cursor-pointer"
          disabled={trace.events.length === 0}
        >
          {t('traceCopy')}
        </button>
        <button
          type="button"
          onClick={downloadAsNdjson}
          className="hover:text-foreground cursor-pointer"
          disabled={trace.events.length === 0}
        >
          {t('traceDownload')}
        </button>
        <button
          type="button"
          onClick={() => clearTrace(tabId)}
          className="hover:text-foreground ml-auto cursor-pointer"
          disabled={trace.events.length === 0}
        >
          {t('traceClear')}
        </button>
      </div>
    </div>
  );
}

// Compact inline events-per-N-ms sparkline. Keeps the chip narrow — we
// don't show absolute counts here, just relative rate so the user can
// spot bursts at a glance. Bucketing covers traces with a wide range of
// densities (a few events across seconds vs hundreds per second).
function bucketEventsPerInterval(events: readonly TraceEntry[], bucketMs: number): number[] {
  if (events.length === 0) return [];
  let maxTs = 0;
  for (const e of events) {
    if (e.tsMs > maxTs) maxTs = e.tsMs;
  }
  const bucketCount = Math.max(1, Math.ceil(maxTs / bucketMs) + 1);
  const buckets = new Array<number>(bucketCount).fill(0);
  for (const e of events) {
    const b = Math.min(bucketCount - 1, Math.floor(e.tsMs / bucketMs));
    buckets[b]++;
  }
  return buckets;
}

interface MiniSparklineProps {
  buckets: number[];
}

function MiniSparkline({ buckets }: MiniSparklineProps) {
  // Inline SVG mini-sparkline so the chip's content stays self-
  // contained. The existing <Sparkline> component used by MemoryChip
  // is sized + tuned for the larger popover view; here we want a tiny
  // 48px-wide strip that fits inside a 12px-tall status-bar row.
  if (buckets.length === 0) {
    return <span className="inline-block h-3 w-12" aria-hidden="true" />;
  }
  const max = Math.max(1, ...buckets);
  const width = 48;
  const height = 12;
  const barWidth = Math.max(1, width / buckets.length);
  return (
    <svg
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      className="inline-block shrink-0"
      aria-hidden="true"
    >
      {buckets.map((v, i) => {
        const h = (v / max) * height;
        return (
          <rect
            key={i}
            x={i * barWidth}
            y={height - h}
            width={Math.max(0.5, barWidth - 0.5)}
            height={h}
            fill="currentColor"
            opacity={0.7}
          />
        );
      })}
    </svg>
  );
}

function formatTraceLine(e: TraceEntry): string {
  const parent = e.parent ? ` ⇐ ${e.parent}` : '';
  return `${e.tsMs.toFixed(2)}ms [${e.source}] ${e.name}${parent}  ${e.durationMs.toFixed(2)}ms`;
}

// One row in the trace table. Renders five grid cells (matching
// TRACE_GRID_TEMPLATE) inside the parent grid via React fragments —
// the outer grid container owns the column layout, so each cell
// becomes a direct child cell rather than this component owning its
// own layout. `title` on each cell carries the full formatted line so
// truncated values stay inspectable on hover.
function TraceRow({ entry }: { entry: TraceEntry }) {
  const tooltip = formatTraceLine(entry);
  return (
    <>
      <div
        className="border-border/40 text-muted-foreground border-b px-2 py-0.5 text-right tabular-nums"
        title={tooltip}
      >
        {entry.tsMs.toFixed(1)}
      </div>
      <div
        className="border-border/40 text-muted-foreground border-b px-2 py-0.5"
        title={tooltip}
      >
        {entry.source}
      </div>
      <div
        className="border-border/40 truncate border-b px-2 py-0.5"
        title={tooltip}
      >
        {entry.name}
      </div>
      <div
        className="border-border/40 text-muted-foreground truncate border-b px-2 py-0.5"
        title={tooltip}
      >
        {entry.parent ?? ''}
      </div>
      <div
        className="border-border/40 text-muted-foreground border-b px-2 py-0.5 text-right tabular-nums"
        title={tooltip}
      >
        {entry.durationMs.toFixed(2)}ms
      </div>
    </>
  );
}
