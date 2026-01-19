// Valtio state for the AI assistant panel. The catalog tables are
// still the source of truth — the panel mirror is rebuilt via
// `fetchHistory` after every turn. Local state holds:
//   - the active conversation
//   - the message log (in-memory copy of `messages` rows)
//   - in-flight assistant-turn state: chunk accumulator, abort
//     controller, error message
//   - the user's input draft + selected model
//   - panel visibility flag

import { proxy, ref } from 'valtio';
import type { ConversationDto, MessageDto } from './assistant-api.js';

interface AssistantState {
  open: boolean;
  conversation: ConversationDto | null;
  messages: MessageDto[];
  loading: boolean;
  streaming: boolean;
  // Accumulator for the in-flight assistant turn — every `chunk`
  // event from the streaming endpoint appends to this. The final
  // `assistant_message_inserted` event replaces the streaming
  // bubble with the persisted MessageDto.
  streamingText: string;
  abortController: AbortController | null;
  error: string | null;
  inputDraft: string;
  modelName: string;
}

export const assistantState = proxy<AssistantState>({
  open: false,
  conversation: null,
  messages: [],
  loading: false,
  streaming: false,
  streamingText: '',
  abortController: null,
  error: null,
  inputDraft: '',
  modelName: 'llama31_8b',
});

export function setMessages(msgs: MessageDto[]): void {
  assistantState.messages = msgs;
}

export function setStreamingAbortController(c: AbortController | null): void {
  assistantState.abortController = c === null ? null : ref(c);
}
