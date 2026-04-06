import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Menu } from '@base-ui/react/menu';
import {
  BookOpen,
  Boxes,
  ChevronLeft,
  ChevronRight,
  FunctionSquare,
  Plus,
  Settings as SettingsIcon,
  SquareCode,
  X,
} from 'lucide-react';
import {
  panesState,
  openTab,
  closeTab,
  selectTab,
  renameTab,
  moveTab,
  findLeaf,
  importTabIntoLeaf,
  type TabKind,
} from '@/state/tabs';
import { executionsState } from '@/state/execution';
import { serializeFunctionForm } from '@/state/functionForm';
import {
  TAB_DRAG_MIME,
  parseTabDragData,
  writeTabDragData,
  type TabDragPayload,
} from './tabDrag';
import {
  isCrossWindowDrop,
  notifySourceToRemove,
  tearOutTabIfNoDrop,
} from './tearOut';
import { cn } from '@/lib/utils';

// VSCode-style tab strip, scoped to a single leaf pane. Click to select,
// × to close, double-click to rename. Tabs are draggable; the strip is
// also a drop target so users can reorder within a leaf or move tabs in
// from another leaf.

export function TabStrip({ leafId }: { leafId: string }) {
  const { t } = useTranslation('query');
  useSnapshot(panesState); // re-render on any tab change
  useSnapshot(executionsState); // re-render when any tab's run status changes
  const leaf = findLeaf(panesState.root, leafId);

  // Hooks must run before any early return so render order stays stable
  // when the leaf disappears mid-flight (cross-window move can null it
  // out for one render).
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const [overflowing, setOverflowing] = useState(false);
  const [canLeft, setCanLeft] = useState(false);
  const [canRight, setCanRight] = useState(false);

  // Track overflow + scroll-edge state so the chevrons can render
  // disabled at the rails. ResizeObserver catches container shrinks
  // (window resize, drag-handle on the surrounding ResizablePanel);
  // the tab-count dep re-arms the observer reading after tabs are
  // added/removed (changes scrollWidth without firing scroll events).
  const tabCount = leaf?.tabs.length ?? 0;
  const activeTabId = leaf?.activeTabId ?? null;

  // Scroll a newly-active tab into view if it's offscreen. The `+`
  // button always lands a new tab at the end with activeTabId set, so
  // this effect catches that case; it also catches programmatic
  // selectTab() calls (e.g. cross-window drop activates the imported
  // tab). `inline: 'nearest'` is the key — it scrolls horizontally
  // only when the chip is partly/fully outside the viewport, no-op
  // when already visible. `block: 'nearest'` prevents accidental
  // vertical scrolling of the page.
  useEffect(() => {
    const el = scrollRef.current;
    if (!el || !activeTabId) return;
    const chip = el.querySelector<HTMLElement>(`[data-tab-id="${CSS.escape(activeTabId)}"]`);
    if (!chip) return; // pinned chips (Models) live outside the scroll container
    chip.scrollIntoView({ inline: 'nearest', block: 'nearest', behavior: 'smooth' });
  }, [activeTabId, tabCount]);

  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const update = () => {
      // +1 absorbs sub-pixel rounding; without it we'd flicker overflow
      // on/off at zoom levels where scrollWidth == clientWidth + 0.4.
      const overflow = el.scrollWidth > el.clientWidth + 1;
      setOverflowing(overflow);
      setCanLeft(el.scrollLeft > 0);
      setCanRight(overflow && el.scrollLeft + el.clientWidth < el.scrollWidth - 1);
    };
    update();
    el.addEventListener('scroll', update);
    const ro = new ResizeObserver(update);
    ro.observe(el);
    return () => {
      el.removeEventListener('scroll', update);
      ro.disconnect();
    };
  }, [tabCount]);

  function onWheel(e: React.WheelEvent<HTMLDivElement>) {
    const el = scrollRef.current;
    if (!el) return;
    // No horizontal overflow → let the wheel bubble so a vertical
    // scroll over the strip still scrolls the page when there's nothing
    // to scroll horizontally.
    if (el.scrollWidth <= el.clientWidth) return;
    // deltaX (trackpad horizontal / Shift+wheel) and deltaY (mouse
    // wheel) both feed into horizontal scroll. The user shouldn't have
    // to hold Shift just to navigate tabs.
    const delta = e.deltaX || e.deltaY;
    if (delta === 0) return;
    e.preventDefault();
    el.scrollLeft += delta;
  }

  function scrollByPage(direction: 1 | -1) {
    const el = scrollRef.current;
    if (!el) return;
    // ~70% page step keeps a strip of overlap so the user keeps context
    // on which tabs they were just looking at.
    el.scrollBy({ left: direction * el.clientWidth * 0.7, behavior: 'smooth' });
  }

  if (!leaf) return null;

  // No bottom border on the strip — the active tab paints `bg-editor`
  // to merge into the Monaco surface below, and inactive tabs / the
  // empty right-side gap pick up enough separation from the strip's
  // own `bg-background` against the editor's lighter colour. A
  // hairline border here would always cut across the active tab —
  // `overflow-x-auto` on the inner row clips the pseudo-element we'd
  // otherwise use to overlay it.
  // Pinned tabs (Models) get their own slot outside the scrollable row
  // so they stay visible even when the user has filled the strip. We
  // iterate `leaf.tabs` and pick by `pinned` rather than splitting the
  // array — keeps the `index` passed to each chip aligned with the
  // backing `leaf.tabs[]` indices that moveTab/closeTab/onDrop expect.
  const renderChip = (tab: typeof leaf.tabs[number], index: number) => {
    const exec = executionsState.byTabId[tab.id];
    const isStreaming = exec?.status === 'streaming';
    return (
      <TabChip
        key={tab.id}
        leafId={leafId}
        id={tab.id}
        index={index}
        title={tab.title}
        kind={tab.kind}
        sql={tab.sql}
        editorSize={tab.editorSize}
        dirty={tab.dirty}
        pinned={tab.pinned === true}
        active={tab.id === leaf.activeTabId}
        isStreaming={isStreaming}
        renameLabel={t('renameTab')}
        renamePromptLabel={t('renamePrompt')}
        closeLabel={t('closeTab')}
      />
    );
  };

  return (
    <div className="bg-background flex h-9 shrink-0 items-stretch">
      {/* Left rail: pinned tabs (Models) live here, outside the
          scrollable region so they're always visible regardless of how
          many other tabs the user has open. Rendered as icon-only chips
          (title shows on hover). */}
      {leaf.tabs.map((tab, index) => (tab.pinned ? renderChip(tab, index) : null))}
      {overflowing && (
        <ChevronButton
          direction="prev"
          enabled={canLeft}
          onClick={() => scrollByPage(-1)}
          label={t('scrollPrev')}
        />
      )}
      {/* `overflow-x-auto` alone would also expose a vertical scrollbar:
          CSS promotes the other axis to `auto` whenever one is set, and
          the horizontal scrollbar's height pushes the chips a few pixels
          over. We scroll horizontally on wheel/trackpad but hide both
          scrollbars (VS Code-style) — `overflow-y-hidden` kills the
          y-promotion; the arbitrary `scrollbar-width: none` + the
          WebKit pseudo-element handle the visible bar across Chromium
          (Electron) and any preview-in-browser cases. */}
      <div
        ref={scrollRef}
        onWheel={onWheel}
        className="flex flex-1 items-stretch overflow-x-auto overflow-y-hidden [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
      >
        {leaf.tabs.map((tab, index) => (tab.pinned ? null : renderChip(tab, index)))}
        {/* Trailing filler paints the strip's separator line across the
            empty area to the right of the last tab. flex-1 with default
            shrink so it collapses to 0 when tabs overflow horizontally
            — the scrollable row then fills the width on its own and the
            filler is never seen. Drops past the last tab fall through
            to the leaf body's center-zone handler, which appends to
            the leaf — same end result as the old EndSentinel. */}
        <div className="border-border flex-1 border-b" />
      </div>
      {overflowing && (
        <ChevronButton
          direction="next"
          enabled={canRight}
          onClick={() => scrollByPage(1)}
          label={t('scrollNext')}
        />
      )}
      {/* Right rail: + button always visible. Lives outside the
          scrollable region so even an overflowed strip keeps "new tab"
          one click away — matches VS Code's behavior. */}
      <NewTabMenu leafId={leafId} />
    </div>
  );
}

