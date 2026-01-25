// Host environment + IPC bridge. Both OS and runtime are detected *client-side*
// because the chrome decision belongs to the user's actual device, not the
// server (a SaaS backend running Linux shouldn't force Linux chrome on a Mac
// user). The HostBridge is the seam for everything that talks to the desktop
// process — window controls today, dialogs / file pickers / menus later.

export type HostOs = 'windows' | 'macos' | 'linux' | 'unknown';
export type HostRuntime = 'photino' | 'browser';

export type HostMessageHandler = (message: string) => void;

export interface HostBridge {
  minimize(): void;
  toggleMaximize(): void;
  close(): void;
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

export function detectRuntime(): HostRuntime {
  const isPhotino =
    typeof window.external !== 'undefined' &&
    typeof (window.external as External).sendMessage === 'function';
  return isPhotino ? 'photino' : 'browser';
}

function createPhotinoBridge(): HostBridge {
  const handlers: HostMessageHandler[] = [];

  // Register exactly once with Photino. Incoming messages fan out to all
  // subscribers; consumers add via host.onMessage(...).
  const ext = window.external as External;
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
    onMessage: (handler) => handlers.push(handler),
  };
}

function createBrowserBridge(): HostBridge {
  return {
    minimize: () => {},
    toggleMaximize: () => {},
    close: () => window.close(),
    onMessage: () => {}, // No host to push messages from.
  };
}

export const runtime: HostRuntime = detectRuntime();
export const os: HostOs = detectOs();
export const host: HostBridge = runtime === 'photino' ? createPhotinoBridge() : createBrowserBridge();

console.log('[host] detected', { runtime, os });
