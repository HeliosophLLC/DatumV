// The single shared state singleton. Holds the data layer (tabs / groups
// / tombstones); DOM rendering and Monaco editor lifecycle are owned by
// main.ts. This module is dependency-light: only modal.ts (for the
// quota-exhausted alert) and the storage browser APIs.

import { alertModal } from './modal.js';

// ===== Storage keys =====

export const STATE_STORAGE_KEY = 'datum.devweb.state';
export const THEME_STORAGE_KEY = 'datum.devweb.theme';
export const EDITOR_ORIENTATION_STORE = 'datum.devweb.editorOrientation';

// ===== Types =====

export interface Tab {
  id: string;
  name: string;
  sql: string;
  // undefined → not yet hydrated from IDB; fetch on activation
  // null      → no saved result (never run, or freshly created)
  // object    → the result payload, cached in memory
  lastResult: unknown;
  lastRunAt: number;
  sqlOfLastRun: string;
  pinned: boolean;
  maxRows: number;
  trace: boolean;
  // Runtime-only (not persisted) — re-initialised per session.
  running: boolean;
  abortController: AbortController | null;
  runStartedAt: number;
  runningRes: unknown;
  liveTickHandle: number | null;
}

export type EditorOrientation = 'horizontal' | 'vertical';

export interface Group {
  id: string;
  tabIds: string[];
  activeTabId: string | undefined;
  editorOrientation: EditorOrientation;
}

export interface AppState {
  tabs: Tab[];
  groups: Group[];
  focusedGroupId: string | null;
  deletedTabIds: string[];
  // Accessor installed by defineStateAccessors — proxies the focused
  // group's activeTabId so call sites can read/write it on `state`.
  activeTabId: string | undefined;
}

// ===== Storage helpers =====

export function readJson<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw === null ? fallback : (JSON.parse(raw) as T);
  } catch {
    return fallback;
  }
}

export function writeJson(key: string, value: unknown): boolean {
  try {
    localStorage.setItem(key, JSON.stringify(value));
    return true;
  } catch {
    return false;
  }
}

export function uuid(): string {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) return crypto.randomUUID();
  return 'id-' + Math.random().toString(36).slice(2) + Date.now().toString(36);
}

// ===== Migration (legacy keys → new flat state) =====

// One-time migration: copy `datum.devweb.workspace.default` →
// `datum.devweb.state` so users who had their tabs/groups in the
// legacy default-workspace key don't lose them when this build deletes
// multi-workspace support. Idempotent — safe to call on every boot.
export function migrateLegacyKeys(): void {
  const LEGACY_DEFAULT_KEY = 'datum.devweb.workspace.default';
  const LEGACY_REGISTRY_KEY = 'datum.devweb.workspaces';
  if (localStorage.getItem(STATE_STORAGE_KEY) === null) {
    const legacy = localStorage.getItem(LEGACY_DEFAULT_KEY);
    if (legacy !== null) localStorage.setItem(STATE_STORAGE_KEY, legacy);
  }
  localStorage.removeItem(LEGACY_DEFAULT_KEY);
  localStorage.removeItem(LEGACY_REGISTRY_KEY);
}

// ===== State singleton =====

export const state: AppState = {
  tabs: [],
  groups: [],
  focusedGroupId: null,
  deletedTabIds: [],
  // activeTabId is installed as an accessor below — the `undefined` here
  // is just a placeholder for the type.
  activeTabId: undefined,
};

// Install `activeTabId` as an accessor that proxies the focused group's
// value. `enumerable: false` keeps it out of JSON.stringify so any
// accidental serialization doesn't double-write the field — persistState
// constructs the snapshot explicitly.
function defineStateAccessors(s: AppState): void {
  Object.defineProperty(s, 'activeTabId', {
    get(this: AppState): string | undefined {
      const g = focusedGroup(this);
      return g ? g.activeTabId : undefined;
    },
    set(this: AppState, v: string | undefined): void {
      const g = focusedGroup(this);
      if (g) g.activeTabId = v;
    },
    configurable: true,
    enumerable: false,
  });
}
defineStateAccessors(state);

// ===== Group / tab helpers =====

