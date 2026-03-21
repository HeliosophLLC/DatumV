import { proxy, subscribe } from 'valtio';
import { api } from '@/api';
import { settingsState } from './settings';

// Workspace view: what fills the main editor area between the docks.
// Settings is a workspace view, not a dock panel — it lives at the bottom
// of the left dock as a pinned icon, and clicking it swaps the workspace
// to SettingsView. The other docked icons open *side panels* alongside
// the workspace without unmounting the query editor.
export type WorkspaceView = 'query' | 'settings';

// Every icon that can live on a dock. Order in DockState.left / .right
// arrays is the user's visible ordering. Settings is intentionally not
// listed — it's a workspace toggle pinned to the left dock by the
// renderer rather than draggable inventory.
export type PanelId = 'chat' | 'catalog' | 'procedures' | 'projects';
export type DockSide = 'left' | 'right';

export const ALL_PANEL_IDS: readonly PanelId[] = [
  'chat',
  'catalog',
  'procedures',
  'projects',
];

interface DockState {
  // Ordered icon list per side. Settings is never present in either list.
  left: PanelId[];
  right: PanelId[];
  // The currently-expanded panel on each side, or null when nothing is
  // open. At most one panel per side at a time — clicking a different
  // icon on the same side replaces the open panel.
  openLeft: PanelId | null;
  openRight: PanelId | null;
  // What's rendered in the workspace area between the two docks.
  workspaceView: WorkspaceView;
  // Set once after the first settings refresh seeds the dock layout, so
  // subsequent persistence patches don't fight with the initial fetch.
  hydrated: boolean;
}

// Defaults match the server-side defaults in LocalSettingsService —
// every icon starts on the left, right dock empty, nothing open. The
// values here are only visible for ~50ms during the initial fetch.
export const navState = proxy<DockState>({
  left: [...ALL_PANEL_IDS],
  right: [],
  openLeft: null,
  openRight: null,
  workspaceView: 'query',
  hydrated: false,
});

// Whitelist for sanitizing values coming off the wire — settings.json
// from a future version might carry panel ids this build doesn't know,
// so we drop anything unrecognised rather than render broken icons.
const KNOWN_IDS: ReadonlySet<string> = new Set(ALL_PANEL_IDS);
function sanitize(ids: readonly string[] | undefined): PanelId[] {
  if (!ids) return [];
  const out: PanelId[] = [];
  const seen = new Set<string>();
  for (const id of ids) {
    if (!KNOWN_IDS.has(id) || seen.has(id)) continue;
    seen.add(id);
    out.push(id as PanelId);
  }
  return out;
}

// Seed dock state from the freshly-fetched settings document. Called
// once at startup from state/settings.ts after refreshSettings resolves.
export function hydrateDockFromSettings(): void {
  const left = sanitize(settingsState.dockLeftItems);
  const right = sanitize(settingsState.dockRightItems);

  // Deduplicate across sides — a corrupted document with the same id on
  // both sides keeps it where it appeared first (left wins).
  const leftSet = new Set(left);
  const cleanRight = right.filter((id) => !leftSet.has(id));

  navState.left = left;
  navState.right = cleanRight;
  navState.openLeft = clampOpen(settingsState.openLeftPanel, left);
  navState.openRight = clampOpen(settingsState.openRightPanel, cleanRight);
  navState.hydrated = true;
}

function clampOpen(
  candidate: string | null | undefined,
  side: readonly PanelId[],
): PanelId | null {
  if (!candidate) return null;
  return side.includes(candidate as PanelId) ? (candidate as PanelId) : null;
}

// ──────────────────────── Actions ────────────────────────

export function setWorkspaceView(view: WorkspaceView): void {
  navState.workspaceView = view;
}

// Toggle the panel for `id` on its current side. If the panel isn't on
// the requested side (`side` arg), this is a no-op — the caller should
// have used `movePanel` first. Clicking the icon that's already open
// closes the panel; clicking any other icon on the same side opens it
// (replacing whatever was open).
export function togglePanel(side: DockSide, id: PanelId): void {
  const list = side === 'left' ? navState.left : navState.right;
  if (!list.includes(id)) return;
  if (side === 'left') {
    navState.openLeft = navState.openLeft === id ? null : id;
  } else {
    navState.openRight = navState.openRight === id ? null : id;
  }
}

// Close the panel on `side` regardless of which id is open. Used by the
// SidePanelHost close button.
export function closePanel(side: DockSide): void {
  if (side === 'left') {
    navState.openLeft = null;
  } else {
    navState.openRight = null;
  }
}

// Move `id` from its current dock to `toSide` at index `targetIndex`.
// No-op if the icon isn't currently docked anywhere. If the move closes
// the source side's open panel, that panel collapses. The destination
// dock does NOT auto-open the moved panel — the user clicks to open.
export function movePanel(
  id: PanelId,
  toSide: DockSide,
  targetIndex?: number,
): void {
  const fromSide: DockSide | null = navState.left.includes(id)
    ? 'left'
    : navState.right.includes(id)
      ? 'right'
      : null;
  if (fromSide === null) return;

  // Remove from source.
  if (fromSide === 'left') {
    navState.left = navState.left.filter((x) => x !== id);
    if (navState.openLeft === id) navState.openLeft = null;
  } else {
    navState.right = navState.right.filter((x) => x !== id);
    if (navState.openRight === id) navState.openRight = null;
  }

  // Insert into destination. Already-present (same-side reorder)
  // collapses to a noop here since the filter above already removed it.
  const dest = toSide === 'left' ? navState.left : navState.right;
  const idx =
    targetIndex === undefined || targetIndex < 0 || targetIndex > dest.length
      ? dest.length
      : targetIndex;
  const next = [...dest];
  next.splice(idx, 0, id);
  if (toSide === 'left') {
    navState.left = next;
  } else {
    navState.right = next;
  }
}

// ──────────────────────── Persistence ────────────────────────

// Debounced PATCH /api/settings — coalesces a flurry of dock edits
// (drag-drop reorders, rapid icon clicks) into one network call. We
// don't await the response; the in-memory state is the source of truth
// and a transient failure just means the next reload starts from the
// old layout.
let saveTimer: number | null = null;
const SAVE_DEBOUNCE_MS = 250;

function scheduleSave(): void {
  if (!navState.hydrated) return;
  if (saveTimer !== null) window.clearTimeout(saveTimer);
  saveTimer = window.setTimeout(() => {
    saveTimer = null;
    void saveNow();
  }, SAVE_DEBOUNCE_MS);
}

async function saveNow(): Promise<void> {
  try {
    await api.settings.patch({
      dockLeftItems: [...navState.left],
      dockRightItems: [...navState.right],
      openLeftPanel: navState.openLeft ?? undefined,
      openRightPanel: navState.openRight ?? undefined,
      clearOpenLeftPanel: navState.openLeft === null,
      clearOpenRightPanel: navState.openRight === null,
    });
  } catch (err) {
    console.error('[nav] dock persist failed', err);
  }
}

// Wire persistence: every dock-affecting mutation re-schedules a save.
// We subscribe once at module load; the hydrated gate inside
// scheduleSave guards against firing for the initial hydration assign.
subscribe(navState, () => {
  scheduleSave();
});
