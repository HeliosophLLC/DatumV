import { isValidElement, useCallback, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Check, Copy, SquareArrowOutUpRight } from 'lucide-react';
import { openTab } from '@/state/tabs';
import { cn } from '@/lib/utils';

// The fenced-code language lives on the inner `<code class="language-…">`
// element react-markdown hands us as the sole child. Pull it out so we
// can offer SQL-specific affordances (Open in new tab) only for SQL.
function fenceLanguage(children: React.ReactNode): string | null {
  const child = Array.isArray(children) ? children[0] : children;
  if (!isValidElement<{ className?: string }>(child)) return null;
  const className = child.props.className;
  if (typeof className !== 'string') return null;
  const match = /language-(\w+)/.exec(className);
  return match ? match[1].toLowerCase() : null;
}

// SQL fences are tagged ```sql; accept the common aliases too so a doc
// author writing ```postgresql still gets the button.
const SQL_LANGUAGES = new Set(['sql', 'postgresql', 'postgres', 'psql', 'pgsql']);

/**
 * Markdown `<pre>` override that wraps the original `<pre>` in a
 * positioned container so a Copy button can sit pinned to the top-right
 * corner without disturbing the code's layout. Reads the rendered text
 * via the `<pre>` ref's `innerText` on click — robust against the
 * highlight.js `<span class="hljs-*">` token wrapping that
 * rehype-highlight inserts.
 *
 * Shared between `DocsView` and the model-card markdown renderer so
 * both surfaces present the same copy affordance. Labels come from the
 * `docs` i18n namespace; consumers don't have to wire anything up.
 */
export function CodeBlock({
  children,
  ...rest
}: React.HTMLAttributes<HTMLPreElement>) {
  const { t } = useTranslation('docs');
  const preRef = useRef<HTMLPreElement | null>(null);
  const [copied, setCopied] = useState(false);

  const isSql = SQL_LANGUAGES.has(fenceLanguage(children) ?? '');

  const onCopy = useCallback(async () => {
    const text = preRef.current?.innerText ?? '';
    if (text.length === 0) return;
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      // Clipboard API can fail in non-secure contexts or when the doc
      // isn't focused. Swallow — no clipboard, no confirmation.
      return;
    }
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1500);
  }, []);

  // Open the block's SQL in a fresh editor tab. Reads `innerText` (like
  // Copy) so the highlight.js token wrapping doesn't leak into the SQL.
  // `openTab` activates the new tab in the focused leaf, which navigates
  // away from this pinned Docs / Models / Datasets view to the editor.
  const onOpenInNewTab = useCallback(() => {
    const sql = preRef.current?.innerText ?? '';
    if (sql.trim().length === 0) return;
    openTab(sql, undefined, 'sql');
  }, []);

  const Icon = copied ? Check : Copy;
  const label = copied ? t('copy.copied') : t('copy.button');
  const openLabel = t('openInNewTab');

  const buttonClass = cn(
    'flex cursor-pointer items-center gap-1 rounded-xs border bg-zinc-50/90 px-1.5 py-0.5 text-xs',
    'text-muted-foreground hover:text-foreground dark:bg-zinc-900/90',
  );

  return (
    <div className="group relative">
      <pre ref={preRef} {...rest}>
        {children}
      </pre>
      <div
        className={cn(
          'absolute right-2 top-2 flex items-center gap-1',
          'opacity-0 transition-opacity group-hover:opacity-100 focus-within:opacity-100',
          copied && 'opacity-100',
        )}
      >
        {isSql && (
          <button
            type="button"
            onClick={onOpenInNewTab}
            aria-label={openLabel}
            title={openLabel}
            className={buttonClass}
          >
            <SquareArrowOutUpRight className="size-3" />
            <span>{openLabel}</span>
          </button>
        )}
        <button
          type="button"
          onClick={onCopy}
          aria-label={label}
          title={label}
          className={buttonClass}
        >
          <Icon className="size-3" />
          <span>{label}</span>
        </button>
      </div>
    </div>
  );
}
