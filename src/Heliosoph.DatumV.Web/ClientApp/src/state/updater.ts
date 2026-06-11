import { proxy } from 'valtio';
import { host, type UpdateStatus } from '@/host';

// Update-checker state. Main owns the actual electron-updater probe;
// this store mirrors the most recent status into the renderer and the
// consumers (Help menu, title-bar chip) `useSnapshot` off of it.
//
// `showTransient` distinguishes user-initiated checks from background
// startup checks. The chip shows `checking` / `not-available` / `error`
// only when this flag is set — keeps the chrome quiet on every launch
// (the silent background probe would otherwise flash "Up to date" 5s in
// every time the app opens). `available` is always shown regardless.

export const updaterState = proxy<{
  status: UpdateStatus;
  showTransient: boolean;
}>({
  status: host.getUpdateStatus(),
  showTransient: false,
});

let dismissTimer: ReturnType<typeof setTimeout> | null = null;

function clearDismiss(): void {
  if (dismissTimer === null) return;
  clearTimeout(dismissTimer);
  dismissTimer = null;
}

function scheduleDismiss(delayMs: number): void {
  clearDismiss();
  dismissTimer = setTimeout(() => {
    updaterState.showTransient = false;
    dismissTimer = null;
  }, delayMs);
}

host.onUpdateStatus((status) => {
  updaterState.status = status;
  if (!updaterState.showTransient) return;
  // `available` keeps the chip visible until the user acts on it (a
  // different visual lives in the chip for that state). The two terminal
  // transient states auto-dismiss the chip 5s after arriving so the user
  // gets visual confirmation of their manual check without it lingering.
  if (status.kind === 'not-available' || status.kind === 'error') {
    scheduleDismiss(5_000);
  }
});

export function checkForUpdates(): void {
  clearDismiss();
  updaterState.showTransient = true;
  host.checkForUpdates();
}

// DevTools handle. Until the publish-target repo flips public the real
// probe will 404; this lets you simulate every status via the console:
//   window.__datumv.updater.status = { kind: 'available', version: '0.99.0',
//                                       releaseUrl: 'https://example.com' };
//   window.__datumv.updater.status = { kind: 'checking' };
//   window.__datumv.updater.showTransient = true;
if (typeof window !== 'undefined') {
  const w = window as unknown as { __datumv?: Record<string, unknown> };
  w.__datumv = w.__datumv ?? {};
  w.__datumv.updater = updaterState;
}
