import type { PersistedFunctionForm } from '@/state/functionForm';

// Wire format for tab drag-and-drop. A small custom MIME identifies our
// drags so drop targets can distinguish a tab being moved from a text
// selection or a file drop, and a JSON blob in the data carries the
// source leaf id + tab id + the tab's content. The content is included
// so a cross-window drop (drag from one Electron BrowserWindow to
// another) has everything it needs to recreate the tab in the
// destination — each window owns its own panesState, and the tabId
// from the source window won't exist in the destination's tree.

export const TAB_DRAG_MIME = 'application/x-datum-tab';

export interface TabDragPayload {
  /** Source leaf within the source window. */
  fromLeafId: string;
  /** Tab id (stable across the cross-window move). */
  tabId: string;
  /**
   * Electron BrowserWindow id of the source renderer. Used by the
   * destination window to IPC the source ("you can drop your copy of
   * tab X now") via the main process. Null in non-Electron runs
   * (e.g. unit tests) — cross-window flow is then a no-op.
   */
  sourceWindowId: number | null;
  /**
   * True when the source tab had an in-flight query at drag-start.
   * Cross-window moves disconnect the tab from its execution state
   * (each renderer owns its own exec state), so the destination
   * refuses cross-window drops on running tabs. In-window drops
   * ignore this flag — moving a tab within the same window keeps
   * the exec state intact.
   */
  isRunning: boolean;
  /** Full tab content, used by cross-window drops to recreate the tab. */
  tab: {
    id: string;
    title: string;
    /** Discriminator; absent on old drag payloads — the receiver defaults to `'sql'`. */
    kind?: 'sql' | 'function';
    sql: string;
    editorSize?: number;
    /**
     * Function-tab form state slice — present only when `kind === 'function'`
     * and the user has interacted with the form. Files don't survive the
     * round-trip (the `fileNames` mirror inside survives as a display hint
     * only); see PersistedFunctionForm.
     */
    functionForm?: PersistedFunctionForm;
  };
}

export function writeTabDragData(
  dt: DataTransfer,
  payload: TabDragPayload,
): void {
  dt.setData(TAB_DRAG_MIME, JSON.stringify(payload));
  // text/plain fallback also covers external drag targets (e.g. another
  // app's text editor would see the tab id) and survives any platform
  // where the custom MIME is dropped during a cross-process drag.
  dt.setData('text/plain', payload.tabId);
  dt.effectAllowed = 'move';
}

export function parseTabDragData(dt: DataTransfer): TabDragPayload | null {
  const raw = dt.getData(TAB_DRAG_MIME);
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<TabDragPayload>;
    if (
      typeof parsed.fromLeafId !== 'string' ||
      typeof parsed.tabId !== 'string' ||
      !parsed.tab ||
      typeof parsed.tab.id !== 'string' ||
      typeof parsed.tab.title !== 'string' ||
      typeof parsed.tab.sql !== 'string'
    ) {
      return null;
    }
    return {
      fromLeafId: parsed.fromLeafId,
      tabId: parsed.tabId,
      sourceWindowId:
        typeof parsed.sourceWindowId === 'number'
          ? parsed.sourceWindowId
          : null,
      isRunning: parsed.isRunning === true,
      tab: {
        id: parsed.tab.id,
        title: parsed.tab.title,
        kind: parsed.tab.kind === 'function' ? 'function' : 'sql',
        sql: parsed.tab.sql,
        editorSize:
          typeof parsed.tab.editorSize === 'number'
            ? parsed.tab.editorSize
            : undefined,
        // The form slice is opaquely JSON-typed here — the destination's
        // `hydrateFunctionForm` writes whatever fields are present and
        // tolerates missing ones (textValues/etc. default to {} via the
        // spread).
        functionForm: parsed.tab.functionForm,
      },
    };
  } catch {
    return null;
  }
}
