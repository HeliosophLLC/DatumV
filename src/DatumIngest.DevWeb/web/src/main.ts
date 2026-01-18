// @ts-nocheck
// Phase 0 lift of the inline IIFE from wwwroot/index.html. Module-scope
// replaces the IIFE wrapper. Behaviour is unchanged; further phases will
// peel cohesive sections into typed modules and shrink this file. Types
// are deliberately suppressed here — typing the legacy closure as one
// blob is busywork, peeling-as-we-go is the migration plan.

import { htmlNode, escapeHtml, truncate } from './html-util.js';
import {
  parseParameterList,
  splitTopLevel,
  buildExecuteTemplate,
  buildModifyTemplateFromUdfRow,
  buildModifyTemplateFromProcedureRow,
  buildBuiltinExecuteTemplate,
} from './parser-util.js';
import {
  renderJsonNode,
  renderJsonObject,
  renderJsonArray,
} from './json-render.js';
import * as IDB from './idb.js';
import { loadTheme, applyTheme, toggleTheme } from './theme.js';
import {
  showModal,
  alertModal,
  confirmModal,
  promptModal,
  openImageLightbox,
} from './modal.js';

  // ===== Storage keys & helpers =====
  // Two-tier persistence:
  //   localStorage holds small JSON state — workspace registry, theme, and
  //     per-workspace { tabs: [{ id, name, sql, lastRunAt, sqlOfLastRun }],
  //     groups: [{ id, tabIds, activeTabId }], focusedGroupId, deletedTabIds,
  //     activeTabId (top-level mirror, kept for backward compat) }.
  //     Read synchronously on boot.
  //   IndexedDB holds result payloads only, keyed by "<workspace>::<tabId>".
  //     Lazy-loaded on tab activation so the boot path stays sync.
  const STORE = {
    theme: 'datum.devweb.theme',
    workspaces: 'datum.devweb.workspaces',
    workspace: (name) => `datum.devweb.workspace.${name}`,
  };

  function readJson(key, fallback) {
    try {
      const raw = localStorage.getItem(key);
      return raw === null ? fallback : JSON.parse(raw);
    } catch { return fallback; }
  }
  function writeJson(key, value) {
    try { localStorage.setItem(key, JSON.stringify(value)); return true; }
    catch { return false; }
  }
  function uuid() {
    if (crypto && crypto.randomUUID) return crypto.randomUUID();
    return 'id-' + Math.random().toString(36).slice(2) + Date.now().toString(36);
  }

  // IndexedDB result store — see ./idb.ts

  // Theme — see ./theme.ts

  // ===== Workspaces =====
  // A workspace is { tabs: [Tab], groups: [Group], focusedGroupId,
  //                  deletedTabIds }.
  // A group is { id, tabIds: [string], activeTabId }. The flat `tabs` array
  // is the source of truth for tab content; `groups` is the *view layer*
  // describing how those tabs are partitioned across editor panes. Step 2
  // of the multi-group rollout: there is exactly one group and its tabIds
  // mirrors `tabs.map(t => t.id)`. `state.workspace.activeTabId` is wired
  // as a getter/setter on the workspace that delegates to the focused
  // group, so the dozens of existing call sites continue to work
  // unchanged. Step 3 will let groups hold disjoint tab subsets.
  // A tab in localStorage is { id, name, sql, lastRunAt, sqlOfLastRun }.
  // The runtime tab object also carries `lastResult`:
  //   undefined → not yet hydrated from IDB; fetch on activation
  //   null      → no saved result (never run, or freshly created)
  //   object    → the result payload, cached in memory
  // Workspace identity comes from location.hash (no hash → "default").
  function workspaceName() {
    const h = (location.hash || '').replace(/^#/, '').trim();
    return validName(h) ? h : 'default';
  }
  function validName(name) {
    return typeof name === 'string' && /^[a-zA-Z0-9_-]{1,40}$/.test(name);
  }
  function listWorkspaces() {
    const list = readJson(STORE.workspaces, []);
    return Array.isArray(list) ? list : [];
  }
  function registerWorkspace(name) {
    const list = listWorkspaces();
    if (!list.includes(name)) {
      list.push(name);
      writeJson(STORE.workspaces, list);
    }
  }
  function unregisterWorkspace(name) {
    const list = listWorkspaces().filter(n => n !== name);
    writeJson(STORE.workspaces, list);
    localStorage.removeItem(STORE.workspace(name));
    IDB.deleteWorkspaceResults(name).catch(err =>
      console.warn(`Couldn't clear IDB results for "${name}":`, err));
  }
  function freshTab(n) {
    return {
      id: uuid(),
      name: `Untitled ${n}`,
      sql: '',
      lastResult: null,            // never run yet → no IDB entry to fetch
      lastRunAt: 0,
      sqlOfLastRun: '',
      pinned: false,

      // Per-tab toolbar settings persisted with the workspace. Each tab
      // remembers its preferred row cap and trace toggle so switching
      // tabs swaps the toolbar to that tab's settings.
      maxRows: 200,
      trace: false,

      // Per-tab run state. None of these are persisted — they're
      // re-initialised per session. `running` flags an in-flight query
      // for THIS tab; `abortController` lets the user cancel it; the
      // accumulator `runningRes` captures streamed events so the
      // results pane can re-render on tab switch-back.
      running: false,
      abortController: null,
      runStartedAt: 0,
      runningRes: null,
      liveTickHandle: null,
    };
  }

  // The group whose pane is currently focused — target for Run, keyboard
  // shortcuts, and "active tab" reads. Falls back to the first group so
  // a corrupt focusedGroupId can't strand the workspace.
  function focusedGroup(ws) {
    ws = ws || state.workspace;
    if (!ws || !Array.isArray(ws.groups) || ws.groups.length === 0) return null;
    return ws.groups.find(g => g.id === ws.focusedGroupId) || ws.groups[0];
  }

  // Install `activeTabId` as an accessor on the workspace that proxies the
  // focused group's value. Lets all existing `state.workspace.activeTabId`
  // reads/writes keep working without per-site edits during the multi-group
  // rollout. `enumerable: false` keeps it out of JSON.stringify so any
  // accidental serialization of the workspace doesn't double-write the
  // field (`persistWorkspace` constructs the snapshot explicitly anyway).
  function defineWorkspaceAccessors(ws) {
    Object.defineProperty(ws, 'activeTabId', {
      get() { const g = focusedGroup(this); return g ? g.activeTabId : undefined; },
      set(v) { const g = focusedGroup(this); if (g) g.activeTabId = v; },
      configurable: true,
      enumerable: false,
    });
  }

  // Find the group that contains `tabId`, or null if no group does. Each
  // tab lives in exactly one group's tabIds; the workspace `tabs[]` array
  // is the content store and `groups[].tabIds` partition it across panes.
  function groupOfTab(tabId) {
    return state.workspace.groups.find(g => g.tabIds.includes(tabId)) || null;
  }

  // Lookup a group's DOM root by id.
  function groupElementById(groupId) {
    return document.querySelector(`.editor-group[data-group-id="${groupId}"]`);
  }

  // Groups whose active tab equals `tabId`. Used by the streaming pipeline
  // to update results panes / live tickers without singling out the
  // focused group: a tab can only be active in one group at a time, so
  // this list is 0 or 1 entries long, but coding it as a loop keeps the
  // call sites uniform.
  function getDisplayingGroups(tabId) {
    return state.workspace.groups.filter(g => g.activeTabId === tabId);
  }

  // Append a brand-new tab to the focused group's tabIds. Used by newTab,
  // openSqlInNewTab, and the cross-window-merge path.
  function addTabIdToFocusedGroup(tabId) {
    const g = focusedGroup();
    if (!g) return;
    if (!g.tabIds.includes(tabId)) g.tabIds.push(tabId);
  }

  // Remove a tab id from whichever group owns it. Returns the group it
  // came from so callers can decide whether to dissolve, fall back the
  // group's activeTabId, etc.
  function removeTabIdFromItsGroup(tabId) {
    for (const g of state.workspace.groups) {
      const idx = g.tabIds.indexOf(tabId);
      if (idx >= 0) {
        g.tabIds.splice(idx, 1);
        return g;
      }
    }
    return null;
  }

  // Tear down a group: drop it from `groups[]`, dispose its Monaco editor
  // (if any), remove its DOM node, and re-target focus if it was the
  // focused one. Caller is responsible for ensuring the group's tabIds is
  // empty (or that orphaned tabs are handled elsewhere). Reconciles the
  // group container at the end so the splitter / orient-toggle button /
  // ratio adjust to the new group count.
  function dissolveGroup(groupId) {
    const idx = state.workspace.groups.findIndex(g => g.id === groupId);
    if (idx < 0) return;
    state.workspace.groups.splice(idx, 1);
    const editor = monacoEditorsByGroup.get(groupId);
    if (editor) {
      editor.setModel(null);
      editor.dispose();
      monacoEditorsByGroup.delete(groupId);
    }
    fallbackTextareasByGroup.delete(groupId);
    const groupEl = document.querySelector(`.editor-group[data-group-id="${groupId}"]`);
    if (groupEl) groupEl.remove();
    if (state.workspace.focusedGroupId === groupId) {
      state.workspace.focusedGroupId = state.workspace.groups[0]?.id;
    }
    reconcileGroupDom();
  }

  // Migrate a parsed-from-disk workspace shape in place. Old snapshots
  // (top-level `activeTabId`, no `groups`) get a synthetic single group
  // containing every tab id; new snapshots are accepted as-is. Either way
  // the accessor for `activeTabId` is installed before returning.
  function migrateWorkspaceShape(ws) {
    const persistedActive = ws.activeTabId;
    // Default per-group orientation: workspaces saved before this field
    // existed inherit whatever was in the global localStorage key, so a
    // user who'd been working in side-by-side mode keeps that on reload.
    const fallbackOrientation =
      localStorage.getItem(EDITOR_ORIENTATION_STORE) === 'horizontal'
        ? 'horizontal' : 'vertical';
    if (!Array.isArray(ws.groups) || ws.groups.length === 0) {
      const tabIds = ws.tabs.map(t => t.id);
      const fallbackActive = tabIds.includes(persistedActive) ? persistedActive : tabIds[0];
      ws.groups = [{
        id: 'g1', tabIds, activeTabId: fallbackActive,
        editorOrientation: fallbackOrientation,
      }];
      ws.focusedGroupId = 'g1';
    } else {
      // Normalise each group's tabIds to only valid ids, then make sure
      // every tab is claimed by some group. Orphans can show up after a
      // cross-window merge save: persistWorkspace writes `tabsToWrite`
      // including disk-only tabs, but `groupsToWrite` mirrors only
      // in-memory groups, so the union has tabs that no group references.
      const claimed = new Set();
      for (const g of ws.groups) {
        g.tabIds = Array.isArray(g.tabIds) ? g.tabIds.filter(id => ws.tabs.some(t => t.id === id)) : [];
        for (const id of g.tabIds) claimed.add(id);
        if (!g.tabIds.includes(g.activeTabId)) g.activeTabId = g.tabIds[0];
        if (g.editorOrientation !== 'horizontal' && g.editorOrientation !== 'vertical') {
          g.editorOrientation = fallbackOrientation;
        }
      }
      if (!ws.groups.find(g => g.id === ws.focusedGroupId)) {
        ws.focusedGroupId = ws.groups[0].id;
      }
      const focused = ws.groups.find(g => g.id === ws.focusedGroupId) || ws.groups[0];
      for (const t of ws.tabs) {
        if (!claimed.has(t.id)) {
          focused.tabIds.push(t.id);
          if (!focused.activeTabId) focused.activeTabId = t.id;
        }
      }
    }
    defineWorkspaceAccessors(ws);
  }

  function loadWorkspace(name) {
    const ws = readJson(STORE.workspace(name), null);
    if (ws && Array.isArray(ws.tabs) && ws.tabs.length > 0) {
      // Drop any tabs that overlap with the tombstone list. A bug in
      // earlier persistWorkspace versions could write tabs[] and
      // deletedTabIds[] with overlapping ids (a stale window saving its
      // older state alongside a fresher window's tombstones); those
      // snapshots manifest as "closed tabs reappear after reload."
      // Filtering here heals already-corrupted snapshots.
      const persistedTombs = new Set(
        Array.isArray(ws.deletedTabIds) ? ws.deletedTabIds : []);
      if (persistedTombs.size > 0) {
        ws.tabs = ws.tabs.filter(t => !persistedTombs.has(t && t.id));
      }
      if (ws.tabs.length === 0) {
        // Tombstones ate everything — fall through to the fresh-workspace
        // branch below by making the outer guard fail on next iteration.
        return loadFreshWorkspace();
      }
      // Ensure each tab has the expected shape (forward-compat). Tabs hydrated
      // from localStorage have `lastResult` left undefined so the renderer
      // knows to fetch from IDB on first activation. Older saves that included
      // a `lastResult` field are ignored — IDB is the source of truth now.
      ws.tabs = ws.tabs.map(t => {
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
          // Run-state fields are runtime-only; they always start fresh on
          // workspace load even if a session crashed mid-run.
          running: false,
          abortController: null,
          runStartedAt: 0,
          runningRes: null,
          liveTickHandle: null,
        };
      });
      if (!ws.tabs.find(t => t.id === ws.activeTabId)) {
        ws.activeTabId = ws.tabs[0].id;
      }
      // Tombstones for tabs intentionally closed in any window. Used by
      // persistWorkspace to avoid resurrecting a deleted tab during a
      // multi-window merge. Stored as a bare string[] of ids; we cap the
      // length on save to keep the snapshot small.
      ws.deletedTabIds = Array.isArray(ws.deletedTabIds) ? ws.deletedTabIds.slice() : [];
      // Synthesise (or normalise) groups + focusedGroupId, then install
      // the activeTabId accessor that delegates to the focused group.
      migrateWorkspaceShape(ws);
      return ws;
    }
    return loadFreshWorkspace();
  }

  // Fresh-workspace seed used both by first-time load and by recovery
  // from a snapshot whose tabs were entirely tombstoned.
  function loadFreshWorkspace() {
    const tab = freshTab(1);
    const orient =
      localStorage.getItem(EDITOR_ORIENTATION_STORE) === 'horizontal'
        ? 'horizontal' : 'vertical';
    const fresh = {
      tabs: [tab],
      groups: [{
        id: 'g1', tabIds: [tab.id], activeTabId: tab.id,
        editorOrientation: orient,
      }],
      focusedGroupId: 'g1',
      deletedTabIds: [],
    };
    defineWorkspaceAccessors(fresh);
    return fresh;
  }
  // One-time guard so we don't spam the user with a modal if every save
  // is failing (quota exhaustion tends to fail repeatedly until something
  // is freed). The console.warn still fires every time.
  let persistFailureNotified = false;

  // Persist current workspace metadata. Result payloads live in IDB and are
  // not part of this snapshot — keeps the localStorage write small and fast.
  //
  // Merge-on-save: read whatever is currently on disk and union it with our
  // in-memory tab list, indexed by id. This keeps a second window's tabs
  // alive when this window saves, instead of clobbering them. Tabs explicitly
  // closed in this window go onto a tombstone list so the merge doesn't
  // resurrect them from another window's older snapshot.
  function persistWorkspace() {
    const name = workspaceName();
    const onDisk = readJson(STORE.workspace(name), null);

    // Build the FULL tombstone union *first* — local + disk — so a
    // stale window's in-memory tab list can't resurrect tabs that
    // another window has already tombstoned. (Earlier we only filtered
    // disk tabs against tombstones; in-memory tabs were trusted, which
    // meant a stale window happily wrote its old tabs back over a fresh
    // disk state and the next load saw "deleted" tabs reappear.)
    const tombSet = new Set(state.workspace.deletedTabIds || []);
    if (onDisk && Array.isArray(onDisk.deletedTabIds)) {
      for (const id of onDisk.deletedTabIds) tombSet.add(id);
    }

    // In-memory tabs that are tombstoned in EITHER window get dropped.
    // This both fixes the resurrection bug and keeps state.workspace.tabs
    // consistent for the rest of this function (the inMemoryById index
    // below is built after the filter). Group tabIds get the same scrub
    // so the next render doesn't reference a now-deleted tab.
    const before = state.workspace.tabs.length;
    state.workspace.tabs = state.workspace.tabs.filter(t => !tombSet.has(t.id));
    if (state.workspace.tabs.length !== before && Array.isArray(state.workspace.groups)) {
      const validIds = new Set(state.workspace.tabs.map(t => t.id));
      for (const g of state.workspace.groups) {
        g.tabIds = (g.tabIds || []).filter(id => validIds.has(id));
        if (!g.tabIds.includes(g.activeTabId)) g.activeTabId = g.tabIds[0];
      }
    }
    const inMemoryById = new Map(state.workspace.tabs.map(t => [t.id, t]));

    const tabsToWrite = state.workspace.tabs.map(t => ({
      id: t.id,
      name: t.name,
      sql: t.sql,
      lastRunAt: t.lastRunAt,
      sqlOfLastRun: t.sqlOfLastRun,
      pinned: t.pinned === true,
      maxRows: t.maxRows,
      trace: t.trace === true,
      // Runtime-only fields (running, abortController, etc.) are
      // intentionally omitted — a reload should always start clean.
    }));
    if (onDisk && Array.isArray(onDisk.tabs)) {
      for (const t of onDisk.tabs) {
        if (!t || !t.id) continue;
        if (inMemoryById.has(t.id)) continue;   // we have a fresher view
        if (tombSet.has(t.id)) continue;         // tombstoned anywhere
        tabsToWrite.push({
          id: t.id,
          name: t.name || 'Untitled',
          sql: t.sql || '',
          lastRunAt: t.lastRunAt || 0,
          sqlOfLastRun: t.sqlOfLastRun || '',
          pinned: t.pinned === true,
          maxRows: typeof t.maxRows === 'number' && t.maxRows > 0 ? t.maxRows : 200,
          trace: t.trace === true,
        });
      }
    }

    // Cap tombstones FIFO so the snapshot can't grow without bound. 500 is
    // far more than any real session would close.
    const tombArr = [...tombSet];
    const cappedTombs = tombArr.length > 500 ? tombArr.slice(tombArr.length - 500) : tombArr;
    state.workspace.deletedTabIds = cappedTombs;

    // Snapshot the group layout. Each group records only its id, tab-id
    // ordering, and which tab is active there — group memberships are
    // partition info, not tab content. The top-level `activeTabId` field
    // is also written so an older code revision (without group support)
    // can still load the workspace.
    const groupsToWrite = (state.workspace.groups || []).map(g => ({
      id: g.id,
      tabIds: Array.isArray(g.tabIds) ? g.tabIds.slice() : [],
      activeTabId: g.activeTabId,
      editorOrientation: g.editorOrientation,
    }));
    const snapshot = {
      activeTabId: state.workspace.activeTabId,
      tabs: tabsToWrite,
      groups: groupsToWrite,
      focusedGroupId: state.workspace.focusedGroupId,
      deletedTabIds: cappedTombs,
    };
    const ok = writeJson(STORE.workspace(name), snapshot);
    if (!ok) {
      // Most likely cause is localStorage quota exhaustion (a few large SQL
      // pastes can do it). Loud-fail to the console every time so it shows
      // up in dev tools, and pop a one-time modal so the user knows their
      // tabs aren't being saved before they restart and lose work.
      console.warn('[DatumIngest] Failed to persist workspace — localStorage write returned false. ' +
                   'Likely quota exceeded; tab changes are not being saved.');
      if (!persistFailureNotified) {
        persistFailureNotified = true;
        try {
          alertModal('Tabs are not being saved',
            'localStorage rejected the workspace snapshot — usually because the per-origin storage quota is full. ' +
            'New tabs and edits will be lost on reload until space is freed (close large tabs or clear site data).');
        } catch { /* alertModal may not be ready during early boot */ }
      }
    }
  }

  // ===== State =====
  // Run state lives on each tab (tab.running / tab.abortController / ...)
  // so multiple tabs can have queries in flight concurrently. The server
  // currently serialises queries via a SemaphoreSlim, so requests from
  // multiple tabs queue server-side — but each tab tracks its own state
  // and the UI accurately reflects "running" per tab.
  const state = {
    workspace: null,            // { tabs, groups, focusedGroupId, deletedTabIds }
  };

  // Debounce save so rapid keystrokes don't flog localStorage. Kept
  // short (75ms) so the unsaved-edit window is small — important
  // because dev-server restarts can yank the page before a longer
  // debounce gets a chance to fire. Page-visibility / pagehide handlers
  // (see boot()) flush the pending save eagerly to close the gap when
  // the user alt-tabs away or the tab unloads.
  let saveTimer = null;
  function scheduleSave() {
    if (saveTimer) clearTimeout(saveTimer);
    saveTimer = setTimeout(flushPendingSave, 75);
  }
  function flushPendingSave() {
    if (saveTimer) { clearTimeout(saveTimer); saveTimer = null; }
    persistWorkspace();
  }

  function activeTab() {
    return state.workspace.tabs.find(t => t.id === state.workspace.activeTabId);
  }

  // ===== Group lookup helpers =====
  // Per-group DOM elements (tab strip, editor, results, toolbar inputs)
  // all live under a `.editor-group[data-group-id=...]` subtree. Step 1
  // of the multi-group refactor: there is exactly one group ('g1') and
  // every helper defaults to it. When the split feature lands, callers
  // that need a specific group will pass its element explicitly; the
  // default will become "the focused group" rather than "the only group".
  const DEFAULT_GROUP_ID = 'g1';
  function focusedGroupEl() {
    // Workspace may not be loaded yet during early boot; fall back to
    // the seed group baked into the HTML so helpers don't blow up.
    const id = state.workspace ? state.workspace.focusedGroupId : DEFAULT_GROUP_ID;
    return document.querySelector(`.editor-group[data-group-id="${id}"]`)
        || document.querySelector('.editor-group');
  }
  function tabStripEl(g)             { return (g || focusedGroupEl()).querySelector('.tab-strip'); }
  function editorPaneEl(g)           { return (g || focusedGroupEl()).querySelector('.editor-pane'); }
  function editorHostEl(g)           { return (g || focusedGroupEl()).querySelector('.editor-host'); }
  function editorToolbarEl(g)        { return (g || focusedGroupEl()).querySelector('.editor-toolbar'); }
  function editorResultsWrapperEl(g) { return (g || focusedGroupEl()).querySelector('.editor-results-wrapper'); }
  function editorResultsResizeEl(g)  { return (g || focusedGroupEl()).querySelector('.editor-results-resize'); }
  function resultsPaneEl(g)          { return (g || focusedGroupEl()).querySelector('.results-pane'); }
  function runBtnEl(g)               { return (g || focusedGroupEl()).querySelector('.run-btn'); }
  function maxRowsInputEl(g)         { return (g || focusedGroupEl()).querySelector('.max-rows-input'); }
  function traceToggleEl(g)          { return (g || focusedGroupEl()).querySelector('.trace-toggle'); }
  function elapsedEl(g)              { return (g || focusedGroupEl()).querySelector('.elapsed'); }

  // ===== Tabs =====
  function renderTabStrip() {
    for (const g of state.workspace.groups) renderTabStripForGroup(g);
  }

  // Render one group's tab strip from its own `tabIds` (NOT the global
  // `tabs[]` order — each group has its own ordering). The "active" tab
  // class reflects the group's own activeTabId; clicking a tab focuses
  // its group as a side effect, so visual focus follows interaction.
  function renderTabStripForGroup(group) {
    const groupEl = groupElementById(group.id);
    if (!groupEl) return;
    const strip = tabStripEl(groupEl);
    strip.innerHTML = '';
    for (const tabId of group.tabIds) {
      const t = state.workspace.tabs.find(x => x.id === tabId);
      if (!t) continue;
      const div = document.createElement('div');
      div.className = 'tab' + (t.id === group.activeTabId ? ' active' : '')
        + (t.pinned ? ' pinned' : '');
      div.dataset.id = t.id;

      if (t.pinned) {
        const pin = document.createElement('span');
        pin.className = 'pin';
        pin.textContent = '📌';
        pin.title = 'Pinned — right-click to unpin';
        div.appendChild(pin);
      }

      // Running indicator: small spinner-like glyph in front of the name
      // when this tab has an in-flight query. Lets the user see at a
      // glance which tabs are still working without switching to them.
      if (t.running) {
        const spinner = document.createElement('span');
        spinner.className = 'running-indicator';
        spinner.textContent = '⏳';
        spinner.title = 'Query running — Esc on this tab to cancel';
        div.appendChild(spinner);
      }

      const name = document.createElement('span');
      name.className = 'name';
      name.textContent = t.name;
      name.title = 'Double-click to rename';
      name.addEventListener('dblclick', (e) => {
        e.stopPropagation();
        beginRenameTab(t.id, name);
      });
      div.appendChild(name);

      if (t.sql !== t.sqlOfLastRun) {
        const dirty = document.createElement('span');
        dirty.className = 'dirty';
        dirty.textContent = '●';
        dirty.title = 'unsaved changes since last run';
        div.appendChild(dirty);
      }

      // Pinned tabs hide the close button so an accidental click can't drop
      // the work; the tab can still be closed via right-click → Unpin → Close.
      if (!t.pinned) {
        const close = document.createElement('button');
        close.className = 'close';
        close.textContent = '×';
        close.title = 'Close tab';
        close.addEventListener('click', (e) => {
          e.stopPropagation();
          closeTab(t.id);
        });
        div.appendChild(close);
      }

      div.addEventListener('click', () => activateTab(t.id));
      // Middle-click closes (browser-tab convention). Pinned tabs are
      // protected — same rule as the × button.
      div.addEventListener('auxclick', (e) => {
        if (e.button !== 1 || t.pinned) return;
        e.preventDefault();
        closeTab(t.id);
      });
      // Suppress middle-click autoscroll on the strip.
      div.addEventListener('mousedown', (e) => {
        if (e.button === 1) e.preventDefault();
      });
      div.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        showTabContextMenu(e.clientX, e.clientY, t.id);
      });

      // Drag-to-reorder. Drop position is "before" if the cursor is in
      // the left half of the target tab, "after" otherwise. Cross-group
      // drops are step-4 territory.
      div.draggable = true;
      div.addEventListener('dragstart', (e) => {
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', t.id);
        div.classList.add('dragging');
      });
      div.addEventListener('dragend', () => {
        div.classList.remove('dragging');
        clearDropIndicators();
      });
      div.addEventListener('dragover', (e) => {
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        const rect = div.getBoundingClientRect();
        const before = (e.clientX - rect.left) < rect.width / 2;
        clearDropIndicators();
        div.classList.add(before ? 'drop-before' : 'drop-after');
      });
      div.addEventListener('dragleave', () => {
        div.classList.remove('drop-before', 'drop-after');
      });
      div.addEventListener('drop', (e) => {
        e.preventDefault();
        const draggedId = e.dataTransfer.getData('text/plain');
        const before = div.classList.contains('drop-before');
        clearDropIndicators();
        moveTab(draggedId, t.id, before);
      });
      strip.appendChild(div);
    }

    // Per-group "+" button creates a tab in *this* group, regardless of
    // which group is currently focused.
    const newBtn = document.createElement('button');
    newBtn.className = 'new-tab';
    newBtn.textContent = '+';
    newBtn.title = 'New tab';
    newBtn.addEventListener('click', () => newTabInGroup(group.id));
    strip.appendChild(newBtn);

    // Spacer pushes the orient-toggle to the right edge so it lines up
    // with the editor pane's right margin and is visually clear about
    // which pane it controls.
    const spacer = document.createElement('span');
    spacer.className = 'strip-spacer';
    strip.appendChild(spacer);

    // Per-pane orient toggle: flips this pane's editor/results stack
    // between vertical (default) and horizontal (side-by-side). The
    // divider line in the SVG previews the *target* state — clicking
    // produces the layout the icon depicts.
    const orientBtn = document.createElement('button');
    orientBtn.className = 'strip-orient-toggle';
    orientBtn.title = 'Toggle this pane\'s editor/results layout (vertical ↔ side-by-side)';
    orientBtn.setAttribute('aria-label', 'Toggle pane layout');
    const horizontal = group.editorOrientation === 'horizontal';
    orientBtn.innerHTML =
      '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.6">' +
      '<rect x="3" y="4" width="18" height="16" rx="1"></rect>' +
      // Target = opposite of current: horizontal pane → click produces
      // a vertical (stacked) layout → horizontal divider line.
      `<line class="orient-toggle-divider" ${horizontal
        ? 'x1="3" y1="12" x2="21" y2="12"'
        : 'x1="12" y1="4" x2="12" y2="20"'}></line>` +
      '</svg>';
    orientBtn.addEventListener('click', () => toggleEditorOrientationForGroup(group));
    strip.appendChild(orientBtn);
  }

  // Inline-rename: swap the .name span for an <input>. Commit on Enter or
  // blur; cancel on Escape. The new name is trimmed; an empty result keeps
  // the previous name so the tab is never anonymous in the strip.
  function beginRenameTab(id, nameEl) {
    const tab = state.workspace.tabs.find(t => t.id === id);
    if (!tab) return;
    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'name-input';
    input.value = tab.name;
    nameEl.replaceWith(input);
    input.focus();
    input.select();

    let done = false;
    const commit = (newName) => {
      if (done) return;
      done = true;
      const trimmed = (newName ?? '').trim();
      if (trimmed && trimmed !== tab.name) {
        tab.name = trimmed;
        persistWorkspace();
      }
      renderTabStrip();
    };
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); commit(input.value); }
      else if (e.key === 'Escape') { e.preventDefault(); commit(null); }
    });
    input.addEventListener('blur', () => commit(input.value));
    // Don't let click inside the input bubble up to the tab activator.
    input.addEventListener('click', (e) => e.stopPropagation());
  }

  function togglePinTab(id) {
    const tab = state.workspace.tabs.find(t => t.id === id);
    if (!tab) return;
    tab.pinned = !tab.pinned;
    renderTabStrip();
    persistWorkspace();
  }

  // Custom context menu — single shared element, repositioned on each open.
  // Dismissed by any outside pointerdown, scroll, resize, or Escape.
  let tabContextMenu = null;
  function showTabContextMenu(x, y, tabId) {
    closeTabContextMenu();
    const tab = state.workspace.tabs.find(t => t.id === tabId);
    if (!tab) return;

    const menu = document.createElement('div');
    menu.id = 'tab-context-menu';
    const addItem = (label, onClick, opts = {}) => {
      const btn = document.createElement('button');
      btn.textContent = label;
      if (opts.disabled) btn.disabled = true;
      btn.addEventListener('click', () => { closeTabContextMenu(); onClick(); });
      menu.appendChild(btn);
    };
    const addSep = () => {
      const s = document.createElement('div'); s.className = 'sep'; menu.appendChild(s);
    };

    addItem('Rename…', () => {
      // Re-find the live name span — renderTabStrip may have rebuilt the DOM.
      const nameEl = focusedGroupEl().querySelector(`.tab-strip .tab[data-id="${tabId}"] .name`);
      if (nameEl) beginRenameTab(tabId, nameEl);
    });
    addItem(tab.pinned ? 'Unpin' : 'Pin', () => togglePinTab(tabId));
    addSep();
    addItem('Close', () => closeTab(tabId), { disabled: tab.pinned });

    document.body.appendChild(menu);
    // Clamp to viewport so right-edge clicks don't push the menu off-screen.
    const rect = menu.getBoundingClientRect();
    const left = Math.min(x, window.innerWidth - rect.width - 4);
    const top  = Math.min(y, window.innerHeight - rect.height - 4);
    menu.style.left = `${Math.max(4, left)}px`;
    menu.style.top  = `${Math.max(4, top)}px`;
    tabContextMenu = menu;

    setTimeout(() => {
      window.addEventListener('pointerdown', onTabMenuOutside, true);
      window.addEventListener('keydown', onTabMenuKey, true);
      window.addEventListener('scroll', closeTabContextMenu, true);
      window.addEventListener('resize', closeTabContextMenu, true);
    }, 0);
  }
  function onTabMenuOutside(e) {
    if (tabContextMenu && !tabContextMenu.contains(e.target)) closeTabContextMenu();
  }
  function onTabMenuKey(e) {
    if (e.key === 'Escape') closeTabContextMenu();
  }
  function closeTabContextMenu() {
    if (!tabContextMenu) return;
    tabContextMenu.remove();
    tabContextMenu = null;
    window.removeEventListener('pointerdown', onTabMenuOutside, true);
    window.removeEventListener('keydown', onTabMenuKey, true);
    window.removeEventListener('scroll', closeTabContextMenu, true);
    window.removeEventListener('resize', closeTabContextMenu, true);
  }

  function newTab() { newTabInGroup(state.workspace.focusedGroupId); }

  // Create a new tab and add it to the specified group. Each group's "+"
  // button calls this with its own group id, so + in pane B creates the
  // tab in pane B regardless of which group is currently focused.
  function newTabInGroup(groupId) {
    const group = state.workspace.groups.find(g => g.id === groupId);
    if (!group) return;
    const n = state.workspace.tabs.length + 1;
    const t = freshTab(n);
    state.workspace.tabs.push(t);
    group.tabIds.push(t.id);
    state.workspace.focusedGroupId = group.id;
    group.activeTabId = t.id;
    renderTabStrip();
    swapEditorToActiveTab();
    renderResultsForActiveTab();
    syncToolbarToActiveTab();
    persistWorkspace();
  }

  function activateTab(id) {
    const group = groupOfTab(id);
    if (!group) return;
    const alreadyActive = group.id === state.workspace.focusedGroupId
                       && group.activeTabId === id;
    if (alreadyActive) return;
    state.workspace.focusedGroupId = group.id;
    group.activeTabId = id;
    renderTabStrip();
    swapEditorToActiveTab();
    renderResultsForActiveTab();
    syncToolbarToActiveTab();
    persistWorkspace();
  }

  // Reorder OR move-across-groups: pull `draggedId` out of its group's
  // tabIds and reinsert adjacent to `targetId`. `before === true` puts
  // it left of the target, false puts it right. When src and dst are
  // different groups the dragged tab becomes the active tab in dst,
  // focus follows the drop, and src dissolves if it ran out of tabs.
  function moveTab(draggedId, targetId, before) {
    if (!draggedId || draggedId === targetId) return;
    const srcGroup = groupOfTab(draggedId);
    const dstGroup = groupOfTab(targetId);
    if (!srcGroup || !dstGroup) return;

    if (srcGroup === dstGroup) {
      // Intra-group reorder.
      const tabIds = srcGroup.tabIds;
      const fromIdx = tabIds.indexOf(draggedId);
      if (fromIdx < 0) return;
      tabIds.splice(fromIdx, 1);
      let toIdx = tabIds.indexOf(targetId);
      if (toIdx < 0) { tabIds.splice(fromIdx, 0, draggedId); return; }
      if (!before) toIdx += 1;
      tabIds.splice(toIdx, 0, draggedId);
      renderTabStrip();
      persistWorkspace();
      return;
    }

    // Cross-group move.
    const srcIdx = srcGroup.tabIds.indexOf(draggedId);
    if (srcIdx < 0) return;
    srcGroup.tabIds.splice(srcIdx, 1);

    let toIdx = dstGroup.tabIds.indexOf(targetId);
    if (toIdx < 0) toIdx = dstGroup.tabIds.length;
    if (!before) toIdx += 1;
    dstGroup.tabIds.splice(toIdx, 0, draggedId);

    // Drop activates the moved tab in dst, and focus follows the drop —
    // the user just told us "I want this tab here", so make it current.
    dstGroup.activeTabId = draggedId;
    state.workspace.focusedGroupId = dstGroup.id;

    // Recover src: if the active tab moved away, fall back to the first
    // remaining; if src is now empty, dissolve it (always allowed in
    // multi-group state since the user explicitly moved its last tab
    // out — symmetric to closing the last tab in a non-only group).
    if (srcGroup.tabIds.length === 0) {
      dissolveGroup(srcGroup.id);
    } else if (srcGroup.activeTabId === draggedId) {
      srcGroup.activeTabId = srcGroup.tabIds[0];
    }

    renderTabStrip();
    swapEditorToActiveTab();
    renderResultsForActiveTab();
    syncToolbarToActiveTab();
    persistWorkspace();
  }

  function clearDropIndicators() {
    // Sweep across every group's tab strip so cross-group drag indicators
    // are cleared too (no-op for groups that don't have any).
    document.querySelectorAll('.tab-strip .tab.drop-before, .tab-strip .tab.drop-after')
      .forEach(el => el.classList.remove('drop-before', 'drop-after'));
  }

  async function closeTab(id) {
    const tab = state.workspace.tabs.find(t => t.id === id);
    if (!tab) return;
    // Pinned tabs are protected — unpin first to close.
    if (tab.pinned) return;
    if (tab.sql && tab.sql !== tab.sqlOfLastRun) {
      const ok = await confirmModal('Close tab', `"${tab.name}" has unsaved changes since last run. Close anyway?`);
      if (!ok) return;
    }
    // Dispose any Monaco model attached to this tab.
    if (monacoModels.has(id)) {
      monacoModels.get(id).dispose();
      monacoModels.delete(id);
    }
    // Drop the saved result for this tab from IDB. Best-effort.
    IDB.deleteResult(workspaceName(), id).catch(err =>
      console.warn(`Couldn't delete IDB result for tab ${id}:`, err));
    const containingGroup = groupOfTab(id);
    state.workspace.tabs = state.workspace.tabs.filter(t => t.id !== id);
    // Record a tombstone so the multi-window merge in persistWorkspace
    // doesn't resurrect this tab from a stale on-disk snapshot.
    if (!Array.isArray(state.workspace.deletedTabIds)) state.workspace.deletedTabIds = [];
    state.workspace.deletedTabIds.push(id);
    if (containingGroup) {
      containingGroup.tabIds = containingGroup.tabIds.filter(tid => tid !== id);
      if (containingGroup.tabIds.length === 0) {
        if (state.workspace.groups.length === 1) {
          // Last tab in the only group → seed a fresh tab so the user
          // never lands in an empty workspace.
          const fresh = freshTab(1);
          state.workspace.tabs.push(fresh);
          containingGroup.tabIds.push(fresh.id);
          containingGroup.activeTabId = fresh.id;
        } else {
          // Multi-group: an emptied group dissolves and the surviving
          // group becomes (or stays) focused.
          dissolveGroup(containingGroup.id);
        }
      } else if (containingGroup.activeTabId === id) {
        containingGroup.activeTabId = containingGroup.tabIds[0];
      }
    }
    renderTabStrip();
    swapEditorToActiveTab();
    renderResultsForActiveTab();
    syncToolbarToActiveTab();
    persistWorkspace();
  }

  // ===== Editor =====
  // We keep one Monaco editor *per group* and swap models per tab. Each tab has
  // its own ITextModel, so cursor position, scroll, and undo stack survive
  // tab switches. Until Monaco loads we use a textarea fallback (also one
  // per group). Models stay 1:1 with tabs and are shared across editors —
  // a tab lives in exactly one group at a time, so its model is attached
  // to at most one editor at a time.
  const monacoEditorsByGroup = new Map();        // groupId -> IStandaloneCodeEditor
  const monacoModels = new Map();                // tabId -> ITextModel
  const fallbackTextareasByGroup = new Map();    // groupId -> HTMLTextAreaElement

  // Convenience accessors — reads/writes that used to operate on the lone
  // singleton route through the focused group. When a second group exists
  // (step 3b+) the Run button, keyboard shortcuts, and selection capture
  // continue to target whichever pane the user last interacted with.
  function focusedMonacoEditor() {
    return monacoEditorsByGroup.get(state.workspace.focusedGroupId) || null;
  }
  function focusedFallbackTextarea() {
    return fallbackTextareasByGroup.get(state.workspace.focusedGroupId) || null;
  }

  function getOrCreateModel(tab) {
    if (!window.monaco) return null;
    let model = monacoModels.get(tab.id);
    if (!model) {
      model = monaco.editor.createModel(tab.sql || '', 'sql');
      // Per-model listener so cross-tab switches don't pile up listeners.
      model.onDidChangeContent(() => {
        const t = state.workspace.tabs.find(x => monacoModels.get(x.id) === model);
        if (!t) return;
        t.sql = model.getValue();
        renderTabStrip();         // dirty marker may flip
        scheduleSave();
      });
      monacoModels.set(tab.id, model);
    }
    return model;
  }

  function swapEditorToActiveTab() {
    for (const g of state.workspace.groups) swapEditorForGroup(g);
  }

  // Attach the model for `group.activeTabId` to whichever editor (Monaco
  // or fallback) belongs to that group. Focuses the editor only if it
  // belongs to the focused group, so split-pane swaps don't yank the
  // caret away from where the user is typing.
  function swapEditorForGroup(group) {
    const tab = state.workspace.tabs.find(t => t.id === group.activeTabId);
    if (!tab) return;
    const editor = monacoEditorsByGroup.get(group.id);
    if (editor) {
      editor.setModel(getOrCreateModel(tab));
      if (group.id === state.workspace.focusedGroupId) editor.focus();
      return;
    }
    const fb = fallbackTextareasByGroup.get(group.id);
    if (fb) fb.value = tab.sql;
  }

  // Stand up the textarea fallback inside one group's editor host. Each
  // group gets its own textarea instance keyed by groupId — tabs in that
  // group share it the same way they share the group's Monaco editor.
  function bootFallbackForGroup(groupId) {
    if (fallbackTextareasByGroup.has(groupId)) return;   // already booted
    const groupEl = document.querySelector(`.editor-group[data-group-id="${groupId}"]`);
    if (!groupEl) return;
    const host = editorHostEl(groupEl);
    host.innerHTML = '';
    const ta = document.createElement('textarea');
    ta.className = 'editor-fallback';
    ta.spellcheck = false;
    ta.autocomplete = 'off';
    ta.autocapitalize = 'off';
    host.appendChild(ta);

    ta.addEventListener('keydown', (e) => {
      if (e.key === 'Tab' && !e.ctrlKey && !e.metaKey) {
        e.preventDefault();
        const start = ta.selectionStart;
        const end = ta.selectionEnd;
        ta.value = ta.value.slice(0, start) + '  ' + ta.value.slice(end);
        ta.selectionStart = ta.selectionEnd = start + 2;
      }
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
        e.preventDefault();
        run();
      }
    });
    ta.addEventListener('input', () => {
      // Edits go to whichever tab is active in *this* group — not the
      // focused group, in case the user is typing into a non-focused pane.
      const group = state.workspace.groups.find(g => g.id === groupId);
      if (!group) return;
      const tab = state.workspace.tabs.find(t => t.id === group.activeTabId);
      if (!tab) return;
      tab.sql = ta.value;
      renderTabStrip();
      scheduleSave();
    });

    fallbackTextareasByGroup.set(groupId, ta);
  }

  // Stand up a Monaco editor for one group. Replaces the group's textarea
  // fallback with a real editor and stashes it in `monacoEditorsByGroup`
  // so `swapEditorToActiveTab` / `getEditorSelection` can find it. Called
  // once per group after Monaco's loader resolves; called again per new
  // group when the user splits the layout.
  function bootMonacoForGroup(groupId) {
    if (!window.monaco) return;
    if (monacoEditorsByGroup.has(groupId)) return;       // already booted
    const groupEl = document.querySelector(`.editor-group[data-group-id="${groupId}"]`);
    if (!groupEl) return;
    const host = editorHostEl(groupEl);
    host.innerHTML = '';
    fallbackTextareasByGroup.delete(groupId);

    const editor = monaco.editor.create(host, {
      model: null,
      language: 'sql',
      theme: document.documentElement.getAttribute('data-theme') === 'dark' ? 'vs-dark' : 'vs',
      automaticLayout: true,
      minimap: { enabled: false },
      fontFamily: 'ui-monospace, SFMono-Regular, "JetBrains Mono", Consolas, "Liberation Mono", Menlo, monospace',
      fontSize: 14,
      scrollBeyondLastLine: false,
      wordWrap: 'on',
      renderLineHighlight: 'all',
      tabSize: 2,
      insertSpaces: true,

      // Render completion / hover / parameter-hint widgets via
      // position: fixed so they escape the editor pane's
      // `overflow: hidden`. Without this, the popup gets clipped at
      // the editor/results boundary and parts of it appear behind
      // the results table when the cursor is near the bottom of the
      // editor pane.
      fixedOverflowWidgets: true,

      // Disable Monaco's word-based suggestion fallback. Default since
      // Monaco 0.34 is 'matchingDocuments', which scrapes words from
      // every open SQL tab and offers them as plain Text completions —
      // so a tab containing `system_models` makes the column name
      // `backend` surface in unrelated zones (e.g. typing `b` at the
      // start of an empty file). The DatumIngest language server is
      // schema-aware and zone-aware; the word fallback only adds noise
      // and out-of-context column suggestions.
      wordBasedSuggestions: 'off',
    });

    // Ctrl/Cmd+Enter is handled at window-level (see boot()) so it
    // routes by `focusedGroupId` — which the pane's pointerdown handler
    // updates on every click — instead of by Monaco's per-editor
    // keyboard-focus, which can desync from the user's intent if they
    // clicked the pane's chrome rather than its editor host.
    editor.onDidFocusEditorWidget(() => {
      if (state.workspace.focusedGroupId !== groupId) {
        state.workspace.focusedGroupId = groupId;
        syncToolbarToActiveTab();
      }
    });

    monacoEditorsByGroup.set(groupId, editor);
  }

  function initMonaco() {
    require.config({
      paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.50.0/min/vs' },
    });

    // Cross-origin worker workaround: load workers via a same-origin Blob
    // URL that defers to the CDN. Without this, Monaco silently runs without
    // workers (still functional, but tokenization happens on the main thread).
    self.MonacoEnvironment = {
      getWorkerUrl: function () {
        return URL.createObjectURL(new Blob([
          `self.MonacoEnvironment = { baseUrl: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.50.0/min/' };` +
          `importScripts('https://cdn.jsdelivr.net/npm/monaco-editor@0.50.0/min/vs/base/worker/workerMain.js');`
        ], { type: 'text/javascript' }));
      },
    };

    require(['vs/editor/editor.main'], () => {
      // Boot a Monaco editor inside every existing group, then wire
      // language services once (providers are registered globally).
      for (const g of state.workspace.groups) bootMonacoForGroup(g.id);
      registerLanguageProviders();
      swapEditorToActiveTab();
    }, (err) => {
      console.warn('Monaco failed to load; staying on textarea fallback.', err);
    });
  }

  // ===== Language services (completion / hover / diagnostics) =====
  // The DatumIngest LanguageService is exposed as three POST endpoints; each
  // Monaco provider just calls the matching one and translates the response.
  // Server CompletionItemKind values are LSP-aligned (Keyword=14, Struct=22,
  // Field=5, Function=3, TypeParameter=25); Monaco uses a different numeric
  // enum so we map by string name to avoid hard-coding magic numbers.

  // Tracks the latest diagnose-request id per model; older responses are
  // discarded so a slow server reply can't paint stale markers.
  const diagnoseGenByModelId = new Map();

  function registerLanguageProviders() {
    if (!window.monaco) return;

    // Replace Monaco's built-in 'sql' tokenizer with the DatumIngest grammar
    // so backtick template strings, ${…} splices, and dialect-specific
    // keywords/types render with the right colors. Fetch is async; if it
    // fails we just keep the default tokenizer (everything still works,
    // backticks just render as identifiers).
    // Models created before this resolves are tokenized with Monaco's
    // built-in 'sql' grammar, which doesn't know about backtick template
    // strings — they render as a sequence of identifiers/operators with
    // no error. Swapping the provider does NOT retokenize existing
    // models, so we re-set each model's language to 'sql' to force a
    // fresh tokenization pass with the new grammar.
    fetch('/api/lang/grammar')
      .then(response => response.ok ? response.json() : null)
      .then(grammar => {
        if (!grammar) return;
        monaco.languages.setMonarchTokensProvider('sql', grammar);
        for (const model of monaco.editor.getModels()) {
          if (model.getLanguageId() === 'sql') {
            monaco.editor.setModelLanguage(model, 'sql');
          }
        }
      })
      .catch(() => { /* keep built-in tokenizer */ });

    // Override Monaco's default 'sql' language configuration. The defaults
    // treat `'` as an unconditional auto-closing pair, which inserts an
    // unwanted second quote when the user types `'` inside a backtick
    // template (visually breaking the string). They also use a default
    // word pattern that excludes `@` and `$`, so double-clicking `@xyz`
    // selects only `xyz`. Both are dialect mismatches.
    monaco.languages.setLanguageConfiguration('sql', {
      // Treat `@var` / `$param` / `__internal` as single words for
      // double-click selection, word-jumps, and rename targeting.
      wordPattern: /[@$]?[a-zA-Z_]\w*/,

      comments: {
        lineComment: '--',
        blockComment: ['/*', '*/'],
      },

      brackets: [
        ['(', ')'],
        ['[', ']'],
        ['{', '}'],
      ],

      // Auto-close pairs are suppressed when the cursor is in a string or
      // comment context (per the Monarch tokenizer's classes). That stops
      // `'` from auto-closing inside a backtick template body — the
      // tokenizer tags the body as `string`, so `notIn: ['string']`
      // applies.
      autoClosingPairs: [
        { open: '(', close: ')' },
        { open: '[', close: ']' },
        { open: '{', close: '}' },
        { open: "'", close: "'", notIn: ['string', 'comment'] },
        { open: '"', close: '"', notIn: ['string', 'comment'] },
        { open: '`', close: '`', notIn: ['string', 'comment'] },
      ],

      surroundingPairs: [
        { open: '(', close: ')' },
        { open: '[', close: ']' },
        { open: '{', close: '}' },
        { open: "'", close: "'" },
        { open: '"', close: '"' },
        { open: '`', close: '`' },
      ],
    });

    monaco.languages.registerCompletionItemProvider('sql', {
      // '\n' was here too, but it caused the suggest popup to appear every
      // time the user pressed Enter for a fresh line — and Monaco then
      // selects the first match (alphabetically near 'ALTER'), so an
      // unsuspecting Enter to confirm gobbles the wrong word. Trigger only
      // on intent-bearing characters: word boundary (space) and member
      // access (dot). Argument separators ('(' and ',') are NOT triggers
      // here — those fire signature help instead, and having both fire on
      // the same character causes the completions popup to render in
      // front of and obscure the signature tooltip. Inside an arg list
      // the user can still pull up completions explicitly via Ctrl+Space,
      // or just start typing — Monaco's quick-suggestions kick in on
      // letters.
      triggerCharacters: [' ', '.'],
      provideCompletionItems: async (model, position) => {
        const sql = model.getValue();
        const offset = model.getOffsetAt(position);
        let items;
        try {
          items = await postJson('/api/lang/complete', { sql, offset });
        } catch { return { suggestions: [] }; }
        if (!Array.isArray(items)) return { suggestions: [] };

        // Replace the word currently under the cursor (Monaco fills this
        // range with the completion's insertText). Without an explicit range
        // Monaco picks one heuristically and sometimes keeps stray prefix.
        const word = model.getWordUntilPosition(position);
        const range = new monaco.Range(
          position.lineNumber, word.startColumn,
          position.lineNumber, word.endColumn);

        return {
          suggestions: items.map(it => ({
            label: it.label,
            kind: completionKindFromName(it.kind),
            insertText: it.insertText ?? it.label,
            detail: it.detail ?? undefined,
            documentation: it.documentation
              ? { value: it.documentation, isTrusted: false }
              : undefined,
            sortText: typeof it.sortOrder === 'number'
              ? String(it.sortOrder).padStart(8, '0')
              : undefined,
            range,
          })),
        };
      },
    });

    monaco.languages.registerHoverProvider('sql', {
      provideHover: async (model, position) => {
        const sql = model.getValue();
        const offset = model.getOffsetAt(position);
        let hover;
        try {
          hover = await postJson('/api/lang/hover', { sql, offset });
        } catch { return null; }
        if (!hover || !hover.contents) return null;
        return {
          range: lspRangeToMonaco(
            hover.startLine, hover.startColumn, hover.endLine, hover.endColumn),
          contents: [{ value: hover.contents }],
        };
      },
    });

    // Signature help (parameter hints): the floating tooltip that stays
    // visible while the user types arguments inside a function call's
    // parens. Triggers on `(` and `,` so the active-parameter highlight
    // updates as the user moves between arguments. Re-trigger characters
    // re-fire the request without dismissing the tooltip.
    monaco.languages.registerSignatureHelpProvider('sql', {
      signatureHelpTriggerCharacters: ['(', ','],
      signatureHelpRetriggerCharacters: [','],
      provideSignatureHelp: async (model, position) => {
        const sql = model.getValue();
        const offset = model.getOffsetAt(position);
        let sig;
        try {
          sig = await postJson('/api/lang/signature', { sql, offset });
        } catch { return null; }
        if (!sig || !sig.signatures || sig.signatures.length === 0) return null;
        return {
          value: {
            signatures: sig.signatures.map(s => ({
              label: s.label,
              documentation: s.documentation
                ? { value: s.documentation, isTrusted: false }
                : undefined,
              parameters: (s.parameters || []).map(p => ({
                label: p.label,
                documentation: p.documentation
                  ? { value: p.documentation, isTrusted: false }
                  : undefined,
              })),
            })),
            activeSignature: sig.activeSignature ?? 0,
            activeParameter: sig.activeParameter ?? 0,
          },
          dispose: () => {},
        };
      },
    });

    // Diagnostics — debounced. Re-run on any model content change. Markers
    // are written directly with monaco.editor.setModelMarkers under the
    // owner key 'datum-ingest' so we own the namespace cleanly.
    let diagnoseTimer = null;
    function scheduleDiagnose(model) {
      if (diagnoseTimer) clearTimeout(diagnoseTimer);
      diagnoseTimer = setTimeout(() => runDiagnose(model), 250);
    }
    async function runDiagnose(model) {
      if (model.isDisposed()) return;
      const gen = (diagnoseGenByModelId.get(model.id) || 0) + 1;
      diagnoseGenByModelId.set(model.id, gen);
      const sql = model.getValue();
      let diags;
      try {
        diags = await postJson('/api/lang/diagnose', { sql });
      } catch { return; }
      if (model.isDisposed()) return;
      if (diagnoseGenByModelId.get(model.id) !== gen) return;     // stale
      const markers = (Array.isArray(diags) ? diags : []).map(d => ({
        severity: severityFromName(d.severity),
        message: d.message,
        startLineNumber: d.startLine + 1,
        startColumn:     d.startColumn + 1,
        endLineNumber:   d.endLine   + 1,
        endColumn:       d.endColumn + 1,
        source: 'datum-ingest',
      }));
      monaco.editor.setModelMarkers(model, 'datum-ingest', markers);
    }

    // Hook every model that exists or comes into being. Diagnostics fire on
    // each change after a 250ms debounce.
    function attachDiagnoseToModel(model) {
      if (model.getLanguageId() !== 'sql') return;
      scheduleDiagnose(model);
      model.onDidChangeContent(() => scheduleDiagnose(model));
    }
    monaco.editor.getModels().forEach(attachDiagnoseToModel);
    monaco.editor.onDidCreateModel(attachDiagnoseToModel);
  }

  // Map server CompletionItemKind enum-name string to Monaco's enum.
  function completionKindFromName(name) {
    const K = monaco.languages.CompletionItemKind;
    switch (name) {
      case 'Keyword':       return K.Keyword;
      case 'Table':         return K.Struct;       // closest visual match
      case 'Column':        return K.Field;
      case 'Function':      return K.Function;
      case 'TypeParameter': return K.TypeParameter;
      case 'Variable':      return K.Variable;     // procedural @vars
      default:              return K.Text;
    }
  }
  function severityFromName(name) {
    const S = monaco.MarkerSeverity;
    switch (name) {
      case 'Error':       return S.Error;
      case 'Warning':     return S.Warning;
      case 'Information': return S.Info;
      default:            return S.Info;
    }
  }
  function lspRangeToMonaco(sl, sc, el, ec) {
    return new monaco.Range(sl + 1, sc + 1, el + 1, ec + 1);
  }
  async function postJson(url, body) {
    const r = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    return r.json();
  }

  // ===== Results =====
  // Top-level entry: re-render every group's results pane. Each group
  // shows results for its own activeTabId, so switching tabs in one
  // pane doesn't disturb the other. IDB reads are independent so we
  // run them concurrently.
  function renderResultsForActiveTab() {
    return Promise.all(state.workspace.groups.map(g => renderResultsForGroup(g)));
  }

  async function renderResultsForGroup(group) {
    const groupEl = groupElementById(group.id);
    if (!groupEl) return;
    const tab = state.workspace.tabs.find(t => t.id === group.activeTabId);
    const results = resultsPaneEl(groupEl);
    const elapsed = elapsedEl(groupEl);
    // Revoke only THIS group's previous URLs. With two panes rendering
    // concurrently, a global revoke would kill the other group's
    // just-created URLs.
    revokeMediaObjectUrlsForGroup(group.id);
    results.innerHTML = '';
    elapsed.textContent = '';
    if (!tab) return;

    // The mediaUrlCollector swap below keeps blob-URL ownership pinned to
    // this group across the synchronous render call chain (renderResult →
    // renderCell → dataB64ToBlobUrl). We wrap each render entry-point
    // because awaiting IDB in this function would otherwise let a sibling
    // group's render clobber the collector before our sync work runs.
    let groupUrls = mediaObjectUrlsByGroup.get(group.id);
    if (!groupUrls) {
      groupUrls = [];
      mediaObjectUrlsByGroup.set(group.id, groupUrls);
    }
    const renderWithCollector = (fn) => {
      const prev = mediaUrlCollector;
      mediaUrlCollector = groupUrls;
      try { fn(); } finally { mediaUrlCollector = prev; }
    };

    // If THIS tab has an in-flight query, render the streaming view from
    // the per-tab accumulator (rebuild chunks / partial rows so the user
    // can see progress on switch-back). The live ticker resumes painting
    // on its next 250ms tick once the tab is the active tab in this group.
    if (tab.running && tab.runningRes) {
      renderWithCollector(() => renderRunningTab(tab, results));
      return;
    }

    // tab.lastResult is undefined when hydrated from localStorage and not yet
    // fetched from IDB. Show a placeholder while we resolve it; guard against
    // the user switching tabs in this group before the read returns.
    if (tab.lastResult === undefined) {
      const loading = document.createElement('div');
      loading.className = 'meta';
      loading.textContent = 'Loading saved result…';
      results.appendChild(loading);
      const captureTabId = tab.id;
      const captureWs = workspaceName();
      const captureGroupId = group.id;
      const stillActive = () => {
        if (workspaceName() !== captureWs) return false;
        const liveGroup = state.workspace.groups.find(g => g.id === captureGroupId);
        return !!liveGroup && liveGroup.activeTabId === captureTabId;
      };
      try {
        const r = await IDB.loadResult(captureWs, captureTabId);
        if (!stillActive()) return;                          // user moved on
        tab.lastResult = r;                                   // cache, even if null
      } catch (err) {
        if (!stillActive()) return;
        tab.lastResult = null;
        results.innerHTML = '';
        const errDiv = document.createElement('div');
        errDiv.className = 'error';
        errDiv.textContent = `Couldn't load saved result: ${err.message}`;
        results.appendChild(errDiv);
        return;
      }
      results.innerHTML = '';
    }

    if (!tab.lastResult) {
      const meta = document.createElement('div');
      meta.className = 'meta';
      meta.textContent = tab.lastRunAt
        ? '(saved result not found — re-run to see it)'
        : 'No results yet. Press Run.';
      results.appendChild(meta);
      return;
    }
    renderWithCollector(() => renderResult(tab.lastResult, results));
    const r = tab.lastResult;
    if (!r.error) {
      const rowText = `${r.rowCount} ${r.rowCount === 1 ? 'row' : 'rows'}`;
      const timeText = typeof r.elapsedMs === 'number' ? `${(r.elapsedMs / 1000).toFixed(2)} s` : '';
      elapsed.textContent = timeText ? `${rowText} · ${timeText}` : rowText;
    }
  }

  // Renders the partial state of a query that's still in flight on this
  // tab — invoked when the user switches back to a running tab. Rebuilds
  // any streaming chunks from the accumulator and lists how many rows
  // have arrived. The live ticker on the `.elapsed` slot paints the elapsed time
  // from its next 250ms tick.
  function renderRunningTab(tab, container) {
    const res = tab.runningRes;
    const meta = document.createElement('div');
    meta.className = 'meta';
    meta.textContent = `Running… (Esc to cancel) · ${res.rowCount.toLocaleString()} ${res.rowCount === 1 ? 'row' : 'rows'} so far`;
    container.appendChild(meta);

    if (res.chunks && res.chunks.length > 0) {
      // Rebuild the live token-stream pane from the accumulated chunks.
      // The live append path in run() will continue from this point —
      // checking `streamPane` truthy in the chunk handler now hits the
      // newly-attached element.
      const header = document.createElement('div');
      header.className = 'meta stream-header';
      const model = res.chunks[0].model;
      header.textContent = `streaming from models.${model}`;
      container.appendChild(header);
      const pre = document.createElement('pre');
      pre.className = 'streaming-output';
      for (const c of res.chunks) {
        pre.appendChild(document.createTextNode(c.text));
      }
      pre.scrollTop = pre.scrollHeight;
      container.appendChild(pre);
    }
  }

  // Refresh every group's toolbar to match its own active tab. Each
  // group's toolbar reflects ITS active tab's settings — Run button
  // label, maxRows, trace — so the two panes stay independent. The
  // header status only reflects the focused group.
  function syncToolbarToActiveTab() {
    for (const g of state.workspace.groups) syncToolbarForGroup(g);
    const status = document.getElementById('status');
    const tab = activeTab();
    status.textContent = (tab && tab.running) ? 'running… (Esc to cancel)' : '';
    // Header icons reflect focused-group state (which can change when
    // the user clicks into a non-focused pane).
    updateSplitButtonIcon();
    refreshFocusedGroupHighlight();
  }

  function syncToolbarForGroup(group) {
    const groupEl = groupElementById(group.id);
    if (!groupEl) return;
    const tab = state.workspace.tabs.find(t => t.id === group.activeTabId);
    const runBtn = runBtnEl(groupEl);
    const maxRowsInput = maxRowsInputEl(groupEl);
    const traceToggle = traceToggleEl(groupEl);
    if (!tab) {
      runBtn.textContent = 'Run';
      runBtn.disabled = true;
      maxRowsInput.disabled = true;
      traceToggle.disabled = true;
      return;
    }
    runBtn.disabled = false;
    runBtn.textContent = tab.running ? 'Cancel' : 'Run';
    runBtn.title = tab.running
      ? 'Stop the in-flight query for this tab (Esc)'
      : 'Run the SQL in this tab (Ctrl/⌘+Enter)';
    maxRowsInput.disabled = false;
    traceToggle.disabled = false;
    maxRowsInput.value = tab.maxRows || 200;
    traceToggle.checked = tab.trace === true;
  }

  /// Renders one result set (one row-producing statement's output) as a
  /// section: optional "Result N" label when there's more than one,
  /// truncation warning, and the actual data table. Pulled out of
  /// renderResult so multi-statement scripts can stamp out one section
  /// per `schema` event.
  function renderResultSet(set, container, label) {
    if (label !== null && label !== undefined) {
      const heading = document.createElement('div');
      heading.className = 'meta result-set-label';
      const rowCount = set.rowCount ?? (set.rows ? set.rows.length : 0);
      heading.textContent =
        `Result ${label} · ${rowCount.toLocaleString()} ${rowCount === 1 ? 'row' : 'rows'}`;
      container.appendChild(heading);
    }

    if (set.truncated) {
      const w = document.createElement('div');
      w.className = 'warn';
      w.textContent = `⚠ Truncated at ${set.rowCount} rows (raise the max rows control to see more).`;
      container.appendChild(w);
    }

    if (!set.schema || set.schema.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'meta';
      empty.textContent = '(no columns)';
      container.appendChild(empty);
      return;
    }

    const table = document.createElement('table');
    table.className = 'results';
    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    for (const col of set.schema) {
      const th = document.createElement('th');
      th.appendChild(document.createTextNode(col.name));
      const k = document.createElement('span');
      k.className = 'kind';
      k.textContent = col.isArray ? `Array<${col.kind}>` : col.kind;
      th.appendChild(k);
      headRow.appendChild(th);
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    for (const row of set.rows) {
      const tr = document.createElement('tr');
      for (const cell of row) tr.appendChild(renderCell(cell));
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    container.appendChild(table);
  }

  function renderResult(res, container) {
    if (res.error) {
      const err = document.createElement('div');
      err.className = 'error';
      err.textContent = res.error + (res.detail ? '\n\n' + res.detail : '');
      container.appendChild(err);
      return;
    }

    // One entry per row-producing statement. Each renders as its own
    // table so multi-statement scripts don't conflate schemas. The "Result N"
    // heading only appears when there's more than one — single-statement
    // runs render exactly as they did before.
    const sets = res.resultSets ?? [];
    sets.forEach((set, index) => {
      renderResultSet(set, container, sets.length > 1 ? index + 1 : null);
    });
    if (sets.length === 0 && !res.error) {
      const empty = document.createElement('div');
      empty.className = 'meta';
      empty.textContent = '(no rows)';
      container.appendChild(empty);
    }

    // Engine trace, when the user enabled the toggle and the engine emitted
    // anything. Collapsed by default; the summary shows the line count so
    // users can decide whether to expand.
    if (typeof res.trace === 'string' && res.trace.length > 0) {
      const lineCount = res.trace.split('\n').filter(l => l.length > 0).length;
      const details = document.createElement('details');
      details.className = 'trace';
      const summary = document.createElement('summary');
      summary.textContent = `Execution trace (${lineCount.toLocaleString()} ${lineCount === 1 ? 'line' : 'lines'})`;
      details.appendChild(summary);
      const pre = document.createElement('pre');
      pre.textContent = res.trace;
      details.appendChild(pre);
      container.appendChild(details);
    }
  }

  // Object URLs created for the currently-rendered media cells. Tracked so
  // we can revoke them on the next render — without revocation the browser
  // would hold the underlying blobs alive indefinitely (a few MB per image
  // adds up across runs).
  // Per-group URL tracking. With two panes rendering concurrently, a
  // single global list led group B's render to revoke group A's
  // freshly-created blob URLs out from under group A's <img> elements,
  // leaving them blank while the byte-count caption survived. Each
  // group now owns its own URL list.
  const mediaObjectUrlsByGroup = new Map();
  // The collector array dataB64ToBlobUrl pushes into. Callers that
  // render media cells set this to the right group's list immediately
  // before the synchronous rendering call and restore it after. Using
  // a module-level flag works only because the cell-rendering call
  // chain (renderResult → renderCell → dataB64ToBlobUrl) is fully
  // synchronous; no await separates the assignment from the use.
  let mediaUrlCollector = null;
  function revokeMediaObjectUrlsForGroup(groupId) {
    const list = mediaObjectUrlsByGroup.get(groupId);
    if (!list) return;
    for (const u of list) URL.revokeObjectURL(u);
    mediaObjectUrlsByGroup.set(groupId, []);
  }

  // Decode base64 → Blob → object URL. Used in place of `data:` URLs for
  // image / audio / video cells: blob URLs aren't subject to Chromium's
  // ~2 MB URL-length limit, so right-click "Open image in new tab" works
  // reliably regardless of the image's encoded size.
  function dataB64ToBlobUrl(b64, mime) {
    const bin = atob(b64);
    const len = bin.length;
    const arr = new Uint8Array(len);
    for (let i = 0; i < len; i++) arr[i] = bin.charCodeAt(i);
    const url = URL.createObjectURL(new Blob([arr], { type: mime }));
    if (mediaUrlCollector) mediaUrlCollector.push(url);
    return url;
  }

  // Hover-revealed copy button. `getText` is a thunk so JSON cells can copy
  // their pretty-printed display text (which the renderer computes once
  // and stores on the <pre>) without us having to capture it eagerly.
  // Skipped for media cells — copying base64 image bytes isn't useful and
  // would clobber the clipboard with megabytes of text.
  const COPY_ICON_SVG = '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="1.6">'
    + '<rect x="9" y="9" width="11" height="11" rx="1.5"></rect>'
    + '<path d="M5 15V5a2 2 0 0 1 2-2h10"></path></svg>';
  const CHECK_ICON_SVG = '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2">'
    + '<polyline points="5 12 10 17 19 7"></polyline></svg>';

  function addCopyButton(td, getText) {
    const btn = document.createElement('button');
    btn.className = 'cell-copy';
    btn.type = 'button';
    btn.title = 'Copy';
    btn.setAttribute('aria-label', 'Copy cell value');
    btn.innerHTML = COPY_ICON_SVG;
    btn.addEventListener('click', async (e) => {
      // Don't bubble into the cell — image cells handle clicks for the
      // lightbox, and we don't want a stray copy click to also expand.
      e.stopPropagation();
      try {
        await navigator.clipboard.writeText(getText());
        btn.classList.add('copied');
        btn.innerHTML = CHECK_ICON_SVG;
        setTimeout(() => {
          btn.classList.remove('copied');
          btn.innerHTML = COPY_ICON_SVG;
        }, 900);
      } catch (err) {
        console.warn('[DatumIngest] clipboard.writeText failed:', err);
      }
    });
    td.appendChild(btn);
  }

  // renderJsonNode/Object/Array — see ./json-render.ts

  function renderCell(cell) {
    const td = document.createElement('td');
    if (cell.kind === 'null') {
      td.classList.add('null');
      td.textContent = 'NULL';
      addCopyButton(td, () => 'NULL');
      return td;
    }
    // Array<Image> cell: emit one <img> per element in a flex grid. Each
    // thumbnail is independently clickable for the lightbox. Items are
    // {mime, dataB64} pairs; we currently only construct image arrays
    // server-side, but the loop tolerates other mimes by skipping them.
    if (cell.kind === 'media_array' && Array.isArray(cell.items)) {
      const grid = document.createElement('div');
      grid.className = 'media-array';
      for (const item of cell.items) {
        if (!item || !item.mime || !item.mime.startsWith('image/')) continue;
        const url = dataB64ToBlobUrl(item.dataB64, item.mime);
        const img = document.createElement('img');
        img.src = url; img.alt = '';
        img.title = 'Click to expand';
        img.addEventListener('click', () => openImageLightbox(url));
        grid.appendChild(img);
      }
      td.appendChild(grid);
      const note = document.createElement('div');
      note.className = 'blob';
      note.textContent = `${cell.items.length} ${cell.items.length === 1 ? 'image' : 'images'}`;
      td.appendChild(note);
      return td;
    }
    if (cell.kind === 'media' && cell.mime) {
      // Prefer blob URLs over `data:` URLs: Chrome disables "Open image in
      // new tab" for data URLs longer than ~2 MB (so a large image renders
      // fine inline but loses the context-menu option), while blob URLs
      // navigate cleanly at any size.
      const url = dataB64ToBlobUrl(cell.dataB64, cell.mime);
      let media;
      if (cell.mime.startsWith('image/')) {
        media = document.createElement('img');
        media.src = url; media.alt = '';
        media.title = 'Click to expand';
        media.addEventListener('click', () => openImageLightbox(url));
      } else if (cell.mime.startsWith('audio/')) {
        media = document.createElement('audio'); media.controls = true; media.src = url;
      } else if (cell.mime.startsWith('video/')) {
        media = document.createElement('video'); media.controls = true; media.src = url;
      } else {
        media = document.createElement('span'); media.textContent = `<${cell.mime}>`;
      }
      td.appendChild(media);
      const note = document.createElement('div');
      note.className = 'blob';
      const bytes = Math.floor((cell.dataB64.length * 3) / 4);
      note.textContent = `${cell.mime} · ${bytes.toLocaleString()} bytes`;
      td.appendChild(note);
      return td;
    }
    if (cell.kind === 'json') {
      // Server already decoded CBOR → JSON text. Render as a collapsible
      // tree so deep structures stay inspectable without taking over the
      // cell. If the text doesn't parse (shouldn't happen — the server
      // only emits this kind on successful decode), fall back to a flat
      // pretty-printed pre.
      const text = cell.text ?? '';
      let parsed;
      try { parsed = JSON.parse(text); }
      catch {
        const pre = document.createElement('pre');
        pre.className = 'json';
        pre.textContent = text;
        td.appendChild(pre);
        addCopyButton(td, () => pre.textContent);
        return td;
      }
      const wrap = document.createElement('div');
      wrap.className = 'json-tree';
      wrap.appendChild(renderJsonNode(parsed));
      td.appendChild(wrap);
      // Copy returns the canonical pretty-printed form so users get the
      // same text whether the tree is expanded or collapsed.
      addCopyButton(td, () => JSON.stringify(parsed, null, 2));
      return td;
    }
    const pre = document.createElement('pre');
    pre.textContent = cell.text ?? '';
    td.appendChild(pre);
    addCopyButton(td, () => pre.textContent);
    return td;
  }

  // ===== Run =====
  // Read the current editor selection. Monaco returns "" for an empty
  // selection (caret only). The textarea fallback uses selectionStart/End.
  // Returns the selected text (untrimmed) or "" when nothing is highlighted.
  function getEditorSelection() {
    const editor = focusedMonacoEditor();
    if (editor) {
      const sel = editor.getSelection();
      if (!sel || sel.isEmpty()) return '';
      return editor.getModel().getValueInRange(sel);
    }
    const fb = focusedFallbackTextarea();
    if (fb) {
      const { selectionStart: a, selectionEnd: b, value } = fb;
      if (typeof a === 'number' && typeof b === 'number' && b > a) {
        return value.slice(a, b);
      }
    }
    return '';
  }

  // run() launches a query for the currently-active tab. Each tab keeps
  // its own running state on the tab object (tab.running, tab.runningRes,
  // tab.abortController, tab.liveTickHandle) so multiple tabs can have
  // queries in flight at once. The server queues them via its query lock
  // today, but the UX correctly reflects "running per tab" regardless.
  async function run() {
    const tab = activeTab();
    if (!tab) return;
    if (tab.running) return;  // already running on THIS tab — Cancel button handles abort

    // If text is highlighted, run just that fragment; otherwise run the
    // whole tab. Trim AFTER picking so leading/trailing whitespace in a
    // selection doesn't make us silently fall back to the full tab.
    const selection = getEditorSelection();
    const isPartial = selection.trim().length > 0;
    const sql = (isPartial ? selection : tab.sql).trim();
    if (!sql) return;

    // Tab-scoped run state. The closure captures `tab` so handlers below
    // operate on this specific tab even if the user switches away.
    tab.running = true;
    tab.abortController = new AbortController();
    tab.runStartedAt = performance.now();
    tab.runIsPartial = isPartial;
    tab.runningRes = {
      // One entry per `schema` event = one per row-producing statement.
      // Each entry owns its own schema, rows, and truncation state so
      // multi-statement scripts render one table per statement instead
      // of fighting over a single shared one.
      resultSets: [],
      // Aggregate row count across all sets — surfaced in the running
      // toolbar status as "N rows so far".
      rowCount: 0,
      elapsedMs: 0,
      trace: null,
      error: null,
      sessionId: null,
      chunks: [],
    };

    // The DOM nodes for this tab's streaming output. Re-attached when the
    // user switches back to this tab while it's still running (rebuild
    // from tab.runningRes). Tracked here so subsequent chunk events can
    // append directly without rebuilding from scratch.
    let streamPane = null;
    let streamHeaderModel = null;

    // Helpers: where is the running tab being displayed right now? At most
    // one group at a time has it as activeTabId. The DOM bindings below
    // resolve fresh each call so a tab moved between groups (step 4+) or
    // a tab toggled out and back in updates the right pane.
    function displayingGroupForRunningTab() {
      return getDisplayingGroups(tab.id)[0] || null;
    }
    function liveResultsPane() {
      const g = displayingGroupForRunningTab();
      return g ? resultsPaneEl(groupElementById(g.id)) : null;
    }
    function liveElapsedSlot() {
      const g = displayingGroupForRunningTab();
      return g ? elapsedEl(groupElementById(g.id)) : null;
    }

    // Reflect the new running state on whichever views are visible.
    syncToolbarToActiveTab();
    renderTabStrip();
    {
      const results = liveResultsPane();
      if (results) results.innerHTML = '<div class="meta">Running… (Esc to cancel)</div>';
    }

    // Live tick on the toolbar's elapsed slot. Only paints while the tab
    // is the active one in some group — switching tabs leaves the
    // visible elapsed slot showing the new active tab's static stats,
    // not a clobbered live value from a backgrounded run.
    function paintLiveStats() {
      const slot = liveElapsedSlot();
      if (!slot) return;
      const seconds = (performance.now() - tab.runStartedAt) / 1000;
      const rows = tab.runningRes.rowCount;
      const rowText = `${rows.toLocaleString()} ${rows === 1 ? 'row' : 'rows'}`;
      slot.textContent = `${rowText} · ${seconds.toFixed(1)} s (running)`;
    }
    paintLiveStats();
    tab.liveTickHandle = setInterval(paintLiveStats, 250);

    try {
      const response = await fetch('/api/query/stream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          sql,
          // Per-tab settings — each tab remembers its own row cap and
          // trace toggle so switching tabs doesn't change the next run's
          // shape.
          maxRows: tab.maxRows || 200,
          trace: tab.trace === true,
        }),
        signal: tab.abortController.signal,
      });

      if (!response.ok) {
        try {
          const errBody = await response.json();
          tab.runningRes.error = errBody.error || `HTTP ${response.status}`;
        } catch {
          tab.runningRes.error = `HTTP ${response.status}`;
        }
      } else {
        // Clear the placeholder if the tab is being displayed somewhere
        // — events render in place from here.
        {
          const results = liveResultsPane();
          if (results) results.innerHTML = '';
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buf = '';

        while (true) {
          const { value, done } = await reader.read();
          if (done) break;
          buf += decoder.decode(value, { stream: true });
          const lines = buf.split('\n');
          buf = lines.pop();
          for (const line of lines) {
            if (!line.trim()) continue;
            let event;
            try { event = JSON.parse(line); } catch { continue; }
            handleStreamEvent(event);
          }
        }
        if (buf.trim()) {
          try { handleStreamEvent(JSON.parse(buf)); } catch { /* ignore */ }
        }
      }
    } catch (err) {
      if (err && err.name === 'AbortError') {
        if (!tab.runningRes.error) tab.runningRes.error = 'cancelled';
      } else {
        tab.runningRes.error = `Network error: ${err.message}`;
      }
    } finally {
      clearInterval(tab.liveTickHandle);
      tab.liveTickHandle = null;
      tab.running = false;
      tab.abortController = null;
    }

    function handleStreamEvent(event) {
      const res = tab.runningRes;
      // Whether the user is currently looking at this tab in some group;
      // gates direct DOM appends. Tabs that aren't displayed anywhere
      // accumulate into res only.
      const results = liveResultsPane();
      const isActive = !!results;

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
          if (isActive) {
            // After a tab switch-back, our closure's streamPane may be
            // stale (the DOM was wiped) — but renderRunningTab already
            // rebuilt the pane from res.chunks. Re-grab it from the DOM
            // before appending so we don't end up with two duplicate
            // streaming panes one above the other.
            if (!streamPane || !streamPane.isConnected) {
              streamPane = results.querySelector('pre.streaming-output');
            }
            if (!streamPane) {
              streamHeaderModel = event.model;
              const header = document.createElement('div');
              header.className = 'meta stream-header';
              header.textContent = `streaming from models.${event.model}`;
              results.appendChild(header);
              streamPane = document.createElement('pre');
              streamPane.className = 'streaming-output';
              results.appendChild(streamPane);
            }
            streamPane.appendChild(document.createTextNode(event.text));
            streamPane.scrollTop = streamPane.scrollHeight;
          } else {
            // Will get rebuilt from res.chunks if/when user switches back.
            streamPane = null;
            streamHeaderModel = null;
          }
          break;
        case 'row':
          // Attach to the current result set (the most-recent schema's).
          // If a row arrives before any schema (defensive — shouldn't
          // happen on the wire), open a placeholder set to anchor it.
          {
            let cur = res.resultSets[res.resultSets.length - 1];
            if (!cur) {
              cur = { schema: null, rows: [], rowCount: 0, truncated: false };
              res.resultSets.push(cur);
            }
            cur.rows.push(event.cells);
            cur.rowCount = cur.rows.length;
            res.rowCount += 1;
          }
          break;
        case 'truncated':
          {
            const cur = res.resultSets[res.resultSets.length - 1];
            if (cur) {
              cur.truncated = true;
              cur.rowCount = event.rowCount;
            }
          }
          break;
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

    const finalRes = tab.runningRes;
    const wallMs = performance.now() - tab.runStartedAt;
    if (typeof finalRes.elapsedMs !== 'number' || !finalRes.elapsedMs) {
      finalRes.elapsedMs = wallMs;
    }
    tab.runningRes = null;
    tab.lastResult = finalRes;
    tab.lastRunAt = Date.now();
    if (!isPartial) tab.sqlOfLastRun = tab.sql;

    renderTabStrip();
    persistWorkspace();

    // If the user is still looking at this tab in any group, paint the
    // final result. If they switched away, the renderer will pick it up
    // next time they come back; meanwhile leave the toolbar / elapsed
    // slots showing whatever's now active per pane.
    if (getDisplayingGroups(tab.id).length > 0) {
      renderResultsForActiveTab();
    }
    // Run-button state changes regardless (e.g. another tab might still
    // be running and need to keep its Cancel state).
    syncToolbarToActiveTab();

    IDB.saveResult(workspaceName(), tab.id, finalRes).catch(err =>
      console.warn(`Couldn't save result for tab ${tab.id}:`, err));

    if (!finalRes.error) refreshSidebarForSql(sql);
  }

  // Cancels the in-flight query for the active tab, if any. Pulled out so
  // the Esc key handler and the Run button's Cancel mode share one entry
  // point.
  function cancelActiveTabRun() {
    const tab = activeTab();
    if (!tab || !tab.running || !tab.abortController) return;
    tab.abortController.abort();
    const status = document.getElementById('status');
    if (status) status.textContent = 'cancelling…';
  }

  // ===== Workspace switcher =====
  function rebuildWsSelect() {
    const sel = document.getElementById('ws-select');
    const all = listWorkspaces();
    sel.innerHTML = '';
    for (const name of all) {
      const opt = document.createElement('option');
      opt.value = name; opt.textContent = name;
      sel.appendChild(opt);
    }
    sel.value = workspaceName();
  }

  async function newWorkspace() {
    const name = await promptModal('New workspace', 'Name (letters, digits, _ or -; up to 40 chars):');
    if (name === null) return;
    if (!validName(name)) {
      await alertModal('Invalid name', 'Workspace names must match [a-zA-Z0-9_-]{1,40}.');
      return;
    }
    if (listWorkspaces().includes(name)) {
      // Already exists — just switch.
      location.hash = name === 'default' ? '' : name;
      return;
    }
    registerWorkspace(name);
    location.hash = name === 'default' ? '' : name;
  }

  async function deleteWorkspace() {
    const name = workspaceName();
    if (name === 'default') {
      await alertModal('Cannot delete', 'The "default" workspace cannot be deleted.');
      return;
    }
    const ok = await confirmModal('Delete workspace?',
      `This permanently removes "${name}" and all its tabs from this browser. Continue?`);
    if (!ok) return;
    unregisterWorkspace(name);
    location.hash = '';
  }

  function onHashChange() {
    persistWorkspace();
    const name = workspaceName();
    registerWorkspace(name);
    // Workspace switch invalidates every group's editor — the new
    // workspace may have a different group layout entirely. Tear them
    // down before swapping state so reconcileGroupDom rebuilds cleanly.
    for (const editor of monacoEditorsByGroup.values()) {
      editor.setModel(null);
      editor.dispose();
    }
    monacoEditorsByGroup.clear();
    fallbackTextareasByGroup.clear();
    for (const m of monacoModels.values()) m.dispose();
    monacoModels.clear();
    state.workspace = loadWorkspace(name);
    rebuildWsSelect();
    reconcileGroupDom();
    applyGroupContainerOrientation(
      localStorage.getItem(GROUP_ORIENTATION_STORE) === 'stack'
        ? 'stack' : 'side-by-side');
    for (const g of state.workspace.groups) applyEditorOrientationForGroup(g);
    updateSplitButtonIcon();
    renderTabStrip();
    swapEditorToActiveTab();
    renderResultsForActiveTab();
    syncToolbarToActiveTab();
  }

  // Cross-window live sync. The browser's `storage` event fires on every
  // *other* same-origin window/tab when localStorage changes here, so this
  // is how Window A learns about Window B's saves without a reload.
  //
  // Three keys are interesting:
  //   STORE.workspace(<currentName>) — our workspace was just rewritten by
  //     another window. Merge in any new tabs and apply remote tombstones.
  //   STORE.workspaces — the workspace registry changed (another window
  //     created or deleted a workspace). Refresh the dropdown.
  //   STORE.theme — the user toggled theme in another window. Mirror it.
  //
  // We deliberately don't try to reconcile *content edits* on tabs that
  // exist in both windows — picking up a remote edit mid-typing would
  // stomp the local user's keystrokes. In-memory always wins for tabs we
  // already have; only adds and remote-tombstone removals propagate.
  function applyRemoteWorkspaceSnapshot(onDisk) {
    if (!onDisk || !Array.isArray(onDisk.tabs)) return;

    const inMemoryById = new Map(state.workspace.tabs.map(t => [t.id, t]));
    const localTombs = new Set(state.workspace.deletedTabIds || []);
    const diskTombs = new Set(Array.isArray(onDisk.deletedTabIds) ? onDisk.deletedTabIds : []);

    let tabsChanged = false;
    let activeMissing = false;

    // Apply remote tombstones first — drop tabs another window deleted.
    for (const id of diskTombs) {
      if (localTombs.has(id)) continue;       // already gone here
      const idx = state.workspace.tabs.findIndex(t => t.id === id);
      if (idx === -1) continue;               // we never saw it
      state.workspace.tabs.splice(idx, 1);
      removeTabIdFromItsGroup(id);
      tabsChanged = true;
      if (monacoModels.has(id)) {
        monacoModels.get(id).dispose();
        monacoModels.delete(id);
      }
      // IDB result was deleted by the closing window already.
      if (id === state.workspace.activeTabId) activeMissing = true;
    }

    // Add tabs we don't have. Skip anything we tombstoned locally — our
    // delete is fresher than disk's "still alive" view (next save will
    // propagate the tombstone to disk). Newly-arrived tabs land in the
    // focused group (where to assign them is otherwise ambiguous; users
    // can move them across groups via tab drag once that ships).
    for (const t of onDisk.tabs) {
      if (!t || !t.id) continue;
      if (inMemoryById.has(t.id)) continue;
      if (localTombs.has(t.id)) continue;
      if (diskTombs.has(t.id)) continue;
      state.workspace.tabs.push({
        id: t.id,
        name: t.name || 'Untitled',
        sql: t.sql || '',
        lastResult: undefined,                 // hydrate from IDB on activation
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
      });
      addTabIdToFocusedGroup(t.id);
      tabsChanged = true;
    }

    // Carry forward remote tombstones into local state so our next save
    // doesn't drop them. Cap FIFO at 500 to match persistWorkspace.
    if (diskTombs.size > 0) {
      const merged = new Set([...localTombs, ...diskTombs]);
      const arr = [...merged];
      state.workspace.deletedTabIds = arr.length > 500 ? arr.slice(arr.length - 500) : arr;
    }

    // If our active tab got tombstoned remotely, fall back to the first
    // surviving tab in the focused group. If that group is now empty we
    // seed it with a fresh tab so the workspace is never tabless.
    if (activeMissing) {
      const g = focusedGroup();
      if (g && g.tabIds.length === 0) {
        const fresh = freshTab(1);
        state.workspace.tabs.push(fresh);
        g.tabIds.push(fresh.id);
      }
      if (g) g.activeTabId = g.tabIds[0];
      swapEditorToActiveTab();
      renderResultsForActiveTab();
      syncToolbarToActiveTab();
    }

    if (tabsChanged) renderTabStrip();
  }

  function setupCrossWindowSync() {
    window.addEventListener('storage', (e) => {
      // Same-tab writes don't fire this event, so we'll only see real
      // cross-window changes here.
      if (!e.key) return;

      // Workspace registry changed — refresh the dropdown so newly created
      // workspaces from other windows show up immediately.
      if (e.key === STORE.workspaces) {
        rebuildWsSelect();
        return;
      }

      // Theme toggled in another window — mirror it.
      if (e.key === STORE.theme && e.newValue) {
        applyTheme(e.newValue);
        return;
      }

      // Our current workspace was rewritten somewhere else.
      if (e.key === STORE.workspace(workspaceName())) {
        if (!e.newValue) return;               // workspace deleted elsewhere; let beforeunload sort it out
        let snapshot;
        try { snapshot = JSON.parse(e.newValue); }
        catch { return; }
        applyRemoteWorkspaceSnapshot(snapshot);
      }
    });
  }

  // Modal helpers (alert/confirm/prompt) + image lightbox — see ./modal.ts

  // ===== Catalog sidebar =====
  // Activity-bar-driven panel listing the user's catalog (tables, UDFs,
  // procedures, built-in functions, models) with right-click templates that
  // scaffold queries into new tabs. Backend data comes either from the
  // existing /api/tables endpoint or — for the system_* virtual tables —
  // a regular /api/query call. State (active section, collapsed flag,
  // width) lives in localStorage.
  const SIDEBAR_STORE = {
    section: 'datum.devweb.sidebar.section',
    width: 'datum.devweb.sidebar.width',
    collapsed: 'datum.devweb.sidebar.collapsed',
  };

  // Persisted height (px) of the editor pane, restored on boot and updated
  // on drag end. Stored as an integer; missing/invalid falls back to the
  // CSS default (32vh).
  const EDITOR_HEIGHT_STORE = 'datum.devweb.editorHeight';
  // Same idea for editor-pane width when the wrapper is in `.horizontal`
  // (side-by-side) orientation. Independent key so the two layouts each
  // remember their own size.
  const EDITOR_WIDTH_STORE = 'datum.devweb.editorWidth';
  // Orientation of the editor/results split: 'vertical' (default) or
  // 'horizontal' (side-by-side).
  const EDITOR_ORIENTATION_STORE = 'datum.devweb.editorOrientation';
  // Arrangement of multiple editor groups inside #group-container:
  // 'side-by-side' (default, flex-direction: row) or 'stack'
  // (flex-direction: column, one pane above the other).
  const GROUP_ORIENTATION_STORE = 'datum.devweb.groupOrientation';
  // Fraction of the container occupied by the FIRST group. Both groups
  // get `flex: ratio 1 0` / `flex: 1-ratio 1 0` so their sizes track
  // the container's actual dimensions instead of being pinned to px.
  const GROUP_SPLIT_RATIO_STORE = 'datum.devweb.groupSplitRatio';

  // Per-section cache so switching tabs doesn't re-fetch. Keys: 'tables',
  // 'udfs', 'procedures', 'functions', 'models'. Values: rendered HTML or
  // the raw rows used to render. Cleared on refresh.
  const sidebarCache = {};

  let activeSection = null;       // current section name, or null when collapsed
  let sidebarCollapsed = false;   // user has toggled sidebar away

  function initCatalogSidebar() {
    // Restore persisted state.
    const persistedSection = localStorage.getItem(SIDEBAR_STORE.section) || 'tables';
    const persistedWidth = parseInt(localStorage.getItem(SIDEBAR_STORE.width) || '240', 10);
    sidebarCollapsed = localStorage.getItem(SIDEBAR_STORE.collapsed) === '1';

    const sidebar = document.getElementById('sidebar');
    sidebar.style.width = clampSidebarWidth(persistedWidth) + 'px';
    sidebar.classList.toggle('collapsed', sidebarCollapsed);

    // Wire activity-bar buttons.
    document.querySelectorAll('#activity-bar button').forEach(btn => {
      btn.addEventListener('click', () => {
        const name = btn.dataset.section;
        if (activeSection === name && !sidebarCollapsed) {
          // Clicking the active icon collapses.
          sidebarCollapsed = true;
          sidebar.classList.add('collapsed');
          localStorage.setItem(SIDEBAR_STORE.collapsed, '1');
          highlightActive();
        } else {
          setActiveSection(name);
          if (sidebarCollapsed) {
            sidebarCollapsed = false;
            sidebar.classList.remove('collapsed');
            localStorage.setItem(SIDEBAR_STORE.collapsed, '0');
          }
        }
      });
    });

    document.getElementById('sidebar-refresh').addEventListener('click', () => {
      if (activeSection) {
        delete sidebarCache[activeSection];
        loadSection(activeSection);
      }
    });

    // Ctrl+B (Cmd+B on Mac) toggles the sidebar collapsed/expanded. The
    // listener is window-level so it fires even when Monaco has focus —
    // and we preventDefault to suppress the browser's "toggle bookmarks
    // bar" default. We bypass when a text input outside the editor has
    // focus so the user can still type a literal "b" with modifiers in
    // workspace inputs etc.
    window.addEventListener('keydown', (e) => {
      if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return;
      if (e.key !== 'b' && e.key !== 'B') return;
      const target = e.target;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA')) {
        // Allow legitimate Ctrl+B in form inputs (some browsers use it
        // for word boldening in contenteditable contexts; also avoid
        // hijacking workspace name input). Monaco's editor host is a
        // <div>, not <textarea>, so this still fires for SQL editing.
        return;
      }
      e.preventDefault();
      toggleSidebar();
    });

    // Drag-to-resize on the sidebar handle. Editor-pane resize handles
    // are wired per group by reconcileGroupDom().
    setupSidebarResize();

    // Activate persisted section. If the sidebar was collapsed at last save,
    // we still pick a section so the activity-bar highlight is correct and
    // re-expand will work without an extra click.
    setActiveSection(persistedSection, /*skipExpand=*/true);
  }

  function clampSidebarWidth(w) {
    return Math.max(160, Math.min(600, isFinite(w) ? w : 240));
  }

  // Flips the sidebar between collapsed and expanded. Used by the Ctrl+B
  // shortcut and any future toggle button. Re-shows the previously active
  // section on expand so the panel doesn't open empty.
  function toggleSidebar() {
    const sidebar = document.getElementById('sidebar');
    if (sidebarCollapsed) {
      sidebarCollapsed = false;
      sidebar.classList.remove('collapsed');
      localStorage.setItem(SIDEBAR_STORE.collapsed, '0');
      // If a section is set, ensure its content is fresh in the DOM (cache
      // hits make this near-free). Falls back to 'tables' when the user
      // somehow lands here without a stored section.
      if (activeSection) loadSection(activeSection);
      else setActiveSection('tables');
    } else {
      sidebarCollapsed = true;
      sidebar.classList.add('collapsed');
      localStorage.setItem(SIDEBAR_STORE.collapsed, '1');
    }
    highlightActive();
  }

  // After a successful query, drop sidebar caches whose contents may have
  // changed. Best-effort: a regex tells us "the SQL mentions a CREATE/DROP/
  // ALTER FUNCTION/PROCEDURE/TABLE statement" and we invalidate the matching
  // section. False positives (e.g. those tokens inside a string literal) are
  // harmless — at worst we re-fetch a section that didn't actually change.
  function refreshSidebarForSql(sql) {
    if (!sql) return;
    const text = String(sql);

    // CREATE / DROP / ALTER FUNCTION (including OR REPLACE / OR ALTER variants).
    // We allow any single keyword between the leading verb and FUNCTION/
    // PROCEDURE/TABLE so OR REPLACE / OR ALTER / IF NOT EXISTS / IF EXISTS
    // / TEMP / TEMPORARY all match without specific cases.
    const fnRe   = /\b(CREATE|DROP|ALTER)\s+(\w+\s+)?(\w+\s+)?FUNCTION\b/i;
    const procRe = /\b(CREATE|DROP|ALTER)\s+(\w+\s+)?(\w+\s+)?PROCEDURE\b/i;
    const tblRe  = /\b(CREATE|DROP|ALTER)\s+(\w+\s+)?(\w+\s+)?TABLE\b/i;

    const dirty = [];
    if (fnRe.test(text))   dirty.push('udfs');
    if (procRe.test(text)) dirty.push('procedures');
    if (tblRe.test(text))  dirty.push('tables');
    if (dirty.length === 0) return;

    for (const section of dirty) {
      delete sidebarCache[section];
      // If the user is currently viewing the dirty section, re-render now.
      // Otherwise the cache miss will fetch fresh on next activation.
      if (activeSection === section && !sidebarCollapsed) {
        loadSection(section);
      }
    }
  }

  function highlightActive() {
    document.querySelectorAll('#activity-bar button').forEach(btn => {
      btn.classList.toggle('active',
        btn.dataset.section === activeSection && !sidebarCollapsed);
    });
  }

  function setActiveSection(name, skipExpand) {
    activeSection = name;
    localStorage.setItem(SIDEBAR_STORE.section, name);
    document.getElementById('sidebar-title').textContent = sectionTitle(name);
    highlightActive();
    if (!skipExpand) {
      const sidebar = document.getElementById('sidebar');
      sidebar.classList.remove('collapsed');
      sidebarCollapsed = false;
      localStorage.setItem(SIDEBAR_STORE.collapsed, '0');
    }
    loadSection(name);
  }

  function sectionTitle(name) {
    return ({
      tables: 'Tables',
      udfs: 'UDFs',
      procedures: 'Procedures',
      functions: 'Functions',
      models: 'Models',
    })[name] || name;
  }

  async function loadSection(name) {
    const content = document.getElementById('sidebar-content');
    if (sidebarCache[name]) {
      // Rendered DOM survives a section switch — re-attach instead of
      // re-rendering. (Fast path; refresh button clears the cache.)
      content.replaceChildren(sidebarCache[name]);
      return;
    }

    content.replaceChildren(htmlNode('<div class="empty-state">Loading…</div>'));

    try {
      let frag;
      switch (name) {
        case 'tables':     frag = await loadTablesSection(); break;
        case 'udfs':       frag = await loadUdfsSection(); break;
        case 'procedures': frag = await loadProceduresSection(); break;
        case 'functions':  frag = await loadFunctionsSection(); break;
        case 'models':     frag = await loadModelsSection(); break;
        default:           frag = htmlNode('<div class="empty-state">Unknown section.</div>');
      }
      sidebarCache[name] = frag;
      content.replaceChildren(frag);
    } catch (err) {
      console.warn(`Failed to load ${name}:`, err);
      content.replaceChildren(htmlNode(
        `<div class="empty-state">Failed to load.<br><small>${escapeHtml(String(err.message || err))}</small></div>`));
    }
  }

  // htmlNode, escapeHtml — see ./html-util.ts

  // ───── Section loaders ─────

  async function loadTablesSection() {
    const res = await fetch('/api/tables');
    if (!res.ok) throw new Error(`/api/tables → ${res.status}`);
    const tables = await res.json();
    if (!Array.isArray(tables) || tables.length === 0) {
      return htmlNode('<div class="empty-state">No tables registered.</div>');
    }
    tables.sort((a, b) => a.name.localeCompare(b.name));
    const wrap = document.createDocumentFragment();
    for (const t of tables) {
      const node = renderTreeNode({
        name: t.name,
        meta: t.rows >= 0 ? `${t.rows.toLocaleString()} rows` : '',
        onContextMenu: (e) => openTableContextMenu(e, t.name),
      });
      wrap.appendChild(node);
    }
    const container = document.createElement('div');
    container.appendChild(wrap);
    return container;
  }

  async function loadUdfsSection() {
    const rows = await runIntrospectionQuery(
      'SELECT name, parameters, return_type, body, body_kind FROM system_udfs ORDER BY name');
    if (rows.length === 0) {
      return htmlNode('<div class="empty-state">No UDFs registered.<br><small>Use CREATE FUNCTION to add one.</small></div>');
    }
    const container = document.createElement('div');
    for (const r of rows) {
      container.appendChild(renderTreeNode({
        name: r.name,
        popover: `${r.name}(${r.parameters || ''})`,
        onContextMenu: (e) => openUdfContextMenu(e, r),
      }));
    }
    return container;
  }

  async function loadProceduresSection() {
    const rows = await runIntrospectionQuery(
      'SELECT name, parameters, source_text FROM system_procedures ORDER BY name');
    if (rows.length === 0) {
      return htmlNode('<div class="empty-state">No procedures registered.<br><small>Use CREATE PROCEDURE to add one.</small></div>');
    }
    const container = document.createElement('div');
    for (const r of rows) {
      container.appendChild(renderTreeNode({
        name: r.name,
        popover: `${r.name}(${r.parameters || ''})`,
        onContextMenu: (e) => openProcedureContextMenu(e, r),
      }));
    }
    return container;
  }

  async function loadFunctionsSection() {
    // Built-in functions come from the LanguageServer's static manifest, not
    // a virtual SQL table — they're fixed at startup and the manifest is the
    // canonical source. Endpoint pre-filters internal `__` helpers.
    const res = await fetch('/api/lang/functions');
    if (!res.ok) throw new Error(`/api/lang/functions → ${res.status}`);
    const fns = await res.json();
    if (!Array.isArray(fns) || fns.length === 0) {
      return htmlNode('<div class="empty-state">No functions found.</div>');
    }
    const container = document.createElement('div');
    for (const f of fns) {
      const sig = `${f.name}(${(f.parameters || []).map(p => p.name + ':' + p.type).join(', ')})`;
      container.appendChild(renderTreeNode({
        name: f.name,
        meta: f.category || '',
        popover: `${sig}${f.returnType ? ' → ' + f.returnType : ''}`,
        onContextMenu: (e) => openFunctionContextMenu(e, f),
      }));
    }
    return container;
  }

  async function loadModelsSection() {
    const rows = await runIntrospectionQuery(
      'SELECT name, category, backend, status FROM system_models ORDER BY category, name');
    if (rows.length === 0) {
      return htmlNode('<div class="empty-state">No models registered.</div>');
    }
    const container = document.createElement('div');
    for (const r of rows) {
      container.appendChild(renderTreeNode({
        name: r.name,
        meta: r.backend || '',
        title: `${r.name} — ${r.category || ''} (${r.status || ''})`,
        onContextMenu: (e) => openModelContextMenu(e, r),
      }));
    }
    return container;
  }

  // truncate — see ./html-util.ts

  function renderTreeNode({ name, meta, title, popover, onContextMenu }) {
    const node = document.createElement('div');
    node.className = 'tree-node';
    // The browser-native `title` attribute is slow and visually
    // inconsistent. For nodes that pass `popover`, we render a styled
    // floating element instead. Plain `title` is still used for nodes
    // that don't opt in.
    if (title && !popover) node.title = title;
    node.innerHTML = `
      <span class="twisty"></span>
      <span class="name">${escapeHtml(name)}</span>
      ${meta ? `<span class="meta">${escapeHtml(meta)}</span>` : ''}
    `;
    if (popover) attachTreeNodePopover(node, popover);
    if (onContextMenu) {
      node.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        onContextMenu(e);
      });
    }
    return node;
  }

  // Attach a hover popover to a tree node. The popover is created on
  // mouseenter, positioned to the right of the node (or left/below if
  // it would overflow), and removed on mouseleave.
  function attachTreeNodePopover(node, text) {
    let popoverEl = null;
    const remove = () => {
      if (popoverEl) {
        popoverEl.remove();
        popoverEl = null;
      }
    };
    node.addEventListener('mouseenter', () => {
      remove();
      popoverEl = document.createElement('div');
      popoverEl.className = 'tree-node-popover';
      popoverEl.textContent = text;
      document.body.appendChild(popoverEl);
      const nodeRect = node.getBoundingClientRect();
      const popRect = popoverEl.getBoundingClientRect();
      const margin = 8;
      // Prefer to the right of the sidebar node; flip below if the
      // popover would overflow the viewport horizontally.
      let left = nodeRect.right + margin;
      let top = nodeRect.top;
      if (left + popRect.width > window.innerWidth - margin) {
        left = Math.max(margin, nodeRect.left);
        top = nodeRect.bottom + 4;
      }
      if (top + popRect.height > window.innerHeight - margin) {
        top = Math.max(margin, window.innerHeight - popRect.height - margin);
      }
      popoverEl.style.left = left + 'px';
      popoverEl.style.top = top + 'px';
    });
    node.addEventListener('mouseleave', remove);
    // Defensive: if the node is detached (e.g. sidebar refresh) while
    // the popover is up, drop it on the next tick.
    node.addEventListener('mousedown', remove);
  }

  // Runs an introspection query against /api/query and returns the rows as
  // an array of { columnName: stringOrNull } objects. Each backend cell is
  // wrapped as { kind, text, ... }; we unwrap text/json cells, leaving null
  // for null cells. Throws on backend error.
  async function runIntrospectionQuery(sql) {
    const res = await fetch('/api/query', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sql, maxRows: 5000 }),
    });
    if (!res.ok) {
      let msg = `${res.status}`;
      try { const j = await res.json(); msg = j.error || msg; } catch {}
      throw new Error(msg);
    }
    const payload = await res.json();
    if (payload.error) throw new Error(payload.error);
    const schema = payload.schema || [];
    const rows = payload.rows || [];
    const cols = schema.map(c => c.name);
    return rows.map(row => {
      const o = {};
      for (let i = 0; i < cols.length; i++) {
        const cell = row[i];
        if (!cell || cell.kind === 'null') { o[cols[i]] = null; continue; }
        if (cell.kind === 'text' || cell.kind === 'json') { o[cols[i]] = cell.text; continue; }
        // Media / unknown — just stash the cell object so the caller can
        // decide; introspection columns shouldn't hit this.
        o[cols[i]] = cell;
      }
      return o;
    });
  }

  // ───── Context menus ─────

  function openContextMenu(e, items) {
    closeCatalogContextMenu();
    const menu = document.createElement('div');
    menu.id = 'catalog-context-menu';
    for (const it of items) {
      if (it.separator) {
        const sep = document.createElement('div');
        sep.className = 'sep';
        sep.style.cssText = 'height:1px;background:var(--border);margin:4px 0;';
        menu.appendChild(sep);
        continue;
      }
      const btn = document.createElement('button');
      btn.textContent = it.label;
      if (it.disabled) btn.disabled = true;
      btn.addEventListener('click', (ev) => {
        ev.stopPropagation();
        closeCatalogContextMenu();
        try { it.action(); } catch (err) { console.warn(err); }
      });
      menu.appendChild(btn);
    }
    document.body.appendChild(menu);
    // Position; flip if it would overflow.
    const rect = menu.getBoundingClientRect();
    let x = e.clientX, y = e.clientY;
    if (x + rect.width > window.innerWidth - 8) x = window.innerWidth - rect.width - 8;
    if (y + rect.height > window.innerHeight - 8) y = window.innerHeight - rect.height - 8;
    menu.style.left = x + 'px';
    menu.style.top = y + 'px';
    setTimeout(() => {
      window.addEventListener('click', closeCatalogContextMenu, { once: true, capture: true });
      window.addEventListener('keydown', escCloseCatalogMenu, { once: true });
    }, 0);
  }

  function closeCatalogContextMenu() {
    const m = document.getElementById('catalog-context-menu');
    if (m) m.remove();
  }

  function escCloseCatalogMenu(e) {
    if (e.key === 'Escape') closeCatalogContextMenu();
  }

  function openTableContextMenu(e, tableName) {
    openContextMenu(e, [
      { label: `SELECT * FROM ${tableName} LIMIT 100`,
        action: () => openSqlInNewTab(tableName,
          `SELECT * FROM ${tableName} LIMIT 100`) },
    ]);
  }

  function openUdfContextMenu(e, udf) {
    openContextMenu(e, [
      { label: 'Execute',
        action: () => openSqlInNewTab(`EXEC udf.${udf.name}`,
          buildExecuteTemplate('udf', udf.name, udf.parameters)) },
      { label: 'Modify',
        action: () => openSqlInNewTab(`Modify ${udf.name}`,
          buildModifyTemplateFromUdfRow(udf)) },
    ]);
  }

  function openProcedureContextMenu(e, proc) {
    openContextMenu(e, [
      { label: 'Execute',
        action: () => openSqlInNewTab(`EXEC proc.${proc.name}`,
          buildExecuteTemplate('proc', proc.name, proc.parameters)) },
      { label: 'Modify',
        action: () => openSqlInNewTab(`Modify ${proc.name}`,
          buildModifyTemplateFromProcedureRow(proc)) },
    ]);
  }

  function openFunctionContextMenu(e, fn) {
    openContextMenu(e, [
      { label: 'Execute',
        action: () => openSqlInNewTab(`SELECT ${fn.name}`,
          buildBuiltinExecuteTemplate(fn)) },
    ]);
  }

  function openModelContextMenu(e, model) {
    openContextMenu(e, [
      { label: 'Execute',
        action: () => openSqlInNewTab(`models.${model.name}`,
          `SELECT models.${model.name}('your input here')`) },
    ]);
  }

  // Template builders — see ./parser-util.ts

  // Opens a fresh tab, names it from the supplied label, fills in the SQL,
  // and switches to it.
  function openSqlInNewTab(name, sql) {
    const t = freshTab(state.workspace.tabs.length + 1);
    t.name = name.length > 32 ? name.slice(0, 31) + '…' : name;
    t.sql = sql;
    // Seed sqlOfLastRun so the tab opens clean — the dirty marker only
    // appears once the user actually edits the seeded text.
    t.sqlOfLastRun = sql;
    state.workspace.tabs.push(t);
    addTabIdToFocusedGroup(t.id);
    state.workspace.activeTabId = t.id;
    renderTabStrip();
    swapEditorToActiveTab();
    renderResultsForActiveTab();
    syncToolbarToActiveTab();
    persistWorkspace();
  }

  // ───── Resize ─────
  function setupSidebarResize() {
    const handle = document.getElementById('sidebar-resize');
    const sidebar = document.getElementById('sidebar');
    let dragStartX = 0, dragStartW = 0;

    function onMove(e) {
      const dx = e.clientX - dragStartX;
      const w = clampSidebarWidth(dragStartW + dx);
      sidebar.style.width = w + 'px';
    }
    function onUp() {
      handle.classList.remove('dragging');
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      localStorage.setItem(SIDEBAR_STORE.width, parseInt(sidebar.style.width, 10));
    }

    handle.addEventListener('mousedown', (e) => {
      // Don't allow drag while collapsed (handle is hidden anyway, but be safe).
      if (sidebar.classList.contains('collapsed')) return;
      e.preventDefault();
      handle.classList.add('dragging');
      dragStartX = e.clientX;
      dragStartW = sidebar.getBoundingClientRect().width;
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });
  }

  // Bottom bound is recomputed each drag from the current viewport so the
  // results pane is guaranteed at least ~120px regardless of window size.
  function clampEditorHeight(h) {
    const min = 120;
    const max = Math.max(min, window.innerHeight - 200);
    if (!isFinite(h)) return min;
    return Math.max(min, Math.min(max, h));
  }

  // Horizontal-orientation analog: clamp width against the wrapper's own
  // width so the results pane keeps at least ~240px no matter how narrow
  // the window gets.
  function clampEditorWidth(w, groupEl) {
    const wrapper = editorResultsWrapperEl(groupEl || focusedGroupEl());
    const total = wrapper ? wrapper.getBoundingClientRect().width : window.innerWidth;
    const min = 240;
    const max = Math.max(min, total - 240);
    if (!isFinite(w)) return min;
    return Math.max(min, Math.min(max, w));
  }

  function isHorizontalSplit(groupEl) {
    const wrapper = editorResultsWrapperEl(groupEl || focusedGroupEl());
    return !!wrapper && wrapper.classList.contains('horizontal');
  }

  // Apply the persisted editor-pane size for the current orientation. The
  // CSS default (32vh / 50%) stays in effect when no px value is saved.
  // For step 3b both groups read the same persisted size; per-group size
  // persistence is polish (3c).
  function applyPersistedEditorSize(groupEl) {
    groupEl = groupEl || focusedGroupEl();
    const pane = editorPaneEl(groupEl);
    if (!pane) return;
    if (isHorizontalSplit(groupEl)) {
      pane.style.height = '';
      const persisted = parseInt(localStorage.getItem(EDITOR_WIDTH_STORE) || '', 10);
      pane.style.width = isFinite(persisted) ? clampEditorWidth(persisted, groupEl) + 'px' : '';
    } else {
      pane.style.width = '';
      const persisted = parseInt(localStorage.getItem(EDITOR_HEIGHT_STORE) || '', 10);
      pane.style.height = isFinite(persisted) ? clampEditorHeight(persisted) + 'px' : '';
    }
  }

  function setupEditorResultsResize(groupEl) {
    groupEl = groupEl || focusedGroupEl();
    if (!groupEl) return;
    if (groupEl.dataset.resizeWired === '1') {
      // Re-apply persisted size in case orientation changed under us, but
      // don't pile up a second mousedown listener on the same handle.
      applyPersistedEditorSize(groupEl);
      return;
    }
    groupEl.dataset.resizeWired = '1';
    const handle = editorResultsResizeEl(groupEl);
    const pane = editorPaneEl(groupEl);
    if (!handle || !pane) return;

    applyPersistedEditorSize(groupEl);

    let dragStart = 0, dragStartSize = 0, dragHorizontal = false;
    function onMove(e) {
      if (dragHorizontal) {
        const dx = e.clientX - dragStart;
        const w = clampEditorWidth(dragStartSize + dx, groupEl);
        pane.style.width = w + 'px';
      } else {
        const dy = e.clientY - dragStart;
        const h = clampEditorHeight(dragStartSize + dy);
        pane.style.height = h + 'px';
      }
    }
    function onUp() {
      handle.classList.remove('dragging');
      document.body.style.userSelect = '';
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      if (dragHorizontal) {
        localStorage.setItem(EDITOR_WIDTH_STORE, parseInt(pane.style.width, 10));
      } else {
        localStorage.setItem(EDITOR_HEIGHT_STORE, parseInt(pane.style.height, 10));
      }
    }

    handle.addEventListener('mousedown', (e) => {
      e.preventDefault();
      handle.classList.add('dragging');
      // Suppress text selection while dragging — the cursor would otherwise
      // grab whatever it passes over inside the editor or results table.
      document.body.style.userSelect = 'none';
      dragHorizontal = isHorizontalSplit(groupEl);
      const rect = pane.getBoundingClientRect();
      if (dragHorizontal) {
        dragStart = e.clientX;
        dragStartSize = rect.width;
      } else {
        dragStart = e.clientY;
        dragStartSize = rect.height;
      }
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });
  }

  // Apply a group's current editorOrientation to its DOM. Each group
  // owns its own orientation; the per-tab-strip toggle flips the value
  // on `group.editorOrientation` and calls this. The persisted size
  // (height vs width) is reapplied because the axis it's measured against
  // has changed.
  function applyEditorOrientationForGroup(group) {
    if (!group) return;
    const groupEl = groupElementById(group.id);
    const wrapper = editorResultsWrapperEl(groupEl);
    if (!wrapper) return;
    const horizontal = group.editorOrientation === 'horizontal';
    wrapper.classList.toggle('horizontal', horizontal);
    applyPersistedEditorSize(groupEl);
    refreshOrientToggleIconForGroup(group);
  }

  // Update the orient-toggle button (rendered into each tab strip) so
  // its divider line previews the *target* orientation — i.e. the
  // layout that clicking it will produce. Currently horizontal →
  // divider is horizontal (click → vertical/stacked); currently
  // vertical → divider is vertical (click → horizontal/side-by-side).
  function refreshOrientToggleIconForGroup(group) {
    if (!group) return;
    const groupEl = groupElementById(group.id);
    if (!groupEl) return;
    const divider = groupEl.querySelector('.orient-toggle-divider');
    if (!divider) return;
    const horizontal = group.editorOrientation === 'horizontal';
    if (horizontal) {
      // Target = vertical (stacked) → horizontal divider.
      divider.setAttribute('x1', '3');  divider.setAttribute('y1', '12');
      divider.setAttribute('x2', '21'); divider.setAttribute('y2', '12');
    } else {
      // Target = horizontal (side-by-side) → vertical divider.
      divider.setAttribute('x1', '12'); divider.setAttribute('y1', '4');
      divider.setAttribute('x2', '12'); divider.setAttribute('y2', '20');
    }
  }

  // Flip the supplied group's orientation in state and reapply.
  function toggleEditorOrientationForGroup(group) {
    if (!group) return;
    group.editorOrientation = group.editorOrientation === 'horizontal' ? 'vertical' : 'horizontal';
    applyEditorOrientationForGroup(group);
    persistWorkspace();
  }

  // ===== Group splitter + container orientation =====

  // Keep the split ratio in a sane range so a user can always grab the
  // splitter and the smaller pane never shrinks below ~15% of the
  // container.
  function clampSplitRatio(r) {
    if (!isFinite(r)) return 0.5;
    return Math.max(0.15, Math.min(0.85, r));
  }

  function applyPersistedGroupSplitRatio() {
    const container = document.getElementById('group-container');
    if (!container) return;
    const groups = container.querySelectorAll(':scope > .editor-group');
    if (groups.length < 2) {
      // Single pane: clear any inline flex from a prior split so the
      // group fills the container.
      groups.forEach(el => { el.style.flex = ''; });
      return;
    }
    const persisted = parseFloat(localStorage.getItem(GROUP_SPLIT_RATIO_STORE));
    const ratio = clampSplitRatio(isFinite(persisted) ? persisted : 0.5);
    groups[0].style.flex = `${ratio} 1 0`;
    groups[1].style.flex = `${1 - ratio} 1 0`;
  }

  // Build #group-resize with its floating control pill (merge + orient
  // swap). The buttons stop propagation on mousedown so clicking them
  // doesn't trigger the parent handle's drag-start; click events then
  // run normally and route to the right action.
  function buildGroupResizeElement() {
    const el = document.createElement('div');
    el.id = 'group-resize';

    const controls = document.createElement('div');
    controls.className = 'group-resize-controls';

    const mergeBtn = document.createElement('button');
    mergeBtn.title = 'Merge panes back into one';
    mergeBtn.setAttribute('aria-label', 'Merge panes');
    mergeBtn.innerHTML =
      '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.6">' +
      '<path d="M6 6L18 18M6 18L18 6"></path>' +
      '</svg>';
    mergeBtn.addEventListener('mousedown', (e) => e.stopPropagation());
    mergeBtn.addEventListener('click', mergeAllGroupsIntoFocused);
    controls.appendChild(mergeBtn);

    const swapBtn = document.createElement('button');
    swapBtn.title = 'Swap pane arrangement (side-by-side ↔ stacked)';
    swapBtn.setAttribute('aria-label', 'Swap pane arrangement');
    // Initial divider position is set by refreshGroupOrientToggleIcon
    // after the element is inserted; the placeholder coords here are
    // fine because that helper rewrites them.
    swapBtn.innerHTML =
      '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.6">' +
      '<rect x="3" y="4" width="18" height="16" rx="1"></rect>' +
      '<line class="group-orient-divider" x1="12" y1="4" x2="12" y2="20"></line>' +
      '</svg>';
    swapBtn.addEventListener('mousedown', (e) => e.stopPropagation());
    swapBtn.addEventListener('click', toggleGroupContainerOrientation);
    controls.appendChild(swapBtn);

    el.appendChild(controls);
    return el;
  }

  // Drag handle between the two groups. Resizing recomputes the ratio
  // from the cursor's position relative to the container so both
  // orientations (side-by-side and stacked) share one implementation.
  function setupGroupResize(splitter) {
    if (splitter.dataset.wired === '1') return;
    splitter.dataset.wired = '1';

    let dragVertical = false;
    function onMove(e) {
      const container = document.getElementById('group-container');
      if (!container) return;
      const rect = container.getBoundingClientRect();
      const total = dragVertical ? rect.height : rect.width;
      const start = dragVertical ? rect.top : rect.left;
      const cur = dragVertical ? e.clientY : e.clientX;
      const ratio = clampSplitRatio((cur - start) / total);
      const groups = container.querySelectorAll(':scope > .editor-group');
      if (groups.length < 2) return;
      groups[0].style.flex = `${ratio} 1 0`;
      groups[1].style.flex = `${1 - ratio} 1 0`;
    }
    function onUp() {
      splitter.classList.remove('dragging');
      document.body.style.userSelect = '';
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      const container = document.getElementById('group-container');
      const groups = container ? container.querySelectorAll(':scope > .editor-group') : [];
      if (groups.length >= 2) {
        const f = parseFloat(groups[0].style.flex);
        if (isFinite(f)) localStorage.setItem(GROUP_SPLIT_RATIO_STORE, f);
      }
    }
    splitter.addEventListener('mousedown', (e) => {
      e.preventDefault();
      splitter.classList.add('dragging');
      document.body.style.userSelect = 'none';
      const container = document.getElementById('group-container');
      dragVertical = !!container && container.classList.contains('vertical-stack');
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });
  }

  // Update the orient-swap button (rendered into the floating controls
  // on the resize line) so its divider line previews the *target*
  // arrangement — i.e. the layout that clicking it will produce.
  // Currently stacked → divider is vertical (click → side-by-side);
  // currently side-by-side → divider is horizontal (click → stacked).
  function refreshGroupOrientToggleIcon() {
    const splitter = document.getElementById('group-resize');
    if (!splitter) return;
    const divider = splitter.querySelector('.group-orient-divider');
    if (!divider) return;
    const container = document.getElementById('group-container');
    const stacked = !!container && container.classList.contains('vertical-stack');
    if (stacked) {
      // Target = side-by-side → vertical divider.
      divider.setAttribute('x1', '12'); divider.setAttribute('y1', '4');
      divider.setAttribute('x2', '12'); divider.setAttribute('y2', '20');
    } else {
      // Target = stacked → horizontal divider.
      divider.setAttribute('x1', '3');  divider.setAttribute('y1', '12');
      divider.setAttribute('x2', '21'); divider.setAttribute('y2', '12');
    }
  }

  // Apply 'side-by-side' (default) or 'stack' to the container. Re-runs
  // ratio application because the axis the ratio applies to changes.
  function applyGroupContainerOrientation(orient) {
    const container = document.getElementById('group-container');
    if (!container) return;
    container.classList.toggle('vertical-stack', orient === 'stack');
    applyPersistedGroupSplitRatio();
    refreshGroupOrientToggleIcon();
  }
  function toggleGroupContainerOrientation() {
    const container = document.getElementById('group-container');
    const next = container && container.classList.contains('vertical-stack')
      ? 'side-by-side' : 'stack';
    localStorage.setItem(GROUP_ORIENTATION_STORE, next);
    applyGroupContainerOrientation(next);
  }

  // ===== Group DOM factory + lifecycle =====

  // Build the .editor-group skeleton from scratch. Mirrors the hardcoded
  // markup for the first group so reconcileGroupDom can stamp out
  // additional groups when the user splits the layout.
  function createEditorGroupElement(groupId) {
    const root = document.createElement('div');
    root.className = 'editor-group';
    root.dataset.groupId = groupId;
    root.innerHTML =
      '<div class="tab-strip"></div>' +
      '<div class="editor-results-wrapper">' +
        '<div class="editor-pane">' +
          '<div class="editor-host"></div>' +
          '<div class="editor-toolbar">' +
            '<button class="run-btn">Run</button>' +
            '<span class="tip">Ctrl/⌘+Enter</span>' +
            '<span class="tip">max rows ' +
              '<input class="max-rows-input" type="number" value="200" min="1" max="100000">' +
            '</span>' +
            '<label class="tip" title="Capture engine execution trace for the next run">' +
              '<input class="trace-toggle" type="checkbox"> trace' +
            '</label>' +
            '<span class="spacer"></span>' +
            '<span class="elapsed tip"></span>' +
          '</div>' +
        '</div>' +
        '<div class="editor-results-resize" title="Drag to resize"></div>' +
        '<div class="results-pane">' +
          '<div class="meta">Type a query and press Run.</div>' +
        '</div>' +
      '</div>';
    return root;
  }

  // Wire one group's toolbar buttons + inputs. Idempotent — guarded by a
  // dataset flag so reconcileGroupDom can call it on every reconciliation
  // pass without piling up duplicate handlers. The handlers re-resolve
  // the live group by id from `state.workspace` rather than closing over
  // the group object, so they keep working after a workspace switch
  // reuses the same DOM node for a fresh workspace's group.
  function wireGroupToolbar(groupEl, group) {
    if (groupEl.dataset.toolbarWired === '1') return;
    groupEl.dataset.toolbarWired = '1';
    const groupId = group.id;
    const liveGroup = () => state.workspace.groups.find(g => g.id === groupId);
    const liveTab = () => {
      const g = liveGroup();
      return g ? state.workspace.tabs.find(t => t.id === g.activeTabId) : null;
    };

    // Each handler focuses its group as a side effect, so clicks on the
    // non-focused pane "follow" — the user's interaction defines focus
    // even when the keyboard hasn't moved yet.
    runBtnEl(groupEl).addEventListener('click', () => {
      const g = liveGroup();
      if (!g) return;
      state.workspace.focusedGroupId = g.id;
      const tab = liveTab();
      if (tab && tab.running) cancelActiveTabRun();
      else run();
    });
    maxRowsInputEl(groupEl).addEventListener('input', (e) => {
      const g = liveGroup();
      if (!g) return;
      state.workspace.focusedGroupId = g.id;
      const tab = liveTab();
      if (!tab) return;
      const v = parseInt(e.target.value, 10);
      tab.maxRows = (Number.isFinite(v) && v > 0) ? v : 200;
      scheduleSave();
    });
    traceToggleEl(groupEl).addEventListener('change', (e) => {
      const g = liveGroup();
      if (!g) return;
      state.workspace.focusedGroupId = g.id;
      const tab = liveTab();
      if (!tab) return;
      tab.trace = e.target.checked === true;
      scheduleSave();
    });
    // Pointerdown anywhere in the pane → focus this group AND move
    // Monaco's keyboard focus to its editor. Without the second part,
    // editor-scoped commands like Ctrl+Enter still target whichever
    // editor previously had keyboard focus (e.g. the OTHER pane), so a
    // click on this pane's tab strip followed by Ctrl+Enter could run
    // the wrong tab. Form inputs are skipped — stealing focus from them
    // would block typing into max-rows / trace controls.
    groupEl.addEventListener('pointerdown', (e) => {
      if (state.workspace.focusedGroupId !== groupId && liveGroup()) {
        state.workspace.focusedGroupId = groupId;
        // Status bar / header icons reflect the focused group's tab.
        syncToolbarToActiveTab();
      }
      const target = e.target;
      if (target && /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)) return;
      const editor = monacoEditorsByGroup.get(groupId);
      if (editor) {
        if (!editor.hasTextFocus()) editor.focus();
        return;
      }
      const fb = fallbackTextareasByGroup.get(groupId);
      if (fb && document.activeElement !== fb) fb.focus();
    });
  }

  // Ensure #group-container's children mirror state.workspace.groups in
  // order. Adds any missing .editor-group nodes, removes any orphaned
  // ones, and (idempotently) wires every group's toolbar + resize handle.
  // Also stands up an editor (Monaco if loaded, else the textarea
  // fallback) for any group that doesn't have one yet — so split,
  // workspace-switch, and boot all share one path.
  function reconcileGroupDom() {
    const container = document.getElementById('group-container');
    if (!container) return;
    const validIds = new Set(state.workspace.groups.map(g => g.id));
    for (const el of Array.from(container.querySelectorAll(':scope > .editor-group'))) {
      if (!validIds.has(el.dataset.groupId)) el.remove();
    }
    let prevEl = null;
    for (const g of state.workspace.groups) {
      let el = groupElementById(g.id);
      if (!el) {
        el = createEditorGroupElement(g.id);
        if (prevEl) prevEl.after(el);
        else container.prepend(el);
      } else if (prevEl && prevEl.nextSibling !== el) {
        // Order changed — re-anchor.
        prevEl.after(el);
      }
      wireGroupToolbar(el, g);
      setupEditorResultsResize(el);
      // Editor-instance bring-up: Monaco if its loader has resolved,
      // otherwise the textarea fallback. The fallback gets replaced by
      // Monaco later when the loader's callback fires.
      if (!monacoEditorsByGroup.has(g.id) && !fallbackTextareasByGroup.has(g.id)) {
        if (window.monaco) bootMonacoForGroup(g.id);
        else bootFallbackForGroup(g.id);
      }
      prevEl = el;
    }

    // Splitter between groups. Only meaningful with 2+ groups; otherwise
    // remove it so the single pane fills the container without a gap.
    // Group-level controls (merge / swap orientation) live inside the
    // splitter so they're discoverable on hover and stay close to the
    // visual "seam" between panes.
    let splitter = document.getElementById('group-resize');
    if (state.workspace.groups.length >= 2) {
      if (!splitter) {
        splitter = buildGroupResizeElement();
      }
      const groups = container.querySelectorAll(':scope > .editor-group');
      if (groups.length >= 2 && groups[0].nextSibling !== splitter) {
        groups[0].after(splitter);
      }
      setupGroupResize(splitter);
      refreshGroupOrientToggleIcon();
    } else if (splitter) {
      splitter.remove();
    }

    // Re-apply ratio AFTER the splitter (and any newly-created group
    // elements) are in place so the flex values stick.
    applyPersistedGroupSplitRatio();
  }

  // ===== Split / merge =====

  // Move the focused group's active tab into a new group on the right.
  // Source pane is reseeded with a fresh tab if it would otherwise empty
  // (we never want a group to exist with zero tabs). For step 3b only one
  // additional group is allowed; clicking again merges back.
  function splitFocusedGroup() {
    if (state.workspace.groups.length >= 2) {
      mergeAllGroupsIntoFocused();
      return;
    }
    const source = focusedGroup();
    if (!source) return;
    const tabIdToMove = source.activeTabId;
    const moveIdx = source.tabIds.indexOf(tabIdToMove);
    if (moveIdx >= 0) source.tabIds.splice(moveIdx, 1);
    if (source.tabIds.length === 0) {
      const fresh = freshTab(state.workspace.tabs.length + 1);
      state.workspace.tabs.push(fresh);
      source.tabIds.push(fresh.id);
    }
    source.activeTabId = source.tabIds[0];

    const newGroupId = 'g' + Date.now().toString(36);
    state.workspace.groups.push({
      id: newGroupId,
      tabIds: [tabIdToMove],
      activeTabId: tabIdToMove,
      // Inherit the source pane's current editor orientation so the
      // newly-spawned pane "feels like" the one it came from.
      editorOrientation: source.editorOrientation || 'vertical',
    });
    state.workspace.focusedGroupId = newGroupId;

    // reconcileGroupDom creates the new group's DOM and stands up its
    // editor (Monaco if loaded, else fallback) in one shot.
    reconcileGroupDom();

    renderTabStrip();
    swapEditorToActiveTab();
    renderResultsForActiveTab();
    syncToolbarToActiveTab();
    updateSplitButtonIcon();
    persistWorkspace();
  }

  // Inverse of splitFocusedGroup: pull every other group's tabs into the
  // focused group, then dispose those groups. Source-group ordering wins
  // for the merged tab strip; the focused group's active tab stays
  // active.
  function mergeAllGroupsIntoFocused() {
    if (state.workspace.groups.length < 2) return;
    const focused = focusedGroup();
    if (!focused) return;
    for (const g of state.workspace.groups.slice()) {
      if (g === focused) continue;
      for (const tabId of g.tabIds) {
        if (!focused.tabIds.includes(tabId)) focused.tabIds.push(tabId);
      }
      g.tabIds = [];
      dissolveGroup(g.id);
    }
    renderTabStrip();
    swapEditorToActiveTab();
    renderResultsForActiveTab();
    syncToolbarToActiveTab();
    updateSplitButtonIcon();
    persistWorkspace();
  }

  // Add/remove a `.focused` class on each .editor-group element so CSS
  // can paint the accent on whichever pane Run / keys / orient-toggle
  // currently target. Only meaningful when there's more than one group;
  // single-pane mode looks the same as before.
  function refreshFocusedGroupHighlight() {
    const showHighlight = state.workspace.groups.length > 1;
    for (const g of state.workspace.groups) {
      const el = groupElementById(g.id);
      if (!el) continue;
      el.classList.toggle('focused', showHighlight && g.id === state.workspace.focusedGroupId);
    }
  }

  // Toggle the Split button icon between "split" and "merge" appearance
  // so the user can tell which action the next click will perform.
  function updateSplitButtonIcon() {
    const btn = document.getElementById('split-toggle');
    if (!btn) return;
    const isSplit = state.workspace.groups.length >= 2;
    btn.title = isSplit
      ? 'Merge panes back into one'
      : 'Split editor into two side-by-side panes';
    btn.setAttribute('aria-label', btn.title);
    // The two-rect icon uses different fills to hint at split vs merge.
    btn.classList.toggle('is-split', isSplit);
  }

  // ===== Boot =====
  function boot() {
    applyTheme(loadTheme());
    document.getElementById('theme-toggle').addEventListener('click', toggleTheme);

    const name = workspaceName();
    registerWorkspace(name);
    state.workspace = loadWorkspace(name);

    rebuildWsSelect();
    document.getElementById('ws-select').addEventListener('change', (e) => {
      const target = e.target.value;
      location.hash = target === 'default' ? '' : target;
    });
    document.getElementById('ws-new').addEventListener('click', newWorkspace);
    document.getElementById('ws-delete').addEventListener('click', deleteWorkspace);
    window.addEventListener('hashchange', onHashChange);
    setupCrossWindowSync();

    // Reconcile DOM with the workspace's groups before any rendering —
    // this both wires the existing first-group toolbar handlers and
    // stamps out additional .editor-group nodes for any groups loaded
    // from a previously-saved split. Orientation is applied after the
    // groups exist so the focused group's wrapper picks up the .horizontal
    // class before Monaco measures.
    reconcileGroupDom();
    applyGroupContainerOrientation(
      localStorage.getItem(GROUP_ORIENTATION_STORE) === 'stack'
        ? 'stack' : 'side-by-side');
    for (const g of state.workspace.groups) applyEditorOrientationForGroup(g);
    updateSplitButtonIcon();

    renderTabStrip();
    swapEditorToActiveTab();
    renderResultsForActiveTab();
    syncToolbarToActiveTab();

    initCatalogSidebar();

    document.getElementById('split-toggle').addEventListener('click', splitFocusedGroup);

    // Esc cancels the active tab's in-flight query. The browser aborts
    // the fetch, the server's RequestAborted token fires through
    // plan.ExecuteAsync, and the catch block sees AbortError and renders
    // the result as "cancelled". Idle Esc (no running query on the
    // active tab) falls through to other handlers (modal close, etc.).
    window.addEventListener('keydown', (e) => {
      if (e.key !== 'Escape') return;
      const tab = activeTab();
      if (tab && tab.running) cancelActiveTabRun();
    });

    // Ctrl/Cmd+Enter runs the focused pane's active tab. Routed at
    // window level (in capture phase) so it bypasses Monaco's
    // per-editor command system: that system fires for whichever
    // editor has Monaco-keyboard-focus, which can drift from the user's
    // intent if they clicked the pane's chrome (tab strip, results
    // pane, toolbar) without the click landing inside the editor host.
    // `focusedGroupId` is updated on every pointerdown anywhere in a
    // pane, so it reliably tracks "the pane the user just touched."
    window.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter') return;
      if (!(e.ctrlKey || e.metaKey)) return;
      if (e.shiftKey || e.altKey) return;
      // Skip when typing into a non-editor input (workspace name,
      // max-rows etc.). Monaco's hidden textarea inside `.editor-host`
      // is allowed through so Ctrl+Enter still works while typing.
      const t = e.target;
      if (t && /^(INPUT|SELECT)$/.test(t.tagName) &&
          !(t.closest && t.closest('.editor-host'))) {
        return;
      }
      e.preventDefault();
      e.stopPropagation();
      run();
    }, /*capture=*/true);

    // Three save triggers ensure pending edits reach localStorage even
    // when the page is yanked unexpectedly (dev-server restart, browser
    // crash, system sleep):
    //   beforeunload — fires on most navigation/close events.
    //   pagehide — fires more reliably than beforeunload on bfcache /
    //     mobile / Firefox tab-close paths.
    //   visibilitychange (hidden) — fires when the user alt-tabs to a
    //     terminal (e.g. to restart WebDev) before any unload signal,
    //     so edits made up to the moment of switching are persisted.
    window.addEventListener('beforeunload', flushPendingSave);
    window.addEventListener('pagehide', flushPendingSave);
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') flushPendingSave();
    });

    // Now try to upgrade the textarea to Monaco. If the loader fails (offline,
    // CDN blocked) the textarea stays in place and everything still works.
    if (window.require) initMonaco();
  }

  boot();
