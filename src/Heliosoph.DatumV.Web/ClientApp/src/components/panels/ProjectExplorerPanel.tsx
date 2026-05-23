import { useEffect, useMemo, useRef, useState, type MouseEvent } from 'react';
import { useSnapshot } from 'valtio';
import { useTranslation } from 'react-i18next';
import {
  Database,
  Eye,
  EyeOff,
  File,
  FileCode2,
  FileJson,
  FileText,
  FolderOpen,
  Folder,
  Layers,
  Loader2,
  RefreshCw,
  Sparkles,
  SquareMinus,
} from 'lucide-react';
import {
  beginInlineRename,
  buildFileTree,
  cancelInlineRename,
  clearSelection,
  collapseAllDirs,
  collapseOrAscendFocused,
  collectVisiblePaths,
  commitInlineRename,
  deleteFile,
  expandOrDescendFocused,
  extendSelectionTo,
  filesState,
  filterFilesBySystemVisibility,
  loadFiles,
  moveFocus,
  selectNode,
  subscribeFilesToHub,
  toggleDirExpanded,
  toggleSelectedNode,
  toggleSystemFilesVisible,
  type FileTreeNode,
} from '@/state/files';
import { openFileInTab } from '@/state/tabs';
import { openDialog } from '@/state/dialogs';
import type {
  DeleteFileDialogResult,
} from '@/components/dialogs/DeleteFileDialog';
import type { DockSide } from '@/state/nav';
import type { FileEntryDto } from '@/api/generated/openapi-client';
import { TreeBranch, TreeRoot, TreeRow } from '@/components/ui/tree';
import { PanelHeader, PanelHeaderButton } from './PanelHeader';

// Per-row props passed down from the panel to TreeNode / FileRow. Threading
// the whole context as one prop keeps the recursive node API stable as
// we add more interaction state.
interface RowContext {
  expandedDirs: Readonly<Record<string, true>>;
  selectedPaths: Readonly<Record<string, true>>;
  focusedPath: string | null;
  renamingPath: string | null;
  onRowClick: (path: string, isDir: boolean, e: MouseEvent) => void;
  onFileActivate: (file: FileEntryDto) => void;
  onContextMenu: (
    path: string,
    isDir: boolean,
    file: FileEntryDto | null,
    e: MouseEvent,
  ) => void;
}

// Kinds the editor can open as a SQL tab. The user-data file kinds
// (`data`, `data_sidecar`) and bookkeeping kinds (`manifest`, `gitignore`,
// `other`) aren't text-editable here and stay click-to-select only.
const OPENABLE_FILE_KINDS: ReadonlySet<string> = new Set([
  'query',
  'udf',
  'procedure',
  'view',
  'model',
]);

