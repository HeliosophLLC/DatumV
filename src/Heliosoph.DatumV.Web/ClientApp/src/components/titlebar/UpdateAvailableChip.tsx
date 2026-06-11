import { useSnapshot } from 'valtio';
import { useTranslation } from 'react-i18next';
import {
  ArrowUpCircle,
  CheckCircle2,
  Loader2,
  AlertTriangle,
} from 'lucide-react';
import { updaterState } from '@/state/updater';
import { cn } from '@/lib/utils';

// Title-bar chip surfacing the updater state. Visibility rules:
//   - `available`   → always visible (clickable, opens release page).
//   - `checking`    → only if showTransient (manual user check); pulses.
//   - `not-available` → only if showTransient; auto-dismissed after 5s
//                       by state/updater.ts.
//   - `error`       → only if showTransient; auto-dismissed after 5s;
//                     hover for the underlying error message.
//   - `idle`        → always hidden.
//
// Background startup checks set showTransient=false so a successful
// silent probe leaves the chrome quiet. The chip is `app-no-drag` so
// clicks reach React handlers instead of starting a window drag.
//
// Mounted between the menu/title and the right-edge window controls in
// each platform title bar (Mac variant uses absolute-positioned title,
// so the chip sits at the right of the bar via ml-auto).
export function UpdateAvailableChip({ className }: { className?: string }) {
  const snap = useSnapshot(updaterState);
  const { t } = useTranslation();
  const { status } = snap;

  if (status.kind === 'idle') return null;

  if (status.kind === 'available') {
    return (
      <button
        type="button"
        onClick={() => {
          void window.electronHost.openExternal(status.releaseUrl);
        }}
        title={t('updater.viewRelease')}
        className={cn(
          'app-no-drag flex items-center gap-1.5 rounded-xs border px-2 py-0.5 text-[11px] transition-colors',
          'cursor-pointer text-blue-600 dark:text-blue-400 border-blue-500/30 bg-blue-500/10 hover:bg-blue-500/20',
          className,
        )}
      >
        <ArrowUpCircle className="size-3" />
        <span>{t('updater.available', { version: status.version })}</span>
      </button>
    );
  }

  // Below here: only render if the user explicitly asked for a check.
  if (!snap.showTransient) return null;

  if (status.kind === 'checking') {
    return (
      <div
        className={cn(
          'app-no-drag flex items-center gap-1.5 rounded-xs border px-2 py-0.5 text-[11px]',
          'text-blue-600 dark:text-blue-400 border-blue-500/30 bg-blue-500/10 animate-pulse',
          className,
        )}
      >
        <Loader2 className="size-3 animate-spin" />
        <span>{t('updater.checking')}</span>
      </div>
    );
  }

  if (status.kind === 'not-available') {
    return (
      <div
        className={cn(
          'app-no-drag flex items-center gap-1.5 rounded-xs border px-2 py-0.5 text-[11px]',
          'text-green-700 dark:text-green-400 border-green-500/30 bg-green-500/10',
          className,
        )}
      >
        <CheckCircle2 className="size-3" />
        <span>{t('updater.upToDate', { version: status.currentVersion })}</span>
      </div>
    );
  }

  // status.kind === 'error'
  return (
    <div
      title={status.message}
      className={cn(
        'app-no-drag flex items-center gap-1.5 rounded-xs border px-2 py-0.5 text-[11px]',
        'text-amber-700 dark:text-amber-400 border-amber-500/30 bg-amber-500/10',
        className,
      )}
    >
      <AlertTriangle className="size-3" />
      <span>{t('updater.checkFailed')}</span>
    </div>
  );
}
