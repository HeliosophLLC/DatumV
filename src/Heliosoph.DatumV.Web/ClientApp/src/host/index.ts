// Host environment + IPC bridge. The OS comes from the Electron preload
// (process.platform) — no client-side sniffing of navigator.platform.
// The HostBridge is the seam for everything that talks to the Electron
// main process: window controls today, file pickers and notifications
// as the UI grows.

export type HostOs = 'windows' | 'macos' | 'linux' | 'unknown';

export type HostMessageHandler = (message: string) => void;

export interface RecentCatalog {
  path: string;
  displayName: string;
  lastOpenedAt: string;
}

// Update-checker status shape pushed from main on every transition.
// Mirrors the `UpdateStatus` discriminated union in electron/main.ts —
// keep both in sync.
export type UpdateStatus =
  | { kind: 'idle' }
  | { kind: 'checking' }
  | { kind: 'available'; version: string; releaseUrl: string }
  | { kind: 'not-available'; currentVersion: string }
  | { kind: 'error'; message: string };

export interface HostBridge {
  minimize(): void;
  toggleMaximize(): void;
  close(): void;
  // Subscribe to host-pushed messages. Today the only kind is
  // 'host:window.maximized' / 'host:window.normal' — translated from
  // Electron's typed window.maximizedChanged channel so existing
  // subscribers in state/window.ts keep their string-match shape.
  onMessage(handler: HostMessageHandler): void;
  // Ship a localized application-menu template to main. Plain-data
  // tree (see src/commands/menuDefinition.ts); main re-builds the
  // native Electron menu from it. Called on app start and on every
  // locale change from state/menu.ts.
  setApplicationMenu(tree: unknown): void;
  // Subscribe to native-menu click delivery. The commandId matches
  // what was assigned in the menu definition; state/menu.ts routes
  // it through the command registry.
  onMenuCommand(handler: (commandId: string) => void): void;
  // Recent-catalog management. Main owns the recents file plus the
  // backend-respawn-on-swap flow; the renderer reads recents to
  // populate the "Open Recent" submenu and kicks off swaps through
  // these calls.
  getRecentCatalogs(): Promise<RecentCatalog[]>;
  pickAndOpenCatalog(): Promise<void>;
  pickAndCreateCatalog(): Promise<void>;
  openCatalogPath(path: string): Promise<void>;
  // Ship translated dialog + splash strings to main so its native
  // dialogs and splash overlay render in the user's locale. Plain
  // data; main caches them and re-renders on each invocation.
  setHostStrings(strings: unknown): void;
  // Ship the last-resolved theme so main can pass it on the loader
  // URL (splash / welcome paint in the user's scheme rather than OS
  // preference). Called by state/theme.ts on every change.
  setHostTheme(theme: 'light' | 'dark'): void;
  // Per-window zoom controls. App-wide consistency is layered on top
  // by state/zoom.ts via a same-origin localStorage key that every
  // window (main, torn-out, dialog) observes through 'storage'
  // events.
  setZoomLevel(level: number): void;
  getZoomLevel(): number;
  // Update checker — fire-and-forget request to re-probe GitHub
  // Releases; the result arrives via the status subscription below.
  // No await; the IPC resolves as soon as main kicks off the probe.
  checkForUpdates(): void;
  // Synchronous read of the most-recent status. Useful at mount so a
  // status pushed before the renderer subscribed isn't missed.
  getUpdateStatus(): UpdateStatus;
  // Subscribe to every status transition. Returns an unsubscribe.
  onUpdateStatus(cb: (status: UpdateStatus) => void): () => void;
}

