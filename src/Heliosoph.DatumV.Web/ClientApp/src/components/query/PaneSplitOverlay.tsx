import type { SplitSide } from '@/state/tabs';
import { cn } from '@/lib/utils';

// Drop-zone overlay shown over a leaf pane's body while a tab is being
// dragged. Purely presentational — the parent leaf owns the drag state
// + handlers and passes the active zone in as a prop. This split is
// load-bearing: the overlay is `pointer-events: none` even when active
// so it never eats clicks from the editor underneath, which means it
// can't be the drag target itself either (an element with
// pointer-events: none receives no drag events). Hence the handlers
// live on the leaf's outer container.
//
// Five zones:
//
//   ┌─────────────────────────────┐
//   │             top             │
//   ├─────┬─────────────────┬─────┤
//   │ left│     center      │right│
//   ├─────┴─────────────────┴─────┤
//   │            bottom           │
//   └─────────────────────────────┘
//
//   - center → drop the tab into THIS leaf's tab list
//   - top/right/bottom/left → split this leaf, placing the new pane on
//     that side

export type DropZone = SplitSide | 'center' | null;

const ZONE_EDGE_THRESHOLD = 0.25; // 25% from each edge counts as a side zone.

/**
 * Returns the zone the cursor is currently over, or null when it's
 * outside the rect. Exposed so the owning leaf can compute the zone
 * in its own `dragover` handler and pass it back to the overlay.
 */
export function zoneForCursor(
  rect: DOMRect,
  clientX: number,
  clientY: number,
): DropZone {
  const x = (clientX - rect.left) / rect.width;
  const y = (clientY - rect.top) / rect.height;
  if (x < 0 || x > 1 || y < 0 || y > 1) return null;

  // Pick the zone whose edge is closest to the cursor among the four
  // sides. Center wins when all four normalised distances exceed the
  // threshold. Using the minimum distance instead of strict bands
  // avoids ambiguity at corners.
  const distLeft = x;
  const distRight = 1 - x;
  const distTop = y;
  const distBottom = 1 - y;
  const min = Math.min(distLeft, distRight, distTop, distBottom);
  if (min >= ZONE_EDGE_THRESHOLD) return 'center';
  if (min === distLeft) return 'left';
  if (min === distRight) return 'right';
  if (min === distTop) return 'top';
  return 'bottom';
}

export function PaneSplitOverlay({
  active,
  zone,
}: {
  active: boolean;
  zone: DropZone;
}) {
  if (!active) return null;
  return (
    <div className="pointer-events-none absolute inset-0 z-30">
      <div
        className={cn(
          'absolute transition-all duration-100',
          'bg-primary/20 border-primary border-2',
          zoneRectClasses(zone),
        )}
      />
    </div>
  );
}

function zoneRectClasses(zone: DropZone): string {
  switch (zone) {
    case 'left':
      return 'inset-y-0 left-0 w-1/2';
    case 'right':
      return 'inset-y-0 right-0 w-1/2';
    case 'top':
      return 'inset-x-0 top-0 h-1/2';
    case 'bottom':
      return 'inset-x-0 bottom-0 h-1/2';
    case 'center':
      return 'inset-4';
    case null:
      return 'opacity-0';
  }
}
