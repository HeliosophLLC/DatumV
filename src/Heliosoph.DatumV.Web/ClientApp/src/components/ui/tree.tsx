import type { HTMLAttributes, MouseEvent, ReactNode } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';
import { cn } from '@/lib/utils';

// Shared tree primitives for sidebar tree panels (CatalogExplorerPanel,
// ProjectExplorerPanel, etc.). Owns the visual language only — sizing,
// hover affordance, chevron treatment, dashed-line indents, selection /
// focus highlights. Each panel builds its own data → tree projection and
// slots its own row content.

export function TreeRoot({
  children,
  className,
  ...props
}: HTMLAttributes<HTMLUListElement>) {
  return (
    <ul role="tree" className={cn('py-1 text-xs', className)} {...props}>
      {children}
    </ul>
  );
}

/**
 * Toggleable parent node with a label row + optional nested child group.
 * The chevron+expand affordance lives here so callers only supply the
 * icon/label payload to the right of it. When `open` is true, children
 * render inside a nested `<ul>` with the dashed-border indent.
 *
 * Selection / focus props are optional. When provided, the row also
 * dispatches `onRowClick` (with the original `MouseEvent` so the caller
 * can read modifier keys) and renders the appropriate highlights.
 * Callers can omit them entirely; the row stays a plain toggle button.
 */
export function TreeBranch({
  label,
  open,
  onToggle,
  dimmed = false,
  selected = false,
  focused = false,
  onRowClick,
  dataPath,
  children,
}: {
  label: ReactNode;
  open: boolean;
  onToggle: () => void;
  /** Mute the row text — used for system views, secondary kinds, etc. */
  dimmed?: boolean;
  /** Highlights the row as part of the active selection. */
  selected?: boolean;
  /** Marks the row as the keyboard-cursor target (focus ring). */
  focused?: boolean;
  /**
   * Optional click intercept. When provided, the row dispatches this
   * (with the original MouseEvent for modifier detection) instead of
   * calling `onToggle` directly — the caller decides whether to also
   * toggle. When omitted, plain click → toggle, matching the old API.
   */
  onRowClick?: (e: MouseEvent<HTMLButtonElement>) => void;
  /**
   * Tagging hook for callers that need to query the row by path (e.g.
   * arrow-key navigation that calls scrollIntoView on the focused row).
   * Lives on the layout element directly so scrollIntoView actually
   * resolves a box rather than the no-box `display: contents` wrapper.
   */
  dataPath?: string;
  children?: ReactNode;
}) {
  return (
    <li>
      <button
        type="button"
        onClick={(e) => (onRowClick ? onRowClick(e) : onToggle())}
        className={cn(
          'flex w-full items-center gap-1 px-2 py-1 text-left transition-colors',
          selectionClasses(selected, focused, dimmed),
        )}
        aria-expanded={open}
        aria-selected={selected || undefined}
        data-path={dataPath}
      >
        {open ? (
          <ChevronDown className="size-3 shrink-0" />
        ) : (
          <ChevronRight className="size-3 shrink-0" />
        )}
        {label}
      </button>
      {open && <ul className="ml-3 border-l border-dashed pl-3">{children}</ul>}
    </li>
  );
}

/**
 * Non-toggleable leaf row. The caller slots icon + label + trailing
 * metadata directly as children; this component only owns the
 * common padding / hover / monospaced typography.
 *
 * Selection / focus props mirror TreeBranch's. `onRowClick` is dispatched
 * with the original MouseEvent so the caller can read modifier keys.
 */
export function TreeRow({
  children,
  dimmed = false,
  selected = false,
  focused = false,
  onRowClick,
  dataPath,
  title,
}: {
  children: ReactNode;
  dimmed?: boolean;
  selected?: boolean;
  focused?: boolean;
  onRowClick?: (e: MouseEvent<HTMLDivElement>) => void;
  /** See TreeBranch.dataPath — lives on the layout element. */
  dataPath?: string;
  title?: string;
}) {
  return (
    <li>
      <div
        className={cn(
          'flex w-full items-center gap-1 px-2 py-0.5 font-mono transition-colors',
          // Cursor-pointer signals row is interactable when the panel
          // has wired a click handler; static rows stay default.
          onRowClick && 'cursor-pointer',
          selectionClasses(selected, focused, dimmed),
        )}
        title={title}
        onClick={onRowClick}
        aria-selected={selected || undefined}
        data-path={dataPath}
      >
        {children}
      </div>
    </li>
  );
}

/**
 * Uppercase tracked subsection header — used inside an open TreeBranch to
 * label a homogeneous group of TreeRow children (e.g. "Columns (5)" or
 * "Indexes (2)" in CatalogExplorerPanel).
 */
export function TreeSubheader({ children }: { children: ReactNode }) {
  return (
    <li className="text-muted-foreground px-2 pt-2 pb-1 text-[10px] tracking-wide uppercase">
      {children}
    </li>
  );
}

/**
 * Resolves the visual treatment for a row given its state. Centralised
 * so TreeBranch + TreeRow stay aligned and the panel doesn't have to
 * re-derive the class set.
 */
function selectionClasses(
  selected: boolean,
  focused: boolean,
  dimmed: boolean,
): string {
  return cn(
    !selected && 'hover:bg-primary/10',
    selected && 'bg-primary/20 hover:bg-primary/25',
    focused && 'ring-1 ring-inset ring-primary/60',
    dimmed && 'text-muted-foreground',
  );
}
