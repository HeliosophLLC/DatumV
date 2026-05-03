import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Loader2 } from 'lucide-react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeHighlight from 'rehype-highlight';
import {
  datasetsState,
  dismissError,
  entryCardAssetUrl,
  heroImageUrl,
  installVariant,
  loadEntryCard,
  resolveActiveVariant,
  setSelectedVariantId,
  uninstallVariant,
  type DatasetEntrySnapshot,
  type DatasetVariantSnapshot,
} from '@/state/datasets';
import { CodeBlock } from '@/components/markdown/CodeBlock';
import { isExternalUrl, openExternalUrl } from '@/lib/openExternal';
import { DownloadProgressBar } from '@/components/shared/DownloadProgressBar';
import { ModalityChipLabel, TaskChipLabel } from '@/components/shared/TaskChip';
import { buildTaskFamilyMap } from '@/components/shared/taskStyles';
import { shortenPath } from '@/lib/formatDownload';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

// Detail pane for one dataset entry. Layout (top → bottom):
//   1. Hero band (entry hero image with bottom fade into the page bg).
//   2. Variant tab strip (only when the entry has > 1 variant).
//   3. Entry chrome: name + summary + install/uninstall action for the
//      active variant.
//   4. Active variant chrome: subtitle + per-variant summary + sizes.
//   5. Entry card markdown (when present) or fallback description.
//   6. Tags + attributions.

