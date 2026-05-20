import { proxy } from 'valtio';
import { api } from '@/api';

// Per-tab static EXPLAIN state. Lives alongside `executionsState` in a
// separate side store because:
//   - EXPLAIN doesn't share the streaming-NDJSON lifecycle of `runTab`;
//     it's a one-shot fetch returning a single rendered string.
//   - The user can run / explain a tab in either order; whichever was
//     more recent wins the results pane (compared via timestamps in
//     ResultsPane).
//
// Status lifecycle (mirrors `executionsState`):
//   idle ──runExplain──► running ──ok──► done
//                                 └─err─► error
//
// We don't persist explain state across reloads — re-running the button
// regenerates it. The state map is keyed by tab id and is cleaned up on
// tab close via `disposeTabExplain` (mirrors `disposeTabExecution`).

export type ExplainStatus = 'idle' | 'running' | 'done' | 'error';

export interface TabExplain {
  status: ExplainStatus;
  // Rendered plan text from ExplainPlanNode.Render(). Null until the
  // first successful response lands.
  plan: string | null;
  // Server-supplied error message (parse / planning failure). Null on
  // success.
  error: string | null;
  // ms since epoch when the response landed (success OR error). Used
  // by ResultsPane to decide whether the explain or the run result is
  // most recent — the higher timestamp wins. Null while still running.
  completedAt: number | null;
}

export const explainState = proxy<{ byTabId: Record<string, TabExplain> }>({
  byTabId: {},
});

const abortByTab = new Map<string, AbortController>();

/**
 * Fetches a static EXPLAIN plan for `sql`, stores it on `tabId`.
 * Idempotent against an in-flight call for the same tab — the current
 * request is aborted before a new one starts, so the latest click wins.
 */
export async function runExplain(tabId: string, sql: string): Promise<void> {
  // Abort any in-flight explain for this tab — the user is asking for a
  // fresh plan; the prior one is no longer interesting.
  abortByTab.get(tabId)?.abort();
  const abort = new AbortController();
  abortByTab.set(tabId, abort);

  explainState.byTabId[tabId] = {
    status: 'running',
    plan: null,
    error: null,
    completedAt: null,
  };

  try {
    const response = await api.queryExplain.explain({ sql }, abort.signal);
    if (abort.signal.aborted) return;
    const slot = explainState.byTabId[tabId];
    if (!slot) return; // tab closed mid-flight
    if (response.error) {
      slot.status = 'error';
      slot.error = response.error;
    } else {
      slot.status = 'done';
      slot.plan = response.plan ?? '';
    }
    slot.completedAt = Date.now();
  } catch (err) {
    if (abort.signal.aborted) return;
    const slot = explainState.byTabId[tabId];
    if (!slot) return;
    slot.status = 'error';
    // The generated client throws via a plain string-message helper; we
    // surface whichever message is available without trying to reach into
    // a structured exception type.
    slot.error = err instanceof Error ? err.message : String(err);
    slot.completedAt = Date.now();
  } finally {
    if (abortByTab.get(tabId) === abort) {
      abortByTab.delete(tabId);
    }
  }
}

/**
 * Clears the explain slot for `tabId`. Use when closing a tab so the
 * map doesn't keep stale references. Mirrors `disposeTabExecution`.
 */
export function disposeTabExplain(tabId: string): void {
  abortByTab.get(tabId)?.abort();
  abortByTab.delete(tabId);
  delete explainState.byTabId[tabId];
}
