import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Menu } from '@base-ui/react/menu';
import { Boxes, FunctionSquare, Play, Plus, SquareCode, Square, X } from 'lucide-react';
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
import { cancelTab, executionsState, runTab } from '@/state/execution';
import { runFunctionTab } from '@/state/functionForm';
import { resolveRunSql } from '@/state/activeEditor';
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
  if (!leaf) return null;

  // No bottom border on the strip — the active tab paints `bg-editor`
  // to merge into the Monaco surface below, and inactive tabs / the
  // empty right-side gap pick up enough separation from the strip's
  // own `bg-background` against the editor's lighter colour. A
  // hairline border here would always cut across the active tab —
  // `overflow-x-auto` on the inner row clips the pseudo-element we'd
  // otherwise use to overlay it.
  return (
    <div className="bg-background flex h-9 shrink-0 items-stretch">
      <div className="flex flex-1 items-stretch overflow-x-auto">
        {leaf.tabs.map((tab, index) => {
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
              runLabel={t('run')}
              cancelLabel={t('cancel')}
            />
          );
        })}
        {/* End-of-strip drop sentinel + new-tab menu. The sentinel
            absorbs drops past the last tab so the user can move a tab to
            the very end. The + button opens a dropdown — single-click on
            "SQL Query" matches the prior single-click-to-add-tab UX,
            "Execute Function" creates a function tab. */}
        <EndSentinel leafId={leafId} tabCount={leaf.tabs.length} />
        <NewTabMenu leafId={leafId} />

        {/* Trailing filler that paints the strip's separator line in
            the empty area to the right of the + button. flex-1 with
            default shrink so it collapses to 0 when tabs overflow
            horizontally — the scrollable row then fills the width on
            its own and the filler is never seen. */}
        <div className="border-border flex-1 border-b" />
      </div>
    </div>
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
  runLabel,
  cancelLabel,
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
  runLabel: string;
  cancelLabel: string;
}) {
  const { t: tModels } = useTranslation('models');
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

  function onPlayOrStop(e: React.MouseEvent) {
    // Don't switch tabs when clicking the play button — the user might
    // be kicking off a long-running query from a tab they don't want
    // to leave the current one for. The click does NOT propagate to
    // the tab's onSelect.
    e.stopPropagation();
    if (isStreaming) {
      cancelTab(id);
      return;
    }
    if (kind === 'function') {
      // Function tabs synthesise their request from the form state on
      // the tab; the Play button on the strip is a parity entry point
      // with the form's own Run button + Ctrl+Enter. Form-level field
      // errors get raised inside runFunctionTab.
      void runFunctionTab(id);
      return;
    }
    // resolveRunSql honours the focused editor's current selection only
    // when that editor belongs to THIS tab's leaf. So clicking the
    // Play button on a tab in leaf B while you've been editing leaf B
    // runs your selection; clicking it on leaf A's tab from leaf B's
    // editor falls back to the whole tab text. Matches Ctrl+Enter's
    // selection-aware behaviour.
    void runTab(id, resolveRunSql(sql, leafId));
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
    const payload: TabDragPayload = {
      fromLeafId: leafId,
      tabId: id,
      sourceWindowId,
      // Captured at dragstart; if the run finishes mid-drag the
      // destination still sees the stale flag, which is the safer
      // direction (refuse rather than orphan an in-flight stream).
      isRunning: isStreaming,
      tab: { id, title, kind: dragKind, sql, editorSize },
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

  const displayTitle = kind === 'models' ? tModels('title') : title;

  return (
    <div
      role="tab"
      aria-selected={active}
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
        // Pinned Models tab — no play button (nothing to run), distinct
        // icon, and no close button below.
        <Boxes className="text-primary size-3.5 shrink-0" />
      ) : (
        <button
          type="button"
          onClick={onPlayOrStop}
          aria-label={isStreaming ? cancelLabel : runLabel}
          title={isStreaming ? cancelLabel : runLabel}
          className={cn(
            'flex shrink-0 cursor-pointer items-center justify-center rounded-xs p-0.5 transition-colors',
            isStreaming
              ? 'text-destructive hover:bg-destructive/15'
              : 'text-primary hover:bg-primary/15',
          )}
        >
          {isStreaming ? (
            <Square className="size-3" />
          ) : (
            <Play className="size-3" />
          )}
        </button>
      )}
      <span className="max-w-[160px] truncate">
        {displayTitle}
        {dirty ? ' •' : ''}
      </span>
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
        <Menu.Positioner side="bottom" align="start" sideOffset={4}>
          <Menu.Popup
            className={cn(
              'bg-popover text-popover-foreground border-border z-50 min-w-48',
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

function EndSentinel({ leafId, tabCount }: { leafId: string; tabCount: number }) {
  const [hover, setHover] = useState(false);

  function hasTabPayload(e: React.DragEvent): boolean {
    return e.dataTransfer.types.includes(TAB_DRAG_MIME);
  }

  function onDragOver(e: React.DragEvent) {
    if (!hasTabPayload(e)) return;
    e.preventDefault();
    e.stopPropagation();
    e.dataTransfer.dropEffect = 'move';
    setHover(true);
  }

  function onDragLeave() {
    setHover(false);
  }

  function onDrop(e: React.DragEvent) {
    if (!hasTabPayload(e)) return;
    e.preventDefault();
    e.stopPropagation();
    const payload = parseTabDragData(e.dataTransfer);
    setHover(false);
    if (!payload) return;
    if (isCrossWindowDrop(payload)) {
      if (payload.isRunning) return; // see chip onDrop for rationale.
      importTabIntoLeaf(leafId, payload.tab, tabCount);
      notifySourceToRemove(payload);
    } else {
      moveTab(payload.tabId, leafId, tabCount);
    }
  }

  return (
    <div
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
      className="border-border relative w-2 shrink-0 border-b"
    >
      {hover && (
        <div className="bg-primary pointer-events-none absolute inset-y-0 left-0 w-0.5" />
      )}
    </div>
  );
}
