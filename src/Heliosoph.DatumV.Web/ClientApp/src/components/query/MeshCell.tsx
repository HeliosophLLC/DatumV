import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Box } from 'lucide-react';
import { MeshViewer, MeshViewerBody } from './MeshViewer';
import type { JsonCell } from '@/state/execution';
import { cn } from '@/lib/utils';

/**
 * Grid-cell rendering for a Mesh value. Shows a clickable summary
 * (vertex + triangle counts) — click opens the full 3D viewer modal.
 */
export function MeshCell({
  cell,
  largeMedia = false,
}: {
  cell: JsonCell;
  largeMedia?: boolean;
}) {
  const { t } = useTranslation('query');
  const [open, setOpen] = useState(false);
  const info = cell.mesh;

  if (!info) {
    // Defensive: server always emits the metadata for mesh cells. Falling
    // back to a text cell keeps the grid from blowing up if a protocol
    // drift ever drops the field.
    return <span className="text-muted-foreground italic">Mesh</span>;
  }

  const summary =
    `${info.vertexCount.toLocaleString()} ${t('meshVerticesLabel')} `
    + `· ${info.triangleCount.toLocaleString()} ${t('meshTrianglesLabel')}`;
  const title =
    `Mesh · ${summary}`
    + (info.hasColor ? ` · ${t('meshColored')}` : '')
    + (info.hasNormals ? ` · ${t('meshShaded')}` : '');

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
      <MeshViewer
        cell={cell}
        open={open}
        onClose={() => setOpen(false)}
        title={title}
      />
    </>
  );
}

/**
 * Full-pane rendering when a query returns a single Mesh value
 * (1 row × 1 column). Renders the viewer body inline rather than via
 * a modal, since the pane is already the canvas the user has.
 */
export function SingleValueMesh({ cell }: { cell: JsonCell }) {
  return (
    <div className="flex h-full w-full">
      <MeshViewerBody cell={cell} active={true} />
    </div>
  );
}
