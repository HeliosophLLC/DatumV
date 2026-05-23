import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { resolveDialog } from '@/state/dialogs';

// Delete-confirmation dialog for the Project Explorer's right-click
// Delete action. Opened by ProjectExplorerPanel via openDialog(); the
// caller awaits the result and only invokes deleteFile() on `confirm`.

export interface DeleteFileDialogProps {
  requestId: string;
  // Display name (basename) for the prompt body.
  name: string;
  // Catalog-relative path shown under the title so users see exactly
  // which file is being targeted.
  path: string;
  // True when the target is a directory — the dialog calls out the
  // recursive nature of the delete in that case.
  isDirectory: boolean;
}

export type DeleteFileAction = 'confirm' | 'cancel';

export interface DeleteFileDialogResult {
  action: DeleteFileAction;
}

export function DeleteFileDialog({
  requestId,
  name,
  path,
  isDirectory,
}: DeleteFileDialogProps) {
  const { t } = useTranslation('dialogs');

  function resolve(action: DeleteFileAction) {
    const payload: DeleteFileDialogResult = { action };
    resolveDialog(requestId, payload);
  }

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.defaultPrevented) return;
      if (e.key === 'Escape') {
        e.preventDefault();
        resolve('cancel');
      } else if (e.key === 'Enter') {
        e.preventDefault();
        resolve('confirm');
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [requestId]);

  const messageKey = isDirectory
    ? 'deleteFile.messageDirectory'
    : 'deleteFile.message';
  const detailKey = isDirectory
    ? 'deleteFile.detailDirectory'
    : 'deleteFile.detail';

  return (
    <div className="flex flex-1 flex-col overflow-hidden select-none">
      <header className="border-b px-6 py-4">
        <h1 className="flex items-center gap-2 text-base font-medium">
          <Trash2 className="size-4 text-destructive" />
          {t('deleteFile.title')}
        </h1>
        {path && (
          <p className="text-muted-foreground mt-1 truncate text-xs" title={path}>
            {path}
          </p>
        )}
      </header>

      <div className="flex-1 space-y-3 overflow-y-auto px-6 py-5 text-sm">
        <p>{t(messageKey, { name })}</p>
        <p className="text-muted-foreground">{t(detailKey)}</p>
      </div>

      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        <Button variant="outline" size="sm" onClick={() => resolve('cancel')}>
          {t('deleteFile.cancel')}
        </Button>
        <Button
          variant="destructive"
          size="sm"
          onClick={() => resolve('confirm')}
        >
          {t('deleteFile.confirm')}
        </Button>
      </footer>
    </div>
  );
}
