import { useSnapshot } from 'valtio';
import { useTranslation } from 'react-i18next';
import { llmState } from '@/state/llm';
import { closePanel, type DockSide } from '@/state/nav';
import { ChatToolbar } from '@/components/chat/ChatToolbar';
import { ConversationView } from '@/components/chat/ConversationView';
import { NoLlmInstalledCard } from '@/components/chat/NoLlmInstalledCard';
import { PanelHeader } from './PanelHeader';

// Side-panel wrapper around the chat surface. Toolbar (history / new /
// reload) is always above the body; the body switches between:
//   * No LLM installed → install CTA (the input would be unusable
//     anyway, but the toolbar still lets the user browse history).
//   * Anything else    → ConversationView (renders its own empty state
//     when there are no messages yet).
export function ChatPanel({ side }: { side: DockSide }) {
  const { t } = useTranslation('panels');
  const { available, loading } = useSnapshot(llmState);
  const noLlm = !loading && available.length === 0;
  return (
    <div className="flex h-full flex-col">
      <PanelHeader title={t('chat.title')} onClose={() => closePanel(side)} />
      <ChatToolbar />
      <div className="flex-1 overflow-hidden">
        {noLlm ? <NoLlmInstalledCard /> : <ConversationView />}
      </div>
    </div>
  );
}
