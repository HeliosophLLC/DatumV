// Electron shell for Heliosoph DatumV. Owns the main BrowserWindow,
// dialog windows, the .NET subprocess lifecycle, the update checker,
// and the navigation lock that keeps every renderer pinned to the
// app's own origin.

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
import { autoUpdater } from 'electron-updater';
import { spawn, type ChildProcess } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import {
  CATALOG_MARKER,
  computeGlobalDataPath,
  hasCatalogMarker,
  readRecents,
  seedLegacyRecentsIfNeeded,
  touchRecent,
  type RecentCatalog,
} from './catalog-recents';

// Shape pushed to renderers on every updater state transition. Mirrored
// exactly by ClientApp/src/state/updater.ts — keep both in sync.
type UpdateStatus =
  | { kind: 'idle' }
  | { kind: 'checking' }
  | { kind: 'available'; version: string; releaseUrl: string }
  | { kind: 'not-available'; currentVersion: string }
  | { kind: 'error'; message: string };

const DEV_VITE_URL = 'http://localhost:5173';
const DEV_KESTREL_URL = 'http://127.0.0.1:5050';

interface DialogOpenSpec {
  requestId: string;
  kind: string;
  payload?: Record<string, unknown> | null;
  modal?: boolean;
}

// Identify the app to the OS. On Windows, toast notifications display the
// AppUserModelId as their title — without this they read "electron.app.Electron".
// setName overrides package.json's productName for `app.getName()`, the default
// menu's "About …" item, and the window-title fallback.
app.setName('DatumV');
app.setAppUserModelId('com.heliosoph.datumv');

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

// Per-machine global-data folder — settings.json, recents.json, models
// cache. The backend reads DATUMV_GLOBAL_PATH so both sides agree on the
// exact directory regardless of how each platform names its app-data
// folder. Resolved once at module init since it's a function of the
// current OS, never per-launch state.
const globalDataPath = computeGlobalDataPath();

// .NET subprocess lifecycle. The renderer is the entry; the .NET app is
// a headless child spawned at startup. We pin Kestrel to a known port
// in dev so vite.config.ts's proxy stays static; in prod we read the
// listening URL back from stdout.
let dotnetProcess: ChildProcess | null = null;
let stoppingDotnet = false;

// Catalog path that swapCatalog last successfully spawned the backend
// against. Tracked so `backend.restart` (triggered after a CUDA bundle
// install completes) can respawn at the same catalog without the
// renderer having to remember which one is current.
let lastOpenedCatalogPath: string | null = null;

// Returns the absolute path of an installed CUDA runtime bundle in the
// per-machine cache, or null when none is installed. Mirrors
// GpuRuntimeProbe.TryReadInstalled on the .NET side: a version subdir
// counts as "installed" iff it contains an installed.json marker (the
// installer writes it last, atomically). Cheap to call repeatedly —
// runs on every backend spawn.
function findInstalledCudaBundleDir(): string | null {
  const cacheRoot = path.join(globalDataPath, 'cuda-runtime');
  if (!fs.existsSync(cacheRoot)) return null;
  let entries: string[];
  try {
    entries = fs.readdirSync(cacheRoot);
  } catch {
    return null;
  }
  for (const name of entries) {
    if (name.startsWith('.')) continue;  // staging dirs
    const dir = path.join(cacheRoot, name);
    try {
      if (!fs.statSync(dir).isDirectory()) continue;
      if (fs.existsSync(path.join(dir, 'installed.json'))) return dir;
    } catch {
      // Race with installer GC; skip and continue.
    }
  }
  return null;
}

