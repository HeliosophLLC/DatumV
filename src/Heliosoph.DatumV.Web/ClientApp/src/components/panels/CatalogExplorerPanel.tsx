import { useEffect, useMemo, useRef, type MouseEvent } from 'react';
import { useSnapshot } from 'valtio';
import { useTranslation } from 'react-i18next';
import {
  Braces,
  Brackets,
  Calendar,
  CaseSensitive,
  Database,
  Eye,
  FileBox,
  Folder,
  FolderOpen,
  Hash,
  KeyRound,
  Loader2,
  RefreshCw,
  Search,
  Sigma,
  SquareMinus,
  ToggleLeft,
  Type,
} from 'lucide-react';
import {
  catalogExplorerState,
  clearCatalogSelection,
  collapseAllCatalog,
  collapseOrAscendCatalogFocused,
  collectCatalogVisibleKeys,
  columnNodeKey,
  columnsFolderKey,
  expandOrDescendCatalogFocused,
  extendCatalogSelectionTo,
  indexNodeKey,
  indexesFolderKey,
  loadCatalog,
  moveCatalogFocus,
  selectCatalogNode,
  tableNodeKey,
  toggleCatalogSelectedNode,
  toggleCatalogSubfolderExpanded,
  toggleTableExpanded,
} from '@/state/catalogExplorer';
import type { DockSide } from '@/state/nav';
import type {
  ColumnEntryDto,
  IndexEntryDto,
  TableEntryDto,
} from '@/api/generated/openapi-client';
import { TreeBranch, TreeRoot, TreeRow } from '@/components/ui/tree';
import { cn } from '@/lib/utils';
import { PanelHeader, PanelHeaderButton } from './PanelHeader';

// Per-row props passed down to TableNode / ColumnRow / IndexRow.
interface RowContext {
  selectedKeys: Readonly<Record<string, true>>;
  focusedKey: string | null;
  expandedSubfolders: Readonly<Record<string, true>>;
  onRowClick: (key: string, isBranch: boolean, e: MouseEvent) => void;
}