function ChevronButton({
  direction,
  enabled,
  onClick,
  label,
}: {
  direction: 'prev' | 'next';
  enabled: boolean;
  onClick: () => void;
  label: string;
}) {
  const Icon = direction === 'prev' ? ChevronLeft : ChevronRight;
  // Always rendered (alongside the opposite-side chevron) whenever the
  // strip overflows — disabled-state is purely visual. Anti-flicker:
  // toggling the buttons in and out of the DOM as the user scrolls past
  // the rails would change the scrollable area's clientWidth, which
  // could re-trigger the overflow threshold and cause oscillation.
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={!enabled}
      aria-label={label}
      title={label}
      className={cn(
        'border-border flex shrink-0 items-center justify-center border-b px-1.5 transition-colors',
        // Vertical separators that anchor the chevron between its
        // neighbours: left chevron just needs a right border (between
        // it and the scrollable region); right chevron needs both, to
        // separate it from the scrollable region on the left and the
        // + button on the right.
        direction === 'prev' ? 'border-r' : 'border-l border-r',
        enabled
          ? 'cursor-pointer text-muted-foreground hover:bg-muted hover:text-foreground'
          : 'cursor-default text-muted-foreground/30',
      )}
    >
      <Icon className="size-4" />
    </button>
  );
}

