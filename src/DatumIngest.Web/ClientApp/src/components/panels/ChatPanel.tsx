import { useSnapshot } from 'valtio';
import { llmState } from '@/state/llm';
import { ChatToolbar } from '@/components/chat/ChatToolbar';
import { ConversationView } from '@/components/chat/ConversationView';
import { NoLlmInstalledCard } from '@/components/chat/NoLlmInstalledCard';

// Side-panel wrapper around the chat surface. Toolbar (history / new /
// reload) is always above the body; the body switches between:
//   * No LLM installed → install CTA (the input would be unusable
//     anyway, but the toolbar still lets the user browse history).
//   * Anything else    → ConversationView (renders its own empty state
//     when there are no messages yet).
export function ChatPanel() {
  const { available, loading } = useSnapshot(llmState);
  const noLlm = !loading && available.length === 0;
  return (
    <div className="flex h-full flex-col">
      <ChatToolbar />
      <div className="flex-1 overflow-hidden">
        {noLlm ? <NoLlmInstalledCard /> : <ConversationView />}
      </div>
    </div>
  );
}
