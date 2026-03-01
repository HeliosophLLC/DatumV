import { useSnapshot } from 'valtio';
import { MessageSquare, SquareTerminal, Boxes, Settings as SettingsIcon } from 'lucide-react';
import { navState, setView, type ActiveView } from '@/state/nav';
import { isTornOutWindow } from '@/state/tabs';
import { cn } from '@/lib/utils';

// Compact horizontal nav for the title bar. Replaces the old vertical
// SideNav; chosen icons + ordering are unchanged so the user's mental
// model carries over (Chat / Query / Models on the left, Settings
// pinned to the right of the primary group). Title-bar embedding
// reclaims the 48 px column the SideNav used to occupy — that space
// goes to the editor / chat panes underneath.
//
// Returned null in:
//   - Dialog windows: their title bar already strips min/max + custom
//     content; nav has nothing to switch to.
//   - Torn-out tab windows: those are single-purpose editor surfaces.
//     The view switcher is a main-window concept.
//
// The nav row carries `app-no-drag` so clicks reach the buttons
// instead of starting a window drag — the title bars apply
// `app-drag` to themselves, and we override per-element.

interface NavItem {
  id: ActiveView;
  icon: typeof MessageSquare;
}

const PRIMARY: readonly NavItem[] = [
  { id: 'chat', icon: MessageSquare },
  { id: 'query', icon: SquareTerminal },
  { id: 'models', icon: Boxes },
];

const UTILITY: readonly NavItem[] = [
  { id: 'settings', icon: SettingsIcon },
];

export function AppNav({ dialog = false }: { dialog?: boolean }) {
  const { view } = useSnapshot(navState);
  if (dialog || isTornOutWindow) return null;

  return (
    <div className="app-no-drag relative z-10 flex items-center gap-0.5">
      {PRIMARY.map((item) => (
        <NavButton
          key={item.id}
          item={item}
          active={view === item.id}
        />
      ))}
      <div className="mx-1 h-4 w-px bg-border" />
      {UTILITY.map((item) => (
        <NavButton
          key={item.id}
          item={item}
          active={view === item.id}
        />
      ))}
    </div>
  );
}

function NavButton({ item, active }: { item: NavItem; active: boolean }) {
  const Icon = item.icon;
  return (
    <button
      type="button"
      onClick={() => setView(item.id)}
      aria-current={active ? 'page' : undefined}
      aria-label={item.id}
      title={item.id}
      className={cn(
        'flex size-7 items-center justify-center rounded-xs transition-colors',
        // Active uses the primary accent (same blue as the active-tab
        // top indicator). Inactive hover tints toward primary so the
        // affordance reads as "this thing leads to the editor's blue."
        // Active stays on the default arrow cursor — clicking the
        // already-selected view is a no-op, and pointer here would
        // suggest an action that doesn't exist.
        active
          ? 'cursor-default bg-primary/15 text-primary'
          : 'cursor-pointer text-muted-foreground hover:bg-primary/10 hover:text-primary',
      )}
    >
      <Icon className="size-4" />
    </button>
  );
}
