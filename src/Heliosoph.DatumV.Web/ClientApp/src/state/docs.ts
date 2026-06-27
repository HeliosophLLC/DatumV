import { proxy } from 'valtio';
import MiniSearch, { type SearchResult } from 'minisearch';

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

// Binary assets referenced by the markdown (figures, screenshots). Vite
// returns each as a hashed URL (e.g. `/assets/torch-abc123.gif`), emitted
// as separate files alongside the JS bundle so they're fetched lazily
// only when a doc that references them renders.
const ASSET_MODULES = import.meta.glob<string>(
  '../../../../../docs/**/*.{gif,png,jpg,jpeg,svg,webp}',
  { import: 'default', eager: true },
);

/** Normalised path of one doc, e.g. `sql/select.md` or `getting-started.md`. */
export type DocPath = string;

export interface DocEntry {
  path: DocPath;
  /** Last path segment without the `.md` extension. */
  name: string;
  /** Path segments above the file, e.g. `['sql']` for `sql/select.md`. */
  folders: string[];
  /** Raw markdown source. */
  content: string;
  /** Newline-joined heading text extracted from the markdown source. Stored
   *  separately from `content` so the search index can boost heading hits. */
  headings: string;
}

function extractHeadings(content: string): string {
  const out: string[] = [];
  for (const line of content.split('\n')) {
    const m = /^#{1,6}\s+(.+?)\s*$/.exec(line);
    if (m) out.push(m[1]);
  }
  return out.join('\n');
}

// Strip a leading YAML frontmatter block (`---\n…\n---\n`) from the raw
// markdown. ReactMarkdown has no frontmatter plugin wired up, so anything
// left in would render as a horizontal rule plus literal `title: …` text.
// We don't currently use any of the frontmatter fields, so a flat strip
// is enough — when we want the `title`, add a parser here.
function stripFrontmatter(content: string): string {
  if (!content.startsWith('---')) return content;
  // Accept either Unix or Windows line endings on the opening fence.
  const afterOpen = content.match(/^---\r?\n/);
  if (!afterOpen) return content;
  const rest = content.slice(afterOpen[0].length);
  const close = rest.match(/\r?\n---\r?\n/);
  if (!close) return content;
  return rest.slice(close.index! + close[0].length);
}

function buildEntries(): DocEntry[] {
  const entries: DocEntry[] = [];
  for (const [rawKey, rawContent] of Object.entries(RAW_MODULES)) {
    // rawKey looks like `../../../../../docs/sql/select.md`; trim back to
    // a `docs-relative` slashed path.
    const marker = '/docs/';
    const idx = rawKey.indexOf(marker);
    if (idx < 0) continue;
    const path = rawKey.slice(idx + marker.length).replace(/\\/g, '/');
    if (path === 'technical' || path.startsWith('technical/')) continue;
    const segments = path.split('/');
    const file = segments[segments.length - 1];
    const folders = segments.slice(0, -1);
    const name = file.replace(/\.md$/i, '');
    const content = stripFrontmatter(rawContent);
    entries.push({
      path,
      name,
      folders,
      content,
      headings: extractHeadings(content),
    });
  }
  entries.sort((a, b) => a.path.localeCompare(b.path));
  return entries;
}

export const DOC_ENTRIES: readonly DocEntry[] = buildEntries();

// Map of `docs-relative` path → hashed asset URL emitted by Vite.
// e.g. `figures/torch.gif` → `/assets/torch-abc123.gif`.
const DOC_ASSETS: ReadonlyMap<string, string> = (() => {
  const out = new Map<string, string>();
  for (const [rawKey, url] of Object.entries(ASSET_MODULES)) {
    const marker = '/docs/';
    const idx = rawKey.indexOf(marker);
    if (idx < 0) continue;
    const path = rawKey.slice(idx + marker.length).replace(/\\/g, '/');
    out.set(path, url);
  }
  return out;
})();