function TabChip({
  leafId,
  id,
  index,
  title,
  kind,
  sql,
  editorSize,
  dirty,
  pinned,
  active,
  isStreaming,
  renameLabel,
  renamePromptLabel,
  closeLabel,
}: {
  leafId: string;
  id: string;
  index: number;
  title: string;
  kind: TabKind;
  sql: string;
  editorSize: number | undefined;
  dirty: boolean;
  pinned: boolean;
  active: boolean;
  isStreaming: boolean;
  renameLabel: string;
  renamePromptLabel: string;
  closeLabel: string;
}) {
  const { t: tModels } = useTranslation('models');
  const { t: tSettingsNs } = useTranslation('settings');
  const { t: tDocs } = useTranslation('docs');
  // 'before' = drop indicator on the left edge; 'after' = right edge.
  // null while no drag is over this chip.
  const [dropSide, setDropSide] = useState<'before' | 'after' | null>(null);

  function onSelect() {
    selectTab(id);
  }

  function onRename() {
    if (pinned) return;
    const next = window.prompt(renamePromptLabel, title);
    if (next === null) return;
    renameTab(id, next);
  }

  function onClose(e: React.MouseEvent) {
    e.stopPropagation();
    closeTab(id);
  }

  function onMouseDown(e: React.MouseEvent) {
    // Middle-click closes the tab (browser-style). Pinned tabs ignore
    // it (closeTab itself short-circuits, but suppressing the autoscroll
    // cursor swap matters even when the close is a no-op).
    if (e.button === 1) {
      e.preventDefault();
      e.stopPropagation();
      if (!pinned) closeTab(id);
    }
  }

  // dragstart-time payload. Captured here so `onDragEnd` below has the
  // full tab content even if the user dragged so fast that the tab
  // disappeared from state mid-flight (cross-window receive runs the
  // tab through `closeTab` before our local dragend fires).
  const dragPayloadRef = useRef<TabDragPayload | null>(null);

  function onDragStart(e: React.DragEvent) {
    // Pinned tabs (Models) can't be dragged — abort the drag instead of
    // populating a payload the import-time guards would refuse anyway.
    // `draggable={!pinned}` on the chip element below is the primary
    // gate; this is a defense in case a stylesheet or future change
    // re-enables the draggable attribute on a pinned chip.
    if (pinned) {
      e.preventDefault();
      return;
    }
    // SYNCHRONOUS. The HTML5 spec freezes dataTransfer after dragstart's
    // synchronous body returns; any await before writeTabDragData would
    // post the writes past the freeze and a cross-window drop would see
    // empty dataTransfer. host.windowId() is a sync accessor (preload
    // pre-resolves it on load) — see electron/preload.ts.
    const host = window.electronHost;
    const sourceWindowId = host?.isElectron ? host.windowId() : null;
    // `kind` is widened to include 'models', but pinned tabs are gated
    // out above — a 'models' value can't reach here. Narrow defensively
    // so the wire payload's type stays 'sql' | 'function'.
    const dragKind: 'sql' | 'function' = kind === 'function' ? 'function' : 'sql';
    // Snapshot the form-state slice so a cross-window drop can rebuild
    // the function tab on the destination — null for SQL tabs and for
    // function tabs the user hasn't interacted with.
    const functionForm =
      dragKind === 'function' ? serializeFunctionForm(id) : null;
    const payload: TabDragPayload = {
      fromLeafId: leafId,
      tabId: id,
      sourceWindowId,
      // Captured at dragstart; if the run finishes mid-drag the
      // destination still sees the stale flag, which is the safer
      // direction (refuse rather than orphan an in-flight stream).
      isRunning: isStreaming,
      tab: {
        id,
        title,
        kind: dragKind,
        sql,
        editorSize,
        functionForm: functionForm ?? undefined,
      },
    };
    dragPayloadRef.current = payload;
    writeTabDragData(e.dataTransfer, payload);
  }

  function onDragEnd(e: React.DragEvent) {
    const payload = dragPayloadRef.current;
    dragPayloadRef.current = null;
    if (!payload) return;
    // `dropEffect === 'none'` is the HTML5 signal that no drop target
    // accepted the drag. With our in-window + cross-window drops both
    // calling `preventDefault` + `dropEffect = 'move'`, this only
    // remains 'none' when the user released outside every drop zone
    // — exactly the tear-out trigger.
    if (e.dataTransfer.dropEffect !== 'none') return;
    void tearOutTabIfNoDrop(payload);
  }

  function hasTabPayload(e: React.DragEvent): boolean {
    return e.dataTransfer.types.includes(TAB_DRAG_MIME);
  }

  function onDragOver(e: React.DragEvent) {
    if (!hasTabPayload(e)) return;
    e.preventDefault();
    e.stopPropagation();
    e.dataTransfer.dropEffect = 'move';
    const rect = e.currentTarget.getBoundingClientRect();
    const side = e.clientX < rect.left + rect.width / 2 ? 'before' : 'after';
    setDropSide(side);
  }

  function onDragLeave() {
    setDropSide(null);
  }

  function onDrop(e: React.DragEvent) {
    if (!hasTabPayload(e)) return;
    e.preventDefault();
    e.stopPropagation();
    const payload = parseTabDragData(e.dataTransfer);
    const side = dropSide;
    setDropSide(null);
    if (!payload) return;
    // Insert position depends on which half of the chip the cursor was
    // over. `moveTab` adjusts internally when source-and-target are the
    // same leaf so the rendered position matches the indicator.
    const insertIndex = side === 'after' ? index + 1 : index;
    if (isCrossWindowDrop(payload)) {
      // Refuse cross-window receive while the source's query is still
      // streaming — the move would orphan the in-flight request in
      // the source's renderer. Drop is silently no-op; source keeps
      // the tab.
      if (payload.isRunning) return;
      importTabIntoLeaf(leafId, payload.tab, insertIndex);
      notifySourceToRemove(payload);
    } else {
      moveTab(payload.tabId, leafId, insertIndex);
    }
  }

  const displayTitle =
    kind === 'models'
      ? tModels('title')
      : kind === 'settings'
        ? tSettingsNs('title')
        : kind === 'docs'
          ? tDocs('title')
          : title;

  return (
    <div
      role="tab"
      aria-selected={active}
      data-tab-id={id}
      draggable={!pinned}
      onDragStart={onDragStart}
      onDragEnd={onDragEnd}
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
      onClick={onSelect}
      onMouseDown={onMouseDown}
      onDoubleClick={onRename}
      title={pinned ? displayTitle : renameLabel}
      className={cn(
        'group border-border relative flex shrink-0 cursor-pointer items-center gap-2 border-r px-3 py-1.5 text-sm transition-colors',
        active
          ? cn(
              // Match the Monaco editor surface so the active tab
              // visually flows into the editor below. No border-b
              // here — the strip's separator line is painted by the
              // inactive tabs / sentinel / + button / trailing filler,
              // each carrying their own bottom border. The active tab
              // intentionally omits it so there's no hairline between
              // tab and editor.
              'bg-editor text-foreground',
              // Top accent — primary line at the top so the tab reads
              // as "front edge of the editor pane".
              "before:absolute before:inset-x-0 before:top-0 before:h-0.5 before:bg-primary before:content-['']",
            )
          : 'border-b border-border text-muted-foreground hover:bg-muted/40 hover:text-foreground',
      )}
    >
      {dropSide === 'before' && (
        <div className="bg-primary pointer-events-none absolute inset-y-0 left-0 w-0.5" />
      )}
      {dropSide === 'after' && (
        <div className="bg-primary pointer-events-none absolute inset-y-0 right-0 w-0.5" />
      )}
      {kind === 'models' ? (
        // Pinned tabs (Models / Settings / Docs) carry their own
        // distinctive icon — clicking the chip just selects the tab.
        // Run-style affordances now live in the leaf's vertical toolbar.
        <Boxes className="text-primary size-3.5 shrink-0" />
      ) : kind === 'settings' ? (
        <SettingsIcon className="text-primary size-3.5 shrink-0" />
      ) : kind === 'docs' ? (
        <BookOpen className="text-primary size-3.5 shrink-0" />
      ) : kind === 'function' ? (
        <FunctionSquare
          className={cn(
            'size-3.5 shrink-0',
            isStreaming ? 'text-primary animate-pulse' : 'text-muted-foreground',
          )}
        />
      ) : (
        <SquareCode
          className={cn(
            'size-3.5 shrink-0',
            isStreaming ? 'text-primary animate-pulse' : 'text-muted-foreground',
          )}
        />
      )}
      {!pinned && (
        // Pinned chips (Models) are icon-only — title shows via the
        // chip's `title` attribute on hover.
        <span className="max-w-[160px] truncate">
          {displayTitle}
          {dirty ? ' •' : ''}
        </span>
      )}
      {!pinned && (
        <button
          type="button"
          onClick={onClose}
          aria-label={closeLabel}
          title={closeLabel}
          className={cn(
            'cursor-pointer text-muted-foreground hover:bg-muted hover:text-foreground rounded-xs p-0.5 opacity-0 transition-opacity',
            'group-hover:opacity-100',
            active && 'opacity-100',
          )}
        >
          <X className="size-3" />
        </button>
      )}
    </div>
  );
}

