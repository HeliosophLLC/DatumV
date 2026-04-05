import { cn } from '@/lib/utils';

// Global status bar pinned to the bottom of every main / torn-out
// window (suppressed in dialog windows by WindowChrome). Empty by
// design today — the slots below are the home for future chips
// (compute-node id, connection state, active query stats, residency
// summary, etc.). Mirrors the SSMS-style status row at the bottom of
// the results pane but lives at the window level so chips that aren't
// specific to a single query can find a stable home.
//
// Color matches the titlebar (`bg-background` + `text-muted-foreground`
// + border) so the chrome reads as one continuous frame around the
// workspace — distinct from the SSMS-style yellow `bg-status-bar` that
// the per-results-pane footer uses for query-specific state.
//
// Adding a chip:
//   1. Build the chip component as a `shrink-0 whitespace-nowrap`
//      element with its own padding (typically `px-3`). Give each
//      sibling a left border (`border-border border-l`) so adjacent
//      chips read as distinct panels.
//   2. Drop it into `leftChips` or `rightChips`. Left grows; the right
//      stack stays anchored. If a chip is mode-driven (e.g. visible
//      only when a particular state is set), gate its render at the
//      callsite — the bar itself stays unconditional so its height
//      doesn't shift as chips appear and disappear.
export function GlobalStatusBar({
  leftChips,
  rightChips,
  className,
}: {
  leftChips?: React.ReactNode;
  rightChips?: React.ReactNode;
  className?: string;
}) {
  return (
    <div
      className={cn(
        'bg-background text-muted-foreground border-border flex h-6 shrink-0 items-stretch overflow-hidden border-t text-xs',
        className,
      )}
      role="status"
      aria-live="polite"
    >
      {/* Left section grows to fill; chips here render in document
          order. `min-w-0` lets long content truncate via the chip's
          own classes rather than pushing the right stack off-screen. */}
      <div className="flex min-w-0 flex-1 items-stretch">{leftChips}</div>
      {/* Right section stays its natural width — chips here pin to
          the trailing edge. */}
      <div className="flex shrink-0 items-stretch">{rightChips}</div>
    </div>
  );
}
