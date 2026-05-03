import i18next from 'i18next';
import ICU from 'i18next-icu';
import { initReactI18next } from 'react-i18next';
import enCatalog from './locales/en/catalog.json';
import enChat from './locales/en/chat.json';
import enCommon from './locales/en/common.json';
import enDatasets from './locales/en/datasets.json';
import enDialogs from './locales/en/dialogs.json';
import enDocs from './locales/en/docs.json';
import enHome from './locales/en/home.json';
import enModels from './locales/en/models.json';
import enPanels from './locales/en/panels.json';
import enProcedures from './locales/en/procedures.json';
import enProjectExplorer from './locales/en/projectExplorer.json';
import enQuery from './locales/en/query.json';
import enSettings from './locales/en/settings.json';
import enStatus from './locales/en/status.json';

// Adding a locale = (1) ship a folder under locales/<tag>/, (2) add an entry
// here, (3) extend `resources` below. The active locale is matched against
// this list; anything unrecognised falls back to `FallbackLocale`.
export const SupportedLocales = ['en'] as const;
export type SupportedLocale = (typeof SupportedLocales)[number];
export const FallbackLocale: SupportedLocale = 'en';

const resources = {
  en: {
    common: enCommon,
    home: enHome,
    chat: enChat,
    models: enModels,
    datasets: enDatasets,
    query: enQuery,
    settings: enSettings,
    dialogs: enDialogs,
    panels: enPanels,
    catalog: enCatalog,
    procedures: enProcedures,
    projectExplorer: enProjectExplorer,
    docs: enDocs,
    status: enStatus,
  },
} as const;

i18next
  .use(ICU)
  .use(initReactI18next)
  .init({
    resources,
    lng: FallbackLocale,
    fallbackLng: FallbackLocale,
    defaultNS: 'common',
    ns: [
      'common',
      'home',
      'chat',
      'models',
      'datasets',
      'query',
      'settings',
      'dialogs',
      'panels',
      'catalog',
      'procedures',
      'projectExplorer',
      'docs',
      'status',
    ],
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