// Tree view: tables → Columns + Indexes subfolders → leaves. Mirrors the
// Project Explorer's visual + interaction model (shared TreeBranch/TreeRow
// primitives, identical click + keyboard semantics). Selection logic
// lives in state/catalogExplorer.ts so this file stays focused on
// rendering.
// Side parameter is accepted for header-API consistency with the other
// panels (Chat / Procedures), even though the Catalog header drops the
// default close button in favour of Refresh + Collapse All. Closing
// happens via the dock icon itself, so no affordance is lost.
export function CatalogExplorerPanel({ side: _side }: { side: DockSide }) {
  const { t } = useTranslation('catalog');
  const { t: tPanels } = useTranslation('panels');
  const {
    status,
    tables,
    error,
    expandedTables,
    expandedSubfolders,
    selectedKeys,
    focusedKey,
  } = useSnapshot(catalogExplorerState);

  useEffect(() => {
    void loadCatalog();
  }, []);

  const visibleOrder = useMemo(
    () => collectCatalogVisibleKeys(tables, expandedTables, expandedSubfolders),
    [tables, expandedTables, expandedSubfolders],
  );

  const treeContainerRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!focusedKey) return;
    const row = treeContainerRef.current?.querySelector(
      `[data-path="${CSS.escape(focusedKey)}"]`,
    );
    row?.scrollIntoView({ block: 'nearest' });
  }, [focusedKey]);

  const handleRowClick = (
    key: string,
    isBranch: boolean,
    e: MouseEvent,
  ) => {
    if (e.shiftKey) {
      extendCatalogSelectionTo(key, visibleOrder);
    } else if (e.ctrlKey || e.metaKey) {
      toggleCatalogSelectedNode(key);
    } else {
      selectCatalogNode(key);
      // Plain click on any branch (table or subfolder) toggles its
      // expansion. Modifier clicks skip the toggle so selection-building
      // never moves rows around under the cursor.
      if (isBranch) {
        if (key.startsWith('t:')) {
          toggleTableExpanded(key.substring(2));
        } else if (key.startsWith('tc:') || key.startsWith('ti:')) {
          toggleCatalogSubfolderExpanded(key);
        }
      }
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        moveCatalogFocus('down', visibleOrder, e.shiftKey);
        break;
      case 'ArrowUp':
        e.preventDefault();
        moveCatalogFocus('up', visibleOrder, e.shiftKey);
        break;
      case 'ArrowRight':
        e.preventDefault();
        expandOrDescendCatalogFocused();
        break;
      case 'ArrowLeft':
        e.preventDefault();
        collapseOrAscendCatalogFocused();
        break;
      case 'Escape':
        e.preventDefault();
        clearCatalogSelection();
        break;
    }
  };

  const rowContext: RowContext = {
    selectedKeys,
    focusedKey,
    expandedSubfolders,
    onRowClick: handleRowClick,
  };

  const isRefreshing = status === 'loading' && tables.length > 0;

  return (
    <div className="flex h-full flex-col select-none">
      <PanelHeader
        title={tPanels('catalog.title')}
        actions={
          <>
            <PanelHeaderButton
              onClick={() => collapseAllCatalog()}
              ariaLabel={t('collapseAll')}
            >
              <SquareMinus className="size-3.5" />
            </PanelHeaderButton>
            <PanelHeaderButton
              onClick={() => void loadCatalog(true)}
              ariaLabel={t('refresh')}
            >
              <RefreshCw
                className={`size-3.5 ${isRefreshing ? 'animate-spin' : ''}`}
              />
            </PanelHeaderButton>
          </>
        }
      />
      <div className="flex-1 overflow-hidden">
        {status === 'loading' && tables.length === 0 ? (
          <div className="text-muted-foreground flex h-full items-center justify-center gap-2 text-xs">
            <Loader2 className="size-3.5 animate-spin" />
            {t('loading')}
          </div>
        ) : status === 'error' ? (
          <div className="text-destructive p-3 text-xs">
            <p className="font-medium">{t('errorTitle')}</p>
            <p className="mt-1 break-words">{error}</p>
          </div>
        ) : tables.length === 0 ? (
          <div className="text-muted-foreground p-3 text-xs">{t('noTables')}</div>
        ) : (
          <div
            ref={treeContainerRef}
            className="h-full overflow-y-auto outline-none"
            tabIndex={0}
            onKeyDown={handleKeyDown}
          >
            <TreeRoot>
              {tables.map((table) => {
                const schema = table.schema ?? '';
                const name = table.name ?? '';
                const expandKey = `${schema}.${name}`;
                const open = expandedTables[expandKey] === true;
                return (
                  <TableNode
                    key={expandKey}
                    table={table as TableEntryDto}
                    open={open}
                    ctx={rowContext}
                  />
                );
              })}
            </TreeRoot>
          </div>
        )}
      </div>
    </div>
  );
}

