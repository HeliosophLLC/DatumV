// Streaming query pipeline. Owns the fetch / NDJSON parse / state
// mutation flow for the run-button path. State mutations through the
// valtio proxy automatically re-render the React results pane — no
// imperative renderResultsForActiveTab() calls needed here.
//
// The toolbar (Run/Cancel button label, elapsed slot, tab-strip
// running indicator) is still imperative; main.ts wires those callbacks
// via setRunHooks() at boot.

import { ref, snapshot } from 'valtio';
import {
  state,
  activeTab,
  getDisplayingGroups,
  persistState,
  type Tab,
} from './state.js';
import * as IDB from './idb.js';
import type { QueryResult, StreamEvent } from './result-types.js';

// ===== Hooks =====
//
// run.ts mutates state but doesn't own toolbar / tab-strip / sidebar
// renderers. main.ts registers these hooks once at boot.

export interface RunHooks {
  renderTabStrip: () => void;
  syncToolbar: () => void;
  refreshSidebarForSql: (sql: string) => void;
}

let hooks: RunHooks = {
  renderTabStrip: () => {},
  syncToolbar: () => {},
  refreshSidebarForSql: () => {},
};

export function setRunHooks(h: RunHooks): void {
  hooks = h;
}

// ===== Cancel =====

export function cancelActiveTabRun(): void {
  const tab = activeTab();
  if (!tab || !tab.running || !tab.abortController) return;
  tab.abortController.abort();
  const status = document.getElementById('status');
  if (status) status.textContent = 'cancelling…';
}

// ===== Live elapsed tick =====
//
// Writes to the `.elapsed` slot inside whichever group currently shows
// the running tab. Stays imperative because the toolbar is still
// imperative; converts to React when the toolbar does.

function paintElapsedForTab(tab: Tab): void {
  if (!tab.runningRes) return;
  const g = getDisplayingGroups(tab.id)[0];
  if (!g) return;
  const groupEl = document.querySelector(
    `.editor-group[data-group-id="${g.id}"]`,
  );
  const slot = groupEl?.querySelector('.elapsed') as HTMLElement | null;
  if (!slot) return;
  const seconds = (performance.now() - tab.runStartedAt) / 1000;
  const rows = tab.runningRes.rowCount;
  const rowText = `${rows.toLocaleString()} ${rows === 1 ? 'row' : 'rows'}`;
  slot.textContent = `${rowText} · ${seconds.toFixed(1)} s (running)`;
}

// ===== Run =====

