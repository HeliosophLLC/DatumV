import {
  app,
  BrowserWindow,
  ipcMain,
  Menu,
  Notification,
  shell,
  dialog as electronDialog,
} from 'electron';
import type { IpcMainEvent } from 'electron';
import { spawn, type ChildProcess } from 'node:child_process';
import path from 'node:path';

const DEV_VITE_URL = 'http://localhost:5173';
const DEV_KESTREL_URL = 'http://127.0.0.1:5050';

interface DialogOpenSpec {
  requestId: string;
  kind: string;
  payload?: Record<string, unknown> | null;
  modal?: boolean;
}

// Single-instance lock. Without this, the user double-clicking the
// shortcut would spawn a second Electron + .NET pair, which the .NET
// catalog opener doesn't tolerate (it holds an exclusive file lock).
// Failing to get the lock → quit immediately; the existing instance's
// 'second-instance' handler focuses its main window.
if (!app.requestSingleInstanceLock()) {
  app.quit();
  process.exit(0);
}

app.on('second-instance', () => {
  const all = BrowserWindow.getAllWindows();
  // Main window is the only one without a parent. Fall back to whatever
  // exists if the user somehow has only a dialog open.
  const main = all.find((w) => !w.getParentWindow()) ?? all[0];
  if (!main) return;
  if (main.isMinimized()) main.restore();
  main.focus();
});

// .NET subprocess lifecycle. The renderer is the entry; the .NET app is
// a headless child spawned at startup. We pin Kestrel to a known port
// in dev so vite.config.ts's proxy stays static; in prod we read the
// listening URL back from stdout.
let dotnetProcess: ChildProcess | null = null;
let stoppingDotnet = false;

async function startDotnetBackend(): Promise<string> {
  const isDev = !app.isPackaged;

  // Dev: dotnet run on the source csproj. Prod: the published binary
  // bundled as extraResources by electron-builder (Day 6).
  //
  // DOTNET_CONFIGURATION env var (Debug | Release) lets the "App: Release"
  // launch profile spawn the backend in Release config so POOL_DIAGNOSTICS
  // is off and per-pool-return stack-trace capture doesn't dominate
  // allocation profiles. Unset → defaults to Debug, matching dotnet run's
  // default and the prior behaviour.
  const projectDir = path.resolve(__dirname, '..', '..');
  const dotnetConfig = process.env.DOTNET_CONFIGURATION ?? 'Debug';
  const cmd = isDev
    ? 'dotnet'
    : path.join(process.resourcesPath, 'backend', 'DatumIngest.Web.exe');
  const args = isDev ? ['run', '--project', projectDir, '-c', dotnetConfig] : [];

  const env: NodeJS.ProcessEnv = {
    ...process.env,
    DATUM_WEB_URL: isDev ? DEV_KESTREL_URL : 'http://127.0.0.1:0',
  };

  console.log(`[dotnet] spawning: ${cmd} ${args.join(' ')}`);
  dotnetProcess = spawn(cmd, args, { env, shell: false });

  return new Promise<string>((resolve, reject) => {
    let resolved = false;
    const timer = setTimeout(() => {
      if (!resolved) reject(new Error('.NET backend did not become ready within 90s'));
    }, 90_000);

    dotnetProcess!.stdout?.on('data', (data: Buffer) => {
      const text = data.toString();
      process.stdout.write(`[dotnet] ${text}`);

      if (!resolved) {
        // Program.RunHeadless prints: "DatumIngest listening at <url>"
        const match = text.match(/DatumIngest listening at (\S+)/);
        if (match) {
          resolved = true;
          clearTimeout(timer);
          resolve(match[1]);
        }
      }
    });

    dotnetProcess!.stderr?.on('data', (data: Buffer) => {
      process.stderr.write(`[dotnet ERR] ${data}`);
    });

    dotnetProcess!.on('error', (err) => {
      clearTimeout(timer);
      if (!resolved) reject(err);
    });

    dotnetProcess!.on('exit', (code) => {
      console.error(`[dotnet] exited with code ${code}`);
      clearTimeout(timer);
      if (!resolved) reject(new Error(`.NET backend exited with code ${code} before ready signal`));
      // If the backend dies after startup, take the whole app down —
      // there's nothing useful the renderer can do without it.
      if (!stoppingDotnet && resolved) {
        console.error('[dotnet] backend exited unexpectedly; quitting');
        app.quit();
      }
    });
  });
}

