import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { refreshHealth } from './state/health';
import { refreshSettings } from './state/settings';
import { conversationState } from './state/conversation';
import { navState } from './state/nav';
import { WindowChrome } from '@/components/window/WindowChrome';
import { SideNav } from '@/components/nav/SideNav';
import { HomePage } from '@/components/home/HomePage';
import { ConversationView } from '@/components/chat/ConversationView';
import { ModelsView } from '@/components/models/ModelsView';
import { QueryEditorView } from '@/components/query/QueryEditorView';
import { SettingsView } from '@/components/settings/SettingsView';
import {
  ResizableHandle,
  ResizablePanel,
  ResizablePanelGroup,
} from '@/components/ui/resizable';

export default function App() {
  const { messages, status } = useSnapshot(conversationState);
  const { view } = useSnapshot(navState);

  useEffect(() => {
    refreshHealth();
    refreshSettings();
  }, []);

  const showConversation = messages.length > 0 || status !== 'idle';
  const isChat = view === 'chat';

  return (
    <WindowChrome>
      <SideNav />
      {isChat ? (
        <main className="flex flex-1 flex-col overflow-hidden">
          {showConversation ? <ConversationView /> : <HomePage />}
        </main>
      ) : (
        <ResizablePanelGroup orientation="horizontal" className="flex-1">
          <ResizablePanel
            id="secondary"
            defaultSize="65%"
            minSize="20%"
            className="flex flex-col overflow-hidden"
          >
            {view === 'query' && <QueryEditorView />}
            {view === 'models' && <ModelsView />}
            {view === 'settings' && <SettingsView />}
          </ResizablePanel>
          <ResizableHandle />
          <ResizablePanel
            id="chat"
            defaultSize="35%"
            minSize="20%"
            className="flex flex-col overflow-hidden"
          >
            {showConversation ? <ConversationView /> : <HomePage />}
          </ResizablePanel>
        </ResizablePanelGroup>
      )}
    </WindowChrome>
  );
}
