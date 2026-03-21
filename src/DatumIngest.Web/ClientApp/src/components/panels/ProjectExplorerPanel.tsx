import { useTranslation } from 'react-i18next';
import { FolderTree } from 'lucide-react';

// Placeholder panel: the Project Explorer surface is on the roadmap
// but not in this round. Renders the dock affordance so the icon does
// something when clicked, without committing to a final shape.
export function ProjectExplorerPanel() {
  const { t } = useTranslation('panels');
  return (
    <div className="text-muted-foreground flex h-full flex-col items-center justify-center gap-3 px-6 text-center text-xs">
      <FolderTree className="size-8 opacity-50" />
      <p>{t('projects.comingSoon')}</p>
    </div>
  );
}
