import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Download, Loader2, RotateCcw, Trash2 } from 'lucide-react';
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
  computeEtaSeconds,
  computeRateBytesPerSec,
  downloadsState,
  installModel,
  refreshDownloads,
  restartDownload,
  uninstallModel,
  type ActiveDownload,
} from '@/state/downloads';
import { localeState } from '@/state/locale';
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
  const modelDisplayName = model.displayName ?? modelId;
  const installState = downloads.state?.[modelId];
  const activeDownload = downloads.active[modelId];
  const error = downloads.errors[modelId];

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
      {(model.attributions?.length ?? 0) > 0 && (
        <p className="text-muted-foreground mt-1 text-xs">
          <span className="font-medium">{t('card.attributions')}</span>{' '}
          {model.attributions!.join(' · ')}
        </p>
      )}

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
        modelDisplayName={modelDisplayName}
        placeholder={!!model.placeholder}
        installed={installState === 'installed'}
        downloading={!!activeDownload}
        partialBytes={downloads.partials[modelId] ?? 0}
      />
    </article>
  );
}

// 3-second moving-average window. Long enough to absorb single-event
// jitter (file-boundary stalls, TCP buffer flushes) without making the
// display feel laggy when the actual speed shifts.
const RATE_SMOOTHING_MS = 3_000;

function DownloadProgress({ download }: { download: ActiveDownload }) {
  const { t } = useTranslation('models');
  const { resolved: locale } = useSnapshot(localeState);
  const total = download.bytesTotalAcrossModel;
  const read = download.bytesReadTotal;
  const percent = total > 0 ? (read / total) * 100 : 0;

  // Tick every second so elapsed / ETA refresh even when progress events
  // pause (e.g. between files). The ticker drives only this component's
  // re-render; the underlying samples/startedAt come from valtio state.
  const [, setTick] = useState(0);
  useEffect(() => {
    const id = window.setInterval(() => setTick((n) => n + 1), 1000);
    return () => window.clearInterval(id);
  }, []);

  // Smooth the rate over the last RATE_SMOOTHING_MS so the displayed speed
  // and ETA don't snap with every progress event. Raw rate (from the
  // sliding sample window) is fed in once per render; the buffer holds
  // recent readings and we display their mean.
  const rateBuffer = useRef<{ t: number; rate: number }[]>([]);
  const rawRate = computeRateBytesPerSec(download.samples);
  const now = performance.now();
  if (rawRate != null) {
    rateBuffer.current.push({ t: now, rate: rawRate });
  }
  // Drop readings older than the smoothing window. Empty buffer → null.
  rateBuffer.current = rateBuffer.current.filter((s) => now - s.t < RATE_SMOOTHING_MS);
  const rate = rateBuffer.current.length > 0
    ? rateBuffer.current.reduce((sum, s) => sum + s.rate, 0) / rateBuffer.current.length
    : null;

  const etaSec = computeEtaSeconds(download, rate);
  const elapsedSec = Math.max(0, Math.round((Date.now() - download.startedAt) / 1000));

  // Each stat cell has a fixed minimum width so that as the rate digits
  // shift (e.g. "1.6 MB/s" → "980 KB/s"), the elapsed/remaining values
  // don't bounce horizontally. `tabular-nums` keeps the digits themselves
  // aligned within a cell. Empty cells render a non-breaking space so the
  // grid layout stays stable across the "no rate yet" and "no ETA" states.
  const rateText = rate != null ? formatBytesPerSec(rate) : '';
  const elapsedText = t('card.elapsed', { duration: formatDuration(elapsedSec, locale) });
  const remainingText =
    etaSec != null ? t('card.remaining', { duration: formatDuration(Math.round(etaSec), locale) }) : '';

  return (
    <div className="mt-1 flex flex-col gap-1">
      <Progress value={percent} />
      <div className="text-muted-foreground flex justify-between gap-2 text-xs">
        <span className="truncate">
          {download.fileCount > 0
            ? t('card.downloadingFile', {
                index: download.fileIndex,
                count: download.fileCount,
                file: shortenPath(download.currentFile),
              })
            : t('card.downloadingStarting')}
        </span>
        <span className="shrink-0">{total > 0 ? `${Math.round(percent)}%` : ''}</span>
      </div>
      <div className="text-muted-foreground flex gap-3 text-xs tabular-nums">
        <span className="inline-block min-w-[5.5rem]">{rateText || ' '}</span>
        <span className="inline-block min-w-[8rem]">{elapsedText}</span>
        <span className="inline-block min-w-[9rem]">{remainingText || ' '}</span>
      </div>
    </div>
  );
}

