import { proxy, subscribe } from 'valtio';
import {
  hydrateFunctionForm,
  serializeFunctionForm,
  type PersistedFunctionForm,
} from './functionForm';

// Query-editor pane tree. PR 1 shipped a flat `tabs[]` model; PR 7 lifts
// that into a recursive binary tree of panes so the user can split the
// editor surface VSCode-style. The shape:
//
//   PaneNode = LeafPane { tabs: Tab[], activeTabId }
//            | SplitPane { orientation, children: [PaneNode, PaneNode] }
//
// Binary splits map 1:1 onto a `ResizablePanelGroup` with two `Panel`s;
// N-way splits are achievable by nesting splits of the same orientation.
// Keeping it binary keeps state mutations small and lossless under
// drag-to-edge.
//
// All `Tab` instances in the tree are unique by `id`. Closing the last
// tab in a non-root leaf collapses the leaf into its sibling so a
// pane never hangs around empty.

/**
 * Discriminator for what the tab body renders.
 *  - `sql`: Monaco editor + results pane (the original tab shape).
 *  - `function`: a kind-driven form for invoking a single scalar function
 *    with named-parameter inputs (file upload for binary kinds).
 *  - `models`: the model catalog browser. Always present as a pinned tab
 *    in the main window's first leaf — see `MODELS_TAB_ID` + the boot
 *    path's `ensureModelsTab` injection.
 *  - `datasets`: the dataset catalog browser. Pinned alongside Models —
 *    see `DATASETS_TAB_ID`.
 *  - `settings`: the app settings page. Pinned alongside Models in the
 *    main window's first leaf — see `SETTINGS_TAB_ID`.
 *  - `docs`: the bundled markdown documentation browser. Pinned
 *    alongside Models / Settings — see `DOCS_TAB_ID`.
 */
export type TabKind = 'sql' | 'function' | 'models' | 'datasets' | 'settings' | 'docs';

/**
 * Well-known ids for the permanent pinned tabs. Stable across boots so
 * the persisted tree round-trips without duplication, and so guards
 * (`closeTab`, `moveTab`, `splitLeaf`) can short-circuit by id check as
 * well as by `pinned`.
 */
export const MODELS_TAB_ID = 'pinned-models';
export const DATASETS_TAB_ID = 'pinned-datasets';
export const SETTINGS_TAB_ID = 'pinned-settings';
export const DOCS_TAB_ID = 'pinned-docs';

/** Ordered list of all pinned tab kinds, in left-to-right strip order. */
const PINNED_KINDS: readonly TabKind[] = ['settings', 'docs', 'models', 'datasets'];

export interface Tab {
  /** Stable identifier; used as Monaco model key and execution-state key. */
  id: string;
  /** Display name on the strip. Defaults to "Untitled-N" until renamed. */
  title: string;
  /** What this tab represents. */
  kind: TabKind;
  /** Current editor text. Mirrors the Monaco model when the editor is mounted. */
  sql: string;
  /** True when `sql` differs from the last persisted-or-saved baseline. */
  dirty: boolean;
  /**
   * Editor-pane share of the editor/results vertical split, as a
   * percentage (0–100). Undefined means "use the default 65". Persisted
   * so the layout survives reload.
   */
  editorSize?: number;
  /**
   * Pinned tabs can't be closed, moved between leaves, split off, or
   * dragged out into a torn-out window. Used today only by the permanent
   * Models tab; the field is general so future pinned surfaces (e.g.
   * "Tables", "Files") can reuse the same guards.
   */
  pinned?: boolean;
}

export interface LeafPane {
  kind: 'leaf';
  /** Stable identifier; used as the focused-leaf key + DnD source/target. */
  id: string;
  tabs: Tab[];
  /** Null when the leaf has no tabs (transient — empty non-root leaves collapse). */
  activeTabId: string | null;
}

export type SplitOrientation = 'horizontal' | 'vertical';

export interface SplitPane {
  kind: 'split';
  id: string;
  /**
   * `horizontal` = side-by-side (children stack along the X axis).
   * `vertical`   = stacked (children stack along the Y axis).
   * Matches the semantics of `ResizablePanelGroup.orientation`.
   */
  orientation: SplitOrientation;
  children: [PaneNode, PaneNode];
}

export type PaneNode = LeafPane | SplitPane;

