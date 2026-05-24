import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import {
  ArrowUpCircle,
  CircleCheck,
  CircleDashed,
  HardDrive,
  Loader2,
  Search,
  X,
} from 'lucide-react';
import {
  aggregateEntryStatus,
  clearFilters,
  driftedCount,
  filterEntries,
  groupTasksByFamily,
  installStateCounts,
  isDriftedEntry,
  loadModelsCatalog,
  modelsState,
  setQuery,
  setSelectedEntry,
  setUpdatesOnly,
  tasksWithAssignedModels,
  toggleInstallState,
  toggleTask,
  type CatalogEntrySnapshot,
  type CatalogTaskInfoSnapshot,
} from '@/state/models';
import type { ModelInstallState } from '@/api/generated/openapi-client';
import { downloadsState, refreshDownloads } from '@/state/downloads';
import { ModelDetail } from '@/components/models/ModelDetail';
import {
  buildTaskFamilyMap,
  familyAccentClass,
  familyHoverBackgroundClass,
  familySelectedBackgroundClass,
} from '@/components/shared/taskStyles';
import { TaskChipIcon } from '@/components/shared/TaskChip';
import { Button } from '@/components/ui/button';
import {
  ResizableHandle,
  ResizablePanel,
  ResizablePanelGroup,
} from '@/components/ui/resizable';
import { cn } from '@/lib/utils';

