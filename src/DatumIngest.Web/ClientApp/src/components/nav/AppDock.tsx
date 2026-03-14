import { useSnapshot } from 'valtio';
import { useTranslation } from 'react-i18next';
import { Settings as SettingsIcon } from 'lucide-react';
import { navState, setView, type ActiveView } from '@/state/nav';
import { isTornOutWindow } from '@/state/tabs';
import { cn } from '@/lib/utils';

// VS Code-style activity bar: a 48px vertical column on the left edge of
// the window. Primary views (Chat / Query / Models) at the top, Settings
// pinned to the bottom via `mt-auto` on the utility group. Active item
// gets a left accent stripe — same affordance VS Code uses, so the
// "you are here" read is instant.
//
// Hidden in dialog and torn-out windows: those modes have no view
// switcher (dialogs are single-purpose modals; torn-out windows host one
// query tab). Matches the previous AppNav gate.

interface DockItem {
  id: ActiveView;
  icon: typeof SettingsIcon;
}

// Top of the dock — reserved for future navigator surfaces (Tables,
// Files, etc.). Empty for now; the spacer below pushes Settings to the
// bottom regardless.
const PRIMARY: readonly DockItem[] = [];

const UTILITY: readonly DockItem[] = [
  { id: 'settings', icon: SettingsIcon },
];

export function AppDock({ dialog = false }: { dialog?: boolean }) {
  const { view } = useSnapshot(navState);
  if (dialog || isTornOutWindow) return null;

  return (
    <nav
      aria-label="Primary"
      className="flex w-12 flex-col items-center border-r bg-background py-1"
    >
      {PRIMARY.map((item) => (
        <DockButton key={item.id} item={item} active={view === item.id} />
      ))}
      <div className="mt-auto" />
      {UTILITY.map((item) => (
        <DockButton key={item.id} item={item} active={view === item.id} />
      ))}
    </nav>
  );
}

function DockButton({ item, active }: { item: DockItem; active: boolean }) {
  const { t } = useTranslation();
  const Icon = item.icon;
  const label = t(`nav.${item.id}`);
  // Active dock item toggles back to the workspace (query/tab editor)
  // — VS Code-style activity bar behavior: clicking the highlighted
  // item closes its surface and returns to the editor.
  return (
    <button
      type="button"
      onClick={() => setView(active ? 'query' : item.id)}
      aria-current={active ? 'page' : undefined}
      aria-label={label}
      title={label}
      className={cn(
        'relative flex size-10 items-center justify-center rounded-xs transition-colors',
        // Left accent stripe on the active item — pseudo-element so we
        // don't need an extra wrapper div per button.
        'before:absolute before:top-1.5 before:bottom-1.5 before:left-0 before:w-0.5 before:rounded-r-sm before:bg-primary before:transition-opacity',
        active
          ? 'cursor-default bg-primary/15 text-primary before:opacity-100'
          : 'cursor-pointer text-muted-foreground hover:bg-primary/10 hover:text-primary before:opacity-0',
      )}
    >
      <Icon className="size-5" />
    </button>
  );
}