interface PanesState {
  root: PaneNode;
  /**
   * The leaf whose editor most recently had focus. Used by the toolbar /
   * keybinds to decide which tab to act on. Always points at a live
   * leaf in `root`; actions keep this invariant.
   */
  focusedLeafId: string;
}

const STORAGE_KEY = 'datumv:panes';
const SAVE_DEBOUNCE_MS = 500;

// No layout cap. The per-axis rule we tried earlier turned out to be a
// poor fit for "max columns" — depending on tree shape, a user could
// still produce arbitrarily wide layouts by always dropping on the
// edge of the deeper subtree. Rather than chase a constraint that
// doesn't match user intent, splits are unbounded; the user gets to
// arrange their workspace however they want.

function newId(prefix: string): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return `${prefix}-${crypto.randomUUID()}`;
  }
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1e6)}`;
}

function newTabId(): string {
  return newId('tab');
}

function newLeafId(): string {
  return newId('leaf');
}

function newSplitId(): string {
  return newId('split');
}

function nextUntitledTitle(tree: PaneNode): string {
  const taken = new Set<number>();
  forEachTab(tree, (t) => {
    const m = /^Untitled-(\d+)$/.exec(t.title);
    if (m) taken.add(parseInt(m[1], 10));
  });
  let n = 1;
  while (taken.has(n)) n++;
  return `Untitled-${n}`;
}

// ────────── Tree traversal helpers ──────────

function forEachTab(node: PaneNode, fn: (t: Tab) => void): void {
  if (node.kind === 'leaf') {
    for (const t of node.tabs) fn(t);
  } else {
    forEachTab(node.children[0], fn);
    forEachTab(node.children[1], fn);
  }
}

function forEachLeaf(node: PaneNode, fn: (l: LeafPane) => void): void {
  if (node.kind === 'leaf') {
    fn(node);
  } else {
    forEachLeaf(node.children[0], fn);
    forEachLeaf(node.children[1], fn);
  }
}

export function findLeaf(node: PaneNode, leafId: string): LeafPane | null {
  if (node.kind === 'leaf') return node.id === leafId ? node : null;
  return findLeaf(node.children[0], leafId) ?? findLeaf(node.children[1], leafId);
}

export function findTab(
  node: PaneNode,
  tabId: string,
): { leaf: LeafPane; tab: Tab; index: number } | null {
  if (node.kind === 'leaf') {
    const index = node.tabs.findIndex((t) => t.id === tabId);
    if (index < 0) return null;
    return { leaf: node, tab: node.tabs[index], index };
  }
  return (
    findTab(node.children[0], tabId) ?? findTab(node.children[1], tabId)
  );
}

function firstLeaf(node: PaneNode): LeafPane {
  if (node.kind === 'leaf') return node;
  return firstLeaf(node.children[0]);
}

/**
 * Walks the tree and returns the parent split + which side `target` is on,
 * or null when `target` is the root. Used by collapse / split-replace.
 */
function findParent(
  root: PaneNode,
  target: PaneNode,
): { parent: SplitPane; side: 0 | 1 } | null {
  if (root.kind === 'leaf') return null;
  if (root.children[0] === target) return { parent: root, side: 0 };
  if (root.children[1] === target) return { parent: root, side: 1 };
  return (
    findParent(root.children[0], target) ??
    findParent(root.children[1], target)
  );
}

// ────────── Initial state + persistence ──────────

interface PersistedTab {
  id: string;
  title: string;
  kind?: TabKind;
  sql: string;
  editorSize?: number;
  pinned?: boolean;
  /**
   * Function-tab form state. Absent for SQL tabs and for function tabs
   * the user hasn't interacted with yet. File handles don't survive the
   * round-trip — the `fileNames` mirror is included as a display hint,
   * but actual `File` objects need to be re-selected on first run after
   * rehydrate.
   */
  functionForm?: PersistedFunctionForm;
}

type PersistedNode =
  | { kind: 'leaf'; id: string; tabs: PersistedTab[]; activeTabId: string | null }
  | {
      kind: 'split';
      id: string;
      orientation: SplitOrientation;
      children: [PersistedNode, PersistedNode];
    };

interface PersistedState {
  root: PersistedNode;
  focusedLeafId: string;
}

function readPersisted(): PanesState | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<PersistedState>;
    if (!parsed.root) return null;
    const root = parsePersistedNode(parsed.root);
    if (!root) return null;
    // Validate the focused leaf still exists; fall back to the first leaf.
    const focused =
      typeof parsed.focusedLeafId === 'string' && findLeaf(root, parsed.focusedLeafId)
        ? parsed.focusedLeafId
        : firstLeaf(root).id;
    return { root, focusedLeafId: focused };
  } catch {
    return null;
  }
}

function parsePersistedNode(raw: PersistedNode | undefined): PaneNode | null {
  if (!raw) return null;
  if (raw.kind === 'leaf') {
    if (typeof raw.id !== 'string') return null;
    if (!Array.isArray(raw.tabs)) return null;
    const tabs: Tab[] = [];
    for (const t of raw.tabs) {
      if (
        typeof t?.id === 'string' &&
        typeof t.title === 'string' &&
        typeof t.sql === 'string'
      ) {
        const kind: TabKind =
          t.kind === 'function'
            ? 'function'
            : t.kind === 'models'
              ? 'models'
              : t.kind === 'datasets'
                ? 'datasets'
                : t.kind === 'settings'
                  ? 'settings'
                  : t.kind === 'docs'
                    ? 'docs'
                    : 'sql';
        tabs.push({
          id: t.id,
          title: t.title,
          kind,
          sql: t.sql,
          dirty: false,
          editorSize:
            typeof t.editorSize === 'number' &&
            t.editorSize > 0 &&
            t.editorSize < 100
              ? t.editorSize
              : undefined,
          pinned: t.pinned === true ? true : undefined,
        });
        // Restore the function-form proxy slot before the React tree
        // mounts the form. `ensureFunctionForm` would otherwise create
        // a blank slot on first render and overwrite the persisted
        // selection / values.
        if (kind === 'function' && t.functionForm) {
          hydrateFunctionForm(t.id, t.functionForm);
        }
      }
    }
    if (tabs.length === 0) return null;
    const activeTabId =
      typeof raw.activeTabId === 'string' &&
      tabs.some((t) => t.id === raw.activeTabId)
        ? raw.activeTabId
        : tabs[0].id;
    return { kind: 'leaf', id: raw.id, tabs, activeTabId };
  }
  if (raw.kind === 'split') {
    if (typeof raw.id !== 'string') return null;
    if (raw.orientation !== 'horizontal' && raw.orientation !== 'vertical') return null;
    if (!Array.isArray(raw.children) || raw.children.length !== 2) return null;
    const a = parsePersistedNode(raw.children[0]);
    const b = parsePersistedNode(raw.children[1]);
    if (!a || !b) return null;
    return { kind: 'split', id: raw.id, orientation: raw.orientation, children: [a, b] };
  }
  return null;
}

/**
 * Reads a tab seed from the URL hash placed there by Electron's main
 * process during tab tear-out. Format: `#/tab-window/seed=<base64url>`.
 * Returns null when the hash doesn't match — this is the boot path for
 * the regular main window.
 */
