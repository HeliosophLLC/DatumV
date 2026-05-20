import { useEffect, useState } from 'react';
import { useSnapshot } from 'valtio';
import { useTranslation } from 'react-i18next';
import {
  movePanel,
  navState,
  togglePanel,
  type DockSide,
  type PanelId,
} from '@/state/nav';
import { isTornOutWindow } from '@/state/tabs';
import { PANEL_REGISTRY } from '@/components/panels/registry';
import { cn } from '@/lib/utils';

// VS Code-style activity bar — now in two flavours:
//
//   <AppDock side="left" />   always visible. Renders the panel icons
//                              configured for the left dock.
//   <AppDock side="right" />  hidden when no panel icons live on the
//                              right dock; visible the moment the user
//                              drags one over.
//
// Both docks are HTML5 drop targets so users can move icons between
// them. The drag source is the dock button itself.
//
// Hidden entirely in torn-out windows (they host a single editor and
// have no dock affordance).
//
// Settings used to live as a pinned icon at the bottom of the left dock
// that swapped the workspace view to a settings page. It is now a pinned
// tab in the query editor's strip alongside Models / Documentation.

const DOCK_MIME = 'application/x-datum-panel-id';

// Document-level drag tracker. The empty-right-dock landing strip needs
// to expand the moment the user starts dragging an icon — not when they
// happen to hover over the strip — because at zero width there's nothing
// to hover over (the user reported "I don't see the right panel when I
// drag"). We listen at the document so any drag of our MIME type lights
// up the strip immediately. Subscriber count is bounded (at most 2: the
// left and right docks) so a tiny module-level subscriber set is fine.
type DragSubscriber = (inFlight: boolean) => void;
const dragSubscribers = new Set<DragSubscriber>();
let dragListenersBound = false;
function bindDragListeners() {
  if (dragListenersBound) return;
  dragListenersBound = true;
  // dragstart bubbles up from the source button — we set the MIME on
  // the source so by the time the document handler fires, the types
  // array carries our discriminator. Only flip the state for our drags
  // so the host page's other DnD (file drops, browser links) doesn't
  // light up the dock strip.
  document.addEventListener('dragstart', (e) => {
    if (e.dataTransfer?.types.includes(DOCK_MIME)) {
      dragSubscribers.forEach((fn) => fn(true));
    }
  });
  // `dragend` fires on the source whether the drag completed or was
  // cancelled (esc, drop outside any target), so this is the right
  // place to clear regardless of outcome.
  document.addEventListener('dragend', () => {
    dragSubscribers.forEach((fn) => fn(false));
  });
}

function useDockDragInFlight(): boolean {
  const [inFlight, setInFlight] = useState(false);
  useEffect(() => {
    bindDragListeners();
    dragSubscribers.add(setInFlight);
    return () => {
      dragSubscribers.delete(setInFlight);
    };
  }, []);
  return inFlight;
}

