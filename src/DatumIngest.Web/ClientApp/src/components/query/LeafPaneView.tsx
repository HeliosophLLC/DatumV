import { useEffect, useRef, useState } from 'react';
import { useSnapshot } from 'valtio';
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';
import Editor, { type OnMount } from '@monaco-editor/react';
import { usePanelRef } from 'react-resizable-panels';
import {
  panesState,
  setTabSql,
  setTabEditorSize,
  focusLeaf,
  findLeaf,
  moveTab,
  splitLeaf,
  importTabIntoLeaf,
  importTabAsSplit,
} from '@/state/tabs';
import { isCrossWindowDrop, notifySourceToRemove } from './tearOut';
import { settingsState } from '@/state/settings';
import { disposeTabExecution } from '@/state/execution';
import {
  setActiveEditor,
  clearActiveEditorForLeaf,
} from '@/state/activeEditor';
import { acquireModel, releaseModel } from '@/state/monacoModels';
import {
  ResizableHandle,
  ResizablePanel,
  ResizablePanelGroup,
} from '@/components/ui/resizable';
import { TabStrip } from './TabStrip';
import { ResultsPane } from './ResultsPane';
import {
  PaneSplitOverlay,
  zoneForCursor,
  type DropZone,
} from './PaneSplitOverlay';
import { TAB_DRAG_MIME, parseTabDragData } from './tabDrag';
import { resolveMonacoTheme } from './monacoTheme';

// Single leaf pane — one tab strip, one Monaco editor, one results pane.
// `QueryEditorView` composes one or more of these into a tree via
// ResizablePanelGroup. Monaco text models are NOT owned by the leaf —
// they live in a module-scoped registry (state/monacoModels) so a tab
// keeps its undo / cursor / scroll history when it moves between
// leaves under drag-and-drop. The leaf borrows the model from the
// registry for whichever tab is active and releases it only when the
// tab is truly gone from the entire pane tree.

