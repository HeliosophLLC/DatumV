import { useEffect, useRef, useState } from 'react';
import { Popover } from '@base-ui/react/popover';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';

import {
  computeEtaSeconds,
  computeRateBytesPerSec,
  dismissActiveDownload,
  dismissDownloadError,
  downloadsState,
  installModel,
  type ActiveDownload,
} from '@/state/downloads';
import {
  datasetsState,
  dismissActiveInstall as dismissActiveDatasetInstall,
  dismissError as dismissDatasetError,
  installVariant,
  type ActiveDatasetInstall,
} from '@/state/datasets';

// Status-bar chip showing aggregate model-download / install activity.
// Hidden when nothing is in flight — left-section placement keeps the
// right-anchored chips from shifting as it appears and disappears.
//
// Tone precedence: errors (red, demands attention) > stalled (amber,
// no fresh progress / lost connection) > active progress (solid blue) >
// install-only (blue tint). Width is fixed so the label can cycle
// through these states without the chip resizing.

// A download counts as stalled when no fresh progress sample (or, before
// the first sample arrives, no time since the optimistic click) has
// landed in this long. Server-side HttpClient timeout is ~100s, plus
// SignalR can drop events during a wifi blip — so the chip falls back to
// a client-side timer to keep the user informed.
const STALL_AFTER_MS = 15_000;

// EMA smoothing factor for the rate display. Low alpha = heavy smoothing
// → the rate readout takes ~10 samples (~10s at 1Hz) to converge but
// stops jittering by ±20% each tick. ETA derives from the smoothed
// rate so it stops bouncing for free.
const RATE_SMOOTHING_ALPHA = 0.25;

function applyEma(prev: number | null, current: number | null): number | null {
  if (current == null) return prev;
  if (prev == null) return current;
  return RATE_SMOOTHING_ALPHA * current + (1 - RATE_SMOOTHING_ALPHA) * prev;
}

// Project a dataset variant install onto the ActiveDownload shape the
// chip's rendering helpers consume. The dataset record carries a few
// extra fields (phase, currentTable, jobIndex/Count) which the chip
// doesn't surface — they only matter inside the dataset detail card.
// `bytesTotalAcrossDataset` is renamed to `bytesTotalAcrossModel` so the
// existing aggregation + ActiveRow / StalledRow don't have to branch on
// the source.
function adaptDatasetActive(d: ActiveDatasetInstall): ActiveDownload {
  return {
    modelId: d.datasetId,
    bytesReadTotal: d.bytesReadTotal,
    bytesTotalAcrossModel: d.bytesTotalAcrossDataset,
    fileIndex: d.fileIndex,
    fileCount: d.fileCount,
    currentFile: d.currentFile,
    startedAt: d.startedAt,
    samples: d.samples,
  };
}

function rawPercentOf(bytesRead: number, bytesTotal: number): number | null {
  if (bytesTotal <= 0) return null;
  return Math.min(99, Math.floor((bytesRead / bytesTotal) * 100));
}