async function startDotnetBackend(catalogPath: string): Promise<string> {
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
  const backendBinary = process.platform === 'win32'
    ? 'Heliosoph.DatumV.Web.exe'
    : 'Heliosoph.DatumV.Web';
  const cmd = isDev
    ? 'dotnet'
    : path.join(process.resourcesPath, 'backend', backendBinary);
  // SkipClientApp=true in dev: the renderer is served by the concurrent Vite
  // dev server (DEV_VITE_URL), so the backend never needs wwwroot built. Left
  // unset, the `dotnet run` build would trigger the csproj's ClientAppInstall
  // (npm ci) + Vite build, and `npm ci` wipes ClientApp/node_modules out from
  // under the running dev server — killing Vite and taking the app down. In
  // prod the published binary already ships wwwroot, so this only applies to
  // the dev `dotnet run` path.
  const args = isDev
    ? ['run', '--project', projectDir, '-c', dotnetConfig, '--property:SkipClientApp=true']
    : [];

  // Catalog + global-data paths are the workspace contract: backend
  // refuses to start without DATUMV_CATALOG_PATH (see Program.cs).
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    DATUMV_WEB_URL: isDev ? DEV_KESTREL_URL : 'http://127.0.0.1:0',
    DATUMV_CATALOG_PATH: catalogPath,
    DATUMV_GLOBAL_PATH: globalDataPath,
  };

  // CUDA runtime libraries are deferred-installed to the user's data dir
  // by the GPU section in Settings (see state/gpu.ts + CudaBundleInstaller).
  // When present, prepend that dir to the loader search path so
  // libonnxruntime_providers_cuda.so's dlopen() finds libcublasLt.so.12 /
  // libcudart.so.12 / libcudnn.so.9 / etc. Without this they resolve only
  // against the system loader path, and a machine without CUDA toolkit
  // installed silently falls back to CPU.
  const cudaCacheDir = findInstalledCudaBundleDir();
  if (cudaCacheDir) {
    if (process.platform === 'linux') {
      env.LD_LIBRARY_PATH = process.env.LD_LIBRARY_PATH
        ? `${cudaCacheDir}:${process.env.LD_LIBRARY_PATH}`
        : cudaCacheDir;
    } else if (process.platform === 'win32') {
      // Windows resolves DLLs via PATH (and the app-dir search) — no
      // LD_LIBRARY_PATH equivalent. Prepend so the cache wins over any
      // system CUDA toolkit version the user might have separately.
      env.PATH = `${cudaCacheDir};${process.env.PATH ?? ''}`;
    }
    console.log(`[gpu] CUDA runtime: ${cudaCacheDir}`);
  } else {
    console.log('[gpu] CUDA runtime: not installed; backend will use CPU/Vulkan');
  }

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
        // Program.RunHeadless prints: "Heliosoph.DatumV listening at <url>"
        const match = text.match(/Heliosoph.DatumV listening at (\S+)/);
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

// Stop the backend and await its actual exit. Catalog swap calls this
// before respawning so the new catalog never collides with the old
// child's file lock. Bounded by a 10s safety timer to keep the UI
// responsive even if the child wedges on shutdown.
function stopDotnetBackendAndAwait(): Promise<void> {
  return new Promise((resolve) => {
    const proc = dotnetProcess;
    if (!proc || proc.killed) {
      resolve();
      return;
    }
    const onExit = (): void => {
      proc.off('exit', onExit);
      resolve();
    };
    proc.on('exit', onExit);
    stopDotnetBackend();
    setTimeout(() => {
      proc.off('exit', onExit);
      resolve();
    }, 10_000);
  });
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
    // Linux taskbar / window manager doesn't embed the icon in the binary
    // the way Windows does, so BrowserWindow must be told explicitly.
    // Windows + macOS pull from the bundled .exe / .app and ignore this
    // option — leaving it set is harmless cross-platform.
    icon: path.join(__dirname, '..', 'build', 'icons', '512x512.png'),
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

  lockNavigation(win);

  return win;
}

// Allow only navigations that stay within the app's own renderer URLs.
// Concretely: http(s) on localhost or 127.0.0.1 (covers dev Vite at 5173,
// dev Kestrel at 5050, and prod Kestrel on its dynamic port). Anything
// else — a malicious script setting location.href to evil.com, a stray
// <a href="https://…">, a server-side 30x to an external host — is
// canceled before the renderer commits to it. New-window requests
// (window.open, target="_blank") are denied uniformly; external http(s)
// URLs are routed to the user's default browser via shell.openExternal
// so legitimate link clicks still feel right. New BrowserWindows for
// tab tear-out and dialogs go through explicit ipcMain handlers, never
// through window.open.
function isAppOriginUrl(raw: string): boolean {
  let parsed: URL;
  try { parsed = new URL(raw); }
  catch { return false; }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return false;
  return parsed.hostname === 'localhost' || parsed.hostname === '127.0.0.1';
}

