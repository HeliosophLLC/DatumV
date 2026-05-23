// Ships translated copies of every user-visible string the Electron
// main process needs (folder-picker titles, error-dialog text, splash
// status during catalog swap) so main never imports i18next directly.
// Same pattern as state/menu.ts uses for the menu template: publish on
// startup, re-publish on locale change.
//
// Note: the very first splash flash on app launch happens BEFORE the
// renderer has loaded i18next, so main keeps English defaults for that
// gap. Every subsequent swap uses the user's locale because the
// renderer publishes once mounted.

import i18next from 'i18next';
import { host } from '@/host';

export interface HostStrings {
  catalog: {
    openTitle: string;
    openButtonLabel: string;
    newTitle: string;
    newButtonLabel: string;
    invalidTitle: string;
    invalidMessage: string;
    // Template with `{path}` and `{marker}` placeholders — main
    // substitutes them at error-display time since the picked path
    // isn't known until the dialog returns.
    invalidDetail: string;
  };
  splash: {
    stoppingBackend: string;
    startingBackend: string;
    loadingWorkspace: string;
  };
}

function snapshot(): HostStrings {
  // Pass keys as literals so typed-t can verify they exist in the
  // bundled resources (the `as string` casts only widen the return
  // type — the key check still runs on the literal at the call site).
  return {
    catalog: {
      openTitle: i18next.t('catalog.openTitle') as string,
      openButtonLabel: i18next.t('catalog.openButtonLabel') as string,
      newTitle: i18next.t('catalog.newTitle') as string,
      newButtonLabel: i18next.t('catalog.newButtonLabel') as string,
      invalidTitle: i18next.t('catalog.invalidTitle') as string,
      invalidMessage: i18next.t('catalog.invalidMessage') as string,
      invalidDetail: i18next.t('catalog.invalidDetail') as string,
    },
    splash: {
      stoppingBackend: i18next.t('splash.stoppingBackend') as string,
      startingBackend: i18next.t('splash.startingBackend') as string,
      loadingWorkspace: i18next.t('splash.loadingWorkspace') as string,
    },
  };
}

function publish(): void {
  host.setHostStrings(snapshot());
}

i18next.on('languageChanged', publish);
queueMicrotask(publish);
