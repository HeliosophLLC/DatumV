import type { JsonCell } from './execution';

// Column display modes — the registry of "ways to render this kind of cell"
// shared by the results-pane header chip (per-column override) and the
// settings page (per-kind default).
//
// Adding a new kind:
//   1. Add an entry below with a stable `kindKey`, an `appliesTo` predicate,
//      a `defaultMode` baseline, and one option per mode.
//   2. Add the matching i18n entries — labels/descriptions live under
//      `query.columnModes.<kindKey>.<modeId>.{label, description}`; the
//      settings section uses `settings.columnModes.kinds.<kindKey>` for
//      the section's kind-row label.
//   3. Wire the renderers in `ResultsPane.tsx`'s `CellValue` dispatch.
//
// Mode ids and kindKeys are persisted to the server's settings.json, so
// don't rename them without a migration.

export interface ColumnDisplayModeOption {
  id: string;
  /** i18n key (relative to the `query` namespace). */
  labelKey: string;
  /** Optional secondary line shown in the chip popover + settings rows. */
  descriptionKey?: string;
}

export interface ColumnDisplayModeDef {
  /**
   * Stable key used as the dictionary key in settings storage. Keep
   * snake_case so it matches `JsonCell.kind` values exactly when there's
   * a 1:1 correspondence.
   */
  kindKey: string;
  /**
   * Decides whether this mode set applies to a given cell. The caller
   * (results-pane column-mode detection) typically passes the first non-
   * null cell of the column.
   */
  appliesTo: (cell: JsonCell) => boolean;
  /** Mode id used when the user has no override and settings has no default. */
  defaultMode: string;
  /** i18n key (relative to the `settings` namespace) — settings-page row label. */
  settingsLabelKey: string;
  options: ColumnDisplayModeOption[];
}

export const COLUMN_MODE_REGISTRY: ColumnDisplayModeDef[] = [
  {
    kindKey: 'numeric_array',
    appliesTo: (cell) => cell.kind === 'numeric_array',
    defaultMode: 'stats',
    settingsLabelKey: 'columnModes.kinds.numericArray',
    options: [
      {
        id: 'stats',
        labelKey: 'columnModes.numeric_array.stats.label',
        descriptionKey: 'columnModes.numeric_array.stats.description',
      },
      {
        id: 'histogram',
        labelKey: 'columnModes.numeric_array.histogram.label',
        descriptionKey: 'columnModes.numeric_array.histogram.description',
      },
      {
        id: 'preview',
        labelKey: 'columnModes.numeric_array.preview.label',
        descriptionKey: 'columnModes.numeric_array.preview.description',
      },
    ],
  },
];

export function findColumnModeDef(
  rows: readonly JsonCell[][],
  colIdx: number,
  sampleLimit: number,
): ColumnDisplayModeDef | null {
  const limit = Math.min(rows.length, sampleLimit);
  for (let r = 0; r < limit; r++) {
    const cell = rows[r]?.[colIdx];
    if (!cell || cell.kind === 'null') continue;
    for (const def of COLUMN_MODE_REGISTRY) {
      if (def.appliesTo(cell)) return def;
    }
    return null;
  }
  return null;
}

/**
 * Resolves the mode for a column given (in priority order):
 *   1. an explicit per-column override (chip selection)
 *   2. the user's per-kind default from settings
 *   3. the registry's baseline `defaultMode`.
 */
export function resolveColumnMode(
  def: ColumnDisplayModeDef,
  override: string | undefined,
  settingsDefaults: Readonly<Record<string, string>>,
): string {
  if (override !== undefined) return override;
  const fromSettings = settingsDefaults[def.kindKey];
  if (fromSettings !== undefined && def.options.some((o) => o.id === fromSettings)) {
    return fromSettings;
  }
  return def.defaultMode;
}
