import { useState } from 'react';
import { Popover } from '@base-ui/react/popover';
import { useTranslation } from 'react-i18next';
import type { MemoryProfile, ExecutionStatus } from '../../state/execution';
import { Sparkline } from './Sparkline';

// Status-bar chip showing live + post-mortem memory residency for the
// running (or just-finished) query. Renders nothing when no samples have
// arrived yet — the chip pops into the status bar as soon as the first
// `memory_sample` event lands. Click-to-expand opens a popover with both
// bins, the budget threshold, peak, and current.
//
// Visual states:
//   - streaming, no budget: muted text, sparkline only (no % no threshold)
//   - streaming, under budget: muted text, normal sparkline + %
//   - streaming, near budget (>= 80%): amber text + %
//   - streaming, at/over budget (>= 100%): red text + %
//   - done: dimmed but click-expandable; freezes the final values
//
// The pressure threshold (80%) is a UX cue, not a behaviour switch — the
// server side decides when to actually spill.

const NEAR_BUDGET_THRESHOLD = 0.8;
const AT_BUDGET_THRESHOLD = 1.0;

export interface MemoryChipProps {
  profile: MemoryProfile;
  status: ExecutionStatus;
}

export function MemoryChip({ profile, status }: MemoryChipProps) {
  const { t } = useTranslation('query');
  if (profile.latest === null) return null;

  const current = profile.latest.rowBytes;
  const budget = profile.budgetBytes;
  const fraction = budget !== null && budget > 0 ? current / budget : null;
  const isDone = status === 'done' || status === 'cancelled' || status === 'error';

  // Sparkline upper bound: budget if set, else the data's own peak so the
  // line uses the full vertical range. Shared between chip + popover so a
  // sample's apparent height stays consistent across views.
  const sparkMax = budget ?? Math.max(profile.peakRowBytes, 1);
  const rowValues = profile.samples.map((s) => s.rowBytes);

  // VRAM strip: only render when the probe produced data. Hosts without
  // an NVIDIA GPU never receive the field and the strip stays hidden.
  // Uses the latest sample's `vramTotalBytes` as the sparkline ceiling so
  // the line scales against device capacity, not just observed peak.
  const vramUsed = profile.latest.vramUsedBytes;
  const vramTotal = profile.latest.vramTotalBytes;
  const vramAvailable = vramUsed !== null && vramTotal !== null && vramTotal > 0;
  const vramFraction = vramAvailable ? vramUsed! / vramTotal! : null;
  const vramValues = profile.samples.map((s) => s.vramUsedBytes ?? 0);
  const vramPercentText =
    vramFraction !== null ? ` ${Math.round(vramFraction * 100)}%` : '';

  // Pressure-driven foreground colour. Standalone Tailwind classes so the
  // colour stays sharp against the yellow status-bar background in both
  // light and dark themes.
  let textTone = 'text-status-bar-foreground';
  let sparkStroke: string | undefined;
  if (fraction !== null) {
    if (fraction >= AT_BUDGET_THRESHOLD) {
      textTone = 'text-red-700 dark:text-red-500 font-medium';
      sparkStroke = 'currentColor';
    } else if (fraction >= NEAR_BUDGET_THRESHOLD) {
      textTone = 'text-amber-700 dark:text-amber-500';
      sparkStroke = 'currentColor';
    }
  }
  if (isDone) textTone += ' opacity-70';

  const percentText =
    fraction !== null ? ` ${Math.round(fraction * 100)}%` : '';

  return (
    <Popover.Root>
      <Popover.Trigger
        className={`border-status-bar-foreground/25 flex shrink-0 cursor-pointer items-center gap-1.5 whitespace-nowrap border-l px-3 py-1 font-mono ${textTone} hover:bg-status-bar-foreground/10`}
        aria-label={t('memoryChipTooltip')}
      >
        <span>{formatBytes(current)}</span>
        <Sparkline
          values={rowValues}
          maxValue={sparkMax}
          threshold={budget ?? undefined}
          stroke={sparkStroke ?? 'currentColor'}
          className="h-3 w-12"
          ariaLabel={t('memoryChipLabel')}
        />
        {percentText && <span>{percentText}</span>}
        {vramAvailable && (
          <>
            <span className="text-status-bar-foreground/40 px-1">|</span>
            <span>{formatBytes(vramUsed!)}</span>
            <Sparkline
              values={vramValues}
              maxValue={vramTotal!}
              stroke="currentColor"
              className="h-3 w-12"
              ariaLabel={t('memoryVramChipLabel')}
            />
            <span>{vramPercentText}</span>
          </>
        )}
      </Popover.Trigger>
      <Popover.Portal>
        {/* z-index lives on the positioner, not just the popup, so the
            popover sits above the results table's sticky header (which
            creates a z-20 stacking context). Base UI's positioner
            doesn't apply z-index by default, so a popup-only z-50
            renders behind the header. */}
        <Popover.Positioner
          side="top"
          align="end"
          sideOffset={6}
          className="z-[100]"
        >
          <Popover.Popup className="bg-popover text-popover-foreground border-border w-72 rounded-md border p-3 shadow-md">
            <MemoryPopoverBody profile={profile} />
          </Popover.Popup>
        </Popover.Positioner>
      </Popover.Portal>
    </Popover.Root>
  );
}

