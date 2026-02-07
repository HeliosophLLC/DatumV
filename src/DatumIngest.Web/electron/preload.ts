import { contextBridge, ipcRenderer } from 'electron';

// Surface exposed to every renderer (main window + dialog windows). All
// callers go through this; the renderer's React code never touches
// ipcRenderer directly.
contextBridge.exposeInMainWorld('electronHost', {
  isElectron: true,
  platform: process.platform,

  // Window controls. The target is implicit (this renderer's window).
  minimize: () => ipcRenderer.invoke('window.minimize'),
  toggleMaximize: () => ipcRenderer.invoke('window.toggleMaximize'),
  close: () => ipcRenderer.invoke('window.close'),

  // Subscribe to maximize-state echo from main. Returns an unsubscribe
  // function for clean teardown.
  onMaximizedChanged: (cb: (maximized: boolean) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, maximized: boolean) => cb(maximized);
    ipcRenderer.on('window.maximizedChanged', handler);
    return () => ipcRenderer.off('window.maximizedChanged', handler);
  },

  // Open a dialog window. The Promise resolves with the dialog's result
  // (or null if the user X-closed without resolving). Main process owns
  // the lifecycle.
  openDialog: (spec: unknown) => ipcRenderer.invoke('dialog.open', spec),

  // Called from inside a dialog window's renderer to deliver its final
  // result. ipcRenderer.send (not invoke) because main uses ipcMain.on
  // to receive and reply via the originator's pending Promise — fire-
  // and-forget from the dialog's side.
  resolveDialog: (result: unknown) => ipcRenderer.send('dialog.resolve', result),

  // Native OS notification. No-ops if the host doesn't support
  // notifications (e.g. some Linux desktops without a notification
  // daemon installed).
  notify: (opts: { title: string; body: string }) => ipcRenderer.invoke('notify', opts),

  // Native file/folder picker. Returns Electron's OpenDialogReturnValue
  // shape: { canceled: boolean, filePaths: string[] }.
  showOpenDialog: (options: unknown) => ipcRenderer.invoke('fs.showOpenDialog', options),
});
