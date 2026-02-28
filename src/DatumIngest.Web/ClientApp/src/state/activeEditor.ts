import type * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';

// Module-scoped reference to the currently-focused Monaco editor. Set by
// each LeafPaneView on focus and cleared on dispose. Read from the
// per-leaf toolbars / keybinds so they can sample the editor's current
// selection without prop-drilling the ref.
//
// With split panes there are multiple Monaco editors alive at once;
// `activeEditor` is whichever one most recently had focus. The owning
// leaf's id is paired so callers can sanity-check that the editor and
// the state they're reading from are talking about the same pane.

let activeEditor: monaco.editor.IStandaloneCodeEditor | null = null;
let activeLeafId: string | null = null;

export function setActiveEditor(
  editor: monaco.editor.IStandaloneCodeEditor | null,
  leafId: string | null = null,
): void {
  activeEditor = editor;
  activeLeafId = leafId;
}

export function clearActiveEditorForLeaf(leafId: string): void {
  if (activeLeafId === leafId) {
    activeEditor = null;
    activeLeafId = null;
  }
}

/**
 * Returns the SQL to execute for "run". If the editor has a non-empty
 * selection, runs just that text — matches SSMS / DataGrip / VSCode SQL
 * Tools. Otherwise returns the full tab SQL.
 *
 * The optional `leafId` lets the caller insist on a specific leaf's
 * editor — falls back to `fallbackSql` if the focused editor belongs to
 * a different leaf (i.e. the user hit Run on one pane's toolbar while
 * another pane has focus).
 */
export function resolveRunSql(fallbackSql: string, leafId?: string): string {
  if (!activeEditor) return fallbackSql;
  if (leafId && activeLeafId !== leafId) return fallbackSql;
  const selection = activeEditor.getSelection();
  if (!selection || selection.isEmpty()) return fallbackSql;
  const model = activeEditor.getModel();
  if (!model) return fallbackSql;
  const selected = model.getValueInRange(selection);
  return selected.trim().length > 0 ? selected : fallbackSql;
}
