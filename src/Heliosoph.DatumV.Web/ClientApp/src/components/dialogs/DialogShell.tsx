import { useEffect, useMemo } from 'react';
import { WindowChrome } from '@/components/window/WindowChrome';
import { AboutDialog } from './AboutDialog';
import { DeleteFileDialog } from './DeleteFileDialog';
import { ExternalChangeDialog } from './ExternalChangeDialog';
import { LicenseDialog } from './LicenseDialog';
import { PreFlightDialog } from './PreFlightDialog';
import { UnsavedChangesDialog } from './UnsavedChangesDialog';
import { resolveDialog } from '@/state/dialogs';
import { refreshSettings } from '@/state/settings';
import type { PreFlightBlock } from '@/state/execution';

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

function parsePreFlightBlock(raw: string | undefined): PreFlightBlock | null {
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as PreFlightBlock;
    if (
      !parsed ||
      typeof parsed.message !== 'string' ||
      !Array.isArray(parsed.models) ||
      !Array.isArray(parsed.suggestions)
    ) {
      return null;
    }
    return parsed;
  } catch {
    return null;
  }
}

function parseStringArray(raw: string | undefined): string[] {
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    if (Array.isArray(parsed) && parsed.every((s) => typeof s === 'string')) {
      return parsed;
    }
  } catch {
    /* fall through */
  }
  return [];
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

  // Dialog windows mount their own React tree, so they need their own
  // settings refresh — without it, settingsState stays at the proxy
  // defaults and the theme subscriber renders OS preference instead of
  // the user's chosen theme. Same call the main App makes.
  useEffect(() => {
    refreshSettings();
  }, []);

  useEffect(() => {
    if (!parsed) return;
    document.title = `DatumV — ${parsed.kind}`;
  }, [parsed]);

  if (!parsed || !parsed.requestId) {
    return (
      <WindowChrome kind="dialog">
        <div className="text-muted-foreground flex flex-1 items-center justify-center p-8 text-sm">
          Unknown dialog (no requestId in URL).
        </div>
      </WindowChrome>
    );
  }

  const child = renderDialogBody(parsed);
  return <WindowChrome kind="dialog">{child}</WindowChrome>;
}

function renderDialogBody({ kind, requestId, params }: ParsedHash): React.ReactNode {
  switch (kind) {
    case 'about':
      return <AboutDialog requestId={requestId} />;
    case 'externalChange':
      return (
        <ExternalChangeDialog
          requestId={requestId}
          fileName={params.fileName ?? ''}
          filePath={params.filePath ?? ''}
          // Booleans arrive as the string 'true'/'false' from URL-encoded
          // params (main.ts JSON.stringifies non-string payload values).
          isDirty={params.isDirty === 'true'}
        />
      );
    case 'unsavedChanges':
      return (
        <UnsavedChangesDialog
          requestId={requestId}
          fileName={params.fileName ?? ''}
          filePath={params.filePath ?? ''}
        />
      );
    case 'deleteFile':
      return (
        <DeleteFileDialog
          requestId={requestId}
          name={params.name ?? ''}
          path={params.path ?? ''}
          isDirectory={params.isDirectory === 'true'}
        />
      );
    case 'confirmLicense':
      return (
        <LicenseDialog
          requestId={requestId}
          licenseId={params.licenseId ?? ''}
          modelDisplayName={params.modelDisplayName}
          // `informational` arrives as the string 'true' from URL-encoded
          // params (main.ts JSON.stringifies non-string payload values).
          informational={params.informational === 'true'}
        />
      );
    case 'preflightRequired': {
      // The block payload is JSON-stringified by main.ts when packing
      // non-string values into URL query params. Parse it back; if the
      // payload is malformed or absent, fall through to a manual close
      // (a render-time resolveDialog would be a render-side-effect).
      const block = parsePreFlightBlock(params.block);
      if (!block) {
        return (
          <div className="flex flex-1 flex-col items-center justify-center gap-2 p-8 text-sm">
            <p className="text-foreground font-medium">
              Malformed pre-flight payload.
            </p>
            <button
              type="button"
              className="text-primary text-xs underline"
              onClick={() => resolveDialog(requestId, { install: false })}
            >
              close
            </button>
          </div>
        );
      }
      // Snapshot of catalog entries already downloading or in their
      // post-download installSql phase at dialog-open time. main.ts
      // JSON-encodes non-string payload values into URL params, so
      // this arrives as a stringified array.
      const inFlightIds = parseStringArray(params.inFlightIds);
      return (
        <PreFlightDialog
          requestId={requestId}
          block={block}
          inFlightIds={inFlightIds}
        />
      );
    }
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
