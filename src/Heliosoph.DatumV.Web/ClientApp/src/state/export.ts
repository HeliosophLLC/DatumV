// Export action: orchestrates the "export this query's results to a file"
// flow that the toolbar Download button and the Run > Export menu item
// both trigger.
//
// Flow (Electron-only):
//   1. Resolve the active query tab. Reject if it isn't a `kind: 'sql'`
//      tab — function tabs run synthesised scripts and don't have a
//      single SQL surface to ship to COPY.
//   2. Open the configuration dialog (ExportDialog). The user picks a
//      format (Parquet / CSV) and tunes the format-specific options the
//      COPY planner accepts. Cancel here exits the flow.
//   3. Show a native save dialog (host's showSaveDialog IPC) seeded with
//      the catalog root + the tab's title + the chosen format's
//      extension. The format-picker dropdown in the save dialog still
//      shows every registered format so the user can change their mind
//      one more time without re-opening the config dialog — the path
//      extension wins at the COPY planner.
//   4. Build a `COPY (<sql>) TO '<path>' (<options>)` statement and hand
//      it to the existing `runTab` machinery with origin='export'. The
//      COPY plan runs through the same NDJSON pipeline as any other
//      statement, so progress, cancellation, and error reporting come
//      for free.
//
// No new state lives in this module today — the per-tab status is
// already represented on `executionsState` (via the `origin` field). If
// we later add cross-tab export state (a queue, a history) it goes in a
// valtio proxy declared here.

import { api } from '@/api';
import type {
  ExportDialogResult,
  ExportOptionValues,
} from '@/components/dialogs/ExportDialog';
import { openDialog } from '@/state/dialogs';
import { resolveRunSql } from '@/state/activeEditor';
import { runTab } from '@/state/execution';
import { findLeaf, panesState, type Tab } from '@/state/tabs';

/**
 * Static catalog of formats the COPY planner accepts. Mirrors the
 * `IExportFormatRegistry.Default` set on the backend. When a third
 * format ships we'll replace this with a `/api/export/formats` fetch
 * wired through `state/export.ts`; the static stub keeps the slice
 * tight.
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
  { name: 'csv', displayName: 'CSV (Comma-Separated Values)', extensions: ['.csv'] },
];

/**
 * Begins an export for the active SQL tab in `leafId`. No-op when the
 * active tab isn't an SQL tab, when no tab is active, or when the user
 * cancels either the configuration dialog or the save dialog.
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

  // Configuration first. Cancel / X-close both short-circuit before the
  // native save dialog opens, so the user never sees their last-used
  // folder land in the picker for an export they didn't end up running.
  const { result: dialogPromise } = openDialog<ExportDialogResult>({
    kind: 'export',
  });
  const dialogResult = await dialogPromise;
  if (!dialogResult || dialogResult.action !== 'continue') return;
  const { format, options } = dialogResult;

  const descriptor =
    EXPORT_FORMATS.find((f) => f.name === format) ?? EXPORT_FORMATS[0];

  const defaultDir = await tryGetCatalogRoot();
  const filename = suggestedFilename(tab, descriptor);
  const saveResult = await eh.showSaveDialog({
    defaultPath: defaultDir ? `${defaultDir}/${filename}` : filename,
    // Lead with the chosen format so the picker pre-selects its
    // extension; keep the other formats available below so a user who
    // changes their mind between formats with the same option set can
    // switch on the spot. The COPY planner re-infers the format from the
    // path extension if it disagrees with the radio choice.
    filters: orderedFilters(descriptor),
    properties: ['showOverwriteConfirmation', 'createDirectory'],
  });
  if (saveResult.canceled || !saveResult.filePath) return;

  const sql = buildCopySql(tab, leafId, saveResult.filePath, format, options);
  void runTab(tab.id, sql, { origin: 'export' });
}

/**
 * Save-dialog filters, with the user's chosen format first so the
 * picker pre-selects its extension. Electron's filter shape wants the
 * extensions without the leading dot.
 */
function orderedFilters(preferred: ExportFormatDescriptor) {
  const rest = EXPORT_FORMATS.filter((f) => f.name !== preferred.name);
  return [preferred, ...rest].map((f) => ({
    name: f.displayName,
    extensions: f.extensions.map((e) => e.replace(/^\./, '')),
  }));
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
 * chosen format's extension appended if it isn't already there. Strips
 * `.sql` from saved query files so the dialog suggests
 * `MyQuery.parquet` rather than `MyQuery.sql.parquet`.
 */
function suggestedFilename(tab: Tab, descriptor: ExportFormatDescriptor): string {
  const ext = descriptor.extensions[0];
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
 * a COPY statement that writes to `targetPath`. The format is forced via
 * the FORMAT option to match the user's dialog choice — independent of
 * the file extension, which Electron may have changed if the user typed
 * a different one into the save-dialog filename box.
 *
 * Path escaping: backslashes survive verbatim inside the single-quoted
 * SQL literal (parser only treats `'` as special); single quotes in a
 * path are rare but legal on every supported OS, so we double them.
 */
function buildCopySql(
  tab: Tab,
  leafId: string,
  targetPath: string,
  format: string,
  options: ExportOptionValues,
): string {
  const innerSql = resolveRunSql(tab.sql, leafId).trim().replace(/;+\s*$/u, '');
  const escapedPath = targetPath.replace(/'/g, "''");
  // FORMAT goes first so a parse error in a later option still names a
  // resolved format in the message. Each option's value is emitted as a
  // SQL literal — numbers unquoted, strings single-quoted with `'`
  // doubled. The grammar treats `<value> ::= <string-literal> |
  // <number-literal> | <identifier>`; we pick `<string-literal>` for
  // non-numeric values because the FORMAT identifier slot is the only
  // identifier the grammar binds, and most option values (delimiter
  // characters, line-ending names, NULL strings) aren't valid bare
  // identifiers anyway.
  const optionParts: string[] = [`FORMAT ${format}`];
  for (const [key, value] of Object.entries(options)) {
    optionParts.push(`${key} ${formatOptionValue(value)}`);
  }
  const optionsClause = `(${optionParts.join(', ')})`;
  return `COPY (${innerSql}) TO '${escapedPath}' ${optionsClause}`;
}

function formatOptionValue(value: string | number): string {
  if (typeof value === 'number') return String(value);
  // Single-quoted string literal with `'` doubled. The parser also
  // accepts identifiers but our value strings include things like
  // delimiter characters and line-ending names — string-quoting them
  // uniformly keeps the SQL builder honest.
  return `'${value.replace(/'/g, "''")}'`;
}
