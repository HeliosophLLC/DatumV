import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Loader2, Search, X } from 'lucide-react';
import {
  clearFilters,
  datasetsState,
  filterEntries,
  loadDatasetsCatalog,
  modalitiesInManifest,
  modalityCounts,
  setQuery,
  setSelectedEntry,
  taskCounts,
  tasksDeclaredByEntries,
  toggleModality,
  toggleTask,
  type DatasetEntrySnapshot,
} from '@/state/datasets';
import {
  type CatalogTaskInfoSnapshot,
  groupTasksByFamily,
} from '@/state/models';
import { DatasetDetail } from '@/components/datasets/DatasetDetail';
import { InlineMarkdown } from '@/components/markdown/InlineMarkdown';
import { TaskChipIcon } from '@/components/shared/TaskChip';
import { Button } from '@/components/ui/button';
import {
  buildTaskFamilyMap,
  familyAccentClass,
  familyHoverBackgroundClass,
  familySelectedBackgroundClass,
  modalityHoverBackgroundClass,
  modalityIcon,
  modalityIconColorClass,
  modalitySelectedBackgroundClass,
} from '@/components/shared/taskStyles';
import {
  ResizableHandle,
  ResizablePanel,
  ResizablePanelGroup,
} from '@/components/ui/resizable';
import { cn } from '@/lib/utils';

// Pinned-tab body for the Datasets surface. Layout: header search +
// list/detail split. Each list row represents an entry (e.g. "COCO
// 2017"); the detail pane renders entry chrome + a variant tab strip
// the user can flip between to swap which variant is active.

export function DatasetsView() {
  const { t } = useTranslation('datasets');
  const {
    manifest,
    tasks,
    loading,
    error,
    query,
    selectedEntryName,
    installStates,
    active,
    selectedModalities,
    selectedTasks,
  } = useSnapshot(datasetsState);

  useEffect(() => {
    void loadDatasetsCatalog();
  }, []);

  const visibleModalities = useMemo(
    () => modalitiesInManifest(manifest),
    [manifest],
  );
  const mCounts = useMemo(() => modalityCounts(manifest), [manifest]);
  const visibleTasks = useMemo(
    () => tasksDeclaredByEntries(tasks, manifest),
    [tasks, manifest],
  );
  const tCounts = useMemo(() => taskCounts(manifest), [manifest]);
  // Name → family lookup so each row's task chips can pick up the
  // family-accent left border. Built once over the task vocabulary so
  // we don't walk it per row.
  const taskFamilies = useMemo(() => buildTaskFamilyMap(tasks), [tasks]);

  const filtered = useMemo(
    () => (manifest
      ? filterEntries(manifest, query, selectedModalities, selectedTasks)
      : []),
    [manifest, query, selectedModalities, selectedTasks],
  );

  // Auto-select the first match once the manifest lands so the detail
  // pane never sits empty. Don't override an existing selection — even
  // if it falls outside the current filter, the user's last-clicked
  // entry stays in the detail pane.
  useEffect(() => {
    if (selectedEntryName !== null) return;
    if (filtered.length === 0) return;
    const firstName = filtered[0].name;
    if (firstName) setSelectedEntry(firstName);
  }, [filtered, selectedEntryName]);

  const selectedEntry = useMemo(() => {
    if (!manifest || !selectedEntryName) return null;
    return (manifest.datasets ?? []).find((e) => e.name === selectedEntryName) ?? null;
  }, [manifest, selectedEntryName]);

  if (loading && !manifest) {
    return (
      <div className="flex flex-1 items-center justify-center text-muted-foreground">
        <Loader2 className="mr-2 size-4 animate-spin" />
        {t('loading')}
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex flex-1 items-center justify-center px-6">
        <p className="text-destructive text-sm" role="alert">
          {t('error', { message: error })}
        </p>
      </div>
    );
  }

  return (
    <div className="bg-editor flex h-full flex-col overflow-hidden">
      <header className="flex items-center gap-2 border-b px-4 py-3">
        <div className="flex-1">
          <SearchInput value={query} onChange={setQuery} placeholder={t('search')} />
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={clearFilters}
          disabled={
            query.length === 0
            && selectedModalities.size === 0
            && selectedTasks.size === 0
          }
        >
          {t('filters.clear')}
        </Button>
      </header>

      <ResizablePanelGroup orientation="horizontal" className="flex-1">
        <ResizablePanel
          id="datasets-facets"
          defaultSize="20%"
          minSize="12%"
          className="flex flex-col overflow-hidden"
        >
          <FacetSidebar
            modalities={visibleModalities}
            modalityCounts={mCounts}
            selectedModalities={selectedModalities}
            tasks={visibleTasks}
            taskCounts={tCounts}
            selectedTasks={selectedTasks}
          />
        </ResizablePanel>
        <ResizableHandle />
        <ResizablePanel
          id="datasets-list"
          defaultSize="25%"
          minSize="15%"
          className="flex flex-col overflow-hidden"
        >
          <div className="flex-1 overflow-y-auto">
            {filtered.length === 0 ? (
              <p className="text-muted-foreground p-6 text-center text-sm">
                {t('emptyList')}
              </p>
            ) : (
              <ul className="flex flex-col">
                {filtered.map((entry) => (
                  <EntryListItem
                    key={entry.name}
                    entry={entry}
                    active={entry.name === selectedEntryName}
                    installStates={installStates}
                    activeInstalls={active}
                    taskFamilies={taskFamilies}
                    onSelect={() => entry.name && setSelectedEntry(entry.name)}
                  />
                ))}
              </ul>
            )}
          </div>
        </ResizablePanel>
        <ResizableHandle />
        <ResizablePanel
          id="datasets-detail"
          defaultSize="57%"
          minSize="25%"
          className="flex flex-col overflow-hidden"
        >
          <div className="flex-1 overflow-y-auto">
            {selectedEntry ? (
              <DatasetDetail entry={selectedEntry} />
            ) : (
              <p className="text-muted-foreground flex h-full items-center justify-center px-6 text-center text-sm">
                {t('noSelection')}
              </p>
            )}
          </div>
        </ResizablePanel>
      </ResizablePanelGroup>
    </div>
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
        onKeyDown={(e) => {
          if (e.key === 'Escape' && value.length > 0) {
            e.preventDefault();
            onChange('');
          }
        }}
        placeholder={placeholder}
        className="placeholder:text-muted-foreground placeholder:italic w-full bg-transparent py-1 pl-7 pr-7 text-xs outline-none"
      />
      {value.length > 0 && (
        <button
          type="button"
          onClick={() => onChange('')}
          aria-label="Clear search"
          className="text-muted-foreground hover:text-foreground absolute right-1.5"
        >
          <X className="size-3.5" />
        </button>
      )}
    </div>
  );
}

