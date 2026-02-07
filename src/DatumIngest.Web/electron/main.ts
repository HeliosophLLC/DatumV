import {
  app,
  BrowserWindow,
  ipcMain,
  Menu,
  Notification,
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
  const projectDir = path.resolve(__dirname, '..', '..');
  const cmd = isDev
    ? 'dotnet'
    : path.join(process.resourcesPath, 'backend', 'DatumIngest.Web.exe');
  const args = isDev ? ['run', '--project', projectDir] : [];

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
    // Chromeless on Windows; OS chrome on Mac/Linux (matches current
    // TitleBar.tsx which renders null off Windows).
    frame: process.platform !== 'win32',
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

function createMainWindow(loadUrl: string): BrowserWindow {
  const win = createWindow({});
  win.loadURL(loadUrl);
  return win;
}

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
