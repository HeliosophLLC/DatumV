import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { resolveDialog } from '@/state/dialogs';

// "Help → About" dialog. Loaded by DialogShell when the URL hash is
// `#/dialog/about?requestId=...`. Read-only: there's no Accept /
// Decline outcome, just a Close button that resolves the dialog
// with null.
//
// Version info comes from the synchronously-resolved `versions`
// block on electronHost (captured at preload time — app version
// via sendSync to main, electron/chrome/node from process.versions).

export interface AboutDialogProps {
  requestId: string;
}

export function AboutDialog({ requestId }: AboutDialogProps) {
  const { t } = useTranslation('dialogs');
  const versions = window.electronHost.versions;
  const year = new Date().getFullYear();

  function onClose() {
    resolveDialog(requestId, null);
  }

  return (
    <div className="flex flex-1 flex-col overflow-hidden">
      <header className="flex flex-col gap-1 border-b px-6 py-5">
        <h1 className="text-lg font-semibold">{t('about.tagline')}</h1>
        <p className="text-muted-foreground text-xs">
          {t('about.version', { version: versions.app })}
        </p>
      </header>

      <div className="flex-1 space-y-5 overflow-y-auto px-6 py-5 text-sm">
        <section>
          <h2 className="text-muted-foreground mb-2 text-xs font-medium uppercase tracking-wide">
            {t('about.versions.heading')}
          </h2>
          <dl className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-1 font-mono text-xs">
            <dt className="text-muted-foreground">{t('about.versions.electron')}</dt>
            <dd>{versions.electron}</dd>
            <dt className="text-muted-foreground">{t('about.versions.chrome')}</dt>
            <dd>{versions.chrome}</dd>
            <dt className="text-muted-foreground">{t('about.versions.node')}</dt>
            <dd>{versions.node}</dd>
          </dl>
        </section>

        <section>
          <h2 className="text-muted-foreground mb-2 text-xs font-medium uppercase tracking-wide">
            {t('about.license.heading')}
          </h2>
          <p className="text-foreground/90 mb-3 text-xs">
            {t('about.copyright', { year })}
          </p>
          {/* whitespace-pre-line preserves the paragraph breaks in the
              localized MIT text without needing a markdown renderer. */}
          <p className="text-foreground/80 whitespace-pre-line text-xs leading-relaxed">
            {t('about.license.mit')}
          </p>
        </section>
      </div>

      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        <Button variant="default" size="sm" onClick={onClose}>
          {t('about.close')}
        </Button>
      </footer>
    </div>
  );
}