function TableNode({
  table,
  open,
  ctx,
}: {
  table: TableEntryDto;
  open: boolean;
  ctx: RowContext;
}) {
  const { t } = useTranslation('catalog');
  const schema = table.schema ?? '';
  const name = table.name ?? '';
  const columns: readonly ColumnEntryDto[] = table.columns ?? [];
  const indexes: readonly IndexEntryDto[] = table.indexes ?? [];
  const isView = table.kind !== 'BASE TABLE';
  const tKey = tableNodeKey(schema, name);
  const selected = ctx.selectedKeys[tKey] === true;
  const focused = ctx.focusedKey === tKey;
  const colsKey = columnsFolderKey(schema, name);
  const idxKey = indexesFolderKey(schema, name);
  const colsOpen = ctx.expandedSubfolders[colsKey] === true;
  const idxOpen = ctx.expandedSubfolders[idxKey] === true;
  return (
    <TreeBranch
      open={open}
      onToggle={() => toggleTableExpanded(`${schema}.${name}`)}
      onRowClick={(e) => ctx.onRowClick(tKey, true, e)}
      selected={selected}
      focused={focused}
      dimmed={isView}
      dataPath={tKey}
      label={
        <>
          {/* Tables: sky-blue Database — same data-kind palette as the
              Project Explorer's `data` rows. Two-tone fill+text matches
              the manila folder treatment so the icon reads as a chip
              rather than a thin outline.
              Views: cyan Eye — mirrors the Project Explorer's view
              affordance so the kind is recognisable across both panels. */}
          {isView ? (
            <Eye className="size-3 shrink-0 text-cyan-600 dark:text-cyan-400" />
          ) : (
            <Database className="size-3 shrink-0 fill-sky-200 text-sky-600 dark:fill-sky-900/40 dark:text-sky-400" />
          )}
          <span className="truncate font-mono">
            <span className="text-muted-foreground">{schema}.</span>
            {name}
          </span>
        </>
      }
    >
      <SubfolderBranch
        folderKey={colsKey}
        open={colsOpen}
        labelText={`${t('columns')} (${columns.length})`}
        ctx={ctx}
      >
        {columns.map((col) => (
          <ColumnRow
            key={col.ordinal ?? col.name ?? ''}
            column={col}
            tableSchema={schema}
            tableName={name}
            ctx={ctx}
          />
        ))}
      </SubfolderBranch>
      <SubfolderBranch
        folderKey={idxKey}
        open={idxOpen}
        labelText={`${t('indexes')} (${indexes.length})`}
        ctx={ctx}
      >
        {indexes.length === 0 ? (
          <li className="text-muted-foreground px-2 pb-1 text-[11px] italic">
            {t('noIndexes')}
          </li>
        ) : (
          indexes.map((idx) => (
            <IndexRow
              key={idx.name ?? ''}
              index={idx}
              tableSchema={schema}
              tableName={name}
              ctx={ctx}
            />
          ))
        )}
      </SubfolderBranch>
    </TreeBranch>
  );
}

function SubfolderBranch({
  folderKey,
  open,
  labelText,
  ctx,
  children,
}: {
  folderKey: string;
  open: boolean;
  labelText: string;
  ctx: RowContext;
  children: React.ReactNode;
}) {
  const Icon = open ? FolderOpen : Folder;
  const selected = ctx.selectedKeys[folderKey] === true;
  const focused = ctx.focusedKey === folderKey;
  return (
    <TreeBranch
      open={open}
      onToggle={() => toggleCatalogSubfolderExpanded(folderKey)}
      onRowClick={(e) => ctx.onRowClick(folderKey, true, e)}
      selected={selected}
      focused={focused}
      dataPath={folderKey}
      label={
        <>
          {/* Same manila folder as the Project Explorer. Visual continuity
              between the two panels matters more than per-panel branding. */}
          <Icon className="size-3 shrink-0 fill-amber-400 text-amber-500 dark:fill-amber-300 dark:text-amber-400" />
          <span className="truncate font-mono">{labelText}</span>
        </>
      }
    >
      {children}
    </TreeBranch>
  );
}

function ColumnRow({
  column,
  tableSchema,
  tableName,
  ctx,
}: {
  column: ColumnEntryDto;
  tableSchema: string;
  tableName: string;
  ctx: RowContext;
}) {
  const { t } = useTranslation('catalog');
  const typeLabel = column.isArray ? `${column.dataType}[]` : column.dataType;
  const key = column.name
    ? columnNodeKey(tableSchema, tableName, column.name)
    : '';
  const selected = key ? ctx.selectedKeys[key] === true : false;
  const focused = key ? ctx.focusedKey === key : false;
  const { Icon, colorClass } = iconForColumn(column);
  return (
    <TreeRow
      selected={selected}
      focused={focused}
      dataPath={key || undefined}
      onRowClick={key ? (e) => ctx.onRowClick(key, false, e) : undefined}
    >
      <Icon
        className={`size-3 shrink-0 ${colorClass}`}
        aria-label={column.isPrimaryKey ? t('primaryKey') : undefined}
      />
      <span className="truncate">{column.name}</span>
      <span className="text-muted-foreground ml-auto pl-2 text-[10px]">
        {typeLabel}
        {column.isNullable === false && (
          <span className="ml-1 uppercase">{t('notNull')}</span>
        )}
      </span>
    </TreeRow>
  );
}

