import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  AlertTriangle,
  Check,
  Download,
  HardDrive,
  Info,
  ScrollText,
  Sparkles,
} from 'lucide-react';
import { api } from '@/api';
import { resolveDialog, openDialog } from '@/state/dialogs';
import { Button } from '@/components/ui/button';
import type {
  PreFlightBlock,
  PreFlightLicense,
  PreFlightModelRequirement,
  PreFlightReason,
} from '@/state/execution';

// Plan-time pre-flight dialog. Launched by DialogShell when the URL hash
// is `#/dialog/preflightRequired?requestId=...&block=<JSON>`. The block
// payload is the structured PreFlightBlock the server emitted, JSON-
// stringified into the URL by the openDialog plumbing.
//
// Resolves with:
//   - `{ install: true, acceptedLicenseIds: [...] }` →
//       caller fires installs for ModelNotInstalled entries. License
//       acceptance is persisted (POST /licenses/{id}/accept) inside the
//       dialog as the user accepts each one, so by the time Install
//       resolves the server already has the acceptances on record; the
//       returned id list is informational for the caller (logging, etc).
//   - `{ install: false, acceptedLicenseIds: [...] }` → caller just
//       clears the per-tab block; the user's editor text is untouched.
//       Already-persisted acceptances are NOT rolled back — accepting a
//       license is a deliberate user action with downstream value
//       beyond this one query.
//   - `null` (X-close) → caller treats as { install: false }.

export interface PreFlightDialogProps {
  requestId: string;
  // The raw block from the URL, already parsed back to an object.
  block: PreFlightBlock;
  // Catalog entry ids that were already in the
  // downloading / post-download installing phase when the parent
  // window opened this dialog. Snapshot (not live) — the status-bar
  // chip owns live byte/ETA progress; this list only drives the
  // "Downloading…" pill + the "skip re-firing this install" gate.
  inFlightIds: string[];
}

export interface PreFlightDialogResult {
  install: boolean;
  acceptedLicenseIds: string[];
}

