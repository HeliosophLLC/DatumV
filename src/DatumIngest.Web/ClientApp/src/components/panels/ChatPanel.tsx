import { useSnapshot } from 'valtio';
import { conversationState } from '@/state/conversation';
import { llmState } from '@/state/llm';
import { ConversationView } from '@/components/chat/ConversationView';
import { NoLlmInstalledCard } from '@/components/chat/NoLlmInstalledCard';
import { HomePage } from '@/components/home/HomePage';

// Side-panel wrapper around the chat surface. Three states:
//   * No LLM installed → install CTA (suppresses both HomePage and the
//     conversation view; the input would be unusable anyway).
//   * Idle and empty    → HomePage greeting + prompt.
//   * Anything else     → streaming conversation view.
export function ChatPanel() {
  const { messages, status } = useSnapshot(conversationState);
  const { available, loading } = useSnapshot(llmState);
  if (!loading && available.length === 0) {
    return <NoLlmInstalledCard />;
  }
  const showConversation = messages.length > 0 || status !== 'idle';
  return showConversation ? <ConversationView /> : <HomePage />;
}
