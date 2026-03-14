import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Download, Loader2, RotateCcw, Trash2 } from 'lucide-react';
import type { CatalogModelSnapshot } from '@/state/models';
import {
  computeEtaSeconds,
  computeRateBytesPerSec,
  downloadsState,
  installModel,
  restartDownload,
  uninstallModel,
  type ActiveDownload,
} from '@/state/downloads';
import { localeState } from '@/state/locale';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Progress } from '@/components/ui/progress';

// Right-pane content for the Models view. Shows full info + actions for
// one selected model. The list row on the left handles search/selection;
// this component owns the heavy chrome (description, badges, license,
// progress, install/uninstall buttons).

export function ModelDetail({ model }: { model: CatalogModelSnapshot }) {
  const { t } = useTranslation('models');
  const downloads = useSnapshot(downloadsState);

  const modelId = model.id ?? '';
  const modelDisplayName = model.displayName ?? modelId;
  const installState = downloads.state?.[modelId];
  const activeDownload = downloads.active[modelId];
  const installing = downloads.installing[modelId] === true;
  const error = downloads.errors[modelId];
  const hasInstallSql = typeof model.installSql === 'string' && model.installSql.length > 0;

  return (
    <article className="mx-auto flex w-full max-w-3xl flex-col gap-4 px-6 py-5">
      <header className="flex flex-col gap-2">
        <div className="flex items-start justify-between gap-3">
          <h2 className="text-xl font-medium">{modelDisplayName}</h2>
          <div className="flex shrink-0 gap-1.5">
            {model.placeholder && (
              <Badge variant="muted">{t('card.comingSoon')}</Badge>
            )}
            {!model.placeholder && installState === 'installed' && !installing && (
              <Badge>{t('card.installed')}</Badge>
            )}
            {!model.placeholder && installState === 'downloaded' && !installing && (
              <Badge variant="muted">{t('card.downloaded')}</Badge>
            )}
            {!model.placeholder && installing && (
              <Badge variant="muted">{t('card.installing')}</Badge>
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
        <p className="text-muted-foreground text-xs font-mono">{modelId}</p>
      </header>

      <div className="flex flex-wrap gap-1.5">
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
        <p className="text-muted-foreground text-xs">
          <span className="font-medium">{t('card.attributions')}</span>{' '}
          {model.attributions!.join(' · ')}
        </p>
      )}

      {activeDownload && <DownloadProgress download={activeDownload} />}

      {error && !activeDownload && (
        <p className="text-destructive text-xs" role="alert">
          {error}
        </p>
      )}

      <DetailActions
        modelId={modelId}
        modelDisplayName={modelDisplayName}
        placeholder={!!model.placeholder}
        installed={installState === 'installed'}
        downloaded={installState === 'downloaded'}
        downloading={!!activeDownload}
        installing={installing}
        hasInstallSql={hasInstallSql}
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
  rateBuffer.current = rateBuffer.current.filter((s) => now - s.t < RATE_SMOOTHING_MS);
  const rate = rateBuffer.current.length > 0
    ? rateBuffer.current.reduce((sum, s) => sum + s.rate, 0) / rateBuffer.current.length
    : null;

  const etaSec = computeEtaSeconds(download, rate);
  const elapsedSec = Math.max(0, Math.round((Date.now() - download.startedAt) / 1000));

  const rateText = rate != null ? formatBytesPerSec(rate) : '';
  const elapsedText = t('card.elapsed', { duration: formatDuration(elapsedSec, locale) });
  const remainingText =
    etaSec != null ? t('card.remaining', { duration: formatDuration(Math.round(etaSec), locale) }) : '';

  return (
    <div className="flex flex-col gap-1">
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
        <span className="inline-block min-w-[5.5rem]">{rateText || ' '}</span>
        <span className="inline-block min-w-[8rem]">{elapsedText}</span>
        <span className="inline-block min-w-[9rem]">{remainingText || ' '}</span>
      </div>
    </div>
  );
}

function DetailActions({
  modelId,
  modelDisplayName,
  placeholder,
  installed,
  downloaded,
  downloading,
  installing,
  hasInstallSql,
  partialBytes,
}: {
  modelId: string;
  modelDisplayName: string;
  placeholder: boolean;
  installed: boolean;
  downloaded: boolean;
  downloading: boolean;
  installing: boolean;
  hasInstallSql: boolean;
  partialBytes: number;
}) {
  const { t } = useTranslation('models');

  if (placeholder) return null;
  if (downloading) {
    return (
      <p className="text-muted-foreground text-xs">
        {t('card.downloadingHint')}
      </p>
    );
  }
  if (installing) {
    return (
      <p className="text-muted-foreground flex items-center gap-2 text-xs">
        <Loader2 className="size-3 animate-spin" />
        {t('card.installingHint')}
      </p>
    );
  }

  if (installed) {
    return (
      <div className="flex justify-end">
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

  // Files-are-there-but-install-didn't-run path. Most common after a
  // process restart (ModelRegistry is in-memory and resets) — the user
  // has the bytes but needs to re-register the SQL-defined model.
  if (downloaded && hasInstallSql) {
    return (
      <div className="flex justify-end gap-2">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => void uninstallModel(modelId)}
        >
          <Trash2 />
          {t('card.remove')}
        </Button>
        <Button
          variant="default"
          size="sm"
          onClick={() => void installModel(modelId, modelDisplayName)}
        >
          {t('card.install')}
        </Button>
      </div>
    );
  }

  // Bytes from a prior interrupted attempt are sitting on disk.
  if (partialBytes > 0) {
    return (
      <div className="flex justify-end gap-2">
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
    <div className="flex justify-end">
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

function formatBytesPerSec(bps: number): string {
  const KB = 1024;
  const MB = KB * 1024;
  const GB = MB * 1024;
  if (bps >= GB) return `${(bps / GB).toFixed(2)} GB/s`;
  if (bps >= MB) return `${(bps / MB).toFixed(1)} MB/s`;
  if (bps >= KB) return `${(bps / KB).toFixed(0)} KB/s`;
  return `${Math.round(bps)} B/s`;
}

const durationFormatterCache = new Map<string, Intl.DurationFormat>();

function getDurationFormatter(locale: string): Intl.DurationFormat {
  let f = durationFormatterCache.get(locale);
  if (!f) {
    f = new Intl.DurationFormat(locale, { style: 'narrow' });
    durationFormatterCache.set(locale, f);
  }
  return f;
}

function formatDuration(totalSec: number, locale: string): string {
  const h = Math.floor(totalSec / 3600);
  const m = Math.floor((totalSec % 3600) / 60);
  const s = totalSec % 60;

  const duration: Intl.DurationInput = {};
  if (h > 0) duration.hours = h;
  if (h > 0 || m > 0) duration.minutes = m;
  if (h === 0) duration.seconds = s;

  return getDurationFormatter(locale).format(duration);
}

function formatPartialSize(bytes: number): string {
  const MB = 1024 * 1024;
  const GB = MB * 1024;
  if (bytes >= GB) return `${(bytes / GB).toFixed(1)} GB`;
  if (bytes >= MB) return `${(bytes / MB).toFixed(0)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${bytes} B`;
}

function shortenPath(path: string): string {
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
