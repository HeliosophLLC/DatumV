import type enChat from './locales/en/chat.json';
import type enCommon from './locales/en/common.json';
import type enDialogs from './locales/en/dialogs.json';
import type enHome from './locales/en/home.json';
import type enModels from './locales/en/models.json';
import type enQuery from './locales/en/query.json';
import type enSettings from './locales/en/settings.json';

// Module augmentation: makes `t('window.close')` etc. type-checked and
// autocompletable. The English bundle is the canonical key set — other
// locales are validated against it at typecheck time when they're added
// to `resources` in ./index.ts.
declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'common';
    resources: {
      common: typeof enCommon;
      home: typeof enHome;
      chat: typeof enChat;
      models: typeof enModels;
      query: typeof enQuery;
      settings: typeof enSettings;
      dialogs: typeof enDialogs;
    };
    returnNull: false;
  }
}
