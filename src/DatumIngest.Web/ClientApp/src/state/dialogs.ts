// Dialog open/resolve plumbing. The Electron main process owns each
// dialog's lifecycle: ipcMain.handle('dialog.open') spawns a child
// BrowserWindow with `parent + modal: true`, awaits a resolve message
// from that window's renderer, and returns the result directly to the
// originator's invoke promise. X-close without prior resolve synthesises
// `null`. See src/DatumIngest.Web/electron/main.ts.
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
// callers would stash the handle and call handle.close() to dismiss.
// v1 ships modal only; handle.close() is unimplemented.

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

export function openDialog<R = unknown, P = unknown>(spec: DialogSpec<P>): DialogHandle<R> {
  const requestId = crypto.randomUUID();
  const result = window.electronHost
    .openDialog({
      requestId,
      kind: spec.kind,
      payload: (spec.payload ?? null) as Record<string, unknown> | null,
      modal: spec.modal ?? true,
    })
    .then((r) => (r ?? null) as R | null);
  return {
    requestId,
    result,
    close() {
      console.warn('[dialogs] handle.close() not implemented — v1 is modal only');
    },
  };
}

// Called from a dialog's content to deliver its final result. The dialog
// SPA imports this and calls it on Accept/Decline. The Electron main
// process knows which dialog window the sender is via webContents
// identity — requestId is vestigial here, kept on the signature so the
// dialog components don't need to change.
export function resolveDialog<R>(_requestId: string, result: R): void {
  window.electronHost.resolveDialog(result);
}