interface MemoryPopoverBodyProps {
  profile: MemoryProfile;
}

function MemoryPopoverBody({ profile }: MemoryPopoverBodyProps) {
  const { t } = useTranslation('query');
  // Hover index is shared across every sparkline so the crosshair stays
  // aligned vertically across Row / Arena / VRAM. Each sparkline reports
  // hover updates via onHover; we mirror back via hoveredIndex prop so
  // all render the crosshair at the same column.
  const [hoveredIndex, setHoveredIndex] = useState<number | null>(null);

  if (profile.latest === null) return null;

  const rowValues = profile.samples.map((s) => s.rowBytes);
  const arenaValues = profile.samples.map((s) => s.arenaBytes);
  const rowMax = profile.budgetBytes ?? Math.max(profile.peakRowBytes, 1);
  const arenaMax = Math.max(...arenaValues, 1);

  // VRAM strip is only present when the probe produced data on at least
  // the latest sample. Total is taken from the latest sample (device
  // capacity doesn't change mid-query) and used as the sparkline ceiling.
  const vramTotal = profile.latest.vramTotalBytes;
  const vramAvailable = vramTotal !== null && vramTotal > 0;
  const vramValues = profile.samples.map((s) => s.vramUsedBytes ?? 0);

  // Hovered sample: show its values in the bottom row when present, else
  // fall back to the latest sample (mirrors the chip's "current" reading
  // before any hover happens).
  const hoveredSample =
    hoveredIndex !== null && hoveredIndex >= 0 && hoveredIndex < profile.samples.length
      ? profile.samples[hoveredIndex]
      : null;
  const displaySample = hoveredSample ?? profile.latest;
  const isHovering = hoveredSample !== null;

  // Total duration = elapsedMs of the latest sample. Min duration is 0.
  // First sample's elapsedMs is typically close to 0 (cell-entry sample
  // fires before any work), so we just use 0 as the start label.
  const totalElapsedMs = profile.latest.elapsedMs;

  return (
    <div className="flex flex-col gap-2 select-none">
      <div className="text-xs font-medium">{t('memoryPopoverTitle')}</div>

      <div className="flex flex-col gap-0.5">
        <div className="text-muted-foreground text-[10px] uppercase tracking-wide">
          {t('memoryRowBytesLabel')}
        </div>
        <Sparkline
          values={rowValues}
          maxValue={rowMax}
          threshold={profile.budgetBytes ?? undefined}
          fill="rgb(59 130 246 / 0.18)"
          stroke="rgb(59 130 246)"
          className="h-8 w-full"
          ariaLabel={t('memoryRowBytesLabel')}
          interactive
          hoveredIndex={hoveredIndex}
          onHover={setHoveredIndex}
        />
      </div>

      <div className="flex flex-col gap-0.5">
        <div className="text-muted-foreground text-[10px] uppercase tracking-wide">
          {t('memoryArenaBytesLabel')}
        </div>
        <Sparkline
          values={arenaValues}
          maxValue={arenaMax}
          fill="rgb(148 163 184 / 0.18)"
          stroke="rgb(148 163 184)"
          className="h-8 w-full"
          ariaLabel={t('memoryArenaBytesLabel')}
          interactive
          hoveredIndex={hoveredIndex}
          onHover={setHoveredIndex}
        />
      </div>

      {vramAvailable && (
        <div className="flex flex-col gap-0.5">
          <div className="text-muted-foreground text-[10px] uppercase tracking-wide">
            {t('memoryVramBytesLabel')}
          </div>
          <Sparkline
            values={vramValues}
            maxValue={vramTotal!}
            fill="rgb(34 197 94 / 0.18)"
            stroke="rgb(34 197 94)"
            className="h-8 w-full"
            ariaLabel={t('memoryVramBytesLabel')}
            interactive
            hoveredIndex={hoveredIndex}
            onHover={setHoveredIndex}
          />
        </div>
      )}

      {/* Time axis: start (0s) on the left, total duration on the right.
          Aligns visually with the sparklines above since they fill the
          same width container. Middle is intentionally blank — two
          labels keep the strip readable at popover sizes. */}
      <div className="text-muted-foreground flex justify-between font-mono text-[10px]">
        <span>0s</span>
        <span>{formatElapsedMs(totalElapsedMs)}</span>
      </div>

      {/* Single-line labels keep the popover height stable on hover. The
          sparkline labels above this row carry the full descriptive text
          ("Row (budgeted)", "Arena (mmap, OS-paged)") — here we use the
          short forms ("row", "arena") so neither state wraps to a second
          line and the popover doesn't reflow under the cursor. */}
      <div className="text-muted-foreground grid grid-cols-3 gap-2 font-mono text-xs">
        <div className="flex flex-col">
          <span className="text-[10px] uppercase tracking-wide whitespace-nowrap">
            {isHovering ? t('memoryAtTimeLabel') : t('memoryCurrentLabel')}
          </span>
          <span className="text-foreground">
            {isHovering
              ? formatElapsedMs(displaySample.elapsedMs)
              : formatBytes(displaySample.rowBytes)}
          </span>
        </div>
        <div className="flex flex-col">
          <span className="text-[10px] uppercase tracking-wide whitespace-nowrap">
            {isHovering ? t('memoryRowShortLabel') : t('memoryPeakLabel')}
          </span>
          <span className="text-foreground">
            {isHovering
              ? formatBytes(displaySample.rowBytes)
              : formatBytes(profile.peakRowBytes)}
          </span>
        </div>
        <div className="flex flex-col">
          <span className="text-[10px] uppercase tracking-wide whitespace-nowrap">
            {isHovering ? t('memoryArenaShortLabel') : t('memoryBudgetLabel')}
          </span>
          <span className="text-foreground">
            {isHovering
              ? formatBytes(displaySample.arenaBytes)
              : profile.budgetBytes !== null
                ? formatBytes(profile.budgetBytes)
                : t('memoryBudgetNone')}
          </span>
        </div>
      </div>

      {vramAvailable && (
        <div className="text-muted-foreground flex justify-between gap-2 font-mono text-xs">
          <span className="text-[10px] uppercase tracking-wide whitespace-nowrap">
            {t('memoryVramShortLabel')}
          </span>
          <span className="text-foreground">
            {formatBytes(displaySample.vramUsedBytes ?? 0)} / {formatBytes(vramTotal!)}
          </span>
        </div>
      )}
    </div>
  );
}