// ──────────────────────── Search index ────────────────────────

// MiniSearch index built once at module load. Field boosts (`name` 5x,
// `headings` 3x, `path` 2x) push title-row matches above accidental body
// hits — a doc whose name is "Joins" beats every other doc that happens
// to use the word "join" in its body. Prefix matches help mid-word typing
// ("subquer" finds "subqueries.md"); fuzzy is conservative (0.2) so a
// short query doesn't match wildly different terms.
const SEARCH_INDEX = new MiniSearch<DocEntry>({
  idField: 'path',
  fields: ['name', 'headings', 'path', 'content'],
  storeFields: ['path'],
  searchOptions: {
    prefix: true,
    fuzzy: 0.2,
    boost: { name: 5, headings: 3, path: 2 },
    combineWith: 'AND',
  },
});
SEARCH_INDEX.addAll(DOC_ENTRIES as DocEntry[]);

export interface DocHit {
  entry: DocEntry;
  /** Sum of MiniSearch's relevance scores across the matched terms. */
  score: number;
  /** Short snippet from the doc's body around the first matched term,
   *  with the matched substring wrapped in U+0001 / U+0002 sentinels so
   *  the renderer can split-and-highlight without re-running the regex. */
  snippet: string;
  /** Matched terms in the order MiniSearch returned them. Used to render
   *  inline highlights and as a fallback when the snippet had no body
   *  match (e.g. the hit came purely from the path or headings). */
  terms: string[];
}

/** Snippet sentinel pair (U+0001 START, U+0002 END). Picked because they
 *  can't appear in valid markdown source — the renderer splits on them
 *  to wrap matches in `<mark>` without an HTML parser. */
export const HIGHLIGHT_START = '';
export const HIGHLIGHT_END = '';

const SNIPPET_RADIUS = 80;
const SNIPPET_FALLBACK = 200;

function snippetFor(content: string, terms: readonly string[]): string {
  // Pick the first term that actually appears in body content (terms can
  // hit purely on path/headings via the boosted index, in which case the
  // body has no anchor and we fall back to a leading excerpt).
  const lower = content.toLowerCase();
  for (const term of terms) {
    const idx = lower.indexOf(term.toLowerCase());
    if (idx < 0) continue;
    const start = Math.max(0, idx - SNIPPET_RADIUS);
    const end = Math.min(content.length, idx + term.length + SNIPPET_RADIUS);
    const before = content.slice(start, idx);
    const match = content.slice(idx, idx + term.length);
    const after = content.slice(idx + term.length, end);
    const body = `${before}${HIGHLIGHT_START}${match}${HIGHLIGHT_END}${after}`
      .replace(/\s+/g, ' ')
      .trim();
    return (start > 0 ? '…' : '') + body + (end < content.length ? '…' : '');
  }
  const leading = content.slice(0, SNIPPET_FALLBACK).replace(/\s+/g, ' ').trim();
  return content.length > SNIPPET_FALLBACK ? leading + '…' : leading;
}

/** Run a free-text search across every doc; returns ranked hits with
 *  snippets and the matched terms for inline highlighting. Returns an
 *  empty array for empty/whitespace queries. */
export function searchDocs(query: string): readonly DocHit[] {
  const trimmed = query.trim();
  if (trimmed.length === 0) return [];
  const results = SEARCH_INDEX.search(trimmed) as (SearchResult & { path: string })[];
  const hits: DocHit[] = [];
  for (const r of results) {
    const entry = DOC_ENTRIES.find((e) => e.path === r.path);
    if (!entry) continue;
    const terms = r.terms ?? [];
    hits.push({
      entry,
      score: r.score,
      snippet: snippetFor(entry.content, terms),
      terms,
    });
  }
  return hits;
}

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

