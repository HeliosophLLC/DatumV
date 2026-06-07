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
  actions,
  children,
}: {
  open: boolean;
  onClose: () => void;
  title?: string;
  // Optional header-row content rendered between the title and the
  // close button. Callers pass kind-specific affordances here (Download
  // for image / audio / video; nothing for struct / numeric_array).
  // Keeping it as a slot — rather than a `downloadHref` prop —
  // generalises cleanly when future modals want more than one action.
  actions?: React.ReactNode;
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
    // Inset the top edge by the title-bar height so the OS drag region
    // (and its minimize / maximize / close buttons) stays interactive
    // while the modal is open. `--app-titlebar-h` is set by TitleBar.tsx
    // to match the active platform-flavored bar; falls back to 32px so
    // the modal still renders sensibly if the var hasn't been written
    // yet (e.g. a torn-out window's first paint).
    <div
      role="dialog"
      aria-modal="true"
      onClick={onClose}
      style={{ top: 'var(--app-titlebar-h, 32px)' }}
      className="fixed inset-x-0 bottom-0 z-50 flex items-center justify-center bg-black/70 p-6"
    >
      <div
        // Stop propagation so clicks inside the panel don't close the
        // modal. The backdrop's onClick triggers close.
        onClick={(e) => e.stopPropagation()}
        // `min-w-xs` (~20rem) keeps the panel wide enough that a small
        // payload (tiny GIF, single audio control) doesn't collapse the
        // header until the title truncation no longer has room to do its
        // job. Without it a 64×64 image would shrink the panel down to
        // the image's width and the title would have nowhere to go.
        className="bg-card text-card-foreground border-border relative flex max-h-full min-w-xs max-w-full flex-col overflow-hidden rounded-xs border shadow-xl"
      >
        {/* Always render the header row so the close button has a stable
            slot even when the caller passed no title. The flex row's
            `justify-between` keeps the title and close button apart and
            `truncate` on the title cuts off long names without sliding
            behind the button — replaces the prior absolute-positioned X
            that overlapped the title text on a narrow panel. */}
        <div className="border-border bg-muted flex items-center justify-between gap-2 border-b px-3 py-2 text-xs font-medium">
          <span className="min-w-0 flex-1 truncate">{title ?? ''}</span>
          {actions !== undefined && (
            <div className="flex shrink-0 items-center gap-1">{actions}</div>
          )}
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            // Destructive-red hover mirrors the OS title bar's close
            // button (WindowsTitleBar uses the same pattern) so the
            // affordance reads as "this closes the surface" without an
            // extra label. White foreground on hover keeps the X
            // legible against the red fill.
            className="text-muted-foreground hover:bg-red-600 hover:text-white -my-1 -mr-1 shrink-0 cursor-pointer rounded-xs p-1 transition-colors"
          >
            <X className="size-4" />
          </button>
        </div>
        <div className="flex-1 overflow-auto">{children}</div>
      </div>
    </div>,
    document.body,
  );
}
