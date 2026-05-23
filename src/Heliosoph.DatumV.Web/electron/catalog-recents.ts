import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

// Workspace-style catalogs: one folder = one catalog, recorded in a
// per-machine recents list under the global-data path. The user picks
// which catalog to open through the File menu; the backend is spawned
// with DATUMV_CATALOG_PATH pointing at the chosen folder.

export interface RecentCatalog {
  path: string;
  displayName: string;
  lastOpenedAt: string;
}

// Marker file the engine writes when it opens a catalog. Mirrors
// Heliosoph.DatumV.Catalog.CatalogStore.DefaultFileName in C# — kept in
// sync by convention since the two halves never communicate the literal.
export const CATALOG_MARKER = '.datum-catalog.json';

const RECENTS_FILE = 'recents.json';
const MAX_RECENTS = 12;

// Matches Environment.SpecialFolder.LocalApplicationData on each
// platform so the backend (which reads .NET's special folder) and the
// shell agree on a single global-data directory without round-tripping
// the path between them.
export function computeGlobalDataPath(): string {
  if (process.platform === 'win32') {
    const local = process.env.LOCALAPPDATA
      ?? path.join(os.homedir(), 'AppData', 'Local');
    return path.join(local, 'Heliosoph.DatumV');
  }
  if (process.platform === 'darwin') {
    return path.join(os.homedir(), 'Library', 'Application Support', 'Heliosoph.DatumV');
  }
  const xdg = process.env.XDG_DATA_HOME
    ?? path.join(os.homedir(), '.local', 'share');
  return path.join(xdg, 'Heliosoph.DatumV');
}

function recentsPath(globalDataPath: string): string {
  return path.join(globalDataPath, RECENTS_FILE);
}

export function readRecents(globalDataPath: string): RecentCatalog[] {
  const filepath = recentsPath(globalDataPath);
  if (!fs.existsSync(filepath)) return [];
  try {
    const raw = fs.readFileSync(filepath, 'utf8');
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((e): e is RecentCatalog =>
      typeof e === 'object'
      && e !== null
      && typeof e.path === 'string'
      && typeof e.displayName === 'string'
      && typeof e.lastOpenedAt === 'string'
    );
  } catch (err) {
    console.error('[catalog] failed to read recents.json:', err);
    return [];
  }
}

function writeRecents(globalDataPath: string, recents: RecentCatalog[]): void {
  fs.mkdirSync(globalDataPath, { recursive: true });
  const filepath = recentsPath(globalDataPath);
  const tmp = filepath + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(recents, null, 2), 'utf8');
  fs.renameSync(tmp, filepath);
}

// Move (or add) the given catalog path to the head of the recents list
// and persist. Returns the updated list. Same-path normalization on
// Windows means C:\Foo and c:/foo/ collapse to one entry.
export function touchRecent(globalDataPath: string, catalogPath: string): RecentCatalog[] {
  const normalized = path.normalize(catalogPath);
  const same = (a: string, b: string): boolean =>
    process.platform === 'win32'
      ? a.toLowerCase() === b.toLowerCase()
      : a === b;
  const filtered = readRecents(globalDataPath).filter((r) => !same(path.normalize(r.path), normalized));
  const entry: RecentCatalog = {
    path: normalized,
    displayName: path.basename(normalized) || normalized,
    lastOpenedAt: new Date().toISOString(),
  };
  const next = [entry, ...filtered].slice(0, MAX_RECENTS);
  writeRecents(globalDataPath, next);
  return next;
}

export function hasCatalogMarker(folder: string): boolean {
  try {
    return fs.existsSync(path.join(folder, CATALOG_MARKER));
  } catch {
    return false;
  }
}

// Migration helper run at boot. The app always lands on the welcome
// screen now; this just guarantees that a user upgrading from the
// pre-workspace layout sees their existing catalog in the recents
// list instead of having to hunt for it via "Open Catalog".
//
// Idempotent: skips when recents already contains the legacy folder
// (or any other entry — once the user has touched recents at all, we
// trust them to manage it).
export function seedLegacyRecentsIfNeeded(globalDataPath: string): void {
  if (readRecents(globalDataPath).length > 0) return;
  if (!hasCatalogMarker(globalDataPath)) return;
  touchRecent(globalDataPath, globalDataPath);
}
