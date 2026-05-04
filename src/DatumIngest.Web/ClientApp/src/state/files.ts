import { proxy, ref } from 'valtio';
import { api } from '@/api';
import type { FileEntryDto, FilesDto } from '@/api/generated/openapi-client';
import {
  CatalogChangeKind,
  onCatalogChanged,
  onFilesChanged,
  acquireCatalogHub,
} from '@/api/catalogHub';

// Read-only cache for the Project Explorer panel. One round trip to
// /api/files seeds the flat list; the panel folds it into a directory
// tree at render time. CatalogHub pushes drive debounced refetches.
//
// `files` is wrapped in `ref(...)` so Valtio doesn't proxy the array —
// the rows never mutate in place; we always replace the whole reference
// after a fresh fetch.

export type FilesStatus = 'idle' | 'loading' | 'ready' | 'error';

interface FilesState {
  status: FilesStatus;
  files: readonly FileEntryDto[];
  error: string | null;
  // Per-directory expansion (key = directory path with forward slashes,
  // empty string for root). Preserved across refetches so a live update
  // doesn't collapse the user's open branches.
  expandedDirs: Record<string, true>;
    // ──── Selection model (VS Code parity) ────
  // selectedPaths: every currently-selected node, as a set keyed by path.
  // Multi-select via Ctrl/Cmd+click; range select via Shift+click between
  // anchor and target.
  selectedPaths: Record<string, true>;
  // anchorPath: where range selection extends *from*. Set on plain click
  // and Ctrl-click; held still across Shift-clicks. Null when nothing has
  // been clicked yet, or when the only selected node was deselected via
  // a toggle.
  anchorPath: string | null;
  // focusedPath: keyboard focus marker (what arrow keys move from, what
  // gets the focus-ring style). Tracks the most-recently-interacted-with
  // node — moves on click and on arrow nav.
  focusedPath: string | null;
  // When false, infrastructure paths (data/, python/, uv/, venvs/) are
  // hidden from the tree. Defaults to true so first-time users see the
  // full layout — flipping it off is the user's "clean up the noise"
  // gesture once they know what's there.
  showSystemFiles: boolean;
}

/**
 * Path prefixes considered "system" — generated, downloaded, or runtime
 * infrastructure. Everything under one of these roots is hidden when
 * `showSystemFiles` is off. Matches by literal prefix on the
 * forward-slash path the server emits.
 */
export const SYSTEM_PATH_PREFIXES: readonly string[] = [
  'data/',
  'python/',
  'uv/',
  'venvs/',
];

export const filesState = proxy<FilesState>({
  status: 'idle',
  files: [],
  error: null,
  // Root is expanded by default so the panel doesn't open empty on first
  // mount. Sub-folders stay collapsed until the user clicks them.
  expandedDirs: { '': true },
  selectedPaths: {},
  anchorPath: null,
  focusedPath: null,
  showSystemFiles: true,
});

export function toggleSystemFilesVisible(): void {
  filesState.showSystemFiles = !filesState.showSystemFiles;
}

/**
 * Filters out paths under any {@link SYSTEM_PATH_PREFIXES} root when
 * `showSystemFiles` is false. Pure — the panel calls this to feed its
 * tree projection.
 */
export function filterFilesBySystemVisibility(
  files: readonly FileEntryDto[],
  showSystemFiles: boolean,
): readonly FileEntryDto[] {
  if (showSystemFiles) return files;
  return files.filter((f) => {
    const path = f.path ?? '';
    return !SYSTEM_PATH_PREFIXES.some((prefix) => path.startsWith(prefix));
  });
}

export function toggleDirExpanded(key: string): void {
  if (filesState.expandedDirs[key]) {
    const next = { ...filesState.expandedDirs };
    delete next[key];
    filesState.expandedDirs = next;
  } else {
    filesState.expandedDirs = {
      ...filesState.expandedDirs,
      [key]: true,
    };
  }
}

/**
 * Collapses every directory in the tree. The synthetic root key ('') stays
 * expanded because the panel never renders a chevron for it — it's always
 * the visible top level.
 */
export function collapseAllDirs(): void {
  filesState.expandedDirs = { '': true };
}

let inflight: Promise<void> | null = null;

export function loadFiles(force = false): Promise<void> {
  if (inflight) return inflight;
  if (!force && filesState.status === 'ready') {
    return Promise.resolve();
  }
  filesState.status = 'loading';
  filesState.error = null;
  inflight = (async () => {
    try {
      const dto: FilesDto = await api.files.getFiles();
      filesState.files = ref(dto.files ?? []);
      filesState.status = 'ready';
    } catch (err) {
      filesState.error = err instanceof Error ? err.message : String(err);
      filesState.status = 'error';
    } finally {
      inflight = null;
    }
  })();
  return inflight;
}

// ──────────────────────── Live updates ────────────────────────

// Same 250ms debounce as catalogExplorer: a burst of DDL (CREATE FUNCTION
// in a loop, a script that installs five models) collapses into one
// refetch. Gives the engine time to broadcast every event before we ask.
const REFETCH_DEBOUNCE_MS = 250;
let refetchTimer: number | null = null;

