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
// json-render functions are consumed by results.tsx; main.ts no longer
// needs them.
import * as IDB from './idb.js';
import { loadTheme, applyTheme, toggleTheme } from './theme.js';
import {
  alertModal,
  confirmModal,
  promptModal,
  openImageLightbox,
} from './modal.js';
import { mountResultsPane, unmountResultsPane } from './react-mount.js';
import { runQuery, cancelActiveTabRun, setRunHooks } from './run.js';
import {
  state,
  type Tab,
  type Group,
  type AppState,
  type EditorOrientation,
  STATE_STORAGE_KEY,
  THEME_STORAGE_KEY,
  EDITOR_ORIENTATION_STORE,
  readJson,
  writeJson,
  uuid,
  migrateLegacyKeys,
  freshTab,
  focusedGroup,
  groupOfTab,
  getDisplayingGroups,
  addTabIdToFocusedGroup,
  removeTabIdFromItsGroup,
  loadInitialState,
  seedFreshState,
  persistState,
  scheduleSave,
  flushPendingSave,
  activeTab,
} from './state.js';

  // Storage keys, types, and the state singleton — see ./state.ts
  // Run the legacy-key migration before any other state code looks at
  // localStorage so the new STATE_STORAGE_KEY is populated when present.
  migrateLegacyKeys();

  // IndexedDB result store — see ./idb.ts

  // Theme — see ./theme.ts

  // ===== Group DOM helpers =====
  // Lookup a group's DOM root by id.
  function groupElementById(groupId) {
    return document.querySelector(`.editor-group[data-group-id="${groupId}"]`);
  }

  // Tear down a group: drop it from `groups[]`, dispose its Monaco editor
  // (if any), remove its DOM node, and re-target focus if it was the
  // focused one. Caller is responsible for ensuring the group's tabIds is
  // empty (or that orphaned tabs are handled elsewhere). Reconciles the
  // group container at the end so the splitter / orient-toggle button /
  // ratio adjust to the new group count.
  function dissolveGroup(groupId) {
    const idx = state.groups.findIndex(g => g.id === groupId);
    if (idx < 0) return;
    state.groups.splice(idx, 1);
    const editor = monacoEditorsByGroup.get(groupId);
    if (editor) {
      editor.setModel(null);
      editor.dispose();
      monacoEditorsByGroup.delete(groupId);
    }
    fallbackTextareasByGroup.delete(groupId);
    unmountResultsPane(groupId);
    const groupEl = document.querySelector(`.editor-group[data-group-id="${groupId}"]`);
    if (groupEl) groupEl.remove();
    if (state.focusedGroupId === groupId) {
      state.focusedGroupId = state.groups[0]?.id;
    }
    reconcileGroupDom();
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
    const id = state.focusedGroupId || DEFAULT_GROUP_ID;
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
    for (const g of state.groups) renderTabStripForGroup(g);
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
      const t = state.tabs.find(x => x.id === tabId);
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
    const tab = state.tabs.find(t => t.id === id);
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
        persistState();
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
    const tab = state.tabs.find(t => t.id === id);
    if (!tab) return;
    tab.pinned = !tab.pinned;
    renderTabStrip();
    persistState();
  }

  // Custom context menu — single shared element, repositioned on each open.
  // Dismissed by any outside pointerdown, scroll, resize, or Escape.
  let tabContextMenu = null;
  function showTabContextMenu(x, y, tabId) {
    closeTabContextMenu();
    const tab = state.tabs.find(t => t.id === tabId);
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

  function newTab() { newTabInGroup(state.focusedGroupId); }

  // Create a new tab and add it to the specified group. Each group's "+"
  // button calls this with its own group id, so + in pane B creates the
  // tab in pane B regardless of which group is currently focused.
  function newTabInGroup(groupId) {
    const group = state.groups.find(g => g.id === groupId);
    if (!group) return;
    const n = state.tabs.length + 1;
    const t = freshTab(n);
    state.tabs.push(t);
    group.tabIds.push(t.id);
    state.focusedGroupId = group.id;
    group.activeTabId = t.id;
    renderTabStrip();
    swapEditorToActiveTab();
    // results pane re-renders via valtio subscription
    syncToolbarToActiveTab();
    persistState();
  }

  function activateTab(id) {
    const group = groupOfTab(id);
    if (!group) return;
    const alreadyActive = group.id === state.focusedGroupId
                       && group.activeTabId === id;
    if (alreadyActive) return;
    state.focusedGroupId = group.id;
    group.activeTabId = id;
    renderTabStrip();
    swapEditorToActiveTab();
    // results pane re-renders via valtio subscription
    syncToolbarToActiveTab();
    persistState();
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
      persistState();
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
    state.focusedGroupId = dstGroup.id;

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
    // results pane re-renders via valtio subscription
    syncToolbarToActiveTab();
    persistState();
  }

  function clearDropIndicators() {
    // Sweep across every group's tab strip so cross-group drag indicators
    // are cleared too (no-op for groups that don't have any).
    document.querySelectorAll('.tab-strip .tab.drop-before, .tab-strip .tab.drop-after')
      .forEach(el => el.classList.remove('drop-before', 'drop-after'));
  }

  async function closeTab(id) {
    const tab = state.tabs.find(t => t.id === id);
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
    IDB.deleteResult(id).catch(err =>
      console.warn(`Couldn't delete IDB result for tab ${id}:`, err));
    const containingGroup = groupOfTab(id);
    state.tabs = state.tabs.filter(t => t.id !== id);
    // Record a tombstone so the multi-window merge in persistState
    // doesn't resurrect this tab from a stale on-disk snapshot.
    if (!Array.isArray(state.deletedTabIds)) state.deletedTabIds = [];
    state.deletedTabIds.push(id);
    if (containingGroup) {
      containingGroup.tabIds = containingGroup.tabIds.filter(tid => tid !== id);
      if (containingGroup.tabIds.length === 0) {
        if (state.groups.length === 1) {
          // Last tab in the only group → seed a fresh tab so the user
          // never lands in an empty workspace.
          const fresh = freshTab(1);
          state.tabs.push(fresh);
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
    // results pane re-renders via valtio subscription
    syncToolbarToActiveTab();
    persistState();
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
    return monacoEditorsByGroup.get(state.focusedGroupId) || null;
  }
  function focusedFallbackTextarea() {
    return fallbackTextareasByGroup.get(state.focusedGroupId) || null;
  }

  function getOrCreateModel(tab) {
    if (!window.monaco) return null;
    let model = monacoModels.get(tab.id);
    if (!model) {
      model = monaco.editor.createModel(tab.sql || '', 'sql');
      // Per-model listener so cross-tab switches don't pile up listeners.
      model.onDidChangeContent(() => {
        const t = state.tabs.find(x => monacoModels.get(x.id) === model);
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
    for (const g of state.groups) swapEditorForGroup(g);
  }

  // Attach the model for `group.activeTabId` to whichever editor (Monaco
  // or fallback) belongs to that group. Focuses the editor only if it
  // belongs to the focused group, so split-pane swaps don't yank the
  // caret away from where the user is typing.
  function swapEditorForGroup(group) {
    const tab = state.tabs.find(t => t.id === group.activeTabId);
    if (!tab) return;
    const editor = monacoEditorsByGroup.get(group.id);
    if (editor) {
      editor.setModel(getOrCreateModel(tab));
      if (group.id === state.focusedGroupId) editor.focus();
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
        runQuery(getEditorSelection());
      }
    });
    ta.addEventListener('input', () => {
      // Edits go to whichever tab is active in *this* group — not the
      // focused group, in case the user is typing into a non-focused pane.
      const group = state.groups.find(g => g.id === groupId);
      if (!group) return;
      const tab = state.tabs.find(t => t.id === group.activeTabId);
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
      if (state.focusedGroupId !== groupId) {
        state.focusedGroupId = groupId;
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
      for (const g of state.groups) bootMonacoForGroup(g.id);
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

  // Refresh every group's toolbar to match its own active tab. Each
  // group's toolbar reflects ITS active tab's settings — Run button
  // label, maxRows, trace — so the two panes stay independent. The
  // header status only reflects the focused group.
  function syncToolbarToActiveTab() {
    for (const g of state.groups) syncToolbarForGroup(g);
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
    const tab = state.tabs.find(t => t.id === group.activeTabId);
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

  // ===== Cross-window sync =====
  // The browser's `storage` event fires on every *other* same-origin
  // window/tab when localStorage changes here, so this is how Window A
  // learns about Window B's saves without a reload.
  //
  // Two keys are interesting:
  //   STORE.state — our state was just rewritten by another window. Merge
  //     in any new tabs and apply remote tombstones.
  //   STORE.theme — the user toggled theme in another window. Mirror it.
  //
  // We deliberately don't try to reconcile *content edits* on tabs that
  // exist in both windows — picking up a remote edit mid-typing would
  // stomp the local user's keystrokes. In-memory always wins for tabs we
  // already have; only adds and remote-tombstone removals propagate.
  function applyRemoteSnapshot(onDisk) {
    if (!onDisk || !Array.isArray(onDisk.tabs)) return;

    const inMemoryById = new Map(state.tabs.map(t => [t.id, t]));
    const localTombs = new Set(state.deletedTabIds || []);
    const diskTombs = new Set(Array.isArray(onDisk.deletedTabIds) ? onDisk.deletedTabIds : []);

    let tabsChanged = false;
    let activeMissing = false;

    // Apply remote tombstones first — drop tabs another window deleted.
    for (const id of diskTombs) {
      if (localTombs.has(id)) continue;       // already gone here
      const idx = state.tabs.findIndex(t => t.id === id);
      if (idx === -1) continue;               // we never saw it
      state.tabs.splice(idx, 1);
      removeTabIdFromItsGroup(id);
      tabsChanged = true;
      if (monacoModels.has(id)) {
        monacoModels.get(id).dispose();
        monacoModels.delete(id);
      }
      // IDB result was deleted by the closing window already.
      if (id === state.activeTabId) activeMissing = true;
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
      state.tabs.push({
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
    // doesn't drop them. Cap FIFO at 500 to match persistState.
    if (diskTombs.size > 0) {
      const merged = new Set([...localTombs, ...diskTombs]);
      const arr = [...merged];
      state.deletedTabIds = arr.length > 500 ? arr.slice(arr.length - 500) : arr;
    }

    // If our active tab got tombstoned remotely, fall back to the first
    // surviving tab in the focused group. If that group is now empty we
    // seed it with a fresh tab so the editor is never tabless.
    if (activeMissing) {
      const g = focusedGroup();
      if (g && g.tabIds.length === 0) {
        const fresh = freshTab(1);
        state.tabs.push(fresh);
        g.tabIds.push(fresh.id);
      }
      if (g) g.activeTabId = g.tabIds[0];
      swapEditorToActiveTab();
      // results pane re-renders via valtio subscription
      syncToolbarToActiveTab();
    }

    if (tabsChanged) renderTabStrip();
  }

  function setupCrossWindowSync() {
    window.addEventListener('storage', (e) => {
      // Same-tab writes don't fire this event, so we'll only see real
      // cross-window changes here.
      if (!e.key) return;

      if (e.key === STORE.theme && e.newValue) {
        applyTheme(e.newValue);
        return;
      }

      if (e.key === STORE.state) {
        if (!e.newValue) return;
        let snapshot;
        try { snapshot = JSON.parse(e.newValue); }
        catch { return; }
        applyRemoteSnapshot(snapshot);
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
  // EDITOR_ORIENTATION_STORE — see ./state.ts (imported above).
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
    const t = freshTab(state.tabs.length + 1);
    t.name = name.length > 32 ? name.slice(0, 31) + '…' : name;
    t.sql = sql;
    // Seed sqlOfLastRun so the tab opens clean — the dirty marker only
    // appears once the user actually edits the seeded text.
    t.sqlOfLastRun = sql;
    state.tabs.push(t);
    addTabIdToFocusedGroup(t.id);
    state.activeTabId = t.id;
    renderTabStrip();
    swapEditorToActiveTab();
    // results pane re-renders via valtio subscription
    syncToolbarToActiveTab();
    persistState();
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
    persistState();
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
  // the live group by id from `state` rather than closing over
  // the group object, so they keep working after a workspace switch
  // reuses the same DOM node for a fresh workspace's group.
  function wireGroupToolbar(groupEl, group) {
    if (groupEl.dataset.toolbarWired === '1') return;
    groupEl.dataset.toolbarWired = '1';
    const groupId = group.id;
    const liveGroup = () => state.groups.find(g => g.id === groupId);
    const liveTab = () => {
      const g = liveGroup();
      return g ? state.tabs.find(t => t.id === g.activeTabId) : null;
    };

    // Each handler focuses its group as a side effect, so clicks on the
    // non-focused pane "follow" — the user's interaction defines focus
    // even when the keyboard hasn't moved yet.
    runBtnEl(groupEl).addEventListener('click', () => {
      const g = liveGroup();
      if (!g) return;
      state.focusedGroupId = g.id;
      const tab = liveTab();
      if (tab && tab.running) cancelActiveTabRun();
      else runQuery(getEditorSelection());
    });
    maxRowsInputEl(groupEl).addEventListener('input', (e) => {
      const g = liveGroup();
      if (!g) return;
      state.focusedGroupId = g.id;
      const tab = liveTab();
      if (!tab) return;
      const v = parseInt(e.target.value, 10);
      tab.maxRows = (Number.isFinite(v) && v > 0) ? v : 200;
      scheduleSave();
    });
    traceToggleEl(groupEl).addEventListener('change', (e) => {
      const g = liveGroup();
      if (!g) return;
      state.focusedGroupId = g.id;
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
      if (state.focusedGroupId !== groupId && liveGroup()) {
        state.focusedGroupId = groupId;
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

  // Ensure #group-container's children mirror state.groups in
  // order. Adds any missing .editor-group nodes, removes any orphaned
  // ones, and (idempotently) wires every group's toolbar + resize handle.
  // Also stands up an editor (Monaco if loaded, else the textarea
  // fallback) for any group that doesn't have one yet — so split,
  // workspace-switch, and boot all share one path.
  function reconcileGroupDom() {
    const container = document.getElementById('group-container');
    if (!container) return;
    const validIds = new Set(state.groups.map(g => g.id));
    for (const el of Array.from(container.querySelectorAll(':scope > .editor-group'))) {
      if (!validIds.has(el.dataset.groupId)) {
        unmountResultsPane(el.dataset.groupId);
        el.remove();
      }
    }
    let prevEl = null;
    for (const g of state.groups) {
      let el = groupElementById(g.id);
      const isNew = !el;
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
      // React results pane: idempotent — mountResultsPane no-ops if a
      // root is already attached for this group. Called for every group
      // every reconcile so the seed group baked into the HTML (which
      // hits this branch with isNew=false) still gets mounted on first
      // boot.
      void isNew;
      const paneEl = el.querySelector('.results-pane');
      if (paneEl) mountResultsPane(paneEl, g.id);
      prevEl = el;
    }

    // Splitter between groups. Only meaningful with 2+ groups; otherwise
    // remove it so the single pane fills the container without a gap.
    // Group-level controls (merge / swap orientation) live inside the
    // splitter so they're discoverable on hover and stay close to the
    // visual "seam" between panes.
    let splitter = document.getElementById('group-resize');
    if (state.groups.length >= 2) {
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
    if (state.groups.length >= 2) {
      mergeAllGroupsIntoFocused();
      return;
    }
    const source = focusedGroup();
    if (!source) return;
    const tabIdToMove = source.activeTabId;
    const moveIdx = source.tabIds.indexOf(tabIdToMove);
    if (moveIdx >= 0) source.tabIds.splice(moveIdx, 1);
    if (source.tabIds.length === 0) {
      const fresh = freshTab(state.tabs.length + 1);
      state.tabs.push(fresh);
      source.tabIds.push(fresh.id);
    }
    source.activeTabId = source.tabIds[0];

    const newGroupId = 'g' + Date.now().toString(36);
    state.groups.push({
      id: newGroupId,
      tabIds: [tabIdToMove],
      activeTabId: tabIdToMove,
      // Inherit the source pane's current editor orientation so the
      // newly-spawned pane "feels like" the one it came from.
      editorOrientation: source.editorOrientation || 'vertical',
    });
    state.focusedGroupId = newGroupId;

    // reconcileGroupDom creates the new group's DOM and stands up its
    // editor (Monaco if loaded, else fallback) in one shot.
    reconcileGroupDom();

    renderTabStrip();
    swapEditorToActiveTab();
    // results pane re-renders via valtio subscription
    syncToolbarToActiveTab();
    updateSplitButtonIcon();
    persistState();
  }

  // Inverse of splitFocusedGroup: pull every other group's tabs into the
  // focused group, then dispose those groups. Source-group ordering wins
  // for the merged tab strip; the focused group's active tab stays
  // active.
  function mergeAllGroupsIntoFocused() {
    if (state.groups.length < 2) return;
    const focused = focusedGroup();
    if (!focused) return;
    for (const g of state.groups.slice()) {
      if (g === focused) continue;
      for (const tabId of g.tabIds) {
        if (!focused.tabIds.includes(tabId)) focused.tabIds.push(tabId);
      }
      g.tabIds = [];
      dissolveGroup(g.id);
    }
    renderTabStrip();
    swapEditorToActiveTab();
    // results pane re-renders via valtio subscription
    syncToolbarToActiveTab();
    updateSplitButtonIcon();
    persistState();
  }

  // Add/remove a `.focused` class on each .editor-group element so CSS
  // can paint the accent on whichever pane Run / keys / orient-toggle
  // currently target. Only meaningful when there's more than one group;
  // single-pane mode looks the same as before.
  function refreshFocusedGroupHighlight() {
    const showHighlight = state.groups.length > 1;
    for (const g of state.groups) {
      const el = groupElementById(g.id);
      if (!el) continue;
      el.classList.toggle('focused', showHighlight && g.id === state.focusedGroupId);
    }
  }

  // Toggle the Split button icon between "split" and "merge" appearance
  // so the user can tell which action the next click will perform.
  function updateSplitButtonIcon() {
    const btn = document.getElementById('split-toggle');
    if (!btn) return;
    const isSplit = state.groups.length >= 2;
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

    // run.ts mutates state and IDB but doesn't own toolbar / tab-strip
    // / sidebar renderers — wire those callbacks here.
    setRunHooks({
      renderTabStrip,
      syncToolbar: syncToolbarToActiveTab,
      refreshSidebarForSql,
    });

    loadInitialState();
    setupCrossWindowSync();

    // Reconcile DOM with the loaded groups before any rendering — this
    // both wires the existing first-group toolbar handlers and stamps
    // out additional .editor-group nodes for any groups loaded from a
    // previously-saved split. Orientation is applied after the groups
    // exist so the focused group's wrapper picks up the .horizontal
    // class before Monaco measures.
    reconcileGroupDom();
    applyGroupContainerOrientation(
      localStorage.getItem(GROUP_ORIENTATION_STORE) === 'stack'
        ? 'stack' : 'side-by-side');
    for (const g of state.groups) applyEditorOrientationForGroup(g);
    updateSplitButtonIcon();

    renderTabStrip();
    swapEditorToActiveTab();
    // results pane re-renders via valtio subscription
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
      runQuery(getEditorSelection());
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
