import { proxy } from 'valtio';
import { acquireStreamHub, onConnectionClosed } from '@/api/hub';

// Mirrors the server's reactive loop. `messages` is the persisted turns
// list; `streaming` accumulates the in-flight assistant turn token by
// token. When the server emits OnComplete, we move `streaming` into
// `messages` and clear it. `status` drives view affordances (input
// disabled while awaiting / streaming, enabled with banner on error).
//
// No DB rehydration on launch — see project_message_graph_design memory.
// Fresh each session; persistence happens server-side for inspectability
// and future rehydration.

export type Role = 'user' | 'assistant';

export interface ChatMessage {
  id: string;
  role: Role;
  content: string;
}

export type ChatStatus = 'idle' | 'awaiting' | 'streaming' | 'error';

interface ConversationState {
  messages: ChatMessage[];
  streaming: string;
  status: ChatStatus;
  error: string | null;
}

export const conversationState = proxy<ConversationState>({
  messages: [],
  streaming: '',
  status: 'idle',
  error: null,
});

// One receiver object for the connection's lifetime. The hub layer wants
// the same reference across registrations; using a const object avoids
// any chance of double-registration.
const receiver = {
  async onPong(): Promise<void> {},
  async onToken(content: string): Promise<void> {
    conversationState.streaming += content;
    if (conversationState.status === 'awaiting') {
      conversationState.status = 'streaming';
    }
  },
  async onComplete(): Promise<void> {
    // Includes the cancellation path — server emits OnComplete after
    // persisting whatever partial response it captured.
    finalizeStreamingTurn();
  },
  async onError(message: string): Promise<void> {
    conversationState.status = 'error';
    conversationState.error = message;
    conversationState.streaming = '';
  },
};

// One-shot subscriber to connection-closed events from the hub layer.
// If the WS dies while we're mid-stream, neither OnComplete nor OnError
// will arrive — we manufacture an error so the input doesn't stay stuck.
let connectionCloseHandlerRegistered = false;

function ensureConnectionCloseHandler() {
  if (connectionCloseHandlerRegistered) return;
  connectionCloseHandlerRegistered = true;
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
}

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

  conversationState.messages.push({
    id: crypto.randomUUID(),
    role: 'user',
    content: trimmed,
  });
  conversationState.streaming = '';
  conversationState.status = 'awaiting';
  conversationState.error = null;

  try {
    ensureConnectionCloseHandler();
    const hub = await acquireStreamHub(receiver);
    await hub.sendMessage(trimmed);
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
  try {
    const hub = await acquireStreamHub(receiver);
    await hub.cancelMessage();
  } catch {
    // Best-effort; the server may have already finished. The OnComplete
    // path will reset status normally.
  }
}