/**
 * Resolve a markdown link `href` written inside the doc at `fromPath`
 * against the in-corpus path set. Returns:
 *  - `{ kind: 'doc', path, anchor? }` when the link points at another
 *    bundled doc (anchor is the `#fragment` if present)
 *  - `{ kind: 'anchor', anchor }` when the link is a same-doc anchor
 *    (e.g. `#examples`)
 *  - `null` for everything else — external URLs, paths that escape the
 *    docs/ corpus, files we don't have. Callers should treat null as
 *    "ignore the click".
 */
export type DocLinkTarget =
  | { kind: 'doc'; path: DocPath; anchor: string | null }
  | { kind: 'anchor'; anchor: string };

export function resolveDocLink(
  fromPath: DocPath,
  href: string,
): DocLinkTarget | null {
  if (href.length === 0) return null;
  // Same-doc anchor.
  if (href.startsWith('#')) {
    const anchor = href.slice(1);
    return anchor.length === 0 ? null : { kind: 'anchor', anchor };
  }
  // Hard-block anything with a protocol — http(s), mailto, file, etc.
  // We're not opening external links in this surface; the user explicitly
  // wants out-of-corpus links to be no-ops.
  if (/^[a-z][a-z0-9+.-]*:/i.test(href)) return null;
  // Absolute paths (`/foo`) aren't part of any markdown convention used in
  // the docs corpus; treat as out-of-corpus.
  if (href.startsWith('/')) return null;

  // Split off the anchor before path-resolving so we can carry it onto
  // the destination doc.
  const hashIdx = href.indexOf('#');
  const relPath = hashIdx < 0 ? href : href.slice(0, hashIdx);
  const anchor = hashIdx < 0 ? null : href.slice(hashIdx + 1) || null;

  const resolved = walkRelative(fromPath, relPath);
  if (resolved === null) return null;
  // Only resolve to .md files we actually have bundled.
  if (!resolved.toLowerCase().endsWith('.md')) return null;
  const match = DOC_ENTRIES.find((e) => e.path === resolved);
  if (!match) return null;
  return { kind: 'doc', path: match.path, anchor };
}

/** Walk a `relPath` from the directory of `fromPath`, honouring `.` and
 *  `..` segments. Returns the resolved `docs-relative` path, or null if
 *  the walk escapes the docs root. Doesn't check whether the resolved
 *  path corresponds to a bundled file — callers do that against their
 *  own corpus (markdown entries, asset map, …). */
function walkRelative(fromPath: DocPath, relPath: string): string | null {
  const fromSegments = fromPath.split('/').slice(0, -1);
  const stack = [...fromSegments];
  for (const seg of relPath.split('/')) {
    if (seg === '' || seg === '.') continue;
    if (seg === '..') {
      if (stack.length === 0) return null;
      stack.pop();
      continue;
    }
    stack.push(seg);
  }
  return stack.join('/');
}

/** Resolve an `<img src="…">` in the doc at `fromPath` against the
 *  bundled asset map. Returns the hashed URL Vite emitted for the asset,
 *  or null when the src is external (has a protocol), absolute, or
 *  points at a file that isn't part of the bundled corpus. */
export function resolveDocAsset(fromPath: DocPath, src: string): string | null {
  if (src.length === 0) return null;
  if (/^[a-z][a-z0-9+.-]*:/i.test(src)) return null; // http(s), data:, …
  if (src.startsWith('/')) return null;
  // No anchor handling — images don't take fragments.
  const resolved = walkRelative(fromPath, src);
  if (resolved === null) return null;
  return DOC_ASSETS.get(resolved) ?? null;
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

/** Build the full folder/file tree from `DOC_ENTRIES`. Used when the
 *  search box is empty; ranked hits replace the tree once the user
 *  types a query. */
export function buildDocTree(): DocFolderNode {
  const root: DocFolderNode = {
    kind: 'folder',
    name: '',
    path: '',
    children: [],
  };

  const folderIndex = new Map<string, DocFolderNode>();
  folderIndex.set('', root);

  for (const entry of DOC_ENTRIES) {
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
