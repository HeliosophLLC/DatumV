import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { panesState, saveActiveTab, type PaneNode } from '@/state/tabs';
import { runCommand } from '@/commands/registry';
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

  // Ctrl/Cmd+Enter is owned by the application menu's "Run > Run Query"
  // accelerator (electron/main.ts → Menu.setApplicationMenu), which
  // dispatches via runCommand('query.run') even when Monaco has focus.
  // The remaining window-level keys are the ones that don't have a menu
  // entry yet: F5 (run) and Ctrl/Cmd+S (save) as a backstop alongside
  // the File menu's Ctrl+S accelerator so the keystroke never falls
  // through to the browser's "Save Page As" dialog.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const isRunKey = e.key === 'F5';
      const isSaveKey = (e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's';
      if (!isRunKey && !isSaveKey) return;
      if (e.defaultPrevented) return;
      if (isSaveKey) {
        e.preventDefault();
        void saveActiveTab();
        return;
      }
      e.preventDefault();
      runCommand('query.run');
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
