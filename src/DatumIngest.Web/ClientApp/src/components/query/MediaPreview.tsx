import { useEffect } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';

// Simple in-window modal for previewing image / audio / video cells.
// Rendered via React Portal so it floats above the data-grid's scroll
// containers regardless of their z-index. Click the backdrop or press
// Escape to dismiss; click inside the panel is a no-op.
//
// Intentionally lightweight — doesn't share infrastructure with the
// multi-window dialog system in `components/dialogs/`, which is for
// Electron BrowserWindow dialogs (license confirms, etc.). For inline
// cell previews the dialog-window roundtrip is heavier than this
// component's whole implementation.

export function MediaPreview({
  open,
  onClose,
  title,
  children,
}: {
  open: boolean;
  onClose: () => void;
  title?: string;
  children: React.ReactNode;
}) {
  // Escape closes — the click-outside path already handles mouse/touch.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  if (!open) return null;

  return createPortal(
    <div
      role="dialog"
      aria-modal="true"
      onClick={onClose}
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-6"
    >
      <div
        // Stop propagation so clicks inside the panel don't close the
        // modal. The backdrop's onClick triggers close.
        onClick={(e) => e.stopPropagation()}
        className="bg-card text-card-foreground border-border relative flex max-h-full max-w-full flex-col overflow-hidden rounded-xs border shadow-xl"
      >
        {title && (
          <div className="border-border bg-muted flex items-center justify-between border-b px-3 py-2 text-xs font-medium">
            <span className="truncate">{title}</span>
          </div>
        )}
        <button
          type="button"
          onClick={onClose}
          aria-label="Close"
          className="hover:bg-muted text-muted-foreground hover:text-foreground absolute top-2 right-2 rounded-xs p-1 transition-colors"
        >
          <X className="size-4" />
        </button>
        <div className="flex-1 overflow-auto p-3">{children}</div>
      </div>
    </div>,
    document.body,
  );
}
