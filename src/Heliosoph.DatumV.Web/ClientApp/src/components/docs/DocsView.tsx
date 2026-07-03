import { useCallback, useEffect, useMemo, useRef } from 'react';
import { useSnapshot } from 'valtio';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronRight, FileText, Folder, Search } from 'lucide-react';
import ReactMarkdown, { type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSlug from 'rehype-slug';
import rehypeHighlight from 'rehype-highlight';
import {
  buildDocTree,
  docsState,
  findDoc,
  HIGHLIGHT_END,
  HIGHLIGHT_START,
  resolveDocAsset,
  resolveDocLink,
  searchDocs,
  selectDoc,
  setDocQuery,
  toggleFolder,
  type DocFolderNode,
  type DocHit,
  type DocPath,
} from '@/state/docs';
import { CodeBlock } from '@/components/markdown/CodeBlock';
import { LightboxImage } from '@/components/shared/LightboxImage';
import { cn } from '@/lib/utils';

// Pinned Documentation tab. Two-pane layout: collapsible folder tree on
// the left, rendered markdown on the right. Doc content is bundled into
// the JS at build time via `state/docs.ts`, so this view does no network
// I/O — selecting a file is a synchronous proxy update.

export function DocsView() {
  const { t } = useTranslation('docs');
  const { selectedPath, query } = useSnapshot(docsState);

  const trimmedQuery = query.trim();
  const searching = trimmedQuery.length > 0;

  // Two view modes for the left rail: a folder tree (no query) and a flat
  // ranked hit list (query). Both are pure derivations of the current
  // query string, computed once per keystroke. MiniSearch's index lives
  // module-scoped in state/docs.ts and is built once at module load.
  const tree = useMemo(
    () => (searching ? null : buildDocTree()),
    [searching],
  );
  const hits = useMemo(
    () => (searching ? searchDocs(trimmedQuery) : []),
    [searching, trimmedQuery],
  );

  const selectedDoc = useMemo(() => findDoc(selectedPath), [selectedPath]);
  const hasResults = searching ? hits.length > 0 : (tree?.children.length ?? 0) > 0;

  return (
    <div className="bg-editor flex h-full flex-col overflow-hidden">
      <header className="flex flex-col gap-2 border-b px-4 py-3">
        <h1 className="text-sm font-medium">{t('title')}</h1>
        <SearchInput
          value={query}
          onChange={setDocQuery}
          placeholder={t('search.placeholder')}
        />
      </header>

      <div className="flex flex-1 overflow-hidden">
        <nav
          aria-label={t('title')}
          className="w-80 shrink-0 overflow-y-auto border-r py-2"
        >
          {!hasResults ? (
            <p className="text-muted-foreground px-4 py-6 text-center text-xs">
              {t('empty')}
            </p>
          ) : searching ? (
            <HitList hits={hits} selectedPath={selectedPath} />
          ) : (
            <TreeChildren node={tree!} depth={0} selectedPath={selectedPath} />
          )}
        </nav>

        <div className="flex-1 overflow-hidden">
          {selectedDoc ? (
            <DocArticle path={selectedDoc.path} content={selectedDoc.content} />
          ) : (
            <p className="text-muted-foreground flex h-full items-center justify-center px-6 text-center text-sm">
              {t('selectPrompt')}
            </p>
          )}
        </div>
      </div>
    </div>
  );
}

/**
 * Renders one doc's markdown with in-corpus link navigation. Holds the
 * scroll container ref so anchor links can scroll the heading into view
 * after navigating to a different doc (the standard `:target` /
 * `scrollIntoView` flow doesn't reach across a doc swap because the
 * heading element doesn't exist until the new doc renders).
 *
 * A `<DocLink>` runtime closure carries the current `path` so resolution
 * happens against the right "from" doc — rebuilt only when the active
 * doc changes (cheap; user navigates a doc at a time).
 *
 * Pending anchor (`pendingAnchorRef`): set by a click on a cross-doc
 * link with `#fragment`; consumed by the effect that fires when the new
 * doc's DOM has mounted.
 */
function DocArticle({ path, content }: { path: DocPath; content: string }) {
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const pendingAnchorRef = useRef<string | null>(null);

  const scrollToAnchor = useCallback((anchor: string) => {
    const container = scrollRef.current;
    if (!container) return;
    // CSS.escape guards against ids that contain special selector chars
    // (rare in slugged headings, but cheap insurance).
    const el = container.querySelector<HTMLElement>(`#${CSS.escape(anchor)}`);
    if (!el) return;
    el.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }, []);

  // After a cross-doc click, the new doc's headings render on the next
  // commit — wait one frame for the DOM to land, then jump.
  useEffect(() => {
    const anchor = pendingAnchorRef.current;
    if (!anchor) return;
    pendingAnchorRef.current = null;
    // rAF * 2 sits past react-markdown's commit + browser layout pass.
    const handle = requestAnimationFrame(() =>
      requestAnimationFrame(() => scrollToAnchor(anchor)),
    );
    return () => cancelAnimationFrame(handle);
  }, [path, scrollToAnchor]);

  const onLinkClick = useCallback(
    (href: string, event: React.MouseEvent<HTMLAnchorElement>) => {
      const target = resolveDocLink(path, href);
      if (target === null) {
        // Out-of-corpus / external / unresolved: swallow the click. The
        // user asked for these to be no-ops; falling back to default
        // navigation would unload the SPA.
        event.preventDefault();
        return;
      }
      event.preventDefault();
      if (target.kind === 'anchor') {
        scrollToAnchor(target.anchor);
        return;
      }
      if (target.path === path) {
        // Same doc, just an anchor or no anchor — no doc swap needed.
        if (target.anchor) scrollToAnchor(target.anchor);
        return;
      }
      if (target.anchor) pendingAnchorRef.current = target.anchor;
      selectDoc(target.path);
    },
    [path, scrollToAnchor],
  );

  // Memoise the components map so react-markdown doesn't re-render the
  // entire tree every keystroke / scroll. The map depends only on the
  // active doc's path (captured by onLinkClick).
  const components = useMemo<Components>(
    () => ({
      a: ({ href, children, ...rest }) => {
        const resolved = href !== undefined ? resolveDocLink(path, href) : null;
        // Visually demote dead links so the user can see "this won't take
        // me anywhere" before they click. Live links keep the default
        // primary-coloured link style from `.markdown-body a`.
        const isLive = resolved !== null;
        return (
          <a
            {...rest}
            href={href}
            onClick={(e) => href !== undefined && onLinkClick(href, e)}
            className={cn(!isLive && 'text-muted-foreground no-underline')}
            title={isLive ? undefined : href}
          >
            {children}
          </a>
        );
      },
      img: ({ src, alt, ...rest }) => {
        const resolved =
          typeof src === 'string' ? resolveDocAsset(path, src) : null;
        if (resolved === null) {
          // Out-of-corpus / unresolved: render nothing. A broken-image
          // glyph would just be noise; the alt-text fallback below tells
          // the user what they're missing.
          return alt ? (
            <span className="text-muted-foreground text-xs italic">{alt}</span>
          ) : null;
        }
        return <LightboxImage {...rest} src={resolved} alt={alt ?? ''} />;
      },
      pre: ({ children, ...rest }) => <CodeBlock {...rest}>{children}</CodeBlock>,
    }),
    [path, onLinkClick],
  );

  return (
    <div ref={scrollRef} className="h-full overflow-y-auto">
      <article className="markdown-body mx-auto max-w-3xl px-8 py-6">
        <ReactMarkdown
          remarkPlugins={[remarkGfm]}
          rehypePlugins={[rehypeSlug, [rehypeHighlight, { detect: true }]]}
          components={components}
        >
          {content}
        </ReactMarkdown>
      </article>
    </div>
  );
}

function HitList({
  hits,
  selectedPath,
}: {
  hits: readonly DocHit[];
  selectedPath: DocPath | null;
}) {
  return (
    <ul className="flex flex-col">
      {hits.map((hit) => (
        <HitRow
          key={hit.entry.path}
          hit={hit}
          active={hit.entry.path === selectedPath}
        />
      ))}
    </ul>
  );
}

function HitRow({ hit, active }: { hit: DocHit; active: boolean }) {
  return (
    <li>
      <button
        type="button"
        onClick={() => selectDoc(hit.entry.path)}
        aria-current={active ? 'page' : undefined}
        title={hit.entry.path}
        className={cn(
          'flex w-full flex-col gap-1 px-3 py-2 text-left transition-colors',
          active
            ? 'bg-primary/15'
            : 'hover:bg-muted/50',
        )}
      >
        <div className="flex items-center gap-2 text-xs font-medium">
          <FileText className="size-3 shrink-0" />
          <span className="truncate">{hit.entry.name}</span>
        </div>
        {hit.entry.folders.length > 0 && (
          <span className="text-muted-foreground truncate pl-5 font-mono text-[10px]">
            {hit.entry.folders.join('/')}
          </span>
        )}
        <span className="text-muted-foreground line-clamp-2 pl-5 text-[11px] leading-snug">
          <Snippet text={hit.snippet} />
        </span>
      </button>
    </li>
  );
}

/** Render a snippet with `HIGHLIGHT_START` / `HIGHLIGHT_END` sentinels
 *  converted into `<mark>` spans. The sentinels are guaranteed unused in
 *  source markdown (control characters 0x01 / 0x02), so a flat split is
 *  enough — no HTML parser, no XSS risk. */
function Snippet({ text }: { text: string }) {
  const parts: React.ReactNode[] = [];
  let cursor = 0;
  let key = 0;
  while (cursor < text.length) {
    const start = text.indexOf(HIGHLIGHT_START, cursor);
    if (start < 0) {
      parts.push(text.slice(cursor));
      break;
    }
    if (start > cursor) parts.push(text.slice(cursor, start));
    const end = text.indexOf(HIGHLIGHT_END, start + HIGHLIGHT_START.length);
    if (end < 0) {
      parts.push(text.slice(start + HIGHLIGHT_START.length));
      break;
    }
    parts.push(
      <mark
        key={key++}
        className="bg-primary/30 text-foreground rounded-xs px-0.5"
      >
        {text.slice(start + HIGHLIGHT_START.length, end)}
      </mark>,
    );
    cursor = end + HIGHLIGHT_END.length;
  }
  return <>{parts}</>;
}

function TreeChildren({
  node,
  depth,
  selectedPath,
}: {
  node: DocFolderNode;
  depth: number;
  selectedPath: DocPath | null;
}) {
  return (
    <ul className="flex flex-col">
      {node.children.map((child) =>
        child.kind === 'folder' ? (
          <FolderRow
            key={`folder:${child.path}`}
            node={child}
            depth={depth}
            selectedPath={selectedPath}
          />
        ) : (
          <FileRow
            key={`file:${child.path}`}
            path={child.path}
            name={child.name}
            depth={depth}
            active={child.path === selectedPath}
          />
        ),
      )}
    </ul>
  );
}

function FolderRow({
  node,
  depth,
  selectedPath,
}: {
  node: DocFolderNode;
  depth: number;
  selectedPath: DocPath | null;
}) {
  const { expandedFolders, query } = useSnapshot(docsState);
  // A folder renders open when (a) the user explicitly expanded it,
  // (b) the current selection lives underneath it, or (c) the user is
  // searching (the filtered tree is small enough to show every match).
  const userExpanded = expandedFolders.includes(node.path);
  const selectionInside = selectedPath?.startsWith(node.path + '/') ?? false;
  const searching = query.trim().length > 0;
  const expanded = userExpanded || selectionInside || searching;

  return (
    <li>
      <FolderToggle node={node} depth={depth} expanded={expanded} />
      {expanded && (
        <TreeChildren node={node} depth={depth + 1} selectedPath={selectedPath} />
      )}
    </li>
  );
}

function FolderToggle({
  node,
  depth,
  expanded,
}: {
  node: DocFolderNode;
  depth: number;
  expanded: boolean;
}) {
  const Caret = expanded ? ChevronDown : ChevronRight;
  return (
    <button
      type="button"
      onClick={() => toggleFolder(node.path)}
      aria-expanded={expanded}
      title={node.path}
      className={cn(
        'text-muted-foreground hover:text-foreground hover:bg-muted/50',
        'flex w-full cursor-pointer items-center gap-1 py-1 pr-3 text-left text-xs uppercase tracking-wide transition-colors',
      )}
      style={{ paddingLeft: `${depth * 12 + 8}px` }}
    >
      <Caret className="size-3 shrink-0" />
      <Folder className="size-3 shrink-0" />
      <span className="truncate">{node.name}</span>
    </button>
  );
}

function FileRow({
  path,
  name,
  depth,
  active,
}: {
  path: DocPath;
  name: string;
  depth: number;
  active: boolean;
}) {
  return (
    <li>
      <button
        type="button"
        onClick={() => selectDoc(path)}
        aria-current={active ? 'page' : undefined}
        title={path}
        className={cn(
          'flex w-full cursor-pointer items-center gap-2 py-1 pr-3 text-left text-xs transition-colors',
          active
            ? 'bg-primary/15 text-primary'
            : 'text-foreground hover:bg-muted/50',
        )}
        style={{ paddingLeft: `${depth * 12 + 8}px` }}
      >
        <FileText className="size-3 shrink-0" />
        <span className="truncate">{name}</span>
      </button>
    </li>
  );
}

function SearchInput({
  value,
  onChange,
  placeholder,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder: string;
}) {
  return (
    <div className="border-input focus-within:border-primary relative flex items-center rounded-xs border transition-colors">
      <Search className="text-muted-foreground absolute left-2 size-3.5" />
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        className="placeholder:text-muted-foreground w-full bg-transparent py-1 pl-7 pr-2 text-xs outline-none"
      />
    </div>
  );
}
