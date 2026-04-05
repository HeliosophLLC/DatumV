import { useMemo } from 'react';
import { useSnapshot } from 'valtio';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronRight, FileText, Folder, Search } from 'lucide-react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import {
  buildDocTree,
  docsState,
  findDoc,
  selectDoc,
  setDocQuery,
  toggleFolder,
  type DocFolderNode,
  type DocPath,
} from '@/state/docs';
import { cn } from '@/lib/utils';

// Pinned Documentation tab. Two-pane layout: collapsible folder tree on
// the left, rendered markdown on the right. Doc content is bundled into
// the JS at build time via `state/docs.ts`, so this view does no network
// I/O — selecting a file is a synchronous proxy update.

export function DocsView() {
  const { t } = useTranslation('docs');
  const { selectedPath, query } = useSnapshot(docsState);

  const tree = useMemo(() => buildDocTree(query), [query]);
  const selectedDoc = useMemo(() => findDoc(selectedPath), [selectedPath]);
  const hasResults = tree.children.length > 0;

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
          className="w-72 shrink-0 overflow-y-auto border-r py-2"
        >
          {hasResults ? (
            <TreeChildren node={tree} depth={0} selectedPath={selectedPath} />
          ) : (
            <p className="text-muted-foreground px-4 py-6 text-center text-xs">
              {t('empty')}
            </p>
          )}
        </nav>

        <div className="flex-1 overflow-y-auto">
          {selectedDoc ? (
            <article className="markdown-body mx-auto max-w-3xl px-8 py-6">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>
                {selectedDoc.content}
              </ReactMarkdown>
            </article>
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