function stopDotnetBackend(): void {
  if (!dotnetProcess || dotnetProcess.killed) return;
  stoppingDotnet = true;

  // Windows has no SIGINT/SIGTERM for child processes. `dotnet run`
  // spawns the actual binary as a grandchild, so we need taskkill /T
  // to walk the tree.
  if (process.platform === 'win32') {
    try {
      spawn('taskkill', ['/PID', String(dotnetProcess.pid), '/T', '/F'], { shell: false });
    } catch (err) {
      console.error('[dotnet] taskkill failed', err);
    }
  } else {
    // SIGINT triggers Program.RunHeadless's Console.CancelKeyPress hook,
    // which stops the host application lifetime cleanly (catalog close,
    // etc). Fall back to SIGKILL if it doesn't exit within 5s.
    dotnetProcess.kill('SIGINT');
    const proc = dotnetProcess;
    setTimeout(() => {
      if (proc && !proc.killed) proc.kill('SIGKILL');
    }, 5000);
  }
  dotnetProcess = null;
}

function buildDialogUrl(parentUrl: string, spec: DialogOpenSpec): string {
  // Reuse the parent's origin so dev (Vite) and prod (Kestrel) work the
  // same way — no special-casing needed. Hash routing matches the
  // DialogShell.tsx parser: #/dialog/{kind}?requestId=...&...
  const origin = new URL(parentUrl).origin;
  const params = new URLSearchParams({ requestId: spec.requestId });
  if (spec.payload) {
    for (const [k, v] of Object.entries(spec.payload)) {
      if (v == null) continue;
      params.set(k, typeof v === 'string' ? v : JSON.stringify(v));
    }
  }
  return `${origin}/#/dialog/${encodeURIComponent(spec.kind)}?${params.toString()}`;
}

function createWindow(opts: { dialog?: boolean; parent?: BrowserWindow; modal?: boolean }): BrowserWindow {
  const win = new BrowserWindow({
    width: opts.dialog ? 720 : 1280,
    height: opts.dialog ? 600 : 800,
    minWidth: opts.dialog ? 480 : 640,
    minHeight: opts.dialog ? 320 : 480,
    show: false,
    parent: opts.parent,
    modal: opts.modal,
    // Dialogs are modal children: minimize sends them to the taskbar
    // where the user can't get them back (parent owns focus), and maximize
    // on a fixed modal isn't useful. Disabling at the BrowserWindow level
    // covers OS gestures / keyboard shortcuts that bypass our titlebar.
    minimizable: !opts.dialog,
    maximizable: !opts.dialog,
    // Chromeless everywhere. Our custom TitleBar renders on every platform
    // (Win / Mac / Linux flavors), and CSS app-region + Chromium handle
    // drag and OS-edge resize uniformly.
    frame: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
    },
  });

  win.once('ready-to-show', () => win.show());

  // Echo maximize state to whichever renderer mounted this window's
  // titlebar — main window and dialog windows both render WindowChrome.
  win.on('maximize', () => win.webContents.send('window.maximizedChanged', true));
  win.on('unmaximize', () => win.webContents.send('window.maximizedChanged', false));

  return win;
}

