import { proxy, subscribe } from 'valtio';
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

// Seed with the OS preference so first paint matches the OS while settings
// are still loading. Flash only happens if the user has overridden away from
// OS preference, and is bounded by the settings fetch latency.
const initialResolved = osPrefersDark() ? 'dark' : 'light';
applyToDocument(initialResolved);

export const themeState = proxy<ThemeState>({
  resolved: initialResolved,
});

// React to server-side preference changes (refreshSettings, updateSettings).
subscribe(settingsState, () => {
  themeState.resolved = resolve(settingsState.theme);
  applyToDocument(themeState.resolved);
});

// React to OS-level changes while preference is 'system'.
window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
  if (settingsState.theme !== 'system') return;
  themeState.resolved = osPrefersDark() ? 'dark' : 'light';
  applyToDocument(themeState.resolved);
});

// Single mutator. Server is the source of truth; the subscribe above
// propagates back into themeState once settingsState updates.
export function setTheme(preference: ThemePreference): Promise<void> {
  return updateSettings({ theme: preference });
}
