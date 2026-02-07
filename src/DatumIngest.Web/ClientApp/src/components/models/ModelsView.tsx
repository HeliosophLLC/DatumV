import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Download, Loader2, Trash2 } from 'lucide-react';
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
import {
  acceptLicenseAndInstall,
  downloadsState,
  installModel,
  refreshDownloads,
  uninstallModel,
  type ActiveDownload,
} from '@/state/downloads';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Progress } from '@/components/ui/progress';
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
    void refreshDownloads();
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
  const downloads = useSnapshot(downloadsState);

  const modelId = model.id ?? '';
  const installState = downloads.state?.[modelId];
  const activeDownload = downloads.active[modelId];
  const error = downloads.errors[modelId];
  const pendingLicenseId = downloads.needsLicenseAcceptance[modelId];

  return (
    <article className="hover:bg-muted/40 flex flex-col gap-2 rounded-xs border p-4 transition-colors">
      <div className="flex items-start justify-between gap-3">
        <h2 className="text-sm font-medium">{model.displayName}</h2>
        <div className="flex shrink-0 gap-1.5">
          {model.placeholder && (
            <Badge variant="muted">{t('card.comingSoon')}</Badge>
          )}
          {!model.placeholder && installState === 'installed' && (
            <Badge>{t('card.installed')}</Badge>
          )}
          {!model.placeholder && installState === 'partial' && !activeDownload && (
            <Badge variant="muted">{t('card.partial')}</Badge>
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

      {activeDownload && (
        <DownloadProgress download={activeDownload} />
      )}

      {error && !activeDownload && (
        <p className="text-destructive text-xs" role="alert">
          {error}
        </p>
      )}

      <CardActions
        modelId={modelId}
        placeholder={!!model.placeholder}
        installed={installState === 'installed'}
        downloading={!!activeDownload}
        pendingLicenseId={pendingLicenseId}
      />
    </article>
  );
}

function DownloadProgress({ download }: { download: ActiveDownload }) {
  const { t } = useTranslation('models');
  const total = download.bytesTotalAcrossModel;
  const read = download.bytesReadTotal;
  const percent = total > 0 ? (read / total) * 100 : 0;

  return (
    <div className="mt-1 flex flex-col gap-1">
      <Progress value={percent} />
      <div className="text-muted-foreground flex justify-between text-xs">
        <span>
          {download.fileCount > 0
            ? t('card.downloadingFile', {
                index: download.fileIndex,
                count: download.fileCount,
                file: shortenPath(download.currentFile),
              })
            : t('card.downloadingStarting')}
        </span>
        <span>{total > 0 ? `${Math.round(percent)}%` : ''}</span>
      </div>
    </div>
  );
}

function CardActions({
  modelId,
  placeholder,
  installed,
  downloading,
  pendingLicenseId,
}: {
  modelId: string;
  placeholder: boolean;
  installed: boolean;
  downloading: boolean;
  pendingLicenseId?: string;
}) {
  const { t } = useTranslation('models');

  if (placeholder) return null;
  if (downloading) {
    // Cancel intentionally not wired in this round — server has no cancel
    // hub method for installs yet. Show the inert progress + helper text.
    return (
      <p className="text-muted-foreground mt-1 text-xs">
        {t('card.downloadingHint')}
      </p>
    );
  }

  if (installed) {
    return (
      <div className="mt-1 flex justify-end">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => void uninstallModel(modelId)}
        >
          <Trash2 />
          {t('card.remove')}
        </Button>
      </div>
    );
  }

  if (pendingLicenseId) {
    return (
      <div className="mt-1 flex flex-col items-end gap-1">
        <p className="text-muted-foreground text-xs">
          {t('card.licenseRequired', { license: pendingLicenseId })}
        </p>
        <Button
          variant="default"
          size="sm"
          onClick={() => void acceptLicenseAndInstall(modelId)}
        >
          {t('card.acceptAndDownload')}
        </Button>
      </div>
    );
  }

  return (
    <div className="mt-1 flex justify-end">
      <Button
        variant="default"
        size="sm"
        onClick={() => void installModel(modelId)}
      >
        <Download />
        {t('card.download')}
      </Button>
    </div>
  );
}

function shortenPath(path: string): string {
  // Long paths inside HF repos can be unwieldy in a single-line caption.
  // Keep the trailing segment; ellipsise the middle if it's wide.
  if (path.length <= 48) return path;
  const slash = path.lastIndexOf('/');
  if (slash === -1) return path.slice(0, 24) + '…' + path.slice(-24);
  const tail = path.slice(slash + 1);
  return '…/' + tail;
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