export function DownloadsChip() {
  const { t } = useTranslation('status');
  const snap = useSnapshot(downloadsState);
  const datasetSnap = useSnapshot(datasetsState);

  // Project the dataset active map into the same shape the chip already
  // aggregates over. Dataset entries split by phase: starting /
  // downloading rendered as active downloads, ingesting rendered as
  // post-download installs (parallel to the model side's `installing`
  // map). The same map drives popover row rendering further down.
  const datasetDownloading: Record<string, ActiveDownload> = {};
  const datasetIngestingIds: string[] = [];
  for (const [id, d] of Object.entries(datasetSnap.active)) {
    if (d.phase === 'ingesting') datasetIngestingIds.push(id);
    else datasetDownloading[id] = adaptDatasetActive(d);
  }
  const datasetErrorIds = Object.keys(datasetSnap.errors);

  const modelActiveIds = Object.keys(snap.active);
  const activeIds = [...modelActiveIds, ...Object.keys(datasetDownloading)];
  const installingIds = Object.keys(snap.installing);
  const venvIds = Object.keys(snap.venvSteps);
  const errorIds = [...Object.keys(snap.errors), ...datasetErrorIds];

  const hasAnything =
    activeIds.length > 0 ||
    installingIds.length > 0 ||
    venvIds.length > 0 ||
    datasetIngestingIds.length > 0 ||
    errorIds.length > 0 ||
    snap.pythonHostStep != null;

  // Tick once per second while anything is active so the stall threshold
  // elapses without a hub event to trigger a re-render. Quiet otherwise.
  const [, forceRender] = useState(0);
  const hasActive = activeIds.length > 0;
  useEffect(() => {
    if (!hasActive) return;
    const id = window.setInterval(() => forceRender((x) => x + 1), 1000);
    return () => window.clearInterval(id);
  }, [hasActive]);

  // Monotonic clamp for the aggregate percent (kept above the early
  // return so the hook order stays stable across renders where the chip
  // goes from visible to hidden). Reset to 0 whenever there are no
  // healthy active downloads so the next batch starts fresh.
  const monotonicAggPercentRef = useRef<number>(0);

  if (!hasAnything) return null;

  const perfNow = performance.now();
  const wallNow = Date.now();
  // Lookup that resolves an active id from either the model or dataset
  // map. Both contribute to aggregate progress + stall detection.
  const lookupActive = (id: string): ActiveDownload =>
    (snap.active[id] as ActiveDownload | undefined) ?? datasetDownloading[id]!;
  const stalledIds = activeIds.filter((id) =>
    isStalled(lookupActive(id), perfNow, wallNow),
  );
  const healthyActiveIds = activeIds.filter((id) => !stalledIds.includes(id));

  let bytesRead = 0;
  let bytesTotal = 0;
  for (const id of healthyActiveIds) {
    const a = lookupActive(id);
    bytesRead += a.bytesReadTotal;
    bytesTotal += a.bytesTotalAcrossModel;
  }
  // Cap at 99% so the chip never reads "100%" while files are still
  // moving — the transition to "Installing" or chip-disappears is the
  // real completion signal.
  const rawAggPercent = rawPercentOf(bytesRead, bytesTotal);
  // Apply the monotonic clamp. Percent can otherwise read backwards
  // when bytesTotalAcrossModel revises upward mid-stream (the server
  // learns of additional files) or when a second install joins the
  // in-flight set and dilutes the aggregate before its bytes-read
  // catches up. The ref itself is declared above the early return so
  // the hook order stays stable.
  if (healthyActiveIds.length === 0) {
    monotonicAggPercentRef.current = 0;
  } else if (
    rawAggPercent != null &&
    rawAggPercent > monotonicAggPercentRef.current
  ) {
    monotonicAggPercentRef.current = rawAggPercent;
  }
  const percent =
    rawAggPercent != null ? monotonicAggPercentRef.current : null;

  const installingCount =
    new Set([...installingIds, ...venvIds, ...datasetIngestingIds]).size
    + (snap.pythonHostStep ? 1 : 0);

  let chipClasses: string;
  let chipText: string;
  if (errorIds.length > 0) {
    chipClasses =
      'border-red-700 bg-red-600 text-white hover:bg-red-700 dark:bg-red-500 dark:hover:bg-red-600';
    chipText = t('downloadsChip.failed', { count: errorIds.length });
  } else if (stalledIds.length > 0) {
    chipClasses =
      'border-amber-700 bg-amber-500 text-white hover:bg-amber-600 dark:bg-amber-600 dark:hover:bg-amber-700';
    chipText = t('downloadsChip.stalled', { count: stalledIds.length });
  } else if (healthyActiveIds.length > 0) {
    chipClasses =
      'border-blue-700 bg-blue-600 text-white hover:bg-blue-700 dark:bg-blue-500 dark:hover:bg-blue-600';
    chipText =
      percent != null
        ? t('downloadsChip.downloadingWithPercent', { percent })
        : t('downloadsChip.downloadingNoTotal', { count: healthyActiveIds.length });
  } else {
    chipClasses =
      'border-border bg-blue-500/15 text-blue-700 hover:bg-blue-500/25 dark:text-blue-400';
    chipText = t('downloadsChip.installing', { count: installingCount });
  }

  return (
    <Popover.Root>
      <Popover.Trigger
        className={`flex w-48 shrink-0 cursor-pointer items-center justify-center overflow-hidden whitespace-nowrap border-r px-3 font-mono text-xs ${chipClasses}`}
        aria-label={t('downloadsChip.label')}
      >
        <span className="truncate">{chipText}</span>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Positioner side="top" align="start" sideOffset={6} className="z-[100]">
          <Popover.Popup className="bg-popover text-popover-foreground border-border w-[28rem] rounded-md border p-3 shadow-md">
            <DownloadsPopoverBody stalledIds={stalledIds} />
          </Popover.Popup>
        </Popover.Positioner>
      </Popover.Portal>
    </Popover.Root>
  );
}

