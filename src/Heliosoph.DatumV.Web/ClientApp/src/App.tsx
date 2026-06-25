import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { refreshHealth } from './state/health';
import { refreshSettings } from './state/settings';
import { maybePromptGpuInstall, maybePromptGpuWrongBuild } from './state/gpu';
import { hydrateDockFromSettings, navState } from './state/nav';
import { subscribeCatalogExplorerToHub } from './state/catalogExplorer';
import { subscribeRoutinesToHub } from './state/functionCatalog';
import { hydrateTabsFromCatalog, isTornOutWindow } from './state/tabs';
import { WindowChrome } from '@/components/window/WindowChrome';
import { AppDock } from '@/components/nav/AppDock';
import { QueryEditorView } from '@/components/query/QueryEditorView';
import { SidePanelHost } from '@/components/panels/SidePanelHost';
import {
  ResizableHandle,
  ResizablePanel,
  ResizablePanelGroup,
} from '@/components/ui/resizable';

// Layout:
//
//   ┌─────────────────────────────────────────────────────────────┐
//   │ TitleBar                                                    │
//   ├──┬──────────────────────────────────────────────────────┬──┤
//   │L │ [left panel?] | workspace | [right panel?]           │R │
//   │  │  ResizablePanelGroup w/ at most 3 panels             │  │
//   └──┴──────────────────────────────────────────────────────┴──┘
//
// The workspace always renders the query editor; Settings, Models,
// and Documentation are pinned tabs in the editor's tab strip rather
// than alternative workspace views.
//
// The ResizablePanelGroup is keyed on the open-panel signature so
// react-resizable-panels recomputes sizes cleanly when a panel opens
// or closes. autoSaveId persists each combination's split independently
// (closing then reopening Chat restores its prior width).
const SIDE_PANEL_DEFAULT_SIZE = '25%';
const WORKSPACE_MIN_SIZE = '30%';
const SIDE_PANEL_MIN_SIZE = '15%';

export default function App() {
  const { openLeft, openRight } = useSnapshot(navState);

  useEffect(() => {
    refreshHealth();
    void refreshSettings().then(() => hydrateDockFromSettings());
    void hydrateTabsFromCatalog();
    subscribeCatalogExplorerToHub();
    subscribeRoutinesToHub();
    // First-launch GPU prompts. Both functions internally gate on every
    // condition (variant, driver, CC, install state, dismissal) and
    // no-op if any fails — and their conditions are mutually exclusive
    // by construction, so at most one will ever fire on a given launch.
    // Skipped in tear-out windows because they never reach this branch.
    void maybePromptGpuInstall();
    void maybePromptGpuWrongBuild();
  }, []);

  // Torn-out tab windows skip the main shell entirely: no docks, no
  // panels, no view switcher — just the query editor for the tab that
  // was torn out. The split / DnD / Monaco machinery inside
  // QueryEditorView is identical to the main window's, so dragging
  // tabs back into the main works seamlessly.
  if (isTornOutWindow) {
    return (
      <WindowChrome>
        <div className="flex flex-1 flex-col overflow-hidden">
          <QueryEditorView />
        </div>
      </WindowChrome>
    );
  }

  // Group key encodes which sides have panels open so the resize
  // library remounts cleanly when a panel opens or closes (rather
  // than trying to reconcile a 3-panel layout into a 2-panel one,
  // which produces visible flicker as it re-clamps sizes).
  const groupKey = `dock:${openLeft ?? '_'}|${openRight ?? '_'}`;

  return (
    <WindowChrome>
      <AppDock side="left" />
      <ResizablePanelGroup
        key={groupKey}
        orientation="horizontal"
        className="flex-1"
      >
        {openLeft && (
          <>
            <ResizablePanel
              id="left-panel"
              defaultSize={SIDE_PANEL_DEFAULT_SIZE}
              minSize={SIDE_PANEL_MIN_SIZE}
              className="flex flex-col overflow-hidden"
            >
              <SidePanelHost side="left" panelId={openLeft} />
            </ResizablePanel>
            <ResizableHandle />
          </>
        )}
        <ResizablePanel
          id="workspace"
          minSize={WORKSPACE_MIN_SIZE}
          className="flex flex-col overflow-hidden"
        >
          <QueryEditorView />
        </ResizablePanel>
        {openRight && (
          <>
            <ResizableHandle />
            <ResizablePanel
              id="right-panel"
              defaultSize={SIDE_PANEL_DEFAULT_SIZE}
              minSize={SIDE_PANEL_MIN_SIZE}
              className="flex flex-col overflow-hidden"
            >
              <SidePanelHost side="right" panelId={openRight} />
            </ResizablePanel>
          </>
        )}
      </ResizablePanelGroup>
      <AppDock side="right" />
    </WindowChrome>
  );
}
