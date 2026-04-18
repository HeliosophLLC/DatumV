import { proxy } from 'valtio';
import {
  acquireStreamHub,
  onChatComplete,
  onChatError,
  onChatToken,
  onConnectionClosed,
} from '@/api/hub';
import { refreshAvailableLlms } from './llm';

// Server-side sentinel (DatumIngest.Web.Llm.NoLlmInstalledException.Marker).
// When an OnError message starts with this token we suppress the generic
// "something went wrong" banner — the chat surface renders its own empty
// state via llmState.available being empty.
const NO_LLM_MARKER = 'NoLlmInstalled';

// Mirrors the server's reactive loop. `messages` is the persisted turns
// list; `streaming` accumulates the in-flight assistant turn token by
// token. When the server emits OnComplete, we move `streaming` into
// `messages` and clear it. `status` drives view affordances (input
// disabled while awaiting / streaming, enabled with banner on error).
//
// Conversations: on boot we fetch the default conversation id from the
// server (lazy-creating one if needed) and hydrate `messages` from its
// persisted history. `reloadConversation` re-fetches both the server's
// in-memory accumulator (cleared) and our `messages` list — pair with a
// hand-edit to the messages table to test reload semantics.

export type Role = 'user' | 'assistant';

export interface ChatMessage {
  id: string;
  role: Role;
  content: string;
}

export type ChatStatus = 'idle' | 'awaiting' | 'streaming' | 'error';

interface ConversationState {
  conversationId: number | null;
  messages: ChatMessage[];
  streaming: string;
  status: ChatStatus;
  error: string | null;
}

export const conversationState = proxy<ConversationState>({
  conversationId: null,
  messages: [],
  streaming: '',
  status: 'idle',
  error: null,
});

interface MessageDto {
  id: number;
  conversationId: number;
  kind: string;
  role: string;
  content: string;
  model: string | null;
  inputTokens: number | null;
  outputTokens: number | null;
  createdAt: string;
}

interface ConversationDto {
  id: number;
  title: string | null;
  model: string | null;
  createdAt: string;
  updatedAt: string;
}

// Filters server messages down to the shape the UI renders. Non-turn rows
// (checkpoints, hidden) are skipped here; future commits may render
// checkpoint dividers, at which point this widens.
function toChatMessages(messages: MessageDto[]): ChatMessage[] {
  return messages
    .filter((m) => m.kind !== 'hidden' && (m.role === 'user' || m.role === 'assistant'))
    .map((m) => ({
      id: String(m.id),
      role: m.role as Role,
      content: m.content,
    }));
}

async function fetchDefaultConversation(): Promise<ConversationDto> {
  const res = await fetch('/api/conversations/default');
  if (!res.ok) throw new Error(`GET /api/conversations/default → ${res.status}`);
  return res.json() as Promise<ConversationDto>;
}

async function fetchMessages(conversationId: number): Promise<MessageDto[]> {
  const res = await fetch(`/api/conversations/${conversationId}/messages`);
  if (!res.ok) throw new Error(`GET messages for ${conversationId} → ${res.status}`);
  return res.json() as Promise<MessageDto[]>;
}

// Fire-and-forget init: resolve the default conversation id and hydrate
// its history. Errors are swallowed onto `status: 'error'` so the chat
// surface can still render a banner instead of a blank screen.
let initPromise: Promise<void> | null = null;
function ensureInitialized(): Promise<void> {
  if (initPromise) return initPromise;
  initPromise = (async () => {
    try {
      const conv = await fetchDefaultConversation();
      conversationState.conversationId = conv.id;
      const messages = await fetchMessages(conv.id);
      conversationState.messages = toChatMessages(messages);
    } catch (err) {
      conversationState.status = 'error';
      conversationState.error =
        err instanceof Error ? err.message : String(err);
    }
  })();
  return initPromise;
}
void ensureInitialized();

onChatToken((content) => {
  conversationState.streaming += content;
  if (conversationState.status === 'awaiting') {
    conversationState.status = 'streaming';
  }
});