// Project Explorer: renders the catalog directory as a tree, backed by
// /api/files (which surfaces SystemFilesProvider's `system.files` view).
// Uses the shared tree primitives in components/ui/tree.tsx so it stays
// visually aligned with CatalogExplorerPanel. Files are classified by
// `kind` so each one gets the right icon; orphan files (no manifest
// entry) get a subtle badge so the user notices.
// Side parameter is accepted for header-API consistency with the other
// panels (Chat / Catalog / Procedures), even though Project Explorer's
// header drops the default close button in favour of Refresh + Collapse
// All. Closing happens via the dock icon itself, so no affordance is lost.
export function ProjectExplorerPanel({ side: _side }: { side: DockSide }) {
  const { t } = useTranslation('projectExplorer');
  const { t: tPanels } = useTranslation('panels');
  const {
    status,
    files,
    error,
    expandedDirs,
    selectedPaths,
    focusedPath,
    showSystemFiles,
    renamingPath,
  } = useSnapshot(filesState);

  useEffect(() => {
    // Subscribe before the first fetch so any hub event that lands
    // mid-fetch triggers a debounced refetch — closing the small race
    // between "fetch completes" and "first event arrives". The
    // subscription is idempotent and panel-scoped: tornout windows
    // never mount this component, so they never pay for the hub work
    // (which they don't consume anyway).
    subscribeFilesToHub();
    void loadFiles();
  }, []);

  // Apply the system-visibility filter before tree-build so collapsed
  // directories don't tease "...but there's more here you can't see".
  const visibleFiles = useMemo(
    () => filterFilesBySystemVisibility(files, showSystemFiles),
    [files, showSystemFiles],
  );

  // Folding the flat list into a tree is O(n) but pure, so memoise on the
  // file list reference. State module replaces the reference on every
  // refetch (or visibility toggle), which naturally invalidates the memo.
  const tree = useMemo(() => buildFileTree(visibleFiles), [visibleFiles]);
  // Visible-row order, recomputed when the tree or any directory's
  // expansion state changes. Drives shift-click range + arrow-key nav.
  const visibleOrder = useMemo(
    () => collectVisiblePaths(tree, expandedDirs),
    [tree, expandedDirs],
  );

  // Auto-scroll the focused row into view when arrow nav moves it past
  // the visible window. Querying by data attribute avoids threading refs
  // through every node — paths are unique and the tree is small.
  const treeContainerRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!focusedPath) return;
    const row = treeContainerRef.current?.querySelector(
      `[data-path="${CSS.escape(focusedPath)}"]`,
    );
    row?.scrollIntoView({ block: 'nearest' });
  }, [focusedPath]);

  const handleRowClick = (
    path: string,
    isDir: boolean,
    e: MouseEvent,
  ) => {
    if (e.shiftKey) {
      extendSelectionTo(path, visibleOrder);
    } else if (e.ctrlKey || e.metaKey) {
      toggleSelectedNode(path);
    } else {
      selectNode(path);
      // Plain click on a directory also toggles expansion (VS Code
      // parity). Modifier clicks intentionally skip the toggle so
      // selection-building never moves rows around under the cursor.
      if (isDir) toggleDirExpanded(path);
    }
  };

  const handleFileActivate = (file: FileEntryDto) => {
    const kind = file.kind ?? 'other';
    const path = file.path;
    if (!path || !OPENABLE_FILE_KINDS.has(kind)) return;
    void openFileInTab(path);
  };

  const handleRequestDelete = async (path: string, isDir: boolean) => {
    selectNode(path);
    const basename = path.split('/').pop() ?? path;
    const { result } = openDialog<
      DeleteFileDialogResult,
      { name: string; path: string; isDirectory: boolean }
    >({
      kind: 'deleteFile',
      payload: { name: basename, path, isDirectory: isDir },
    });
    const action = (await result)?.action ?? 'cancel';
    if (action !== 'confirm') return;
    try {
      await deleteFile(path);
    } catch {
      // Watcher refetch will reconcile if the server somehow accepted
      // a partial delete; surface the failure as a soft warning rather
      // than blocking the UI.
      console.warn('[explorer] delete failed', path);
    }
  };

  const handleContextMenu = async (
    path: string,
    isDir: boolean,
    file: FileEntryDto | null,
    e: MouseEvent,
  ) => {
    e.preventDefault();
    e.stopPropagation();
    const host = window.electronHost;
    if (!host) return;
    // Match VS Code: right-click on a non-selected row moves selection
    // to it before the menu opens so the menu's actions clearly refer
    // to the row under the cursor.
    selectNode(path);
    const kind = file?.kind ?? null;
    const canOpen = !isDir && kind !== null && OPENABLE_FILE_KINDS.has(kind);
    const result = await host.showContextMenu({
      items: isDir
        ? [
            { id: 'rename', label: t('menu.rename'), accelerator: 'F2' },
            { id: 'delete', label: t('menu.delete'), accelerator: 'Delete' },
          ]
        : [
            { id: 'open', label: t('menu.open'), enabled: canOpen },
            { type: 'separator' },
            { id: 'rename', label: t('menu.rename'), accelerator: 'F2' },
            { id: 'delete', label: t('menu.delete'), accelerator: 'Delete' },
          ],
    });
    if (result === null) return;
    switch (result) {
      case 'open':
        if (file) handleFileActivate(file);
        break;
      case 'rename':
        beginInlineRename(path);
        break;
      case 'delete':
        void handleRequestDelete(path, isDir);
        break;
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    // Arrow / Enter / Esc are owned by the inline-rename input when
    // it's mounted; the tree handler must not steal them. The input
    // itself stops propagation, so this only fires for keys that
    // bubbled past it — but pinning the guard here makes the rule
    // explicit.
    if (renamingPath !== null) return;
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        moveFocus('down', visibleOrder, e.shiftKey);
        break;
      case 'ArrowUp':
        e.preventDefault();
        moveFocus('up', visibleOrder, e.shiftKey);
        break;
      case 'ArrowRight':
        e.preventDefault();
        expandOrDescendFocused(tree);
        break;
      case 'ArrowLeft':
        e.preventDefault();
        collapseOrAscendFocused(tree);
        break;
      case 'Escape':
        e.preventDefault();
        clearSelection();
        break;
      case 'F2':
        if (!focusedPath) break;
        e.preventDefault();
        beginInlineRename(focusedPath);
        break;
      case 'Delete':
        if (!focusedPath) break;
        e.preventDefault();
        void handleRequestDelete(
          focusedPath,
          !files.find((f) => f.path === focusedPath),
        );
        break;
      case 'Enter': {
        // Enter on a focused file = open as a SQL tab; on a focused dir
        // = toggle expansion. Mirrors the double-click affordance so
        // keyboard-only users have the same "activate the row" gesture.
        if (!focusedPath) break;
        const file = files.find((f) => f.path === focusedPath);
        if (file) {
          e.preventDefault();
          handleFileActivate(file);
        } else {
          // No file means the focused row is a directory.
          e.preventDefault();
          toggleDirExpanded(focusedPath);
        }
        break;
      }
    }
  };

  const rowContext: RowContext = {
    expandedDirs,
    selectedPaths,
    focusedPath,
    renamingPath,
    onRowClick: handleRowClick,
    onFileActivate: handleFileActivate,
    onContextMenu: handleContextMenu,
  };

  const isRefreshing = status === 'loading' && files.length > 0;

  const header = (
    <PanelHeader
      title={tPanels('projects.title')}
      actions={
        <>
          <PanelHeaderButton
            onClick={() => toggleSystemFilesVisible()}
            ariaLabel={
              showSystemFiles ? t('hideSystem') : t('showSystem')
            }
          >
            {showSystemFiles ? (
              <EyeOff className="size-3.5" />
            ) : (
              <Eye className="size-3.5" />
            )}
          </PanelHeaderButton>
          <PanelHeaderButton
            onClick={() => collapseAllDirs()}
            ariaLabel={t('collapseAll')}
          >
            <SquareMinus className="size-3.5" />
          </PanelHeaderButton>
          <PanelHeaderButton
            onClick={() => void loadFiles(true)}
            ariaLabel={t('refresh')}
          >
            <RefreshCw
              className={`size-3.5 ${isRefreshing ? 'animate-spin' : ''}`}
            />
          </PanelHeaderButton>
        </>
      }
    />
  );

  return (
    <div className="flex h-full flex-col select-none">
      {header}
      <div className="flex-1 overflow-hidden">
        {status === 'loading' && files.length === 0 ? (
          <div className="text-muted-foreground flex h-full items-center justify-center gap-2 text-xs">
            <Loader2 className="size-3.5 animate-spin" />
            {t('loading')}
          </div>
        ) : status === 'error' ? (
          <div className="text-destructive p-3 text-xs">
            <p className="font-medium">{t('errorTitle')}</p>
            <p className="mt-1 break-words">{error}</p>
          </div>
        ) : files.length === 0 ? (
          <div className="text-muted-foreground p-3 text-xs">{t('empty')}</div>
        ) : (
          // tabIndex makes the tree container focusable — required to
          // receive keydown for arrow nav. Outline removed because the
          // focused-row indicator already shows where keyboard focus
          // is targeting within the tree.
          <div
            ref={treeContainerRef}
            className="h-full overflow-y-auto outline-none"
            tabIndex={0}
            onKeyDown={handleKeyDown}
          >
            <TreeRoot>
              {tree.children.map((node) => (
                <TreeNode key={node.path} node={node} ctx={rowContext} />
              ))}
            </TreeRoot>
          </div>
        )}
      </div>
    </div>
  );
}

