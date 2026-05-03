import type { DockSide, PanelId } from '@/state/nav';
import { ChatPanel } from './ChatPanel';
import { CatalogExplorerPanel } from './CatalogExplorerPanel';
import { ProceduresPanel } from './ProceduresPanel';
import { ProjectExplorerPanel } from './ProjectExplorerPanel';

// Thin chrome for every side panel: a flex column with the dock-coloured
// background, and a slot for the panel component. Header is rendered by
// each panel via <PanelHeader> so per-panel actions (refresh, collapse all,
// etc.) live next to the panel logic that drives them.

export function SidePanelHost({
  side,
  panelId,
}: {
  side: DockSide;
  panelId: PanelId;
}) {
  return (
    <div className="flex h-full flex-col overflow-hidden bg-white dark:bg-background">
      <PanelBody panelId={panelId} side={side} />
    </div>
  );
}

function PanelBody({
  panelId,
  side,
}: {
  panelId: PanelId;
  side: DockSide;
}) {
  switch (panelId) {
    case 'chat':
      return <ChatPanel side={side} />;
    case 'catalog':
      return <CatalogExplorerPanel side={side} />;
    case 'procedures':
      return <ProceduresPanel side={side} />;
    case 'projects':
      return <ProjectExplorerPanel side={side} />;
  }
}