export function PreFlightDialog({
  requestId,
  block,
  inFlightIds,
}: PreFlightDialogProps) {
  const { t } = useTranslation('dialogs');

  // Acceptance state is a Set of license ids the user has accepted (or
  // were already accepted server-side when the event fired). Initialised
  // from the wire payload so server-known acceptances don't block the
  // Install button.
  const initialAccepted = useMemo<Set<string>>(() => {
    const s = new Set<string>();
    for (const m of block.models) {
      for (const l of m.licenses) if (l.accepted) s.add(l.id);
    }
    return s;
  }, [block.models]);
  const [acceptedIds, setAcceptedIds] = useState<Set<string>>(initialAccepted);

  const inFlightSet = useMemo(() => new Set(inFlightIds), [inFlightIds]);

  const grouped = useMemo(() => groupModels(block.models), [block.models]);
  // Rows that will actually trigger a fresh install when the user
  // clicks the button — drops in-flight entries so a re-click doesn't
  // 409 against the server's single-flight guard and stamp a spurious
  // error on the model card.
  const pendingInstall = useMemo(
    () =>
      grouped.installable.filter((m) => !inFlightSet.has(m.catalogEntryId)),
    [grouped.installable, inFlightSet],
  );
  const inFlightInstall = useMemo(
    () => grouped.installable.filter((m) => inFlightSet.has(m.catalogEntryId)),
    [grouped.installable, inFlightSet],
  );
  const installCount = pendingInstall.length;
  const totalSizeMb = pendingInstall.reduce(
    (sum, m) => sum + (m.approxSizeMb ?? 0),
    0,
  );

  // True when the dialog has nothing actionable left for the user
  // beyond acknowledging — every installable row is already in flight,
  // no pinned issues to resolve, no typos to fix. The footer button
  // flips to a primary "OK" in this case and Enter dismisses the
  // dialog (instead of being a no-op because installCount === 0).
  const dismissOnly =
    installCount === 0 &&
    inFlightInstall.length > 0 &&
    grouped.pinnedMissing.length === 0 &&
    grouped.pinnedUnknown.length === 0 &&
    block.suggestions.length === 0;

  // Dedupe licenses across all model rows. Only licenses with
  // requiresAcceptance gate the Install button; informational licenses
  // render in the same section but never block.
  const uniqueLicenses = useMemo(
    () => dedupeLicenses(block.models),
    [block.models],
  );
  const gatingIds = useMemo(
    () => uniqueLicenses.filter((l) => l.requiresAcceptance).map((l) => l.id),
    [uniqueLicenses],
  );
  const pendingGateCount =
    gatingIds.length - gatingIds.filter((id) => acceptedIds.has(id)).length;
  const allAccepted = pendingGateCount === 0;
  const installEnabled = installCount > 0 && allAccepted;

  async function onAcceptLicense(licenseId: string, modelDisplayName: string) {
    const { result } = openDialog<{ accepted: boolean }>({
      kind: 'confirmLicense',
      payload: { licenseId, modelDisplayName },
    });
    const decision = await result;
    if (!decision?.accepted) return;
    try {
      await api.modelCatalog.acceptLicense(licenseId);
    } catch (err) {
      console.error('[preflight] acceptLicense failed', licenseId, err);
      return;
    }
    setAcceptedIds((prev) => {
      const next = new Set(prev);
      next.add(licenseId);
      return next;
    });
  }

  function onInstall() {
    const payload: PreFlightDialogResult = {
      install: true,
      acceptedLicenseIds: Array.from(acceptedIds),
    };
    resolveDialog(requestId, payload);
  }
  function onCancel() {
    const payload: PreFlightDialogResult = {
      install: false,
      acceptedLicenseIds: Array.from(acceptedIds),
    };
    resolveDialog(requestId, payload);
  }

  // Keyboard shortcuts:
  //   Esc   → dismiss (same path as the Cancel / OK button).
  //   Enter → fire the install (only when the button would actually be
  //           enabled; otherwise eat the keystroke so it doesn't fall
  //           through to anything else listening).
  // The license sub-dialog runs in its own modal child window with its
  // own focus + keymap, so this listener doesn't compete with it while
  // the user is reading a license.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.defaultPrevented) return;
      const target = e.target as HTMLElement | null;
      // Don't hijack typing in editable surfaces — there aren't any in
      // this dialog today, but cheap insurance for future fields.
      if (
        target &&
        (target.tagName === 'INPUT' ||
          target.tagName === 'TEXTAREA' ||
          target.isContentEditable)
      ) {
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        onCancel();
      } else if (e.key === 'Enter') {
        // Enter on the all-in-flight state dismisses the dialog
        // (matches the primary "OK" button shown in that mode).
        // Otherwise it fires the install — but only when the button
        // would actually be enabled, so license-gated state still
        // blocks the keystroke.
        if (dismissOnly) {
          e.preventDefault();
          onCancel();
          return;
        }
        if (!installEnabled) return;
        e.preventDefault();
        onInstall();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // `installEnabled` / `dismissOnly` drive the Enter branch; the
    // resolve callbacks close over `acceptedIds` via Array.from at
    // call time so re-binding on those changes keeps the payload
    // accurate.
  }, [installEnabled, dismissOnly, acceptedIds, requestId]);

  return (
    <div className="flex flex-1 flex-col overflow-hidden select-none">
      <header className="border-b px-6 py-4">
        <h1 className="text-base font-medium">
          {installCount > 0
            ? t('preflight.title.install', { count: installCount })
            : grouped.installable.length > 0
              ? t('preflight.title.allInFlight')
              : t('preflight.title.typo')}
        </h1>
        {installCount > 0 && totalSizeMb > 0 && (
          <p className="text-muted-foreground mt-1 flex items-center gap-1 text-xs">
            <HardDrive className="size-3" />
            {t('preflight.totalSize', { size: formatSizeMb(totalSizeMb) })}
          </p>
        )}
        {inFlightInstall.length > 0 && (
          <p className="text-muted-foreground mt-1 flex items-center gap-1 text-xs">
            <Download className="size-3" />
            {t('preflight.downloading.summary', {
              count: inFlightInstall.length,
            })}
          </p>
        )}
      </header>

      <div className="flex-1 overflow-y-auto px-6 py-4">
        {uniqueLicenses.length > 0 && (
          <Section title={t('preflight.section.licenses')}>
            {pendingGateCount > 0 && (
              <p className="text-muted-foreground mb-2 text-[0.7rem]">
                {t('preflight.licenses.needsAcceptance', {
                  count: pendingGateCount,
                })}
              </p>
            )}
            {uniqueLicenses.map((l) => (
              <LicenseRow
                key={l.id}
                license={l}
                accepted={acceptedIds.has(l.id)}
                modelDisplayName={firstModelTitleFor(block.models, l.id)}
                onAccept={() =>
                  onAcceptLicense(l.id, firstModelTitleFor(block.models, l.id))
                }
              />
            ))}
          </Section>
        )}

        {grouped.installable.length > 0 && (
          <Section title={t('preflight.section.install')}>
            {grouped.installable.map((m) => (
              <ModelRow
                key={m.typedReference}
                model={m}
                acceptedIds={acceptedIds}
                inFlight={inFlightSet.has(m.catalogEntryId)}
              />
            ))}
          </Section>
        )}

        {grouped.pinnedMissing.length > 0 && (
          <Section title={t('preflight.section.pinned')}>
            {grouped.pinnedMissing.map((m) => (
              <ModelRow
                key={m.typedReference}
                model={m}
                acceptedIds={acceptedIds}
                inFlight={inFlightSet.has(m.catalogEntryId)}
              />
            ))}
          </Section>
        )}

        {grouped.pinnedUnknown.length > 0 && (
          <Section title={t('preflight.section.unknown')}>
            {grouped.pinnedUnknown.map((m) => (
              <ModelRow
                key={m.typedReference}
                model={m}
                acceptedIds={acceptedIds}
                inFlight={inFlightSet.has(m.catalogEntryId)}
              />
            ))}
          </Section>
        )}

        {block.suggestions.length > 0 && (
          <Section title={t('preflight.section.suggestions')}>
            {block.suggestions.map((s) => (
              <div
                key={s.typedName}
                className="bg-muted/40 mb-2 flex items-start gap-2 rounded-md px-3 py-2 text-xs last:mb-0"
              >
                <Sparkles className="text-muted-foreground mt-0.5 size-3.5 shrink-0" />
                <div className="flex-1">
                  <span className="text-muted-foreground line-through">
                    {s.typedName}
                  </span>
                  <span className="text-muted-foreground mx-1">→</span>
                  <span className="text-foreground font-mono">
                    {s.suggestion}
                  </span>
                </div>
              </div>
            ))}
          </Section>
        )}
      </div>

      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        {installCount > 0 && !allAccepted && (
          <span
            className="text-muted-foreground mr-auto text-[0.7rem]"
            role="status"
          >
            {t('preflight.licenses.installBlocked')}
          </span>
        )}
        {installCount === 0 &&
          inFlightInstall.length > 0 &&
          grouped.pinnedMissing.length === 0 &&
          grouped.pinnedUnknown.length === 0 && (
            <span
              className="text-muted-foreground mr-auto text-[0.7rem]"
              role="status"
            >
              {t('preflight.downloading.allInFlightHint')}
            </span>
          )}
        {/* When the only thing left for the user to do is acknowledge
            an in-flight set (nothing to install, nothing to fix), the
            dismiss button stops being a "Cancel" and becomes a primary
            "OK" — it's the sole action and there's no live operation
            to cancel. The resolution payload is still install:false so
            execution.ts's dismissPreFlight path handles it. */}
        <Button
          variant={dismissOnly ? 'default' : 'outline'}
          size="sm"
          onClick={onCancel}
        >
          {dismissOnly ? t('preflight.ok') : t('preflight.cancel')}
        </Button>
        {installCount > 0 && (
          <Button
            variant="default"
            size="sm"
            onClick={onInstall}
            disabled={!installEnabled}
          >
            <Download className="size-3.5" />
            {t('preflight.install', { count: installCount })}
          </Button>
        )}
      </footer>
    </div>
  );
}