// The group whose pane is currently focused — target for Run, keyboard
// shortcuts, and "active tab" reads. Falls back to the first group so
// a corrupt focusedGroupId can't strand the state.
export function focusedGroup(s: AppState = state): Group | null {
  if (!s || !Array.isArray(s.groups) || s.groups.length === 0) return null;
  return s.groups.find((g) => g.id === s.focusedGroupId) || s.groups[0];
}

// Find the group that contains `tabId`, or null if no group does. Each
// tab lives in exactly one group's tabIds; the `tabs[]` array is the
// content store and `groups[].tabIds` partition it across panes.
export function groupOfTab(tabId: string): Group | null {
  return state.groups.find((g) => g.tabIds.includes(tabId)) || null;
}

// Groups whose active tab equals `tabId`. Used by the streaming pipeline
// to update results panes / live tickers without singling out the
// focused group: a tab can only be active in one group at a time, so
// this list is 0 or 1 entries long, but coding it as a loop keeps the
// call sites uniform.
export function getDisplayingGroups(tabId: string): Group[] {
  return state.groups.filter((g) => g.activeTabId === tabId);
}

// Append a brand-new tab to the focused group's tabIds. Used by newTab,
// openSqlInNewTab, and the cross-window-merge path.
export function addTabIdToFocusedGroup(tabId: string): void {
  const g = focusedGroup();
  if (!g) return;
  if (!g.tabIds.includes(tabId)) g.tabIds.push(tabId);
}

// Remove a tab id from whichever group owns it. Returns the group it
// came from so callers can decide whether to dissolve, fall back the
// group's activeTabId, etc.
export function removeTabIdFromItsGroup(tabId: string): Group | null {
  for (const g of state.groups) {
    const idx = g.tabIds.indexOf(tabId);
    if (idx >= 0) {
      g.tabIds.splice(idx, 1);
      return g;
    }
  }
  return null;
}

// ===== Fresh tab seed =====

export function freshTab(n: number): Tab {
  return {
    id: uuid(),
    name: `Untitled ${n}`,
    sql: '',
    lastResult: null,
    lastRunAt: 0,
    sqlOfLastRun: '',
    pinned: false,
    // Per-tab toolbar settings persisted with state.
    maxRows: 200,
    trace: false,
    // Per-tab run state. None of these are persisted.
    running: false,
    abortController: null,
    runStartedAt: 0,
    runningRes: null,
    liveTickHandle: null,
  };
}

// ===== Load / normalise / persist =====

// Normalise a parsed-from-disk state shape in place. Old snapshots
// (top-level `activeTabId`, no `groups`) get a synthetic single group
// containing every tab id; new snapshots are accepted as-is.
function normaliseStateShape(s: AppState): void {
  const persistedActive = s.activeTabId;
  const fallbackOrientation: EditorOrientation =
    localStorage.getItem(EDITOR_ORIENTATION_STORE) === 'horizontal'
      ? 'horizontal'
      : 'vertical';
  if (!Array.isArray(s.groups) || s.groups.length === 0) {
    const tabIds = s.tabs.map((t) => t.id);
    const fallbackActive = tabIds.includes(persistedActive as string)
      ? persistedActive
      : tabIds[0];
    s.groups = [
      {
        id: 'g1',
        tabIds,
        activeTabId: fallbackActive,
        editorOrientation: fallbackOrientation,
      },
    ];
    s.focusedGroupId = 'g1';
  } else {
    // Normalise each group's tabIds to only valid ids, then make sure
    // every tab is claimed by some group. Orphans can show up after a
    // cross-window merge save.
    const claimed = new Set<string>();
    for (const g of s.groups) {
      g.tabIds = Array.isArray(g.tabIds)
        ? g.tabIds.filter((id) => s.tabs.some((t) => t.id === id))
        : [];
      for (const id of g.tabIds) claimed.add(id);
      if (!g.tabIds.includes(g.activeTabId as string)) g.activeTabId = g.tabIds[0];
      if (g.editorOrientation !== 'horizontal' && g.editorOrientation !== 'vertical') {
        g.editorOrientation = fallbackOrientation;
      }
    }
    if (!s.groups.find((g) => g.id === s.focusedGroupId)) {
      s.focusedGroupId = s.groups[0].id;
    }
    const focused = s.groups.find((g) => g.id === s.focusedGroupId) || s.groups[0];
    for (const t of s.tabs) {
      if (!claimed.has(t.id)) {
        focused.tabIds.push(t.id);
        if (!focused.activeTabId) focused.activeTabId = t.id;
      }
    }
  }
}

