import { useSnapshot } from 'valtio';
import { MessageSquare, Boxes } from 'lucide-react';
import { navState, setView, type ActiveView } from '@/state/nav';
import { cn } from '@/lib/utils';

// VSCode-flavored icon column. Sits between TitleBar (top) and the active
// view (right). Always visible; tooltips on hover for accessibility. New
// views land here as additional items — order maps to the user's mental
// model, not insertion order (Chat is primary, Models is supporting,
// Editor / Settings / etc. plug in later).
//
// Width is locked to 48 px because the rest of the layout assumes it; if
// we add an expanded-with-labels mode, that's a state in `navState`
// rather than an inline prop here.

interface NavItem {
  id: ActiveView;
  icon: typeof MessageSquare;
  labelKey: string; // i18n key (common namespace) — looked up by callers
}

const ITEMS: readonly NavItem[] = [
  { id: 'chat', icon: MessageSquare, labelKey: 'nav.chat' },
  { id: 'models', icon: Boxes, labelKey: 'nav.models' },
];

export function SideNav() {
  const { view } = useSnapshot(navState);
  return (
    <nav className="bg-background flex w-12 shrink-0 flex-col items-center gap-1 py-2">
      {ITEMS.map((item) => (
        <SideNavButton
          key={item.id}
          item={item}
          active={view === item.id}
          onClick={() => setView(item.id)}
        />
      ))}
    </nav>
  );
}

function SideNavButton({
  item,
  active,
  onClick,
}: {
  item: NavItem;
  active: boolean;
  onClick: () => void;
}) {
  const Icon = item.icon;
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={active ? 'page' : undefined}
      aria-label={item.id}
      className={cn(
        'flex size-9 items-center justify-center rounded-xs transition-colors',
        active
          ? 'bg-muted text-foreground'
          : 'text-muted-foreground hover:bg-muted/60 hover:text-foreground',
      )}
    >
      <Icon className="size-4.5" />
    </button>
  );
}
