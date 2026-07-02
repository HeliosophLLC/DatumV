import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { ChevronRight, Loader2 } from 'lucide-react';
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
  loadRecipeSql,
  resolveActiveVariant,
  setSelectedVariantId,
  uninstallVariant,
  type DatasetEntrySnapshot,
  type DatasetVariantSnapshot,
} from '@/state/datasets';
import { CodeBlock } from '@/components/markdown/CodeBlock';
import { InlineMarkdown } from '@/components/markdown/InlineMarkdown';
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
      <HeroBand key={entry.name} entryName={entry.name} />

      <div className="flex flex-col gap-4 px-6 py-5">
        <header className="min-w-0">
          <h1 className="text-base font-medium">{entry.name}</h1>
          {entry.summary && (
            <p className="text-muted-foreground mt-1 text-sm">
              <InlineMarkdown>{entry.summary}</InlineMarkdown>
            </p>
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

        <RecipeSection variant={variant} />

        {entryCard ? (
          <EntryCardBody markdown={entryCard} entryName={entry.name} />
        ) : entry.description ? (
          <section>
            <p className="text-foreground text-sm leading-relaxed">
              <InlineMarkdown>{entry.description}</InlineMarkdown>
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
            <p className="text-muted-foreground mt-1 text-sm">
              <InlineMarkdown>{variant.summary}</InlineMarkdown>
            </p>
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
      entry.phase === 'starting'
        ? t('progress.starting')
        : entry.fileCount > 0
          ? t('progress.downloading', {
              current: shortenPath(entry.currentFile || '—'),
              fileIndex: entry.fileIndex,
              fileCount: entry.fileCount,
            })
          : t('progress.downloadingNoCount', {
              current: shortenPath(entry.currentFile || '—'),
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

  const rowsSoFar = entry.rowsWrittenSoFar ?? 0;
  const label =
    rowsSoFar > 0
      ? t('progress.ingestingWithRows', {
          table: entry.currentTable || '—',
          jobIndex: entry.jobIndex,
          jobCount: entry.jobCount,
          rows: rowsSoFar.toLocaleString(),
        })
      : t('progress.ingesting', {
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

// Snapshot shapes derived from the variant so the recipe helpers stay
// assignable from the deep-readonly `useSnapshot` value.
type RecipeVersion = NonNullable<DatasetVariantSnapshot['versions']>[number];
type RecipeJobT = NonNullable<RecipeVersion['ingest']>[number];
type RecipeSourceT = NonNullable<RecipeVersion['sources']>[number];

// Expandable "View recipe" section. Surfaces exactly how the active
// variant turns its raw download into the installed table(s): the
// download sources, then per ingest job either the SQL recipe script
// (fetched lazily + syntax-highlighted) or a note that the archive is
// ingested directly. Sits between the variant card and the entry card
// body so the reader flows "what is this / what will I install → how is
// it built → the long-form writeup."
function RecipeSection({ variant }: { variant: DatasetVariantSnapshot }) {
  const { t } = useTranslation('datasets');
  const [open, setOpen] = useState(false);

  // versions[0] is the recommended cut the installer runs; the manifest
  // validator guarantees it carries non-empty sources + ingest.
  const version = variant.versions?.[0];
  const jobs = version?.ingest ?? [];
  const sources = version?.sources ?? [];
  if (jobs.length === 0) return null;

  return (
    <section className="border-border rounded-xs border">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="flex w-full cursor-pointer items-center gap-1.5 px-4 py-2.5 text-left text-sm font-medium"
      >
        <ChevronRight
          className={cn('size-4 transition-transform', open && 'rotate-90')}
        />
        {t('recipe.view')}
      </button>

      {open && (
        <div className="flex flex-col gap-4 border-t px-4 py-3">
          {sources.length > 0 && (
            <div className="flex flex-col gap-1">
              <h3 className="text-muted-foreground text-xs font-medium tracking-wide uppercase">
                {t('recipe.sources')}
              </h3>
              <ul className="flex flex-col gap-0.5 text-xs">
                {sources.map((s, i) => {
                  const d = describeSource(s);
                  return (
                    <li key={i} className="flex flex-wrap items-baseline gap-1.5">
                      <span className="text-muted-foreground">{d.kind}</span>
                      {d.locator && (
                        <span className="font-mono break-all">{d.locator}</span>
                      )}
                    </li>
                  );
                })}
              </ul>
            </div>
          )}

          {jobs.map((job, i) => (
            <RecipeJob key={job.tableName ?? i} job={job} open={open} />
          ))}
        </div>
      )}
    </section>
  );
}

// One ingest job inside the recipe section. SQL jobs lazily fetch and
// render their script (only once the section is opened); direct jobs
// state that the source archive is ingested as-is.
function RecipeJob({ job, open }: { job: RecipeJobT; open: boolean }) {
  const { t } = useTranslation('datasets');
  const [sql, setSql] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const sqlFile = job.sqlFile ?? null;

  useEffect(() => {
    if (!open || !sqlFile) return;
    let cancelled = false;
    setLoading(true);
    void loadRecipeSql(sqlFile).then((text) => {
      if (cancelled) return;
      setSql(text);
      setLoading(false);
    });
    return () => {
      cancelled = true;
    };
  }, [open, sqlFile]);

  const artifacts = recipeArtifacts(job);

  return (
    <div className="flex flex-col gap-2">
      <h3 className="text-muted-foreground text-xs font-medium tracking-wide uppercase">
        {t('recipe.table', { table: job.tableName ?? '' })}
      </h3>

      {sqlFile ? (
        <>
          {artifacts.length > 0 && (
            <div className="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-0.5 text-xs">
              {artifacts.map((a) => (
                <div key={a.name} className="contents">
                  <span className="text-muted-foreground font-mono">${a.name}</span>
                  <span className="font-mono break-all">{a.path}</span>
                </div>
              ))}
            </div>
          )}
          {loading ? (
            <div className="text-muted-foreground flex items-center gap-2 text-xs">
              <Loader2 className="size-3.5 animate-spin" />
              {t('recipe.loading')}
            </div>
          ) : sql ? (
            <SqlBlock sql={sql} />
          ) : (
            <p className="text-muted-foreground text-xs">{t('recipe.unavailable')}</p>
          )}
        </>
      ) : (
        <p className="text-foreground text-xs">
          {t('recipe.direct', { source: job.sourcePath ?? '' })}
        </p>
      )}
    </div>
  );
}

// Renders a SQL recipe through the same markdown pipeline the entry card
// uses, so the code block picks up highlight.js theming + the shared
// Copy affordance. Wrapping the raw SQL in a fenced ```sql block is the
// least-code way to reuse that pipeline.
function SqlBlock({ sql }: { sql: string }) {
  const markdown = '```sql\n' + sql.replace(/\s+$/, '') + '\n```';
  return (
    <div className="markdown-body">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[[rehypeHighlight, { detect: true }]]}
        components={{
          pre: ({ children, ...rest }) => <CodeBlock {...rest}>{children}</CodeBlock>,
        }}
      >
        {markdown}
      </ReactMarkdown>
    </div>
  );
}

// Compact human label for a download source. Reads the polymorphic
// `type` discriminator and surfaces the primary locator (repo, tag, or
// URL list). Subtype fields aren't on the readonly base snapshot, so
// widen to read them.
function describeSource(source: RecipeSourceT): { kind: string; locator: string } {
  const s = source as {
    type?: string;
    repo?: string;
    tag?: string;
    urls?: ReadonlyArray<{ url?: string }>;
  };
  switch (s.type) {
    case 'huggingface':
      return { kind: 'HuggingFace', locator: s.repo ?? '' };
    case 'github-release':
      return { kind: 'GitHub', locator: [s.repo, s.tag].filter(Boolean).join(' @ ') };
    case 'https':
      return {
        kind: 'HTTPS',
        locator: (s.urls ?? [])
          .map((u) => u.url ?? '')
          .filter(Boolean)
          .join(', '),
      };
    default:
      return { kind: s.type ?? 'source', locator: '' };
  }
}

// The raw-cache files a SQL recipe binds as `$name` parameters. Exactly
// one of `artifact` (single, bound as $artifact) or `artifacts` (named
// map) is set on a SQL job.
function recipeArtifacts(job: RecipeJobT): Array<{ name: string; path: string }> {
  if (job.artifact) return [{ name: 'artifact', path: job.artifact }];
  if (job.artifacts) {
    return Object.entries(job.artifacts).map(([name, path]) => ({ name, path }));
  }
  return [];
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
