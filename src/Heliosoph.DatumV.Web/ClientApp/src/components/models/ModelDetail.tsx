import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { ChevronDown, ChevronRight, CircleCheck, Download, HardDrive, Loader2, RotateCcw, Trash2 } from 'lucide-react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeHighlight from 'rehype-highlight';
import { CodeBlock } from '@/components/markdown/CodeBlock';
import { isExternalUrl, openExternalUrl } from '@/lib/openExternal';
import { openDocInDocsTab, resolveDocCorpusLink } from '@/state/docs';
import {
  isDriftedVariant,
  loadEntryCard,
  modelsState,
  openModelEntry,
  resolveCardEntryLink,
  setSelectedVariant,
  type CatalogEntrySnapshot,
  type CatalogVariantSnapshot,
} from '@/state/models';
import { buildTaskFamilyMap } from '@/components/shared/taskStyles';
import { TaskChipLabel } from '@/components/shared/TaskChip';
import { cn } from '@/lib/utils';
import {
  activateVersion,
  deleteVersion,
  downloadsState,
  installModel,
  installPinnedVersion,
  restartDownload,
  uninstallModel,
  type ActiveDownload,
  type PythonInstallStep,
} from '@/state/downloads';
import { DownloadProgressBar } from '@/components/shared/DownloadProgressBar';
import { shortenPath } from '@/lib/formatDownload';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Progress } from '@/components/ui/progress';

// Right-pane content for the Models view. The entry owns chrome (hero,
// description, tasks, license, attributions, optional card markdown); the
// active variant owns install actions, hardware/size badges, and the
// previous-versions disclosure. A variant tab strip flips between
// variants without reloading the entry.

