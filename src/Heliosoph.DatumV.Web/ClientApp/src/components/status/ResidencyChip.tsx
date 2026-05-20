import { useEffect } from 'react';
import { Popover } from '@base-ui/react/popover';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';

import {
  evictModel,
  refreshResidency,
  residencyState,
} from '@/state/residency';

// Status-bar chip showing how many models are currently resident in
// VRAM, with a popover listing each one and an EVICT button.
//
// Display states:
//   - empty (no models loaded): neutral grey "Residency · No models loaded"
//   - one or more loaded: shows count, lights up busy when any has refs > 0
//
// Server is the source of truth. On mount we trigger a refresh; hub
// events keep the state current afterward. The chip itself renders
// from the Valtio snapshot — no fetch on every render.
export function ResidencyChip() {
  const { t } = useTranslation('status');
  const snap = useSnapshot(residencyState);

  // First mount: kick the seed. Subsequent re-mounts are no-ops because
  // the state is already populated; the refresh is idempotent and
  // cheap (single REST call) so we just call it again rather than
  // gating on `seeded`. This catches the case where the user opens a
  // second window — the second window's state needs to learn about
  // models already loaded.
  useEffect(() => {
    void refreshResidency();
  }, []);

  const entries = Object.values(snap.byName);
  const count = entries.length;
  const busyCount = entries.filter((e) => e.activeRefs > 0).length;

  // Color tone: busy → blue accent; idle resident → muted; empty → muted.
  // Mirrors MemoryChip's pattern of standalone Tailwind classes so the
  // colour reads against the bar's bg-background.
  const tone =
    busyCount > 0
      ? 'text-blue-700 dark:text-blue-400'
      : 'text-muted-foreground';

  // Fixed-width chip (`w-44`) keeps the label stable as `count`
  // transitions through 0 → 1 → many; without a fixed width the chip
  // visibly jumps whenever a model loads or evicts. Content is
  // centered so the change reads as a text swap rather than a slide.
  return (
    <Popover.Root>
      <Popover.Trigger
        className={`border-border hover:bg-muted/40 flex w-44 shrink-0 cursor-pointer items-center justify-center gap-1.5 overflow-hidden whitespace-nowrap border-l px-3 font-mono text-xs ${tone}`}
        aria-label={t('residencyChip.label')}
      >
        <span className="shrink-0 uppercase tracking-wide">
          {t('residencyChip.label')}
        </span>
        <span className="shrink-0">·</span>
        <span className="min-w-0 truncate">
          {count === 0
            ? t('residencyChip.empty')
            : t('residencyChip.modelCount', { count })}
        </span>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Positioner side="top" align="end" sideOffset={6} className="z-[100]">
          <Popover.Popup className="bg-popover text-popover-foreground border-border w-96 rounded-md border p-3 shadow-md">
            <ResidencyPopoverBody />
          </Popover.Popup>
        </Popover.Positioner>
      </Popover.Portal>
    </Popover.Root>
  );
}

function ResidencyPopoverBody() {
  const { t } = useTranslation('status');
  const snap = useSnapshot(residencyState);

  // Stable ordering: alphabetical by name. Resident set changes
  // frequently in calibration scenarios; sort keeps row positions
  // predictable as entries appear and disappear.
  const entries = Object.values(snap.byName).sort((a, b) =>
    a.modelName.localeCompare(b.modelName),
  );

  if (entries.length === 0) {
    return (
      <div className="flex flex-col gap-2 select-none">
        <div className="text-xs font-medium">
          {t('residencyChip.popoverTitle')}
        </div>
        <div className="text-muted-foreground text-xs">
          {t('residencyChip.empty')}
        </div>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2 select-none">
      <div className="text-xs font-medium">
        {t('residencyChip.popoverTitle')}
      </div>
      <table className="w-full text-xs">
        <thead className="text-muted-foreground text-[10px] uppercase tracking-wide">
          <tr>
            <th className="text-left font-normal">
              {t('residencyChip.columnName')}
            </th>
            <th className="text-right font-normal">
              {t('residencyChip.columnWeight')}
            </th>
            <th className="text-right font-normal">
              {t('residencyChip.columnRefs')}
            </th>
            <th />
          </tr>
        </thead>
        <tbody>
          {entries.map((entry) => (
            <tr key={entry.modelName} className="border-border/40 border-t">
              <td className="font-mono">{entry.modelName}</td>
              <td className="text-right font-mono">
                {formatBytes(entry.weightCostBytes)}
              </td>
              <td className="text-right font-mono">
                <span
                  className={
                    entry.activeRefs > 0
                      ? 'rounded bg-blue-500/15 px-1.5 py-0.5 text-blue-700 dark:text-blue-400'
                      : 'text-muted-foreground'
                  }
                >
                  {entry.activeRefs > 0
                    ? t('residencyChip.busyBadge')
                    : t('residencyChip.idleBadge')}
                </span>
              </td>
              <td className="text-right">
                <EvictButton modelName={entry.modelName} pinned={entry.activeRefs > 0} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function EvictButton({ modelName, pinned }: { modelName: string; pinned: boolean }) {
  const { t } = useTranslation('status');

  async function handleClick() {
    const outcome = await evictModel(modelName);
    if (outcome === 'pinned') {
      // Server refused. Surface via console for now; could promote
      // to a toast when toast infrastructure lands in this app.
      console.warn(t('residencyChip.evictPinnedToast'));
    }
    if (outcome === 'notResident') {
      console.warn(t('residencyChip.evictNotResidentToast'));
    }
  }

  return (
    <button
      type="button"
      onClick={handleClick}
      disabled={pinned}
      className="border-border hover:bg-muted/60 cursor-pointer rounded border px-2 py-0.5 text-[10px] uppercase tracking-wide disabled:cursor-not-allowed disabled:opacity-40"
    >
      {t('residencyChip.evictButton')}
    </button>
  );
}

// Local formatBytes copy — same shape as the one in MemoryChip. A
// shared util would be cleaner once a third caller appears.
function formatBytes(bytes: number): string {
  const KB = 1024;
  const MB = KB * 1024;
  const GB = MB * 1024;
  if (bytes >= GB) return `${(bytes / GB).toFixed(2)} GB`;
  if (bytes >= MB) return `${(bytes / MB).toFixed(1)} MB`;
  if (bytes >= KB) return `${(bytes / KB).toFixed(0)} KB`;
  return `${Math.round(bytes)} B`;
}
