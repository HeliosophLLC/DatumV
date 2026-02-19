import { proxy, subscribe } from 'valtio';

// Query-editor tab state. Each tab is an editable SQL document with its
// own title, dirty flag, and last-run result handle. The Monaco editor
// renders the active tab's text and keeps cursor / scroll / undo per
// tab id via dedicated ITextModel instances (managed in the view).
//
// PR 1 keeps the model flat — `tabs[]` + `activeTabId`. PR 7 layers in
// tab groups; the refactor is a single `Tab[]` → `Group[] { tabs: Tab[] }`
// shape change. Pre-emptively nesting groups now adds dead branches and
// makes the early surface harder to read.

export interface Tab {
  /** Stable identifier; used as Monaco model key. Never reused once closed. */
  id: string;
  /** Display name on the strip. Defaults to "Untitled-N" until renamed. */
  title: string;
  /** Current editor text. Mirrors the Monaco model when the editor is mounted. */
  sql: string;
  /** True when `sql` differs from the last persisted-or-saved baseline. */
  dirty: boolean;
}

interface TabsState {
  tabs: Tab[];
  activeTabId: string | null;
}

const STORAGE_KEY = 'datumingest:tabs';
const SAVE_DEBOUNCE_MS = 500;

// Lazily generate ids. `crypto.randomUUID` is available in every browser /
// Electron renderer we target (Chromium ≥ 92). Falls back to a timestamp+
// random suffix for the unlikely older runtime, which still gives stable
// keys within a session.
function newTabId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return `tab-${Date.now()}-${Math.floor(Math.random() * 1e6)}`;
}

function nextUntitledTitle(existing: readonly Tab[]): string {
  // Find the smallest N such that "Untitled-N" isn't taken.
  const taken = new Set(
    existing
      .map((t) => /^Untitled-(\d+)$/.exec(t.title)?.[1])
      .filter((n): n is string => n !== undefined)
      .map((n) => parseInt(n, 10)),
  );
  let n = 1;
  while (taken.has(n)) n++;
  return `Untitled-${n}`;
}

function readPersisted(): TabsState | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<TabsState>;
    if (!Array.isArray(parsed.tabs)) return null;
    // Validate each tab has the required shape; drop malformed entries
    // rather than crashing the whole boot.
    const tabs: Tab[] = [];
    for (const t of parsed.tabs) {
      if (
        typeof t?.id === 'string' &&
        typeof t.title === 'string' &&
        typeof t.sql === 'string'
      ) {
        tabs.push({
          id: t.id,
          title: t.title,
          sql: t.sql,
          dirty: Boolean(t.dirty),
        });
      }
    }
    if (tabs.length === 0) return null;
    const activeTabId =
      typeof parsed.activeTabId === 'string' &&
      tabs.some((t) => t.id === parsed.activeTabId)
        ? parsed.activeTabId
        : tabs[0].id;
    return { tabs, activeTabId };
  } catch {
    // localStorage unavailable, parse errors, etc. — fall through to
    // a fresh single-tab state. Never block the app on persistence
    // failure.
    return null;
  }
}

function createInitialState(): TabsState {
  const persisted = readPersisted();
  if (persisted) return persisted;

  // First-ever boot: one empty Untitled-1 tab.
  const firstTab: Tab = {
    id: newTabId(),
    title: 'Untitled-1',
    sql: '',
    dirty: false,
  };
  return { tabs: [firstTab], activeTabId: firstTab.id };
}

export const tabsState = proxy<TabsState>(createInitialState());

// Debounced auto-save. Subscribes to every mutation and pushes the
// snapshot through localStorage on a trailing-edge timer so a burst of
// keystrokes results in one write rather than dozens. Always-on once
// the module is imported; no React lifecycle to wire up.
let saveTimer: number | undefined;
subscribe(tabsState, () => {
  if (saveTimer !== undefined) window.clearTimeout(saveTimer);
  saveTimer = window.setTimeout(() => {
    saveTimer = undefined;
    try {
      // Strip the `dirty` field on persist — it's a per-session UI
      // concept, not part of the document. Reloading shouldn't
      // resurrect a "you have unsaved changes" indicator from a
      // previous run.
      const snapshot = {
        tabs: tabsState.tabs.map((t) => ({
          id: t.id,
          title: t.title,
          sql: t.sql,
        })),
        activeTabId: tabsState.activeTabId,
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
    } catch {
      // Quota errors / disabled storage — drop the save silently.
      // Loss-of-tabs across a crash is preferable to crashing on every
      // keystroke.
    }
  }, SAVE_DEBOUNCE_MS);
});

// ────────── Actions ──────────

export function openTab(initialSql = ''): Tab {
  const tab: Tab = {
    id: newTabId(),
    title: nextUntitledTitle(tabsState.tabs),
    sql: initialSql,
    dirty: false,
  };
  tabsState.tabs.push(tab);
  tabsState.activeTabId = tab.id;
  return tab;
}

export function closeTab(id: string): void {
  const idx = tabsState.tabs.findIndex((t) => t.id === id);
  if (idx < 0) return;

  tabsState.tabs.splice(idx, 1);

  // Reselect if we closed the active one. Picks the neighbour to the
  // right by default (fall back to the left at the end of the strip)
  // so closing in sequence walks the user toward the strip's start
  // rather than jumping to a random surviving tab. When the last tab
  // is closed we leave `activeTabId` null — the editor renders a
  // blank surface until the user opens a new tab.
  if (tabsState.activeTabId === id) {
    if (tabsState.tabs.length === 0) {
      tabsState.activeTabId = null;
    } else {
      const neighbour = tabsState.tabs[idx] ?? tabsState.tabs[idx - 1];
      tabsState.activeTabId = neighbour.id;
    }
  }
}

export function selectTab(id: string): void {
  if (tabsState.tabs.some((t) => t.id === id)) {
    tabsState.activeTabId = id;
  }
}

export function renameTab(id: string, title: string): void {
  const tab = tabsState.tabs.find((t) => t.id === id);
  if (!tab) return;
  const trimmed = title.trim();
  if (trimmed.length === 0) return;
  tab.title = trimmed;
}

export function setTabSql(id: string, sql: string): void {
  const tab = tabsState.tabs.find((t) => t.id === id);
  if (!tab) return;
  if (tab.sql === sql) return;
  tab.sql = sql;
  tab.dirty = true;
}

export function getActiveTab(): Tab | null {
  if (tabsState.activeTabId === null) return null;
  return tabsState.tabs.find((t) => t.id === tabsState.activeTabId) ?? null;
}