function readSeedFromHash(): Tab | null {
  if (typeof window === 'undefined') return null;
  const hash = window.location.hash || '';
  const match = /^#\/tab-window\/seed=([A-Za-z0-9_-]+=*)$/.exec(hash);
  if (!match) return null;
  try {
    // base64url decode — atob doesn't handle the URL-safe variant
    // directly, so normalize first.
    const normalized = match[1].replace(/-/g, '+').replace(/_/g, '/');
    const padded = normalized + '='.repeat((4 - (normalized.length % 4)) % 4);
    const json = atob(padded);
    // The seed may carry a `functionForm` slice when a function tab is
    // being torn out — extract it before the partial-Tab type-narrowing
    // (PersistedFunctionForm isn't on Tab itself).
    const parsed = JSON.parse(json) as Partial<Tab> & {
      functionForm?: PersistedFunctionForm;
    };
    if (
      typeof parsed.id !== 'string' ||
      typeof parsed.title !== 'string' ||
      typeof parsed.sql !== 'string'
    ) {
      return null;
    }
    const kind: TabKind =
      parsed.kind === 'function' ? 'function' : 'sql';
    // Restore the function-form proxy BEFORE the returned Tab is
    // committed to panesState — `ensureFunctionForm` would otherwise
    // clobber the persisted slice on the first render.
    if (kind === 'function' && parsed.functionForm) {
      hydrateFunctionForm(parsed.id, parsed.functionForm);
    }
    return {
      id: parsed.id,
      title: parsed.title,
      kind,
      sql: parsed.sql,
      dirty: false,
      editorSize:
        typeof parsed.editorSize === 'number' &&
        parsed.editorSize > 0 &&
        parsed.editorSize < 100
          ? parsed.editorSize
          : undefined,
    };
  } catch {
    return null;
  }
}