export function LeafPaneView({ leafId }: { leafId: string }) {
  useSnapshot(panesState);
  const { theme } = useSnapshot(settingsState);
  const leaf = findLeaf(panesState.root, leafId);

  const editorRef = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  // Tracks which tab ids this leaf has hosted so the cleanup effect can
  // notice when a tab leaves the leaf. The set is local — disposal
  // decisions consult the global tree via `findTabAnywhere`, not this
  // set's contents.
  const knownTabsRef = useRef<Set<string>>(new Set());

  // Imperative handle on the editor's ResizablePanel. Lets the tab-
  // change effect call `resize()` to swap the editor / results split
  // to whatever the newly-active tab had saved, without remounting the
  // panel group (which would kill Monaco state and animate ugly).
  const editorPanelRef = usePanelRef();

  // Drag-and-drop state for the split overlay. The overlay itself is
  // purely presentational — it has pointer-events: none even when
  // visible, which is what lets the editor underneath stay clickable
  // but also means the overlay can never be the drag target. So the
  // handlers live here on the leaf's outer container and we pass the
  // computed zone down to the overlay.
  const [dropActive, setDropActive] = useState(false);
  const [dropZone, setDropZone] = useState<DropZone>(null);
  const dragDepthRef = useRef(0);
  const bodyRef = useRef<HTMLDivElement | null>(null);

  // Snapshot of this leaf's current tabs / active id. Reads through the
  // proxy each render so Valtio invalidation works as expected.
  const tabs = leaf?.tabs ?? [];
  const activeTabId = leaf?.activeTabId ?? null;

  // Release model + execution state for tabs that have truly left the
  // pane tree (closed, not just moved to another leaf). Two-step:
  //   1. Diff `knownTabsRef` against current leaf tabs to find what
  //      left THIS leaf. Update `knownTabsRef` for the next diff.
  //   2. For each departed id, only release if `findTabAnywhere` is
  //      false — otherwise the tab is alive in a sibling leaf and
  //      needs to keep its model so its undo history survives.
  useEffect(() => {
    const liveHere = new Set(tabs.map((t) => t.id));
    const known = knownTabsRef.current;
    for (const id of known) {
      if (!liveHere.has(id) && !findTabAnywhere(id)) {
        releaseModel(id);
        disposeTabExecution(id);
      }
    }
    knownTabsRef.current = liveHere;
  }, [tabs]);

  function syncActiveModel() {
    const editor = editorRef.current;
    if (!editor || activeTabId === null) return;

    const tab = tabs.find((t) => t.id === activeTabId);
    if (!tab) return;

    const model = acquireModel(activeTabId, tab.sql);
    editor.setModel(model);
    editor.focus();
  }

  useEffect(() => {
    syncActiveModel();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeTabId, tabs]);

  // Restore the editor / results split for the newly-active tab. Reads
  // through the proxy each time so we don't capture a stale snapshot —
  // this matters when a cross-pane drag changes the leaf's tabs and the
  // effect re-runs with a freshly arrived tab. Programmatic `resize`
  // also re-fires the group's `onLayoutChanged` below; the threshold
  // there absorbs the no-op write-back.
  useEffect(() => {
    if (activeTabId === null) return;
    const live = findLeaf(panesState.root, leafId);
    const tab = live?.tabs.find((t) => t.id === activeTabId);
    if (!tab) return;
    editorPanelRef.current?.resize(`${tab.editorSize ?? 65}%`);
  }, [activeTabId, leafId, editorPanelRef]);

  // Persist the editor's share of the split whenever the layout
  // settles (after a user drag, or after programmatic resize). We
  // attribute the change to whichever tab is *currently* active on
  // this leaf — read fresh through the proxy rather than captured.
  function onPanelLayoutChanged() {
    const size = editorPanelRef.current?.getSize().asPercentage;
    if (size === undefined) return;
    const live = findLeaf(panesState.root, leafId);
    if (!live || live.activeTabId === null) return;
    setTabEditorSize(live.activeTabId, size);
  }

  // On leaf unmount, clear our active-editor registration. We also
  // sweep `knownTabsRef` for tabs that have vanished from the tree —
  // this catches the close-last-tab path where closing collapses the
  // leaf in the same render. The diff effect above never sees the
  // empty `tabs` snapshot in that case (React unmounts us before the
  // effect re-runs), so models would leak without this sweep. Tabs
  // that survived elsewhere (cross-pane move that emptied the leaf)
  // intentionally keep their models — they're attached to whichever
  // leaf picks them up next.
  useEffect(() => {
    const knownTabs = knownTabsRef;
    return () => {
      clearActiveEditorForLeaf(leafId);
      for (const id of knownTabs.current) {
        if (!findTabAnywhere(id)) {
          releaseModel(id);
          disposeTabExecution(id);
        }
      }
    };
  }, [leafId]);

  const onMount: OnMount = (editor) => {
    editorRef.current = editor;
    setActiveEditor(editor, leafId);

    editor.onDidDispose(() => {
      if (editorRef.current === editor) editorRef.current = null;
      clearActiveEditorForLeaf(leafId);
    });

    editor.onDidFocusEditorWidget(() => {
      setActiveEditor(editor, leafId);
      focusLeaf(leafId);
    });

    // Ctrl/Cmd+Enter and F5 are handled at the window level by
    // QueryEditorView (which reads panesState.focusedLeafId so the
    // run follows focus). Defaults are unbound globally in monaco/setup
    // so those keystrokes bubble out of Monaco untouched.

    syncActiveModel();
  };

  const monacoTheme = resolveMonacoTheme(theme);

  // Clicking anywhere in the leaf marks it focused even before Monaco
  // takes focus — useful when the user clicks the results pane and
  // expects subsequent Run/keybinds to target this leaf.
  function onMouseDownCapture() {
    focusLeaf(leafId);
  }

  function hasTabPayload(e: React.DragEvent): boolean {
    return e.dataTransfer.types.includes(TAB_DRAG_MIME);
  }

  // dragenter / dragleave bubble from every internal child. A depth
  // counter is the standard way to know when the cursor has actually
  // exited the leaf rather than just crossed an internal sibling
  // boundary. Without it the overlay flickers off every time the
  // cursor moves between the tab strip / toolbar / editor / results.
  function onDragEnter(e: React.DragEvent) {
    if (!hasTabPayload(e)) return;
    dragDepthRef.current++;
    if (dragDepthRef.current === 1) setDropActive(true);
  }

  function onDragLeave(e: React.DragEvent) {
    if (!hasTabPayload(e)) return;
    dragDepthRef.current--;
    if (dragDepthRef.current <= 0) {
      dragDepthRef.current = 0;
      setDropActive(false);
      setDropZone(null);
    }
  }

  function onDragOver(e: React.DragEvent) {
    if (!hasTabPayload(e)) return;
    // preventDefault marks this element as a valid drop target; without
    // it the `drop` event won't fire at all even though the cursor is
    // visibly over the leaf.
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    // Compute zone against the editor+results region only — we don't
    // want the tab strip / toolbar to register as part of the body
    // when the user is hovering near the top of the leaf.
    const rect = bodyRef.current?.getBoundingClientRect();
    if (!rect) return;
    const next = zoneForCursor(rect, e.clientX, e.clientY);
    setDropZone(next);
  }

  function onDrop(e: React.DragEvent) {
    if (!hasTabPayload(e)) return;
    e.preventDefault();
    const payload = parseTabDragData(e.dataTransfer);
    const zone = dropZone;
    dragDepthRef.current = 0;
    setDropActive(false);
    setDropZone(null);
    if (!payload || zone === null) return;
    const crossWindow = isCrossWindowDrop(payload);
    // Cross-window receive is refused while the source tab is running
    // — the streaming request lives in the source renderer and a
    // cross-window move would orphan it. In-window drops ignore this
    // (the exec state stays attached to the same tabId in the same
    // panesState).
    if (crossWindow && payload.isRunning) return;
    if (zone === 'center') {
      if (crossWindow) {
        importTabIntoLeaf(leafId, payload.tab, Number.MAX_SAFE_INTEGER);
        notifySourceToRemove(payload);
      } else {
        // Append to this leaf's tab list. moveTab no-ops cleanly when
        // source === target at the same effective index.
        moveTab(payload.tabId, leafId, Number.MAX_SAFE_INTEGER);
      }
      return;
    }
    if (crossWindow) {
      importTabAsSplit(leafId, payload.tab, zone);
      notifySourceToRemove(payload);
    } else {
      splitLeaf(leafId, payload.tabId, zone);
    }
  }

  return (
    <div
      onMouseDownCapture={onMouseDownCapture}
      onDragEnter={onDragEnter}
      onDragLeave={onDragLeave}
      onDragOver={onDragOver}
      onDrop={onDrop}
      className="relative flex h-full flex-col overflow-hidden"
    >
      <TabStrip leafId={leafId} />
      {activeTabId !== null ? (
        <div ref={bodyRef} className="relative flex flex-1 flex-col overflow-hidden">
        <ResizablePanelGroup
          orientation="vertical"
          className="flex-1"
          onLayoutChanged={onPanelLayoutChanged}
        >
          <ResizablePanel
            panelRef={editorPanelRef}
            id={`editor-${leafId}`}
            // Compute initial size from the active tab so the first
            // paint matches the saved layout. The effect above handles
            // tab switches; this only matters for the very first render.
            defaultSize={`${tabs.find((t) => t.id === activeTabId)?.editorSize ?? 65}%`}
            minSize="20%"
            className="flex flex-col overflow-hidden"
          >
            {/* Extra `overflow-hidden` shell around Monaco. During a
                resize Monaco's `automaticLayout` lags one frame behind
                the panel's new size — without this clip, that mid-
                resize state produces a transient scrollbar that
                visibly wraps around the editor. The shell hides it
                until layout catches up. */}
            <div className="relative min-h-0 flex-1 overflow-hidden">
            <Editor
              onMount={onMount}
              theme={monacoTheme}
              defaultLanguage="sql"
              defaultValue={tabs.find((t) => t.id === activeTabId)?.sql ?? ''}
              // The @monaco-editor/react wrapper disposes the editor's
              // currently-attached model on unmount by default. Our
              // models live in a process-wide registry and survive
              // cross-pane drags, so we must opt OUT of that disposal —
              // otherwise collapsing a source leaf takes the moved
              // tab's model down with it and the destination leaf
              // re-acquires a disposed handle.
              keepCurrentModel
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
            </div>
          </ResizablePanel>
          <ResizableHandle />
          <ResizablePanel
            id={`results-${leafId}`}
            // No `defaultSize`. With both panels specifying default
            // sizes, any non-100 sum (e.g. editor=67.5% + results=35%
            // = 102.5%) gets normalised by the lib, which shaves a bit
            // off the editor each reload — and since we persist the
            // post-normalisation value, the drift compounds. Leaving
            // results sizeless lets the lib auto-fill the remainder
            // exactly, so there's nothing to renormalise.
            minSize="10%"
            className="flex flex-col overflow-hidden"
          >
            <ResultsPane leafId={leafId} />
          </ResizablePanel>
        </ResizablePanelGroup>
        </div>
      ) : (
        <div ref={bodyRef} className="flex-1 overflow-hidden" />
      )}
      <PaneSplitOverlay active={dropActive} zone={dropZone} />
    </div>
  );
}

// Cheap walk; preferred over importing `getAllTabs` and building a set
// just to answer a yes/no on a single id during model GC.
function findTabAnywhere(tabId: string): boolean {
  let found = false;
  const visit = (n: typeof panesState.root) => {
    if (found) return;
    if (n.kind === 'leaf') {
      if (n.tabs.some((t) => t.id === tabId)) found = true;
    } else {
      visit(n.children[0]);
      visit(n.children[1]);
    }
  };
  visit(panesState.root);
  return found;
}
