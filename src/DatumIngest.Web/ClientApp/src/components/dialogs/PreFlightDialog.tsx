import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, Download, HardDrive, Info, Sparkles } from 'lucide-react';
import { resolveDialog } from '@/state/dialogs';
import { Button } from '@/components/ui/button';
import type {
  PreFlightBlock,
  PreFlightModelRequirement,
  PreFlightReason,
} from '@/state/execution';

// Plan-time pre-flight dialog. Launched by DialogShell when the URL hash
// is `#/dialog/preflightRequired?requestId=...&block=<JSON>`. The block
// payload is the structured PreFlightBlock the server emitted, JSON-
// stringified into the URL by the openDialog plumbing.
//
// Resolves with:
//   - `{ install: true }`  → caller fires installs for ModelNotInstalled
//                            entries and clears the per-tab block.
//   - `{ install: false }` → caller just clears the per-tab block; the
//                            user's editor text is untouched.
//   - `null` (X-close)     → caller treats as { install: false }.
//
// The dialog itself doesn't initiate downloads — those have to run in
// the parent window where downloadsState + the SignalR hub live. Keep
// this component a pure renderer over the payload so the kick-off
// stays single-sourced in execution.ts.

export interface PreFlightDialogProps {
  requestId: string;
  // The raw block from the URL, already parsed back to an object.
  block: PreFlightBlock;
}

export function PreFlightDialog({ requestId, block }: PreFlightDialogProps) {
  const { t } = useTranslation('dialogs');

  const grouped = useMemo(() => groupModels(block.models), [block.models]);
  const installCount = grouped.installable.length;
  const totalSizeMb = grouped.installable.reduce(
    (sum, m) => sum + (m.approxSizeMb ?? 0),
    0,
  );

  function onInstall() {
    resolveDialog(requestId, { install: true });
  }
  function onCancel() {
    resolveDialog(requestId, { install: false });
  }

  return (
    <div className="flex flex-1 flex-col overflow-hidden">
      <header className="border-b px-6 py-4">
        <h1 className="text-base font-medium">
          {installCount > 0
            ? t('preflight.title.install', { count: installCount })
            : t('preflight.title.typo')}
        </h1>
        {installCount > 0 && totalSizeMb > 0 && (
          <p className="text-muted-foreground mt-1 flex items-center gap-1 text-xs">
            <HardDrive className="size-3" />
            {t('preflight.totalSize', { size: formatSizeMb(totalSizeMb) })}
          </p>
        )}
      </header>

      <div className="flex-1 overflow-y-auto px-6 py-4">
        {grouped.installable.length > 0 && (
          <Section title={t('preflight.section.install')}>
            {grouped.installable.map((m) => (
              <ModelRow key={m.typedReference} model={m} />
            ))}
          </Section>
        )}

        {grouped.pinnedMissing.length > 0 && (
          <Section title={t('preflight.section.pinned')}>
            {grouped.pinnedMissing.map((m) => (
              <ModelRow key={m.typedReference} model={m} />
            ))}
          </Section>
        )}

        {grouped.pinnedUnknown.length > 0 && (
          <Section title={t('preflight.section.unknown')}>
            {grouped.pinnedUnknown.map((m) => (
              <ModelRow key={m.typedReference} model={m} />
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
                  <span className="text-foreground font-mono">{s.suggestion}</span>
                </div>
              </div>
            ))}
          </Section>
        )}
      </div>

      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        <Button variant="outline" size="sm" onClick={onCancel}>
          {t('preflight.cancel')}
        </Button>
        {installCount > 0 && (
          <Button variant="default" size="sm" onClick={onInstall}>
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

function ModelRow({ model }: { model: PreFlightModelRequirement }) {
  const { t } = useTranslation('dialogs');
  const siblings = model.siblingIdentifiers.filter((s) => s !== model.identifier);
  return (
    <div className="border-border/60 bg-muted/20 mb-2 rounded-md border px-3 py-2 last:mb-0">
      <div className="flex items-baseline justify-between gap-3">
        <code className="text-foreground font-mono text-xs break-all">
          {model.typedReference}
        </code>
        {model.approxSizeMb !== null && model.reason === 'modelNotInstalled' && (
          <span className="text-muted-foreground shrink-0 text-[0.7rem]">
            ~{formatSizeMb(model.approxSizeMb)}
          </span>
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

function formatSizeMb(mb: number): string {
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${Math.round(mb)} MB`;
}