/**
 * True when this renderer was spawned by `tabwindow.spawn` from main
 * (i.e. it's a torn-out window). Torn-out windows are session-scoped:
 * they don't load from or save to localStorage, since their lifecycle
 * is tied to the user's drag-out gesture rather than persistent
 * workspace state. Dragging a tab back to the main window relocates
 * its content there, where persistence picks it up.
 *
 * Also consumed by `App.tsx` to strip the SideNav + chat panel in
 * torn-out windows — those surfaces are main-window concepts.
 */
export const isTornOutWindow: boolean = readSeedFromHash() !== null;

function createInitialState(): PanesState {
  const seed = readSeedFromHash();
  if (seed) {
    // Torn-out windows are single-purpose editor surfaces — no pinned
    // tabs, no AppDock chrome. The Models tab lives only in the main
    // window.
    const leaf: LeafPane = {
      kind: 'leaf',
      id: newLeafId(),
      tabs: [seed],
      activeTabId: seed.id,
    };
    return { root: leaf, focusedLeafId: leaf.id };
  }

  const persisted = readPersisted();
  if (persisted) {
    ensurePinnedTabs(persisted);
    return persisted;
  }

  // First-ever boot: pinned Models / Datasets / Settings / Docs tabs +
  // one Untitled-1 SQL tab. Pinned tabs take indices 0..N so they sit
  // to the LEFT of user-created tabs on the strip, matching VS Code's
  // "pinned tabs at the front" convention.
  const firstUntitled: Tab = {
    id: newTabId(),
    title: 'Untitled-1',
    kind: 'sql',
    sql: '',
    dirty: false,
  };
  const leaf: LeafPane = {
    kind: 'leaf',
    id: newLeafId(),
    tabs: [
      createPinnedTab('settings'),
      createPinnedTab('docs'),
      createPinnedTab('models'),
      createPinnedTab('datasets'),
      firstUntitled,
    ],
    activeTabId: firstUntitled.id,
  };
  return { root: leaf, focusedLeafId: leaf.id };
}

function pinnedIdForKind(kind: TabKind): string {
  switch (kind) {
    case 'models':
      return MODELS_TAB_ID;
    case 'datasets':
      return DATASETS_TAB_ID;
    case 'settings':
      return SETTINGS_TAB_ID;
    case 'docs':
      return DOCS_TAB_ID;
    default:
      throw new Error(`No pinned id for kind ${kind}`);
  }
}

function pinnedTitleForKind(kind: TabKind): string {
  // Titles here are placeholders — pinned chips render icon-only and the
  // visible label comes from i18n at render time. Stored so the localStorage
  // shape stays self-describing for debugging.
  switch (kind) {
    case 'models':
      return 'Models';
    case 'datasets':
      return 'Datasets';
    case 'settings':
      return 'Settings';
    case 'docs':
      return 'Documentation';
    default:
      throw new Error(`No pinned title for kind ${kind}`);
  }
}

function createPinnedTab(kind: TabKind): Tab {
  return {
    id: pinnedIdForKind(kind),
    title: pinnedTitleForKind(kind),
    kind,
    sql: '',
    dirty: false,
    pinned: true,
  };
}

/**
 * Walk the tree; for each pinned kind not yet present, prepend a fresh
 * tab into the first leaf at its canonical position. Idempotent on
 * already-pinned-bearing trees. Called on every boot from persisted
 * state so the invariant "main window always has Models / Settings /
 * Docs at the front of the first leaf" holds even when the persisted
 * localStorage predates one of these tabs.
 */
function ensurePinnedTabs(state: PanesState): void {
  const first = firstLeaf(state.root);
  // Pull every existing pinned tab out of the strip so we can re-insert
  // them at the front in canonical order — handles both "missing kind
  // not yet added" and "persisted state had pinned tabs in a stale
  // order" (e.g. predated a new pinned kind being inserted mid-list).
  const existing = new Map<TabKind, Tab>();
  first.tabs = first.tabs.filter((t) => {
    if (PINNED_KINDS.includes(t.kind)) {
      existing.set(t.kind, t);
      return false;
    }
    return true;
  });
  const canonical: Tab[] = [];
  for (const kind of PINNED_KINDS) {
    canonical.push(existing.get(kind) ?? createPinnedTab(kind));
  }
  first.tabs = [...canonical, ...first.tabs];
}

