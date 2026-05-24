import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';

import {
  levelToPercent,
  resetZoom,
  zoomIn,
  zoomOut,
  zoomState,
} from '@/state/zoom';

// Status-bar chip showing the current app-wide zoom percentage with
// inline −/+ controls. Clicking the percent label resets to 100%.
// State + cross-window sync live in state/zoom.ts; this is a pure
// view onto the Valtio snapshot.
export function ZoomChip() {
  const { t } = useTranslation('status');
  const snap = useSnapshot(zoomState);
  const percent = levelToPercent(snap.level);

  return (
    <div
      className="border-border text-muted-foreground flex w-28 shrink-0 items-stretch border-l font-mono text-xs"
      aria-label={t('zoomChip.label')}
    >
      <button
        type="button"
        onClick={zoomOut}
        aria-label={t('zoomChip.zoomOut')}
        className="hover:bg-muted/40 flex w-6 cursor-pointer items-center justify-center"
      >
        −
      </button>
      <button
        type="button"
        onClick={resetZoom}
        aria-label={t('zoomChip.reset')}
        className="hover:bg-muted/40 flex flex-1 cursor-pointer items-center justify-center tabular-nums"
      >
        {t('zoomChip.percent', { percent })}
      </button>
      <button
        type="button"
        onClick={zoomIn}
        aria-label={t('zoomChip.zoomIn')}
        className="hover:bg-muted/40 flex w-6 cursor-pointer items-center justify-center"
      >
        +
      </button>
    </div>
  );
}
