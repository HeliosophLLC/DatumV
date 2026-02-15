import { useEffect, useState } from 'react';
import { useSnapshot } from 'valtio';
import { refreshHealth } from './state/health';
import { refreshSettings, settingsState } from './state/settings';
import { conversationState } from './state/conversation';
import { navState, type ActiveView } from './state/nav';
import { WindowChrome } from '@/components/window/WindowChrome';
import { SideNav } from '@/components/nav/SideNav';
import { HomePage } from '@/components/home/HomePage';
import { ConversationView } from '@/components/chat/ConversationView';
import { ModelsView } from '@/components/models/ModelsView';
import { QueryEditorView } from '@/components/query/QueryEditorView';
import { SettingsView } from '@/components/settings/SettingsView';
import { cn } from '@/lib/utils';

type SecondaryView = Exclude<ActiveView, 'chat'>;

export default function App() {
  const { messages, status } = useSnapshot(conversationState);
  const { view } = useSnapshot(navState);
  const { animations } = useSnapshot(settingsState);

  useEffect(() => {
    refreshHealth();
    refreshSettings();
  }, []);

  // Chat-vs-home is internal to the chat panel; the panel always renders
  // regardless of `view` and is animated between full-width and docked.
  const showConversation = messages.length > 0 || status !== 'idle';

  // Track the most recently active non-chat view so it remains mounted
  // while the chat panel slides back to full width. Without this, the
  // secondary content unmounts at click time and only chat animates,
  // which looks like a flash rather than a slide.
  const [secondaryView, setSecondaryView] = useState<SecondaryView | null>(
    view === 'chat' ? null : view,
  );
  useEffect(() => {
    if (view !== 'chat') setSecondaryView(view);
  }, [view]);

  return (
    <WindowChrome>
      <SideNav />
      <main className="relative flex flex-1 overflow-hidden">
        <div className="min-w-0 flex-1 overflow-hidden">
          {secondaryView === 'query' && <QueryEditorView />}
          {secondaryView === 'models' && <ModelsView />}
          {secondaryView === 'settings' && <SettingsView />}
        </div>
        <div
          className={cn(
            'flex shrink-0 flex-col overflow-hidden border-l',
            animations && 'transition-[width] duration-300 ease-in-out',
            view === 'chat' ? 'w-full' : 'w-96',
          )}
        >
          {showConversation ? <ConversationView /> : <HomePage />}
        </div>
      </main>
    </WindowChrome>
  );
}
