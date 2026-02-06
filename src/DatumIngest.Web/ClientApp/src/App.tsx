import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { refreshHealth } from './state/health';
import { refreshSettings } from './state/settings';
import { conversationState } from './state/conversation';
import { navState } from './state/nav';
import { TitleBar } from '@/components/titlebar/TitleBar';
import { ResizeFrame } from '@/components/window/ResizeFrame';
import { SideNav } from '@/components/nav/SideNav';
import { HomePage } from '@/components/home/HomePage';
import { ConversationView } from '@/components/chat/ConversationView';
import { ModelsView } from '@/components/models/ModelsView';
import { SettingsView } from '@/components/settings/SettingsView';

export default function App() {
  const { messages, status } = useSnapshot(conversationState);
  const { view } = useSnapshot(navState);

  useEffect(() => {
    refreshHealth();
    refreshSettings();
  }, []);

  // Chat-vs-home is internal to the chat view. The nav-level routing picks
  // between Chat / Models / Settings; Chat then sub-renders HomePage on a
  // cold session and ConversationView once any turn exists.
  const showConversation = messages.length > 0 || status !== 'idle';

  return (
    <div className="bg-background text-foreground flex h-screen flex-col border">
      <TitleBar />
      <ResizeFrame />
      <div className="flex flex-1 overflow-hidden">
        <SideNav />
        <main className="flex flex-1 flex-col overflow-hidden">
          {view === 'chat' && (showConversation ? <ConversationView /> : <HomePage />)}
          {view === 'models' && <ModelsView />}
          {view === 'settings' && <SettingsView />}
        </main>
      </div>
    </div>
  );
}
