import { useSnapshot } from 'valtio';
import { hostState } from '@/state/host';
import { windowState, startResize, type ResizeSide } from '@/state/window';

// Eight invisible zones around the window edge that delegate to the OS for
// native resize. Edge strips are 4px thick; corner squares are 8px (slightly
// larger so diagonal resize is grabbable). Skipped entirely in browser mode
// (the browser owns the window) and when the window is maximized (resizing
// a maximized window is a no-op and the zones would just block clicks).
//
// Render at the App root so the zones sit above all content. They cover
// the title bar's corners; the title bar's buttons are inset enough that
// the 8px corner overlap doesn't steal real button-clicks.

const EDGE = 4;
const CORNER = 8;

interface Zone {
  side: ResizeSide;
  style: React.CSSProperties;
  cursor: string;
}

const zones: Zone[] = [
  // Edges (excluded from the corner squares' areas).
  { side: 'top',    cursor: 'ns-resize', style: { top: 0,    left: CORNER, right: CORNER, height: EDGE } },
  { side: 'right',  cursor: 'ew-resize', style: { top: CORNER, bottom: CORNER, right: 0, width: EDGE } },
  { side: 'bottom', cursor: 'ns-resize', style: { bottom: 0, left: CORNER, right: CORNER, height: EDGE } },
  { side: 'left',   cursor: 'ew-resize', style: { top: CORNER, bottom: CORNER, left: 0, width: EDGE } },
  // Corners.
  { side: 'top-left',     cursor: 'nwse-resize', style: { top: 0, left: 0, width: CORNER, height: CORNER } },
  { side: 'top-right',    cursor: 'nesw-resize', style: { top: 0, right: 0, width: CORNER, height: CORNER } },
  { side: 'bottom-left',  cursor: 'nesw-resize', style: { bottom: 0, left: 0, width: CORNER, height: CORNER } },
  { side: 'bottom-right', cursor: 'nwse-resize', style: { bottom: 0, right: 0, width: CORNER, height: CORNER } },
];

export function ResizeFrame() {
  const { os, runtime } = useSnapshot(hostState);
  const { maximized } = useSnapshot(windowState);

  // Windows-only today (the Win32 P/Invoke in HostBridge.cs handles the
  // actual resize). Mac/Linux use OS chrome with native resize via the
  // window border. Browser owns its own window. Maximized = no resize.
  if (runtime !== 'photino' || os !== 'windows' || maximized) return null;

  return (
    <div className="pointer-events-none fixed inset-0 z-50">
      {zones.map((z) => (
        <div
          key={z.side}
          className="pointer-events-auto absolute"
          style={{ ...z.style, cursor: z.cursor }}
          onMouseDown={(event) => {
            if (event.button !== 0) return;
            event.preventDefault();
            startResize(z.side);
          }}
        />
      ))}
    </div>
  );
}