export async function runQuery(selectedText: string): Promise<void> {
  const tab = activeTab();
  if (!tab) return;
  if (tab.running) return; // already running on THIS tab

  // If text is highlighted, run just that fragment; otherwise run the
  // whole tab. Trim AFTER picking so leading/trailing whitespace in a
  // selection doesn't make us silently fall back to the full tab.
  const isPartial = selectedText.trim().length > 0;
  const sql = (isPartial ? selectedText : tab.sql).trim();
  if (!sql) return;

  // Tab-scoped run state. The closure captures `tab` so handlers below
  // operate on this specific tab even if the user switches away.
  tab.running = true;
  // ref() so valtio doesn't proxy the AbortController — fetch's signal
  // getter would otherwise hand a proxied AbortSignal to the browser
  // and trigger "Illegal invocation".
  tab.abortController = ref(new AbortController());
  tab.runStartedAt = performance.now();
  tab.runIsPartial = isPartial;
  tab.runningRes = {
    resultSets: [],
    rowCount: 0,
    elapsedMs: 0,
    trace: null,
    error: null,
    sessionId: null,
    chunks: [],
  };

  hooks.syncToolbar();
  hooks.renderTabStrip();

  paintElapsedForTab(tab);
  tab.liveTickHandle = setInterval(
    () => paintElapsedForTab(tab),
    250,
  ) as unknown as number;

  try {
    const response = await fetch('/api/query/stream', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        sql,
        maxRows: tab.maxRows || 200,
        trace: tab.trace === true,
      }),
      signal: tab.abortController.signal,
    });

    if (!response.ok) {
      try {
        const errBody = (await response.json()) as { error?: string };
        tab.runningRes.error = errBody.error || `HTTP ${response.status}`;
      } catch {
        tab.runningRes.error = `HTTP ${response.status}`;
      }
    } else if (response.body) {
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buf = '';

      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });
        const lines = buf.split('\n');
        buf = lines.pop() ?? '';
        for (const line of lines) {
          if (!line.trim()) continue;
          let event: StreamEvent;
          try {
            event = JSON.parse(line) as StreamEvent;
          } catch {
            continue;
          }
          handleStreamEvent(tab, event);
        }
      }
      if (buf.trim()) {
        try {
          handleStreamEvent(tab, JSON.parse(buf) as StreamEvent);
        } catch {
          /* ignore trailing junk */
        }
      }
    }
  } catch (err) {
    const e = err as { name?: string; message?: string };
    if (tab.runningRes) {
      if (e.name === 'AbortError') {
        if (!tab.runningRes.error) tab.runningRes.error = 'cancelled';
      } else {
        tab.runningRes.error = `Network error: ${e.message ?? String(err)}`;
      }
    }
  } finally {
    if (tab.liveTickHandle !== null) {
      clearInterval(tab.liveTickHandle);
      tab.liveTickHandle = null;
    }
    tab.running = false;
    tab.abortController = null;
  }

  const finalRes = tab.runningRes;
  if (!finalRes) return;
  const wallMs = performance.now() - tab.runStartedAt;
  if (typeof finalRes.elapsedMs !== 'number' || !finalRes.elapsedMs) {
    finalRes.elapsedMs = wallMs;
  }
  tab.runningRes = null;
  tab.lastResult = finalRes;
  tab.lastRunAt = Date.now();
  if (!isPartial) tab.sqlOfLastRun = tab.sql;

  hooks.renderTabStrip();
  persistState();
  hooks.syncToolbar();

  // snapshot() unwraps the valtio proxy into a plain object so
  // IndexedDB's structuredClone-based put() doesn't choke on Proxy.
  IDB.saveResult(tab.id, snapshot(finalRes)).catch((err) =>
    console.warn(`Couldn't save result for tab ${tab.id}:`, err),
  );

  if (!finalRes.error) hooks.refreshSidebarForSql(sql);
}

function handleStreamEvent(tab: Tab, event: StreamEvent): void {
  const res = tab.runningRes;
  if (!res) return;
  switch (event.type) {
    case 'session':
      res.sessionId = event.id;
      break;
    case 'cell_started':
      break;
    case 'schema':
      // Each schema event opens a new result set. Rows that follow
      // attach to the most-recent set.
      res.resultSets.push({
        schema: event.columns,
        rows: [],
        rowCount: 0,
        truncated: false,
      });
      break;
    case 'chunk':
      res.chunks.push({ model: event.model, text: event.text });
      break;
    case 'row': {
      let cur = res.resultSets[res.resultSets.length - 1];
      if (!cur) {
        cur = { schema: null, rows: [], rowCount: 0, truncated: false };
        res.resultSets.push(cur);
      }
      cur.rows.push(event.cells);
      cur.rowCount = cur.rows.length;
      res.rowCount += 1;
      break;
    }
    case 'truncated': {
      const cur = res.resultSets[res.resultSets.length - 1];
      if (cur) {
        cur.truncated = true;
        cur.rowCount = event.rowCount;
      }
      break;
    }
    case 'trace':
      res.trace = event.text;
      break;
    case 'cell_completed':
      break;
    case 'complete':
      res.elapsedMs = event.elapsedMs;
      break;
    case 'error':
      res.error = event.message;
      if (event.detail) res.detail = event.detail;
      break;
    default:
      break;
  }
}
