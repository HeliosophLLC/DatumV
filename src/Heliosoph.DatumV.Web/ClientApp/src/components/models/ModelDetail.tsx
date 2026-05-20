import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { ChevronDown, ChevronRight, CircleCheck, Download, HardDrive, Loader2, RotateCcw, Trash2 } from 'lucide-react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeHighlight from 'rehype-highlight';
import { CodeBlock } from '@/components/markdown/CodeBlock';
import { isExternalUrl, openExternalUrl } from '@/lib/openExternal';
import { isDrifted, loadFamilyCard, modelsState, setSelectedId, type CatalogModelSnapshot } from '@/state/models';
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

// Right-pane content for the Models view. Shows full info + actions for
// one selected model. The list row on the left handles search/selection;
// this component owns the heavy chrome (description, badges, license,
// progress, install/uninstall buttons).

export function ModelDetail({ model }: { model: CatalogModelSnapshot }) {
  const { t } = useTranslation('models');
  const downloads = useSnapshot(downloadsState);
  const models = useSnapshot(modelsState);

  const modelId = model.id ?? '';
  const modelDisplayName = model.displayName ?? modelId;
  const installState = downloads.state?.[modelId];
  const activeDownload = downloads.active[modelId];
  const installing = downloads.installing[modelId] === true;
  const error = downloads.errors[modelId];
  // Drift badge only renders when the entry is installed AND the active
  // on-disk version trails the catalog's newest declared version. Warn-
  // only — clicking Install on the card runs the latest cut (versions[0])
  // and the badge clears once `<id>/active` flips.
  const drifted = isDrifted(model, models.activeVersions);
  const activeVersion = models.activeVersions[modelId];
  const latestVersion = model.versions?.[0]?.version;
  // Name → family lookup so each task badge can pick up the right
  // family-accent left border. Memoised against the (rarely-changing)
  // task vocabulary so we don't rebuild it on every snapshot tick.
  const taskFamilies = useMemo(() => buildTaskFamilyMap(models.tasks), [models.tasks]);
  // Python install sub-step. The venv-install step is model-scoped (keyed
  // by catalog id), so it appears only on the card that triggered it. The
  // uv-download + python-install steps are machine-scoped — surface them
  // alongside whichever model is currently installing, since that's what
  // triggered the host-level work.
  const venvStep = downloads.venvSteps[modelId];
  const hostStep = downloads.pythonHostStep;
  const activeStep: PythonInstallStep | null = venvStep ?? (installing ? hostStep : null) ?? null;

  // Sibling variants — every catalog entry sharing this model's
  // `modelFamily`. Empty when the entry stands alone. Drives the variant
  // picker chips at the top of the card; selecting a sibling swaps the
  // detail pane via `setSelectedId`.
  const modelFamily = model.modelFamily ?? null;
  const siblings = useMemo(() => {
    if (modelFamily === null || models.manifest === null) return [];
    return (models.manifest.models ?? []).filter(
      (m) => m.modelFamily === modelFamily,
    );
  }, [modelFamily, models.manifest]);

  // Family card markdown — fetched once per family, cached in state.
  // `loadFamilyCard` is a no-op when the family already has an entry
  // (cached value, possibly null); we keep a local copy for rendering.
  const [familyCard, setFamilyCard] = useState<string | null>(null);
  useEffect(() => {
    if (modelFamily === null) {
      setFamilyCard(null);
      return;
    }
    let cancelled = false;
    void loadFamilyCard(modelFamily).then((text) => {
      if (!cancelled) setFamilyCard(text);
    });
    return () => {
      cancelled = true;
    };
  }, [modelFamily]);

  return (
    <article className="mx-auto flex w-full max-w-3xl flex-col">
      <ModelHeroBand modelId={modelId} />
      <div className="flex flex-col gap-4 px-6 py-5">
      <header className="flex flex-col gap-2">
        {siblings.length > 1 && (
          <div className="flex flex-col gap-1">
            <span className="text-muted-foreground text-[10px] uppercase tracking-wide">
              {t('card.modelFamily')} · {modelFamily}
            </span>
            <div className="flex flex-wrap gap-1">
              {siblings.map((sib) => {
                const sid = sib.id ?? '';
                const isActive = sid === modelId;
                return (
                  <button
                    key={sid}
                    type="button"
                    onClick={() => sid && setSelectedId(sid)}
                    aria-pressed={isActive}
                    disabled={isActive}
                    className={cn(
                      'rounded-xs px-2 py-0.5 text-xs transition-colors',
                      isActive
                        ? 'bg-primary/15 text-primary cursor-default'
                        : 'text-muted-foreground hover:bg-primary/10 hover:text-primary cursor-pointer',
                    )}
                  >
                    {sib.displayName ?? sib.id}
                  </button>
                );
              })}
            </div>
          </div>
        )}
        {!model.placeholder && (
          <div className="flex items-center justify-between gap-3">
            {/* Install-state indicator. Only renders when the entry is
                actually installed or downloaded — other states leave the
                left side empty and `justify-between` keeps the action
                button anchored on the right. */}
            {installState === 'installed' && !installing ? (
              <div className="flex items-center gap-1 text-xs text-primary">
                <CircleCheck className="size-3.5" />
                <span>{t('card.installed')}</span>
              </div>
            ) : installState === 'downloaded' && !installing ? (
              <div className="flex items-center gap-1 text-xs text-primary">
                <HardDrive className="size-3.5" />
                <span>{t('card.downloaded')}</span>
              </div>
            ) : (
              <span />
            )}
            <DetailActions
              modelId={modelId}
              modelDisplayName={modelDisplayName}
              placeholder={!!model.placeholder}
              installed={installState === 'installed'}
              downloaded={installState === 'downloaded'}
              downloading={!!activeDownload}
              installing={installing}
              partialBytes={downloads.partials[modelId] ?? 0}
              installStep={activeStep}
            />
          </div>
        )}
        <div className="flex items-start justify-between gap-3">
          <h2 className="text-xl font-medium">{modelDisplayName}</h2>
          <div className="flex shrink-0 gap-1.5">
            {model.placeholder && (
              <Badge variant="muted">{t('card.comingSoon')}</Badge>
            )}
            {!model.placeholder && installing && (
              <Badge variant="muted">{t('card.installing')}</Badge>
            )}
            {!model.placeholder && installState === 'partial' && !activeDownload && (
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
            {model.requiresHfLogin && (
              <Badge variant="outline">{t('card.gated')}</Badge>
            )}
          </div>
        </div>
        {model.summary && (
          <p className="text-foreground text-sm leading-relaxed">{model.summary}</p>
        )}
        {model.description && (
          <p className="text-muted-foreground text-xs leading-relaxed">{model.description}</p>
        )}
        <p className="text-muted-foreground text-xs font-mono">{modelId}</p>
      </header>

      <div className="flex flex-wrap gap-1.5">
        {(model.tasks ?? []).map((task) => (
          <TaskChipLabel
            key={task}
            task={task}
            family={taskFamilies.get(task.toLowerCase()) ?? ''}
            label={t(`tasks.${task}` as 'tasks.TextEmbedder', { defaultValue: task })}
          />
        ))}
        {typeof model.approxSizeMb === 'number' && (
          <Badge variant="outline">
            {t('card.size', { size: model.approxSizeMb })}
          </Badge>
        )}
        {model.hardware?.preferred && (
          <Badge variant="outline">{hardwareLabel(t, model.hardware.preferred)}</Badge>
        )}
        {(model.licenseIds ?? []).map((id) => (
          <Badge key={id} variant="outline">
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

      {familyCard && (
        <div className="markdown-body">
          <ReactMarkdown
            remarkPlugins={[remarkGfm]}
            rehypePlugins={[[rehypeHighlight, { detect: true }]]}
            // Rewrite relative URLs to the family-card-asset endpoint so
            // `![alt](yolox/street-detections.png)` resolves to the file
            // served from `models/cards/yolox/street-detections.png`.
            // Absolute URLs / data: / mailto: / anchors / SPA-routes
            // pass through unchanged.
            urlTransform={(url) => {
              if (modelFamily === null) return url;
              if (/^https?:|^data:|^mailto:|^#|^\//i.test(url)) return url;
              const clean = url.replace(/^\.\//, '');
              return `/api/model-catalog/family-cards/${encodeURIComponent(modelFamily)}/assets/${clean}`;
            }}
            components={{
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
            {familyCard}
          </ReactMarkdown>
        </div>
      )}

      <PreviousVersionsDisclosure
        model={model}
        modelId={modelId}
        modelDisplayName={modelDisplayName}
        activeVersion={activeVersion}
        versionsOnDisk={models.versionsOnDisk[modelId] ?? []}
        busy={!!activeDownload || installing}
      />
      </div>
    </article>
  );
}

// Hero band at the top of the model detail card. Matches the dataset
// side's HeroBand: height-clamped, centered, with a bottom gradient
// fade into the page background. Hides on 404 / load error so entries
// without a declared HeroImageFile (or with a missing file on disk)
// just don't render a band.
function ModelHeroBand({ modelId }: { modelId: string }) {
  const [visible, setVisible] = useState(true);
  if (!visible) return null;
  return (
    <div className="relative w-full overflow-hidden">
      <img
        src={`/api/model-catalog/models/${encodeURIComponent(modelId)}/hero`}
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
  const statusText = download.fileCount > 0
    ? t('card.downloadingFile', {
        index: download.fileIndex,
        count: download.fileCount,
        file: shortenPath(download.currentFile),
      })
    : t('card.downloadingStarting');
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

  // No Python sub-step: just the generic SQL-install spinner. Either
  // we're past the venv stage (catalog INSTALL SQL is running) or the
  // catalog entry is an ONNX model that has no venv work at all.
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

  // Prefer the most specific text we have: detail (wheel name, version)
  // over stage label ("downloading", "extracting"). Both are free-form
  // from the install backend.
  const subline = step.detail
    ? t('card.pythonStepWithDetail', { step: stepLabel, detail: step.detail })
    : step.stage
    ? t('card.pythonStepWithStage', { step: stepLabel, stage: step.stage })
    : stepLabel;

  // Determinate progress when the backend reports totals (uv download),
  // indeterminate otherwise (venv install — uv pip doesn't expose
  // per-package byte totals).
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
  modelId,
  modelDisplayName,
  placeholder,
  installed,
  downloaded,
  downloading,
  installing,
  partialBytes,
  installStep,
}: {
  modelId: string;
  modelDisplayName: string;
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
  // has the bytes but needs to re-register the model. Holds for both
  // SQL-defined models (re-runs installSql) and built-in IModel entries
  // (re-runs the catalog-driven registrar) — either way the bytes-only
  // state needs an "Install" affordance, not "Download".
  if (downloaded) {
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

// Bytes/sec, formatDuration, and shortenPath have moved to
// `@/lib/formatDownload` so the shared <DownloadProgressBar> can reuse
// them — this file imports `shortenPath` above for the status-line
// label. `formatPartialSize` stays local since it's only used by the
// model card's resume affordance below.

function formatPartialSize(bytes: number): string {
  const MB = 1024 * 1024;
  const GB = MB * 1024;
  if (bytes >= GB) return `${(bytes / GB).toFixed(1)} GB`;
  if (bytes >= MB) return `${(bytes / MB).toFixed(0)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${bytes} B`;
}

function PreviousVersionsDisclosure({
  model,
  modelId,
  modelDisplayName,
  activeVersion,
  versionsOnDisk,
  busy,
}: {
  model: CatalogModelSnapshot;
  modelId: string;
  modelDisplayName: string;
  activeVersion: string | undefined;
  versionsOnDisk: readonly string[];
  busy: boolean;
}) {
  const { t } = useTranslation('models');
  const [open, setOpen] = useState(false);

  const versions = model.versions ?? [];
  // Hide entirely when there are no previous versions to act on. v1
  // catalog entries only have versions[0] today; a 1-version entry has
  // nothing for this disclosure to show beyond the active version
  // itself (which is already actionable via the top-level Remove
  // button), so we keep the card uncluttered.
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
                modelId={modelId}
                modelDisplayName={modelDisplayName}
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

// Strip non-digit characters from a version string to derive the
// suffix used by pinned identifiers ("2026-05-29" → "20260529"). Mirrors
// CatalogVersionModel.EffectivePinnedAs on the engine side so the
// hint text matches what the parser actually accepts at query time.
function pinnedDigits(version: string): string {
  return version.replace(/\D/g, '');
}

function PreviousVersionRow({
  modelId,
  modelDisplayName,
  versionString,
  identifiers,
  deprecated,
  deprecationReason,
  state,
  busy,
}: {
  modelId: string;
  modelDisplayName: string;
  versionString: string;
  identifiers: readonly { identifier?: string; pinnedAs?: string }[];
  deprecated: boolean;
  deprecationReason: string;
  state: 'active' | 'onDisk' | 'notOnDisk';
  busy: boolean;
}) {
  const { t } = useTranslation('models');
  const digits = pinnedDigits(versionString);
  // First identifier is representative for the "callable as" hint —
  // most catalog entries declare one identifier per version, and when
  // they declare several they share the version-suffix convention so
  // showing one example reads cleaner than a full list.
  const firstIdentifier = identifiers[0]?.identifier ?? '';

  const onInstall = () => {
    void installPinnedVersion(modelId, versionString, modelDisplayName);
  };
  const onActivate = () => {
    void activateVersion(modelId, versionString);
  };
  const onDelete = () => {
    const message = state === 'active'
      ? t('previousVersions.deleteActiveConfirm')
      : t('previousVersions.deleteConfirm', { version: versionString, digits });
    // window.confirm is a stopgap — fine for v1 since the action is
    // recoverable (re-install). Promoting to a typed Electron dialog
    // belongs with the rest of the catalog dialogs if the UX needs it.
    if (!window.confirm(message)) return;
    void deleteVersion(modelId, versionString);
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