// Hydrate `state` from localStorage. If nothing's saved (or every saved
// tab is tombstoned), seed a fresh single-tab single-group state.
export function loadInitialState(): void {
  const raw = readJson<any>(STATE_STORAGE_KEY, null);
  if (raw && Array.isArray(raw.tabs) && raw.tabs.length > 0) {
    // Heal already-corrupted snapshots: drop any tabs that overlap with
    // the tombstone list.
    const persistedTombs = new Set<string>(
      Array.isArray(raw.deletedTabIds) ? raw.deletedTabIds : [],
    );
    if (persistedTombs.size > 0) {
      raw.tabs = raw.tabs.filter((t: any) => !persistedTombs.has(t && t.id));
    }
    if (raw.tabs.length > 0) {
      state.tabs = raw.tabs.map((t: any): Tab => {
        const hasRun = (t.lastRunAt || 0) > 0;
        return {
          id: t.id || uuid(),
          name: t.name || 'Untitled',
          sql: t.sql || '',
          lastResult: hasRun ? undefined : null,
          lastRunAt: t.lastRunAt || 0,
          sqlOfLastRun: t.sqlOfLastRun || '',
          pinned: t.pinned === true,
          maxRows: typeof t.maxRows === 'number' && t.maxRows > 0 ? t.maxRows : 200,
          trace: t.trace === true,
          running: false,
          abortController: null,
          runStartedAt: 0,
          runningRes: null,
          liveTickHandle: null,
        };
      });
      state.groups = Array.isArray(raw.groups) ? raw.groups : [];
      state.focusedGroupId = raw.focusedGroupId || null;
      state.deletedTabIds = Array.isArray(raw.deletedTabIds)
        ? raw.deletedTabIds.slice()
        : [];
      // Project persisted activeTabId onto whichever group will end up
      // focused — normaliseStateShape uses it as a hint when synthesising
      // groups for older snapshots.
      if (raw.activeTabId) {
        for (const g of state.groups) {
          if (g.tabIds && g.tabIds.includes(raw.activeTabId)) {
            g.activeTabId = raw.activeTabId;
          }
        }
      }
      normaliseStateShape(state);
      return;
    }
  }
  seedFreshState();
}

// Fresh-state seed used both by first-time load and by recovery from a
// snapshot whose tabs were entirely tombstoned.
export function seedFreshState(): void {
  const tab = freshTab(1);
  const orient: EditorOrientation =
    localStorage.getItem(EDITOR_ORIENTATION_STORE) === 'horizontal'
      ? 'horizontal'
      : 'vertical';
  state.tabs = [tab];
  state.groups = [
    {
      id: 'g1',
      tabIds: [tab.id],
      activeTabId: tab.id,
      editorOrientation: orient,
    },
  ];
  state.focusedGroupId = 'g1';
  state.deletedTabIds = [];
}

// One-time guard so we don't spam the user with a modal if every save
// is failing (quota exhaustion tends to fail repeatedly until something
// is freed). The console.warn still fires every time.
let persistFailureNotified = false;

