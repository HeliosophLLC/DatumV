// Declarative application-menu shape. The same tree is consumed by
// the native Electron menu builder (in electron/main.ts) and the
// renderer-drawn <MenuBar> on Windows/Linux. Labels are i18n keys
// here; state/menu.ts resolves them via i18next before shipping the
// template to main, and the React <MenuBar> resolves them at render
// time the same way.

import type { CommandId } from './registry';
import { OPEN_RECENT_PREFIX } from './registry';
import type { RecentCatalog } from '@/host';
import i18next from 'i18next';

// Closed set of menu-label translation keys. Listed explicitly so
// i18next's typed-t can validate that every labelKey used in the
// menu tree corresponds to an existing locale entry — passing a
// plain `string` would fail the strict key check the project's
// CustomTypeOptions opts into (see src/i18n/types.d.ts).
export type MenuLabelKey =
  | 'menu.app'
  | 'menu.file.label'
  | 'menu.file.newQuery'
  | 'menu.file.save'
  | 'menu.file.newCatalog'
  | 'menu.file.openCatalog'
  | 'menu.file.openRecent'
  | 'menu.file.openRecentEmpty'
  | 'menu.file.closeTab'
  | 'menu.file.exit'
  | 'menu.run.label'
  | 'menu.run.run'
  | 'menu.run.export'
  | 'menu.edit.label'
  | 'menu.view.label'
  | 'menu.view.resetZoom'
  | 'menu.view.zoomIn'
  | 'menu.view.zoomOut'
  | 'menu.window.label'
  | 'menu.help.label'
  | 'menu.help.about';

export type ElectronRole =
  | 'appMenu'
  | 'editMenu'
  | 'windowMenu'
  | 'copy'
  | 'paste'
  | 'cut'
  | 'selectAll'
  | 'undo'
  | 'redo'
  | 'minimize'
  | 'close'
  | 'quit'
  | 'reload'
  | 'forceReload'
  | 'toggleDevTools'
  | 'togglefullscreen'
  | 'about';

export type MenuNode =
  | {
      kind: 'submenu';
      labelKey: MenuLabelKey;
      children: MenuNode[];
      // When set, the native menu uses Electron's stock localized
      // submenu (App / Edit / Window) and ignores `children`. The
      // renderer-drawn <MenuBar> skips these entries entirely since
      // they're macOS-only.
      macRole?: ElectronRole;
    }
  | {
      kind: 'item';
      labelKey: MenuLabelKey;
      commandId: CommandId;
      accelerator?: string;
      enabled?: boolean;
    }
  // Pre-resolved item: label is the final string (not a translation
  // key) and commandId is free-form. Used for dynamic entries like
  // the Open Recent submenu, where the visible label is user data
  // (a folder name) and per-row context (the catalog path) is
  // encoded into the commandId.
  | {
      kind: 'rawItem';
      label: string;
      commandId: string;
      accelerator?: string;
      enabled?: boolean;
    }
  | {
      kind: 'role';
      role: ElectronRole;
      // Optional override; absent → Electron supplies its own
      // localized label (preferred for native menus).
      labelKey?: MenuLabelKey;
    }
  | { kind: 'separator' };

// Constructors keep literal narrowing for labelKey across conditional
// spreads (an inline object literal in `...(cond ? [obj] : [])` loses
// the contextual type from the outer MenuNode[] annotation, which
// widens labelKey to plain string and breaks i18next's typed-t).
function submenu(
  labelKey: MenuLabelKey,
  children: MenuNode[],
  macRole?: ElectronRole,
): MenuNode {
  return macRole ? { kind: 'submenu', labelKey, children, macRole } : { kind: 'submenu', labelKey, children };
}

function item(
  labelKey: MenuLabelKey,
  commandId: CommandId,
  accelerator?: string,
): MenuNode {
  return accelerator
    ? { kind: 'item', labelKey, commandId, accelerator }
    : { kind: 'item', labelKey, commandId };
}

function role(r: ElectronRole, labelKey?: MenuLabelKey): MenuNode {
  return labelKey ? { kind: 'role', role: r, labelKey } : { kind: 'role', role: r };
}

const sep: MenuNode = { kind: 'separator' };

// Drops the currently-open catalog from the list — switching to the
// catalog you're already in is a no-op and clutters the menu. The
// head of recents is always the open one (touchRecent moves it
// there on every open / swap).
function recentChildren(recents: RecentCatalog[]): MenuNode[] {
  const tail = recents.slice(1);
  if (tail.length === 0) {
    return [{
      kind: 'rawItem',
      label: i18next.t('menu.file.openRecentEmpty'),
      commandId: 'noop',
      enabled: false,
    }];
  }
  return tail.map((r): MenuNode => ({
    kind: 'rawItem',
    label: r.displayName,
    commandId: OPEN_RECENT_PREFIX + r.path,
  }));
}

// Builds the menu fresh on every call so locale changes propagate by
// re-running buildMenu + republishing. isMac branches only on where
// the App / Window menus sit and on whether the exit/quit item lives
// in File (Win/Linux) vs the App menu (macOS, supplied by appMenu).
export function buildMenu(opts: {
  isMac: boolean;
  recentCatalogs?: RecentCatalog[];
}): MenuNode[] {
  const { isMac, recentCatalogs = [] } = opts;
  return [
    ...(isMac ? [submenu('menu.app', [], 'appMenu')] : []),
    submenu('menu.file.label', [
      item('menu.file.newCatalog', 'file.newCatalog', 'CmdOrCtrl+Shift+N'),
      item('menu.file.openCatalog', 'file.openCatalog', 'CmdOrCtrl+Shift+O'),
      submenu('menu.file.openRecent', recentChildren(recentCatalogs)),
      sep,
      item('menu.file.newQuery', 'file.newQuery', 'CmdOrCtrl+N'),
      item('menu.file.save', 'file.save', 'CmdOrCtrl+S'),
      sep,
      item('menu.file.closeTab', 'file.closeTab', 'CmdOrCtrl+W'),
      ...(isMac ? [] : [sep, item('menu.file.exit', 'file.exit', 'Alt+F4')]),
    ]),
    submenu('menu.run.label', [
      item('menu.run.run', 'query.run', 'CmdOrCtrl+Enter'),
      item('menu.run.export', 'query.export', 'CmdOrCtrl+Shift+E'),
    ]),
    submenu('menu.edit.label', [
      role('undo'),
      role('redo'),
      sep,
      role('cut'),
      role('copy'),
      role('paste'),
      role('selectAll'),
    ], 'editMenu'),
    submenu('menu.view.label', [
      role('reload'),
      role('forceReload'),
      role('toggleDevTools'),
      sep,
      // Renderer-owned zoom — routes through state/zoom.ts so a change
      // in one window propagates to every other via localStorage. The
      // Electron role variants are per-WebContents only, which would
      // desync torn-out tabs and dialog children from the main SPA.
      item('menu.view.resetZoom', 'view.zoomReset', 'CmdOrCtrl+0'),
      item('menu.view.zoomIn', 'view.zoomIn', 'CmdOrCtrl+Plus'),
      item('menu.view.zoomOut', 'view.zoomOut', 'CmdOrCtrl+-'),
      sep,
      role('togglefullscreen'),
    ]),
    ...(isMac ? [submenu('menu.window.label', [], 'windowMenu')] : []),
    submenu('menu.help.label', [item('menu.help.about', 'help.about')]),
  ];
}