onChatComplete(() => {
  // Includes the cancellation path — server emits OnComplete after
  // persisting whatever partial response it captured.
  finalizeStreamingTurn();
});

onChatError((message) => {
  if (message.includes(NO_LLM_MARKER)) {
    // Don't show the generic error banner — the chat surface will render
    // a dedicated empty state once llmState refreshes (which the server
    // confirms: no LLM is loadable right now). Drop the optimistic user
    // turn that was pushed before the send too, so the user's input
    // doesn't linger as if it were accepted.
    if (conversationState.messages.length > 0) {
      const last = conversationState.messages[conversationState.messages.length - 1];
      if (last.role === 'user') conversationState.messages.pop();
    }
    conversationState.status = 'idle';
    conversationState.error = null;
    conversationState.streaming = '';
    void refreshAvailableLlms();
    return;
  }
  conversationState.status = 'error';
  conversationState.error = message;
  conversationState.streaming = '';
});

// If the WS dies while we're mid-stream, neither OnComplete nor OnError
// will arrive — we manufacture an error so the input doesn't stay stuck.
onConnectionClosed((err) => {
  if (
    conversationState.status === 'awaiting' ||
    conversationState.status === 'streaming'
  ) {
    // Salvage whatever we received before the drop into messages so
    // the partial response stays visible; the server already persisted
    // its side via the agent's finally block.
    finalizeStreamingTurn();
    conversationState.status = 'error';
    conversationState.error = err?.message ?? 'Connection lost';
    conversationState.streaming = '';
  }
});

function finalizeStreamingTurn() {
  if (conversationState.streaming.length > 0) {
    conversationState.messages.push({
      id: crypto.randomUUID(),
      role: 'assistant',
      content: conversationState.streaming,
    });
  }
  conversationState.streaming = '';
  conversationState.status = 'idle';
  conversationState.error = null;
}

export async function sendMessage(content: string): Promise<void> {
  const trimmed = content.trim();
  if (!trimmed) return;
  // Block only while a turn is mid-flight. From 'error' we DO want to
  // accept a new message — that's how the user recovers.
  if (conversationState.status === 'awaiting' || conversationState.status === 'streaming') {
    return;
  }

  await ensureInitialized();
  const conversationId = conversationState.conversationId;
  if (conversationId === null) return;

  conversationState.messages.push({
    id: crypto.randomUUID(),
    role: 'user',
    content: trimmed,
  });
  conversationState.streaming = '';
  conversationState.status = 'awaiting';
  conversationState.error = null;

  try {
    const hub = await acquireStreamHub();
    await hub.sendMessage(conversationId, trimmed);
  } catch (err) {
    // If the server-side method threw or the connection failed during
    // invoke, surface as error state. If the connection-closed handler
    // already fired, this overrides with the more specific error which
    // is fine.
    conversationState.status = 'error';
    conversationState.error = err instanceof Error ? err.message : String(err);
    conversationState.streaming = '';
  }
}

export async function cancelMessage(): Promise<void> {
  if (conversationState.status !== 'awaiting' && conversationState.status !== 'streaming') {
    return;
  }
  const conversationId = conversationState.conversationId;
  if (conversationId === null) return;
  try {
    const hub = await acquireStreamHub();
    await hub.cancelMessage(conversationId);
  } catch {
    // Best-effort; the server may have already finished. The OnComplete
    // path will reset status normally.
  }
}

// Drops the server's accumulator for the active conversation and replaces
// our local message list with whatever is in the database now. Use after
// hand-editing the messages table via the SQL panel.
export async function reloadConversation(): Promise<void> {
  const conversationId = conversationState.conversationId;
  if (conversationId === null) return;
  try {
    const hub = await acquireStreamHub();
    await hub.reloadConversation(conversationId);
    const messages = await fetchMessages(conversationId);
    conversationState.messages = toChatMessages(messages);
    conversationState.streaming = '';
    if (conversationState.status === 'error') {
      conversationState.status = 'idle';
      conversationState.error = null;
    }
  } catch (err) {
    conversationState.status = 'error';
    conversationState.error = err instanceof Error ? err.message : String(err);
  }
}
