import { proxy } from 'valtio';

// 'system' follows the OS preference and updates live on OS changes.
// 'light' / 'dark' override that.
export type ThemePreference = 'light' | 'dark' | 'system';
export type ResolvedTheme = 'light' | 'dark';

interface ThemeState {
  preference: ThemePreference;
  resolved: ResolvedTheme;
}

const STORAGE_KEY = 'datum.theme';

function readStoredPreference(): ThemePreference {
  const value = localStorage.getItem(STORAGE_KEY);
  return value === 'light' || value === 'dark' || value === 'system' ? value : 'system';
}

function osPrefersDark(): boolean {
  return window.matchMedia('(prefers-color-scheme: dark)').matches;
}

function resolve(pref: ThemePreference): ResolvedTheme {
  if (pref === 'system') return osPrefersDark() ? 'dark' : 'light';
  return pref;
}

function applyToDocument(resolved: ResolvedTheme): void {
  document.documentElement.classList.toggle('dark', resolved === 'dark');
}

const initialPreference = readStoredPreference();
const initialResolved = resolve(initialPreference);
applyToDocument(initialResolved);

export const themeState = proxy<ThemeState>({
  preference: initialPreference,
  resolved: initialResolved,
});

export function setTheme(preference: ThemePreference): void {
  themeState.preference = preference;
  themeState.resolved = resolve(preference);
  localStorage.setItem(STORAGE_KEY, preference);
  applyToDocument(themeState.resolved);
}

// Track OS changes while preference is 'system'.
window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (event) => {
  if (themeState.preference !== 'system') return;
  themeState.resolved = event.matches ? 'dark' : 'light';
  applyToDocument(themeState.resolved);
});
