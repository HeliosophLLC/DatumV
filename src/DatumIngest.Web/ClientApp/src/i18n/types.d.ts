import type enCatalog from './locales/en/catalog.json';
import type enChat from './locales/en/chat.json';
import type enCommon from './locales/en/common.json';
import type enDialogs from './locales/en/dialogs.json';
import type enDocs from './locales/en/docs.json';
import type enHome from './locales/en/home.json';
import type enModels from './locales/en/models.json';
import type enPanels from './locales/en/panels.json';
import type enProcedures from './locales/en/procedures.json';
import type enProjectExplorer from './locales/en/projectExplorer.json';
import type enQuery from './locales/en/query.json';
import type enSettings from './locales/en/settings.json';
import type enStatus from './locales/en/status.json';

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
      panels: typeof enPanels;
      catalog: typeof enCatalog;
      procedures: typeof enProcedures;
      projectExplorer: typeof enProjectExplorer;
      docs: typeof enDocs;
      status: typeof enStatus;
    };
    returnNull: false;
  }
}
