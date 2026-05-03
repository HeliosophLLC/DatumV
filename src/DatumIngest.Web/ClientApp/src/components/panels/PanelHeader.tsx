import type { ReactNode } from 'react';
import { X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/utils';

// Shared header chrome for side panels. Owns the title-bar styling
// (height, background, border) so every panel reads the same. Each panel
// supplies its title and decides whether to show the default close button
// and what (if any) extra action icons to render alongside it.
//
// Layout: title gets `flex-1 min-w-0 truncate` so it ellipsizes when the
// panel is narrow; the action icons stay rendered on the right (`shrink-0`).
//
// Why panels render their own header (vs. SidePanelHost doing it once):
// per-panel action affordances (refresh, collapse-all, etc.) are first-class
// and live next to the panel logic that drives them, not in a registry.

export function PanelHeader({
  title,
  onClose,
  actions,
}: {
  title: string;
  /** When supplied, the default X close button renders and dispatches this. */
  onClose?: () => void;
  /** Action buttons rendered between the title and the close button. */
  actions?: ReactNode;
}) {
  const { t } = useTranslation('panels');
  return (
    <div className="select-none bg-dock-header text-dock-header-foreground flex h-9 shrink-0 items-center gap-1 border-b px-3">
      <span className="flex-1 min-w-0 truncate text-xs font-medium tracking-wide uppercase">
        {title}
      </span>
      {actions}
      {onClose !== undefined && (
        <PanelHeaderButton
          onClick={onClose}
          ariaLabel={t('closePanel')}
          title={t('closePanel')}
        >
          <X className="size-3.5" />
        </PanelHeaderButton>
      )}
    </div>
  );
}

/**
 * Standard-sized header action button. Slot the icon (and any aria/title
 * text) in via props so every button reads the same.
 */
export function PanelHeaderButton({
  onClick,
  ariaLabel,
  title,
  children,
  className,
}: {
  onClick: () => void;
  ariaLabel: string;
  title?: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={ariaLabel}
      title={title ?? ariaLabel}
      className={cn(
        'text-dock-header-foreground/80 hover:text-dock-header-foreground flex size-6 shrink-0 items-center justify-center rounded-xs transition-colors hover:bg-white/15 cursor-pointer',
        className,
      )}
    >
      {children}
    </button>
  );
}
