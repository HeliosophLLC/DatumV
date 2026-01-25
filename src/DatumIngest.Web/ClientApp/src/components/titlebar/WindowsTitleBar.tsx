import { useSnapshot } from 'valtio';
import { Minus, Square, Copy, X } from 'lucide-react';
import { windowState, minimize, toggleMaximize, close, startDrag } from '@/state/window';
import { cn } from '@/lib/utils';

// Win11-flavored: 32px tall, app title left, [-][□/❐][×] right, square buttons,
// close turns red on hover. Maximize icon swaps to a "restore" glyph (overlapping
// squares) when the window is currently maximized.
//
// An absolute-positioned drag layer fills the header beneath the buttons. The
// CSS `app-drag` fallback is kept for hosts that honor -webkit-app-region;
// when they don't (WebView2 chromeless), the JS mousedown handler asks the
// host to start a native OS drag.
export function WindowsTitleBar() {
  const { maximized } = useSnapshot(windowState);
  const MaxIcon = maximized ? Copy : Square;

  return (
    <header className="app-drag relative flex h-8 items-center bg-background select-none">
      <div className="absolute inset-0" onMouseDown={onTitleBarMouseDown} />
      <div className="relative z-10 px-3 text-xs text-muted-foreground">DatumIngest</div>
      <div className="app-no-drag relative z-10 ml-auto flex">
        <WinButton onClick={minimize} aria-label="Minimize">
          <Minus className="size-3.5" />
        </WinButton>
        <WinButton onClick={toggleMaximize} aria-label={maximized ? 'Restore' : 'Maximize'}>
          <MaxIcon className="size-3" />
        </WinButton>
        <WinButton onClick={close} aria-label="Close" closeStyle>
          <X className="size-3.5" />
        </WinButton>
      </div>
    </header>
  );
}

function onTitleBarMouseDown(event: React.MouseEvent) {
  if (event.button !== 0) return;
  event.preventDefault();
  startDrag();
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
