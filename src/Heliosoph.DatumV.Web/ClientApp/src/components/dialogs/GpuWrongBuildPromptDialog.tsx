import { useTranslation } from 'react-i18next';
import { resolveDialog } from '@/state/dialogs';
import { Button } from '@/components/ui/button';

// First-launch prompt shown on the cuda variant when CUDA can't be used
// on this machine — either no NVIDIA driver is installed at all, or the
// detected NVIDIA GPU's compute capability is below the 7.0 floor
// (Kepler / Maxwell / Pascal). In both cases the cuda build will fall
// back to CPU; the standard build would provide DirectML (Windows) or
// Vulkan (Linux) GPU acceleration for the same hardware.
//
// Loaded by DialogShell when the URL hash is
// `#/dialog/gpuWrongBuildPrompt?requestId=...&reason=...&gpuName=...&cc=...`.
//
// Three exits:
//   - "Open downloads" → opens the GitHub Releases page in the user's
//     default browser; closes the dialog. Does not persist a dismissal
//     (the user might want to see the prompt again next launch as a
//     reminder until they actually install the right build).
//   - "Later"           → close, no persistence.
//   - "Don't ask again" → persist, close.

export type GpuWrongBuildReason = 'noDriver' | 'incompatibleArch';

export interface GpuWrongBuildPromptDialogProps {
  requestId: string;
  reason: GpuWrongBuildReason;
  gpuName: string | null;
  computeCapability: string | null;
}

const DOWNLOADS_URL = 'https://github.com/HeliosophLLC/DatumV/releases/latest';

export function GpuWrongBuildPromptDialog({
  requestId,
  reason,
  gpuName,
  computeCapability,
}: GpuWrongBuildPromptDialogProps) {
  const { t } = useTranslation('dialogs');

  function pick(action: 'open' | 'later' | 'never') {
    if (action === 'open') {
      void window.electronHost?.openExternal(DOWNLOADS_URL);
    }
    resolveDialog(requestId, { action });
  }

  const detailKey =
    reason === 'incompatibleArch'
      ? 'gpuWrongBuildPrompt.detailIncompatibleArch'
      : 'gpuWrongBuildPrompt.detailNoDriver';

  return (
    <div className="flex flex-1 flex-col overflow-hidden">
      <header className="flex flex-col gap-1 border-b px-6 py-4">
        <h1 className="text-base font-medium">{t('gpuWrongBuildPrompt.title')}</h1>
      </header>
      <div className="flex-1 overflow-y-auto px-6 py-4 text-sm">
        <p className="mb-3">
          {t(detailKey, {
            gpu: gpuName ?? 'NVIDIA GPU',
            cc: computeCapability ?? '?',
          })}
        </p>
        <p className="text-muted-foreground">{t('gpuWrongBuildPrompt.recommendation')}</p>
      </div>
      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        <Button variant="ghost" size="sm" onClick={() => pick('never')}>
          {t('gpuWrongBuildPrompt.never')}
        </Button>
        <Button variant="outline" size="sm" onClick={() => pick('later')}>
          {t('gpuWrongBuildPrompt.later')}
        </Button>
        <Button variant="default" size="sm" onClick={() => pick('open')}>
          {t('gpuWrongBuildPrompt.open')}
        </Button>
      </footer>
    </div>
  );
}