function lockNavigation(win: BrowserWindow): void {
  const blockExternal = (
    event: Electron.Event,
    url: string,
    kind: 'navigate' | 'redirect',
  ): void => {
    if (isAppOriginUrl(url)) return;
    event.preventDefault();
    console.warn(`[security] blocked ${kind} to`, url);
  };
  win.webContents.on('will-navigate', (event, url) => blockExternal(event, url, 'navigate'));
  win.webContents.on('will-redirect', (event, url) => blockExternal(event, url, 'redirect'));
  win.webContents.setWindowOpenHandler(({ url }) => {
    try {
      const parsed = new URL(url);
      if (
        (parsed.protocol === 'http:' || parsed.protocol === 'https:') &&
        parsed.hostname !== 'localhost' &&
        parsed.hostname !== '127.0.0.1'
      ) {
        void shell.openExternal(url);
      }
    } catch {
      // Unparseable URL: just deny without forwarding.
    }
    return { action: 'deny' };
  });
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

// Single main window for the whole app session. It hosts two
// rendered surfaces in turn — the loader page (splash or welcome
// mode) and the SPA — sharing the same preload + contextIsolation
// webPreferences. We never spawn a separate splash/welcome window
// (an earlier draft did; it floated above the main content and
// looked broken). Subsequent transitions are just `loadFile` /
// `loadURL` calls on this same window.
function createMainWindow(): BrowserWindow {
  const win = createWindow({});
  mainWindow = win;
  win.on('closed', () => {
    if (mainWindow === win) mainWindow = null;
    for (const child of [...tornOutWindows]) {
      if (!child.isDestroyed()) child.close();
    }
  });
  return win;
}

// ───────────────────────── Host strings ─────────────────────────

// Translated copies of every user-visible string main renders
// natively (folder-picker titles, error-dialog text, splash status
// during catalog swap). The renderer ships these once mounted and on
// every locale change via the `host.strings.set` IPC; English
// defaults cover the brief window before the renderer publishes
// (first-launch splash, etc.).
interface HostStrings {
  catalog: {
    openTitle: string;
    openButtonLabel: string;
    newTitle: string;
    newButtonLabel: string;
    invalidTitle: string;
    invalidMessage: string;
    invalidDetail: string;
  };
  splash: {
    stoppingBackend: string;
    startingBackend: string;
    loadingWorkspace: string;
  };
}

let hostStrings: HostStrings = {
  catalog: {
    openTitle: 'Open Catalog',
    openButtonLabel: 'Open',
    newTitle: 'New Catalog',
    newButtonLabel: 'Create',
    invalidTitle: 'Not a catalog folder',
    invalidMessage: 'Not a catalog folder',
    invalidDetail:
      '{path} does not contain {marker}.\n\n' +
      'Use "New Catalog" if you want to create one here.',
  },
  splash: {
    stoppingBackend: 'Stopping backend…',
    startingBackend: 'Starting backend…',
    loadingWorkspace: 'Loading workspace…',
  },
};

// Last-known resolved theme from the SPA's state/theme.ts. Persisted to
// disk so the loader page (different origin → can't share the SPA's
// localStorage) can paint in the user-chosen theme on first frame at
// next launch, not just match prefers-color-scheme. Republished by the
// SPA on every settings change, on system theme change while
// preference='system', and on first SPA mount.
type ResolvedTheme = 'light' | 'dark';
const THEME_FILE = path.join(globalDataPath, 'loader-theme');

function readPersistedTheme(): ResolvedTheme | null {
  try {
    const v = fs.readFileSync(THEME_FILE, 'utf8').trim();
    return v === 'light' || v === 'dark' ? v : null;
  } catch {
    return null;
  }
}

function writePersistedTheme(theme: ResolvedTheme): void {
  try {
    fs.mkdirSync(globalDataPath, { recursive: true });
    fs.writeFileSync(THEME_FILE, theme, 'utf8');
  } catch (err) {
    console.error('[host] failed to persist loader theme:', err);
  }
}

let loaderTheme: ResolvedTheme | null = readPersistedTheme();

ipcMain.handle('host.theme.set', (_event, incoming: unknown) => {
  if (incoming !== 'light' && incoming !== 'dark') return;
  if (loaderTheme === incoming) return;
  loaderTheme = incoming;
  writePersistedTheme(incoming);
});

ipcMain.handle('host.strings.set', (_event, incoming: unknown) => {
  // Defensive: take what we recognise; ignore extra fields. The
  // renderer's snapshot() shape is the contract, but we don't want a
  // typo there to drop main into all-undefined-labels mode.
  if (!incoming || typeof incoming !== 'object') return;
  const next = incoming as Partial<HostStrings>;
  hostStrings = {
    catalog: { ...hostStrings.catalog, ...(next.catalog ?? {}) },
    splash: { ...hostStrings.splash, ...(next.splash ?? {}) },
  };
});

function fillInvalidDetail(template: string, pickedPath: string): string {
  return template
    .replaceAll('{path}', pickedPath)
    .replaceAll('{marker}', CATALOG_MARKER);
}

// ───────────────────────── Catalog swap ─────────────────────────

// Restart-on-swap model: opening a different catalog tears down the
// current backend, respawns it pointed at the new folder, and reloads
// the renderer so SignalR / query state reconnect cleanly against the
// new database. Cheaper than building true in-process catalog teardown
// (which would have to dispose every singleton that captured the old
// catalog by reference) and matches what VSCode does when a workspace
// switches.
let catalogSwapInProgress = false;

async function swapCatalog(newCatalogPath: string): Promise<void> {
  if (catalogSwapInProgress) return;
  if (!mainWindow) return;
  catalogSwapInProgress = true;
  try {
    // Navigate the main window to the loader (splash mode). Await the
    // load so the loader's `onSplashStatus` subscription is wired up
    // before we send the first status text — otherwise the IPC fires
    // into a window without a listener and the user sees stale content
    // from whatever page was previously mounted.
    await loadLoader(mainWindow, 'splash');

    // No-op when there isn't a backend yet (welcome → SPA transition);
    // skip the "Stopping…" status in that case so the user doesn't see
    // a flash of misleading text.
    if (dotnetProcess) {
      setSplashStatus(hostStrings.splash.stoppingBackend);
      await stopDotnetBackendAndAwait();
    }

    // Reset so the next spawn's `exit` handler treats a future crash as
    // unexpected. Without this, the planned teardown would persistently
    // suppress the auto-quit-on-backend-crash safety.
    stoppingDotnet = false;

    touchRecent(globalDataPath, newCatalogPath);

    setSplashStatus(hostStrings.splash.startingBackend);
    let kestrelUrl: string;
    try {
      kestrelUrl = await startDotnetBackend(newCatalogPath);
    } catch (err) {
      console.error('[catalog] swap failed during backend restart:', err);
      app.quit();
      return;
    }

    setSplashStatus(hostStrings.splash.loadingWorkspace);
    const newLoadUrl = app.isPackaged ? kestrelUrl : DEV_VITE_URL;
    cachedLoadUrl = newLoadUrl;
    await mainWindow.loadURL(newLoadUrl);
    lastOpenedCatalogPath = newCatalogPath;
  } finally {
    catalogSwapInProgress = false;
  }
}

// Open the system folder picker for an existing catalog. Refuses to
// swap if the chosen folder doesn't carry the marker file — that's the
// "New Catalog" path, not "Open Catalog".
async function pickAndOpenCatalog(parent: BrowserWindow): Promise<{ canceled: boolean; path?: string }> {
  const result = await electronDialog.showOpenDialog(parent, {
    title: hostStrings.catalog.openTitle,
    buttonLabel: hostStrings.catalog.openButtonLabel,
    properties: ['openDirectory'],
  });
  if (result.canceled || result.filePaths.length === 0) return { canceled: true };
  const chosen = result.filePaths[0];
  if (!hasCatalogMarker(chosen)) {
    await electronDialog.showMessageBox(parent, {
      type: 'error',
      title: hostStrings.catalog.invalidTitle,
      message: hostStrings.catalog.invalidMessage,
      detail: fillInvalidDetail(hostStrings.catalog.invalidDetail, chosen),
    });
    return { canceled: true };
  }
  void swapCatalog(chosen);
  return { canceled: false, path: chosen };
}

// Open the system folder picker (with create-directory enabled) for a
// new catalog. The backend lazily creates the marker file when it
// opens an empty folder, so we don't need to seed anything here. If
// the picked folder happens to already be a catalog, this acts like
// Open — same target either way.
async function pickAndCreateCatalog(parent: BrowserWindow): Promise<{ canceled: boolean; path?: string }> {
  const result = await electronDialog.showOpenDialog(parent, {
    title: hostStrings.catalog.newTitle,
    buttonLabel: hostStrings.catalog.newButtonLabel,
    properties: ['openDirectory', 'createDirectory'],
  });
  if (result.canceled || result.filePaths.length === 0) return { canceled: true };
  const chosen = result.filePaths[0];
  void swapCatalog(chosen);
  return { canceled: false, path: chosen };
}

// Filter to recents whose folder still exists on disk. The welcome
// screen and File → Open Recent submenu both render these directly,
// and clicking a recent triggers a backend respawn against the path;
// if the folder is gone the respawn fails and main.ts quits the app.
// Cheaper to hide the stale entry than to teach swapCatalog to recover.
ipcMain.handle('catalog.getRecents', (): RecentCatalog[] =>
  readRecents(globalDataPath).filter((r) => fs.existsSync(r.path)),
);

ipcMain.handle('catalog.openPicker', async (event) => {
  const parent = BrowserWindow.fromWebContents(event.sender);
  if (!parent) return { canceled: true };
  return await pickAndOpenCatalog(parent);
});

ipcMain.handle('catalog.newPicker', async (event) => {
  const parent = BrowserWindow.fromWebContents(event.sender);
  if (!parent) return { canceled: true };
  return await pickAndCreateCatalog(parent);
});

ipcMain.handle('catalog.openPath', async (_event, p: string) => {
  if (typeof p !== 'string' || p.length === 0) return { canceled: true };
  void swapCatalog(p);
  return { canceled: false, path: p };
});

// Respawn the .NET backend pointed at the same catalog. Called by the
// renderer after the user installs / uninstalls the CUDA runtime bundle
// — the new env vars (LD_LIBRARY_PATH / PATH) only take effect on a
// fresh process. Piggybacks on swapCatalog so the renderer's splash UX
// and SignalR reconnect logic stays identical to a catalog change.
//
// Fire-and-forget: swapCatalog navigates this window to the loader page,
// which destroys the renderer that invoked us. Awaiting swapCatalog before
// returning would leave Electron trying to deliver our reply to a dead
// renderer — observed as an EPIPE on the IPC socket. Resolve the IPC
// before the navigation kicks in; the renderer is about to be torn down
// anyway and doesn't need our "done" signal.
ipcMain.handle('backend.restart', () => {
  if (!lastOpenedCatalogPath) return { restarted: false };
  void swapCatalog(lastOpenedCatalogPath);
  return { restarted: true };
});

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

// Placeholder native menu installed before the renderer boots. On
// macOS this keeps the screen-top menubar populated (App/Edit/Window
// stock items + their accelerators) instead of blank during the
// .NET-startup + SPA-load window. On Win/Linux it registers the same
// stock accelerators globally even though the bar itself is hidden by
// `frame: false`. Replaced atomically by `menu.set` once the renderer
// publishes its localized template via state/menu.ts.
function setPlaceholderApplicationMenu(): void {
  const isMac = process.platform === 'darwin';
  const template: Electron.MenuItemConstructorOptions[] = [
    ...(isMac ? [{ role: 'appMenu' as const }] : []),
    { role: 'editMenu' },
    { role: 'windowMenu' },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

// Renderer-published menu template. Plain data crosses the IPC boundary
// so main never imports i18next; the renderer resolves labelKey → text
// before sending and re-sends on locale change.
type MenuNode =
  | { kind: 'submenu'; labelKey: string; children: MenuNode[]; macRole?: string }
  | { kind: 'item'; labelKey: string; commandId: string; accelerator?: string; enabled?: boolean }
  | { kind: 'role'; role: string; labelKey?: string }
  | { kind: 'separator' };

function toElectronTemplate(
  nodes: MenuNode[],
  sender: Electron.WebContents,
): Electron.MenuItemConstructorOptions[] {
  return nodes.map((n): Electron.MenuItemConstructorOptions => {
    if (n.kind === 'separator') return { type: 'separator' };
    if (n.kind === 'role') {
      return {
        role: n.role as Electron.MenuItemConstructorOptions['role'],
        ...(n.labelKey ? { label: n.labelKey } : {}),
      };
    }
    if (n.kind === 'submenu') {
      // macRole pulls in Electron's stock localized submenus (App/Edit/
      // Window) when present, ignoring `children` — those submenus are
      // populated by Electron itself.
      if (n.macRole) {
        return {
          role: n.macRole as Electron.MenuItemConstructorOptions['role'],
          label: n.labelKey,
        };
      }
      return { label: n.labelKey, submenu: toElectronTemplate(n.children, sender) };
    }
    return {
      label: n.labelKey,
      accelerator: n.accelerator,
      enabled: n.enabled !== false,
      // Route native-menu clicks back to the originating renderer; the
      // renderer's command registry (src/commands/registry.ts) is the
      // single source of truth for what each commandId does.
      click: () => sender.send('menu.command', n.commandId),
    };
  });
}

ipcMain.handle('menu.set', (event, tree: MenuNode[]) => {
  const menu = Menu.buildFromTemplate(toElectronTemplate(tree, event.sender));
  Menu.setApplicationMenu(menu);
});

// ───────────────────────── Loader (in-window) ─────────────────────────

// Splash, welcome, and the SPA all render inside the same main
// BrowserWindow — there is no floating splash window. They share one
// webPreferences set (contextIsolation + preload), which is what
// allows the navigation between the loader page and the SPA URL.
//
// Splash and welcome are two modes of a single React entry
// (loader.html, built from the ClientApp/src/loader/ tree). Vite's
// multi-entry build emits loader.html alongside index.html in
// wwwroot/; in dev we load it through Vite (HMR comes for free), in
// prod we load it straight off disk because Kestrel isn't up yet
// (first launch) or is being respawned (catalog swap).
//
// The "splash:status" channel is the same one the loader subscribes
// to via the preload-exposed `onSplashStatus` bridge.

type LoaderMode = 'splash' | 'welcome';

function loaderSearchParams(mode: LoaderMode): string {
  const params = new URLSearchParams({ mode });
  // Pass the last-known resolved theme so the loader paints in the
  // user-chosen scheme rather than prefers-color-scheme — relevant on
  // catalog swap (SPA had already resolved the theme) and on app
  // launch after the user picked light/dark in settings. Omitted on
  // truly cold start; the loader falls back to prefers-color-scheme.
  if (loaderTheme) params.set('theme', loaderTheme);
  return params.toString();
}

function loadLoader(win: BrowserWindow, mode: LoaderMode): Promise<void> {
  const search = loaderSearchParams(mode);
  if (!app.isPackaged) {
    return win.loadURL(`${DEV_VITE_URL}/loader.html?${search}`);
  }
  // Packaged: wwwroot is part of the .NET publish output, shipped via
  // electron-builder's extraResources. file:// load means the loader's
  // relative asset URLs (base: './' in vite.config) resolve against
  // the same directory.
  const loaderPath = path.join(
    process.resourcesPath,
    'backend',
    'wwwroot',
    'loader.html',
  );
  return win.loadFile(loaderPath, { search });
}

// Status updates target whatever page is currently mounted in
// mainWindow; if it isn't the loader in splash mode the IPC is
// harmless (only the loader subscribes). Callers await loadLoader
// before the first status push so the renderer's handler is wired up.
function setSplashStatus(text: string): void {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send('splash:status', text);
  }
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

// App version (read from package.json by Electron). Sync so preload
// can capture it before any renderer JS runs and expose it as a
// plain field on electronHost — the About dialog reads it without
// needing an async fetch.
ipcMain.on('app.version.sync', (event) => {
  event.returnValue = app.getVersion();
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
    dialog.setTitle(`DatumV — ${spec.kind}`);

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

// Native OS context menu. The renderer hands us a spec (labels + ids
// per item, plus optional separators); we build the Menu, pop it at
// the cursor on the sender's window, and resolve with the clicked
// item's id (or null if dismissed without a selection). Action
// dispatch lives in the renderer — main just plays template + popup
// so the menu picks up the OS look-and-feel (Win11 acrylic, native
// macOS rendering, etc.).
interface ContextMenuItemSpec {
  id: string;
  label: string;
  accelerator?: string;
  enabled?: boolean;
}
interface ContextMenuSpec {
  items: Array<ContextMenuItemSpec | { type: 'separator' }>;
}

ipcMain.handle(
  'contextMenu.show',
  (event, spec: ContextMenuSpec): Promise<string | null> => {
    const win = BrowserWindow.fromWebContents(event.sender);
    if (!win) return Promise.resolve(null);
    return new Promise<string | null>((resolve) => {
      let settled = false;
      const settle = (result: string | null) => {
        if (settled) return;
        settled = true;
        resolve(result);
      };
      const template: Electron.MenuItemConstructorOptions[] = spec.items.map(
        (it): Electron.MenuItemConstructorOptions => {
          if ('type' in it) {
            return { type: 'separator' };
          }
          return {
            label: it.label,
            accelerator: it.accelerator,
            enabled: it.enabled !== false,
            click: () => settle(it.id),
          };
        },
      );
      const menu = Menu.buildFromTemplate(template);
      // popup's `callback` fires after the menu closes regardless of
      // whether an item was clicked. Electron fires the item's `click`
      // first, so a click resolves the promise before this no-op
      // callback runs; the null path only takes effect when the user
      // dismissed without picking anything.
      menu.popup({
        window: win,
        callback: () => settle(null),
      });
    });
  },
);

// Native file/folder open dialog. Parented to the sender's window so it
// inherits modality. The future ingest UI is the planned consumer.
ipcMain.handle('fs.showOpenDialog', async (event, options: Electron.OpenDialogOptions) => {
  const parentWin = BrowserWindow.fromWebContents(event.sender);
  if (!parentWin) return { canceled: true, filePaths: [] };
  return await electronDialog.showOpenDialog(parentWin, options);
});

// Native file save dialog. Parented to the sender's window so it inherits
// modality. The SPA's editor Ctrl+S handler uses this for scratch SQL
// tabs — the renderer hands in the catalog root as defaultPath; main
// returns the absolute path the user chose, then the renderer writes
// through PUT /api/files/contents using a catalog-relative form.
ipcMain.handle('fs.showSaveDialog', async (event, options: Electron.SaveDialogOptions) => {
  const parentWin = BrowserWindow.fromWebContents(event.sender);
  if (!parentWin) return { canceled: true, filePath: undefined };
  return await electronDialog.showSaveDialog(parentWin, options);
});

// Open a URL in the user's default OS browser. Restricted to http(s) —
// renderer code shouldn't be able to launch arbitrary protocol handlers
// (file://, mailto:, custom-scheme:) that the user didn't initiate. This
// is the documented Electron guard against compromised-renderer link
// injection; consumers (e.g. LicenseDialog) only ever pass URLs that
// originated in trusted markdown bundled with the app.
// Opens THIRD-PARTY-NOTICES.txt in the user's default text viewer.
// Path differs between dev and prod:
//   - prod: shipped via electron-builder's extraResources at
//     `<resources>/THIRD-PARTY-NOTICES.txt`
//   - dev:  generated at the repo root by the npm script; resolved
//     two levels up from main.ts (electron/dist/main.js → electron/ →
//     src/Heliosoph.DatumV.Web/ → src/ → repo root). The constant
//     mirrors what `generate-third-party-notices.mjs` writes.
ipcMain.handle('thirdPartyNotices.open', async () => {
  const noticesPath = app.isPackaged
    ? path.join(process.resourcesPath, 'THIRD-PARTY-NOTICES.txt')
    : path.resolve(__dirname, '..', '..', '..', '..', 'THIRD-PARTY-NOTICES.txt');
  if (!fs.existsSync(noticesPath)) {
    console.warn('[notices] file not found at', noticesPath);
    // Fall back to opening the licenses folder on github — user still
    // gets attribution info even if the local file is missing (e.g.
    // dev hasn't run `npm run generate:notices` yet).
    await shell.openExternal('https://github.com/HeliosophLLC/DatumV/blob/main/THIRD-PARTY-NOTICES.txt');
    return;
  }
  const err = await shell.openPath(noticesPath);
  if (err) {
    console.error('[notices] shell.openPath failed:', err);
  }
});

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

// ---------------------------------------------------------------------
// Update checking. Notify-only: we never auto-download or auto-install.
// The renderer (state/updater.ts) listens on `updater:status` and surfaces
// "v0.1.1 available" in the Help menu + a chip in the title bar; clicking
// the chip opens the GitHub Release page in the user's default browser.
// Manual checks come in via the IPC channel below.
//
// While the publish-target repo is private, the GitHub Releases API
// requires an auth token and electron-updater's anonymous probe will get
// a 404 — surfaces as an `error` status with the underlying message.
// Once the repo is public (or migration to a public repo lands), the
// same code lights up automatically.
// ---------------------------------------------------------------------
let lastUpdateStatus: UpdateStatus = { kind: 'idle' };

function buildReleaseUrl(version: string): string {
  // electron-updater's GitHub provider doesn't surface the canonical
  // release page URL on update-available — construct it from owner +
  // repo + tag (the tag is `v<version>` per release.yml's convention).
  return `https://github.com/HeliosophLLC/DatumV/releases/tag/v${version}`;
}

function broadcastUpdateStatus(status: UpdateStatus): void {
  lastUpdateStatus = status;
  for (const w of BrowserWindow.getAllWindows()) {
    if (w.isDestroyed()) continue;
    w.webContents.send('updater:status', status);
  }
}

autoUpdater.autoDownload = false;
autoUpdater.autoInstallOnAppQuit = false;
autoUpdater.logger = {
  info: (m: unknown) => console.log('[updater]', m),
  warn: (m: unknown) => console.warn('[updater]', m),
  error: (m: unknown) => console.error('[updater]', m),
  debug: () => undefined,
};

autoUpdater.on('checking-for-update', () => {
  broadcastUpdateStatus({ kind: 'checking' });
});
autoUpdater.on('update-available', (info) => {
  broadcastUpdateStatus({
    kind: 'available',
    version: info.version,
    releaseUrl: buildReleaseUrl(info.version),
  });
});
autoUpdater.on('update-not-available', () => {
  broadcastUpdateStatus({
    kind: 'not-available',
    currentVersion: app.getVersion(),
  });
});
autoUpdater.on('error', (err) => {
  broadcastUpdateStatus({
    kind: 'error',
    message: err instanceof Error ? err.message : String(err),
  });
});

function runUpdateCheck(): void {
  if (!app.isPackaged) {
    // electron-updater refuses to probe in dev (no embedded
    // app-update.yml). Tell the renderer so the menu/chip don't sit
    // in `checking` forever during local dev runs.
    broadcastUpdateStatus({
      kind: 'error',
      message: 'Update check is disabled in development builds.',
    });
    return;
  }
  void autoUpdater.checkForUpdates().catch((err: unknown) => {
    broadcastUpdateStatus({
      kind: 'error',
      message: err instanceof Error ? err.message : String(err),
    });
  });
}

ipcMain.handle('updater:check', () => {
  runUpdateCheck();
});
// Synchronous accessor so a window mounting after a status broadcast
// can immediately pick up the most recent state instead of waiting
// for the next event.
ipcMain.on('updater:status.sync', (event) => {
  event.returnValue = lastUpdateStatus;
});

app.whenReady().then(async () => {
  setPlaceholderApplicationMenu();
  const win = createMainWindow();

  // Always land on welcome — the user explicitly picks (or
  // re-picks) a catalog rather than us auto-opening the last one.
  // This makes the launch a single, predictable surface that
  // surfaces recents prominently, and means the backend doesn't
  // spin up until the user has chosen what to point it at. The
  // legacy-layout migration still runs so an upgrading user sees
  // their existing catalog in the recents list.
  seedLegacyRecentsIfNeeded(globalDataPath);
  await loadLoader(win, 'welcome');

  // Initial update check. Delayed so the welcome screen renders first —
  // a network probe at the exact moment the user is staring at the
  // window is the wrong time to surface anything. The renderer reads
  // the most recent status via the sync IPC when it mounts so this
  // delay doesn't leave the UI in `idle` if the check resolves fast.
  setTimeout(runUpdateCheck, 5_000);

  // Mac dock re-activation: if the user closed every window
  // (window-all-closed doesn't quit on darwin), reopen the welcome
  // screen on dock click. If a catalog session was previously
  // active and is still loaded — cachedLoadUrl is set by swapCatalog
  // — restore that instead so a mid-session dock click doesn't
  // throw away their state.
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length > 0) return;
    const next = createMainWindow();
    if (cachedLoadUrl) {
      void next.loadURL(cachedLoadUrl);
    } else {
      void loadLoader(next, 'welcome');
    }
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
