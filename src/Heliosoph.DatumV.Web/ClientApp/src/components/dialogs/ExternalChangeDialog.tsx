import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { resolveDialog } from '@/state/dialogs';

// External-change confirmation. Loaded by DialogShell when the URL hash
// is `#/dialog/externalChange?requestId=...&fileName=...&filePath=...&isDirty=...`.
//
// Fires when the catalog directory watcher reports that a file backing
// an open SQL tab was modified outside the editor (VS Code save, git
// checkout, hand-edit). Style + framing follow Notepad++: prompt rather
// than silently replace; surface a stronger warning when the user has
// unsaved changes that would be lost by reloading.
//
// Resolves with:
//   - `{ action: 'reload' }` — caller refetches from disk and replaces
//     the tab's editor buffer. Baseline mtime advances.
//   - `{ action: 'keep' }` — caller leaves the buffer alone but still
//     advances the baseline mtime so we don't re-prompt for the same
//     change (matches Notepad++ "Keep current document"). If the tab
//     was dirty, the user's edits are preserved verbatim.
//   - `null` (X-close) — caller treats as `keep`. Defaulting to the
//     non-destructive choice avoids losing work on accidental dismiss.

export interface ExternalChangeDialogProps {
  requestId: string;
  fileName: string;
  filePath: string;
  isDirty: boolean;
}

export interface ExternalChangeDialogResult {
  action: 'reload' | 'keep';
}

export function ExternalChangeDialog({
  requestId,
  fileName,
  filePath,
  isDirty,
}: ExternalChangeDialogProps) {
  const { t } = useTranslation('dialogs');

  function resolve(action: 'reload' | 'keep') {
    const payload: ExternalChangeDialogResult = { action };
    resolveDialog(requestId, payload);
  }

  // Keyboard: Esc keeps the current buffer (non-destructive default);
  // Enter triggers the dialog's primary action (Reload when clean, Keep
  // when dirty — destructive Reload requires an explicit click).
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.defaultPrevented) return;
      if (e.key === 'Escape') {
        e.preventDefault();
        resolve('keep');
      } else if (e.key === 'Enter') {
        e.preventDefault();
        resolve(isDirty ? 'keep' : 'reload');
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isDirty, requestId]);

  return (
    <div className="flex flex-1 flex-col overflow-hidden select-none">
      <header className="border-b px-6 py-4">
        <h1 className="flex items-center gap-2 text-base font-medium">
          <AlertTriangle className="size-4 text-amber-500" />
          {t('externalChange.title')}
        </h1>
        <p className="text-muted-foreground mt-1 truncate text-xs" title={filePath}>
          {filePath}
        </p>
      </header>

      <div className="flex-1 space-y-3 overflow-y-auto px-6 py-5 text-sm">
        <p>{t('externalChange.message', { name: fileName })}</p>
        {isDirty ? (
          <p className="text-amber-700 dark:text-amber-300">
            {t('externalChange.detailDirty')}
          </p>
        ) : (
          <p className="text-muted-foreground">
            {t('externalChange.detailClean')}
          </p>
        )}
      </div>

      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        {/* The right-most button is the Enter default. Clean tab →
            Reload is the safe and obvious choice (buffer == disk, just
            resyncing); promote it to default-right. Dirty tab → Reload
            destroys unsaved edits; demote it to destructive-left and
            promote Keep to default-right so an accidental Enter doesn't
            nuke the user's work. */}
        {isDirty ? (
          <>
            <Button
              variant="destructive"
              size="sm"
              onClick={() => resolve('reload')}
            >
              {t('externalChange.reload')}
            </Button>
            <Button variant="default" size="sm" onClick={() => resolve('keep')}>
              {t('externalChange.keep')}
            </Button>
          </>
        ) : (
          <>
            <Button variant="outline" size="sm" onClick={() => resolve('keep')}>
              {t('externalChange.keep')}
            </Button>
            <Button
              variant="default"
              size="sm"
              onClick={() => resolve('reload')}
            >
              {t('externalChange.reload')}
            </Button>
          </>
        )}
      </footer>
    </div>
  );
}
