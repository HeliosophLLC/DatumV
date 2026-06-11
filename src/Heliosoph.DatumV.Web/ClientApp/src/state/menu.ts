// Renderer side of the application-menu plumbing. `initMenu()`
// publishes the localized menu template to main once at startup and
// re-publishes on locale change, and routes native-menu clicks through
// the shared command registry. Called from main.tsx for the SPA root
// only — the loader page and dialog windows skip it, so the loader
// doesn't install a menu it can't service (welcome would otherwise
// surface "New Query", "Close Tab", etc. with no catalog open).

import i18next from 'i18next';
import { subscribe } from 'valtio';
import { host, os } from '@/host';
import {
  buildMenu,
  type MenuNode,
  type MenuLabelKey,
} from '@/commands/menuDefinition';
import { runCommand } from '@/commands/registry';
import { catalogRecentsState, refreshCatalogRecents } from '@/state/catalogRecents';
import { updaterState } from '@/state/updater';

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
  const passRaw = (s: string): string => (isMac ? stripMnemonicMarkers(s) : s);
  return nodes.map((n): unknown => {
    if (n.kind === 'submenu') {
      return { ...n, labelKey: resolve(n.labelKey), children: localize(n.children) };
    }
    if (n.kind === 'item') {
      return { ...n, labelKey: resolve(n.labelKey) };
    }
    if (n.kind === 'rawItem') {
      // Reshape to the wire `item` form — main.ts has no concept of
      // rawItem; for the native menu side, a rawItem with a final
      // label is indistinguishable from a regular item whose key has
      // already been resolved.
      const { label, ...rest } = n;
      return { ...rest, kind: 'item', labelKey: passRaw(label) };
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
  const tree = localize(
    buildMenu({
      isMac: os === 'macos',
      recentCatalogs: catalogRecentsState.recents,
      isCheckingForUpdates: updaterState.status.kind === 'checking',
    }),
  );
  host.setApplicationMenu(tree);
}

let initialized = false;

export function initMenu(): void {
  // Guard against accidental double-init (e.g. a future caller doing
  // it from a useEffect, which StrictMode runs twice in dev). The
  // i18next.on / valtio subscribe wires aren't trivially de-dupable
  // once registered.
  if (initialized) return;
  initialized = true;

  host.onMenuCommand((id) => runCommand(id));

  // Re-publish on locale change. i18next emits 'languageChanged' when
  // state/locale.ts flips settingsState.locale.
  i18next.on('languageChanged', publish);

  // Re-publish when the recents list changes (open / new catalog
  // flows touch the file via main and refreshCatalogRecents() syncs
  // us back).
  subscribe(catalogRecentsState, publish);

  // Re-publish when the updater enters / leaves the `checking` state so
  // Help > "Check for Updates…" greys out while a probe is in flight.
  // Status transitions through other kinds (idle / available / error /
  // not-available) are also handled by this same subscribe — re-running
  // publish is cheap (the wire shape is JSON, main rebuilds the native
  // Menu in one shot), so we don't bother filtering for the specific
  // checking-edge.
  subscribe(updaterState, publish);

  // First publish: deferred a microtask so i18next has finished its
  // synchronous init() before t() runs against it. The recents fetch
  // runs in parallel — the initial publish uses whatever is in state
  // (empty array), and the post-fetch subscribe triggers a republish.
  queueMicrotask(publish);
  void refreshCatalogRecents();
}
