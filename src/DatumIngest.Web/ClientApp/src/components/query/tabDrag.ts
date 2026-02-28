// Wire format for tab drag-and-drop. A small custom MIME identifies our
// drags so drop targets can distinguish a tab being moved from a text
// selection or a file drop, and a JSON blob in the data carries the
// source leaf id + tab id.

export const TAB_DRAG_MIME = 'application/x-datum-tab';

export interface TabDragPayload {
  fromLeafId: string;
  tabId: string;
}

export function writeTabDragData(
  dt: DataTransfer,
  payload: TabDragPayload,
): void {
  dt.setData(TAB_DRAG_MIME, JSON.stringify(payload));
  // Setting text/plain too gives a friendlier fallback if the drop
  // target happens to read plain text (e.g. drag a tab into an
  // external editor). Cost is one extra setData per dragstart.
  dt.setData('text/plain', payload.tabId);
  dt.effectAllowed = 'move';
}

export function parseTabDragData(dt: DataTransfer): TabDragPayload | null {
  const raw = dt.getData(TAB_DRAG_MIME);
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<TabDragPayload>;
    if (
      typeof parsed.fromLeafId === 'string' &&
      typeof parsed.tabId === 'string'
    ) {
      return { fromLeafId: parsed.fromLeafId, tabId: parsed.tabId };
    }
    return null;
  } catch {
    return null;
  }
}
