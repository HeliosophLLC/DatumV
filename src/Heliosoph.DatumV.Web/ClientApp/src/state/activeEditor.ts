import type * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';

// Leaf-keyed registry of mounted Monaco editors. Each LeafPaneView
// registers its editor on mount and unregisters on dispose. Lookups go
// through `getEditorForLeaf(leafId)` rather than a "whoever last had
// focus" global, so a Run command targeting a specific leaf reads the
// right editor regardless of where DOM focus currently sits — which
// matters because Electron menu accelerators fire without disturbing
// renderer focus, and a popup-menu click hands focus to the menu
// button before the command runs.

const editorsByLeaf = new Map<string, monaco.editor.IStandaloneCodeEditor>();

export function registerLeafEditor(
  leafId: string,
  editor: monaco.editor.IStandaloneCodeEditor,
): void {
  editorsByLeaf.set(leafId, editor);
}

export function unregisterLeafEditor(
  leafId: string,
  editor: monaco.editor.IStandaloneCodeEditor,
): void {
  // Only clear if the slot still points at *this* editor — a remount
  // (e.g. SQL ↔ function tab kind swap) may have already replaced it.
  if (editorsByLeaf.get(leafId) === editor) {
    editorsByLeaf.delete(leafId);
  }
}

/**
 * Returns the SQL to execute for "run" on the given leaf. If that
 * leaf's editor has a non-empty selection, returns just that text —
 * matches SSMS / DataGrip / VSCode SQL Tools. Otherwise returns the
 * full tab SQL.
 */
export function resolveRunSql(fallbackSql: string, leafId: string): string {
  const editor = editorsByLeaf.get(leafId);
  if (!editor) return fallbackSql;
  const selection = editor.getSelection();
  if (!selection || selection.isEmpty()) return fallbackSql;
  const model = editor.getModel();
  if (!model) return fallbackSql;
  const selected = model.getValueInRange(selection);
  return selected.trim().length > 0 ? selected : fallbackSql;
}