export function DatasetDetail({ entry }: { entry: DatasetEntrySnapshot }) {
  const { t } = useTranslation('datasets');
  // Task labels live in the `models` namespace under `tasks.<Name>` so
  // both surfaces read the same translations.
  const { t: tModels } = useTranslation('models');
  const { tasks, installStates, active, errors, selectedVariantId } =
    useSnapshot(datasetsState);

  // Name → family lookup so each suitable-for chip can pick up the
  // family-accent left border. Memoised over the task vocabulary so
  // we don't rebuild on every render.
  const taskFamilies = useMemo(() => buildTaskFamilyMap(tasks), [tasks]);

  const variant = resolveActiveVariant(entry, selectedVariantId);
  const variantId = variant?.id ?? '';
  const state = installStates?.[variantId];
  const inFlight = variantId in active ? active[variantId] : null;
  const error = errors[variantId];
  const installed = state === 'installed';

  // Entry card markdown — fetched once per entry, cached in state.
  const [entryCard, setEntryCard] = useState<string | null>(null);
  useEffect(() => {
    if (!entry.name) {
      setEntryCard(null);
      return;
    }
    let cancelled = false;
    void loadEntryCard(entry.name).then((text) => {
      if (!cancelled) setEntryCard(text);
    });
    return () => {
      cancelled = true;
    };
  }, [entry.name]);

  if (!entry.name || !variant) {
    return null;
  }

  return (
    <div className="flex flex-col">
      <HeroBand entryName={entry.name} />

      <div className="flex flex-col gap-4 px-6 py-5">
        <header className="min-w-0">
          <h1 className="text-base font-medium">{entry.name}</h1>
          {entry.summary && (
            <p className="text-muted-foreground mt-1 text-sm">{entry.summary}</p>
          )}
        </header>

        {(entry.modalities?.length ?? 0) > 0 && (
          <div className="flex flex-wrap gap-1.5">
            {(entry.modalities ?? []).map((m) => (
              <ModalityChipLabel
                key={m}
                modality={m}
                label={t(`modality.${m}` as 'modality.Image', { defaultValue: m })}
              />
            ))}
          </div>
        )}

        {(entry.suitableForTasks?.length ?? 0) > 0 && (
          <div className="flex flex-wrap gap-1.5">
            {(entry.suitableForTasks ?? []).map((task) => (
              <TaskChipLabel
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

        {(entry.variants?.length ?? 0) > 1 && (
          <VariantTabs
            entry={entry}
            activeVariantId={variant.id ?? ''}
            installStates={installStates}
            activeInstalls={active}
          />
        )}

        <ActiveVariantCard
          variant={variant}
          datasetDisplayName={entry.name}
          installed={installed}
          installing={inFlight !== null}
          inFlight={inFlight !== null}
          error={error}
        />

        {entryCard ? (
          <EntryCardBody markdown={entryCard} entryName={entry.name} />
        ) : entry.description ? (
          <section>
            <p className="text-foreground text-sm leading-relaxed whitespace-pre-line">
              {entry.description}
            </p>
          </section>
        ) : null}

        {(entry.attributions?.length ?? 0) > 0 && (
          <section className="flex flex-col gap-1">
            <h2 className="text-muted-foreground text-xs font-medium tracking-wide uppercase">
              {t('attributions')}
            </h2>
            <ul className="text-foreground flex flex-col gap-0.5 text-xs">
              {(entry.attributions ?? []).map((line, i) => (
                <li key={i}>{line}</li>
              ))}
            </ul>
          </section>
        )}
      </div>
    </div>
  );
}

// Hero band rendered at the top of the detail card. Height-clamped,
// centered; a gradient overlay fades the bottom into the page
// background so the image bleeds into the body rather than stopping at
// a hard edge.
function HeroBand({ entryName }: { entryName: string }) {
  const [visible, setVisible] = useState(true);
  if (!visible) return null;
  return (
    <div className="relative w-full overflow-hidden">
      <img
        src={heroImageUrl(entryName)}
        alt=""
        className="object-cover h-60 m-auto"
        onError={() => setVisible(false)}
      />
      <div
        aria-hidden
        className="pointer-events-none absolute inset-x-0 bottom-0 h-16 bg-gradient-to-b from-transparent to-[var(--color-editor)]"
      />
    </div>
  );
}

// Tab strip selecting which variant the active-variant section below
// describes. Sits between the entry header (name + summary + install
// action) and the active-variant summary so the user reads "here's the
// dataset → which slice do you want?" → the slice's details. Active
// variant gets the primary-colored bottom border; the rest sit muted.
// Click to switch — the strip's `border-b` defines the rail the
// underlines snap to.
function VariantTabs({
  entry,
  activeVariantId,
  installStates,
  activeInstalls,
}: {
  entry: DatasetEntrySnapshot;
  activeVariantId: string;
  installStates: Readonly<Record<string, string>> | null;
  activeInstalls: Readonly<Record<string, unknown>>;
}) {
  return (
    <div className="border-border flex overflow-x-auto overflow-y-hidden border-b">
      {(entry.variants ?? []).map((v) => {
        const id = v.id ?? '';
        const isActive = id === activeVariantId;
        const isInstalled = installStates?.[id] === 'installed';
        const isInstalling = id in activeInstalls;
        return (
          <button
            key={id}
            type="button"
            onClick={() => setSelectedVariantId(id)}
            aria-pressed={isActive}
            className={cn(
              '-mb-px flex items-center gap-1.5 whitespace-nowrap border-b-2 px-3 py-2 text-xs transition-colors',
              isActive
                ? 'border-primary text-foreground'
                : 'border-transparent text-muted-foreground hover:text-foreground cursor-pointer',
            )}
          >
            {v.displayName ?? id}
            {isInstalling && <Loader2 className="size-3 animate-spin" />}
            {isInstalled && !isInstalling && (
              <span className="text-primary text-[10px]">✓</span>
            )}
          </button>
        );
      })}
    </div>
  );
}

// Bordered card describing the active variant: subtitle + per-variant
// summary + install/uninstall action in the header so the user can
// see at a glance what they're about to install. Sizes grid, in-flight
// progress, and per-variant errors live inside the same box so the
// "variant the install button targets" stays visually unambiguous.
function ActiveVariantCard({
  variant,
  datasetDisplayName,
  installed,
  installing,
  inFlight,
  error,
}: {
  variant: DatasetVariantSnapshot;
  datasetDisplayName: string | undefined;
  installed: boolean;
  installing: boolean;
  inFlight: boolean;
  error: string | undefined;
}) {
  const { t } = useTranslation('datasets');
  const variantId = variant.id ?? '';

  const rows: Array<{ label: string; value: string }> = [];
  if (variant.approxArchiveBytes) {
    rows.push({
      label: t('downloadSize'),
      value: formatBytes(variant.approxArchiveBytes),
    });
  }
  if (variant.approxIngestedBytes) {
    rows.push({
      label: t('ingestedSize'),
      value: formatBytes(variant.approxIngestedBytes),
    });
  }
  const expected = variant.expectedRowCounts ?? {};
  for (const [table, count] of Object.entries(expected)) {
    rows.push({
      label: `${t('expectedRows')} (${table})`,
      value: t('rowCount', { count }),
    });
  }

  return (
    <section className="border-border flex flex-col gap-3 rounded-xs border p-4">
      <header className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-medium">
            {variant.displayName ?? variantId}
          </h2>
          {variant.summary && (
            <p className="text-muted-foreground mt-1 text-sm">{variant.summary}</p>
          )}
        </div>
        <VariantActionButton
          variantId={variantId}
          datasetDisplayName={datasetDisplayName}
          installed={installed}
          installing={installing}
        />
      </header>

      {rows.length > 0 && (
        <div className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1 text-xs">
          {rows.map((row) => (
            <div key={row.label} className="contents">
              <span className="text-muted-foreground">{row.label}</span>
              <span className="font-mono">{row.value}</span>
            </div>
          ))}
        </div>
      )}

      {inFlight && <InFlightProgress variantId={variantId} />}

      {error && (
        <div
          className="text-destructive border-destructive/30 bg-destructive/5 flex items-start justify-between gap-3 rounded-xs border px-3 py-2 text-xs"
          role="alert"
        >
          <span>{error}</span>
          <button
            type="button"
            onClick={() => dismissError(variantId)}
            className="text-destructive/70 hover:text-destructive shrink-0"
          >
            ×
          </button>
        </div>
      )}
    </section>
  );
}

function VariantActionButton({
  variantId,
  datasetDisplayName,
  installed,
  installing,
}: {
  variantId: string;
  datasetDisplayName: string | undefined;
  installed: boolean;
  installing: boolean;
}) {
  const { t } = useTranslation('datasets');
  if (installing) {
    return (
      <Button size="sm" variant="outline" disabled className="shrink-0">
        <Loader2 className="mr-2 size-3.5 animate-spin" />
        {t('progress.starting')}
      </Button>
    );
  }
  if (installed) {
    return (
      <Button
        size="sm"
        variant="outline"
        className="shrink-0"
        onClick={() => void uninstallVariant(variantId)}
      >
        {t('uninstall')}
      </Button>
    );
  }
  return (
    <Button
      size="sm"
      className="shrink-0"
      onClick={() => void installVariant(variantId, datasetDisplayName)}
    >
      {t('install')}
    </Button>
  );
}

function InFlightProgress({ variantId }: { variantId: string }) {
  const { t } = useTranslation('datasets');
  const { active } = useSnapshot(datasetsState);
  const entry = active[variantId];
  if (!entry) return null;

  if (entry.phase === 'downloading' || entry.phase === 'starting') {
    const statusText =
      entry.phase === 'starting' || entry.fileCount === 0
        ? t('progress.starting')
        : t('progress.downloading', {
            current: shortenPath(entry.currentFile || '—'),
            fileIndex: entry.fileIndex,
            fileCount: entry.fileCount,
          });
    return (
      <DownloadProgressBar
        bytesRead={entry.bytesReadTotal}
        bytesTotal={entry.bytesTotalAcrossDataset}
        startedAt={entry.startedAt}
        samples={entry.samples}
        statusText={statusText}
      />
    );
  }

  const label = t('progress.ingesting', {
    table: entry.currentTable || '—',
    jobIndex: entry.jobIndex,
    jobCount: entry.jobCount,
  });
  return (
    <div className="border-border flex items-center gap-2 rounded-xs border px-3 py-2 text-xs">
      <Loader2 className="text-primary size-3.5 animate-spin" />
      <span className="truncate">{label}</span>
    </div>
  );
}

function EntryCardBody({
  markdown,
  entryName,
}: {
  markdown: string;
  entryName: string;
}) {
  return (
    <div className="markdown-body">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[[rehypeHighlight, { detect: true }]]}
        // Rewrite relative asset URLs to the entry-asset endpoint. The
        // card author writes `![alt](sample.png)` referencing a file in
        // the same `cards/<entry>/` directory; the rewrite maps that to
        // the served URL. Absolute URLs, fragment anchors, mailto:, and
        // data: pass through unchanged.
        urlTransform={(url) => {
          if (/^https?:|^data:|^mailto:|^#|^\//i.test(url)) return url;
          return entryCardAssetUrl(entryName, url);
        }}
        components={{
          // The entry's name is rendered as the page-level H1 already;
          // the markdown's title would visually duplicate it. Suppress
          // the leading H1 (any further H1s in the body — rare — also
          // collapse to nothing, on purpose). Authors keep the H1 in
          // the source so the file is readable standalone (GitHub,
          // local editor) without losing structure inside the app.
          h1: () => null,
          pre: ({ children, ...rest }) => (
            <CodeBlock {...rest}>{children}</CodeBlock>
          ),
          a: ({ href, children, ...rest }) => (
            <a
              {...rest}
              href={href}
              onClick={(e) => {
                if (isExternalUrl(href)) {
                  e.preventDefault();
                  openExternalUrl(href);
                }
              }}
            >
              {children}
            </a>
          ),
        }}
      >
        {markdown}
      </ReactMarkdown>
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`;
}
