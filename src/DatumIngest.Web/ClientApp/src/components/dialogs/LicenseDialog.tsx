import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Loader2 } from 'lucide-react';
import ReactMarkdown, { type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { openDialog, resolveDialog } from '@/state/dialogs';
import { Button } from '@/components/ui/button';
import type { CatalogLicense } from '@/api/generated/openapi-client';

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
  // When true, the dialog is purely informational — no Accept/Decline,
  // just a Close button that resolves with null. Used for sub-dialogs
  // spawned when the user clicks a relative-link from the parent license
  // (e.g. an OpenRAIL++-M doc referencing the parent OpenRAIL-M doc).
  // The install's accept decision belongs to the originally-requested
  // license, never to a referenced one.
  informational?: boolean;
}

interface LoadedLicense {
  meta: CatalogLicense;
  text: string;
}

export function LicenseDialog({
  requestId,
  licenseId,
  modelDisplayName,
  informational = false,
}: LicenseDialogProps) {
  const { t } = useTranslation('dialogs');
  const [loaded, setLoaded] = useState<LoadedLicense | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [licenses, text] = await Promise.all([
          fetchLicenseRegistry(),
          fetchLicenseText(licenseId),
        ]);
        if (cancelled) return;
        const meta = licenses[licenseId];
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
        <LicenseMarkdown text={loaded.text} />
      </div>
      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        {informational ? (
          <Button variant="default" size="sm" onClick={onDecline}>
            {t('license.close')}
          </Button>
        ) : (
          <>
            <Button variant="outline" size="sm" onClick={onDecline}>
              {t('license.decline')}
            </Button>
            <Button variant="default" size="sm" onClick={onAccept}>
              {t('license.accept')}
            </Button>
          </>
        )}
      </footer>
    </div>
  );
}

// Render the license body as markdown with link interception. The same
// rendering applies in nested (preview) license sub-dialogs — see
// DialogShell for how those are spawned.
function LicenseMarkdown({ text }: { text: string }) {
  // `components` is stable for the life of the dialog, but referencing it
  // through useMemo keeps eslint-react happy and avoids unnecessary
  // re-allocations on the per-second download ticker if this component
  // ever ends up nested under one.
  // Per-element styling lives here (instead of an external prose stylesheet)
  // so the markdown rendering is colocated with the license dialog. The
  // typography plugin isn't installed; this covers what license documents
  // actually use: headings, paragraphs, lists, emphasis, code, links.
  const components = useMemo<Components>(
    () => ({
      a: ({ href, children, ...rest }) => (
        <LicenseLink href={href ?? ''} {...rest}>
          {children}
        </LicenseLink>
      ),
      h1: ({ children }) => <h1 className="mb-3 mt-1 text-lg font-semibold">{children}</h1>,
      h2: ({ children }) => <h2 className="mb-2 mt-5 text-base font-semibold">{children}</h2>,
      h3: ({ children }) => <h3 className="mb-2 mt-4 text-sm font-semibold">{children}</h3>,
      p: ({ children }) => <p className="mb-3">{children}</p>,
      ul: ({ children }) => <ul className="mb-3 ml-5 list-disc space-y-1">{children}</ul>,
      ol: ({ children }) => <ol className="mb-3 ml-5 list-decimal space-y-1">{children}</ol>,
      li: ({ children }) => <li>{children}</li>,
      strong: ({ children }) => <strong className="font-semibold">{children}</strong>,
      em: ({ children }) => <em className="italic">{children}</em>,
      code: ({ children }) => (
        <code className="bg-muted rounded px-1 py-0.5 font-mono text-xs">{children}</code>
      ),
      hr: () => <hr className="my-4 border-border" />,
      blockquote: ({ children }) => (
        <blockquote className="border-border text-muted-foreground mb-3 border-l-2 pl-3 italic">
          {children}
        </blockquote>
      ),
    }),
    [],
  );
  return (
    <div className="text-foreground/90 text-sm leading-relaxed">
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
        {text}
      </ReactMarkdown>
    </div>
  );
}

function LicenseLink({
  href,
  children,
  ...rest
}: React.AnchorHTMLAttributes<HTMLAnchorElement> & { href: string }) {
  const kind = classifyLink(href);
  const onClick = (e: React.MouseEvent<HTMLAnchorElement>) => {
    e.preventDefault();
    if (kind.type === 'external') {
      void window.electronHost.openExternal(kind.url);
    } else if (kind.type === 'sibling-license') {
      // Sub-dialog. Modal child of the *current* dialog window — the user
      // reads the linked license, closes it, and lands back here with the
      // originally-requested license still on screen. `informational: true`
      // hides the Accept/Decline footer in the sub-dialog because the
      // install's accept choice belongs to the originally-requested
      // license, not to a referenced one.
      openDialog({
        kind: 'confirmLicense',
        payload: {
          licenseId: kind.licenseId,
          modelDisplayName: '',
          informational: true,
        },
      });
    }
    // 'unknown' falls through silently — preventDefault has already
    // blocked the default navigation, which would otherwise pull the
    // dialog away from the license content.
  };
  return (
    <a
      href={href}
      onClick={onClick}
      className="text-blue-600 underline-offset-2 hover:underline dark:text-blue-400"
      {...rest}
    >
      {children}
    </a>
  );
}

type LinkKind =
  | { type: 'external'; url: string }
  | { type: 'sibling-license'; licenseId: string }
  | { type: 'unknown' };

function classifyLink(href: string): LinkKind {
  if (!href) return { type: 'unknown' };
  if (/^https?:\/\//i.test(href)) {
    return { type: 'external', url: href };
  }
  // Match `./id.md`, `id.md`, `../something/id.md` — all map to the same
  // license dir on the server. We extract the basename minus .md.
  const m = href.match(/^(?:\.{1,2}\/)*([\w.-]+)\.md(?:[?#].*)?$/i);
  if (m) {
    return { type: 'sibling-license', licenseId: m[1] };
  }
  return { type: 'unknown' };
}

async function fetchLicenseRegistry(): Promise<Record<string, CatalogLicense>> {
  // Tiny dict (≤20 entries); the dialog fetches it on each open. Caching
  // is fine to add later — license metadata is shipped content, not
  // user-mutable, so the response is safe to memoize indefinitely.
  const response = await window.fetch('/api/licenses', { credentials: 'include' });
  if (!response.ok) {
    throw new Error(`License registry fetch failed: ${response.status}`);
  }
  return (await response.json()) as Record<string, CatalogLicense>;
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