function EntryListItem({
  entry,
  active,
  installStates,
  activeInstalls,
  taskFamilies,
  onSelect,
}: {
  entry: DatasetEntrySnapshot;
  active: boolean;
  installStates: Readonly<Record<string, string>> | null;
  activeInstalls: Readonly<Record<string, unknown>>;
  taskFamilies: ReadonlyMap<string, string>;
  onSelect: () => void;
}) {
  const { t } = useTranslation('datasets');
  // Task labels live in the `models` namespace under `tasks.<Name>` so
  // both surfaces read the same translations. Caller namespace = the
  // entire shared task vocabulary.
  const { t: tModels } = useTranslation('models');

  // Aggregate badge across the entry's variants — surface "the most
  // useful state any variant currently sits in." Mirrors the model
  // side's family aggregate.
  let anyInstalling = false;
  let anyInstalled = false;
  let anyPartial = false;
  for (const v of entry.variants ?? []) {
    const id = v.id ?? '';
    if (id in activeInstalls) anyInstalling = true;
    const s = installStates?.[id];
    if (s === 'installed') anyInstalled = true;
    else if (s === 'partial') anyPartial = true;
  }
  const aggregateState = anyInstalled
    ? 'installed'
    : anyPartial
      ? 'partial'
      : undefined;

  const variantCount = (entry.variants ?? []).length;

  return (
    <li>
      <button
        type="button"
        onClick={onSelect}
        aria-current={active ? 'true' : undefined}
        className={cn(
          'border-border flex w-full cursor-pointer flex-col items-start gap-1 border-b px-4 py-3 text-left transition-colors',
          active ? 'bg-primary/10' : 'hover:bg-muted/40',
        )}
      >
        <div className="flex w-full items-center justify-between gap-2">
          <span className="truncate text-sm font-medium">{entry.name ?? ''}</span>
          <StateBadge state={aggregateState} installing={anyInstalling} />
        </div>
        {entry.summary && (
          <span className="text-muted-foreground line-clamp-2 text-xs">
            <InlineMarkdown>{entry.summary}</InlineMarkdown>
          </span>
        )}
        {(entry.suitableForTasks?.length ?? 0) > 0 && (
          <div className="flex w-full min-w-0 flex-nowrap gap-1 overflow-hidden">
            {(entry.suitableForTasks ?? []).map((task) => (
              <TaskChipIcon
                key={task}
                task={task}
                family={taskFamilies.get(task.toLowerCase()) ?? ''}
                label={tModels(
                  `tasks.${task}` as 'tasks.TextEmbedder',
                  { defaultValue: task },
                )}
              />
            ))}
          </div>
        )}
        {variantCount > 1 && (
          <span className="text-muted-foreground text-[10px]">
            {t('variantCount', { count: variantCount })}
          </span>
        )}
      </button>
    </li>
  );
}

