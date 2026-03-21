import { useTranslation } from 'react-i18next';
import { X } from 'lucide-react';
import { closePanel, type DockSide, type PanelId } from '@/state/nav';
import { PANEL_REGISTRY } from './registry';
import { ChatPanel } from './ChatPanel';
import { CatalogExplorerPanel } from './CatalogExplorerPanel';
import { ProceduresPanel } from './ProceduresPanel';
import { ProjectExplorerPanel } from './ProjectExplorerPanel';

// Header + body shell every dockable side panel sits inside. Keeps the
// title bar styling and the close-X identical across panels — the panel
// components themselves just render their own body.

export function SidePanelHost({
  side,
  panelId,
}: {
  side: DockSide;
  panelId: PanelId;
}) {
  const { t } = useTranslation('panels');
  const entry = PANEL_REGISTRY[panelId];
  // Panel titleKey is registry-data, not a static literal — cast past
  // i18next's strict typed-key check.
  const title = t(entry.titleKey as never) as string;

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <div className="select-none bg-dock-header text-dock-header-foreground flex h-9 shrink-0 items-center justify-between border-b px-3">
        <span className="text-xs font-medium tracking-wide uppercase">
          {title}
        </span>
        <button
          type="button"
          onClick={() => closePanel(side)}
          aria-label={t('closePanel')}
          title={t('closePanel')}
          className="text-dock-header-foreground/80 hover:text-dock-header-foreground flex size-6 items-center justify-center rounded-xs transition-colors hover:bg-white/15 cursor-pointer"
        >
          <X className="size-3.5" />
        </button>
      </div>
      <div className="flex-1 overflow-hidden bg-white dark:bg-background">
        <PanelBody panelId={panelId} />
      </div>
    </div>
  );
}

function PanelBody({ panelId }: { panelId: PanelId }) {
  switch (panelId) {
    case 'chat':
      return <ChatPanel />;
    case 'catalog':
      return <CatalogExplorerPanel />;
    case 'procedures':
      return <ProceduresPanel />;
    case 'projects':
      return <ProjectExplorerPanel />;
  }
}
