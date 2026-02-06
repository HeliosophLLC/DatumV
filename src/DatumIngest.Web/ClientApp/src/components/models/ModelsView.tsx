import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Loader2 } from 'lucide-react';
import {
  clearFilters,
  collectTags,
  collectTasks,
  filterModels,
  loadModelsCatalog,
  modelsState,
  setTask,
  setTier,
  toggleTag,
  type TierFilter,
} from '@/state/models';
import type { CatalogModelSnapshot } from '@/state/models';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';

// Models catalog browser. Loads the manifest on mount, renders filter
// chips (tier / task / tags) at the top, and a card list of matching
// models below. Download / license-acceptance UI lands in the next round
// — for now the cards are read-only browse.

const TIER_OPTIONS: readonly TierFilter[] = ['all', 'starter', 'recommended'];

export function ModelsView() {
  const { t } = useTranslation('models');
  const { manifest, loading, error, tier, task, tags } = useSnapshot(modelsState);

  useEffect(() => {
    void loadModelsCatalog();
  }, []);

  // Filter chip vocabularies are derived from the manifest, not hardcoded —
  // so adding a new task in catalog.json automatically surfaces a chip
  // without UI changes.
  const tasks = useMemo(
    () => (manifest ? collectTasks(manifest) : []),
    [manifest],
  );
  const allTags = useMemo(
    () => (manifest ? collectTags(manifest) : []),
    [manifest],
  );
  const filtered = useMemo(
    () =>
      manifest ? filterModels(manifest, tier, task, tags as readonly string[]) : [],
    [manifest, tier, task, tags],
  );

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
    <div className="flex h-full flex-col overflow-hidden">
      <header className="flex flex-col gap-3 border-b px-6 py-4">
        <div className="flex items-center justify-between">
          <h1 className="text-lg font-medium">{t('title')}</h1>
          <span className="text-muted-foreground text-xs">
            {t('results', { count: filtered.length })}
          </span>
        </div>

        <FilterRow label={t('filters.tier')}>
          {TIER_OPTIONS.map((opt) => (
            <FilterChip
              key={opt}
              active={tier === opt}
              onClick={() => setTier(opt)}
            >
              {t(`filters.tier${capitalize(opt)}` as 'filters.tierAll')}
            </FilterChip>
          ))}
        </FilterRow>

        <FilterRow label={t('filters.task')}>
          <FilterChip active={task === null} onClick={() => setTask(null)}>
            {t('filters.taskAll')}
          </FilterChip>
          {tasks.map((taskName) => (
            <FilterChip
              key={taskName}
              active={task === taskName}
              onClick={() => setTask(taskName)}
            >
              {taskName}
            </FilterChip>
          ))}
        </FilterRow>

        {allTags.length > 0 && (
          <FilterRow label={t('filters.tags')}>
            {allTags.map((tag) => (
              <FilterChip
                key={tag}
                active={tags.includes(tag)}
                onClick={() => toggleTag(tag)}
              >
                {tag}
              </FilterChip>
            ))}
          </FilterRow>
        )}

        {(tier !== 'all' || task !== null || tags.length > 0) && (
          <div className="flex">
            <Button variant="ghost" size="sm" onClick={clearFilters}>
              {t('filters.clear')}
            </Button>
          </div>
        )}
      </header>

      <div className="flex-1 overflow-y-auto px-6 py-4">
        {filtered.length === 0 ? (
          <p className="text-muted-foreground py-8 text-center text-sm">{t('empty')}</p>
        ) : (
          <div className="mx-auto flex w-full max-w-4xl flex-col gap-3">
            {filtered.map((model) => (
              <ModelCard key={model.id} model={model} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

function FilterRow({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="flex flex-wrap items-baseline gap-2">
      <span className="text-muted-foreground w-16 text-xs uppercase tracking-wide">
        {label}
      </span>
      <div className="flex flex-wrap gap-1.5">{children}</div>
    </div>
  );
}

function FilterChip({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        'rounded-xs border px-2 py-0.5 text-xs transition-colors',
        active
          ? 'border-primary bg-primary text-primary-foreground'
          : 'border-border text-muted-foreground hover:border-foreground/40 hover:text-foreground',
      )}
    >
      {children}
    </button>
  );
}

function ModelCard({ model }: { model: CatalogModelSnapshot }) {
  const { t } = useTranslation('models');
  return (
    <article className="hover:bg-muted/40 flex flex-col gap-2 rounded-xs border p-4 transition-colors">
      <div className="flex items-start justify-between gap-3">
        <h2 className="text-sm font-medium">{model.displayName}</h2>
        <div className="flex shrink-0 gap-1.5">
          {model.placeholder && (
            <Badge variant="muted">{t('card.comingSoon')}</Badge>
          )}
          {model.requiresHfLogin && (
            <Badge variant="outline">{t('card.gated')}</Badge>
          )}
        </div>
      </div>
      <p className="text-muted-foreground text-sm">{model.description}</p>
      <div className="mt-1 flex flex-wrap gap-1.5">
        {model.task && <Badge variant="outline">{model.task}</Badge>}
        {typeof model.approxSizeMb === 'number' && (
          <Badge variant="outline">
            {t('card.size', { size: model.approxSizeMb })}
          </Badge>
        )}
        {model.hardware?.preferred && (
          <Badge variant="outline">{hardwareLabel(t, model.hardware.preferred)}</Badge>
        )}
        {(model.licenseIds ?? []).map((id) => (
          <Badge key={id} variant="secondary">
            {id}
          </Badge>
        ))}
      </div>
    </article>
  );
}

function hardwareLabel(
  t: ReturnType<typeof useTranslation<'models'>>['t'],
  preferred: string,
): string {
  if (preferred === 'cpu') return t('card.hardwareCpu');
  if (preferred === 'gpu') return t('card.hardwareGpu');
  if (preferred === 'any') return t('card.hardwareAny');
  return preferred;
}

function capitalize(s: string): string {
  return s.length === 0 ? s : s[0].toUpperCase() + s.slice(1);
}