// Persist state metadata. Result payloads live in IDB and are not part
// of this snapshot — keeps the localStorage write small and fast.
//
// Merge-on-save: read whatever is currently on disk and union it with
// our in-memory tab list, indexed by id. This keeps a second window's
// tabs alive when this window saves, instead of clobbering them. Tabs
// explicitly closed in this window go onto a tombstone list so the
// merge doesn't resurrect them from another window's older snapshot.
export function persistState(): void {
  const onDisk = readJson<any>(STATE_STORAGE_KEY, null);

  // Build the FULL tombstone union *first* — local + disk — so a stale
  // window's in-memory tab list can't resurrect tabs that another
  // window has already tombstoned.
  const tombSet = new Set<string>(state.deletedTabIds || []);
  if (onDisk && Array.isArray(onDisk.deletedTabIds)) {
    for (const id of onDisk.deletedTabIds) tombSet.add(id);
  }

  // In-memory tabs that are tombstoned in EITHER window get dropped.
  const before = state.tabs.length;
  state.tabs = state.tabs.filter((t) => !tombSet.has(t.id));
  if (state.tabs.length !== before && Array.isArray(state.groups)) {
    const validIds = new Set(state.tabs.map((t) => t.id));
    for (const g of state.groups) {
      g.tabIds = (g.tabIds || []).filter((id) => validIds.has(id));
      if (!g.tabIds.includes(g.activeTabId as string)) g.activeTabId = g.tabIds[0];
    }
  }
  const inMemoryById = new Map(state.tabs.map((t) => [t.id, t]));

  const tabsToWrite = state.tabs.map((t) => ({
    id: t.id,
    name: t.name,
    sql: t.sql,
    lastRunAt: t.lastRunAt,
    sqlOfLastRun: t.sqlOfLastRun,
    pinned: t.pinned === true,
    maxRows: t.maxRows,
    trace: t.trace === true,
  }));
  if (onDisk && Array.isArray(onDisk.tabs)) {
    for (const t of onDisk.tabs) {
      if (!t || !t.id) continue;
      if (inMemoryById.has(t.id)) continue;
      if (tombSet.has(t.id)) continue;
      tabsToWrite.push({
        id: t.id,
        name: t.name || 'Untitled',
        sql: t.sql || '',
        lastRunAt: t.lastRunAt || 0,
        sqlOfLastRun: t.sqlOfLastRun || '',
        pinned: t.pinned === true,
        maxRows:
          typeof t.maxRows === 'number' && t.maxRows > 0 ? t.maxRows : 200,
        trace: t.trace === true,
      });
    }
  }

  // Cap tombstones FIFO so the snapshot can't grow without bound.
  const tombArr = [...tombSet];
  const cappedTombs = tombArr.length > 500 ? tombArr.slice(tombArr.length - 500) : tombArr;
  state.deletedTabIds = cappedTombs;

  const groupsToWrite = (state.groups || []).map((g) => ({
    id: g.id,
    tabIds: Array.isArray(g.tabIds) ? g.tabIds.slice() : [],
    activeTabId: g.activeTabId,
    editorOrientation: g.editorOrientation,
  }));
  const snapshot = {
    activeTabId: state.activeTabId,
    tabs: tabsToWrite,
    groups: groupsToWrite,
    focusedGroupId: state.focusedGroupId,
    deletedTabIds: cappedTombs,
  };
  const ok = writeJson(STATE_STORAGE_KEY, snapshot);
  if (!ok) {
    console.warn(
      '[DatumIngest] Failed to persist state — localStorage write returned false. ' +
        'Likely quota exceeded; tab changes are not being saved.',
    );
    if (!persistFailureNotified) {
      persistFailureNotified = true;
      try {
        alertModal(
          'Tabs are not being saved',
          'localStorage rejected the state snapshot — usually because the per-origin storage quota is full. ' +
            'New tabs and edits will be lost on reload until space is freed (close large tabs or clear site data).',
        );
      } catch {
        /* alertModal may not be ready during early boot */
      }
    }
  }
}

// ===== Save debounce =====

// Debounce save so rapid keystrokes don't flog localStorage. Kept short
// (75ms) so the unsaved-edit window is small — important because dev
// server restarts can yank the page before a longer debounce gets a
// chance to fire. Page-visibility / pagehide handlers (in main.ts boot)
// flush the pending save eagerly to close the gap when the user
// alt-tabs away or the tab unloads.
let saveTimer: number | null = null;

export function scheduleSave(): void {
  if (saveTimer !== null) clearTimeout(saveTimer);
  saveTimer = setTimeout(flushPendingSave, 75) as unknown as number;
}

export function flushPendingSave(): void {
  if (saveTimer !== null) {
    clearTimeout(saveTimer);
    saveTimer = null;
  }
  persistState();
}

// ===== Active-tab convenience =====

export function activeTab(): Tab | undefined {
  return state.tabs.find((t) => t.id === state.activeTabId);
}
