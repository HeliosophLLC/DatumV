// Single source of truth for "what does menu item X do." Both the
// renderer-drawn <MenuBar> (Windows/Linux) and the native macOS menu
// bar route through runCommand(id), so behavior stays identical across
// surfaces and there's only one path to test.
//
// Adding a command: add the id here + a handler + an entry in
// commands/menuDefinition.ts. That's it; both surfaces pick it up.

import { host } from '@/host';
import { openTab, closeTab, getFocusedLeaf } from '@/state/tabs';
import { openDialog } from '@/state/dialogs';

export type CommandId =
  | 'file.newQuery'
  | 'file.closeTab'
  | 'file.newCatalog'
  | 'file.openCatalog'
  | 'file.exit'
  | 'help.about';

// Recent-catalog rows ship as `file.openRecent:<path>` — the path is
// encoded into the id rather than carried as a separate menu-item
// field so the existing wire shape (commandId is the only payload
// menu items deliver) stays untouched.
export const OPEN_RECENT_PREFIX = 'file.openRecent:';

type CommandHandler = () => void | Promise<void>;

const handlers: Record<CommandId, CommandHandler> = {
  'file.newQuery': () => {
    openTab('', undefined, 'sql');
  },
  'file.closeTab': () => {
    const leaf = getFocusedLeaf();
    if (leaf.activeTabId !== null) closeTab(leaf.activeTabId);
  },
  'file.newCatalog': () => host.pickAndCreateCatalog(),
  'file.openCatalog': () => host.pickAndOpenCatalog(),
  'file.exit': () => {
    host.close();
  },
  'help.about': () => {
    openDialog({ kind: 'about' });
  },
};

export function runCommand(id: string): void {
  if (id.startsWith(OPEN_RECENT_PREFIX)) {
    const path = id.slice(OPEN_RECENT_PREFIX.length);
    if (path.length > 0) void host.openCatalogPath(path);
    return;
  }
  const handler = handlers[id as CommandId];
  if (!handler) {
    console.warn('[menu] unknown command id:', id);
    return;
  }
  void handler();
}
