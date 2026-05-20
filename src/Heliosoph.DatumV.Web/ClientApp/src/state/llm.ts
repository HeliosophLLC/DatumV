import { proxy } from 'valtio';
import { api } from '@/api';
import type { InstalledLlm } from '@/api/generated/openapi-client';
import {
  onModelInstalled,
  onModelDownloadFailed,
} from '@/api/hub';

// Tracks which LLMs the chat surface can actually use right now. Driven
// by GET /api/llm/available — the server applies the same filters the
// driver does (category=llm + files on disk + estimated VRAM). The
// settings picker and the chat "no LLM" empty state both read this proxy.
//
// Auto-refresh: subscribes to model-install hub events so the picker
// lights up the moment an install finishes; a failed install just leaves
// the previous value in place (the user will see the error in the Models
// view).

interface LlmState {
  available: InstalledLlm[];
  // True while the initial fetch is in flight, so the picker shows a
  // skeleton rather than the "no LLMs installed" empty state for the
  // ~50ms before the first response.
  loading: boolean;
  error: string | null;
}

export const llmState = proxy<LlmState>({
  available: [],
  loading: false,
  error: null,
});

export async function refreshAvailableLlms(): Promise<void> {
  llmState.loading = true;
  try {
    const list = await api.llm.available();
    llmState.available = list;
    llmState.error = null;
  } catch (err) {
    llmState.error = err instanceof Error ? err.message : String(err);
  } finally {
    llmState.loading = false;
  }
}

// Fire-and-forget initial fetch. Errors land on llmState.error and the
// settings picker degrades to its "couldn't load" state.
void refreshAvailableLlms();

// Re-fetch after any model install completes — the newly-installed model
// might be an LLM the user is about to pick. Failures don't trigger a
// refresh (state is unchanged from before the install attempt).
onModelInstalled(() => {
  void refreshAvailableLlms();
});
onModelDownloadFailed(() => {
  // No state change needed, but a refresh is cheap and protects against
  // partial-install rollbacks the server might do in future.
  void refreshAvailableLlms();
});
