// Resolves the user's theme pref ('system' | 'light' | 'dark') to a
// concrete Monaco theme name. Lifted out of the view component so both
// the root view and any per-leaf editor can share the same resolution
// without re-implementing the system detection.
//
// Theme bodies are populated by the LSP grammar+theme fetch in
// `monaco/lsp.ts`; the pre-registration in `monaco/setup.ts` keeps the
// names valid before the fetch completes (Monaco falls back to the
// inherited `base` theme until `defineTheme` lands the real palette).

export const DATUMV_LIGHT_THEME = 'datumv-light';
export const DATUMV_DARK_THEME = 'datumv-dark';

export function resolveMonacoTheme(pref: string): string {
  if (pref === 'dark') return DATUMV_DARK_THEME;
  if (pref === 'light') return DATUMV_LIGHT_THEME;
  if (
    typeof document !== 'undefined' &&
    document.documentElement.classList.contains('dark')
  ) {
    return DATUMV_DARK_THEME;
  }
  return DATUMV_LIGHT_THEME;
}
