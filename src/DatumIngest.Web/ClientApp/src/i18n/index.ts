import i18next from 'i18next';
import ICU from 'i18next-icu';
import { initReactI18next } from 'react-i18next';
import enCommon from './locales/en/common.json';
import enHome from './locales/en/home.json';

// Adding a locale = (1) ship a folder under locales/<tag>/, (2) add an entry
// here, (3) extend `resources` below. The active locale is matched against
// this list; anything unrecognised falls back to `FallbackLocale`.
export const SupportedLocales = ['en'] as const;
export type SupportedLocale = (typeof SupportedLocales)[number];
export const FallbackLocale: SupportedLocale = 'en';

const resources = {
  en: { common: enCommon, home: enHome },
} as const;

i18next
  .use(ICU)
  .use(initReactI18next)
  .init({
    resources,
    lng: FallbackLocale,
    fallbackLng: FallbackLocale,
    defaultNS: 'common',
    ns: ['common', 'home'],
    // React already escapes interpolated values.
    interpolation: { escapeValue: false },
    returnNull: false,
  });

// Settings stores a BCP 47 tag or the sentinel 'system'. Resolve to a
// concrete supported locale: explicit tag → exact then base-language
// match; 'system' → walk navigator.languages in priority order.
export function resolveLocale(setting: string): SupportedLocale {
  if (setting !== 'system') {
    return matchSupported(setting) ?? FallbackLocale;
  }
  const candidates = navigator.languages?.length ? navigator.languages : [navigator.language];
  for (const candidate of candidates) {
    const match = matchSupported(candidate);
    if (match) return match;
  }
  return FallbackLocale;
}

function matchSupported(tag: string | undefined): SupportedLocale | null {
  if (!tag) return null;
  const normalized = tag.toLowerCase();
  const exact = SupportedLocales.find((l) => l === normalized);
  if (exact) return exact;
  const base = normalized.split('-')[0];
  return SupportedLocales.find((l) => l === base) ?? null;
}

export default i18next;