function TreeNode({
  node,
  ctx,
}: {
  node: FileTreeNode;
  ctx: RowContext;
}) {
  if (node.file !== undefined) {
    return <FileRow file={node.file} name={node.name} path={node.path} ctx={ctx} />;
  }
  const open = ctx.expandedDirs[node.path] === true;
  const Icon = open ? FolderOpen : Folder;
  const selected = ctx.selectedPaths[node.path] === true;
  const focused = ctx.focusedPath === node.path;
  const renaming = ctx.renamingPath === node.path;
  return (
    <TreeBranch
      open={open}
      onToggle={() => toggleDirExpanded(node.path)}
      onRowClick={(e) => ctx.onRowClick(node.path, true, e)}
      onRowContextMenu={(e) => ctx.onContextMenu(node.path, true, null, e)}
      selected={selected}
      focused={focused}
      dataPath={node.path}
      label={
        <>
          {/* Manila folder — amber-400 reads as the classic file-explorer
              tan in both light and dark themes. fill-current paints the
              folder body so it's not just an outline. */}
          <Icon className="size-3 shrink-0 fill-amber-400 text-amber-500 dark:fill-amber-300 dark:text-amber-400" />
          {renaming ? (
            <InlineRenameInput initialName={node.name} path={node.path} />
          ) : (
            <span className="truncate font-mono">{node.name}</span>
          )}
        </>
      }
    >
      {node.children.map((child) => (
        <TreeNode key={child.path} node={child} ctx={ctx} />
      ))}
    </TreeBranch>
  );
}

