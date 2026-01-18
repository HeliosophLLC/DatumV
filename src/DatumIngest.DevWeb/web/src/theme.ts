// Theme persistence and Monaco theme synchronisation. The storage key
// is shared with the legacy STORE constant in main.ts; literal kept here
// to keep this module dependency-free.

const THEME_STORAGE_KEY = 'datum.devweb.theme';

export type Theme = 'light' | 'dark';

export function loadTheme(): Theme {
  const saved = localStorage.getItem(THEME_STORAGE_KEY);
  return saved === 'light' ? 'light' : 'dark';
}

export function applyTheme(theme: Theme): void {
  document.documentElement.setAttribute('data-theme', theme);
  const toggle = document.getElementById('theme-toggle');
  if (toggle) toggle.textContent = theme === 'dark' ? '☾' : '☼';
  if ((window as any).monaco) {
    (window as any).monaco.editor.setTheme(theme === 'dark' ? 'vs-dark' : 'vs');
  }
}

export function toggleTheme(): void {
  const current = document.documentElement.getAttribute('data-theme');
  const next: Theme = current === 'dark' ? 'light' : 'dark';
  localStorage.setItem(THEME_STORAGE_KEY, next);
  applyTheme(next);
}
