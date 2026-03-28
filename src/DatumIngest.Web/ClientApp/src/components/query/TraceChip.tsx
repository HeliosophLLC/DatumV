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

export interface TraceChipProps {
  tabId: string;
  trace: TraceState;
}

export function TraceChip({ tabId, trace }: TraceChipProps) {
  const { t } = useTranslation('query');

  const eventCount = trace.events.length;
  const hasEvents = eventCount > 0;
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

      {/* Chip half — only when events have arrived. */}
      {hasEvents && (
        <Popover.Root>
          <Popover.Trigger
            className="border-status-bar-foreground/25 text-status-bar-foreground hover:bg-status-bar-foreground/10 flex shrink-0 cursor-pointer items-center gap-1.5 border-l px-3 py-1"
            aria-label={t('traceChipTooltip')}
          >
            <span>{t('traceChipEvents', { count: eventCount })}</span>
            <MiniSparkline buckets={sparkBuckets} />
            {trace.dropped > 0 && (
              <span className="text-amber-700 dark:text-amber-500">
                {t('traceChipDropped', { count: trace.dropped })}
              </span>
            )}
          </Popover.Trigger>
          <Popover.Portal>
            <Popover.Positioner side="top" align="end" sideOffset={6}>
              <Popover.Popup className="bg-popover text-popover-foreground border-border z-50 w-[28rem] rounded-md border p-3 shadow-md">
                <TracePopoverBody tabId={tabId} trace={trace} />
              </Popover.Popup>
            </Popover.Positioner>
          </Popover.Portal>
        </Popover.Root>
      )}
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
      <div className="text-sm font-medium">{t('tracePopoverTitle')}</div>

      {/* Scope toggles. The Operators toggle here mirrors the status-
          bar checkbox; both write to the same state. Scalars-on is the
          much-larger trace so we keep it opt-in inside the popover. */}
      <div className="flex flex-row gap-3">
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

      <div className="border-border bg-muted/40 max-h-64 min-h-32 overflow-auto rounded-xs border font-mono text-[11px]">
        {filtered.length === 0 ? (
          <div className="text-muted-foreground p-2">
            {trace.events.length === 0 && !trace.completed
              ? t('traceWaiting')
              : t('traceEmpty')}
          </div>
        ) : (
          <ul>
            {filtered.map((e) => (
              <li
                key={`${e.cellId}:${e.sequence}`}
                className="border-border/40 flex items-baseline gap-2 border-b px-2 py-0.5 last:border-b-0"
                title={formatTraceLine(e)}
              >
                <span className="text-muted-foreground tabular-nums">
                  {e.tsMs.toFixed(1)}
                </span>
                <span className="text-muted-foreground">[{e.source}]</span>
                <span className="truncate">{e.name}</span>
                {e.parent && (
                  <span className="text-muted-foreground truncate">
                    ⇐ {e.parent}
                  </span>
                )}
                <span className="text-muted-foreground tabular-nums ml-auto">
                  {e.durationMs.toFixed(2)}ms
                </span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="text-muted-foreground flex flex-row items-center gap-3 text-[11px]">
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
