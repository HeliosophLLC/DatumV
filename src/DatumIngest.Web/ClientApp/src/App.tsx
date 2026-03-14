import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { refreshHealth } from './state/health';
import { refreshSettings } from './state/settings';
import { conversationState } from './state/conversation';
import { navState } from './state/nav';
import { isTornOutWindow } from './state/tabs';
import { WindowChrome } from '@/components/window/WindowChrome';
import { HomePage } from '@/components/home/HomePage';
import { ConversationView } from '@/components/chat/ConversationView';
import { QueryEditorView } from '@/components/query/QueryEditorView';
import { SettingsView } from '@/components/settings/SettingsView';
import {
  ResizableHandle,
  ResizablePanel,
  ResizablePanelGroup,
} from '@/components/ui/resizable';

// Default split between the workspace (left) and the chat dock (right).
// Chat is always present in the main window — the dock no longer toggles
// it. Users resize via the drag handle when they want more workspace
// room. The workspace renders the tab editor (with the pinned Models tab
// as the first tab) when view = 'query', or SettingsView when the user
// has Settings open from the dock.
const DEFAULT_WORKSPACE_SIZE = '65%';
const DEFAULT_CHAT_SIZE = '35%';
const PANEL_MIN_SIZE = '20%';

export default function App() {
  const { messages, status } = useSnapshot(conversationState);
  const { view } = useSnapshot(navState);

  useEffect(() => {
    refreshHealth();
    refreshSettings();
  }, []);

  // Torn-out tab windows skip the main shell entirely: no AppDock, no
  // chat panel, no view switcher — just the query editor for the tab
  // that was torn out. The split / DnD / Monaco machinery inside
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

  const showConversation = messages.length > 0 || status !== 'idle';

  return (
    <WindowChrome>
      <ResizablePanelGroup orientation="horizontal" className="flex-1">
        <ResizablePanel
          id="workspace"
          defaultSize={DEFAULT_WORKSPACE_SIZE}
          minSize={PANEL_MIN_SIZE}
          className="flex flex-col overflow-hidden"
        >
          {view === 'query' && <QueryEditorView />}
          {view === 'settings' && <SettingsView />}
        </ResizablePanel>
        <ResizableHandle />
        <ResizablePanel
          id="chat"
          defaultSize={DEFAULT_CHAT_SIZE}
          minSize={PANEL_MIN_SIZE}
          className="flex flex-col overflow-hidden"
        >
          {showConversation ? <ConversationView /> : <HomePage />}
        </ResizablePanel>
      </ResizablePanelGroup>
    </WindowChrome>
  );
}
