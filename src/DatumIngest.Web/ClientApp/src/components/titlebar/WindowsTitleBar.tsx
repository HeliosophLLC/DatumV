import { useSnapshot } from 'valtio';
import { useTranslation } from 'react-i18next';
import { Minus, Square, Copy, X } from 'lucide-react';
import { windowState, minimize, toggleMaximize, close } from '@/state/window';
import { cn } from '@/lib/utils';

// Win11-flavored: 32px tall, app title left, [-][□/❐][×] right, square buttons,
// close turns red on hover. Maximize icon swaps to a "restore" glyph (overlapping
// squares) when the window is currently maximized.
//
// The header has `app-drag` (CSS -webkit-app-region: drag), which Chromium
// honors at the compositor layer — clicking anywhere in the header starts
// the OS drag automatically. The button row needs `app-no-drag` so clicks
// reach the React handlers instead of starting a drag.
export function WindowsTitleBar({ dialog = false }: { dialog?: boolean } = {}) {
  const { maximized } = useSnapshot(windowState);
  const { t } = useTranslation();
  const MaxIcon = maximized ? Copy : Square;

  return (
    <header className="app-drag relative flex h-8 items-center border-b bg-background select-none">
      <div className="relative z-10 px-3 text-xs text-muted-foreground">{t('app.name')}</div>
      <div className="app-no-drag relative z-10 ml-auto flex">
        {!dialog && (
          <>
            <WinButton onClick={minimize} aria-label={t('window.minimize')}>
              <Minus className="size-3.5" />
            </WinButton>
            <WinButton
              onClick={toggleMaximize}
              aria-label={maximized ? t('window.restore') : t('window.maximize')}
            >
              <MaxIcon className="size-3" />
            </WinButton>
          </>
        )}
        <WinButton onClick={close} aria-label={t('window.close')} closeStyle>
          <X className="size-3.5" />
        </WinButton>
      </div>
    </header>
  );
}

function WinButton({
  children,
  onClick,
  closeStyle = false,
  ...rest
}: {
  children: React.ReactNode;
  onClick: () => void;
  closeStyle?: boolean;
  'aria-label': string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        'flex h-8 w-12 items-center justify-center transition-colors',
        closeStyle ? 'hover:bg-red-600 hover:text-white' : 'hover:bg-muted',
      )}
      {...rest}
    >
      {children}
    </button>
  );
}