export function ModelDetail({
  entry,
  variant,
}: {
  entry: CatalogEntrySnapshot;
  variant: CatalogVariantSnapshot;
}) {
  const { t } = useTranslation('models');
  const downloads = useSnapshot(downloadsState);
  const models = useSnapshot(modelsState);

  const entryName = entry.name ?? '';
  const variantId = variant.id ?? '';
  const variantDisplayName = variant.displayName ?? variantId;

  const installState = downloads.state?.[variantId];
  const activeDownload = downloads.active[variantId];
  const installing = downloads.installing[variantId] === true;
  const error = downloads.errors[variantId];
  const drifted = isDriftedVariant(variant, models.activeVersions);
  const activeVersion = models.activeVersions[variantId];
  const latestVersion = variant.versions?.[0]?.version;

  const taskFamilies = useMemo(() => buildTaskFamilyMap(models.tasks), [models.tasks]);

  const venvStep = downloads.venvSteps[variantId];
  const hostStep = downloads.pythonHostStep;
  const activeStep: PythonInstallStep | null = venvStep ?? (installing ? hostStep : null) ?? null;

  // Entry card markdown — fetched once per entry, cached in state.
  const [entryCard, setEntryCard] = useState<string | null>(null);
  useEffect(() => {
    let cancelled = false;
    void loadEntryCard(entryName).then((text) => {
      if (!cancelled) setEntryCard(text);
    });
    return () => {
      cancelled = true;
    };
  }, [entryName]);

  const variants = entry.variants ?? [];
  const showTabStrip = variants.length > 1;

  return (
    <article className="mx-auto flex w-full max-w-3xl flex-col">
      <ModelHeroBand entryName={entryName} />
      <div className="flex flex-col gap-4 px-6 py-5">
        <header className="flex flex-col gap-2">
          <div className="flex items-start justify-between gap-3">
            <h2 className="text-xl font-medium">{entry.name}</h2>
            <div className="flex shrink-0 gap-1.5">
              {(entry.tags ?? []).slice(0, 2).map((tag) => (
                <Badge key={tag} variant="outline">{tag}</Badge>
              ))}
            </div>
          </div>
          {entry.summary && (
            <p className="text-foreground text-sm leading-relaxed">{entry.summary}</p>
          )}
          {entry.description && (
            <p className="text-muted-foreground text-xs leading-relaxed">{entry.description}</p>
          )}
        </header>

        <div className="flex flex-wrap gap-1.5">
          {(entry.tasks ?? []).map((task) => (
            <TaskChipLabel
              key={task}
              task={task}
              family={taskFamilies.get(task.toLowerCase()) ?? ''}
              label={t(`tasks.${task}` as 'tasks.TextEmbedder', { defaultValue: task })}
            />
          ))}
          {(entry.licenseIds ?? []).map((id) => (
            <Badge key={id} variant="outline">{id}</Badge>
          ))}
        </div>

        {(entry.attributions?.length ?? 0) > 0 && (
          <p className="text-muted-foreground text-xs">
            <span className="font-medium">{t('card.attributions')}</span>{' '}
            {entry.attributions!.join(' · ')}
          </p>
        )}

        {showTabStrip && (
          <div className="border-border flex overflow-x-auto overflow-y-hidden border-b">
            {variants.map((v) => {
              const vid = v.id ?? '';
              const isActive = vid === variantId;
              return (
                <button
                  key={vid}
                  type="button"
                  onClick={() => vid && setSelectedVariant(vid)}
                  aria-pressed={isActive}
                  className={cn(
                    '-mb-px flex items-center gap-1.5 whitespace-nowrap border-b-2 px-3 py-2 text-xs transition-colors',
                    isActive
                      ? 'border-primary text-foreground'
                      : 'border-transparent text-muted-foreground hover:text-foreground cursor-pointer',
                  )}
                >
                  {v.displayName ?? vid}
                </button>
              );
            })}
          </div>
        )}

        <section className="border-border flex flex-col gap-3 rounded-xs border p-4">
          <header className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <h2 className="text-sm font-medium">{variantDisplayName}</h2>
              {variant.summary && (
                <p className="text-muted-foreground mt-1 text-sm">{variant.summary}</p>
              )}
            </div>
            {!variant.placeholder && (
              <DetailActions
                variantId={variantId}
                variantDisplayName={variantDisplayName}
                placeholder={!!variant.placeholder}
                installed={installState === 'installed'}
                downloaded={installState === 'downloaded'}
                downloading={!!activeDownload}
                installing={installing}
                partialBytes={downloads.partials[variantId] ?? 0}
                installStep={activeStep}
              />
            )}
          </header>

          <div className="flex flex-wrap items-center gap-1.5">
            {!variant.placeholder && installState === 'installed' && !installing && (
              <span className="flex items-center gap-1 text-xs text-primary">
                <CircleCheck className="size-3.5" />
                {t('card.installed')}
              </span>
            )}
            {!variant.placeholder && installState === 'downloaded' && !installing && (
              <span className="flex items-center gap-1 text-xs text-primary">
                <HardDrive className="size-3.5" />
                {t('card.downloaded')}
              </span>
            )}
            {variant.placeholder && (
              <Badge variant="muted">{t('card.comingSoon')}</Badge>
            )}
            {!variant.placeholder && installing && (
              <Badge variant="muted">{t('card.installing')}</Badge>
            )}
            {!variant.placeholder && installState === 'partial' && !activeDownload && (
              <Badge variant="muted">{t('card.partial')}</Badge>
            )}
            {drifted && !installing && !activeDownload && (
              <Badge
                variant="outline"
                title={t('card.driftTooltip', {
                  active: activeVersion ?? '',
                  latest: latestVersion ?? '',
                })}
              >
                {t('card.updateAvailable')}
              </Badge>
            )}
            {variant.requiresHfLogin && (
              <Badge variant="outline">{t('card.gated')}</Badge>
            )}
            {(variant.tags ?? []).map((tag) => (
              <Badge key={tag} variant="outline">{tag}</Badge>
            ))}
            {typeof variant.approxSizeMb === 'number' && (
              <Badge variant="outline">
                {t('card.size', { size: variant.approxSizeMb })}
              </Badge>
            )}
            {variant.hardware?.preferred && (
              <Badge variant="outline">{hardwareLabel(t, variant.hardware.preferred)}</Badge>
            )}
          </div>

          <p className="text-muted-foreground text-xs font-mono">{variantId}</p>

          {activeDownload && <DownloadProgress download={activeDownload} />}

          {error && !activeDownload && (
            <p className="text-destructive text-xs" role="alert">
              {error}
            </p>
          )}

          <PreviousVersionsDisclosure
            variant={variant}
            variantId={variantId}
            variantDisplayName={variantDisplayName}
            activeVersion={activeVersion}
            versionsOnDisk={models.versionsOnDisk[variantId] ?? []}
            busy={!!activeDownload || installing}
          />
        </section>
        
        {entryCard && (
          <div className="markdown-body">
            <ReactMarkdown
              remarkPlugins={[remarkGfm]}
              rehypePlugins={[[rehypeHighlight, { detect: true }]]}
              // Rewrite relative URLs to the entry-card-asset endpoint.
              urlTransform={(url) => {
                if (/^https?:|^data:|^mailto:|^#|^\//i.test(url)) return url;
                // Leave sibling-card and docs-corpus links untouched so the
                // click handler can route them in-app (to the linked model
                // or the Documentation tab) rather than rewriting them to
                // the card-asset endpoint.
                if (resolveCardEntryLink(entry.cardFile, url)) return url;
                if (resolveDocCorpusLink(url)) return url;
                const clean = url.replace(/^\.\//, '');
                return `/api/model-catalog/entries/${encodeURIComponent(entryName)}/card/assets/${clean}`;
              }}
              components={{
                pre: ({ children, ...rest }) => (
                  <CodeBlock {...rest}>{children}</CodeBlock>
                ),
                a: ({ href, children, ...rest }) => {
                  const targetEntry = href
                    ? resolveCardEntryLink(entry.cardFile, href)
                    : null;
                  const docPath = href ? resolveDocCorpusLink(href) : null;
                  // Captured separately: `isExternalUrl`'s type guard
                  // narrows `href` away below, so the safety-net check at
                  // the end can't reuse it.
                  const rawHref = href ?? '';
                  return (
                    <a
                      {...rest}
                      href={href}
                      title={isExternalUrl(href) ? href : rest.title}
                      onClick={(e) => {
                        if (targetEntry) {
                          // In-card link to a sibling model — swap the
                          // detail pane to that entry instead of navigating
                          // the webview.
                          e.preventDefault();
                          openModelEntry(targetEntry);
                          return;
                        }
                        if (docPath) {
                          // In-app docs link — switch to the Documentation
                          // tab instead of navigating the webview.
                          e.preventDefault();
                          openDocInDocsTab(docPath);
                          return;
                        }
                        if (isExternalUrl(href)) {
                          e.preventDefault();
                          openExternalUrl(href);
                          return;
                        }
                        // Anything else (a relative link we couldn't
                        // resolve, or one rewritten to the card-asset
                        // endpoint) must NOT navigate — letting the webview
                        // follow it unloads the SPA and blanks the screen.
                        // Pure `#anchor` links are harmless, so let those
                        // through.
                        if (rawHref && !rawHref.startsWith('#')) e.preventDefault();
                      }}
                    >
                      {children}
                    </a>
                  );
                },
              }}
            >
              {entryCard}
            </ReactMarkdown>
          </div>
        )}

      </div>
    </article>
  );
}

function ModelHeroBand({ entryName }: { entryName: string }) {
  const [visible, setVisible] = useState(true);
  if (!visible) return null;
  return (
    <div className="relative w-full overflow-hidden">
      <img
        src={`/api/model-catalog/entries/${encodeURIComponent(entryName)}/hero`}
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

function DownloadProgress({ download }: { download: ActiveDownload }) {
  const { t } = useTranslation('models');
  const isStarting =
    download.bytesReadTotal === 0 && download.bytesTotalAcrossModel === 0;
  const statusText = isStarting
    ? t('card.downloadingStarting')
    : download.fileCount > 0
      ? t('card.downloadingFile', {
          index: download.fileIndex,
          count: download.fileCount,
          file: shortenPath(download.currentFile),
        })
      : t('card.downloadingNoCount', {
          file: shortenPath(download.currentFile || '—'),
        });
  return (
    <DownloadProgressBar
      bytesRead={download.bytesReadTotal}
      bytesTotal={download.bytesTotalAcrossModel}
      startedAt={download.startedAt}
      samples={download.samples}
      statusText={statusText}
    />
  );
}

function InstallingIndicator({ step }: { step: PythonInstallStep | null }) {
  const { t } = useTranslation('models');

  if (!step) {
    return (
      <p className="text-muted-foreground flex items-center gap-2 text-xs">
        <Loader2 className="size-3 animate-spin" />
        {t('card.installingHint')}
      </p>
    );
  }

  const stepLabel =
    step.kind === 'uv-download'
      ? t('card.pythonStep.uvDownload')
      : step.kind === 'python-install'
      ? t('card.pythonStep.pythonInstall')
      : t('card.pythonStep.venvInstall');

  const subline = step.detail
    ? t('card.pythonStepWithDetail', { step: stepLabel, detail: step.detail })
    : step.stage
    ? t('card.pythonStepWithStage', { step: stepLabel, stage: step.stage })
    : stepLabel;

  const showBar =
    typeof step.bytesProcessed === 'number'
    && typeof step.totalBytes === 'number'
    && step.totalBytes > 0;
  const percent = showBar
    ? Math.min(100, ((step.bytesProcessed ?? 0) / (step.totalBytes ?? 1)) * 100)
    : 0;

  return (
    <div className="flex flex-col gap-1">
      <p className="text-muted-foreground flex items-center gap-2 text-xs">
        <Loader2 className="size-3 animate-spin" />
        <span className="truncate">{subline}</span>
      </p>
      {showBar && <Progress value={percent} />}
    </div>
  );
}

function DetailActions({
  variantId,
  variantDisplayName,
  placeholder,
  installed,
  downloaded,
  downloading,
  installing,
  partialBytes,
  installStep,
}: {
  variantId: string;
  variantDisplayName: string;
  placeholder: boolean;
  installed: boolean;
  downloaded: boolean;
  downloading: boolean;
  installing: boolean;
  partialBytes: number;
  installStep: PythonInstallStep | null;
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
    return <InstallingIndicator step={installStep} />;
  }

  if (installed) {
    return (
      <div className="flex justify-end">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => void uninstallModel(variantId)}
        >
          <Trash2 />
          {t('card.remove')}
        </Button>
      </div>
    );
  }

  if (downloaded) {
    return (
      <div className="flex justify-end gap-2">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => void uninstallModel(variantId)}
        >
          <Trash2 />
          {t('card.remove')}
        </Button>
        <Button
          variant="default"
          size="sm"
          onClick={() => void installModel(variantId, variantDisplayName)}
        >
          {t('card.install')}
        </Button>
      </div>
    );
  }

  if (partialBytes > 0) {
    return (
      <div className="flex justify-end gap-2">
        <Button
          variant="outline"
          size="sm"
          onClick={() => void restartDownload(variantId, variantDisplayName)}
        >
          <RotateCcw />
          {t('card.restart')}
        </Button>
        <Button
          variant="default"
          size="sm"
          onClick={() => void installModel(variantId, variantDisplayName)}
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
        onClick={() => void installModel(variantId, variantDisplayName)}
      >
        <Download />
        {t('card.download')}
      </Button>
    </div>
  );
}

function formatPartialSize(bytes: number): string {
  const MB = 1024 * 1024;
  const GB = MB * 1024;
  if (bytes >= GB) return `${(bytes / GB).toFixed(1)} GB`;
  if (bytes >= MB) return `${(bytes / MB).toFixed(0)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${bytes} B`;
}

function PreviousVersionsDisclosure({
  variant,
  variantId,
  variantDisplayName,
  activeVersion,
  versionsOnDisk,
  busy,
}: {
  variant: CatalogVariantSnapshot;
  variantId: string;
  variantDisplayName: string;
  activeVersion: string | undefined;
  versionsOnDisk: readonly string[];
  busy: boolean;
}) {
  const { t } = useTranslation('models');
  const [open, setOpen] = useState(false);

  const versions = variant.versions ?? [];
  if (versions.length <= 1) return null;

  const onDiskSet = new Set(versionsOnDisk);
  const Caret = open ? ChevronDown : ChevronRight;

  return (
    <section className="border-border/60 flex flex-col gap-2 border-t pt-3">
      <button
        type="button"
        className="text-foreground hover:text-foreground/80 flex items-center gap-1.5 text-sm font-medium"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
      >
        <Caret className="size-3.5" />
        {t('previousVersions.title')}
      </button>

      {open && (
        <ul className="flex flex-col gap-1.5">
          {versions.map((v) => {
            const versionString = v.version ?? '';
            const isActive = versionString === activeVersion;
            const isOnDisk = onDiskSet.has(versionString);
            const rowState = isActive
              ? 'active'
              : isOnDisk
                ? 'onDisk'
                : 'notOnDisk';
            return (
              <PreviousVersionRow
                key={versionString}
                variantId={variantId}
                variantDisplayName={variantDisplayName}
                versionString={versionString}
                identifiers={v.models ?? []}
                deprecated={!!v.deprecated}
                deprecationReason={v.deprecationReason ?? ''}
                state={rowState}
                busy={busy}
              />
            );
          })}
        </ul>
      )}
    </section>
  );
}

function pinnedDigits(version: string): string {
  return version.replace(/\D/g, '');
}

function PreviousVersionRow({
  variantId,
  variantDisplayName,
  versionString,
  identifiers,
  deprecated,
  deprecationReason,
  state,
  busy,
}: {
  variantId: string;
  variantDisplayName: string;
  versionString: string;
  identifiers: readonly { identifier?: string; pinnedAs?: string }[];
  deprecated: boolean;
  deprecationReason: string;
  state: 'active' | 'onDisk' | 'notOnDisk';
  busy: boolean;
}) {
  const { t } = useTranslation('models');
  const digits = pinnedDigits(versionString);
  const firstIdentifier = identifiers[0]?.identifier ?? '';

  const onInstall = () => {
    void installPinnedVersion(variantId, versionString, variantDisplayName);
  };
  const onActivate = () => {
    void activateVersion(variantId, versionString);
  };
  const onDelete = () => {
    const message = state === 'active'
      ? t('previousVersions.deleteActiveConfirm')
      : t('previousVersions.deleteConfirm', { version: versionString, digits });
    if (!window.confirm(message)) return;
    void deleteVersion(variantId, versionString);
  };

  return (
    <li className="border-border/40 flex flex-col gap-1 rounded-md border px-2.5 py-1.5">
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-sm">
          <span className="font-mono">{versionString}</span>
          {state === 'active' && (
            <Badge>{t('previousVersions.rowState.active')}</Badge>
          )}
          {state === 'onDisk' && (
            <Badge variant="muted">{t('previousVersions.rowState.onDisk')}</Badge>
          )}
          {state === 'notOnDisk' && (
            <Badge variant="outline">{t('previousVersions.rowState.notOnDisk')}</Badge>
          )}
          {deprecated && (
            <Badge variant="outline" title={deprecationReason || undefined}>
              {t('previousVersions.deprecated')}
            </Badge>
          )}
        </div>
        <div className="flex shrink-0 gap-1.5">
          {state === 'notOnDisk' && (
            <Button variant="default" size="sm" onClick={onInstall} disabled={busy}>
              {t('previousVersions.install')}
            </Button>
          )}
          {state === 'onDisk' && (
            <Button variant="default" size="sm" onClick={onActivate} disabled={busy}>
              {t('previousVersions.activate')}
            </Button>
          )}
          {(state === 'onDisk' || state === 'active') && (
            <Button variant="ghost" size="sm" onClick={onDelete} disabled={busy}>
              <Trash2 />
              {t('previousVersions.delete')}
            </Button>
          )}
        </div>
      </div>
      {firstIdentifier && state !== 'active' && (
        <p className="text-muted-foreground font-mono text-xs">
          {t('previousVersions.rowSyntax', { identifier: firstIdentifier, digits })}
        </p>
      )}
      {deprecated && deprecationReason && (
        <p className="text-muted-foreground text-xs">
          {t('previousVersions.deprecationReason', { reason: deprecationReason })}
        </p>
      )}
    </li>
  );
}

function hardwareLabel(
  t: ReturnType<typeof useTranslation<'models'>>['t'],
  preferred: string,
): string {
  if (preferred === 'cpu') return t('card.hardwareCpu');
  if (preferred === 'cuda') return t('card.hardwareCuda');
  if (preferred === 'directml') return t('card.hardwareDirectMl');
  if (preferred === 'coreml') return t('card.hardwareCoreMl');
  if (preferred === 'any') return t('card.hardwareAny');
  return preferred;
}
