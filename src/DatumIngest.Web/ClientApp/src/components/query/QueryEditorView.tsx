import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { panesState, findLeaf, type PaneNode } from '@/state/tabs';
import { runTab } from '@/state/execution';
import { runFunctionTab } from '@/state/functionForm';
import { resolveRunSql } from '@/state/activeEditor';
import {
  ResizableHandle,
  ResizablePanel,
  ResizablePanelGroup,
} from '@/components/ui/resizable';
import { LeafPaneView } from './LeafPaneView';

// Recursive renderer for the pane tree. Leaves become `LeafPaneView`s
// (full tab strip + editor + results); splits become two-child
// `ResizablePanelGroup`s. Each node uses its stable id as the React
// key so a structural change (split / collapse) doesn't blow away the
// surviving subtree's Monaco state.

export function QueryEditorView() {
  useSnapshot(panesState);

  // Run-the-focused-tab keybinds (Ctrl/Cmd+Enter, F5) bound at the
  // window level. The per-editor Monaco command bindings stay in place
  // and intercept first when the user is typing in an editor — this
  // handler is the fallback for non-editor focus (results pane, tab
  // strip, the empty pane around the splits). It reads
  // `focusedLeafId` to know which leaf to act on, which the rest of
  // the UI keeps current via click + Monaco-focus events.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const isRunKey =
        ((e.ctrlKey || e.metaKey) && e.key === 'Enter') || e.key === 'F5';
      if (!isRunKey) return;
      if (e.defaultPrevented) return; // a Monaco editor already handled it.
      const leafId = panesState.focusedLeafId;
      const leaf = findLeaf(panesState.root, leafId);
      if (!leaf || leaf.activeTabId === null) return;
      const tab = leaf.tabs.find((t) => t.id === leaf.activeTabId);
      if (!tab) return;
      e.preventDefault();
      if (tab.kind === 'function') {
        void runFunctionTab(leaf.activeTabId);
        return;
      }
      void runTab(leaf.activeTabId, resolveRunSql(tab.sql, leafId));
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, []);

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <PaneNodeView node={panesState.root} />
    </div>
  );
}

function PaneNodeView({ node }: { node: PaneNode }) {
  if (node.kind === 'leaf') {
    return <LeafPaneView leafId={node.id} />;
  }
  return (
    <ResizablePanelGroup orientation={node.orientation} className="h-full">
      <ResizablePanel
        id={`split-${node.id}-a`}
        defaultSize="50%"
        minSize="10%"
        className="flex flex-col overflow-hidden"
      >
        <PaneNodeView node={node.children[0]} />
      </ResizablePanel>
      <ResizableHandle />
      <ResizablePanel
        id={`split-${node.id}-b`}
        defaultSize="50%"
        minSize="10%"
        className="flex flex-col overflow-hidden"
      >
        <PaneNodeView node={node.children[1]} />
      </ResizablePanel>
    </ResizablePanelGroup>
  );
}
