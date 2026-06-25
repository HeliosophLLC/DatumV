import { useTranslation } from 'react-i18next';
import { resolveDialog } from '@/state/dialogs';
import { Button } from '@/components/ui/button';

// Fired automatically when a CUDA bundle install finishes — the new env
// vars only take effect on the next backend spawn, so we have to ask the
// user to restart. Loaded by DialogShell when the URL hash is
// `#/dialog/gpuRestartPrompt?requestId=...`.
//
// Two exits:
//   - "Restart now" → calls host.restartBackend(); the renderer reloads
//     onto the loader page mid-resolve, so any IPC reply after that is
//     moot. (See the EPIPE fix in electron/main.ts.)
//   - "Later" / X-close → close, no action. User can hit Restart from
//     Settings → GPU whenever they're ready.

export interface GpuRestartPromptDialogProps {
  requestId: string;
}

export function GpuRestartPromptDialog({ requestId }: GpuRestartPromptDialogProps) {
  const { t } = useTranslation('dialogs');

  function pick(action: 'restart' | 'later') {
    resolveDialog(requestId, { action });
  }

  return (
    <div className="flex flex-1 flex-col overflow-hidden">
      <header className="flex flex-col gap-1 border-b px-6 py-4">
        <h1 className="text-base font-medium">{t('gpuRestartPrompt.title')}</h1>
      </header>
      <div className="flex-1 overflow-y-auto px-6 py-4 text-sm">
        <p className="mb-3">{t('gpuRestartPrompt.body')}</p>
        <p className="text-muted-foreground">{t('gpuRestartPrompt.detail')}</p>
      </div>
      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        <Button variant="outline" size="sm" onClick={() => pick('later')}>
          {t('gpuRestartPrompt.later')}
        </Button>
        <Button variant="default" size="sm" onClick={() => pick('restart')}>
          {t('gpuRestartPrompt.restart')}
        </Button>
      </footer>
    </div>
  );
}