function scheduleRefetch(): void {
  if (refetchTimer !== null) window.clearTimeout(refetchTimer);
  refetchTimer = window.setTimeout(() => {
    refetchTimer = null;
    void loadFiles(true);
  }, REFETCH_DEBOUNCE_MS);
}

// Every kind that adds/removes a file under the catalog root.
// TableAltered isn't here — ALTER TABLE doesn't change the .datum's
// existence, just its schema. Sidecars get rebuilt at scan time; their
// row counts can flap independently, but the panel won't flicker since
// the path set is stable across an ALTER.
const RELEVANT_KINDS: ReadonlySet<CatalogChangeKind> = new Set([
  CatalogChangeKind.TableCreated,
  CatalogChangeKind.TableDropped,
  CatalogChangeKind.IndexCreated,
  CatalogChangeKind.IndexDropped,
  CatalogChangeKind.FunctionCreated,
  CatalogChangeKind.FunctionAltered,
  CatalogChangeKind.FunctionDropped,
  CatalogChangeKind.ProcedureCreated,
  CatalogChangeKind.ProcedureAltered,
  CatalogChangeKind.ProcedureDropped,
  CatalogChangeKind.ModelCreated,
  CatalogChangeKind.ModelAltered,
  CatalogChangeKind.ModelDropped,
  CatalogChangeKind.ViewCreated,
  CatalogChangeKind.ViewAltered,
  CatalogChangeKind.ViewDropped,
]);

let subscribed = false;
// Call once at app startup (App.tsx mount). Idempotent.
export function subscribeFilesToHub(): void {
  if (subscribed) return;
  subscribed = true;
  void acquireCatalogHub().catch((err) => {
    console.error('[files] hub acquire failed', err);
  });
  onCatalogChanged((event) => {
    if (RELEVANT_KINDS.has(event.kind)) {
      scheduleRefetch();
    }
  });
  // Server-side directory watcher: out-of-band changes (VS Code save, git
  // checkout, hand-edit) push this every ~250ms while activity is ongoing.
  // The same debounce coalesces with DDL-driven refetches when our own
  // writes also fire the watcher.
  onFilesChanged(() => {
    scheduleRefetch();
  });
}

// ──────────────────────── Tree projection ────────────────────────
//
// Pure helpers. The panel calls these against the snapshotted file list
// to fold the flat array into a directory tree. Living here (not in the
// component) makes them testable from any future state-driven view.

export interface FileTreeNode {
  // Display label for this node.
  name: string;
  // Catalog-relative path. For directories, this is the dir path
  // (forward slashes, no trailing slash). For files, the full path.
  path: string;
  // Children — populated only for directories.
  children: FileTreeNode[];
  // File-only payload — populated only when this node represents a file.
  file?: FileEntryDto;
}

/**
 * Folds a flat list of files into a directory tree. Empty directories are
 * never present — only directories that contain at least one file appear.
 * Children at each level are sorted with directories first, then files,
 * each alphabetically by name.
 */
export function buildFileTree(files: readonly FileEntryDto[]): FileTreeNode {
  const root: FileTreeNode = { name: '', path: '', children: [] };

  for (const file of files) {
    // NSwag types path as optional; skip rows where the server didn't
    // send one (shouldn't happen in practice — the column is non-null).
    if (!file.path) continue;
    const parts = file.path.split('/');
    let cursor = root;
    for (let i = 0; i < parts.length - 1; i++) {
      const segment = parts[i];
      const subPath = parts.slice(0, i + 1).join('/');
      let child = cursor.children.find(
        (c) => c.name === segment && c.file === undefined,
      );
      if (!child) {
        child = { name: segment, path: subPath, children: [] };
        cursor.children.push(child);
      }
      cursor = child;
    }
    cursor.children.push({
      name: parts[parts.length - 1],
      path: file.path,
      children: [],
      file,
    });
  }

  sortTree(root);
  return root;
}

function sortTree(node: FileTreeNode): void {
  node.children.sort((a, b) => {
    const aIsDir = a.file === undefined;
    const bIsDir = b.file === undefined;
    if (aIsDir !== bIsDir) return aIsDir ? -1 : 1;
    return a.name.localeCompare(b.name);
  });
  for (const child of node.children) {
    if (child.file === undefined) sortTree(child);
  }
}

// ──────────────────────── Visible-order projection ────────────────────────

/**
 * Returns the catalog-relative paths of every currently-visible row, in
 * render order. A row is visible when every ancestor directory on its
 * path is expanded. The synthetic root never appears.
 *
 * Used for range select (Shift+click anchor → target) and arrow-key
 * navigation — both need a deterministic ordered index of what the user
 * sees so "extend selection to" and "move focus down" are well-defined.
 */
export function collectVisiblePaths(
  tree: FileTreeNode,
  expandedDirs: Readonly<Record<string, true>>,
): string[] {
  const out: string[] = [];
  function walk(node: FileTreeNode) {
    for (const child of node.children) {
      out.push(child.path);
      if (child.file === undefined && expandedDirs[child.path]) {
        walk(child);
      }
    }
  }
  walk(tree);
  return out;
}