export const panesState = proxy<PanesState>(createInitialState());

// Listen for "another window received one of our tabs, please drop
// your copy" deliveries from the Electron main process. Fired by the
// destination window's drop handler after a successful cross-window
// receive. Subscription lives for the renderer's lifetime — no
// cleanup needed.
if (typeof window !== 'undefined' && window.electronHost?.isElectron) {
  window.electronHost.onRemoveTab(({ tabId }) => {
    closeTab(tabId);
  });
}

// In a torn-out window, closing the last tab dismisses the window —
// there's nothing else in the shell. The main window deliberately
// stays open with zero tabs (its SideNav + chat surfaces don't
// depend on tab state, so a tabless main is still a useful workspace
// to navigate to a different view).
if (isTornOutWindow) {
  subscribe(panesState, () => {
    if (getAllTabs().length === 0) {
      window.electronHost?.close();
    }
  });
}

// Debounced auto-save. Skipped in torn-out windows so the main window's
// persisted state isn't trampled by a transient secondary window.
let saveTimer: number | undefined;
if (!isTornOutWindow) {
  subscribe(panesState, () => {
    if (saveTimer !== undefined) window.clearTimeout(saveTimer);
    saveTimer = window.setTimeout(() => {
      saveTimer = undefined;
      try {
        const snapshot: PersistedState = {
          root: serializeNode(panesState.root),
          focusedLeafId: panesState.focusedLeafId,
        };
        localStorage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
      } catch {
        // Quota errors / disabled storage — drop the save silently.
      }
    }, SAVE_DEBOUNCE_MS);
  });
}

function serializeNode(node: PaneNode): PersistedNode {
  if (node.kind === 'leaf') {
    return {
      kind: 'leaf',
      id: node.id,
      tabs: node.tabs.map((t) => {
        const persisted: PersistedTab = {
          id: t.id,
          title: t.title,
          kind: t.kind,
          sql: t.sql,
          editorSize: t.editorSize,
          pinned: t.pinned,
        };
        // Function-tab content lives in a sibling proxy keyed by tabId.
        // Pull the slice here so the existing panesState localStorage
        // save carries it without a second persistence channel.
        if (t.kind === 'function') {
          const form = serializeFunctionForm(t.id);
          if (form !== null) persisted.functionForm = form;
        }
        return persisted;
      }),
      activeTabId: node.activeTabId,
    };
  }
  return {
    kind: 'split',
    id: node.id,
    orientation: node.orientation,
    children: [serializeNode(node.children[0]), serializeNode(node.children[1])],
  };
}

// ────────── Snapshot helpers ──────────

/**
 * Returns the leaf the user is currently focused on (toolbar, keybinds,
 * results pane all consult this). Always non-null — the tree always has
 * at least one leaf.
 */
export function getFocusedLeaf(): LeafPane {
  const leaf = findLeaf(panesState.root, panesState.focusedLeafId);
  if (leaf) return leaf;
  // Defensive: if the focused id is stale, snap to the first leaf and
  // repair the state so subsequent reads agree.
  const first = firstLeaf(panesState.root);
  panesState.focusedLeafId = first.id;
  return first;
}

export function getActiveTab(): Tab | null {
  const leaf = getFocusedLeaf();
  if (leaf.activeTabId === null) return null;
  return leaf.tabs.find((t) => t.id === leaf.activeTabId) ?? null;
}

// ────────── Mutation helpers ──────────

function replaceNode(target: PaneNode, replacement: PaneNode): void {
  const found = findParent(panesState.root, target);
  if (!found) {
    panesState.root = replacement;
    return;
  }
  found.parent.children[found.side] = replacement;
}

/**
 * Collapses an empty leaf into its sibling. The root leaf is left
 * untouched — an empty root is allowed (renders a blank surface, see
 * QueryEditorView's null-activeTab branch).
 */
/**
 * Returns the smallest valid insert index for a non-pinned tab — i.e.
 * the count of leading pinned tabs in `leaf`. Used to clamp drop targets
 * so the user can't drop a regular tab to the left of Models.
 */
function firstNonPinnedIndex(leaf: LeafPane): number {
  let i = 0;
  while (i < leaf.tabs.length && leaf.tabs[i].pinned) i++;
  return i;
}

