// Export action: orchestrates the "export this query's results to a file"
// flow that the toolbar Download button and the Run > Export menu item
// both trigger.
//
// Flow (Electron-only):
//   1. Resolve the active query tab. Reject if it isn't a `kind: 'sql'`
//      tab — function tabs run synthesised scripts and don't have a
//      single SQL surface to ship to COPY.
//   2. Show a native save dialog (host's showSaveDialog IPC) seeded with
//      the catalog root + the tab's title + a registered format
//      extension. Filters come from `EXPORT_FORMATS` below.
//   3. Build a `COPY (<sql>) TO '<path>'` statement and hand it to the
//      existing `runTab` machinery with origin='export'. The COPY plan
//      runs through the same NDJSON pipeline as any other statement, so
//      progress, cancellation, and error reporting come for free.
//
// No new state lives in this module today — the per-tab status is
// already represented on `executionsState` (via the new `origin` field).
// If we later add cross-tab export state (a queue, a history) it goes
// in a valtio proxy declared here.

import { api } from '@/api';
import { resolveRunSql } from '@/state/activeEditor';
import { runTab } from '@/state/execution';
import { findLeaf, panesState, type Tab } from '@/state/tabs';

/**
 * Static catalog of formats the COPY planner accepts. Mirrors the
 * `IExportFormatRegistry.Default` set on the backend; today Parquet is
 * the only entry. When the second format ships we'll replace this with a
 * `/api/export/formats` fetch wired through `state/export.ts` — the
 * static stub keeps the slice tight.
 */
export interface ExportFormatDescriptor {
  /** Canonical lower-case name accepted by the COPY planner's FORMAT option. */
  name: string;
  /** Save-dialog label (already localised before this point). */
  displayName: string;
  /** File extensions, including the leading dot. First entry is the default. */
  extensions: string[];
}

export const EXPORT_FORMATS: ExportFormatDescriptor[] = [
  { name: 'parquet', displayName: 'Apache Parquet', extensions: ['.parquet'] },
];

/**
 * Begins an export for the active SQL tab in `leafId`. No-op when the
 * active tab isn't an SQL tab, when no tab is active, or when the user
 * cancels the save dialog.
 */
export async function beginExport(leafId: string): Promise<void> {
  const eh = window.electronHost;
  if (!eh) return;

  const leaf = findLeaf(panesState.root, leafId);
  if (!leaf || leaf.activeTabId === null) return;
  const tab = leaf.tabs.find((t) => t.id === leaf.activeTabId);
  if (!tab) return;

  // Scoped to SQL tabs only. Function tabs synthesise their script from
  // form state and would need their own export path; pinned tabs
  // (models / settings / docs) have no SQL at all.
  if (tab.kind !== 'sql') return;

  const defaultDir = await tryGetCatalogRoot();
  const filename = suggestedFilename(tab);
  const result = await eh.showSaveDialog({
    defaultPath: defaultDir ? `${defaultDir}/${filename}` : filename,
    filters: EXPORT_FORMATS.map((f) => ({
      name: f.displayName,
      // Electron's filter wants extensions without the leading dot.
      extensions: f.extensions.map((e) => e.replace(/^\./, '')),
    })),
    properties: ['showOverwriteConfirmation', 'createDirectory'],
  });
  if (result.canceled || !result.filePath) return;

  const sql = buildCopySql(tab, leafId, result.filePath);
  void runTab(tab.id, sql, { origin: 'export' });
}

/**
 * Catalog root for seeding the save dialog. Returns null on failure —
 * the dialog still opens, it just starts in whatever folder Electron
 * defaults to.
 */
async function tryGetCatalogRoot(): Promise<string | null> {
  try {
    const root = await api.files.getRoot();
    return root.catalogRoot ?? null;
  } catch (err) {
    console.warn('[export] catalog root lookup failed', err);
    return null;
  }
}

/**
 * Default filename for the save dialog: the tab's title, with the
 * preferred format's extension appended if it isn't already there.
 * Strips `.sql` from saved query files so the dialog suggests
 * `MyQuery.parquet` rather than `MyQuery.sql.parquet`.
 */
function suggestedFilename(tab: Tab): string {
  const ext = EXPORT_FORMATS[0].extensions[0];
  let base = tab.title;
  if (base.toLowerCase().endsWith('.sql')) {
    base = base.slice(0, -4);
  }
  if (base.toLowerCase().endsWith(ext.toLowerCase())) {
    return base;
  }
  return `${base}${ext}`;
}

/**
 * Wraps the tab's SQL (or the user's selection, via `resolveRunSql`) in
 * a COPY statement that writes to `targetPath`. The format is inferred
 * from the path's extension at the planner — no FORMAT option required.
 *
 * Path escaping: backslashes survive verbatim inside the single-quoted
 * SQL literal (parser only treats `'` as special); single quotes in a
 * path are rare but legal on every supported OS, so we double them.
 */
function buildCopySql(tab: Tab, leafId: string, targetPath: string): string {
  const innerSql = resolveRunSql(tab.sql, leafId).trim().replace(/;+\s*$/u, '');
  const escapedPath = targetPath.replace(/'/g, "''");
  return `COPY (${innerSql}) TO '${escapedPath}'`;
}
