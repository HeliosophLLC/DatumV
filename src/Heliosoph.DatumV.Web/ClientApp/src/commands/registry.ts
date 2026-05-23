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
  | 'file.exit'
  | 'help.about';

type CommandHandler = () => void | Promise<void>;

const handlers: Record<CommandId, CommandHandler> = {
  'file.newQuery': () => {
    openTab('', undefined, 'sql');
  },
  'file.closeTab': () => {
    const leaf = getFocusedLeaf();
    if (leaf.activeTabId !== null) closeTab(leaf.activeTabId);
  },
  'file.exit': () => {
    host.close();
  },
  'help.about': () => {
    openDialog({ kind: 'about' });
  },
};

export function runCommand(id: string): void {
  const handler = handlers[id as CommandId];
  if (!handler) {
    console.warn('[menu] unknown command id:', id);
    return;
  }
  void handler();
}