function collapseIfEmpty(leaf: LeafPane): void {
  if (leaf.tabs.length > 0) return;
  const found = findParent(panesState.root, leaf);
  if (!found) return; // root leaf — leave alone.
  const sibling = found.parent.children[found.side === 0 ? 1 : 0];
  replaceNode(found.parent, sibling);
  // If the focused leaf disappeared, retarget at the first leaf in
  // whatever survived. firstLeaf walks into the sibling subtree.
  if (panesState.focusedLeafId === leaf.id) {
    panesState.focusedLeafId = firstLeaf(sibling).id;
  }
}

// ────────── Public actions ──────────

/**
 * Opens a new tab in the targeted leaf (focused leaf by default) and
 * makes it active. Returns the created tab.
 *
 * `kind` controls what the tab body renders: `'sql'` (the default) for
 * a Monaco editor + results pane, `'function'` for the Execute-Function
 * form.
 */
export function openTab(
  initialSql = '',
  leafId?: string,
  kind: TabKind = 'sql',
): Tab {
  const leaf =
    (leafId ? findLeaf(panesState.root, leafId) : null) ?? getFocusedLeaf();
  const tab: Tab = {
    id: newTabId(),
    title: nextUntitledTitle(panesState.root),
    kind,
    sql: initialSql,
    dirty: false,
  };
  leaf.tabs.push(tab);
  leaf.activeTabId = tab.id;
  panesState.focusedLeafId = leaf.id;
  return tab;
}

export function closeTab(tabId: string): void {
  const found = findTab(panesState.root, tabId);
  if (!found) return;
  // Pinned tabs (Models) can't be closed. Guard rather than throw so
  // the keybind / middle-click paths can fire unconditionally.
  if (found.tab.pinned) return;
  const { leaf, index } = found;

  leaf.tabs.splice(index, 1);

  if (leaf.activeTabId === tabId) {
    if (leaf.tabs.length === 0) {
      leaf.activeTabId = null;
    } else {
      const neighbour = leaf.tabs[index] ?? leaf.tabs[index - 1];
      leaf.activeTabId = neighbour.id;
    }
  }

  collapseIfEmpty(leaf);
}

export function selectTab(tabId: string): void {
  const found = findTab(panesState.root, tabId);
  if (!found) return;
  found.leaf.activeTabId = tabId;
  panesState.focusedLeafId = found.leaf.id;
}

export function focusLeaf(leafId: string): void {
  if (findLeaf(panesState.root, leafId)) {
    panesState.focusedLeafId = leafId;
  }
}

export function renameTab(tabId: string, title: string): void {
  const found = findTab(panesState.root, tabId);
  if (!found) return;
  const trimmed = title.trim();
  if (trimmed.length === 0) return;
  found.tab.title = trimmed;
}

export function setTabSql(tabId: string, sql: string): void {
  const found = findTab(panesState.root, tabId);
  if (!found) return;
  if (found.tab.sql === sql) return;
  found.tab.sql = sql;
  found.tab.dirty = true;
}

/**
 * Records the editor-pane percentage of the editor/results vertical
 * split for `tabId`. Called whenever the panel group's layout settles
 * (post-drag, or after a programmatic resize on tab switch).
 *
 * Clamped to (0, 100) so a stuck collapse can't poison the field —
 * the panel library uses 0 / 100 as collapsed states, which would
 * misrender on reload. Otherwise the value is written through
 * verbatim: the panel library already rounds `asPercentage` to three
 * decimal places, and any further "skip small changes" threshold here
 * causes the persisted state to lag the lib's actual state, which
 * shows up as a layout jump on the next reload.
 */
export function setTabEditorSize(tabId: string, size: number): void {
  const found = findTab(panesState.root, tabId);
  if (!found) return;
  if (size <= 0 || size >= 100) return;
  if (found.tab.editorSize === size) return;
  found.tab.editorSize = size;
}

/**
 * Moves `tabId` to `toLeafId` at `insertIndex`. Same-leaf reorder is a
 * special case (handled by index adjustment). Source leaf collapses if
 * the move empties it.
 */
