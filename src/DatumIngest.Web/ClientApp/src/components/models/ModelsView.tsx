import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Loader2, Search, X } from 'lucide-react';
import {
  clearFilters,
  filterModels,
  loadModelsCatalog,
  modelsState,
  setQuery,
  setSelectedId,
  setTier,
  type TierFilter,
} from '@/state/models';
import type { CatalogModelSnapshot } from '@/state/models';
import { downloadsState, refreshDownloads } from '@/state/downloads';
import { ModelDetail } from '@/components/models/ModelDetail';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

// VS Code Extensions–style layout: search box + tier tabs in the header,
// compact list of matches on the left, full detail pane for the selected
// model on the right. Tags + task filters were removed in this pass; tier
// stays because Starter / Recommended is genuine curation guidance.

const TIER_OPTIONS: readonly TierFilter[] = ['all', 'starter', 'recommended'];

export function ModelsView() {
  const { t } = useTranslation('models');
  const { manifest, loading, error, tier, query, selectedId } = useSnapshot(modelsState);

  useEffect(() => {
    void loadModelsCatalog();
    void refreshDownloads();
  }, []);

  const filtered = useMemo(
    () => (manifest ? filterModels(manifest, tier, query) : []),
    [manifest, tier, query],
  );

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

  const filtersActive = tier !== 'all' || query.length > 0;

  return (
    <div className="bg-editor flex h-full flex-col overflow-hidden">
      <header className="flex flex-col gap-2 border-b px-4 py-3">
        <div className="flex items-center justify-between">
          <h1 className="text-sm font-medium">{t('title')}</h1>
          <span className="text-muted-foreground text-xs">
            {t('results', { count: filtered.length })}
          </span>
        </div>

        <SearchInput value={query} onChange={setQuery} placeholder={t('search.placeholder')} />

        <div className="flex items-center justify-between gap-2">
          <div className="flex gap-1">
            {TIER_OPTIONS.map((opt) => (
              <TierTab key={opt} active={tier === opt} onClick={() => setTier(opt)}>
                {t(`filters.tier${capitalize(opt)}` as 'filters.tierAll')}
              </TierTab>
            ))}
          </div>
          {filtersActive && (
            <Button variant="ghost" size="sm" onClick={clearFilters}>
              {t('filters.clear')}
            </Button>
          )}
        </div>
      </header>

      <div className="flex flex-1 overflow-hidden">
        <div className="w-80 shrink-0 overflow-y-auto border-r">
          {filtered.length === 0 ? (
            <p className="text-muted-foreground p-6 text-center text-sm">
              {query.length > 0 ? t('noMatches') : t('empty')}
            </p>
          ) : (
            <ul className="flex flex-col">
              {filtered.map((model) => (
                <ModelListItem
                  key={model.id}
                  model={model}
                  active={model.id === selectedId}
                  onSelect={() => model.id && setSelectedId(model.id)}
                />
              ))}
            </ul>
          )}
        </div>

        <div className="flex-1 overflow-y-auto">
          {selectedModel ? (
            <ModelDetail model={selectedModel} />
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
        className="placeholder:text-muted-foreground w-full bg-transparent py-1 pl-7 pr-7 text-xs outline-none"
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

function TierTab({
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
        'rounded-xs px-2 py-0.5 text-xs transition-colors',
        active
          ? 'bg-primary/15 text-primary'
          : 'text-muted-foreground hover:bg-primary/10 hover:text-primary',
      )}
    >
      {children}
    </button>
  );
}

function ModelListItem({
  model,
  active,
  onSelect,
}: {
  model: CatalogModelSnapshot;
  active: boolean;
  onSelect: () => void;
}) {
  const downloads = useSnapshot(downloadsState);
  const modelId = model.id ?? '';
  const installState = downloads.state?.[modelId];
  const activeDownload = downloads.active[modelId];
  const installing = downloads.installing[modelId] === true;

  // Status indicator: a small dot whose color encodes install state.
  // Mirrors VS Code's "installed" green check / "outdated" yellow / "none"
  // but condensed into a single dot so the row stays scannable.
  const dotClass = activeDownload || installing
    ? 'bg-primary animate-pulse'
    : installState === 'installed'
    ? 'bg-emerald-500'
    : installState === 'downloaded'
    ? 'bg-amber-500'
    : installState === 'partial'
    ? 'bg-muted-foreground'
    : 'bg-transparent';

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
          <span className={cn('size-1.5 shrink-0 rounded-full', dotClass)} />
          <span className="min-w-0 flex-1 truncate text-xs font-medium">
            {model.displayName}
          </span>
        </div>
        <span className="text-muted-foreground line-clamp-2 w-full min-w-0 break-words pl-3.5 text-xs">
          {model.description}
        </span>
      </button>
    </li>
  );
}

function capitalize(s: string): string {
  return s.length === 0 ? s : s[0].toUpperCase() + s.slice(1);
}
