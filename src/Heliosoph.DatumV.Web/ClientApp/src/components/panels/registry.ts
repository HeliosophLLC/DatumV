import type { LucideIcon } from 'lucide-react';
import {
  Code2,
  Database,
  FolderTree,
  MessageCircle,
} from 'lucide-react';
import type { PanelId } from '@/state/nav';

// Shared metadata for every dockable panel. AppDock reads `icon` /
// `tooltipKey` / `disabled` to render the icon strip; App.tsx reads
// `titleKey` and resolves the body component lazily so unmounted
// panels don't pay any render cost.
//
// Adding a new panel = add the id to PanelId in state/nav.ts, register
// its row here, and create the component under components/panels/.
// The dock layout, persistence, and dispatch are all data-driven.

export interface PanelRegistryEntry {
  id: PanelId;
  icon: LucideIcon;
  /** i18n key in the `common` namespace for the dock-icon tooltip. */
  tooltipKey: string;
  /** i18n key in the `panels` namespace for the side-panel header. */
  titleKey: string;
  /**
   * True when the panel should render as a disabled placeholder
   * (icon dimmed, clicking surfaces a "coming soon" body). Used for
   * Project Explorer until that surface ships.
   */
  disabled?: boolean;
}

export const PANEL_REGISTRY: Readonly<Record<PanelId, PanelRegistryEntry>> = {
  chat: {
    id: 'chat',
    icon: MessageCircle,
    tooltipKey: 'dock.tooltip.chat',
    titleKey: 'chat.title',
  },
  catalog: {
    id: 'catalog',
    icon: Database,
    tooltipKey: 'dock.tooltip.catalog',
    titleKey: 'catalog.title',
  },
  procedures: {
    id: 'procedures',
    icon: Code2,
    tooltipKey: 'dock.tooltip.procedures',
    titleKey: 'procedures.title',
  },
  projects: {
    id: 'projects',
    icon: FolderTree,
    tooltipKey: 'dock.tooltip.projects',
    titleKey: 'projects.title',
    disabled: true,
  },
};