/**
 * The `+` control: a dropdown menu offering "SQL Query" (the legacy
 * behaviour of single-click → new SQL tab) and "Execute Function" (a
 * kind-driven form for invoking a scalar function). The menu is keyboard
 * accessible (Enter / Space opens, arrow keys navigate, Esc closes) via
 * the Base UI Menu primitive — same access semantics the rest of the
 * app's menus pick up once they're wired through this component.
 */
function NewTabMenu({ leafId }: { leafId: string }) {
  const { t } = useTranslation('query');

  function openWithKind(kind: TabKind) {
    openTab('', leafId, kind);
  }

  return (
    <Menu.Root>
      <Menu.Trigger
        aria-label={t('newTab')}
        title={t('newTab')}
        className={cn(
          'text-muted-foreground hover:bg-muted hover:text-foreground',
          'border-border flex shrink-0 cursor-pointer items-center justify-center border-b px-3 transition-colors',
          'data-[popup-open]:bg-muted data-[popup-open]:text-foreground',
        )}
      >
        <Plus className="size-4" />
      </Menu.Trigger>
      <Menu.Portal>
        {/* The Positioner is the element Floating UI applies
            `position: fixed` to. Without an explicit z-index here, the
            Positioner creates its own stacking context at the body's
            default level — beaten by the ResizableHandle's `z-20` even
            though the Popup's own `z-50` is numerically higher. Putting
            z-50 on the Positioner roots the whole popup at z-50 at body
            level, so the dropdown lands above the editor/chat divider. */}
        <Menu.Positioner side="bottom" align="start" sideOffset={4} className="z-50">
          <Menu.Popup
            className={cn(
              'bg-popover text-popover-foreground border-border min-w-48',
              'rounded-md border p-1 shadow-md outline-none',
            )}
          >
            <Menu.Item
              onClick={() => openWithKind('sql')}
              className={cn(
                'flex cursor-pointer items-center gap-2 rounded-sm px-2 py-1.5 text-sm',
                'outline-none data-[highlighted]:bg-muted data-[highlighted]:text-foreground',
              )}
            >
              <SquareCode className="size-4" />
              {t('newTabSql')}
            </Menu.Item>
            <Menu.Item
              onClick={() => openWithKind('function')}
              className={cn(
                'flex cursor-pointer items-center gap-2 rounded-sm px-2 py-1.5 text-sm',
                'outline-none data-[highlighted]:bg-muted data-[highlighted]:text-foreground',
              )}
            >
              <FunctionSquare className="size-4" />
              {t('newTabFunction')}
            </Menu.Item>
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}

