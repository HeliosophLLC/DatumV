// Renderer side of the application-menu plumbing. Side-effect module:
// publishes the localized menu template to main once at startup and
// re-publishes on locale change, and routes native-menu clicks through
// the shared command registry. Imported once from main.tsx.
//
// Mac uses the native screen-top menubar built from this template;
// Win/Linux additionally render the same tree in <MenuBar> inside the
// custom titlebar (the native bar is hidden by frame: false on those
// platforms, but accelerators registered through setApplicationMenu
// still fire globally).

import i18next from 'i18next';
import { host, os } from '@/host';
import {
  buildMenu,
  type MenuNode,
  type MenuLabelKey,
} from '@/commands/menuDefinition';
import { runCommand } from '@/commands/registry';

// Strip mnemonic-marker ampersands for the macOS native menu, where
// `&` would render as a literal character instead of underlining the
// next letter. Win/Linux Electron parses `&` correctly into native
// underlined accelerators, so we leave them in there. `&&` is the
// escape for a literal `&`.
function stripMnemonicMarkers(s: string): string {
  return s.replace(/&(.)/g, '$1');
}

// Produces a wire-shaped tree where labelKey holds resolved text
// (not a translation key). Return type is `unknown[]` because once
// we resolve labels the value is no longer a valid MenuNode — main
// only needs the structure, not the key types.
function localize(nodes: MenuNode[]): unknown[] {
  const isMac = os === 'macos';
  // i18next's typed-t returns `unknown` when called with a union key
  // (the typed-key overload only narrows when given a single literal).
  // Cast to string at this single boundary — keys come from the
  // closed MenuLabelKey union, and the typed-key augmentation
  // guarantees the resource exists.
  const resolve = (key: MenuLabelKey): string => {
    const raw = i18next.t(key) as string;
    return isMac ? stripMnemonicMarkers(raw) : raw;
  };
  return nodes.map((n): unknown => {
    if (n.kind === 'submenu') {
      return { ...n, labelKey: resolve(n.labelKey), children: localize(n.children) };
    }
    if (n.kind === 'item') {
      return { ...n, labelKey: resolve(n.labelKey) };
    }
    if (n.kind === 'role') {
      // Role items prefer Electron's built-in localized label; only
      // override when the menu definition explicitly supplies a key.
      return n.labelKey ? { ...n, labelKey: resolve(n.labelKey) } : n;
    }
    return n;
  });
}

function publish(): void {
  const tree = localize(buildMenu({ isMac: os === 'macos' }));
  host.setApplicationMenu(tree);
}

host.onMenuCommand((id) => runCommand(id));

// Re-publish on locale change. i18next emits 'languageChanged' when
// state/locale.ts flips settingsState.locale.
i18next.on('languageChanged', publish);

// First publish: deferred a microtask so i18next has finished its
// synchronous init() before t() runs against it.
queueMicrotask(publish);
