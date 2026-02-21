import { useEffect, useRef } from 'react';
import { useSnapshot } from 'valtio';
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';
import Editor, { type OnMount } from '@monaco-editor/react';
import { tabsState, setTabSql } from '@/state/tabs';
import { settingsState } from '@/state/settings';
import { disposeTabExecution, runTab } from '@/state/execution';
import { setActiveEditor, resolveRunSql } from '@/state/activeEditor';
import {
  ResizableHandle,
  ResizablePanel,
  ResizablePanelGroup,
} from '@/components/ui/resizable';
import { TabStrip } from './TabStrip';
import { QueryToolbar } from './QueryToolbar';
import { ResultsPane } from './ResultsPane';

// Single Monaco instance per active panel; per-tab `ITextModel` instances
// preserve cursor / scroll / undo across tab switches. The editor's
// `setModel` is the cheap O(1) operation we want every time `activeTabId`
// changes — we never re-mount the editor.
//
// PR 1 keeps language=sql with Monaco's built-in Monarch tokenizer.
// PR 3 replaces that with the DatumIngest grammar + REST-backed
// completion / hover / signature / diagnostics providers.

export function QueryEditorView() {
  const { tabs, activeTabId } = useSnapshot(tabsState);
  const { theme } = useSnapshot(settingsState);

  // Per-tab Monaco model cache. Keyed by tab.id. Created lazily the first
  // time a tab becomes active; disposed when the tab is closed.
  const modelsRef = useRef<Map<string, monaco.editor.ITextModel>>(new Map());
  const editorRef = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);

  // Garbage-collect models for tabs that have been closed. Runs whenever
  // the tab set changes; this is the only time a model needs disposing.
  // Also tears down any in-flight execution for closed tabs so the
  // AbortController + state slot don't leak.
  useEffect(() => {
    const liveIds = new Set(tabs.map((t) => t.id));
    for (const [id, model] of modelsRef.current) {
      if (!liveIds.has(id)) {
        model.dispose();
        modelsRef.current.delete(id);
        disposeTabExecution(id);
      }
    }
  }, [tabs]);

  // Resolves (or lazily creates) the model for the active tab and binds
  // it to the editor. Shared between the active-tab effect and `onMount`
  // because Monaco's editor ref isn't available until onMount fires —
  // running the swap only from useEffect would miss the very first
  // active tab on initial mount.
  function syncActiveModel() {
    const editor = editorRef.current;
    if (!editor || activeTabId === null) return;

    const tab = tabs.find((t) => t.id === activeTabId);
    if (!tab) return;

    let model = modelsRef.current.get(activeTabId);
    if (!model) {
      // Per-tab unique URI ensures Monaco treats each model as a distinct
      // document for diagnostics / providers. The path is synthetic — it
      // never hits disk.
      const uri = monaco.Uri.parse(`inmemory://tabs/${activeTabId}.sql`);
      model = monaco.editor.createModel(tab.sql, 'sql', uri);
      modelsRef.current.set(activeTabId, model);
    } else if (model.getValue() !== tab.sql) {
      // External mutation (e.g. localStorage restore) — sync the model
      // text back from state so the editor shows the right content.
      model.setValue(tab.sql);
    }
    editor.setModel(model);
    editor.focus();
  }

  useEffect(() => {
    syncActiveModel();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeTabId, tabs]);

  // Dispose every model when the view unmounts. Important because
  // `monaco.editor.createModel` registers globally; orphaned models
  // outlive React's lifecycle otherwise.
  useEffect(() => {
    const models = modelsRef.current;
    return () => {
      for (const model of models.values()) model.dispose();
      models.clear();
    };
  }, []);

  const onMount: OnMount = (editor, monacoNs) => {
    editorRef.current = editor;
    setActiveEditor(editor);
    // Clear the ref when Monaco disposes itself — happens when the
    // editor unmounts (e.g. user closes the last tab). Without this,
    // the next mount's active-tab effect could fire with a stale
    // reference and call `setModel` on a disposed editor.
    editor.onDidDispose(() => {
      if (editorRef.current === editor) {
        editorRef.current = null;
      }
      setActiveEditor(null);
    });

    // F5 + Ctrl/Cmd+Enter run the active tab's SQL. Both read state
    // directly from the proxy so the callback never closes over a
    // stale `activeTabId` / `sql` snapshot. `resolveRunSql` reads
    // the editor's current selection and runs that text only when
    // non-empty — full tab text otherwise.
    const runActive = () => {
      const id = tabsState.activeTabId;
      if (id === null) return;
      const tab = tabsState.tabs.find((x) => x.id === id);
      if (!tab) return;
      void runTab(id, resolveRunSql(tab.sql));
    };
    editor.addCommand(monacoNs.KeyCode.F5, runActive);
    editor.addCommand(monacoNs.KeyMod.CtrlCmd | monacoNs.KeyCode.Enter, runActive);

    // Bind the active tab's model immediately. The active-tab effect
    // has already run (with editorRef still null) and won't re-run
    // until activeTabId/tabs changes, so we must do the first swap
    // ourselves.
    syncActiveModel();
  };

  // Resolve the theme. `settings.theme` is the user pref ('system' |
  // 'light' | 'dark'); Monaco needs a concrete name. Mirror the same
  // resolution the rest of the app uses via the `.dark` class on
  // <html> — set by `state/theme`.
  const monacoTheme = resolveMonacoTheme(theme);

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <TabStrip />
      <QueryToolbar />
      {/* Vertical split: editor on top, results below. The split persists
          across tab switches because the panel group is structural,
          not per-tab. Per-tab Monaco models + per-tab execution state
          stay live regardless of where the user drags the divider. */}
      {activeTabId !== null ? (
        <ResizablePanelGroup orientation="vertical" className="flex-1">
          <ResizablePanel id="query-editor" defaultSize="65%" minSize="20%" className="flex flex-col overflow-hidden">
            {/* `key` reset isn't needed — we swap models imperatively.
                When all tabs are closed we drop the editor from the tree
                entirely, both to release Monaco state and to leave a clean
                blank surface (the empty TabStrip stays above).
                Remounting on next openTab() is cheap because Monaco is
                already initialised. */}
            <Editor
              onMount={onMount}
              theme={monacoTheme}
              defaultLanguage="sql"
              defaultValue={tabs.find((t) => t.id === activeTabId)?.sql ?? ''}
              options={{
                automaticLayout: true,
                fontFamily: 'var(--font-mono)',
                fontSize: 13,
                minimap: { enabled: false },
                scrollBeyondLastLine: false,
                wordWrap: 'on',
                renderLineHighlight: 'all',
              }}
              onChange={(value) => {
                if (activeTabId !== null && value !== undefined) {
                  setTabSql(activeTabId, value);
                }
              }}
            />
          </ResizablePanel>
          <ResizableHandle />
          <ResizablePanel id="query-results" defaultSize="35%" minSize="10%" className="flex flex-col overflow-hidden">
            <ResultsPane />
          </ResizablePanel>
        </ResizablePanelGroup>
      ) : (
        <div className="flex-1 overflow-hidden" />
      )}
    </div>
  );
}

function resolveMonacoTheme(pref: string): string {
  if (pref === 'dark') return 'vs-dark';
  if (pref === 'light') return 'vs';
  // 'system' — defer to the resolved class on <html>. state/theme.ts
  // toggles `.dark`, so we read that synchronously here.
  if (typeof document !== 'undefined' && document.documentElement.classList.contains('dark')) {
    return 'vs-dark';
  }
  return 'vs';
}
