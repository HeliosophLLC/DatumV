import type * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';

// Module-scoped reference to the currently-mounted Monaco editor. Set
// from `QueryEditorView` on mount and cleared on dispose. Read from
// the toolbar / keyboard shortcuts so they can sample the editor's
// current selection without prop-drilling the ref.

let activeEditor: monaco.editor.IStandaloneCodeEditor | null = null;

export function setActiveEditor(
  editor: monaco.editor.IStandaloneCodeEditor | null,
): void {
  activeEditor = editor;
}

/**
 * Returns the SQL to execute for "run". If the editor has a non-empty
 * selection, runs just that text — matches SSMS / DataGrip / VSCode SQL
 * Tools. Otherwise returns the full tab SQL.
 *
 * Falls back to `fallbackSql` when no editor is mounted (close-all-tabs
 * state) — the caller's flow handles the empty case.
 */
export function resolveRunSql(fallbackSql: string): string {
  if (!activeEditor) return fallbackSql;
  const selection = activeEditor.getSelection();
  if (!selection || selection.isEmpty()) return fallbackSql;
  const model = activeEditor.getModel();
  if (!model) return fallbackSql;
  const selected = model.getValueInRange(selection);
  return selected.trim().length > 0 ? selected : fallbackSql;
}