// References to the currently-live main window and any torn-out tab
// windows. Torn-out windows are conceptually dependent on the main
// window — closing main cascade-closes them rather than leaving
// orphaned editor surfaces with no obvious "home." Manual tracking
// (rather than Electron's `parent` BrowserWindow option) keeps the
// torn-outs independent in z-order and taskbar so the user can move
// them behind / beside main freely; we still get the close cascade
// by listening to main's `closed` event.
let mainWindow: BrowserWindow | null = null;
const tornOutWindows = new Set<BrowserWindow>();

function createMainWindow(loadUrl: string): BrowserWindow {
  const win = createWindow({});
  win.loadURL(loadUrl);
  mainWindow = win;
  win.on('closed', () => {
    if (mainWindow === win) mainWindow = null;
    for (const child of [...tornOutWindows]) {
      if (!child.isDestroyed()) child.close();
    }
  });
  return win;
}

// ───────────────────────── Tab tear-out ─────────────────────────

interface TabSeed {
  id: string;
  title: string;
  /** Discriminator; absent for SQL tabs (the renderer defaults missing values to `'sql'`). */
  kind?: 'sql' | 'function';
  sql: string;
  editorSize?: number;
  /**
   * Function-tab form state. Opaque to main — round-tripped through the
   * URL hash and validated by the destination renderer. Set only for
   * function tabs whose form the user has interacted with.
   */
  functionForm?: unknown;
}

/**
 * Cache of the URL the main window loaded, so a tear-out spawn knows
 * where to point new BrowserWindows. Captured at first window creation
 * because the URL depends on dev vs prod and the Kestrel port resolved
 * at startup — recomputing it would duplicate `startDotnetBackend`'s
 * resolution logic for no gain.
 */
let cachedLoadUrl: string | null = null;

function tearOutLoadUrl(seed: TabSeed): string {
  if (!cachedLoadUrl) {
    throw new Error('tearOutLoadUrl called before the main window loaded.');
  }
  const origin = new URL(cachedLoadUrl).origin;
  // Encode the seed payload as URL-safe base64 in the hash. Hash is used
  // (rather than query string) so the SPA's HTTP route stays "/" and
  // any react-router-like setup we add later doesn't misinterpret the
  // payload as a route. Hash is also the same channel `DialogShell`
  // uses for its modal payloads — symmetry with the existing pattern.
  const json = JSON.stringify(seed);
  const b64 = Buffer.from(json, 'utf8').toString('base64url');
  return `${origin}/#/tab-window/seed=${b64}`;
}

/**
 * Spawns a torn-out tab window at the given screen coordinates, seeded
 * with the supplied tab. Returns the new window so the caller can
 * coordinate further IPC if needed (today they don't — the seed is
 * everything the new renderer needs).
 *
 * Position semantics: the supplied (x, y) is the top-left of the new
 * window. Callers (the renderer's tear-out gesture) typically pass the
 * cursor's screen position minus a small offset so the title bar of the
 * new window appears under the cursor — keeps the drop feel right.
 */
function spawnTabWindow(seed: TabSeed, x: number, y: number): BrowserWindow {
  const win = createWindow({});
  win.setBounds({ x: Math.round(x), y: Math.round(y), width: 1280, height: 800 });
  win.loadURL(tearOutLoadUrl(seed));
  tornOutWindows.add(win);
  win.on('closed', () => tornOutWindows.delete(win));
  return win;
}

// IPC: renderer → main → renderer for cross-window tab coordination.
//
// `tabwindow.spawn` opens a new BrowserWindow at the supplied screen
// coordinates, pre-seeded with the supplied tab. Used by the tear-out
// gesture (drag a tab outside any window → drop).
ipcMain.handle(
  'tabwindow.spawn',
  (_event, payload: { seed: TabSeed; x: number; y: number }) => {
    spawnTabWindow(payload.seed, payload.x, payload.y);
  },
);