export function AppDock({ side }: { side: DockSide }) {
  const { left, right, openLeft, openRight } = useSnapshot(navState);
  const [isDragOver, setIsDragOver] = useState(false);

  if (isTornOutWindow) return null;
  const items = side === 'left' ? left : right;
  const open = side === 'left' ? openLeft : openRight;

  // Right dock is conditional: render nothing when no icons live there
  // AND nothing is being dragged that could land here. While a drag is
  // in flight we briefly expand it to a drop landing strip via
  // `isDragOver`, but the strip mounts on dragenter at the document
  // level instead — see DockDropStrip below.
  if (side === 'right' && items.length === 0) {
    return <DockDropStrip side="right" />;
  }

  function onDragOver(event: React.DragEvent<HTMLElement>) {
    if (!event.dataTransfer.types.includes(DOCK_MIME)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
    setIsDragOver(true);
  }
  function onDragLeave(event: React.DragEvent<HTMLElement>) {
    // Only clear when we actually leave the dock — child dragleaves bubble.
    if (event.currentTarget.contains(event.relatedTarget as Node | null)) return;
    setIsDragOver(false);
  }
  function onDrop(event: React.DragEvent<HTMLElement>) {
    const raw = event.dataTransfer.getData(DOCK_MIME);
    if (!raw) return;
    event.preventDefault();
    setIsDragOver(false);
    movePanel(raw as PanelId, side);
  }

  return (
    <nav
      aria-label={side === 'left' ? 'Left dock' : 'Right dock'}
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
      className={cn(
        'flex w-12 flex-col items-center bg-background py-1',
        side === 'left' ? 'border-r' : 'border-l',
        isDragOver && 'bg-primary/5',
      )}
    >
      {items.map((id) => (
        <PanelDockButton key={id} side={side} id={id} active={open === id} />
      ))}
    </nav>
  );
}

// Compact landing strip rendered in place of the right dock when no
// icons live there yet. Picks up the drop the moment the user hovers,
// then the parent layout swaps to the real dock on the next render
// (because the dropped icon now lives in right[]).
function DockDropStrip({ side }: { side: DockSide }) {
  const { t } = useTranslation();
  const [isDragOver, setIsDragOver] = useState(false);
  // Drag-in-flight is tracked globally (document-level) so the strip
  // expands the instant a drag starts, before the user has had a chance
  // to move toward this side of the window. Without that, an empty
  // right dock would be invisible until the user dragged onto it —
  // and at zero width there's nothing to drag onto.
  const hasDragInFlight = useDockDragInFlight();

  function onDragOver(event: React.DragEvent<HTMLDivElement>) {
    if (!event.dataTransfer.types.includes(DOCK_MIME)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
    setIsDragOver(true);
  }
  function onDragLeave(event: React.DragEvent<HTMLDivElement>) {
    if (event.currentTarget.contains(event.relatedTarget as Node | null)) return;
    setIsDragOver(false);
  }
  function onDrop(event: React.DragEvent<HTMLDivElement>) {
    const raw = event.dataTransfer.getData(DOCK_MIME);
    if (!raw) return;
    event.preventDefault();
    setIsDragOver(false);
    movePanel(raw as PanelId, side);
  }

  return (
    <div
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
      aria-label={t('dock.dropHere')}
      className={cn(
        'flex flex-col items-center justify-center border-l text-[10px] transition-all',
        // Visible-but-narrow during a drag; collapsed otherwise so the
        // workspace gets every available pixel back.
        hasDragInFlight ? 'w-12 bg-primary/5' : 'w-0',
        isDragOver && 'bg-primary/15',
      )}
    >
      {hasDragInFlight && (
        <span className="text-muted-foreground rotate-90 tracking-wider uppercase whitespace-nowrap">
          {t('dock.dropHere')}
        </span>
      )}
    </div>
  );
}

// Per-panel dock button (top-of-dock items). Draggable so users can
// move it between docks.
function PanelDockButton({
  side,
  id,
  active,
}: {
  side: DockSide;
  id: PanelId;
  active: boolean;
}) {
  const { t } = useTranslation();
  const entry = PANEL_REGISTRY[id];
  const Icon = entry.icon;
  // Tooltip key is read from the panel registry — i18next's typed t()
  // doesn't accept dynamic keys, so we cast through `string`.
  const label = t(entry.tooltipKey as never) as string;

  function onDragStart(event: React.DragEvent<HTMLButtonElement>) {
    event.dataTransfer.setData(DOCK_MIME, id);
    event.dataTransfer.effectAllowed = 'move';
  }

  return (
    <button
      type="button"
      draggable
      onDragStart={onDragStart}
      onClick={() => togglePanel(side, id)}
      aria-current={active ? 'page' : undefined}
      aria-label={label}
      title={label}
      className={cn(
        'relative flex size-10 items-center justify-center rounded-xs transition-colors',
        // Left accent stripe on the active item, mirrored on the right
        // dock so the affordance reads naturally on both sides.
        side === 'left'
          ? 'before:absolute before:top-1.5 before:bottom-1.5 before:left-0 before:w-0.5 before:rounded-r-sm before:bg-primary before:transition-opacity'
          : 'before:absolute before:top-1.5 before:bottom-1.5 before:right-0 before:w-0.5 before:rounded-l-sm before:bg-primary before:transition-opacity',
        active
          ? 'bg-primary/15 text-primary before:opacity-100'
          : 'cursor-pointer text-muted-foreground hover:bg-primary/10 hover:text-primary before:opacity-0',
        entry.disabled && 'opacity-60',
      )}
    >
      <Icon className="size-5" />
    </button>
  );
}

