import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { FileWarning } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { resolveDialog } from '@/state/dialogs';

// Unsaved-changes confirmation. Loaded by DialogShell when the URL
// hash is `#/dialog/unsavedChanges?requestId=...&fileName=...&filePath=...`.
//
// Fires from `requestCloseTab` when the user closes a SQL tab with
// `dirty === true`. Three outcomes mirror the canonical save/close
// confirm — Save writes through, Don't Save discards, Cancel aborts.

export interface UnsavedChangesDialogProps {
  requestId: string;
  fileName: string;
  // Catalog-relative path when the tab is backed by a file; empty
  // string for scratch (Untitled) tabs.
  filePath: string;
}

export type UnsavedChangesAction = 'save' | 'dontSave' | 'cancel';

export interface UnsavedChangesDialogResult {
  action: UnsavedChangesAction;
}

export function UnsavedChangesDialog({
  requestId,
  fileName,
  filePath,
}: UnsavedChangesDialogProps) {
  const { t } = useTranslation('dialogs');

  function resolve(action: UnsavedChangesAction) {
    const payload: UnsavedChangesDialogResult = { action };
    resolveDialog(requestId, payload);
  }

  // Esc → Cancel (the standard non-destructive dismiss); Enter → Save
  // (the safe primary action that preserves the user's work).
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.defaultPrevented) return;
      if (e.key === 'Escape') {
        e.preventDefault();
        resolve('cancel');
      } else if (e.key === 'Enter') {
        e.preventDefault();
        resolve('save');
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [requestId]);

  return (
    <div className="flex flex-1 flex-col overflow-hidden select-none">
      <header className="border-b px-6 py-4">
        <h1 className="flex items-center gap-2 text-base font-medium">
          <FileWarning className="size-4 text-amber-500" />
          {t('unsavedChanges.title')}
        </h1>
        {filePath && (
          <p className="text-muted-foreground mt-1 truncate text-xs" title={filePath}>
            {filePath}
          </p>
        )}
      </header>

      <div className="flex-1 space-y-3 overflow-y-auto px-6 py-5 text-sm">
        <p>{t('unsavedChanges.message', { name: fileName })}</p>
        <p className="text-muted-foreground">{t('unsavedChanges.detail')}</p>
      </div>

      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        {/* Don't Save is destructive (loses user edits) → left, styled
            destructive. Cancel is the non-destructive escape hatch →
            middle. Save is the primary action and Enter default → right. */}
        <Button
          variant="destructive"
          size="sm"
          onClick={() => resolve('dontSave')}
        >
          {t('unsavedChanges.dontSave')}
        </Button>
        <Button variant="outline" size="sm" onClick={() => resolve('cancel')}>
          {t('unsavedChanges.cancel')}
        </Button>
        <Button variant="default" size="sm" onClick={() => resolve('save')}>
          {t('unsavedChanges.save')}
        </Button>
      </footer>
    </div>
  );
}
