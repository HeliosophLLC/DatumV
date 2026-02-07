import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Loader2 } from 'lucide-react';
import { api } from '@/api';
import { resolveDialog } from '@/state/dialogs';
import { Button } from '@/components/ui/button';
import type { CatalogManifest, CatalogLicense } from '@/api/generated/openapi-client';

// License-acceptance dialog. Loaded by DialogShell when the URL hash is
// `#/dialog/confirmLicense?requestId=...&licenseId=...&modelDisplayName=...`.
//
// Fetches:
//   - The license metadata (title + summary + requiresAcceptance) by
//     pulling the manifest. Cheap — manifest is small and cached server-
//     side.
//   - The license text via GET /api/model-catalog/licenses/{id}/text.
//     Falls back to the summary if the text isn't available.
//
// Resolves the dialog with:
//   - `{ accepted: true }` on Accept
//   - `{ accepted: false }` on Decline
//   - `null` (synthesised by the coordinator) on window X-close
//
// The caller (downloads state) treats `null` and `{accepted:false}` the
// same — abort the install. Differentiating them is a future polish.

export interface LicenseDialogProps {
  requestId: string;
  licenseId: string;
  modelDisplayName?: string;
}

interface LoadedLicense {
  meta: CatalogLicense;
  text: string;
}

export function LicenseDialog({ requestId, licenseId, modelDisplayName }: LicenseDialogProps) {
  const { t } = useTranslation('dialogs');
  const [loaded, setLoaded] = useState<LoadedLicense | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [manifest, text] = await Promise.all([
          api.modelCatalog.getManifest(),
          fetchLicenseText(licenseId),
        ]);
        if (cancelled) return;
        const meta = pickLicense(manifest, licenseId);
        if (!meta) {
          setError(t('license.unknownLicense', { id: licenseId }));
          return;
        }
        setLoaded({ meta, text });
      } catch (err) {
        if (cancelled) return;
        setError(err instanceof Error ? err.message : String(err));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [licenseId, t]);

  function onAccept() {
    resolveDialog(requestId, { accepted: true });
  }
  function onDecline() {
    resolveDialog(requestId, { accepted: false });
  }

  if (error) {
    return (
      <div className="flex flex-1 flex-col gap-4 p-6">
        <h1 className="text-base font-medium">{t('license.title')}</h1>
        <p className="text-destructive text-sm" role="alert">
          {error}
        </p>
        <div className="mt-auto flex justify-end">
          <Button variant="outline" size="sm" onClick={onDecline}>
            {t('license.close')}
          </Button>
        </div>
      </div>
    );
  }

  if (!loaded) {
    return (
      <div className="text-muted-foreground flex flex-1 items-center justify-center gap-2 text-sm">
        <Loader2 className="size-4 animate-spin" />
        {t('license.loading')}
      </div>
    );
  }

  return (
    <div className="flex flex-1 flex-col overflow-hidden">
      <header className="flex flex-col gap-1 border-b px-6 py-4">
        <h1 className="text-base font-medium">{loaded.meta.title}</h1>
        {modelDisplayName && (
          <p className="text-muted-foreground text-xs">
            {t('license.contextFor', { model: modelDisplayName })}
          </p>
        )}
        <p className="text-muted-foreground mt-2 text-xs">{loaded.meta.summary}</p>
      </header>
      <div className="flex-1 overflow-y-auto px-6 py-4">
        <pre className="text-foreground/90 whitespace-pre-wrap font-mono text-xs leading-relaxed">
          {loaded.text}
        </pre>
      </div>
      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        <Button variant="outline" size="sm" onClick={onDecline}>
          {t('license.decline')}
        </Button>
        <Button variant="default" size="sm" onClick={onAccept}>
          {t('license.accept')}
        </Button>
      </footer>
    </div>
  );
}

function pickLicense(manifest: CatalogManifest, id: string): CatalogLicense | null {
  // Licenses dictionary is keyed by id; manifest comes back from NSwag
  // as a possibly-undefined object. Be defensive.
  const dict = manifest.licenses;
  if (!dict) return null;
  return (dict as Record<string, CatalogLicense | undefined>)[id] ?? null;
}

async function fetchLicenseText(id: string): Promise<string> {
  // The generated client wraps this endpoint as a typed download; for
  // a text response we fetch directly to avoid the file-response
  // ceremony.
  const response = await window.fetch(
    `/api/model-catalog/licenses/${encodeURIComponent(id)}/text`,
    { credentials: 'include' },
  );
  if (!response.ok) {
    throw new Error(`License text fetch failed: ${response.status}`);
  }
  return await response.text();
}
