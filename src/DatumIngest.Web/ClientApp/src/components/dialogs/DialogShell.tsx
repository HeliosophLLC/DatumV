import { useEffect, useMemo } from 'react';
import { WindowChrome } from '@/components/window/WindowChrome';
import { LicenseDialog } from './LicenseDialog';
import { resolveDialog } from '@/state/dialogs';

// Mount root for dialog windows. main.tsx renders <DialogShell /> instead
// of <App /> when window.location.hash starts with '#/dialog/'. The shell
// reads the hash to pick which dialog component to render and reads
// query-string args off the same URL.
//
// URL shape: #/dialog/{kind}?requestId=...&...payload-keys-as-query
//
// Each dialog component receives (requestId, params) where params is the
// flat record of query keys minus requestId. The component's job is to
// render the UI and eventually call resolveDialog(requestId, result).

interface ParsedHash {
  kind: string;
  requestId: string;
  params: Record<string, string>;
}

function parseDialogHash(hash: string): ParsedHash | null {
  // hash example: #/dialog/confirmLicense?requestId=...&licenseId=...
  if (!hash.startsWith('#/dialog/')) return null;
  const afterPrefix = hash.slice('#/dialog/'.length);
  const qIdx = afterPrefix.indexOf('?');
  const kind = qIdx < 0 ? afterPrefix : afterPrefix.slice(0, qIdx);
  const queryString = qIdx < 0 ? '' : afterPrefix.slice(qIdx + 1);
  const usp = new URLSearchParams(queryString);
  const requestId = usp.get('requestId') ?? '';
  const params: Record<string, string> = {};
  for (const [k, v] of usp.entries()) {
    if (k === 'requestId') continue;
    params[k] = v;
  }
  return { kind: decodeURIComponent(kind), requestId, params };
}

export function DialogShell() {
  const parsed = useMemo(() => parseDialogHash(window.location.hash), []);

  useEffect(() => {
    if (!parsed) return;
    document.title = `DatumIngest — ${parsed.kind}`;
  }, [parsed]);

  if (!parsed || !parsed.requestId) {
    return (
      <WindowChrome>
        <div className="text-muted-foreground flex flex-1 items-center justify-center p-8 text-sm">
          Unknown dialog (no requestId in URL).
        </div>
      </WindowChrome>
    );
  }

  const child = renderDialogBody(parsed);
  return <WindowChrome>{child}</WindowChrome>;
}

function renderDialogBody({ kind, requestId, params }: ParsedHash): React.ReactNode {
  switch (kind) {
    case 'confirmLicense':
      return (
        <LicenseDialog
          requestId={requestId}
          licenseId={params.licenseId ?? ''}
          modelDisplayName={params.modelDisplayName}
        />
      );
    default:
      return (
        <div className="flex flex-1 flex-col items-center justify-center gap-2 p-8 text-sm">
          <p className="text-foreground font-medium">Unknown dialog kind: {kind}</p>
          <button
            type="button"
            className="text-primary text-xs underline"
            onClick={() => resolveDialog(requestId, null)}
          >
            close
          </button>
        </div>
      );
  }
}