export function moveTab(
  tabId: string,
  toLeafId: string,
  insertIndex: number,
): void {
  const sourceFound = findTab(panesState.root, tabId);
  const target = findLeaf(panesState.root, toLeafId);
  if (!sourceFound || !target) return;
  // Pinned tabs stay anchored to their original leaf at index 0.
  if (sourceFound.tab.pinned) return;

  const { leaf: source, index: fromIndex } = sourceFound;

  if (source === target) {
    // Same-leaf reorder. Splice out + back in. Adjust insertIndex when
    // it's past the removed slot so the user-visible destination matches
    // where the drop indicator was, then clamp away from the pinned
    // prefix so the user can't drop in front of Models.
    const [moved] = source.tabs.splice(fromIndex, 1);
    const adjusted = insertIndex > fromIndex ? insertIndex - 1 : insertIndex;
    const minIndex = firstNonPinnedIndex(source);
    const clamped = Math.max(minIndex, Math.min(adjusted, source.tabs.length));
    source.tabs.splice(clamped, 0, moved);
    source.activeTabId = moved.id;
    panesState.focusedLeafId = source.id;
    return;
  }

  const [moved] = source.tabs.splice(fromIndex, 1);
  const minIndex = firstNonPinnedIndex(target);
  const clamped = Math.max(minIndex, Math.min(insertIndex, target.tabs.length));
  target.tabs.splice(clamped, 0, moved);
  target.activeTabId = moved.id;

  // Repair the source's active tab if we removed the one it pointed at.
  if (source.activeTabId === tabId) {
    if (source.tabs.length === 0) {
      source.activeTabId = null;
    } else {
      const neighbour = source.tabs[fromIndex] ?? source.tabs[fromIndex - 1];
      source.activeTabId = neighbour.id;
    }
  }

  panesState.focusedLeafId = target.id;
  collapseIfEmpty(source);
}

export type SplitSide = 'left' | 'right' | 'top' | 'bottom';

/**
 * Splits `targetLeafId` by pulling `tabId` into a new leaf placed on
 * `side`. The new leaf becomes focused. If the split would empty the
 * source leaf, the resulting structure still works — the source leaf
 * collapses naturally on the next refresh because we run
 * `collapseIfEmpty` here.
 */
export function splitLeaf(
  targetLeafId: string,
  tabId: string,
  side: SplitSide,
): void {
  const target = findLeaf(panesState.root, targetLeafId);
  const sourceFound = findTab(panesState.root, tabId);
  if (!target || !sourceFound) return;
  // Splitting peels the tab off into a new sibling leaf — same lifecycle
  // as moveTab from a pinning perspective.
  if (sourceFound.tab.pinned) return;
  const { leaf: source, index: fromIndex } = sourceFound;

  // Pulling the only tab into a split of its own leaf is a no-op — the
  // user gets the same layout they started with. Bail before mutating.
  if (source === target && source.tabs.length === 1) return;

  const newOrientation: SplitOrientation =
    side === 'left' || side === 'right' ? 'horizontal' : 'vertical';

  const [moved] = source.tabs.splice(fromIndex, 1);

  // Repair source.activeTabId before we possibly collapse the source.
  if (source.activeTabId === tabId) {
    if (source.tabs.length === 0) {
      source.activeTabId = null;
    } else {
      const neighbour = source.tabs[fromIndex] ?? source.tabs[fromIndex - 1];
      source.activeTabId = neighbour.id;
    }
  }

  const newLeaf: LeafPane = {
    kind: 'leaf',
    id: newLeafId(),
    tabs: [moved],
    activeTabId: moved.id,
  };

  // Child order from the side: left/top means the new leaf comes
  // first in the split's child pair; right/bottom means it comes
  // second. Orientation is already resolved above (`newOrientation`).
  const newLeafFirst = side === 'left' || side === 'top';

  // Important: collapse source BEFORE wrapping target in a new split,
  // because the source might BE the target (single tab being split into
  // itself — already handled above, but defensively). Otherwise collapse
  // after the wrap so the parent pointer lookup still finds the target.
  if (source !== target) {
    // Wrap the target leaf in a new split with the new leaf alongside it.
    const newSplit: SplitPane = {
      kind: 'split',
      id: newSplitId(),
      orientation: newOrientation,
      children: newLeafFirst ? [newLeaf, target] : [target, newLeaf],
    };
    replaceNode(target, newSplit);
    collapseIfEmpty(source);
  } else {
    // Same leaf as both source and target: we're peeling a tab off
    // `source` into a sibling leaf. Wrap source (now smaller) in a new
    // split alongside the new leaf.
    const newSplit: SplitPane = {
      kind: 'split',
      id: newSplitId(),
      orientation: newOrientation,
      children: newLeafFirst ? [newLeaf, source] : [source, newLeaf],
    };
    replaceNode(source, newSplit);
  }

  panesState.focusedLeafId = newLeaf.id;
}