function formatBytesPerSec(bps: number): string {
  // Binary units — matches Windows Explorer + most download UIs people
  // see daily. KB/MB labels (not KiB/MiB) for the same reason.
  const KB = 1024;
  const MB = KB * 1024;
  const GB = MB * 1024;
  if (bps >= GB) return `${(bps / GB).toFixed(2)} GB/s`;
  if (bps >= MB) return `${(bps / MB).toFixed(1)} MB/s`;
  if (bps >= KB) return `${(bps / KB).toFixed(0)} KB/s`;
  return `${Math.round(bps)} B/s`;
}

// Cache one Intl.DurationFormat per locale. Constructing a formatter is
// non-trivial (locale data lookup, options resolution) and we call this
// once per render tick per active download — so memoise by locale tag.
const durationFormatterCache = new Map<string, Intl.DurationFormat>();

function getDurationFormatter(locale: string): Intl.DurationFormat {
  let f = durationFormatterCache.get(locale);
  if (!f) {
    // 'narrow' style → "1h 23m" shape across locales; matches the column
    // widths we sized for and stays compact in non-English locales where
    // 'short' / 'long' can balloon.
    f = new Intl.DurationFormat(locale, { style: 'narrow' });
    durationFormatterCache.set(locale, f);
  }
  return f;
}

function formatDuration(totalSec: number, locale: string): string {
  // Decompose into the largest units the format will surface. Hours/min/sec
  // covers everything up to a sensible download duration; days+ falls back
  // to many-hours which is the right tradeoff for our domain.
  const h = Math.floor(totalSec / 3600);
  const m = Math.floor((totalSec % 3600) / 60);
  const s = totalSec % 60;

  // Trim zero-leading fields so the formatter doesn't emit "0h 38s" —
  // Intl.DurationFormat happily renders zero components literally.
  const duration: Intl.DurationInput = {};
  if (h > 0) duration.hours = h;
  if (h > 0 || m > 0) duration.minutes = m;
  // Drop seconds once we're at the hour scale; "1h 23m 5s" is noise.
  if (h === 0) duration.seconds = s;

  return getDurationFormatter(locale).format(duration);
}

function CardActions({
  modelId,
  modelDisplayName,
  placeholder,
  installed,
  downloading,
  partialBytes,
}: {
  modelId: string;
  modelDisplayName: string;
  placeholder: boolean;
  installed: boolean;
  downloading: boolean;
  partialBytes: number;
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

  // Bytes from a prior interrupted attempt are sitting on disk. Server-side
  // installModel resumes naturally from those bytes; Restart wipes them so
  // the next attempt starts from zero. The latter is the escape hatch when
  // a corrupted partial blocks completion.
  if (partialBytes > 0) {
    return (
      <div className="mt-1 flex justify-end gap-2">
        <Button
          variant="outline"
          size="sm"
          onClick={() => void restartDownload(modelId, modelDisplayName)}
        >
          <RotateCcw />
          {t('card.restart')}
        </Button>
        <Button
          variant="default"
          size="sm"
          onClick={() => void installModel(modelId, modelDisplayName)}
        >
          <Download />
          {t('card.resume', { size: formatPartialSize(partialBytes) })}
        </Button>
      </div>
    );
  }

  return (
    <div className="mt-1 flex justify-end">
      <Button
        variant="default"
        size="sm"
        onClick={() => void installModel(modelId, modelDisplayName)}
      >
        <Download />
        {t('card.download')}
      </Button>
    </div>
  );
}

function formatPartialSize(bytes: number): string {
  // Single-decimal MB / GB — same scale convention as the in-flight
  // download speed display, so the user reads "1.6 MB/s" and "120 MB done"
  // in compatible units.
  const MB = 1024 * 1024;
  const GB = MB * 1024;
  if (bytes >= GB) return `${(bytes / GB).toFixed(1)} GB`;
  if (bytes >= MB) return `${(bytes / MB).toFixed(0)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${bytes} B`;
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