// `tabwindow.removeInSource` forwards a "tab X was received elsewhere,
// please close it" instruction from the destination window to the
// source. Only the source renderer can mutate its own state, so main
// is just a postman that resolves source window id → webContents.
ipcMain.handle(
  'tabwindow.removeInSource',
  (_event, payload: { sourceWindowId: number; tabId: string }) => {
    const source = BrowserWindow.fromId(payload.sourceWindowId);
    if (!source || source.isDestroyed()) return;
    source.webContents.send('tabwindow.removeTab', { tabId: payload.tabId });
  },
);

// `tabwindow.cursorScreenPoint` answers the renderer's "where's the
// cursor right now in screen coordinates" — the tear-out gesture uses
// this on `dragend` to position the new window under the cursor.
// `dragend`'s clientX/clientY can be unreliable when the drag finished
// outside the source window, so reading the cursor straight from the
// OS via Electron is more reliable.
ipcMain.handle('tabwindow.cursorScreenPoint', () => {
  // Lazy-imported so this file's top-level imports stay tidy.
  const { screen } = require('electron') as typeof import('electron');
  const pt = screen.getCursorScreenPoint();
  return { x: pt.x, y: pt.y };
});

// `tabwindow.isCursorOverApp` answers "is the cursor inside any of our
// BrowserWindows right now?" — used by the tear-out gesture as a guard:
// if the user released the drag while the cursor was over one of our
// windows (including the source's own title bar / nav / non-drop
// chrome), they almost certainly didn't intend to spawn a new window.
// `dropEffect === 'none'` alone doesn't disambiguate "released on empty
// desktop" from "released on our own chrome where no drop target
// claimed the event," so we add this geometry check.
ipcMain.handle('tabwindow.isCursorOverApp', () => {
  const { screen } = require('electron') as typeof import('electron');
  const pt = screen.getCursorScreenPoint();
  for (const win of BrowserWindow.getAllWindows()) {
    if (win.isDestroyed()) continue;
    const b = win.getBounds();
    if (
      pt.x >= b.x &&
      pt.x < b.x + b.width &&
      pt.y >= b.y &&
      pt.y < b.y + b.height
    ) {
      return true;
    }
  }
  return false;
});

