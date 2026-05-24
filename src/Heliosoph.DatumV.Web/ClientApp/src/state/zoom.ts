import { proxy } from 'valtio';
import { host } from '@/host';

// App-wide zoom kept in sync across every BrowserWindow (main SPA,
// torn-out tabs, dialog children) by writing the level to localStorage
// and reacting to 'storage' events. Same origin → all our windows
// share localStorage; the storage event fires in every *other* window
// of the origin when one of them mutates the key.
//
// The window that performs the write does NOT receive its own storage
// event, so the mutator below applies the level locally before
// writing.

const ZOOM_KEY = 'datumv.zoomLevel';

// Bound the integer level. Electron's webFrame.setZoomLevel accepts
// arbitrary numbers (factor = 1.2^level), but a tight range matches
// what Chromium's own zoom UI offers and keeps the chip readout
// sensible (≈40% … ≈249%).
const MIN_LEVEL = -5;
const MAX_LEVEL = 5;

function clamp(level: number): number {
  if (level < MIN_LEVEL) return MIN_LEVEL;
  if (level > MAX_LEVEL) return MAX_LEVEL;
  return Math.round(level);
}

function readStoredLevel(): number {
  try {
    const v = localStorage.getItem(ZOOM_KEY);
    if (v == null) return 0;
    const n = Number(v);
    return Number.isFinite(n) ? clamp(n) : 0;
  } catch {
    return 0;
  }
}

function writeStoredLevel(level: number): void {
  try {
    localStorage.setItem(ZOOM_KEY, String(level));
  } catch {
    /* private mode / quota — best-effort, in-memory state still wins */
  }
}

const initialLevel = readStoredLevel();

// Apply on module load so every newly-mounted window picks up the
// last-known zoom on the very first frame instead of flashing at 100%
// while React boots.
host.setZoomLevel(initialLevel);

export const zoomState = proxy<{ level: number }>({ level: initialLevel });

// Convert a zoom level to a display percentage. Electron's factor for
// level L is 1.2^L; round so the chip reads as a whole percent.
export function levelToPercent(level: number): number {
  return Math.round(Math.pow(1.2, level) * 100);
}

function applyLevel(level: number): void {
  const next = clamp(level);
  if (zoomState.level === next) return;
  zoomState.level = next;
  host.setZoomLevel(next);
}

export function setZoomLevel(level: number): void {
  const next = clamp(level);
  applyLevel(next);
  writeStoredLevel(next);
}

export function zoomIn(): void {
  setZoomLevel(zoomState.level + 1);
}

export function zoomOut(): void {
  setZoomLevel(zoomState.level - 1);
}

export function resetZoom(): void {
  setZoomLevel(0);
}

// Cross-window propagation. Storage events fire on every other same-
// origin window when one of them writes; the writer applies locally
// above and skips re-applying here (its setItem doesn't echo back).
window.addEventListener('storage', (event) => {
  if (event.key !== ZOOM_KEY) return;
  if (event.newValue == null) return;
  const n = Number(event.newValue);
  if (!Number.isFinite(n)) return;
  applyLevel(n);
});
