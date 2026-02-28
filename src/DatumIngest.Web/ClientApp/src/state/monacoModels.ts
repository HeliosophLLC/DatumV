import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';

// Process-wide registry of Monaco text models, one per tab. Each entry
// outlives any individual editor instance — when a tab moves between
// leaves under drag-and-drop the source leaf's editor releases the
// model and the destination leaf attaches the same model. That keeps
// undo / cursor / scroll history intact across the move. (Monaco
// `IStandaloneCodeEditor.setModel` is the cheap O(1) swap that makes
// this pattern work.)
//
// Lifecycle:
//   - `acquireModel` is called whenever a leaf becomes responsible for
//     rendering a tab. First call creates the model; subsequent calls
//     return the existing one and (defensively) re-sync text if the
//     state's SQL diverged from the model's value (e.g. localStorage
//     restore in another tab of the same browser).
//   - `releaseModel` is called when a tab is truly gone from the entire
//     pane tree (closeTab, not a cross-pane move). The model is disposed
//     and the registry slot freed. Releasing a tab that's still live
//     elsewhere is a bug and the leaf-side effect guards against it.
//
// Models are NOT released on leaf unmount: a leaf collapsing (e.g.
// because its last tab was dragged out) leaves the moved tab's model
// in the registry for the destination leaf to pick up.

const models = new Map<string, monaco.editor.ITextModel>();

export function acquireModel(
  tabId: string,
  initialSql: string,
): monaco.editor.ITextModel {
  let model = models.get(tabId);
  if (!model) {
    // URI is synthetic — Monaco only needs it to be unique per model.
    // Per-tab URIs let language providers attach diagnostics on the
    // right document.
    const uri = monaco.Uri.parse(`inmemory://tabs/${tabId}.sql`);
    model = monaco.editor.createModel(initialSql, 'sql', uri);
    models.set(tabId, model);
    return model;
  }
  if (model.getValue() !== initialSql) {
    // External mutation — sync. This is intentionally `setValue` and
    // not edit-via-pushEditOperations: a `setValue` is a single undo
    // step, which matches "the document just got reloaded" semantics.
    model.setValue(initialSql);
  }
  return model;
}

export function releaseModel(tabId: string): void {
  const model = models.get(tabId);
  if (!model) return;
  model.dispose();
  models.delete(tabId);
}

/**
 * Has a model been created for this tab? Lets the leaf-side cleanup
 * effect check before calling `releaseModel` — releasing an unknown
 * tab is a no-op, but the check keeps the intent explicit.
 */
export function hasModel(tabId: string): boolean {
  return models.has(tabId);
}
