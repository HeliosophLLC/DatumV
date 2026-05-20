import { proxy, subscribe } from 'valtio';
import i18n, { resolveLocale, type SupportedLocale } from '../i18n';
import { settingsState, updateSettings } from './settings';

// Locale is a *derived* state — preference lives in settingsState
// (server-backed) as a BCP 47 tag or the sentinel 'system'. We resolve to
// a concrete supported locale and push it into i18next + <html lang=...>.
// Mirrors state/theme.ts; same valtio rule (api calls in state, not views).
interface LocaleState {
  resolved: SupportedLocale;
}

const initialResolved = resolveLocale(settingsState.locale);
i18n.changeLanguage(initialResolved);
document.documentElement.lang = initialResolved;

export const localeState = proxy<LocaleState>({
  resolved: initialResolved,
});

subscribe(settingsState, () => {
  const next = resolveLocale(settingsState.locale);
  if (next === localeState.resolved) return;
  localeState.resolved = next;
  i18n.changeLanguage(next);
  document.documentElement.lang = next;
});

// Single mutator. Server is the source of truth; the subscribe above
// propagates back into localeState once settingsState updates.
export function setLocale(locale: string): Promise<void> {
  return updateSettings({ locale });
}