function buildApplicationMenu(): void {
  const isMac = process.platform === 'darwin';
  const template: Electron.MenuItemConstructorOptions[] = [
    ...(isMac ? [{ role: 'appMenu' as const }] : []),
    { role: 'editMenu' },
    {
      label: 'View',
      submenu: [
        { role: 'reload' },
        { role: 'forceReload' },
        { role: 'toggleDevTools' },
        { type: 'separator' },
        { role: 'resetZoom' },
        { role: 'zoomIn' },
        { role: 'zoomOut' },
        { type: 'separator' },
        { role: 'togglefullscreen' },
      ],
    },
    { role: 'windowMenu' },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

// Window control handlers. The sender's BrowserWindow is the implicit
// target — works the same for the main window and dialog windows.
ipcMain.handle('window.minimize', (event) => {
  BrowserWindow.fromWebContents(event.sender)?.minimize();
});
ipcMain.handle('window.toggleMaximize', (event) => {
  const win = BrowserWindow.fromWebContents(event.sender);
  if (!win) return;
  if (win.isMaximized()) win.unmaximize();
  else win.maximize();
});
ipcMain.handle('window.close', (event) => {
  BrowserWindow.fromWebContents(event.sender)?.close();
});

// "What's my BrowserWindow id?" — preload's `windowId()` hits this
// once and caches the result. The renderer uses the id as the
// "source window" key when coordinating cross-window tab moves.
//
// Two paths: `invoke` (async, used by anything that can tolerate a
// short null window during preload init) and `on` with `sendSync`
// (synchronous, used by preload itself so the window id is captured
// before any renderer JS runs). The sync path is necessary because
// HTML5 dragstart freezes dataTransfer after the synchronous handler
// returns; a dragstart that happens before an async IPC resolves
// would write a null sourceWindowId into the drag payload, and the
// destination window's `notifySourceToRemove` would no-op — leaving
// the source with a duplicate copy of the moved tab.
ipcMain.handle('window.id', (event) => {
  return BrowserWindow.fromWebContents(event.sender)?.id ?? null;
});
ipcMain.on('window.id.sync', (event) => {
  event.returnValue =
    BrowserWindow.fromWebContents(event.sender)?.id ?? null;
});

// Dialog open: spawn a child BrowserWindow with parent + modal, await the
// dialog's resolve message, return its result to the originator. X-close
// without a prior resolve synthesises `null` so the originator's awaiting
// promise never hangs.
ipcMain.handle('dialog.open', async (event, spec: DialogOpenSpec) => {
  const parentWin = BrowserWindow.fromWebContents(event.sender);
  if (!parentWin) return null;

  return new Promise<unknown>((resolve) => {
    const dialog = createWindow({
      dialog: true,
      parent: parentWin,
      modal: spec.modal !== false,
    });
    dialog.setTitle(`DatumIngest — ${spec.kind}`);

    let settled = false;
    const settle = (result: unknown) => {
      if (settled) return;
      settled = true;
      resolve(result);
    };

    const onResolve = (e: IpcMainEvent, result: unknown) => {
      if (e.sender !== dialog.webContents) return;
      settle(result);
      if (!dialog.isDestroyed()) dialog.close();
    };

    ipcMain.on('dialog.resolve', onResolve);

    dialog.on('closed', () => {
      ipcMain.removeListener('dialog.resolve', onResolve);
      settle(null); // synthesise null on X-close
    });

    dialog.loadURL(buildDialogUrl(parentWin.webContents.getURL(), spec));
  });
});

// Native OS notification (toast on Windows, banner on Mac). Silently
// no-ops on systems where Electron reports notifications unsupported.
ipcMain.handle('notify', (_event, opts: { title: string; body: string }) => {
  if (!Notification.isSupported()) return;
  new Notification({ title: opts.title, body: opts.body }).show();
});

// Native file/folder open dialog. Parented to the sender's window so it
// inherits modality. The future ingest UI is the planned consumer.
ipcMain.handle('fs.showOpenDialog', async (event, options: Electron.OpenDialogOptions) => {
  const parentWin = BrowserWindow.fromWebContents(event.sender);
  if (!parentWin) return { canceled: true, filePaths: [] };
  return await electronDialog.showOpenDialog(parentWin, options);
});

// Open a URL in the user's default OS browser. Restricted to http(s) —
// renderer code shouldn't be able to launch arbitrary protocol handlers
// (file://, mailto:, custom-scheme:) that the user didn't initiate. This
// is the documented Electron guard against compromised-renderer link
// injection; consumers (e.g. LicenseDialog) only ever pass URLs that
// originated in trusted markdown bundled with the app.
ipcMain.handle('shell.openExternal', async (_event, url: string) => {
  if (typeof url !== 'string') return;
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return;
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return;
  await shell.openExternal(url);
});

app.whenReady().then(async () => {
  buildApplicationMenu();

  let kestrelUrl: string;
  try {
    kestrelUrl = await startDotnetBackend();
    console.log(`[main] .NET backend ready at ${kestrelUrl}`);
  } catch (err) {
    console.error('[main] failed to start .NET backend:', err);
    app.quit();
    return;
  }

  // Dev: load Vite (HMR proxies /api + /hubs to Kestrel). Prod: load
  // Kestrel directly — it serves wwwroot/ for the SPA.
  const loadUrl = app.isPackaged ? kestrelUrl : DEV_VITE_URL;
  cachedLoadUrl = loadUrl;
  createMainWindow(loadUrl);

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createMainWindow(loadUrl);
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.on('will-quit', stopDotnetBackend);
// Also catch hard exits — process.exit() and uncaught crashes won't fire
// will-quit, but they will fire 'exit'. Belt-and-suspenders against
// leaking a headless .NET process.
process.on('exit', stopDotnetBackend);
