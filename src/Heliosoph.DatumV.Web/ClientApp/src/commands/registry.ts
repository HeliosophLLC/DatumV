// Single source of truth for "what does menu item X do." Both the
// renderer-drawn <MenuBar> (Windows/Linux) and the native macOS menu
// bar route through runCommand(id), so behavior stays identical across
// surfaces and there's only one path to test.
//
// Adding a command: add the id here + a handler + an entry in
// commands/menuDefinition.ts. That's it; both surfaces pick it up.

import { host } from '@/host';
import {
  openTab,
  getFocusedLeaf,
  requestCloseTab,
  saveActiveTab,
  panesState,
  findLeaf,
} from '@/state/tabs';
import { openDialog } from '@/state/dialogs';
import { runTab } from '@/state/execution';
import { runFunctionTab } from '@/state/functionForm';
import { resolveRunSql } from '@/state/activeEditor';
import { beginExport } from '@/state/export';
import { resetZoom, zoomIn, zoomOut } from '@/state/zoom';
import { checkForUpdates } from '@/state/updater';

export type CommandId =
  | 'file.newQuery'
  | 'file.save'
  | 'file.closeTab'
  | 'file.newCatalog'
  | 'file.openCatalog'
  | 'file.exit'
  | 'query.run'
  | 'query.export'
  | 'view.zoomIn'
  | 'view.zoomOut'
  | 'view.zoomReset'
  | 'help.about'
  | 'help.checkForUpdates';

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
  'file.save': () => saveActiveTab(),
  'file.closeTab': () => {
    const leaf = getFocusedLeaf();
    if (leaf.activeTabId !== null) return requestCloseTab(leaf.activeTabId);
  },
  'file.newCatalog': () => host.pickAndCreateCatalog(),
  'file.openCatalog': () => host.pickAndOpenCatalog(),
  'file.exit': () => {
    host.close();
  },
  'query.run': () => {
    // Mirrors the editor's Play button: route through resolveRunSql so a
    // non-empty selection runs just that text, full tab SQL otherwise.
    // Bound to the menu's CmdOrCtrl+Enter accelerator, which Electron
    // dispatches even while Monaco has focus.
    const leafId = panesState.focusedLeafId;
    const leaf = findLeaf(panesState.root, leafId);
    if (!leaf || leaf.activeTabId === null) return;
    const tab = leaf.tabs.find((t) => t.id === leaf.activeTabId);
    if (!tab) return;
    if (tab.kind === 'models' || tab.kind === 'settings' || tab.kind === 'docs') return;
    if (tab.kind === 'function') {
      void runFunctionTab(leaf.activeTabId);
      return;
    }
    void runTab(leaf.activeTabId, resolveRunSql(tab.sql, leafId));
  },
  'query.export': () => {
    // Same enablement bar as `query.run` but stricter: function tabs
    // synthesise their script from form state and have no single SQL
    // surface to wrap in a COPY. Pinned tabs (Models / Settings / Docs)
    // have no SQL at all. beginExport itself guards on tab.kind so the
    // menu accelerator stays inert when it shouldn't fire.
    const leafId = panesState.focusedLeafId;
    void beginExport(leafId);
  },
  'view.zoomIn': () => zoomIn(),
  'view.zoomOut': () => zoomOut(),
  'view.zoomReset': () => resetZoom(),
  'help.about': () => {
    openDialog({ kind: 'about' });
  },
  'help.checkForUpdates': () => {
    // Fire-and-forget; result arrives via state/updater.ts's status
    // subscription. The Help menu reads `updaterState.status` to swap
    // its label between "Check for updates…" / "Checking…" / etc.
    checkForUpdates();
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
