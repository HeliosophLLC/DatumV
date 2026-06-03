import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { isExternalUrl, openExternalUrl } from '@/lib/openExternal';

// Renders short markdown snippets (catalog summaries / descriptions)
// without emitting a block-level paragraph wrapper, so the caller's
// surrounding element keeps full control of typography (line-clamp,
// color, size). Suitable for inline-only markdown — emphasis, inline
// code, links. Block constructs (lists, fences, headings) flatten to
// their inline content rather than rendering structurally.
export function InlineMarkdown({ children }: { children: string }) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      components={{
        p: ({ children }) => <>{children}</>,
        code: ({ children }) => (
          <code className="bg-muted/60 rounded-xs px-1 py-0.5 font-mono text-[0.9em]">
            {children}
          </code>
        ),
        a: ({ href, children, ...rest }) => (
          <a
            {...rest}
            href={href}
            onClick={(e) => {
              if (isExternalUrl(href)) {
                e.preventDefault();
                openExternalUrl(href);
              }
            }}
          >
            {children}
          </a>
        ),
      }}
    >
      {children}
    </ReactMarkdown>
  );
}
