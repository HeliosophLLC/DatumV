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
  // 'turn' rows are user/assistant exchanges. 'checkpoint' rows are
  // compaction summaries — the UI renders a labeled divider with the
  // summary text underneath; the model sees them as a synthetic system
  // message during prompt assembly.
  kind: 'turn' | 'checkpoint';
  role: Role | 'system';
  content: string;
}

export type ChatStatus = 'idle' | 'awaiting' | 'streaming' | 'error';

interface ConversationState {
  conversationId: number | null;
  // Snapshot of every conversation surfaced in the history popover. Sorted
  // newest-first by updated_at; kept in sync with the server via
  // refreshConversations() — called on boot, after creating a new
  // conversation, and after each send (since updated_at is bumped).
  conversations: ConversationSummary[];
  messages: ChatMessage[];
  streaming: string;
  status: ChatStatus;
  error: string | null;
  // True while a compaction pass is running on the server. The toolbar
  // disables actions and shows a spinner during this window. Separate
  // from `status` because compaction doesn't block the user from reading
  // history — only from kicking off another mutation.
  compacting: boolean;
}

export interface ConversationSummary {
  id: number;
  title: string | null;
  updatedAt: string;
}

export const conversationState = proxy<ConversationState>({
  conversationId: null,
  conversations: [],
  messages: [],
  streaming: '',
  status: 'idle',
  error: null,
  compacting: false,
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

// Filters server messages down to the shape the UI renders. Hidden rows
// are skipped; checkpoint rows are surfaced so the UI can show a divider
// at the compaction point. Anything else (unknown roles / kinds from a
// future schema) is dropped silently.
function toChatMessages(messages: MessageDto[]): ChatMessage[] {
  const out: ChatMessage[] = [];
  for (const m of messages) {
    if (m.kind === 'hidden') continue;
    if (m.kind === 'checkpoint') {
      out.push({
        id: String(m.id),
        kind: 'checkpoint',
        role: 'system',
        content: m.content,
      });
      continue;
    }
    if (m.role !== 'user' && m.role !== 'assistant') continue;
    out.push({
      id: String(m.id),
      kind: 'turn',
      role: m.role,
      content: m.content,
    });
  }
  return out;
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

async function fetchConversations(): Promise<ConversationDto[]> {
  const res = await fetch('/api/conversations');
  if (!res.ok) throw new Error(`GET /api/conversations → ${res.status}`);
  return res.json() as Promise<ConversationDto[]>;
}

async function postConversation(): Promise<ConversationDto> {
  const res = await fetch('/api/conversations', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title: null, model: null }),
  });
  if (!res.ok) throw new Error(`POST /api/conversations → ${res.status}`);
  return res.json() as Promise<ConversationDto>;
}

function summarise(dto: ConversationDto): ConversationSummary {
  return { id: dto.id, title: dto.title, updatedAt: dto.updatedAt };
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
      const [messages, list] = await Promise.all([
        fetchMessages(conv.id),
        fetchConversations(),
      ]);
      conversationState.messages = toChatMessages(messages);
      conversationState.conversations = list.map(summarise);
    } catch (err) {
      conversationState.status = 'error';
      conversationState.error =
        err instanceof Error ? err.message : String(err);
    }
  })();
  return initPromise;
}
void ensureInitialized();

export async function refreshConversations(): Promise<void> {
  try {
    const list = await fetchConversations();
    conversationState.conversations = list.map(summarise);
  } catch {
    // Non-fatal: the popover will show the stale list until next refresh.
  }
}

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
  // Refresh the popover list so updated_at re-sorts. The first refresh
  // catches the just-updated row. The second one picks up the auto-
  // generated title, which is produced fire-and-forget by the agent
  // after OnComplete fires — a delay is the cheap alternative to
  // wiring a dedicated hub push for the title.
  void refreshConversations();
  window.setTimeout(() => void refreshConversations(), 4000);
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
      kind: 'turn',
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
    kind: 'turn',
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

// Switches the active conversation. The server-side accumulator for the
// new id is dropped before the fetch so the next send rebuilds from DB
// (cheap and uniform with reload semantics). No-op when the requested id
// is already active.
export async function switchConversation(id: number): Promise<void> {
  if (conversationState.conversationId === id) return;
  // Refuse the switch while mid-send to avoid leaving the streaming
  // bubble attached to the wrong conversation in the UI.
  if (conversationState.status === 'awaiting' || conversationState.status === 'streaming') {
    return;
  }
  conversationState.conversationId = id;
  conversationState.messages = [];
  conversationState.streaming = '';
  conversationState.status = 'idle';
  conversationState.error = null;
  try {
    const hub = await acquireStreamHub();
    await hub.reloadConversation(id);
    const messages = await fetchMessages(id);
    conversationState.messages = toChatMessages(messages);
  } catch (err) {
    conversationState.status = 'error';
    conversationState.error = err instanceof Error ? err.message : String(err);
  }
}

// Runs the server's compaction pass on the active conversation: a
// summary checkpoint row is inserted at the tail and the next send
// rebuilds the prompt around it. The UI shows the full history; the
// LLM only sees system + summary + post-checkpoint turns.
//
// `compacting` flips to true for the duration of the call so the
// toolbar can show a spinner. Returns the number of turns compacted —
// caller code doesn't currently consume the value but it's useful for
// future "compacted N messages" toasts.
export async function compactConversation(): Promise<number> {
  const conversationId = conversationState.conversationId;
  if (conversationId === null) return 0;
  if (conversationState.status === 'awaiting' || conversationState.status === 'streaming') {
    return 0;
  }
  conversationState.compacting = true;
  try {
    const hub = await acquireStreamHub();
    const count = await hub.compactConversation(conversationId);
    if (count > 0) {
      const messages = await fetchMessages(conversationId);
      conversationState.messages = toChatMessages(messages);
    }
    return count;
  } catch (err) {
    conversationState.status = 'error';
    conversationState.error = err instanceof Error ? err.message : String(err);
    return 0;
  } finally {
    conversationState.compacting = false;
  }
}

// Creates a fresh conversation on the server, switches to it, and
// refreshes the popover list so it shows up. The new conversation has
// no messages, so the surface flips back to HomePage.
export async function newConversation(): Promise<void> {
  if (conversationState.status === 'awaiting' || conversationState.status === 'streaming') {
    return;
  }
  try {
    const conv = await postConversation();
    conversationState.conversationId = conv.id;
    conversationState.messages = [];
    conversationState.streaming = '';
    conversationState.status = 'idle';
    conversationState.error = null;
    await refreshConversations();
  } catch (err) {
    conversationState.status = 'error';
    conversationState.error = err instanceof Error ? err.message : String(err);
  }
}
