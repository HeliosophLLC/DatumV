import { useEffect, useState } from 'react';
import { Popover } from '@base-ui/react/popover';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';

import {
  calibrationState,
  refreshCalibration,
  type RecentCompletion,
} from '@/state/calibration';

// Status-bar chip that:
//  - flashes blue while a calibration ramp is in flight, displaying
//    the active model name;
//  - shows a brief afterglow when a recent ramp just completed, so
//    quick ramps don't snap back to neutral instantaneously;
//  - returns to neutral otherwise; popover still opens and shows
//    the full curves table seeded from /api/model-runtime/calibration.

// How long the "just completed" afterglow stays on the chip after a
// ramp finishes. Matches RECENT_RAMP_WINDOW_MS in calibration.ts so
// the chip's visual state and the popover's "Recent" section stay
// consistent.
const AFTERGLOW_MS = 4_000;

export function CalibrationChip() {
  const { t } = useTranslation('status');
  const snap = useSnapshot(calibrationState);

  // Tick once per second while we're in an active/afterglow state so
  // the afterglow elapses without a hub event to trigger a re-render.
  // Quiet idle state has no tick, so React doesn't render needlessly.
  const [, forceRender] = useState(0);
  const hasRecent = snap.recentCompletions.length > 0;
  const active = snap.activeRamp;
  useEffect(() => {
    if (!active && !hasRecent) return;
    const id = window.setInterval(() => forceRender((x) => x + 1), 1000);
    return () => window.clearInterval(id);
  }, [active, hasRecent]);

  // Determine the chip's visual state:
  //   - active: a ramp is running NOW. Blue, pulsing.
  //   - afterglow: most-recent completion within window. Blue, steady.
  //   - idle: muted text, no animation.
  const now = Date.now();
  const recentInWindow = snap.recentCompletions.find(
    (r) => now - r.finishedAt < AFTERGLOW_MS,
  );

  // Background-based state (instead of text glow):
  //   - active: solid blue background + white text — unmistakable
  //     while a ramp is in flight. Label is just "Calibrating";
  //     model name + step count live in the popover so the chip's
  //     width doesn't bounce as ramps progress (long model names
  //     would otherwise resize the chip mid-update).
  //   - afterglow: tinted blue background that fades out via the
  //     elapsing AFTERGLOW_MS window. Same "no model name in the
  //     label" rule so the chip width stays stable into the
  //     post-completion state.
  //   - idle: no background, muted text — matches sibling chips.
  //
  // Chip itself is fixed-width (`w-32`) and content-centered so the
  // text doesn't visibly shift when the label cycles through these
  // three states.
  let chipLabel: string;
  let chipClasses: string;
  if (active) {
    chipLabel = t('calibrationChip.rampingShort');
    chipClasses =
      'border-blue-700 bg-blue-600 text-white hover:bg-blue-700 dark:bg-blue-500 dark:hover:bg-blue-600';
  } else if (recentInWindow) {
    chipLabel = t('calibrationChip.label');
    chipClasses =
      'border-border bg-blue-500/15 text-blue-700 hover:bg-blue-500/25 dark:text-blue-400';
  } else {
    chipLabel = t('calibrationChip.label');
    chipClasses = 'border-border hover:bg-muted/40 text-muted-foreground';
  }

  return (
    <Popover.Root>
      <Popover.Trigger
        className={`flex w-32 shrink-0 cursor-pointer items-center justify-center overflow-hidden whitespace-nowrap border-l px-3 font-mono text-xs ${chipClasses}`}
        aria-label={t('calibrationChip.label')}
      >
        <span className="truncate">{chipLabel}</span>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Positioner side="top" align="end" sideOffset={6} className="z-[100]">
          <Popover.Popup className="bg-popover text-popover-foreground border-border w-[28rem] rounded-md border p-3 shadow-md">
            <CalibrationPopoverBody />
          </Popover.Popup>
        </Popover.Positioner>
      </Popover.Portal>
    </Popover.Root>
  );
}

