// Host environment + IPC bridge. The OS comes from the Electron preload
// (process.platform) — no client-side sniffing of navigator.platform.
// The HostBridge is the seam for everything that talks to the Electron
// main process: window controls today, file pickers and notifications
// as the UI grows.

export type HostOs = 'windows' | 'macos' | 'linux' | 'unknown';

export type HostMessageHandler = (message: string) => void;

export interface HostBridge {
  minimize(): void;
  toggleMaximize(): void;
  close(): void;
  // Subscribe to host-pushed messages. Today the only kind is
  // 'host:window.maximized' / 'host:window.normal' — translated from
  // Electron's typed window.maximizedChanged channel so existing
  // subscribers in state/window.ts keep their string-match shape.
  onMessage(handler: HostMessageHandler): void;
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
      openExternal(url: string): Promise<void>;
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
      'window.electronHost is not available. DatumIngest only runs inside ' +
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
  };
}

export const os: HostOs = platformToOs(window.electronHost?.platform ?? '');
export const host: HostBridge = createHostBridge();

console.log('[host] detected', { os, platform: window.electronHost?.platform });
