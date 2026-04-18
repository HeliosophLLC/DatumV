import { useSnapshot } from 'valtio';
import { conversationState } from '@/state/conversation';
import { llmState } from '@/state/llm';
import { ChatToolbar } from '@/components/chat/ChatToolbar';
import { ConversationView } from '@/components/chat/ConversationView';
import { NoLlmInstalledCard } from '@/components/chat/NoLlmInstalledCard';
import { HomePage } from '@/components/home/HomePage';

// Side-panel wrapper around the chat surface. Toolbar (history / new /
// reload) is always above the body; the body switches between:
//   * No LLM installed → install CTA (the input would be unusable
//     anyway, but the toolbar still lets the user browse history).
//   * Idle and empty    → HomePage greeting + prompt.
//   * Anything else     → streaming conversation view.
export function ChatPanel() {
  const { messages, status } = useSnapshot(conversationState);
  const { available, loading } = useSnapshot(llmState);
  const noLlm = !loading && available.length === 0;
  const showConversation = messages.length > 0 || status !== 'idle';
  return (
    <div className="flex h-full flex-col">
      <ChatToolbar />
      <div className="flex-1 overflow-hidden">
        {noLlm ? (
          <NoLlmInstalledCard />
        ) : showConversation ? (
          <ConversationView />
        ) : (
          <HomePage />
        )}
      </div>
    </div>
  );
}
