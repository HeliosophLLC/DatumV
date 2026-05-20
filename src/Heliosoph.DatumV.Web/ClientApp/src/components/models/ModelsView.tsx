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
  clearFilters,
  driftedCount,
  filterModels,
  groupByModelFamily,
  groupTasksByFamily,
  installStateCounts,
  isDrifted,
  loadModelsCatalog,
  modelsState,
  setQuery,
  setSelectedId,
  setUpdatesOnly,
  tasksWithAssignedModels,
  toggleInstallState,
  toggleTask,
  type CatalogTaskInfoSnapshot,
  type ModelGroup,
} from '@/state/models';
import type { ModelInstallState } from '@/api/generated/openapi-client';
import type { CatalogModelSnapshot } from '@/state/models';
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

// VS Code Extensions–style layout: search box + a single "Updates" toggle
// in the header (visible only when something is drifted), then a three-
// pane resizable body — task filter on the far left, list of matches in
// the middle, full detail pane on the right.

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
    selectedId,
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

  // Only surface tasks that at least one model actually claims — an
  // unassigned task in the sidebar would empty the list when clicked.
  const visibleTasks = useMemo(
    () => (tasks && manifest ? tasksWithAssignedModels(tasks, manifest) : []),
    [tasks, manifest],
  );

  // Name → family lookup so each row's chips can pick up the family-
  // accent left border. Built once over the task vocabulary so we don't
  // walk it per row.
  const taskFamilies = useMemo(() => buildTaskFamilyMap(tasks), [tasks]);

  const filtered = useMemo(
    () =>
      manifest
        ? filterModels(
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

  // Group filtered entries by model family. Singletons and 1-of-1
  // family survivors render as plain rows; multi-entry families collapse
  // into a single "X variants" row that expands into a picker in the
  // detail pane when selected.
  const grouped = useMemo(() => groupByModelFamily(filtered), [filtered]);

  // Auto-select the first match once the manifest lands. Don't override an
  // existing selection — even if it falls outside the current filter, the
  // user's last-clicked model stays in the detail pane (matches VS Code's
  // behavior, where typing in the search box doesn't unmount the detail).
  useEffect(() => {
    if (selectedId !== null) return;
    if (filtered.length === 0) return;
    const firstId = filtered[0].id;
    if (firstId) setSelectedId(firstId);
  }, [filtered, selectedId]);

  const selectedModel = useMemo(() => {
    if (!manifest || !selectedId) return null;
    return (manifest.models ?? []).find((m) => m.id === selectedId) ?? null;
  }, [manifest, selectedId]);

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
          {/* Updates toggle: always rendered so the row's layout
              stays stable, but disabled when nothing is drifted. */}
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
          {/* Partial is uncommon — collapse it out of the row entirely
              when no model is in that state so the strip stays tidy. */}
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
                {grouped.map((group) =>
                  group.kind === 'single' ? (
                    <ModelListItem
                      key={group.entry.id}
                      model={group.entry}
                      active={group.entry.id === selectedId}
                      drifted={isDrifted(group.entry, activeVersions)}
                      taskFamilies={taskFamilies}
                      onSelect={() => group.entry.id && setSelectedId(group.entry.id)}
                    />
                  ) : (
                    <FamilyListItem
                      key={group.family}
                      group={group}
                      selectedId={selectedId}
                      activeVersions={activeVersions}
                      taskFamilies={taskFamilies}
                      onSelect={setSelectedId}
                    />
                  ),
                )}
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
            {selectedModel ? (
              <ModelDetail model={selectedModel} />
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
  // Same shape as UpdatesToggle, with the disabled-when-zero pattern so
  // the toggle row's layout stays stable as the user installs / removes
  // models. The Partial toggle is hidden by the caller when count === 0
  // (uncommon state) so it doesn't follow this disabled-shell pattern.
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
  // PascalCase family strings (from TaskFamily enum) map to localized
  // section headers via the `filters.family.*` namespace. Unknown families
  // (server added a new one before the front-end caught up) fall through
  // to the raw string so nothing disappears silently.
  return t(`filters.family.${family}` as 'filters.family.ComputerVision', {
    defaultValue: family,
  });
}

function taskLabel(
  t: ReturnType<typeof useTranslation<'models'>>['t'],
  name: string,
): string {
  // Same shape as familyLabel: PascalCase contract name → human-friendly
  // localized label; unknown task names trail back to the raw identifier
  // so a newly-added contract still shows up in the UI.
  return t(`tasks.${name}` as 'tasks.TextEmbedder', { defaultValue: name });
}

function ModelListItem({
  model,
  active,
  drifted,
  taskFamilies,
  onSelect,
}: {
  model: CatalogModelSnapshot;
  active: boolean;
  drifted: boolean;
  taskFamilies: ReadonlyMap<string, string>;
  onSelect: () => void;
}) {
  const { t } = useTranslation('models');
  const downloads = useSnapshot(downloadsState);
  const modelId = model.id ?? '';
  const installState = downloads.state?.[modelId];
  const activeDownload = downloads.active[modelId];
  const installing = downloads.installing[modelId] === true;

  // Status indicator: a small icon whose silhouette encodes install
  // state. Colored install dots competed with the family-accent colors
  // on the task chips, so we read state by shape and keep all icons in
  // a muted hue.
  const status: { Icon: typeof Loader2; label: string; spin: boolean } | null =
    activeDownload || installing
      ? { Icon: Loader2, label: t('card.installing'), spin: true }
      : installState === 'installed'
      ? { Icon: CircleCheck, label: t('card.installed'), spin: false }
      : installState === 'downloaded'
      ? { Icon: HardDrive, label: t('card.downloaded'), spin: false }
      : installState === 'partial'
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
            {model.displayName}
          </span>
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
          {model.summary ?? model.description}
        </span>
        {(model.tasks ?? []).length > 0 && (
          <div className="flex w-full min-w-0 flex-nowrap gap-1 overflow-hidden pl-5">
            {(model.tasks ?? []).map((task) => (
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

function FamilyListItem({
  group,
  selectedId,
  activeVersions,
  taskFamilies,
  onSelect,
}: {
  group: Extract<ModelGroup, { kind: 'family' }>;
  selectedId: string | null;
  activeVersions: Readonly<Record<string, string>>;
  taskFamilies: ReadonlyMap<string, string>;
  onSelect: (id: string) => void;
}) {
  const { t } = useTranslation('models');
  const downloads = useSnapshot(downloadsState);

  const variantIds = group.entries.map((e) => e.id ?? '');
  // The row is "active" when the user has any of this family's variants
  // selected — switching variants inside the family stays on the same
  // row visually.
  const active = selectedId !== null && variantIds.includes(selectedId);

  // Aggregate install state across variants. Priority mirrors the
  // single-row icon precedence: in-flight > installed > downloaded >
  // partial > nothing. The icon represents "the most useful thing this
  // family currently is on disk".
  let anyDownloading = false;
  let anyInstalling = false;
  let anyInstalled = false;
  let anyDownloaded = false;
  let anyPartial = false;
  let anyDrifted = false;
  for (const e of group.entries) {
    const id = e.id ?? '';
    if (downloads.active[id]) anyDownloading = true;
    if (downloads.installing[id] === true) anyInstalling = true;
    const s = downloads.state?.[id];
    if (s === 'installed') anyInstalled = true;
    else if (s === 'downloaded') anyDownloaded = true;
    else if (s === 'partial') anyPartial = true;
    if (isDrifted(e, activeVersions)) anyDrifted = true;
  }
  const status: { Icon: typeof Loader2; label: string; spin: boolean } | null =
    anyDownloading || anyInstalling
      ? { Icon: Loader2, label: t('card.installing'), spin: true }
      : anyInstalled
      ? { Icon: CircleCheck, label: t('card.installed'), spin: false }
      : anyDownloaded
      ? { Icon: HardDrive, label: t('card.downloaded'), spin: false }
      : anyPartial
      ? { Icon: CircleDashed, label: t('card.partial'), spin: false }
      : null;

  const handleClick = () => {
    // Stay on whichever variant the user already had selected when the
    // family was active; otherwise jump to the lead variant.
    const targetId =
      selectedId !== null && variantIds.includes(selectedId)
        ? selectedId
        : group.lead.id ?? '';
    if (targetId) onSelect(targetId);
  };

  return (
    <li>
      <button
        type="button"
        onClick={handleClick}
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
            {group.family}
          </span>
          <span
            className="text-muted-foreground shrink-0 text-[10px]"
            title={t('list.variantCount', { count: group.entries.length })}
          >
            {t('list.variantCount', { count: group.entries.length })}
          </span>
          {anyDrifted && (
            <ArrowUpCircle
              className="text-muted-foreground size-3 shrink-0"
              aria-label={t('card.updateAvailable')}
            >
              <title>{t('card.updateAvailable')}</title>
            </ArrowUpCircle>
          )}
        </div>
        <span className="text-muted-foreground line-clamp-2 w-full min-w-0 break-words pl-5 text-xs">
          {group.lead.summary ?? group.lead.description}
        </span>
        {(group.lead.tasks ?? []).length > 0 && (
          <div className="flex w-full min-w-0 flex-nowrap gap-1 overflow-hidden pl-5">
            {(group.lead.tasks ?? []).map((task) => (
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

