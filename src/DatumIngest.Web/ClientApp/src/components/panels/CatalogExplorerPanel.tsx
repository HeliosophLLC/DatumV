import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { useTranslation } from 'react-i18next';
import { ChevronRight, ChevronDown, Loader2, Database, KeyRound } from 'lucide-react';
import {
  catalogExplorerState,
  loadCatalog,
  toggleTableExpanded,
} from '@/state/catalogExplorer';
import type {
  ColumnEntryDto,
  IndexEntryDto,
  TableEntryDto,
} from '@/api/generated/openapi-client';
import { cn } from '@/lib/utils';

// Tree view: tables → expand → Columns + Indexes subgroups. One round
// trip seeds the tree; hub pushes drive refetches (state/catalogExplorer.ts).
export function CatalogExplorerPanel() {
  const { t } = useTranslation('catalog');
  const { status, tables, error, expandedTables } = useSnapshot(catalogExplorerState);

  useEffect(() => {
    void loadCatalog();
  }, []);

  if (status === 'loading' && tables.length === 0) {
    return (
      <div className="text-muted-foreground flex h-full items-center justify-center gap-2 text-xs">
        <Loader2 className="size-3.5 animate-spin" />
        {t('loading')}
      </div>
    );
  }

  if (status === 'error') {
    return (
      <div className="text-destructive p-3 text-xs">
        <p className="font-medium">{t('errorTitle')}</p>
        <p className="mt-1 break-words">{error}</p>
      </div>
    );
  }

  if (tables.length === 0) {
    return (
      <div className="text-muted-foreground p-3 text-xs">{t('noTables')}</div>
    );
  }

  return (
    <div className="h-full overflow-y-auto">
      <ul role="tree" className="py-1 text-xs">
        {tables.map((table) => {
          const schema = table.schema ?? '';
          const name = table.name ?? '';
          const key = `${schema}.${name}`;
          const open = expandedTables[key] === true;
          return (
            <TableNode
              key={key}
              table={table as TableEntryDto}
              open={open}
              onToggle={() => toggleTableExpanded(key)}
            />
          );
        })}
      </ul>
    </div>
  );
}

function TableNode({
  table,
  open,
  onToggle,
}: {
  table: TableEntryDto;
  open: boolean;
  onToggle: () => void;
}) {
  const { t } = useTranslation('catalog');
  const schema = table.schema ?? '';
  const name = table.name ?? '';
  const columns: readonly ColumnEntryDto[] = table.columns ?? [];
  const indexes: readonly IndexEntryDto[] = table.indexes ?? [];
  // System views are dimmed slightly so user tables read first; same
  // affordance as VS Code's grayed-out node_modules folder.
  const isView = table.kind !== 'BASE TABLE';
  return (
    <li>
      <button
        type="button"
        onClick={onToggle}
        className={cn(
          'hover:bg-primary/10 flex w-full items-center gap-1 px-2 py-1 text-left transition-colors',
          isView && 'text-muted-foreground',
        )}
        aria-expanded={open}
      >
        {open ? (
          <ChevronDown className="size-3 shrink-0" />
        ) : (
          <ChevronRight className="size-3 shrink-0" />
        )}
        <Database className="size-3 shrink-0 opacity-70" />
        <span className="truncate font-mono">
          <span className="text-muted-foreground">{schema}.</span>
          {name}
        </span>
      </button>
      {open && (
        <ul className="border-l border-dashed pl-3 ml-3">
          <li className="text-muted-foreground px-2 pt-2 pb-1 text-[10px] tracking-wide uppercase">
            {t('columns')} ({columns.length})
          </li>
          {columns.map((col) => (
            <ColumnRow key={col.ordinal ?? col.name ?? ''} column={col} />
          ))}
          <li className="text-muted-foreground px-2 pt-3 pb-1 text-[10px] tracking-wide uppercase">
            {t('indexes')} ({indexes.length})
          </li>
          {indexes.length === 0 ? (
            <li className="text-muted-foreground px-2 pb-1 text-[11px] italic">
              {t('noIndexes')}
            </li>
          ) : (
            indexes.map((idx) => (
              <IndexRow key={idx.name ?? ''} index={idx} />
            ))
          )}
        </ul>
      )}
    </li>
  );
}

function ColumnRow({ column }: { column: ColumnEntryDto }) {
  const { t } = useTranslation('catalog');
  const typeLabel = column.isArray ? `${column.dataType}[]` : column.dataType;
  return (
    <li className="flex items-center gap-1 px-2 py-0.5 font-mono">
      {column.isPrimaryKey && (
        <KeyRound
          className="text-primary size-3 shrink-0"
          aria-label={t('primaryKey')}
        />
      )}
      <span className="truncate">{column.name}</span>
      <span className="text-muted-foreground ml-auto pl-2 text-[10px]">
        {typeLabel}
        {column.isNullable === false && (
          <span className="ml-1 uppercase">{t('notNull')}</span>
        )}
      </span>
    </li>
  );
}

function IndexRow({ index }: { index: IndexEntryDto }) {
  const { t } = useTranslation('catalog');
  const columns: readonly string[] = index.columns ?? [];
  return (
    <li className="px-2 py-0.5 font-mono">
      <div className="flex items-center gap-1">
        <span className="truncate">{index.name}</span>
        <span className="text-muted-foreground ml-auto pl-2 text-[10px]">
          {index.kind}
          {index.isUnique && <span className="ml-1 uppercase">{t('unique')}</span>}
        </span>
      </div>
      <div className="text-muted-foreground pl-2 text-[10px]">
        ({columns.join(', ')})
      </div>
    </li>
  );
}