/** Find the tree node at <paramref name="path"/> or null. O(depth). */
function findNode(tree: FileTreeNode, path: string): FileTreeNode | null {
  if (tree.path === path) return tree;
  if (path === '' || !path.startsWith(tree.path === '' ? '' : tree.path + '/')) {
    if (tree.path !== '') return null;
  }
  for (const child of tree.children) {
    if (path === child.path) return child;
    if (child.file === undefined && path.startsWith(child.path + '/')) {
      return findNode(child, path);
    }
  }
  return null;
}

// ──────────────────────── Selection actions ────────────────────────

/**
 * Replace the selection with a single node. Sets anchor = focus = path.
 * Used by plain (no-modifier) row click and by arrow-key moves without
 * the Shift modifier.
 */
export function selectNode(path: string): void {
  filesState.selectedPaths = { [path]: true };
  filesState.anchorPath = path;
  filesState.focusedPath = path;
}

/**
 * Ctrl/Cmd+click: toggle membership in the selection. If the node was
 * the only selected one and the toggle removes it, the anchor clears so
 * the next plain click starts a fresh selection.
 */
export function toggleSelectedNode(path: string): void {
  const next = { ...filesState.selectedPaths };
  if (next[path]) {
    delete next[path];
  } else {
    next[path] = true;
  }
  filesState.selectedPaths = next;
  filesState.anchorPath = next[path] ? path : null;
  filesState.focusedPath = path;
}

/**
 * Shift+click: replace the selection with the range from
 * <c>anchorPath</c> (or the focused node, when no anchor is set) to
 * <paramref name="path"/>. Range is over *visible* nodes only — collapsed
 * subtrees never get pulled into the selection. Anchor is preserved.
 */
export function extendSelectionTo(
  path: string,
  visibleOrder: readonly string[],
): void {
  const anchor =
    filesState.anchorPath ?? filesState.focusedPath ?? path;
  const anchorIdx = visibleOrder.indexOf(anchor);
  const targetIdx = visibleOrder.indexOf(path);
  if (anchorIdx === -1 || targetIdx === -1) {
    selectNode(path);
    return;
  }
  const [lo, hi] =
    anchorIdx <= targetIdx ? [anchorIdx, targetIdx] : [targetIdx, anchorIdx];
  const next: Record<string, true> = {};
  for (let i = lo; i <= hi; i++) {
    next[visibleOrder[i]] = true;
  }
  filesState.selectedPaths = next;
  filesState.focusedPath = path;
  // Anchor intentionally unchanged so a follow-up Shift-click extends
  // from the same origin.
}

/**
 * Up/Down arrow (with or without Shift). Moves the keyboard cursor by
 * one row through <paramref name="visibleOrder"/>; when <c>extend</c> is
 * true, the selection grows from the anchor to the new focus instead of
 * collapsing to a single node.
 */
export function moveFocus(
  direction: 'up' | 'down',
  visibleOrder: readonly string[],
  extend: boolean,
): void {
  if (visibleOrder.length === 0) return;
  const current = filesState.focusedPath;
  let idx = current ? visibleOrder.indexOf(current) : -1;
  if (idx === -1) {
    idx = direction === 'up' ? visibleOrder.length - 1 : 0;
  } else {
    idx =
      direction === 'up'
        ? Math.max(0, idx - 1)
        : Math.min(visibleOrder.length - 1, idx + 1);
  }
  const next = visibleOrder[idx];
  if (extend) {
    extendSelectionTo(next, visibleOrder);
  } else {
    selectNode(next);
  }
}

/**
 * Right arrow. Three cases (VS Code parity):
 *   * Focused leaf: no-op.
 *   * Focused collapsed directory: expand it.
 *   * Focused expanded directory with children: move focus into the
 *     first child.
 */
export function expandOrDescendFocused(tree: FileTreeNode): void {
  const focused = filesState.focusedPath;
  if (!focused) return;
  const node = findNode(tree, focused);
  if (!node || node.file !== undefined) return;
  if (!filesState.expandedDirs[focused]) {
    filesState.expandedDirs = { ...filesState.expandedDirs, [focused]: true };
    return;
  }
  if (node.children.length > 0) {
    selectNode(node.children[0].path);
  }
}

/**
 * Left arrow. Three cases (VS Code parity):
 *   * Focused expanded directory: collapse it.
 *   * Focused leaf or collapsed directory: move focus to the parent.
 *   * Already at top-level node: no-op.
 */
export function collapseOrAscendFocused(tree: FileTreeNode): void {
  const focused = filesState.focusedPath;
  if (!focused) return;
  const node = findNode(tree, focused);
  if (node && node.file === undefined && filesState.expandedDirs[focused]) {
    const next = { ...filesState.expandedDirs };
    delete next[focused];
    filesState.expandedDirs = next;
    return;
  }
  const lastSlash = focused.lastIndexOf('/');
  if (lastSlash > 0) {
    selectNode(focused.substring(0, lastSlash));
  }
}

/** Clears selection (and the anchor); keeps the focus where it was. */
export function clearSelection(): void {
  filesState.selectedPaths = {};
  filesState.anchorPath = null;
}
