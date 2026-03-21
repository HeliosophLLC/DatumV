import { useSnapshot } from 'valtio';
import { conversationState } from '@/state/conversation';
import { ConversationView } from '@/components/chat/ConversationView';
import { HomePage } from '@/components/home/HomePage';

// Side-panel wrapper around the chat surface. Same content as the old
// always-on right panel — empty state when there's no conversation yet,
// switches to the streaming conversation view once messages or status
// activity appears.
export function ChatPanel() {
  const { messages, status } = useSnapshot(conversationState);
  const showConversation = messages.length > 0 || status !== 'idle';
  return showConversation ? <ConversationView /> : <HomePage />;
}
