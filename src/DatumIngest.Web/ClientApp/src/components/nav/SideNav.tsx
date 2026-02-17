import { useSnapshot } from 'valtio';
import { MessageSquare, SquareTerminal, Boxes, Settings as SettingsIcon } from 'lucide-react';
import { navState, setView, type ActiveView } from '@/state/nav';
import { cn } from '@/lib/utils';

// VSCode-flavored icon column. Sits between TitleBar (top) and the active
// view (right). Always visible; tooltips on hover for accessibility. New
// views land here as additional items — order maps to the user's mental
// model, not insertion order (Chat is primary, Models is supporting,
// Settings pinned to the bottom alongside any future utility items).
//
// Width is locked to 48 px because the rest of the layout assumes it; if
// we add an expanded-with-labels mode, that's a state in `navState`
// rather than an inline prop here.

interface NavItem {
  id: ActiveView;
  icon: typeof MessageSquare;
  labelKey: string; // i18n key (common namespace) — looked up by callers
}

const PRIMARY: readonly NavItem[] = [
  { id: 'chat', icon: MessageSquare, labelKey: 'nav.chat' },
  { id: 'query', icon: SquareTerminal, labelKey: 'nav.query' },
  { id: 'models', icon: Boxes, labelKey: 'nav.models' },
];

const UTILITY: readonly NavItem[] = [
  { id: 'settings', icon: SettingsIcon, labelKey: 'nav.settings' },
];

export function SideNav() {
  const { view } = useSnapshot(navState);
  return (
    <nav className="bg-background border-r flex w-12 shrink-0 flex-col items-center py-2">
      <div className="flex flex-col items-center gap-1">
        {PRIMARY.map((item) => (
          <SideNavButton
            key={item.id}
            item={item}
            active={view === item.id}
            onClick={() => setView(item.id)}
          />
        ))}
      </div>
      <div className="flex-1" />
      <div className="flex flex-col items-center gap-1">
        {UTILITY.map((item) => (
          <SideNavButton
            key={item.id}
            item={item}
            active={view === item.id}
            onClick={() => setView(item.id)}
          />
        ))}
      </div>
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