declare global {
  interface Window {
    // Exposed by electron/preload.ts via contextBridge. Always present in
    // production runs — the SPA only loads inside the Electron shell.
    electronHost: {
      isElectron: boolean;
      platform: string;
      minimize(): Promise<void>;
      toggleMaximize(): Promise<void>;
      close(): Promise<void>;
      onMaximizedChanged(cb: (maximized: boolean) => void): () => void;
      openDialog(spec: {
        requestId: string;
        kind: string;
        payload?: Record<string, unknown> | null;
        modal?: boolean;
      }): Promise<unknown>;
      resolveDialog(result: unknown): void;
      notify(opts: { title: string; body: string }): Promise<void>;
      showContextMenu(spec: {
        items: Array<
          | { id: string; label: string; accelerator?: string; enabled?: boolean }
          | { type: 'separator' }
        >;
      }): Promise<string | null>;
      showOpenDialog(options: {
        title?: string;
        defaultPath?: string;
        properties?: ReadonlyArray<
          | 'openFile'
          | 'openDirectory'
          | 'multiSelections'
          | 'showHiddenFiles'
          | 'createDirectory'
          | 'promptToCreate'
          | 'noResolveAliases'
          | 'treatPackageAsDirectory'
          | 'dontAddToRecent'
        >;
        filters?: ReadonlyArray<{ name: string; extensions: string[] }>;
      }): Promise<{ canceled: boolean; filePaths: string[] }>;
      showSaveDialog(options: {
        title?: string;
        defaultPath?: string;
        buttonLabel?: string;
        filters?: ReadonlyArray<{ name: string; extensions: string[] }>;
        properties?: ReadonlyArray<
          | 'showHiddenFiles'
          | 'createDirectory'
          | 'treatPackageAsDirectory'
          | 'showOverwriteConfirmation'
          | 'dontAddToRecent'
        >;
      }): Promise<{ canceled: boolean; filePath?: string }>;
      openExternal(url: string): Promise<void>;
      openThirdPartyNotices(): Promise<void>;
      setApplicationMenu(tree: unknown): Promise<void>;
      onMenuCommand(cb: (commandId: string) => void): () => void;

      // Catalog selection IPC — see electron/preload.ts.
      catalogGetRecents(): Promise<
        Array<{ path: string; displayName: string; lastOpenedAt: string }>
      >;
      catalogOpenPicker(): Promise<{ canceled: boolean; path?: string }>;
      catalogNewPicker(): Promise<{ canceled: boolean; path?: string }>;
      catalogOpenPath(path: string): Promise<{ canceled: boolean; path?: string }>;
      setHostStrings(strings: unknown): Promise<void>;
      setHostTheme(theme: 'light' | 'dark'): Promise<void>;
      setZoomLevel(level: number): void;
      getZoomLevel(): number;
      onSplashStatus(cb: (text: string) => void): () => void;

      // Tab tear-out IPC. See electron/preload.ts for the protocol
      // each method speaks; the renderer consumers live in the query
      // pane components (TabStrip / LeafPaneView).
      spawnTabWindow(payload: {
        seed: {
          id: string;
          title: string;
          /** Discriminator; absent for SQL tabs (the renderer defaults missing values to `'sql'`). */
          kind?: 'sql' | 'function';
          sql: string;
          editorSize?: number;
          /**
           * Function-tab form-state slice — opaque JSON to the IPC layer
           * (main process just round-trips it through the URL hash). The
           * receiving renderer's `readSeedFromHash` validates the shape
           * via `hydrateFunctionForm` on the destination side.
           */
          functionForm?: unknown;
        };
        x: number;
        y: number;
      }): Promise<void>;
      removeTabInSource(payload: {
        sourceWindowId: number;
        tabId: string;
      }): Promise<void>;
      onRemoveTab(cb: (payload: { tabId: string }) => void): () => void;
      getCursorScreenPoint(): Promise<{ x: number; y: number }>;
      isCursorOverApp(): Promise<boolean>;
      // Synchronous accessor. Returns null briefly during the window's
      // first few milliseconds while preload's `window.id` IPC settles.
      windowId(): number | null;
      // Synchronously-resolved version block — captured by preload
      // at init time so renderers can read these as plain fields.
      // The About dialog is the primary consumer.
      versions: {
        app: string;
        electron: string;
        chrome: string;
        node: string;
      };

      // electron-updater bridge — see electron/preload.ts and main.ts.
      // `getLastStatus` is `sendSync` so it returns the most recent
      // status without an async hop; harmless at mount-time.
      updater: {
        checkNow(): Promise<void>;
        getLastStatus(): UpdateStatus;
        onStatus(cb: (status: UpdateStatus) => void): () => void;
      };
    };
  }
}

function platformToOs(platform: string): HostOs {
  switch (platform) {
    case 'win32': return 'windows';
    case 'darwin': return 'macos';
    case 'linux': return 'linux';
    default: return 'unknown';
  }
}

function createHostBridge(): HostBridge {
  const eh = window.electronHost;
  if (!eh?.isElectron) {
    // The SPA bundle is only meant to load inside the Electron shell.
    // Hitting this means the preload didn't run (mis-configured
    // BrowserWindow) or the SPA is being served standalone.
    throw new Error(
      'window.electronHost is not available. Heliosoph.DatumV only runs inside ' +
        'the Electron shell — the SPA must be loaded by electron/main.ts.',
    );
  }

  const handlers: HostMessageHandler[] = [];
  eh.onMaximizedChanged((maximized) => {
    const message = maximized ? 'host:window.maximized' : 'host:window.normal';
    console.log('[host] ←', message);
    for (const h of handlers) h(message);
  });

  return {
    minimize: () => {
      console.log('[host] → window.minimize');
      void eh.minimize();
    },
    toggleMaximize: () => {
      console.log('[host] → window.toggleMaximize');
      void eh.toggleMaximize();
    },
    close: () => {
      console.log('[host] → window.close');
      void eh.close();
    },
    onMessage: (handler) => handlers.push(handler),
    setApplicationMenu: (tree) => {
      void eh.setApplicationMenu(tree);
    },
    onMenuCommand: (handler) => {
      eh.onMenuCommand(handler);
    },
    getRecentCatalogs: () => eh.catalogGetRecents(),
    pickAndOpenCatalog: async () => {
      console.log('[host] → catalog.openPicker');
      await eh.catalogOpenPicker();
    },
    pickAndCreateCatalog: async () => {
      console.log('[host] → catalog.newPicker');
      await eh.catalogNewPicker();
    },
    openCatalogPath: async (path) => {
      console.log('[host] → catalog.openPath', path);
      await eh.catalogOpenPath(path);
    },
    setHostStrings: (strings) => {
      void eh.setHostStrings(strings);
    },
    setHostTheme: (theme) => {
      void eh.setHostTheme(theme);
    },
    setZoomLevel: (level) => {
      eh.setZoomLevel(level);
    },
    getZoomLevel: () => eh.getZoomLevel(),
    checkForUpdates: () => {
      console.log('[host] → updater.check');
      void eh.updater.checkNow();
    },
    getUpdateStatus: () => eh.updater.getLastStatus(),
    onUpdateStatus: (cb) => eh.updater.onStatus(cb),
  };
}

export const os: HostOs = platformToOs(window.electronHost?.platform ?? '');
export const host: HostBridge = createHostBridge();

console.log('[host] detected', { os, platform: window.electronHost?.platform });