function Section({
  title,
  children,
}: {
  title: string;
  children: React.ReactNode;
}) {
  return (
    <section className="mb-4 last:mb-0">
      <h3 className="text-muted-foreground mb-2 text-[0.7rem] font-semibold tracking-wide uppercase">
        {title}
      </h3>
      {children}
    </section>
  );
}

function LicenseRow({
  license,
  accepted,
  modelDisplayName,
  onAccept,
}: {
  license: PreFlightLicense;
  accepted: boolean;
  modelDisplayName: string;
  onAccept: () => void;
}) {
  const { t } = useTranslation('dialogs');
  return (
    <div className="border-border/60 bg-muted/20 mb-2 flex items-center justify-between gap-3 rounded-md border px-3 py-2 last:mb-0">
      <div className="flex min-w-0 flex-1 items-start gap-2">
        <ScrollText className="text-muted-foreground mt-0.5 size-3.5 shrink-0" />
        <div className="min-w-0 flex-1">
          <p className="text-foreground truncate text-xs">{license.title}</p>
          {license.summary && (
            <p className="text-muted-foreground text-[0.7rem]">
              {license.summary}
            </p>
          )}
        </div>
      </div>
      {!license.requiresAcceptance ? (
        <span className="text-muted-foreground shrink-0 text-[0.65rem] uppercase tracking-wide">
          {t('preflight.licenses.informational')}
        </span>
      ) : accepted ? (
        <span className="flex shrink-0 items-center gap-1 rounded-md bg-emerald-500/15 px-2 py-1 text-[0.7rem] text-emerald-700 dark:text-emerald-300">
          <Check className="size-3" />
          {t('preflight.licenses.accepted')}
        </span>
      ) : (
        <Button variant="outline" size="sm" onClick={onAccept}>
          {t('preflight.licenses.review')}
        </Button>
      )}
      <span className="sr-only">{modelDisplayName}</span>
    </div>
  );
}