function FileRow({
  file,
  name,
  path,
  ctx,
}: {
  file: FileEntryDto;
  name: string;
  path: string;
  ctx: RowContext;
}) {
  const { t } = useTranslation('projectExplorer');
  const kind = file.kind ?? 'other';
  const { Icon, colorClass } = iconForKind(kind);
  // Sidecars and ambient project files (manifest, gitignore, other) read
  // dimmer so the user-meaningful routines and data files stand out first.
  // Same affordance CatalogExplorerPanel uses for system views vs base tables.
  const isSecondary =
    kind === 'data_sidecar' ||
    kind === 'other' ||
    kind === 'gitignore' ||
    kind === 'manifest';
  const selected = ctx.selectedPaths[path] === true;
  const focused = ctx.focusedPath === path;
  const renaming = ctx.renamingPath === path;
  return (
    <TreeRow
      dimmed={isSecondary}
      selected={selected}
      focused={focused}
      onRowClick={(e) => ctx.onRowClick(path, false, e)}
      onRowDoubleClick={() => ctx.onFileActivate(file)}
      onRowContextMenu={(e) => ctx.onContextMenu(path, false, file, e)}
      dataPath={path}
      title={`${file.path ?? ''} • ${formatSize(file.sizeBytes ?? 0)}`}
    >
      <Icon className={`size-3 shrink-0 ${colorClass}`} />
      {renaming ? (
        <InlineRenameInput initialName={name} path={path} />
      ) : (
        <>
          <span className="truncate">{name}</span>
          {file.isOrphan && (
            <span
              className="ml-auto pl-2 text-[9px] tracking-wide uppercase text-amber-600 dark:text-amber-400"
              title={t('orphanTitle')}
            >
              {t('orphan')}
            </span>
          )}
        </>
      )}
    </TreeRow>
  );
}

type RenameErrorKey = 'renameInvalid' | 'renameConflict' | 'renameFailed';

function errorKeyFor(code: string): RenameErrorKey {
  switch (code) {
    case 'invalid':
      return 'renameInvalid';
    case 'conflict':
      return 'renameConflict';
    default:
      return 'renameFailed';
  }
}

/**
 * VS Code-style inline rename: the row's label is replaced with a focused
 * `<input>` that commits on Enter or blur and cancels on Escape. Commits
 * call <c>commitInlineRename</c>; a non-null return is rendered inline as
 * an error message and the input stays mounted so the user can correct
 * (typically a name conflict).
 */
