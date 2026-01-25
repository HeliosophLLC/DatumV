import { useSnapshot } from 'valtio';
import { Minus, Square, Copy, X } from 'lucide-react';
import { windowState, minimize, toggleMaximize, close } from '@/state/window';
import { cn } from '@/lib/utils';

// GNOME-flavored: 36px tall, title centered, controls right. GNOME tends to
// be taller than Windows and centers the title. Square buttons match our
// global rounded-xs rule.
export function LinuxTitleBar() {
  const { maximized } = useSnapshot(windowState);
  const MaxIcon = maximized ? Copy : Square;

  return (
    <header className="app-drag relative flex h-9 items-center border-border bg-background select-none">
      <div className="pointer-events-none absolute inset-0 flex items-center justify-center text-xs text-muted-foreground">
        DatumIngest
      </div>
      <div className="app-no-drag ml-auto flex">
        <LinuxButton onClick={minimize} aria-label="Minimize">
          <Minus className="size-3.5" />
        </LinuxButton>
        <LinuxButton onClick={toggleMaximize} aria-label={maximized ? 'Restore' : 'Maximize'}>
          <MaxIcon className="size-3" />
        </LinuxButton>
        <LinuxButton onClick={close} aria-label="Close" closeStyle>
          <X className="size-3.5" />
        </LinuxButton>
      </div>
    </header>
  );
}

function LinuxButton({
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
        'flex h-9 w-11 items-center justify-center transition-colors',
        closeStyle ? 'hover:bg-red-600 hover:text-white' : 'hover:bg-muted',
      )}
      {...rest}
    >
      {children}
    </button>
  );
}