function ModelRow({
  model,
  acceptedIds,
  inFlight,
}: {
  model: PreFlightModelRequirement;
  acceptedIds: Set<string>;
  // True when the parent window saw this catalog entry mid-download (or
  // in its post-download installSql phase) at the moment the dialog
  // opened. Drives a "Downloading…" pill in place of the size estimate
  // and is what the Install button consults to skip re-firing the same
  // install (which would 409 on the server's single-flight key).
  inFlight: boolean;
}) {
  const { t } = useTranslation('dialogs');
  const siblings = model.siblingIdentifiers.filter((s) => s !== model.identifier);
  return (
    <div className="border-border/60 bg-muted/20 mb-2 rounded-md border px-3 py-2 last:mb-0">
      <div className="flex items-baseline justify-between gap-3">
        <code className="text-foreground font-mono text-xs break-all">
          {model.typedReference}
        </code>
        {inFlight ? (
          <span className="inline-flex shrink-0 items-center gap-1 rounded-sm bg-sky-500/15 px-1.5 py-0.5 text-[0.7rem] text-sky-700 dark:text-sky-300">
            <Download className="size-3 animate-pulse" />
            {t('preflight.downloading.pill')}
          </span>
        ) : (
          model.approxSizeMb !== null &&
          model.reason === 'modelNotInstalled' && (
            <span className="text-muted-foreground shrink-0 text-[0.7rem]">
              ~{formatSizeMb(model.approxSizeMb)}
            </span>
          )
        )}
      </div>
      <div className="text-muted-foreground mt-1 text-[0.7rem]">
        {model.catalogEntryId}
        {model.version && (
          <>
            <span className="mx-1">·</span>
            {t('preflight.versionLabel', { version: model.version })}
          </>
        )}
      </div>
      {model.licenses.length > 0 && (
        <div className="mt-1.5 flex flex-wrap items-center gap-1">
          {model.licenses.map((l) => (
            <LicenseBadge
              key={l.id}
              license={l}
              accepted={acceptedIds.has(l.id)}
            />
          ))}
        </div>
      )}
      {siblings.length > 0 && (
        <p className="text-muted-foreground mt-1.5 text-[0.7rem]">
          {t('preflight.siblings', { list: siblings.join(', ') })}
        </p>
      )}
      {model.entryDeprecated && (
        <Banner
          tone="warning"
          icon={<AlertTriangle className="size-3" />}
          text={
            model.supersededBy
              ? t('preflight.entryDeprecatedSuperseded', {
                  successor: model.supersededBy,
                })
              : t('preflight.entryDeprecated')
          }
        />
      )}
      {model.versionDeprecated && (
        <Banner
          tone="warning"
          icon={<AlertTriangle className="size-3" />}
          text={
            model.versionDeprecationReason
              ? t('preflight.versionDeprecatedReason', {
                  reason: model.versionDeprecationReason,
                })
              : t('preflight.versionDeprecated')
          }
        />
      )}
      {model.reason === 'pinnedVersionNotInstalled' && (
        <Banner
          tone="info"
          icon={<Info className="size-3" />}
          text={t('preflight.pinnedHint')}
        />
      )}
      {model.reason === 'pinnedVersionUnknown' && (
        <Banner
          tone="info"
          icon={<Info className="size-3" />}
          text={t('preflight.unknownPinHint')}
        />
      )}
    </div>
  );
}