function CalibrationPopoverBody() {
  const { t } = useTranslation('status');
  const snap = useSnapshot(calibrationState);

  // Refresh curves on open. The state already mutates from hub
  // events; this fills weight_cost for entries the hub events
  // synthesised without it and resyncs the status string. Cheap;
  // single REST round-trip.
  useEffect(() => {
    void refreshCalibration();
  }, []);

  const curves = Object.values(snap.curves).sort((a, b) =>
    a.modelName.localeCompare(b.modelName),
  );

  return (
    <div className="flex flex-col gap-3">
      <div className="text-xs font-medium">
        {t('calibrationChip.popoverTitle')}
      </div>

      {snap.activeRamp && (
        <div className="border-border bg-blue-500/5 rounded border p-2">
          <div className="text-xs">
            {t('calibrationChip.popoverActiveRamp', {
              model: snap.activeRamp.modelName,
            })}
          </div>
          <div className="text-muted-foreground text-[10px]">
            {t('calibrationChip.popoverActiveSteps', {
              count: snap.activeRamp.stepsSoFar.length,
            })}
          </div>
          {snap.activeRamp.stepsSoFar.length > 0 && (
            <table className="mt-1 w-full font-mono text-[10px]">
              <tbody>
                {snap.activeRamp.stepsSoFar.map((s) => (
                  <tr key={s.batchSize}>
                    <td className="text-muted-foreground">batch={s.batchSize}</td>
                    <td className="text-right">{formatBytes(s.totalVramBytes)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      <RecentCompletionsList recent={snap.recentCompletions} />
      <CurveTable curves={curves} />
    </div>
  );
}

function RecentCompletionsList({ recent }: { recent: readonly RecentCompletion[] }) {
  const { t } = useTranslation('status');
  if (recent.length === 0) return null;

  return (
    <div className="flex flex-col gap-1">
      <div className="text-muted-foreground text-[10px] uppercase tracking-wide">
        {t('calibrationChip.popoverRecent')}
      </div>
      <ul className="text-xs">
        {recent.map((r) => (
          <li
            key={`${r.modelName}-${r.finishedAt}`}
            className="border-border/40 flex justify-between border-t py-0.5 font-mono"
          >
            <span>{r.modelName}</span>
            <span
              className={
                r.outcome === 'halted-spill'
                  ? 'text-amber-700 dark:text-amber-500'
                  : r.outcome === 'halted-projection'
                    ? 'text-muted-foreground'
                    : 'text-blue-700 dark:text-blue-400'
              }
            >
              {r.outcome === 'completed'
                ? t('calibrationChip.outcomeCompleted')
                : r.outcome === 'halted-spill'
                  ? t('calibrationChip.outcomeHaltedSpill')
                  : t('calibrationChip.outcomeHaltedProjection')}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

interface CurveTableProps {
  curves: readonly {
    modelName: string;
    weightCostBytes: number;
    status: string;
    entries: readonly { batchSize: number; totalVramBytes: number }[];
  }[];
}

function CurveTable({ curves }: CurveTableProps) {
  const { t } = useTranslation('status');

  function statusLabel(status: string): string {
    switch (status) {
      case 'calibrated':
        return t('calibrationChip.curveStatusCalibrated');
      case 'stale':
        return t('calibrationChip.curveStatusStale');
      default:
        return t('calibrationChip.curveStatusUncalibrated');
    }
  }

  if (curves.length === 0) {
    return (
      <div className="flex flex-col gap-1">
        <div className="text-muted-foreground text-[10px] uppercase tracking-wide">
          {t('calibrationChip.curveTableTitle')}
        </div>
        <div className="text-muted-foreground text-xs">
          {t('calibrationChip.curveTableEmpty')}
        </div>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-1">
      <div className="text-muted-foreground text-[10px] uppercase tracking-wide">
        {t('calibrationChip.curveTableTitle')}
      </div>
      <div className="max-h-72 overflow-y-auto pr-1">
        {curves.map((curve) => (
          <div key={curve.modelName} className="border-border/40 mb-2 border-t pt-1">
            <div className="flex items-baseline justify-between text-xs">
              <span className="font-mono">{curve.modelName}</span>
              <span className="text-muted-foreground text-[10px]">
                {statusLabel(curve.status)}
              </span>
            </div>
            <table className="w-full font-mono text-[10px]">
              <thead className="text-muted-foreground">
                <tr>
                  <th className="text-left font-normal">
                    {t('calibrationChip.curveColumnBatch')}
                  </th>
                  <th className="text-right font-normal">
                    {t('calibrationChip.curveColumnTotal')}
                  </th>
                  <th className="text-right font-normal">
                    {t('calibrationChip.curveColumnActivation')}
                  </th>
                </tr>
              </thead>
              <tbody>
                {curve.entries.map((e) => {
                  const activation = Math.max(
                    0,
                    e.totalVramBytes - curve.weightCostBytes,
                  );
                  return (
                    <tr key={e.batchSize}>
                      <td>{e.batchSize}</td>
                      <td className="text-right">{formatBytes(e.totalVramBytes)}</td>
                      <td className="text-right text-muted-foreground">
                        {formatBytes(activation)}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        ))}
      </div>
    </div>
  );
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
