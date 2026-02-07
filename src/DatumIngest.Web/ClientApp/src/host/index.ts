// Host environment + IPC bridge. The OS is detected client-side because chrome
// decisions belong to the user's actual device (a SaaS backend running Linux
// shouldn't force Linux chrome on a Mac user). The HostBridge is the seam for
// everything that talks to the Photino host process — window controls today,
// dialogs / file pickers / menus later.
//
// DatumIngest is Photino-only. The previous browser-mode shim was removed
// once the merge into a single Web project landed — there is no SPA-in-a-
// vanilla-browser entry point any more. If you need to invoke host APIs
// from anywhere other than Photino, that's a new mode and needs its own
// transport, not a fallback bridge here.

export type HostOs = 'windows' | 'macos' | 'linux' | 'unknown';

export type HostMessageHandler = (message: string) => void;

export type ResizeSide =
  | 'top'
  | 'right'
  | 'bottom'
  | 'left'
  | 'top-left'
  | 'top-right'
  | 'bottom-left'
  | 'bottom-right';

export interface HostBridge {
  minimize(): void;
  toggleMaximize(): void;
  close(): void;
  // Hands the drag/resize gesture to the OS native window manager (Windows
  // only today; Mac/Linux no-op). Called from mousedown on the drag layer /
  // resize zones so the OS takes over from there — snap-to-edge, Aero Snap,
  // Win+arrow handling all flow through naturally.
  startDrag(): void;
  startResize(side: ResizeSide): void;
  // Subscribe to messages pushed from the host (C#). Photino's
  // `window.external.receiveMessage` is a single-callback API; this fanout
  // lets multiple state slices listen to the same channel.
  onMessage(handler: HostMessageHandler): void;
}

declare global {
  interface External {
    sendMessage?(message: string): void;
    receiveMessage?(callback: (message: string) => void): void;
  }
}

export function detectOs(): HostOs {
  const platform = (navigator.platform || '').toLowerCase();
  const ua = (navigator.userAgent || '').toLowerCase();
  if (platform.startsWith('mac') || ua.includes('mac os')) return 'macos';
  if (platform.startsWith('win') || ua.includes('windows')) return 'windows';
  if (platform.startsWith('linux') || ua.includes('linux')) return 'linux';
  return 'unknown';
}

function createHostBridge(): HostBridge {
  const ext = window.external as External | undefined;
  if (!ext || typeof ext.sendMessage !== 'function') {
    // Photino didn't inject the bridge. We don't try to fall back — this
    // is a programming error (SPA bundle loaded outside Photino) and the
    // chat / models / settings flows all depend on the host being there.
    throw new Error(
      'window.external.sendMessage is not available. DatumIngest only runs ' +
        'inside Photino — the SPA must be served by the Photino host process.',
    );
  }

  const handlers: HostMessageHandler[] = [];

  // Register exactly once with Photino. Incoming messages fan out to all
  // subscribers; consumers add via host.onMessage(...).
  if (typeof ext.receiveMessage === 'function') {
    ext.receiveMessage((message) => {
      console.log('[host] ←', message);
      for (const h of handlers) h(message);
    });
  }

  const send = (kind: string) => {
    console.log('[host] →', kind);
    ext.sendMessage!(kind);
  };

  return {
    minimize: () => send('host:window.minimize'),
    toggleMaximize: () => send('host:window.toggleMaximize'),
    close: () => send('host:window.close'),
    startDrag: () => send('host:window.drag'),
    startResize: (side) => send(`host:window.resize.${side}`),
    onMessage: (handler) => handlers.push(handler),
  };
}

export const os: HostOs = detectOs();
export const host: HostBridge = createHostBridge();

console.log('[host] detected', { os });