// Compact per-license chip on each model row. Tone tracks state:
// accepted (green check), gating-but-unaccepted (amber dot),
// informational (muted). Title doubles as tooltip so cramped titles
// still convey context.
function LicenseBadge({
  license,
  accepted,
}: {
  license: PreFlightLicense;
  accepted: boolean;
}) {
  if (!license.requiresAcceptance) {
    return (
      <span
        className="bg-muted/60 text-muted-foreground inline-flex items-center gap-1 rounded-sm px-1.5 py-0.5 text-[0.65rem]"
        title={license.summary || license.title}
      >
        <ScrollText className="size-2.5" />
        {license.title}
      </span>
    );
  }
  if (accepted) {
    return (
      <span
        className="inline-flex items-center gap-1 rounded-sm bg-emerald-500/15 px-1.5 py-0.5 text-[0.65rem] text-emerald-700 dark:text-emerald-300"
        title={license.summary || license.title}
      >
        <Check className="size-2.5" />
        {license.title}
      </span>
    );
  }
  return (
    <span
      className="inline-flex items-center gap-1 rounded-sm bg-amber-500/15 px-1.5 py-0.5 text-[0.65rem] text-amber-700 dark:text-amber-300"
      title={license.summary || license.title}
    >
      <ScrollText className="size-2.5" />
      {license.title}
    </span>
  );
}

function Banner({
  tone,
  icon,
  text,
}: {
  tone: 'warning' | 'info';
  icon: React.ReactNode;
  text: string;
}) {
  const cls =
    tone === 'warning'
      ? 'text-amber-700 dark:text-amber-300'
      : 'text-muted-foreground';
  return (
    <p className={`mt-1.5 flex items-start gap-1.5 text-[0.7rem] ${cls}`}>
      <span className="mt-0.5">{icon}</span>
      <span>{text}</span>
    </p>
  );
}

interface GroupedModels {
  installable: PreFlightModelRequirement[];
  pinnedMissing: PreFlightModelRequirement[];
  pinnedUnknown: PreFlightModelRequirement[];
}

function groupModels(models: PreFlightModelRequirement[]): GroupedModels {
  const installable: PreFlightModelRequirement[] = [];
  const pinnedMissing: PreFlightModelRequirement[] = [];
  const pinnedUnknown: PreFlightModelRequirement[] = [];
  for (const m of models) {
    const reason: PreFlightReason = m.reason;
    switch (reason) {
      case 'modelNotInstalled':
        installable.push(m);
        break;
      case 'pinnedVersionNotInstalled':
        pinnedMissing.push(m);
        break;
      case 'pinnedVersionUnknown':
        pinnedUnknown.push(m);
        break;
    }
  }
  return { installable, pinnedMissing, pinnedUnknown };
}

// Dedupe by license id, preserving first-seen order (matches the order
// in which models appear, which itself mirrors AST visit order). License
// ids are globally unique in the catalog so keeping the first occurrence
// is safe.
function dedupeLicenses(
  models: PreFlightModelRequirement[],
): PreFlightLicense[] {
  const seen = new Map<string, PreFlightLicense>();
  for (const m of models) {
    for (const l of m.licenses) {
      if (!seen.has(l.id)) seen.set(l.id, l);
    }
  }
  return Array.from(seen.values());
}

// Pick a representative model id for a license — feeds the sub-dialog's
// "Required to download X" header. LicenseDialog already tolerates the
// empty-string fallback (renders no contextual line).
function firstModelTitleFor(
  models: PreFlightModelRequirement[],
  licenseId: string,
): string {
  for (const m of models) {
    for (const l of m.licenses) {
      if (l.id === licenseId) return m.catalogEntryId;
    }
  }
  return '';
}

function formatSizeMb(mb: number): string {
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${Math.round(mb)} MB`;
}