/**
 * Adds an externally-supplied tab to `targetLeafId` at `insertIndex`.
 * Used by the cross-window drop path: when a tab dragged from another
 * Electron window lands in this window, the tab's content arrives via
 * `dataTransfer` (the source's panesState is unreachable from here)
 * and this helper materialises it locally.
 *
 * If the incoming tab id collides with an existing one in this tree —
 * possible if both windows were spawned with seeds derived from the
 * same root or if a UUID collision happens — we regenerate. The user-
 * visible content + title stay; only the internal id changes.
 */
export function importTabIntoLeaf(
  targetLeafId: string,
  tab: {
    id: string;
    title: string;
    kind?: TabKind;
    sql: string;
    editorSize?: number;
    functionForm?: PersistedFunctionForm;
  },
  insertIndex?: number,
): void {
  const leaf = findLeaf(panesState.root, targetLeafId);
  if (!leaf) return;
  // Refuse incoming pinned-kind tabs — every window owns its own
  // Models/Settings/Docs surface; importing one would create a duplicate.
  if (tab.kind && PINNED_KINDS.includes(tab.kind)) return;
  const id = findTab(panesState.root, tab.id) ? newTabId() : tab.id;
  const kind: TabKind = tab.kind === 'function' ? 'function' : 'sql';
  const newTab: Tab = {
    id,
    title: tab.title,
    kind,
    sql: tab.sql,
    dirty: false,
    editorSize: tab.editorSize,
  };
  if (kind === 'function' && tab.functionForm) {
    hydrateFunctionForm(id, tab.functionForm);
  }
  const minIndex = firstNonPinnedIndex(leaf);
  const index =
    insertIndex !== undefined
      ? Math.max(minIndex, Math.min(insertIndex, leaf.tabs.length))
      : leaf.tabs.length;
  leaf.tabs.splice(index, 0, newTab);
  leaf.activeTabId = newTab.id;
  panesState.focusedLeafId = leaf.id;
}

/**
 * Cross-window analogue of `splitLeaf`. Wraps `targetLeafId` in a new
 * split and places an externally-supplied tab in the new sibling leaf
 * on `side`. Same id-collision policy as `importTabIntoLeaf`.
 */
export function importTabAsSplit(
  targetLeafId: string,
  tab: {
    id: string;
    title: string;
    kind?: TabKind;
    sql: string;
    editorSize?: number;
    functionForm?: PersistedFunctionForm;
  },
  side: SplitSide,
): void {
  const target = findLeaf(panesState.root, targetLeafId);
  if (!target) return;
  if (tab.kind && PINNED_KINDS.includes(tab.kind)) return;
  const id = findTab(panesState.root, tab.id) ? newTabId() : tab.id;
  const kind: TabKind = tab.kind === 'function' ? 'function' : 'sql';
  const newTab: Tab = {
    id,
    title: tab.title,
    kind,
    sql: tab.sql,
    dirty: false,
    editorSize: tab.editorSize,
  };
  if (kind === 'function' && tab.functionForm) {
    hydrateFunctionForm(id, tab.functionForm);
  }
  const newLeaf: LeafPane = {
    kind: 'leaf',
    id: newLeafId(),
    tabs: [newTab],
    activeTabId: newTab.id,
  };
  const newOrientation: SplitOrientation =
    side === 'left' || side === 'right' ? 'horizontal' : 'vertical';
  const newLeafFirst = side === 'left' || side === 'top';
  const newSplit: SplitPane = {
    kind: 'split',
    id: newSplitId(),
    orientation: newOrientation,
    children: newLeafFirst ? [newLeaf, target] : [target, newLeaf],
  };
  replaceNode(target, newSplit);
  panesState.focusedLeafId = newLeaf.id;
}

/** Read-only flat view of every live tab. Used by the LSP / cell GC. */
export function getAllTabs(): Tab[] {
  const out: Tab[] = [];
  forEachTab(panesState.root, (t) => out.push(t));
  return out;
}

/** Read-only flat view of every leaf id. Used by the editor view to
 *  garbage-collect Monaco models when a leaf disappears. */
export function getAllLeafIds(): string[] {
  const out: string[] = [];
  forEachLeaf(panesState.root, (l) => out.push(l.id));
  return out;
}
