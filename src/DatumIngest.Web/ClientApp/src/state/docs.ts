import { proxy } from 'valtio';

// All markdown documents under repo `/docs/`, bundled into the JS at build
// time via Vite's `import.meta.glob`. The `?raw` query yields the file
// contents as a string; `eager: true` inlines them so no runtime fetch is
// needed and the docs ship offline with the app.
//
// Keys come back as resolved paths relative to this file; we normalise
// them to `docs-relative` slashes (e.g. `sql/select.md`) so the UI can
// build a stable tree and use the path as a routing key.
const RAW_MODULES = import.meta.glob<string>(
  '../../../../../docs/**/*.md',
  { query: '?raw', import: 'default', eager: true },
);

/** Normalised path of one doc, e.g. `sql/select.md` or `getting-started.md`. */
export type DocPath = string;

interface DocEntry {
  path: DocPath;
  /** Last path segment without the `.md` extension. */
  name: string;
  /** Path segments above the file, e.g. `['sql']` for `sql/select.md`. */
  folders: string[];
  /** Raw markdown source. */
  content: string;
}

function buildEntries(): DocEntry[] {
  const entries: DocEntry[] = [];
  for (const [rawKey, content] of Object.entries(RAW_MODULES)) {
    // rawKey looks like `../../../../../docs/sql/select.md`; trim back to
    // a `docs-relative` slashed path.
    const marker = '/docs/';
    const idx = rawKey.indexOf(marker);
    if (idx < 0) continue;
    const path = rawKey.slice(idx + marker.length).replace(/\\/g, '/');
    const segments = path.split('/');
    const file = segments[segments.length - 1];
    const folders = segments.slice(0, -1);
    const name = file.replace(/\.md$/i, '');
    entries.push({ path, name, folders, content });
  }
  entries.sort((a, b) => a.path.localeCompare(b.path));
  return entries;
}

export const DOC_ENTRIES: readonly DocEntry[] = buildEntries();

/** Default path for the initial selection — the getting-started doc when
 *  present, otherwise the first entry in path order. */
function defaultPath(): DocPath | null {
  if (DOC_ENTRIES.length === 0) return null;
  const preferred = DOC_ENTRIES.find((e) => e.path === 'getting-started.md');
  return (preferred ?? DOC_ENTRIES[0]).path;
}

interface DocsState {
  selectedPath: DocPath | null;
  /** Free-text filter applied to file paths. Empty = no filter. */
  query: string;
  /** Slash-joined folder paths the user has explicitly expanded. The
   *  rendering layer also auto-expands folders that contain the current
   *  selection or any search-match, so a folder can render expanded
   *  without being listed here. */
  expandedFolders: string[];
}

export const docsState = proxy<DocsState>({
  selectedPath: defaultPath(),
  query: '',
  // Pre-expand the chain leading to the default selection so the user
  // sees where they are in the tree on first boot.
  expandedFolders: defaultPath() ? ancestorFolders(defaultPath()!) : [],
});

function ancestorFolders(path: DocPath): string[] {
  const segments = path.split('/').slice(0, -1);
  const out: string[] = [];
  let accum = '';
  for (const seg of segments) {
    accum = accum.length === 0 ? seg : `${accum}/${seg}`;
    out.push(accum);
  }
  return out;
}

export function selectDoc(path: DocPath): void {
  docsState.selectedPath = path;
}

export function setDocQuery(query: string): void {
  docsState.query = query;
}

export function toggleFolder(folderPath: string): void {
  const i = docsState.expandedFolders.indexOf(folderPath);
  if (i >= 0) {
    docsState.expandedFolders.splice(i, 1);
  } else {
    docsState.expandedFolders.push(folderPath);
  }
}

export function findDoc(path: DocPath | null): DocEntry | null {
  if (path === null) return null;
  return DOC_ENTRIES.find((e) => e.path === path) ?? null;
}

// ──────────────────────── Tree shape ────────────────────────

export interface DocFolderNode {
  kind: 'folder';
  /** Folder name (last segment), or empty string for the root. */
  name: string;
  /** Slash-joined path to this folder, e.g. `sql` or `design-docs`. */
  path: string;
  children: DocTreeNode[];
}

export interface DocFileNode {
  kind: 'file';
  name: string;
  path: DocPath;
}

export type DocTreeNode = DocFolderNode | DocFileNode;

/** Build a folder/file tree from the flat entry list. Filtered by `query`
 *  on a path-includes basis — empty folders after filtering are pruned. */
export function buildDocTree(query: string): DocFolderNode {
  const needle = query.trim().toLowerCase();
  const root: DocFolderNode = {
    kind: 'folder',
    name: '',
    path: '',
    children: [],
  };

  const folderIndex = new Map<string, DocFolderNode>();
  folderIndex.set('', root);

  for (const entry of DOC_ENTRIES) {
    if (needle.length > 0 && !entry.path.toLowerCase().includes(needle)) {
      continue;
    }
    // Walk / lazily create the folder chain.
    let parent = root;
    let accum = '';
    for (const folder of entry.folders) {
      accum = accum.length === 0 ? folder : `${accum}/${folder}`;
      let child = folderIndex.get(accum);
      if (!child) {
        child = { kind: 'folder', name: folder, path: accum, children: [] };
        folderIndex.set(accum, child);
        parent.children.push(child);
      }
      parent = child;
    }
    parent.children.push({ kind: 'file', name: entry.name, path: entry.path });
  }

  // Folders before files within each level; both lexicographic by name.
  const sort = (node: DocFolderNode) => {
    node.children.sort((a, b) => {
      if (a.kind !== b.kind) return a.kind === 'folder' ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
    for (const c of node.children) {
      if (c.kind === 'folder') sort(c);
    }
  };
  sort(root);
  return root;
}
