import { contextBridge, ipcRenderer, webFrame } from 'electron';

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

  // Native OS context menu. Resolves with the clicked item's id, or
  // null if the user dismissed without picking. The spec is renderer-
  // owned so each surface (results table today; tab strip / tree
  // tomorrow) can hand in its own labels and item ids.
  showContextMenu: (spec: {
    items: Array<
      | { id: string; label: string; accelerator?: string; enabled?: boolean }
      | { type: 'separator' }
    >;
  }) => ipcRenderer.invoke('contextMenu.show', spec) as Promise<string | null>,

  // Native file/folder picker. Returns Electron's OpenDialogReturnValue
  // shape: { canceled: boolean, filePaths: string[] }.
  showOpenDialog: (options: unknown) => ipcRenderer.invoke('fs.showOpenDialog', options),

  // Native file save dialog. Returns Electron's SaveDialogReturnValue
  // shape: { canceled: boolean, filePath?: string }. Used by the editor
  // Ctrl+S flow for scratch SQL tabs.
  showSaveDialog: (options: unknown) => ipcRenderer.invoke('fs.showSaveDialog', options),

  // Open an http(s) URL in the user's default browser. Main-side validates
  // protocol and silently drops anything else — see main.ts.
  openExternal: (url: string) => ipcRenderer.invoke('shell.openExternal', url),

  // Application menu: the renderer ships a localized template (plain
  // data, no i18next on the main side) and re-ships on locale change.
  // Replaces whatever menu is currently installed atomically.
  setApplicationMenu: (tree: unknown) =>
    ipcRenderer.invoke('menu.set', tree),

  // Catalog selection. The renderer drives these from the File menu's
  // "New Catalog" / "Open Catalog" / "Open Recent" entries. Main owns
  // the picker, the recents file, the backend respawn, and the splash
  // overlay — the renderer just kicks the action off.
  catalogGetRecents: () =>
    ipcRenderer.invoke('catalog.getRecents') as Promise<
      Array<{ path: string; displayName: string; lastOpenedAt: string }>
    >,
  catalogOpenPicker: () =>
    ipcRenderer.invoke('catalog.openPicker') as Promise<{
      canceled: boolean;
      path?: string;
    }>,
  catalogNewPicker: () =>
    ipcRenderer.invoke('catalog.newPicker') as Promise<{
      canceled: boolean;
      path?: string;
    }>,
  catalogOpenPath: (catalogPath: string) =>
    ipcRenderer.invoke('catalog.openPath', catalogPath) as Promise<{
      canceled: boolean;
      path?: string;
    }>,
  // Translated dialog + splash strings; renderer republishes on locale
  // change. Main caches and uses these in lieu of hardcoded English
  // for the catalog-swap UX.
  setHostStrings: (strings: unknown) =>
    ipcRenderer.invoke('host.strings.set', strings),

  // Zoom level for this renderer's WebContents. `webFrame` lives in
  // the renderer process, so the preload can call it directly without
  // an IPC hop. App-wide sync is the renderer's job — state/zoom.ts
  // broadcasts via a same-origin localStorage key that every
  // BrowserWindow (main, torn-out, dialog) picks up via 'storage'
  // events.
  setZoomLevel: (level: number) => {
    webFrame.setZoomLevel(level);
  },
  getZoomLevel: () => webFrame.getZoomLevel(),

  // Last-resolved theme ('light' | 'dark'); renderer republishes on
  // every change. Main persists it to disk and passes it on the
  // loader URL so splash / welcome paint in the user's chosen scheme
  // on the next load instead of falling back to OS preference.
  setHostTheme: (theme: 'light' | 'dark') =>
    ipcRenderer.invoke('host.theme.set', theme),

  // Loader-state status text pushed from main during boot / catalog
  // swap. Subscribed by the loader page (splash mode) so it can
  // render the current stage; not used by the SPA itself. Returns an
  // unsubscribe.
  onSplashStatus: (cb: (text: string) => void) => {
    const handler = (_e: Electron.IpcRendererEvent, text: string) => cb(text);
    ipcRenderer.on('splash:status', handler);
    return () => ipcRenderer.off('splash:status', handler);
  },

  // Native-menu click delivery. Fires with the commandId the renderer
  // assigned in its menu definition; the renderer's command registry
  // looks up the handler. Returns an unsubscribe.
  onMenuCommand: (cb: (commandId: string) => void) => {
    const handler = (_e: Electron.IpcRendererEvent, id: string) => cb(id);
    ipcRenderer.on('menu.command', handler);
    return () => ipcRenderer.off('menu.command', handler);
  },

  // Tab tear-out IPC. The renderer drives these directly from the
  // drag-and-drop handlers in TabStrip / LeafPaneView — see PR 8.
  //
  // `spawnTabWindow` opens a new BrowserWindow at the supplied screen
  // coordinates and seeds it with the given tab. Used by the tear-out
  // gesture (drag a tab outside any window and release).
  spawnTabWindow: (payload: {
    seed: {
      id: string;
      title: string;
      kind?: 'sql' | 'function';
      sql: string;
      editorSize?: number;
      functionForm?: unknown;
    };
    x: number;
    y: number;
  }) => ipcRenderer.invoke('tabwindow.spawn', payload),

  // `removeTabInSource` asks the source window (by id) to close the
  // tab that was just received elsewhere. Cross-window state move
  // can't happen purely via dataTransfer — each window owns its own
  // state — so the destination tells the source to drop its copy.
  removeTabInSource: (payload: { sourceWindowId: number; tabId: string }) =>
    ipcRenderer.invoke('tabwindow.removeInSource', payload),

  // Subscribe to "main asked us to remove tab X" deliveries. Fires when
  // another window received one of our tabs and is telling us we can
  // let go of it locally. Returns an unsubscribe.
  onRemoveTab: (cb: (payload: { tabId: string }) => void) => {
    const handler = (_e: Electron.IpcRendererEvent, p: { tabId: string }) =>
      cb(p);
    ipcRenderer.on('tabwindow.removeTab', handler);
    return () => ipcRenderer.off('tabwindow.removeTab', handler);
  },

  // Cursor's current screen coordinates. Used by the tear-out gesture
  // to place the new window directly under the cursor at drop time —
  // `dragend`'s clientX/clientY is unreliable when the drag ended
  // outside any DOM element. See main.ts.
  getCursorScreenPoint: () =>
    ipcRenderer.invoke('tabwindow.cursorScreenPoint') as Promise<{
      x: number;
      y: number;
    }>,

  // True when the OS cursor is currently inside any of our app's
  // BrowserWindows. Tear-out queries this on dragend to avoid
  // spawning a window when the user released over our own chrome
  // (title bar / nav / blank pane chrome) — `dropEffect === 'none'`
  // alone doesn't distinguish "released on empty desktop" from
  // "released on our own non-drop chrome."
  isCursorOverApp: () =>
    ipcRenderer.invoke('tabwindow.isCursorOverApp') as Promise<boolean>,

  // This renderer's own BrowserWindow id. Resolved synchronously at
  // preload time via `sendSync` — *before* any renderer JS runs — so
  // a dragstart that fires immediately after the window mounts still
  // sees a real id (not null). The earlier `invoke().then(...)` shape
  // had a race where a fast drag-back could write a null
  // sourceWindowId into the dataTransfer; the destination window's
  // notifySourceToRemove would then no-op, leaving the source with a
  // duplicate copy of the tab it just gave away.
  //
  // sendSync blocks the renderer briefly, but at preload time the
  // page hasn't started rendering yet so the block is invisible.
  windowId: (() => {
    const cached = ipcRenderer.sendSync('window.id.sync') as number | null;
    return (): number | null => cached;
  })(),

  // Version block resolved synchronously at preload time so the
  // About dialog (and anyone else who wants to display these) can
  // read them as plain fields without async ceremony. App version
  // comes via sendSync from main (which reads package.json);
  // electron/chrome/node come straight from process.versions.
  versions: {
    app: ipcRenderer.sendSync('app.version.sync') as string,
    electron: process.versions.electron ?? '',
    chrome: process.versions.chrome ?? '',
    node: process.versions.node ?? '',
  },
});