function IndexRow({
  index,
  tableSchema,
  tableName,
  ctx,
}: {
  index: IndexEntryDto;
  tableSchema: string;
  tableName: string;
  ctx: RowContext;
}) {
  const { t } = useTranslation('catalog');
  const columns: readonly string[] = index.columns ?? [];
  const key = index.name
    ? indexNodeKey(tableSchema, tableName, index.name)
    : '';
  const selected = key ? ctx.selectedKeys[key] === true : false;
  const focused = key ? ctx.focusedKey === key : false;
  // Indexes keep their custom two-line layout (name on top, column list
  // below). Selection / focus styling matches TreeRow's via the same
  // class shapes used by selectionClasses() in tree.tsx.
  return (
    <li>
      <div
        className={cn(
          'px-2 py-0.5 font-mono transition-colors',
          key && 'cursor-pointer',
          !selected && 'hover:bg-primary/10',
          selected && 'bg-primary/20 hover:bg-primary/25',
          focused && 'ring-1 ring-inset ring-primary/60',
        )}
        data-path={key || undefined}
        onClick={key ? (e) => ctx.onRowClick(key, false, e) : undefined}
        aria-selected={selected || undefined}
      >
        <div className="flex items-center gap-1">
          <Search className="size-3 shrink-0 text-fuchsia-600 dark:text-fuchsia-400" />
          <span className="truncate">{index.name}</span>
          <span className="text-muted-foreground ml-auto pl-2 text-[10px]">
            {index.kind}
            {index.isUnique && (
              <span className="ml-1 uppercase">{t('unique')}</span>
            )}
          </span>
        </div>
        <div className="text-muted-foreground pl-4 text-[10px]">
          ({columns.join(', ')})
        </div>
      </div>
    </li>
  );
}

// Per-column icon + colour. Primary keys always win (KeyRound, primary
// colour) — that's the strongest semantic signal we can show. Otherwise
// we pick from the DataKind enum value the engine returns on the wire.
// Type names match `DataKind.ToString()` on the .NET side.
function iconForColumn(column: ColumnEntryDto): {
  Icon: typeof Hash;
  colorClass: string;
} {
  if (column.isPrimaryKey) {
    return { Icon: KeyRound, colorClass: 'text-primary' };
  }
  const dataType = (column.dataType ?? '').toLowerCase();
  // Integer kinds (Int8/Int16/Int32/Int64).
  if (dataType.startsWith('int')) {
    return { Icon: Hash, colorClass: 'text-indigo-600 dark:text-indigo-400' };
  }
  // Floating-point kinds (Float16/Float32/Float64/Decimal).
  if (
    dataType.startsWith('float') ||
    dataType.startsWith('decimal') ||
    dataType.startsWith('numeric')
  ) {
    return { Icon: Sigma, colorClass: 'text-indigo-600 dark:text-indigo-400' };
  }
  // String kinds.
  if (
    dataType === 'string' ||
    dataType.startsWith('varchar') ||
    dataType.startsWith('char') ||
    dataType === 'text'
  ) {
    return {
      Icon: CaseSensitive,
      colorClass: 'text-orange-600 dark:text-orange-400',
    };
  }
  // Boolean.
  if (dataType === 'boolean' || dataType === 'bool') {
    return {
      Icon: ToggleLeft,
      colorClass: 'text-cyan-600 dark:text-cyan-400',
    };
  }
  // Date / time / timestamp.
  if (
    dataType.startsWith('date') ||
    dataType.startsWith('time') ||
    dataType.startsWith('timestamp')
  ) {
    return {
      Icon: Calendar,
      colorClass: 'text-emerald-600 dark:text-emerald-400',
    };
  }
  // Binary / bytes / blob.
  if (
    dataType === 'bytes' ||
    dataType === 'binary' ||
    dataType === 'blob'
  ) {
    return {
      Icon: FileBox,
      colorClass: 'text-stone-500 dark:text-stone-400',
    };
  }
  // Struct / record.
  if (dataType.startsWith('struct') || dataType.startsWith('record')) {
    return {
      Icon: Braces,
      colorClass: 'text-rose-600 dark:text-rose-400',
    };
  }
  // Arrays.
  if (column.isArray || dataType.endsWith('[]')) {
    return {
      Icon: Brackets,
      colorClass: 'text-violet-600 dark:text-violet-400',
    };
  }
  // Generic / unknown — `Type` reads as "data shape" without committing.
  return {
    Icon: Type,
    colorClass: 'text-slate-500 dark:text-slate-400',
  };
}

