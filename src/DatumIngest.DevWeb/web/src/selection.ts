// Selection mutators for the results table. Components call these from
// click handlers — they look up the live (non-snapshot) tab on the
// valtio proxy and mutate `tab.selection` directly. Snapshot reads in
// the React tree pick up the new state on next render.
//
// The three primary gestures are:
//   plain click  → select only this cell/col, clear the rest
//   meta click   → toggle this cell/col in the selection
//   shift click  → rectangular range from anchor to clicked
//
// Switching to a different result set in the same tab clears the
// selection — selections are scoped to a single set's coordinate space.

import { state, type SelectionState, type Tab } from './state.js';

export interface ClickModifiers {
  shift: boolean;
  meta: boolean;
}

function liveTab(tabId: string): Tab | null {
  return state.tabs.find((t) => t.id === tabId) ?? null;
}

function ensureSelection(tab: Tab, setIndex: number): SelectionState {
  if (!tab.selection || tab.selection.resultSetIndex !== setIndex) {
    tab.selection = {
      resultSetIndex: setIndex,
      anchor: null,
      rows: [],
      cols: [],
      cells: [],
    };
  }
  return tab.selection;
}

export function cellKey(row: number, col: number): string {
  return `${row},${col}`;
}

// ===== Read helpers =====
//
// Used by render code to decide whether a cell / col should get the
// `.selected` class. Cells inherit row and column selection: a cell is
// "selected" if it's in `cells` OR its row is in `rows` OR its col is
// in `cols`.

export function isCellSelected(
  sel: SelectionState | null | undefined,
  row: number,
  col: number,
): boolean {
  if (!sel) return false;
  if (sel.rows.includes(row)) return true;
  if (sel.cols.includes(col)) return true;
  return sel.cells.includes(cellKey(row, col));
}

export function isColSelected(
  sel: SelectionState | null | undefined,
  col: number,
): boolean {
  if (!sel) return false;
  return sel.cols.includes(col);
}

export function isRowSelected(
  sel: SelectionState | null | undefined,
  row: number,
): boolean {
  if (!sel) return false;
  return sel.rows.includes(row);
}

// ===== Mutators =====

export function selectCell(
  tabId: string,
  setIndex: number,
  row: number,
  col: number,
  mods: ClickModifiers,
): void {
  const tab = liveTab(tabId);
  if (!tab) return;
  const sel = ensureSelection(tab, setIndex);
  const key = cellKey(row, col);

  if (mods.shift && sel.anchor) {
    // Rectangular range from the anchor. Replaces previous cell/row/col
    // selections; anchor stays put so subsequent shift-clicks extend
    // from the same pivot.
    const r0 = Math.min(sel.anchor.row, row);
    const r1 = Math.max(sel.anchor.row, row);
    const c0 = Math.min(sel.anchor.col, col);
    const c1 = Math.max(sel.anchor.col, col);
    const cells: string[] = [];
    for (let r = r0; r <= r1; r++) {
      for (let c = c0; c <= c1; c++) {
        cells.push(cellKey(r, c));
      }
    }
    sel.cells = cells;
    sel.rows = [];
    sel.cols = [];
    return;
  }

  if (mods.meta) {
    const idx = sel.cells.indexOf(key);
    if (idx >= 0) sel.cells.splice(idx, 1);
    else sel.cells.push(key);
    sel.anchor = { row, col };
    return;
  }

  // Plain click — collapse the selection to just this cell.
  sel.cells = [key];
  sel.rows = [];
  sel.cols = [];
  sel.anchor = { row, col };
}

export function selectColumn(
  tabId: string,
  setIndex: number,
  col: number,
  mods: ClickModifiers,
): void {
  const tab = liveTab(tabId);
  if (!tab) return;
  const sel = ensureSelection(tab, setIndex);

  if (mods.shift && sel.anchor) {
    const c0 = Math.min(sel.anchor.col, col);
    const c1 = Math.max(sel.anchor.col, col);
    const cols: number[] = [];
    for (let c = c0; c <= c1; c++) cols.push(c);
    sel.cols = cols;
    sel.rows = [];
    sel.cells = [];
    return;
  }

  if (mods.meta) {
    const idx = sel.cols.indexOf(col);
    if (idx >= 0) sel.cols.splice(idx, 1);
    else sel.cols.push(col);
    sel.anchor = { row: sel.anchor?.row ?? 0, col };
    return;
  }

  sel.cols = [col];
  sel.rows = [];
  sel.cells = [];
  sel.anchor = { row: 0, col };
}

export function clearSelection(tabId: string): void {
  const tab = liveTab(tabId);
  if (tab) tab.selection = null;
}
