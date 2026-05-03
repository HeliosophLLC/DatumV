import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { localeState } from '@/state/locale';
import {
  computeEtaSeconds,
  computeRateBytesPerSec,
  type ProgressSample,
} from '@/state/progressSamples';
import { formatBytesPerSec, formatDuration } from '@/lib/formatDownload';
import { Progress } from '@/components/ui/progress';

// Shared download-progress chrome used by both the Models and Datasets
// install surfaces. Renders:
//   1. Determinate progress bar (when totals are known).
//   2. First line: caller-controlled `statusText` + percent (right-
//      aligned, only when totals are known).
//   3. Second line: rate, elapsed, ETA — derived from the sliding
//      `samples` window so the displayed speed tracks the current
//      throughput instead of the lifetime average.
//
// The caller owns the status text so each surface keeps its own i18n
// namespace (models uses `card.downloadingFile`, datasets uses
// `progress.downloading`). Elapsed / ETA labels live in `common.download`
// since they read identically across both surfaces.

// 3-second moving-average window. Long enough to absorb single-event
// jitter (file-boundary stalls, TCP buffer flushes) without making the
// display feel laggy when the actual speed shifts.
const RATE_SMOOTHING_MS = 3_000;

export interface DownloadProgressBarProps {
  // Cumulative bytes read across the whole entry (across all files).
  bytesRead: number;
  // Cumulative bytes expected across the whole entry. Pass 0 when the
  // total is unknown; the bar then renders indeterminate (no percent
  // label, no ETA).
  bytesTotal: number;
  // ms-since-epoch timestamp captured when the install was queued.
  // Drives the elapsed-time display.
  startedAt: number;
  // Sliding window of recent progress observations. The bar averages
  // their derived rates over `RATE_SMOOTHING_MS` so the displayed
  // bandwidth doesn't jitter on every event.
  samples: readonly ProgressSample[];
  // First-line label, localized by the caller (e.g. "Downloading
  // val2017.zip (1 of 1)" or "Starting download…"). Truncates inside
  // the bar.
  statusText: string;
}

export function DownloadProgressBar(props: DownloadProgressBarProps) {
  const { bytesRead, bytesTotal, startedAt, samples, statusText } = props;
  const { t } = useTranslation('common');
  const { resolved: locale } = useSnapshot(localeState);

  const percent = bytesTotal > 0 ? (bytesRead / bytesTotal) * 100 : 0;

  // Tick every second so elapsed / ETA refresh even when progress events
  // pause (between files, during hash verify, etc.). The ticker drives
  // only this component's re-render; the underlying samples + startedAt
  // come from the caller's valtio state.
  const [, setTick] = useState(0);
  useEffect(() => {
    const id = window.setInterval(() => setTick((n) => n + 1), 1000);
    return () => window.clearInterval(id);
  }, []);

  // Smooth the rate over the last RATE_SMOOTHING_MS so the displayed
  // speed and ETA don't snap with every progress event. Raw rate (from
  // the caller's sliding sample window) is fed in once per render; the
  // buffer holds recent readings and we display their mean.
  const rateBuffer = useRef<{ t: number; rate: number }[]>([]);
  const rawRate = computeRateBytesPerSec(samples);
  const now = performance.now();
  if (rawRate != null) {
    rateBuffer.current.push({ t: now, rate: rawRate });
  }
  rateBuffer.current = rateBuffer.current.filter((s) => now - s.t < RATE_SMOOTHING_MS);
  const rate = rateBuffer.current.length > 0
    ? rateBuffer.current.reduce((sum, s) => sum + s.rate, 0) / rateBuffer.current.length
    : null;

  const etaSec = computeEtaSeconds(bytesRead, bytesTotal, rate);
  const elapsedSec = Math.max(0, Math.round((Date.now() - startedAt) / 1000));

  const rateText = rate != null ? formatBytesPerSec(rate) : '';
  const elapsedText = t('download.elapsed', { duration: formatDuration(elapsedSec, locale) });
  const remainingText =
    etaSec != null
      ? t('download.remaining', { duration: formatDuration(Math.round(etaSec), locale) })
      : '';

  return (
    <div className="flex flex-col gap-1">
      <Progress value={percent} />
      <div className="text-muted-foreground flex justify-between gap-2 text-xs">
        <span className="truncate">{statusText}</span>
        <span className="shrink-0">{bytesTotal > 0 ? `${Math.round(percent)}%` : ''}</span>
      </div>
      <div className="text-muted-foreground flex gap-3 text-xs tabular-nums">
        <span className="inline-block min-w-[5.5rem]">{rateText || ' '}</span>
        <span className="inline-block min-w-[8rem]">{elapsedText}</span>
        <span className="inline-block min-w-[9rem]">{remainingText || ' '}</span>
      </div>
    </div>
  );
}
