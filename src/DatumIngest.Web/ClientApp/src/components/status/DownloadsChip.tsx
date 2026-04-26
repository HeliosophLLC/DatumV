import { useEffect, useState } from 'react';
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

export function DownloadsChip() {
  const { t } = useTranslation('status');
  const snap = useSnapshot(downloadsState);

  const activeIds = Object.keys(snap.active);
  const installingIds = Object.keys(snap.installing);
  const venvIds = Object.keys(snap.venvSteps);
  const errorIds = Object.keys(snap.errors);

  const hasAnything =
    activeIds.length > 0 ||
    installingIds.length > 0 ||
    venvIds.length > 0 ||
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

  if (!hasAnything) return null;

  const perfNow = performance.now();
  const wallNow = Date.now();
  const stalledIds = activeIds.filter((id) =>
    isStalled(snap.active[id]!, perfNow, wallNow),
  );
  const healthyActiveIds = activeIds.filter((id) => !stalledIds.includes(id));

  let bytesRead = 0;
  let bytesTotal = 0;
  for (const id of healthyActiveIds) {
    const a = snap.active[id]!;
    bytesRead += a.bytesReadTotal;
    bytesTotal += a.bytesTotalAcrossModel;
  }
  // Cap at 99% so the chip never reads "100%" while files are still
  // moving — the transition to "Installing" or chip-disappears is the
  // real completion signal.
  const percent =
    bytesTotal > 0 ? Math.min(99, Math.floor((bytesRead / bytesTotal) * 100)) : null;

  const installingCount =
    new Set([...installingIds, ...venvIds]).size + (snap.pythonHostStep ? 1 : 0);

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

  const stalledSet = new Set(stalledIds);
  const errorIds = Object.keys(snap.errors).sort();
  const activeIds = Object.keys(snap.active).sort();
  const installingIds = [
    ...Object.keys(snap.installing),
    ...Object.keys(snap.venvSteps),
  ].sort();

  const seen = new Set<string>();
  const rows: { id: string; kind: 'failed' | 'stalled' | 'active' | 'installing' }[] = [];
  for (const id of errorIds) {
    if (seen.has(id)) continue;
    seen.add(id);
    rows.push({ id, kind: 'failed' });
  }
  for (const id of activeIds) {
    if (seen.has(id)) continue;
    seen.add(id);
    rows.push({ id, kind: stalledSet.has(id) ? 'stalled' : 'active' });
  }
  for (const id of installingIds) {
    if (seen.has(id)) continue;
    seen.add(id);
    rows.push({ id, kind: 'installing' });
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="text-xs font-medium">{t('downloadsChip.popoverTitle')}</div>
      <ul className="flex flex-col gap-1">
        {rows.map((row) => (
          <li key={row.id} className="border-border/40 border-t pt-1.5">
            {row.kind === 'failed' && (
              <FailedRow modelId={row.id} error={snap.errors[row.id]!} />
            )}
            {row.kind === 'active' && <ActiveRow active={snap.active[row.id]!} />}
            {row.kind === 'stalled' && <StalledRow active={snap.active[row.id]!} />}
            {row.kind === 'installing' && <InstallingRow modelId={row.id} />}
          </li>
        ))}
      </ul>
    </div>
  );
}

function ActiveRow({ active }: { active: ActiveDownload }) {
  const { t } = useTranslation('status');
  const rate = computeRateBytesPerSec(active.samples);
  const etaSec = computeEtaSeconds(active, rate);
  const percent =
    active.bytesTotalAcrossModel > 0
      ? Math.min(99, Math.floor((active.bytesReadTotal / active.bytesTotalAcrossModel) * 100))
      : null;

  return (
    <div className="flex flex-col gap-0.5 text-xs">
      <div className="flex items-baseline justify-between font-mono">
        <span className="truncate">{active.modelId}</span>
        <span className="text-muted-foreground shrink-0 pl-2">
          {percent != null ? `${percent}%` : t('downloadsChip.rowQueued')}
        </span>
      </div>
      <ProgressBar percent={percent} tone="blue" />
      <div className="text-muted-foreground flex justify-between gap-2 font-mono text-[10px]">
        <span className="min-w-0 truncate">
          {active.fileCount > 0 &&
            t('downloadsChip.currentFileLabel', {
              index: active.fileIndex + 1,
              count: active.fileCount,
            })}
          {active.currentFile ? ` · ${active.currentFile}` : ''}
        </span>
        <span className="shrink-0">
          {formatBytes(active.bytesReadTotal)}
          {active.bytesTotalAcrossModel > 0 && ` / ${formatBytes(active.bytesTotalAcrossModel)}`}
          {rate != null && ` · ${formatBytes(rate)}/s`}
          {etaSec != null && ` · ${formatEta(etaSec)}`}
        </span>
      </div>
    </div>
  );
}

function StalledRow({ active }: { active: ActiveDownload }) {
  const { t } = useTranslation('status');
  const percent =
    active.bytesTotalAcrossModel > 0
      ? Math.min(99, Math.floor((active.bytesReadTotal / active.bytesTotalAcrossModel) * 100))
      : null;

  return (
    <div className="flex flex-col gap-1 text-xs">
      <div className="flex items-baseline justify-between font-mono">
        <span className="truncate">{active.modelId}</span>
        <span className="shrink-0 pl-2 text-amber-700 dark:text-amber-500">
          {t('downloadsChip.rowStalled')}
        </span>
      </div>
      <ProgressBar percent={percent} tone="amber" />
      <div className="text-muted-foreground flex justify-between gap-2 font-mono text-[10px]">
        <span className="min-w-0 truncate">{t('downloadsChip.rowStalledDetail')}</span>
        <span className="shrink-0">
          {formatBytes(active.bytesReadTotal)}
          {active.bytesTotalAcrossModel > 0 && ` / ${formatBytes(active.bytesTotalAcrossModel)}`}
        </span>
      </div>
      <div className="flex justify-end">
        <button
          type="button"
          onClick={() => dismissActiveDownload(active.modelId)}
          className="border-border hover:bg-muted/60 cursor-pointer rounded border px-2 py-0.5 text-[10px] uppercase tracking-wide"
        >
          {t('downloadsChip.dismissButton')}
        </button>
      </div>
    </div>
  );
}

function InstallingRow({ modelId }: { modelId: string }) {
  const { t } = useTranslation('status');
  const snap = useSnapshot(downloadsState);
  // Venv step wins when present; host step (uv / python) is shared across
  // whichever model triggered the install, so we surface it on every
  // installing row that doesn't have its own venv step.
  const step = snap.venvSteps[modelId] ?? snap.pythonHostStep;
  const detail = step ? [step.stage, step.detail].filter(Boolean).join(' · ') : null;

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

function FailedRow({ modelId, error }: { modelId: string; error: string }) {
  const { t } = useTranslation('status');
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
          onClick={() => dismissDownloadError(modelId)}
          className="border-border hover:bg-muted/60 cursor-pointer rounded border px-2 py-0.5 text-[10px] uppercase tracking-wide"
        >
          {t('downloadsChip.dismissButton')}
        </button>
        <button
          type="button"
          onClick={() => void installModel(modelId)}
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