function FacetSidebar({
  modalities,
  modalityCounts,
  selectedModalities,
  tasks,
  taskCounts,
  selectedTasks,
}: {
  modalities: readonly string[];
  modalityCounts: Readonly<Record<string, number>>;
  selectedModalities: ReadonlySet<string>;
  tasks: readonly CatalogTaskInfoSnapshot[];
  taskCounts: Readonly<Record<string, number>>;
  selectedTasks: ReadonlySet<string>;
}) {
  const { t } = useTranslation('datasets');
  const { t: tModels } = useTranslation('models');
  const groupedTasks = useMemo(() => groupTasksByFamily(tasks), [tasks]);

  return (
    <div className="flex h-full flex-col overflow-hidden border-r">
      <div className="flex-1 overflow-y-auto px-3 py-3">
        {modalities.length === 0 && groupedTasks.length === 0 ? (
          <p className="text-muted-foreground p-2 text-xs">
            {t('filters.modalityEmpty')}
          </p>
        ) : (
          <div className="flex flex-col gap-4">
            {modalities.length > 0 && (
              <div className="flex flex-col gap-1 select-none">
                <div className="flex flex-wrap gap-1">
                  {modalities.map((m) => {
                    const isSelected = selectedModalities.has(m);
                    const count = modalityCounts[m] ?? 0;
                    const Icon = modalityIcon(m);
                    return (
                      <button
                        key={m}
                        type="button"
                        onClick={() => toggleModality(m)}
                        aria-pressed={isSelected}
                        className={cn(
                          'flex items-center gap-1.5 rounded-xs border px-2 py-1 text-[11px] text-left transition-colors',
                          isSelected
                            ? modalitySelectedBackgroundClass(m)
                            : cn('cursor-pointer', modalityHoverBackgroundClass(m)),
                        )}
                      >
                        <Icon className={cn('size-3.5', modalityIconColorClass(m))} />
                        <span>
                          {t(`modality.${m}` as 'modality.Image', { defaultValue: m })}
                        </span>
                        <span className="text-muted-foreground/70 tabular-nums text-[10px]">
                          {count}
                        </span>
                      </button>
                    );
                  })}
                </div>
              </div>
            )}

            {groupedTasks.map((group) => (
              <div
                key={group.family}
                className="flex flex-col gap-1 select-none"
              >
                <span className="text-muted-foreground text-[10px] uppercase tracking-wide">
                  {tModels(
                    `filters.family.${group.family}` as 'filters.family.ComputerVision',
                    { defaultValue: group.family },
                  )}
                </span>
                <div className="flex flex-wrap gap-1">
                  {group.tasks.map((task) => {
                    const name = task.name ?? '';
                    if (name.length === 0) return null;
                    const isSelected = selectedTasks.has(name);
                    const count = taskCounts[name] ?? 0;
                    return (
                      <button
                        key={name}
                        type="button"
                        onClick={() => toggleTask(name)}
                        title={task.description ?? undefined}
                        aria-pressed={isSelected}
                        className={cn(
                          'flex items-center gap-1.5 rounded-xs border border-l-6 px-2 py-1 text-[11px] text-left transition-colors',
                          familyAccentClass(group.family),
                          isSelected
                            ? familySelectedBackgroundClass(group.family)
                            : cn(
                                'cursor-pointer',
                                familyHoverBackgroundClass(group.family),
                              ),
                        )}
                      >
                        <span>
                          {tModels(
                            `tasks.${name}` as 'tasks.TextEmbedder',
                            { defaultValue: name },
                          )}
                        </span>
                        <span className="text-muted-foreground/70 tabular-nums text-[10px]">
                          {count}
                        </span>
                      </button>
                    );
                  })}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

function StateBadge({
  state,
  installing,
}: {
  state: string | undefined;
  installing: boolean;
}) {
  const { t } = useTranslation('datasets');
  if (installing) {
    return (
      <span className="text-primary flex items-center gap-1 text-[10px]">
        <Loader2 className="size-3 animate-spin" />
      </span>
    );
  }
  if (state === 'installed') {
    return (
      <span className="bg-primary/15 text-primary rounded-xs px-1.5 py-0.5 text-[10px]">
        {t('state.installed')}
      </span>
    );
  }
  if (state === 'partial') {
    return (
      <span className="bg-muted text-muted-foreground rounded-xs px-1.5 py-0.5 text-[10px]">
        {t('state.partial')}
      </span>
    );
  }
  return null;
}
