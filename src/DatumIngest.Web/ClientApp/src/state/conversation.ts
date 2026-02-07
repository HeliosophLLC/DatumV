import { proxy } from 'valtio';
import {
  acquireStreamHub,
  onChatComplete,
  onChatError,
  onChatToken,
  onConnectionClosed,
} from '@/api/hub';

// Mirrors the server's reactive loop. `messages` is the persisted turns
// list; `streaming` accumulates the in-flight assistant turn token by
// token. When the server emits OnComplete, we move `streaming` into
// `messages` and clear it. `status` drives view affordances (input
// disabled while awaiting / streaming, enabled with banner on error).
//
// No DB rehydration on launch — see project_message_graph_design memory.
// Fresh each session; persistence happens server-side for inspectability
// and future rehydration.
//
// Event wiring lives at module scope: the hub's dispatcher fans events out
// to anyone who subscribed. We register at import time so subscriptions
// are in place before the first sendMessage call, regardless of view
// mount order.

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
    const hub = await acquireStreamHub();
    await hub.cancelMessage();
  } catch {
    // Best-effort; the server may have already finished. The OnComplete
    // path will reset status normally.
  }
}
