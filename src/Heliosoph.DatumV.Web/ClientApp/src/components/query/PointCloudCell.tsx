import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Box } from 'lucide-react';
import { PointCloudViewer, PointCloudViewerBody } from './PointCloudViewer';
import type { JsonCell } from '@/state/execution';
import { cn } from '@/lib/utils';

/**
 * Grid-cell rendering for a PointCloud value. Shows a clickable summary
 * (point count + grid dimensions when organized); clicking opens the
 * full 3D viewer modal.
 */
export function PointCloudCell({
  cell,
  largeMedia = false,
}: {
  cell: JsonCell;
  largeMedia?: boolean;
}) {
  const { t } = useTranslation('query');
  const [open, setOpen] = useState(false);
  const info = cell.pointCloud;

  if (!info) {
    // Defensive: server always emits the metadata for pointcloud cells.
    // Falling back to a text cell keeps the grid from blowing up if a
    // protocol drift ever drops the field.
    return <span className="text-muted-foreground italic">PointCloud</span>;
  }

  const organized = info.width > 0 && info.height > 0
    && info.width * info.height === info.pointCount;
  const summary = organized
    ? `${info.pointCount.toLocaleString()} ${t('pointCloudPointsLabel')} · ${info.width}×${info.height}`
    : `${info.pointCount.toLocaleString()} ${t('pointCloudPointsLabel')}`;
  const title = `PointCloud · ${summary}${info.hasColor ? ' · ' + t('pointCloudColored') : ''}`;

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title={title}
        className={cn(
          'text-muted-foreground hover:text-foreground inline-flex cursor-zoom-in items-center gap-1.5 rounded-xs',
          'hover:ring-primary hover:ring-1',
          largeMedia ? 'px-2 py-1' : '',
        )}
      >
        <Box className={cn(largeMedia ? 'size-5' : 'size-3.5')} />
        <span className={cn('tabular-nums', largeMedia ? 'text-sm' : 'text-xs')}>{summary}</span>
      </button>
      <PointCloudViewer
        cell={cell}
        open={open}
        onClose={() => setOpen(false)}
        title={title}
      />
    </>
  );
}

/**
 * Full-pane rendering when a query returns a single PointCloud value
 * (1 row × 1 column). Renders the viewer inline rather than via a
 * modal, since the pane is already the whole canvas the user has.
 */
export function SingleValuePointCloud({ cell }: { cell: JsonCell }) {
  return (
    <div className="flex h-full w-full">
      <InlinePointCloudViewer cell={cell} />
    </div>
  );
}

function InlinePointCloudViewer({ cell }: { cell: JsonCell }) {
  // Reuse the viewer body directly (no modal wrapper) so the 3D scene
  // fills the results pane. Always active because the single-value
  // path means the user opened this query to see exactly this cloud.
  return <PointCloudViewerBody cell={cell} active={true} />;
}
