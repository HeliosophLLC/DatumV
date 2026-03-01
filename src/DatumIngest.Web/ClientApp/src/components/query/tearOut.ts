import { panesState, findTab, closeTab } from '@/state/tabs';
import { executionsState } from '@/state/execution';
import type { TabDragPayload } from './tabDrag';

// Tab tear-out helpers. The trigger is `dragend` on a TabChip with
// `dropEffect === 'none'` — no in-window or cross-window drop handler
// claimed the drag — which we interpret as "the user released outside
// every window of our app, spawn a new one with this tab in it."
//
// All cross-window state changes go through Electron's main process:
// the source renderer can't directly mutate the destination's
// `panesState`, and vice versa. Main acts as a postman + window
// factory; see electron/main.ts for the IPC schema.

/**
 * If the dragged tab is still alive in this window AND not currently
 * running a query, spawn a torn-out window for it at the cursor's
 * screen position and close the source copy. No-op otherwise.
 *
 * Why we re-check "tab still alive": between `dragstart` (where we
 * captured the payload) and `dragend` (where we fire this), the tab
 * might have moved to another window via a cross-window drop. In that
 * case the source already removed it and we have nothing to tear out.
 *
 * Why "not running": cross-window moves disconnect the tab from its
 * in-flight execution state — the running query is reachable only from
 * the original renderer. The user chose "refuse while running" as the
 * tear-out policy; this gate enforces it.
 */
export async function tearOutTabIfNoDrop(
  payload: TabDragPayload,
): Promise<void> {
  // No Electron host in this run (dev outside the shell, tests). Tear-
  // out is a host-driven feature; bail silently.
  const host = window.electronHost;
  if (!host?.isElectron) return;

  // The tab might have already moved (cross-window drop). Bail.
  const found = findTab(panesState.root, payload.tabId);
  if (!found) return;

  // Running guard. `executionsState` is keyed by tab id; if there's
  // any entry whose status is 'streaming', refuse the tear-out and
  // leave the tab in place. The user can cancel the run and try again.
  const exec = executionsState.byTabId[payload.tabId];
  if (exec?.status === 'streaming') return;

  // Released inside one of our windows (any of them, including the
  // source's own title bar / nav / blank chrome)? The user almost
  // certainly intended to drop in our app — they just landed on a
  // non-drop-target region. Don't spawn a new window for that.
  // The dragend `dropEffect === 'none'` check alone doesn't catch
  // this: any drop on chrome that lacks an `onDrop` handler shows up
  // with dropEffect 'none' even though the cursor is squarely inside
  // our window.
  if (await host.isCursorOverApp()) return;

  // Cursor's screen position is read from Electron rather than from
  // the dragend event — `dragend`'s clientX/clientY are unreliable
  // when the drag ended outside the source window. Subtracting a
  // small offset so the new window's title bar sits under the cursor.
  const { x, y } = await host.getCursorScreenPoint();

  await host.spawnTabWindow({
    seed: {
      id: payload.tab.id,
      title: payload.tab.title,
      sql: payload.tab.sql,
      editorSize: payload.tab.editorSize,
    },
    x: x - 80,
    y: y - 12,
  });

  // Remove from the source after the new window is requested. Order
  // matters only modestly: even if the spawn IPC failed silently, the
  // tab is preserved in the seeded URL hash payload (Electron has it),
  // so closing locally doesn't risk data loss.
  closeTab(payload.tabId);
}

/**
 * Tells the Electron main process to forward a "remove this tab"
 * instruction to the source window. The destination calls this from
 * its drop handler after materialising the tab locally via
 * `importTabIntoLeaf` / `importTabAsSplit`. The source's preload
 * listener (wired in state/tabs.ts) closes the tab on receipt.
 *
 * No-op in non-Electron runs and when the source window id is missing
 * from the payload (cross-platform safety; the drop is still accepted
 * locally, the source just keeps a duplicate copy until manually
 * closed — degraded but not broken).
 */
export function notifySourceToRemove(payload: TabDragPayload): void {
  const host = window.electronHost;
  if (!host?.isElectron) return;
  if (payload.sourceWindowId == null) return;
  void host.removeTabInSource({
    sourceWindowId: payload.sourceWindowId,
    tabId: payload.tabId,
  });
}

/**
 * True when this drop payload originated in another Electron window of
 * the app — i.e. the tab id isn't in our own panesState. Used by the
 * drop handlers in TabStrip / LeafPaneView to choose between the
 * in-window move/split actions and their `importTab…` cross-window
 * equivalents.
 */
export function isCrossWindowDrop(payload: TabDragPayload): boolean {
  return findTab(panesState.root, payload.tabId) === null;
}
