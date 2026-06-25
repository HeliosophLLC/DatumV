import { useTranslation } from 'react-i18next';
import { resolveDialog } from '@/state/dialogs';
import { Button } from '@/components/ui/button';

// First-launch prompt offering to install the deferred CUDA runtime
// bundle. Loaded by DialogShell when the URL hash is
// `#/dialog/gpuInstallPrompt?requestId=...&gpuName=...&sizeBytes=...`.
//
// Three exits:
//   - "Install" → installs in background; user can watch progress in
//     Settings → GPU. Dialog closes immediately.
//   - "Later"   → close, no persistence. Re-prompts on next launch.
//   - "Don't ask again" → persist the dismissal, close. No more prompts
//     until the user reverts the settings.gpuInstallPromptDismissed flag.
//
// X-closing the window resolves null, which state/gpu.ts treats the same
// as "Later" — no persistence.

export interface GpuInstallPromptDialogProps {
  requestId: string;
  gpuName: string;
  sizeBytes: number;
}

export function GpuInstallPromptDialog({
  requestId,
  gpuName,
  sizeBytes,
}: GpuInstallPromptDialogProps) {
  const { t } = useTranslation('dialogs');

  function pick(action: 'install' | 'later' | 'never') {
    resolveDialog(requestId, { action });
  }

  return (
    <div className="flex flex-1 flex-col overflow-hidden">
      <header className="flex flex-col gap-1 border-b px-6 py-4">
        <h1 className="text-base font-medium">{t('gpuInstallPrompt.title')}</h1>
      </header>
      <div className="flex-1 overflow-y-auto px-6 py-4 text-sm">
        <p className="mb-3">
          {t('gpuInstallPrompt.bodyDetected', { gpu: gpuName })}
        </p>
        <p className="text-muted-foreground">
          {t('gpuInstallPrompt.bodySize', { size: formatBytes(sizeBytes) })}
        </p>
      </div>
      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        <Button variant="ghost" size="sm" onClick={() => pick('never')}>
          {t('gpuInstallPrompt.never')}
        </Button>
        <Button variant="outline" size="sm" onClick={() => pick('later')}>
          {t('gpuInstallPrompt.later')}
        </Button>
        <Button variant="default" size="sm" onClick={() => pick('install')}>
          {t('gpuInstallPrompt.install')}
        </Button>
      </footer>
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let v = bytes / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(v >= 10 ? 0 : 1)} ${units[i]}`;
}
