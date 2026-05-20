import { proxy } from 'valtio';
import { host } from '../host';

// Window state pushed from the Electron main process via maximize/unmaximize
// events. We optimistically flip on click for snappy UI; the host echoes
// the real state (also covering OS-driven changes: Win+Up/Down, Aero Snap,
// double-click on the drag region), which reconciles any drift.
export const windowState = proxy({
  maximized: false,
});

host.onMessage((message) => {
  if (message === 'host:window.maximized') {
    windowState.maximized = true;
  } else if (message === 'host:window.normal') {
    windowState.maximized = false;
  }
});

export function minimize(): void {
  host.minimize();
}

export function toggleMaximize(): void {
  // Optimistic flip — host's echo will correct if the action didn't apply.
  windowState.maximized = !windowState.maximized;
  host.toggleMaximize();
}

export function close(): void {
  host.close();
}
