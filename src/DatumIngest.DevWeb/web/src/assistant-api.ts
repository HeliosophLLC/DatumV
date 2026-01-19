// Typed client for the /api/assistant/* HTTP surface. Wraps fetch
// calls in domain verbs the panel uses directly — no SQL strings,
// no cell-shape parsing, no scope_identity heuristics. The
// service-layer atomicity lives on the server.
//
// Wire shape mirrors src/DatumIngest.DevWeb/Assistant/AssistantDtos.cs:
// keep the two in sync if a column lands on either side.

export interface ConversationDto {
  id: number;
  workspace: string;
  title: string;
  startedAt: string; // ISO 8601 timestamp
}

export interface MessageDto {
  id: number;
  turnIndex: number;
  role: string;
  content: string;
  uploadId: number | null;
  toolCallId: string | null;
  createdAt: string;
}

// Discriminated union over the NDJSON events the streaming-turn
// endpoint emits. `type` matches the JsonDerivedType discriminator
// values on the server.
export type TurnEvent =
  | { type: 'user_message_inserted'; message: MessageDto }
  | { type: 'chunk'; text: string }
  | { type: 'assistant_message_inserted'; message: MessageDto }
  | { type: 'complete'; elapsedMs: number }
  | { type: 'error'; message: string; detail: string | null };

// ===== Conversations =====

export async function ensureConversation(
  workspace: string = 'default',
): Promise<ConversationDto> {
  const url = `/api/assistant/conversations?workspace=${encodeURIComponent(workspace)}`;
  const res = await fetch(url, { method: 'POST' });
  if (!res.ok) throw new Error(await readError(res));
  return res.json() as Promise<ConversationDto>;
}

// ===== Messages =====

export async function fetchHistory(
  conversationId: number,
): Promise<MessageDto[]> {
  const res = await fetch(
    `/api/assistant/conversations/${conversationId}/messages`,
  );
  if (!res.ok) throw new Error(await readError(res));
  return res.json() as Promise<MessageDto[]>;
}

// ===== Turn (streaming) =====

export async function streamTurn(
  conversationId: number,
  text: string,
  file: File | null,
  modelName: string,
  onEvent: (ev: TurnEvent) => void,
  signal: AbortSignal,
): Promise<void> {
  const fd = new FormData();
  fd.append('text', text);
  if (file) fd.append('file', file, file.name);

  const url =
    `/api/assistant/conversations/${conversationId}/turn` +
    `?model=${encodeURIComponent(modelName)}`;
  const res = await fetch(url, { method: 'POST', body: fd, signal });
  if (!res.ok) throw new Error(await readError(res));
  if (!res.body) return;

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buf = '';
  // eslint-disable-next-line no-constant-condition
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buf += decoder.decode(value, { stream: true });
    const lines = buf.split('\n');
    buf = lines.pop() ?? '';
    for (const line of lines) {
      if (!line.trim()) continue;
      try {
        onEvent(JSON.parse(line) as TurnEvent);
      } catch {
        // ignore malformed lines — server should not emit them,
        // but keep the loop alive on transport hiccups.
      }
    }
  }
  if (buf.trim()) {
    try { onEvent(JSON.parse(buf) as TurnEvent); }
    catch { /* trailing junk */ }
  }
}

async function readError(res: Response): Promise<string> {
  const text = await res.text().catch(() => '');
  if (!text) return `HTTP ${res.status}`;
  // ASP.NET ProblemDetails JSON, when validation fires.
  try {
    const parsed = JSON.parse(text) as { title?: string; detail?: string };
    return parsed.title || parsed.detail || text;
  } catch {
    return text;
  }
}