function InlineRenameInput({
  initialName,
  path,
}: {
  initialName: string;
  path: string;
}) {
  const { t } = useTranslation('projectExplorer');
  const [value, setValue] = useState(initialName);
  const [errorKey, setErrorKey] = useState<RenameErrorKey | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  // After a failed commit (conflict / invalid name), we re-focus the
  // input without restarting the selection so the user's cursor sits
  // where they left it. The blur-handler path also needs to know not to
  // re-commit during an in-flight commit cycle.
  const committingRef = useRef(false);

  useEffect(() => {
    const el = inputRef.current;
    if (!el) return;
    el.focus();
    // VS Code selects the stem (filename minus extension) so typing
    // replaces just that; extensionless names get the whole label
    // selected. A leading-dot file (.gitignore) reads its dot as the
    // start of the extension, so guard against `dotIdx === 0`.
    const dotIdx = initialName.lastIndexOf('.');
    const end = dotIdx > 0 ? dotIdx : initialName.length;
    el.setSelectionRange(0, end);
  }, [initialName]);

  async function tryCommit() {
    if (committingRef.current) return;
    committingRef.current = true;
    try {
      const result = await commitInlineRename(path, value);
      if (result !== null) {
        setErrorKey(errorKeyFor(result));
        // Restore focus — blur drops out of the input but we want the
        // user to see the error and keep editing.
        requestAnimationFrame(() => inputRef.current?.focus());
      }
    } finally {
      committingRef.current = false;
    }
  }

  function onKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    // Always stop propagation so the tree's arrow / Enter / Esc handler
    // doesn't double-fire.
    e.stopPropagation();
    if (e.key === 'Enter') {
      e.preventDefault();
      void tryCommit();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      cancelInlineRename();
    }
  }

  return (
    <span className="flex flex-1 items-center gap-2">
      <input
        ref={inputRef}
        type="text"
        value={value}
        onChange={(e) => {
          setValue(e.target.value);
          if (errorKey) setErrorKey(null);
        }}
        onBlur={() => {
          // Blur counts as confirm (VS Code parity). Skip when a commit
          // is already in flight — its re-focus would re-trigger this.
          void tryCommit();
        }}
        onClick={(e) => e.stopPropagation()}
        onDoubleClick={(e) => e.stopPropagation()}
        onKeyDown={onKeyDown}
        className="bg-input border-primary text-foreground min-w-0 flex-1 rounded-sm border px-1 font-mono text-xs outline-none"
      />
      {errorKey && (
        <span
          className="text-destructive text-[10px] whitespace-nowrap"
          title={t(errorKey)}
        >
          {t(errorKey)}
        </span>
      )}
    </span>
  );
}

// Per-kind icon + color. File-shaped icons (File/FileJson/FileText/FileCode2)
// get a two-tone treatment — soft `fill-` for the paper body + darker
// `text-` for the outline/accent — mirroring the manila folder. Database,
// Layers, and Sparkles don't have a paper outline that benefits from
// filling, so they stay single-tone but coloured.
function iconForKind(kind: string): {
  Icon: typeof File;
  colorClass: string;
} {
  switch (kind) {
    case 'query':
      // Sky paper — user-authored saved queries. Distinct from the green
      // udf/procedure tone so registered code-as-data reads differently
      // from "scripts the user might re-run."
      return {
        Icon: FileCode2,
        colorClass:
          'fill-sky-100 text-sky-600 dark:fill-sky-900/40 dark:text-sky-300',
      };
    case 'data':
      // Blue — data files
      return { Icon: Database, colorClass: 'text-sky-600 dark:text-sky-400' };
    case 'data_sidecar':
      // Muted slate — secondary derived state
      return { Icon: Layers, colorClass: 'text-slate-400 dark:text-slate-500' };
    case 'udf':
    case 'procedure':
      // Green paper — executable code
      return {
        Icon: FileCode2,
        colorClass:
          'fill-emerald-100 text-emerald-600 dark:fill-emerald-900/40 dark:text-emerald-300',
      };
    case 'model':
      // Violet — model bundles ("AI" sparkles read purple in most tooling)
      return { Icon: Sparkles, colorClass: 'text-violet-600 dark:text-violet-400' };
    case 'view':
      // Cyan eye — views are "look but don't touch" projections over tables
      return { Icon: Eye, colorClass: 'text-cyan-600 dark:text-cyan-400' };
    case 'manifest':
      // Orange paper — JSON / config
      return {
        Icon: FileJson,
        colorClass:
          'fill-orange-100 text-orange-600 dark:fill-orange-900/40 dark:text-orange-300',
      };
    case 'gitignore':
      // Cool grey paper — git plumbing, muted
      return {
        Icon: FileText,
        colorClass:
          'fill-stone-100 text-stone-500 dark:fill-stone-800/60 dark:text-stone-400',
      };
    default:
      // Generic file — soft neutral paper
      return {
        Icon: File,
        colorClass:
          'fill-zinc-100 text-zinc-500 dark:fill-zinc-800/60 dark:text-zinc-400',
      };
  }
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}