export function ModelsView() {
  const { t } = useTranslation('models');
  const {
    manifest,
    tasks,
    activeVersions,
    loading,
    error,
    updatesOnly,
    installStates,
    query,
    selectedTasks,
    selectedEntryName,
    selectedVariantId,
  } = useSnapshot(modelsState);
  const downloads = useSnapshot(downloadsState);
  const driftCount = useMemo(
    () => driftedCount(manifest, activeVersions),
    [manifest, activeVersions],
  );
  const stateCounts = useMemo(
    () => installStateCounts(manifest, downloads.state),
    [manifest, downloads.state],
  );

  useEffect(() => {
    void loadModelsCatalog();
    void refreshDownloads();
  }, []);

  const visibleTasks = useMemo(
    () => (tasks && manifest ? tasksWithAssignedModels(tasks, manifest) : []),
    [tasks, manifest],
  );

  const taskFamilies = useMemo(() => buildTaskFamilyMap(tasks), [tasks]);

  const filtered = useMemo(
    () =>
      manifest
        ? filterEntries(
            manifest,
            updatesOnly,
            installStates,
            downloads.state,
            query,
            selectedTasks,
            activeVersions,
          )
        : [],
    [manifest, updatesOnly, installStates, downloads.state, query, selectedTasks, activeVersions],
  );

  // Auto-select the first match once the manifest lands.
  useEffect(() => {
    if (selectedEntryName !== null) return;
    if (filtered.length === 0) return;
    const first = filtered[0];
    const firstName = first.name;
    const firstVariantId = first.variants?.[0]?.id;
    if (firstName) setSelectedEntry(firstName, firstVariantId ?? null);
  }, [filtered, selectedEntryName]);

  const selectedEntry = useMemo(() => {
    if (!manifest || !selectedEntryName) return null;
    return (manifest.entries ?? []).find((e) => e.name === selectedEntryName) ?? null;
  }, [manifest, selectedEntryName]);

  const selectedVariant = useMemo(() => {
    if (!selectedEntry) return null;
    const vs = selectedEntry.variants ?? [];
    return vs.find((v) => v.id === selectedVariantId) ?? vs[0] ?? null;
  }, [selectedEntry, selectedVariantId]);

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

  const filtersActive =
    updatesOnly || installStates.size > 0 || query.length > 0 || selectedTasks.size > 0;

  return (
    <div className="bg-editor flex h-full flex-col overflow-hidden">
      <header className="flex flex-col gap-2 border-b px-4 py-3">
        <div className="flex items-center gap-2">
          <div className="flex-1">
            <SearchInput
              value={query}
              onChange={setQuery}
              placeholder={t('search.placeholder', { count: filtered.length })}
            />
          </div>
          <Button
            variant="outline"
            size="sm"
            onClick={clearFilters}
            disabled={!filtersActive}
          >
            {t('filters.clear')}
          </Button>
        </div>

        <div className="flex items-center gap-1">
          <UpdatesToggle
            active={updatesOnly}
            disabled={driftCount === 0}
            onToggle={() => setUpdatesOnly(!updatesOnly)}
            label={t('filters.updatesWithCount', { count: driftCount })}
          />
          <InstallStateToggle
            state="installed"
            active={installStates.has('installed')}
            count={stateCounts.installed}
            label={t('filters.installedWithCount', { count: stateCounts.installed })}
          />
          <InstallStateToggle
            state="downloaded"
            active={installStates.has('downloaded')}
            count={stateCounts.downloaded}
            label={t('filters.downloadedWithCount', { count: stateCounts.downloaded })}
          />
          {stateCounts.partial > 0 && (
            <InstallStateToggle
              state="partial"
              active={installStates.has('partial')}
              count={stateCounts.partial}
              label={t('filters.partialWithCount', { count: stateCounts.partial })}
            />
          )}
        </div>
      </header>

      <ResizablePanelGroup orientation="horizontal" className="flex-1">
        <ResizablePanel
          id="models-tasks"
          defaultSize="18%"
          minSize="10%"
          className="flex flex-col overflow-hidden"
        >
          <TaskSidebar
            tasks={visibleTasks}
            selected={selectedTasks}
            onToggle={toggleTask}
          />
        </ResizablePanel>
        <ResizableHandle />
        <ResizablePanel
          id="models-list"
          defaultSize="25%"
          minSize="15%"
          className="flex flex-col overflow-hidden"
        >
          <div className="flex-1 overflow-y-auto">
            {filtered.length === 0 ? (
              <p className="text-muted-foreground p-6 text-center text-sm">
                {query.length > 0 ? t('noMatches') : t('empty')}
              </p>
            ) : (
              <ul className="flex flex-col">
                {filtered.map((entry) => (
                  <EntryListItem
                    key={entry.name}
                    entry={entry}
                    active={entry.name === selectedEntryName}
                    drifted={isDriftedEntry(entry, activeVersions)}
                    taskFamilies={taskFamilies}
                    onSelect={() => {
                      const variantId = entry.variants?.[0]?.id ?? null;
                      if (entry.name) setSelectedEntry(entry.name, variantId);
                    }}
                  />
                ))}
              </ul>
            )}
          </div>
        </ResizablePanel>
        <ResizableHandle />
        <ResizablePanel
          id="models-detail"
          defaultSize="57%"
          minSize="25%"
          className="flex flex-col overflow-hidden"
        >
          <div className="flex-1 overflow-y-auto">
            {selectedEntry && selectedVariant ? (
              <ModelDetail entry={selectedEntry} variant={selectedVariant} />
            ) : (
              <p className="text-muted-foreground flex h-full items-center justify-center px-6 text-center text-sm">
                {t('selectPrompt')}
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

function UpdatesToggle({
  active,
  disabled = false,
  onToggle,
  label,
}: {
  active: boolean;
  disabled?: boolean;
  onToggle: () => void;
  label: string;
}) {
  return (
    <button
      type="button"
      onClick={onToggle}
      disabled={disabled}
      aria-pressed={active}
      className={cn(
        'border flex items-center gap-1 rounded-xs px-2 py-1 text-xs transition-colors select-none',
        disabled
          ? 'text-muted-foreground/40 cursor-not-allowed'
          : active
            ? 'bg-primary/15 cursor-pointer'
            : 'text-muted-foreground hover:bg-primary/10 cursor-pointer',
      )}
    >
      <ArrowUpCircle className="size-3" />
      {label}
    </button>
  );
}

function InstallStateToggle({
  state,
  active,
  count,
  label,
}: {
  state: ModelInstallState;
  active: boolean;
  count: number;
  label: string;
}) {
  const disabled = count === 0;
  const Icon =
    state === 'installed' ? CircleCheck
    : state === 'downloaded' ? HardDrive
    : state === 'partial' ? CircleDashed
    : null;
  return (
    <button
      type="button"
      onClick={() => toggleInstallState(state)}
      disabled={disabled}
      aria-pressed={active}
      className={cn(
        'border flex items-center gap-1 rounded-xs px-2 py-1 text-xs transition-colors select-none',
        disabled
          ? 'text-muted-foreground/40 cursor-not-allowed'
          : active
            ? 'bg-primary/15 cursor-pointer'
            : 'text-muted-foreground hover:bg-primary/10 cursor-pointer',
      )}
    >
      {Icon && <Icon className="size-3" />}
      {label}
    </button>
  );
}

function TaskSidebar({
  tasks,
  selected,
  onToggle,
}: {
  tasks: readonly CatalogTaskInfoSnapshot[];
  selected: ReadonlySet<string>;
  onToggle: (name: string) => void;
}) {
  const { t } = useTranslation('models');
  const grouped = useMemo(() => groupTasksByFamily(tasks), [tasks]);

  return (
    <div className="flex h-full flex-col overflow-hidden border-r">
      <div className="flex-1 overflow-y-auto px-3 py-2">
        {grouped.length === 0 ? (
          <p className="text-muted-foreground text-xs">{t('filters.tasksEmpty')}</p>
        ) : (
          <div className="flex flex-col gap-3">
            {grouped.map((group) => (
              <div
                key={group.family}
                className={cn(
                  'flex flex-col gap-1 select-none',
                )}
              >
                <span className="text-muted-foreground text-[10px] uppercase tracking-wide">
                  {familyLabel(t, group.family)}
                </span>
                <div className="flex flex-wrap gap-1">
                  {group.tasks.map((task) => {
                    const name = task.name ?? '';
                    if (name.length === 0) return null;
                    const isSelected = selected.has(name);
                    return (
                      <button
                        key={name}
                        type="button"
                        onClick={() => onToggle(name)}
                        title={task.description ?? undefined}
                        aria-pressed={isSelected}
                        className={cn(
                          'border border-l-6 rounded-xs px-2 py-1 text-[11px] transition-colors text-left',
                          familyAccentClass(group.family),
                          isSelected
                            ? familySelectedBackgroundClass(group.family)
                            : cn(
                                'cursor-pointer',
                                familyHoverBackgroundClass(group.family),
                              ),
                        )}
                      >
                        {taskLabel(t, name)}
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

function familyLabel(
  t: ReturnType<typeof useTranslation<'models'>>['t'],
  family: string,
): string {
  return t(`filters.family.${family}` as 'filters.family.ComputerVision', {
    defaultValue: family,
  });
}

function taskLabel(
  t: ReturnType<typeof useTranslation<'models'>>['t'],
  name: string,
): string {
  return t(`tasks.${name}` as 'tasks.TextEmbedder', { defaultValue: name });
}

function EntryListItem({
  entry,
  active,
  drifted,
  taskFamilies,
  onSelect,
}: {
  entry: CatalogEntrySnapshot;
  active: boolean;
  drifted: boolean;
  taskFamilies: ReadonlyMap<string, string>;
  onSelect: () => void;
}) {
  const { t } = useTranslation('models');
  const downloads = useSnapshot(downloadsState);
  const models = useSnapshot(modelsState);
  const variantCount = entry.variants?.length ?? 0;

  const aggregate = aggregateEntryStatus(
    entry,
    downloads.state,
    downloads.active,
    downloads.installing,
    models.activeVersions,
  );

  const status: { Icon: typeof Loader2; label: string; spin: boolean } | null =
    aggregate.anyDownloading || aggregate.anyInstalling
      ? { Icon: Loader2, label: t('card.installing'), spin: true }
      : aggregate.anyInstalled
      ? { Icon: CircleCheck, label: t('card.installed'), spin: false }
      : aggregate.anyDownloaded
      ? { Icon: HardDrive, label: t('card.downloaded'), spin: false }
      : aggregate.anyPartial
      ? { Icon: CircleDashed, label: t('card.partial'), spin: false }
      : null;

  return (
    <li>
      <button
        type="button"
        onClick={onSelect}
        aria-current={active ? 'true' : undefined}
        className={cn(
          'flex w-full min-w-0 flex-col gap-0.5 border-b px-3 py-2 text-left transition-colors',
          active
            ? 'cursor-default bg-primary/15 text-foreground'
            : 'cursor-pointer text-foreground hover:bg-muted/60',
        )}
      >
        <div className="flex w-full min-w-0 items-center gap-2">
          {status ? (
            <status.Icon
              className={cn(
                'text-muted-foreground size-3 shrink-0',
                status.spin && 'animate-spin',
              )}
              aria-label={status.label}
            >
              <title>{status.label}</title>
            </status.Icon>
          ) : (
            <span className="size-3 shrink-0" />
          )}
          <span className="min-w-0 flex-1 truncate text-xs font-medium">
            {entry.name}
          </span>
          {variantCount > 1 && (
            <span
              className="text-muted-foreground shrink-0 text-[10px]"
              title={t('list.variantCount', { count: variantCount })}
            >
              {t('list.variantCount', { count: variantCount })}
            </span>
          )}
          {drifted && (
            <ArrowUpCircle
              className="text-muted-foreground size-3 shrink-0"
              aria-label={t('card.updateAvailable')}
            >
              <title>{t('card.updateAvailable')}</title>
            </ArrowUpCircle>
          )}
        </div>
        <span className="text-muted-foreground line-clamp-2 w-full min-w-0 break-words pl-5 text-xs">
          {entry.summary ?? entry.description}
        </span>
        {(entry.tasks ?? []).length > 0 && (
          <div className="flex w-full min-w-0 flex-nowrap gap-1 overflow-hidden pl-5">
            {(entry.tasks ?? []).map((task) => (
              <TaskChipIcon
                key={task}
                task={task}
                family={taskFamilies.get(task.toLowerCase()) ?? ''}
                label={taskLabel(t, task)}
              />
            ))}
          </div>
        )}
      </button>
    </li>
  );
}
