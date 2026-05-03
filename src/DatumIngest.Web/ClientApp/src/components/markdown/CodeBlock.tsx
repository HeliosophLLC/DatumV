import { useCallback, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Check, Copy } from 'lucide-react';
import { cn } from '@/lib/utils';

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

  const Icon = copied ? Check : Copy;
  const label = copied ? t('copy.copied') : t('copy.button');

  return (
    <div className="group relative">
      <pre ref={preRef} {...rest}>
        {children}
      </pre>
      <button
        type="button"
        onClick={onCopy}
        aria-label={label}
        title={label}
        className={cn(
          'absolute right-2 top-2 flex cursor-pointer items-center gap-1 rounded-xs border bg-zinc-50/90 px-1.5 py-0.5 text-xs',
          'text-muted-foreground hover:text-foreground dark:bg-zinc-900/90',
          'opacity-0 transition-opacity group-hover:opacity-100 focus:opacity-100',
          copied && 'opacity-100',
        )}
      >
        <Icon className="size-3" />
        <span>{label}</span>
      </button>
    </div>
  );
}
