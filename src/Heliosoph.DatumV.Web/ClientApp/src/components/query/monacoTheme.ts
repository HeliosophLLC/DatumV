// Resolves the user's theme pref ('system' | 'light' | 'dark') to a
// concrete Monaco theme name. Lifted out of the view component so both
// the root view and any per-leaf editor can share the same resolution
// without re-implementing the system detection.

export function resolveMonacoTheme(pref: string): string {
  if (pref === 'dark') return 'vs-dark';
  if (pref === 'light') return 'vs';
  if (
    typeof document !== 'undefined' &&
    document.documentElement.classList.contains('dark')
  ) {
    return 'vs-dark';
  }
  return 'vs';
}
