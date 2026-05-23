import { proxy, subscribe } from 'valtio';
import { host } from '@/host';
import { settingsState, updateSettings, type ThemePreference } from './settings';

// Theme is a *derived* state — preference lives in settingsState (server-backed),
// resolution combines preference + OS preference. Subscribing to settingsState
// keeps `resolved` and the <html> class in sync without the component layer
// touching API calls (see feedback_api_calls_in_state memory).
export type ResolvedTheme = 'light' | 'dark';

interface ThemeState {
  resolved: ResolvedTheme;
}

function osPrefersDark(): boolean {
  return window.matchMedia('(prefers-color-scheme: dark)').matches;
}

function resolve(preference: ThemePreference): ResolvedTheme {
  if (preference === 'system') return osPrefersDark() ? 'dark' : 'light';
  return preference === 'dark' ? 'dark' : 'light';
}

function applyToDocument(resolved: ResolvedTheme): void {
  document.documentElement.classList.toggle('dark', resolved === 'dark');
}

// Cache the last resolved theme in localStorage so subsequent window opens
// (especially dialog child windows) can paint in the correct theme on the
// very first frame instead of flashing OS-preference → user-preference
// once the settings fetch lands. Same origin, so dialog BrowserWindows
// share this storage with the main window.
const CACHED_THEME_KEY = 'datumv.resolvedTheme';

function readCachedTheme(): ResolvedTheme | null {
  try {
    const v = localStorage.getItem(CACHED_THEME_KEY);
    return v === 'dark' || v === 'light' ? v : null;
  } catch {
    return null;
  }
}

function writeCachedTheme(resolved: ResolvedTheme): void {
  try {
    localStorage.setItem(CACHED_THEME_KEY, resolved);
  } catch {
    /* private mode / quota — best-effort */
  }
}

// Prefer the cached value (set during the previous session) so the first
// paint matches what the user last saw. Falls back to OS preference on a
// truly cold start, which is bounded by the settings fetch latency.
const initialResolved: ResolvedTheme =
  readCachedTheme() ?? (osPrefersDark() ? 'dark' : 'light');
applyToDocument(initialResolved);

export const themeState = proxy<ThemeState>({
  resolved: initialResolved,
});

// Publish the resolved theme to Electron main so the loader page
// (splash / welcome — different origin, no localStorage handoff) can
// paint in the user's chosen scheme on the next load. Main persists
// it across launches.
host.setHostTheme(initialResolved);

// React to server-side preference changes (refreshSettings, updateSettings).
subscribe(settingsState, () => {
  themeState.resolved = resolve(settingsState.theme);
  applyToDocument(themeState.resolved);
  writeCachedTheme(themeState.resolved);
  host.setHostTheme(themeState.resolved);
});

// React to OS-level changes while preference is 'system'.
window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
  if (settingsState.theme !== 'system') return;
  themeState.resolved = osPrefersDark() ? 'dark' : 'light';
  applyToDocument(themeState.resolved);
  writeCachedTheme(themeState.resolved);
  host.setHostTheme(themeState.resolved);
});

// Single mutator. Server is the source of truth; the subscribe above
// propagates back into themeState once settingsState updates.
export function setTheme(preference: ThemePreference): Promise<void> {
  return updateSettings({ theme: preference });
}
