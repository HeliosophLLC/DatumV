import { host } from '@/host';

// Dialog open/resolve plumbing. See plans/dialog-ipc.md for the protocol.
//
// Caller ergonomics:
//
//   const { result } = openDialog<{ accepted: boolean }>({
//     kind: 'confirmLicense',
//     payload: { licenseId: '...', modelId: '...' },
//   });
//   const r = await result;
//   if (r?.accepted) { ... }
//
// Modal vs non-modal: same API. Modal callers await result; non-modal
// callers stash the handle, ignore result or subscribe later, and call
// handle.close() to dismiss. v1 ships modal-only behaviour server-side
// (Photino's modal flag isn't wired in this iteration); the field stays
// in the spec so non-modal callers can opt out without protocol churn.

export interface DialogSpec<P = unknown> {
  kind: string;
  payload?: P;
  modal?: boolean;
}

export interface DialogHandle<R> {
  requestId: string;
  result: Promise<R | null>;
  close(): void;
}

type Resolver = (result: unknown) => void;
const pendingResolvers = new Map<string, Resolver>();

// One-time subscription to host messages. Filters for the resolved kind
// and dispatches to the matching pending Promise.
host.onMessage((message) => {
  const pipe = message.indexOf('|');
  if (pipe < 0) return;
  const kind = message.slice(0, pipe);
  if (kind !== 'host:dialog.resolved') return;
  const payload = message.slice(pipe + 1);
  let parsed: { requestId?: string; result?: unknown };
  try {
    parsed = JSON.parse(payload);
  } catch {
    console.error('[dialogs] bad resolve payload', payload);
    return;
  }
  if (!parsed.requestId) return;
  const resolve = pendingResolvers.get(parsed.requestId);
  if (!resolve) return;
  pendingResolvers.delete(parsed.requestId);
  resolve(parsed.result ?? null);
});

export function openDialog<R = unknown, P = unknown>(spec: DialogSpec<P>): DialogHandle<R> {
  const requestId = crypto.randomUUID();
  const result = new Promise<R | null>((resolve) => {
    pendingResolvers.set(requestId, (value) => resolve(value as R | null));
  });

  host.sendPayload('host:dialog.open', {
    requestId,
    kind: spec.kind,
    payload: spec.payload ?? null,
    modal: spec.modal ?? true,
  });

  return {
    requestId,
    result,
    close() {
      host.sendPayload('host:dialog.close', { requestId });
      // Promise will resolve via the close-without-resolve cancel path
      // synthesised by the coordinator's WindowClosing handler.
    },
  };
}

// Called from a dialog's content to deliver its final result. The dialog
// SPA imports this and calls it on Accept/Decline. The coordinator
// routes the result back to the originator and closes the dialog window.
export function resolveDialog<R>(requestId: string, result: R): void {
  host.sendPayload('host:dialog.resolve', { requestId, result });
}