function DownloadsPopoverBody({ stalledIds }: { stalledIds: readonly string[] }) {
  const { t } = useTranslation('status');
  const snap = useSnapshot(downloadsState);
  const datasetSnap = useSnapshot(datasetsState);

  // Mirror of the parent's per-phase dataset projection so the popover
  // can render dataset rows alongside model rows. Source tag drives
  // dismiss / retry routing into the right state module.
  const datasetDownloading: Record<string, ActiveDownload> = {};
  const datasetIngestingIds: string[] = [];
  for (const [id, d] of Object.entries(datasetSnap.active)) {
    if (d.phase === 'ingesting') datasetIngestingIds.push(id);
    else datasetDownloading[id] = adaptDatasetActive(d);
  }

  const stalledSet = new Set(stalledIds);
  type RowSource = 'model' | 'dataset';
  type Row = {
    id: string;
    source: RowSource;
    kind: 'failed' | 'stalled' | 'active' | 'installing';
  };
  const seen = new Set<string>();
  const rows: Row[] = [];

  const errorEntries: { id: string; source: RowSource }[] = [
    ...Object.keys(snap.errors).map((id) => ({ id, source: 'model' as RowSource })),
    ...Object.keys(datasetSnap.errors).map((id) => ({ id, source: 'dataset' as RowSource })),
  ].sort((a, b) => a.id.localeCompare(b.id));
  for (const { id, source } of errorEntries) {
    const key = `${source}:${id}`;
    if (seen.has(key)) continue;
    seen.add(key);
    rows.push({ id, source, kind: 'failed' });
  }

  const activeEntries: { id: string; source: RowSource }[] = [
    ...Object.keys(snap.active).map((id) => ({ id, source: 'model' as RowSource })),
    ...Object.keys(datasetDownloading).map((id) => ({ id, source: 'dataset' as RowSource })),
  ].sort((a, b) => a.id.localeCompare(b.id));
  for (const { id, source } of activeEntries) {
    const key = `${source}:${id}`;
    if (seen.has(key)) continue;
    seen.add(key);
    rows.push({
      id,
      source,
      kind: stalledSet.has(id) ? 'stalled' : 'active',
    });
  }

  const installingEntries: { id: string; source: RowSource }[] = [
    ...Object.keys(snap.installing).map((id) => ({ id, source: 'model' as RowSource })),
    ...Object.keys(snap.venvSteps).map((id) => ({ id, source: 'model' as RowSource })),
    ...datasetIngestingIds.map((id) => ({ id, source: 'dataset' as RowSource })),
  ].sort((a, b) => a.id.localeCompare(b.id));
  for (const { id, source } of installingEntries) {
    const key = `${source}:${id}`;
    if (seen.has(key)) continue;
    seen.add(key);
    rows.push({ id, source, kind: 'installing' });
  }

  return (
    <div className="flex flex-col gap-2 select-none">
      <div className="text-xs font-medium">{t('downloadsChip.popoverTitle')}</div>
      <ul className="flex flex-col gap-1">
        {rows.map((row) => (
          <li key={`${row.source}:${row.id}`} className="border-border/40 border-t pt-1.5">
            {row.kind === 'failed' && (
              <FailedRow
                modelId={row.id}
                source={row.source}
                error={
                  row.source === 'dataset'
                    ? datasetSnap.errors[row.id]!
                    : snap.errors[row.id]!
                }
              />
            )}
            {row.kind === 'active' && (
              <ActiveRow
                active={
                  row.source === 'dataset'
                    ? datasetDownloading[row.id]!
                    : snap.active[row.id]!
                }
              />
            )}
            {row.kind === 'stalled' && (
              <StalledRow
                source={row.source}
                active={
                  row.source === 'dataset'
                    ? datasetDownloading[row.id]!
                    : snap.active[row.id]!
                }
              />
            )}
            {row.kind === 'installing' && (
              <InstallingRow modelId={row.id} source={row.source} />
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

function ActiveRow({ active }: { active: ActiveDownload }) {
  const { t } = useTranslation('status');
  const rawRate = computeRateBytesPerSec(active.samples);
  const rawPercent = rawPercentOf(
    active.bytesReadTotal,
    active.bytesTotalAcrossModel,
  );

  // Per-row stability state. Refs survive across the 1Hz forceRender
  // ticks and survive useSnapshot mutations, but reset when the row
  // unmounts (download complete or cancelled). Mutating during render is
  // intentional — these are pure functions of the latest props, not
  // independent state we want React to track.
  const smoothedRateRef = useRef<number | null>(null);
  smoothedRateRef.current = applyEma(smoothedRateRef.current, rawRate);
  const monotonicPercentRef = useRef<number>(0);
  if (rawPercent != null && rawPercent > monotonicPercentRef.current) {
    monotonicPercentRef.current = rawPercent;
  }
  const smoothedRate = smoothedRateRef.current;
  const percent = rawPercent != null ? monotonicPercentRef.current : null;
  const etaSec = computeEtaSeconds(active, smoothedRate);

  return (
    <div className="flex flex-col gap-0.5 text-xs">
      <div className="flex items-baseline justify-between font-mono tabular-nums">
        <span className="truncate">{active.modelId}</span>
        <span className="text-muted-foreground inline-block w-10 shrink-0 pl-2 text-right">
          {percent != null ? `${percent}%` : t('downloadsChip.rowQueued')}
        </span>
      </div>
      <ProgressBar percent={percent} tone="blue" />
      <div className="text-muted-foreground flex justify-between gap-2 font-mono text-[10px] tabular-nums">
        <span className="min-w-0 truncate">
          {active.fileCount > 0 &&
            t('downloadsChip.currentFileLabel', {
              index: active.fileIndex + 1,
              count: active.fileCount,
            })}
          {active.currentFile ? ` · ${active.currentFile}` : ''}
        </span>
        {/* Each metric occupies a fixed-width right-aligned slot so the
            digit-count changing ("1.50 MB" → "12.50 MB") doesn't shove
            the next column sideways. Widths sized for worst-case
            "999.9 MB / 999.9 GB", "999.9 MB/s", "99h 59m" at the row's
            10px mono font. */}
        <span className="flex shrink-0 items-baseline gap-1">
          <span className="inline-block w-28 text-right">
            {formatBytes(active.bytesReadTotal)}
            {active.bytesTotalAcrossModel > 0 &&
              ` / ${formatBytes(active.bytesTotalAcrossModel)}`}
          </span>
          <span className="inline-block w-20 text-right">
            {smoothedRate != null ? `· ${formatBytes(smoothedRate)}/s` : ''}
          </span>
          <span className="inline-block w-14 text-right">
            {etaSec != null ? `· ${formatEta(etaSec)}` : ''}
          </span>
        </span>
      </div>
    </div>
  );
}

function StalledRow({
  active,
  source,
}: {
  active: ActiveDownload;
  source: 'model' | 'dataset';
}) {
  const { t } = useTranslation('status');
  const rawPercent = rawPercentOf(
    active.bytesReadTotal,
    active.bytesTotalAcrossModel,
  );
  const monotonicPercentRef = useRef<number>(0);
  if (rawPercent != null && rawPercent > monotonicPercentRef.current) {
    monotonicPercentRef.current = rawPercent;
  }
  const percent = rawPercent != null ? monotonicPercentRef.current : null;

  return (
    <div className="flex flex-col gap-1 text-xs">
      <div className="flex items-baseline justify-between font-mono tabular-nums">
        <span className="truncate">{active.modelId}</span>
        <span className="shrink-0 pl-2 text-amber-700 dark:text-amber-500">
          {t('downloadsChip.rowStalled')}
        </span>
      </div>
      <ProgressBar percent={percent} tone="amber" />
      <div className="text-muted-foreground flex justify-between gap-2 font-mono text-[10px] tabular-nums">
        <span className="min-w-0 truncate">{t('downloadsChip.rowStalledDetail')}</span>
        <span className="inline-block w-28 shrink-0 text-right">
          {formatBytes(active.bytesReadTotal)}
          {active.bytesTotalAcrossModel > 0 && ` / ${formatBytes(active.bytesTotalAcrossModel)}`}
        </span>
      </div>
      <div className="flex justify-end">
        <button
          type="button"
          onClick={() =>
            source === 'dataset'
              ? dismissActiveDatasetInstall(active.modelId)
              : dismissActiveDownload(active.modelId)
          }
          className="border-border hover:bg-muted/60 cursor-pointer rounded border px-2 py-0.5 text-[10px] uppercase tracking-wide"
        >
          {t('downloadsChip.dismissButton')}
        </button>
      </div>
    </div>
  );
}

function InstallingRow({
  modelId,
  source,
}: {
  modelId: string;
  source: 'model' | 'dataset';
}) {
  const { t } = useTranslation('status');
  const snap = useSnapshot(downloadsState);
  const datasetSnap = useSnapshot(datasetsState);
  // Dataset ingesting → derive detail from the active map's currentTable
  // + jobIndex/jobCount; the dataset side has no separate venvSteps.
  // Model installing → venv step wins when present; host step (uv /
  // python) is shared across whichever model triggered the install, so
  // we surface it on every installing row that doesn't have its own
  // venv step.
  let detail: string | null = null;
  if (source === 'dataset') {
    const d = datasetSnap.active[modelId];
    if (d && d.jobCount > 0) {
      const job = `${d.jobIndex + 1}/${d.jobCount}`;
      const base = d.currentTable ? `ingesting ${d.currentTable} · ${job}` : `ingesting · ${job}`;
      // Append the live row counter when we've seen at least one progress
      // tick from the SQL ingest path. Hidden until then so direct-shape
      // ingests (no SqlIngestExecutor) don't show a stuck "0 rows".
      const rows = d.rowsWrittenSoFar ?? 0;
      detail = rows > 0 ? `${base} · ${rows.toLocaleString()} rows` : base;
    }
  } else {
    const step = snap.venvSteps[modelId] ?? snap.pythonHostStep;
    detail = step ? [step.stage, step.detail].filter(Boolean).join(' · ') : null;
  }

  return (
    <div className="flex flex-col gap-0.5 text-xs">
      <div className="flex items-baseline justify-between font-mono">
        <span className="truncate">{modelId}</span>
        <span className="shrink-0 pl-2 text-blue-700 dark:text-blue-400">
          {t('downloadsChip.rowInstalling')}
        </span>
      </div>
      {detail && (
        <div className="text-muted-foreground truncate font-mono text-[10px]">{detail}</div>
      )}
    </div>
  );
}

function FailedRow({
  modelId,
  source,
  error,
}: {
  modelId: string;
  source: 'model' | 'dataset';
  error: string;
}) {
  const { t } = useTranslation('status');
  const onDismiss =
    source === 'dataset'
      ? () => dismissDatasetError(modelId)
      : () => dismissDownloadError(modelId);
  const onRetry =
    source === 'dataset'
      ? () => void installVariant(modelId)
      : () => void installModel(modelId);
  return (
    <div className="flex flex-col gap-1 text-xs">
      <div className="flex items-baseline justify-between font-mono">
        <span className="truncate">{modelId}</span>
        <span className="shrink-0 pl-2 text-red-700 dark:text-red-500">
          {t('downloadsChip.rowFailed')}
        </span>
      </div>
      <div className="text-muted-foreground text-[10px]">{error}</div>
      <div className="flex justify-end gap-1.5">
        <button
          type="button"
          onClick={onDismiss}
          className="border-border hover:bg-muted/60 cursor-pointer rounded border px-2 py-0.5 text-[10px] uppercase tracking-wide"
        >
          {t('downloadsChip.dismissButton')}
        </button>
        <button
          type="button"
          onClick={onRetry}
          className="cursor-pointer rounded border border-blue-700 bg-blue-600 px-2 py-0.5 text-[10px] uppercase tracking-wide text-white hover:bg-blue-700 dark:bg-blue-500 dark:hover:bg-blue-600"
        >
          {t('downloadsChip.retryButton')}
        </button>
      </div>
    </div>
  );
}

function ProgressBar({
  percent,
  tone,
}: {
  percent: number | null;
  tone: 'blue' | 'amber';
}) {
  if (percent == null) {
    const cls =
      tone === 'amber'
        ? 'h-full w-1/4 bg-amber-500/40'
        : 'h-full w-1/4 animate-pulse bg-blue-500/40';
    return (
      <div className="bg-muted/40 h-1 w-full overflow-hidden rounded">
        <div className={cls} />
      </div>
    );
  }
  const fillCls =
    tone === 'amber'
      ? 'h-full bg-amber-500 dark:bg-amber-600'
      : 'h-full bg-blue-600 dark:bg-blue-500';
  return (
    <div className="bg-muted/40 h-1 w-full overflow-hidden rounded">
      <div className={fillCls} style={{ width: `${percent}%` }} />
    </div>
  );
}

// True when no fresh progress sample (or, before the first sample, no
// real-time progress since the optimistic click) has arrived within
// STALL_AFTER_MS. samples[].t is performance.now(); startedAt is Date.now()
// — kept separate so we don't mix monotonic and wall clocks.
function isStalled(a: ActiveDownload, perfNow: number, wallNow: number): boolean {
  const last = a.samples[a.samples.length - 1];
  if (last != null) return perfNow - last.t > STALL_AFTER_MS;
  return wallNow - a.startedAt > STALL_AFTER_MS;
}

function formatBytes(bytes: number): string {
  const KB = 1024;
  const MB = KB * 1024;
  const GB = MB * 1024;
  if (bytes >= GB) return `${(bytes / GB).toFixed(2)} GB`;
  if (bytes >= MB) return `${(bytes / MB).toFixed(1)} MB`;
  if (bytes >= KB) return `${(bytes / KB).toFixed(0)} KB`;
  return `${Math.round(bytes)} B`;
}

function formatEta(seconds: number): string {
  if (seconds < 60) return `${Math.ceil(seconds)}s`;
  const minutes = Math.floor(seconds / 60);
  const secs = Math.ceil(seconds - minutes * 60);
  if (minutes < 60) return `${minutes}m ${secs}s`;
  const hours = Math.floor(minutes / 60);
  const mins = minutes - hours * 60;
  return `${hours}h ${mins}m`;
}