// Formats elapsed milliseconds as a compact human-readable string. Picks the
// largest unit that keeps the value above 1 so "5.4s" doesn't become
// "0.09min" and "2m 34s" doesn't become "154s". Sub-minute renders with one
// decimal; over a minute drops to whole seconds because the decimal isn't
// useful at that scale.
function formatElapsedMs(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)}ms`;
  const totalSeconds = ms / 1000;
  if (totalSeconds < 60) return `${totalSeconds.toFixed(1)}s`;
  const totalMinutes = Math.floor(totalSeconds / 60);
  const remainingSeconds = Math.floor(totalSeconds % 60);
  if (totalMinutes < 60) return `${totalMinutes}m ${remainingSeconds}s`;
  const totalHours = Math.floor(totalMinutes / 60);
  const remainingMinutes = totalMinutes % 60;
  return `${totalHours}h ${remainingMinutes}m`;
}

// Compact byte formatter. Binary units (1024) match the rest of the
// engine's reporting and what most developer tools display. Picks the
// largest unit that keeps the value above 1 so display reads "47 MB"
// instead of "0.05 GB".
function formatBytes(bytes: number): string {
  const KB = 1024;
  const MB = KB * 1024;
  const GB = MB * 1024;
  if (bytes >= GB) return `${(bytes / GB).toFixed(2)} GB`;
  if (bytes >= MB) return `${(bytes / MB).toFixed(1)} MB`;
  if (bytes >= KB) return `${(bytes / KB).toFixed(0)} KB`;
  return `${Math.round(bytes)} B`;
}
