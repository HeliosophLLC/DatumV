import { useEffect, useState } from 'react';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { Download } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { resolveDialog } from '@/state/dialogs';

// Export-configuration dialog. Launched by `state/export.beginExport`
// before the native save dialog so the user can pick the format and
// tune the format-specific options the COPY planner accepts. Each
// format's option set mirrors `IExportFormat.ResolveDisposition` on the
// backend — see docs/technical/copy-and-export.md for the canonical
// reference.
//
// The dialog *only* gathers configuration: the actual save-dialog (file
// path picker) opens after this dialog resolves. Splitting the flow this
// way means a user who wants Parquet with zstd doesn't have to pick the
// path first, see the path land in their last-used folder, and then
// realise they wanted CSV — they decide the format upfront, then pick
// where to put it.
//
// Resolves the dialog with:
//   - `{ action: 'continue', format, options }` on the Continue button
//   - `{ action: 'cancel' }` on Cancel or Escape
//   - `null` (synthesised by the coordinator) on window X-close — the
//      caller treats the same as 'cancel'.

export interface ExportDialogProps {
  requestId: string;
}

export type ExportDialogAction = 'continue' | 'cancel';

export interface ExportDialogResult {
  action: ExportDialogAction;
  // Empty strings for the format / options fields when action === 'cancel'.
  format: string;
  options: ExportOptionValues;
}

// Flat record of option names → SQL literal forms, ready to splice into a
// COPY `(...)` block. Numbers stay as numbers so the SQL builder can emit
// them unquoted; bools / identifiers / delimiters are pre-encoded as
// strings so the caller doesn't have to know format-specific quoting.
export type ExportOptionValues = Record<string, string | number>;

type FormatId = 'parquet' | 'csv' | 'json' | 'arrow';

// Parquet config state — kept as the UI surface (strings / booleans) and
// translated to ExportOptionValues at submit time. Empty strings mean
// "let the backend pick its default" so the SQL builder can skip the
// option entirely.
interface ParquetState {
  compression: string;             // 'snappy' | 'none' | 'gzip' | 'zstd' | 'brotli' | 'lz4' | ''
  compressionLevel: string;        // '' | '0' | '1' | '2' | '3'
  rowGroupSize: string;            // '' | numeric string
}

interface CsvState {
  header: boolean;
  delimiter: CsvDelimiter;         // preset shortcut; 'custom' opens a single-char text input
  customDelimiter: string;         // only consulted when delimiter === 'custom'
  lineEnding: 'lf' | 'crlf';
  nullString: string;              // '' = default empty-field convention
}

type CsvDelimiter = ',' | ';' | 'tab' | '|' | 'custom';

interface JsonState {
  // 'array' → one top-level array, one object per row.
  // 'lines' → newline-delimited JSON (JSONL / ndjson).
  shape: 'array' | 'lines';
  indent: boolean;                 // only valid with shape === 'array'
}

const PARQUET_COMPRESSIONS = ['snappy', 'none', 'gzip', 'zstd', 'brotli', 'lz4'] as const;

export function ExportDialog({ requestId }: ExportDialogProps) {
  const { t } = useTranslation('dialogs');

  const [format, setFormat] = useState<FormatId>('parquet');
  const [parquet, setParquet] = useState<ParquetState>({
    compression: 'snappy',
    compressionLevel: '',
    rowGroupSize: '',
  });
  const [csv, setCsv] = useState<CsvState>({
    header: true,
    delimiter: ',',
    customDelimiter: '',
    lineEnding: 'lf',
    nullString: '',
  });
  const [json, setJson] = useState<JsonState>({
    shape: 'array',
    indent: false,
  });
  const [validationError, setValidationError] = useState<string | null>(null);

  function resolve(action: ExportDialogAction) {
    if (action === 'cancel') {
      resolveDialog<ExportDialogResult>(requestId, {
        action: 'cancel',
        format: '',
        options: {},
      });
      return;
    }
    const built = buildOptions(format, parquet, csv, json);
    if (built.error) {
      setValidationError(built.error);
      return;
    }
    resolveDialog<ExportDialogResult>(requestId, {
      action: 'continue',
      format,
      options: built.options,
    });
  }

  // Keyboard shortcuts: Esc cancels, Enter advances to the save dialog.
  // The Enter binding matches the rest of the dialog suite (DeleteFile,
  // UnsavedChanges) — the user can scan-read the form, hit Enter, and
  // land in the save dialog without touching the mouse.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.defaultPrevented) return;
      if (e.key === 'Escape') {
        e.preventDefault();
        resolve('cancel');
      } else if (e.key === 'Enter') {
        // Don't hijack Enter inside a multi-line input — there are none
        // here today, but defensive against future fields. Hitting Enter
        // inside a numeric/text input still fires the form-level submit
        // semantics that we want.
        const tag = (e.target as HTMLElement | null)?.tagName;
        if (tag === 'TEXTAREA') return;
        e.preventDefault();
        resolve('continue');
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [requestId, format, parquet, csv, json]);

  return (
    <div className="flex flex-1 flex-col overflow-hidden select-none">
      <header className="border-b px-6 py-4">
        <h1 className="flex items-center gap-2 text-base font-medium">
          <Download className="text-primary size-4" />
          {t('export.title')}
        </h1>
        <p className="text-muted-foreground mt-1 text-xs">{t('export.subtitle')}</p>
      </header>

      <div className="flex-1 space-y-5 overflow-y-auto px-6 py-5 text-sm">
        <FormatSection format={format} onChange={setFormat} t={t} />
        {format === 'parquet' && (
          <ParquetSection state={parquet} onChange={setParquet} t={t} />
        )}
        {format === 'csv' && <CsvSection state={csv} onChange={setCsv} t={t} />}
        {format === 'arrow' && <ArrowSection t={t} />}
        {format === 'json' && <JsonSection state={json} onChange={setJson} t={t} />}
        {validationError && (
          <p className="text-destructive text-xs" role="alert">
            {validationError}
          </p>
        )}
      </div>

      <footer className="flex items-center justify-end gap-2 border-t px-6 py-3">
        <Button variant="outline" size="sm" onClick={() => resolve('cancel')}>
          {t('export.cancel')}
        </Button>
        <Button variant="default" size="sm" onClick={() => resolve('continue')}>
          {t('export.continue')}
        </Button>
      </footer>
    </div>
  );
}

// Format picker — radio surface so the choice stays one keystroke away
// (Tab to focus, ←/→ to change) and the per-format option panel below
// updates immediately.
function FormatSection({
  format,
  onChange,
  t,
}: {
  format: FormatId;
  onChange: (next: FormatId) => void;
  t: TFunction<'dialogs'>;
}) {
  return (
    <fieldset>
      <legend className="text-foreground mb-2 font-medium">
        {t('export.format.legend')}
      </legend>
      <div className="grid grid-cols-2 gap-2">
        <FormatRadio
          value="parquet"
          current={format}
          onChange={onChange}
          title={t('export.format.parquet.title')}
          subtitle={t('export.format.parquet.subtitle')}
        />
        <FormatRadio
          value="csv"
          current={format}
          onChange={onChange}
          title={t('export.format.csv.title')}
          subtitle={t('export.format.csv.subtitle')}
        />
        <FormatRadio
          value="json"
          current={format}
          onChange={onChange}
          title={t('export.format.json.title')}
          subtitle={t('export.format.json.subtitle')}
        />
        <FormatRadio
          value="arrow"
          current={format}
          onChange={onChange}
          title={t('export.format.arrow.title')}
          subtitle={t('export.format.arrow.subtitle')}
        />
      </div>
    </fieldset>
  );
}

function FormatRadio({
  value,
  current,
  onChange,
  title,
  subtitle,
}: {
  value: FormatId;
  current: FormatId;
  onChange: (v: FormatId) => void;
  title: string;
  subtitle: string;
}) {
  const checked = current === value;
  return (
    <label
      className={
        'flex cursor-pointer items-start gap-2 rounded border p-3 ' +
        (checked
          ? 'border-primary bg-primary/5'
          : 'border-border hover:bg-muted/40')
      }
    >
      <input
        type="radio"
        name="export-format"
        value={value}
        checked={checked}
        onChange={() => onChange(value)}
        className="mt-0.5"
      />
      <div className="flex flex-col">
        <span className="text-foreground font-medium">{title}</span>
        <span className="text-muted-foreground text-xs">{subtitle}</span>
      </div>
    </label>
  );
}

function ParquetSection({
  state,
  onChange,
  t,
}: {
  state: ParquetState;
  onChange: (next: ParquetState) => void;
  t: TFunction<'dialogs'>;
}) {
  // Compression level is only honoured by gzip / zstd / brotli on the
  // backend — disabling the field for snappy / lz4 / none keeps the UI
  // honest. The user can still pick a level then switch the codec; in
  // that case the level gets dropped at submit time (see buildOptions).
  const levelApplies =
    state.compression === 'gzip'
    || state.compression === 'zstd'
    || state.compression === 'brotli';

  return (
    <fieldset className="space-y-3">
      <legend className="text-foreground font-medium">{t('export.parquet.legend')}</legend>

      <Field label={t('export.parquet.compression')} hint={t('export.parquet.compressionHint')}>
        <select
          className="border-border bg-background w-full rounded border px-2 py-1 text-sm"
          value={state.compression}
          onChange={(e) => onChange({ ...state, compression: e.target.value })}
        >
          {PARQUET_COMPRESSIONS.map((c) => (
            <option key={c} value={c}>
              {c}
              {c === 'snappy' ? ` ${t('export.defaultSuffix')}` : ''}
            </option>
          ))}
        </select>
      </Field>

      <Field
        label={t('export.parquet.compressionLevel')}
        hint={t('export.parquet.compressionLevelHint')}
      >
        <select
          className="border-border bg-background w-full rounded border px-2 py-1 text-sm disabled:opacity-50"
          value={state.compressionLevel}
          disabled={!levelApplies}
          onChange={(e) => onChange({ ...state, compressionLevel: e.target.value })}
        >
          <option value="">{t('export.parquet.compressionLevelDefault')}</option>
          <option value="0">0 — {t('export.parquet.levelNone')}</option>
          <option value="1">1 — {t('export.parquet.levelFastest')}</option>
          <option value="2">2 — {t('export.parquet.levelOptimal')}</option>
          <option value="3">3 — {t('export.parquet.levelSmallest')}</option>
        </select>
      </Field>

      <Field label={t('export.parquet.rowGroupSize')} hint={t('export.parquet.rowGroupSizeHint')}>
        <input
          type="number"
          min={1}
          inputMode="numeric"
          placeholder="50000"
          className="border-border bg-background w-full rounded border px-2 py-1 text-sm"
          value={state.rowGroupSize}
          onChange={(e) => onChange({ ...state, rowGroupSize: e.target.value })}
        />
      </Field>
    </fieldset>
  );
}

function CsvSection({
  state,
  onChange,
  t,
}: {
  state: CsvState;
  onChange: (next: CsvState) => void;
  t: TFunction<'dialogs'>;
}) {
  return (
    <fieldset className="space-y-3">
      <legend className="text-foreground font-medium">{t('export.csv.legend')}</legend>

      <label className="flex items-center gap-2">
        <input
          type="checkbox"
          checked={state.header}
          onChange={(e) => onChange({ ...state, header: e.target.checked })}
        />
        <span>{t('export.csv.header')}</span>
        <span className="text-muted-foreground text-xs">{t('export.csv.headerHint')}</span>
      </label>

      <Field label={t('export.csv.delimiter')} hint={t('export.csv.delimiterHint')}>
        <div className="flex items-center gap-2">
          <select
            className="border-border bg-background w-32 rounded border px-2 py-1 text-sm"
            value={state.delimiter}
            onChange={(e) =>
              onChange({ ...state, delimiter: e.target.value as CsvDelimiter })
            }
          >
            <option value=",">{t('export.csv.delim.comma')}</option>
            <option value=";">{t('export.csv.delim.semicolon')}</option>
            <option value="tab">{t('export.csv.delim.tab')}</option>
            <option value="|">{t('export.csv.delim.pipe')}</option>
            <option value="custom">{t('export.csv.delim.custom')}</option>
          </select>
          {state.delimiter === 'custom' && (
            <input
              type="text"
              maxLength={1}
              placeholder=""
              className="border-border bg-background w-12 rounded border px-2 py-1 text-center text-sm"
              value={state.customDelimiter}
              onChange={(e) =>
                onChange({ ...state, customDelimiter: e.target.value })
              }
            />
          )}
        </div>
      </Field>

      <Field label={t('export.csv.lineEnding')} hint={t('export.csv.lineEndingHint')}>
        <select
          className="border-border bg-background w-32 rounded border px-2 py-1 text-sm"
          value={state.lineEnding}
          onChange={(e) =>
            onChange({ ...state, lineEnding: e.target.value as 'lf' | 'crlf' })
          }
        >
          <option value="lf">LF {t('export.defaultSuffix')}</option>
          <option value="crlf">CRLF</option>
        </select>
      </Field>

      <Field label={t('export.csv.nullString')} hint={t('export.csv.nullStringHint')}>
        <input
          type="text"
          placeholder={t('export.csv.nullStringPlaceholder')}
          className="border-border bg-background w-full rounded border px-2 py-1 text-sm"
          value={state.nullString}
          onChange={(e) => onChange({ ...state, nullString: e.target.value })}
        />
      </Field>
    </fieldset>
  );
}

function ArrowSection({
  t,
}: {
  t: TFunction<'dialogs'>;
}) {
  // No options in v1. The format is the on-disk IPC file format,
  // typed-media metadata is fixed, and we don't expose body-compression
  // to keep the round-trip story bounded. Surface the rationale so users
  // don't wonder where the options panel went.
  return (
    <fieldset className="space-y-3">
      <legend className="text-foreground font-medium">{t('export.arrow.legend')}</legend>
      <p className="text-muted-foreground text-xs">{t('export.arrow.noOptions')}</p>
    </fieldset>
  );
}

function JsonSection({
  state,
  onChange,
  t,
}: {
  state: JsonState;
  onChange: (next: JsonState) => void;
  t: TFunction<'dialogs'>;
}) {
  // Indent is incompatible with JSONL on the backend (one-object-per-line
  // is the definition; indentation splits each object across multiple
  // lines). Mirror the constraint here: disable + force-off when the user
  // picks lines mode so the dialog never produces a SQL the planner will
  // reject.
  const linesMode = state.shape === 'lines';
  return (
    <fieldset className="space-y-3">
      <legend className="text-foreground font-medium">{t('export.json.legend')}</legend>

      <Field label={t('export.json.shape')} hint={t('export.json.shapeHint')}>
        <select
          className="border-border bg-background w-48 rounded border px-2 py-1 text-sm"
          value={state.shape}
          onChange={(e) =>
            onChange({
              ...state,
              shape: e.target.value as JsonState['shape'],
              // Force-off indent when switching to lines so the next
              // Continue click doesn't trip the backend validator.
              indent: e.target.value === 'lines' ? false : state.indent,
            })
          }
        >
          <option value="array">{t('export.json.shape.array')}</option>
          <option value="lines">{t('export.json.shape.lines')}</option>
        </select>
      </Field>

      <label className="flex items-center gap-2">
        <input
          type="checkbox"
          checked={state.indent}
          disabled={linesMode}
          onChange={(e) => onChange({ ...state, indent: e.target.checked })}
        />
        <span className={linesMode ? 'text-muted-foreground' : ''}>
          {t('export.json.indent')}
        </span>
        <span className="text-muted-foreground text-xs">
          {linesMode ? t('export.json.indentDisabled') : t('export.json.indentHint')}
        </span>
      </label>
    </fieldset>
  );
}

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-foreground">{label}</span>
      {children}
      {hint && <span className="text-muted-foreground text-xs">{hint}</span>}
    </label>
  );
}

interface BuildResult {
  options: ExportOptionValues;
  error: string | null;
}

// Translates the UI state into the option bag the COPY SQL builder
// consumes. Empty / default values are omitted so the generated SQL
// stays minimal — the planner already knows the per-format defaults.
function buildOptions(
  format: FormatId,
  parquet: ParquetState,
  csv: CsvState,
  json: JsonState,
): BuildResult {
  const opts: ExportOptionValues = {};
  if (format === 'parquet') {
    if (parquet.compression && parquet.compression !== 'snappy') {
      opts['COMPRESSION'] = parquet.compression;
    }
    const levelApplies =
      parquet.compression === 'gzip'
      || parquet.compression === 'zstd'
      || parquet.compression === 'brotli';
    if (levelApplies && parquet.compressionLevel !== '') {
      const n = Number.parseInt(parquet.compressionLevel, 10);
      if (Number.isInteger(n)) opts['COMPRESSION_LEVEL'] = n;
    }
    if (parquet.rowGroupSize.trim() !== '') {
      const n = Number.parseInt(parquet.rowGroupSize, 10);
      if (!Number.isInteger(n) || n <= 0) {
        return {
          options: {},
          error: 'ROW_GROUP_SIZE must be a positive integer.',
        };
      }
      opts['ROW_GROUP_SIZE'] = n;
    }
    return { options: opts, error: null };
  }

  if (format === 'arrow') {
    // Arrow has no v1 options. The empty option block is fine; the COPY
    // builder still emits `FORMAT 'arrow'` so the planner picks the right
    // sink regardless of file extension.
    return { options: opts, error: null };
  }

  if (format === 'json') {
    // The backend infers LINES from the path extension (.jsonl /
    // .ndjson) when not explicit; we still emit it whenever the user
    // picked it, so the FORMAT-clause-driven SQL is unambiguous
    // regardless of what file extension the save dialog ends up with.
    if (json.shape === 'lines') opts['LINES'] = 'true';
    if (json.indent && json.shape === 'array') opts['INDENT'] = 'true';
    return { options: opts, error: null };
  }

  // CSV
  // HEADER defaults to true on the backend; only emit when the user opts
  // out, so common-case SQL stays terse.
  if (!csv.header) opts['HEADER'] = 'false';

  if (csv.delimiter === 'custom') {
    if (csv.customDelimiter.length !== 1) {
      return {
        options: {},
        error: 'Custom delimiter must be a single character.',
      };
    }
    // Comma is the default — skip emitting it even if the user typed it
    // explicitly into the custom field. The backend re-validates that
    // the character isn't `"`, `\r`, or `\n`.
    if (csv.customDelimiter !== ',') opts['DELIMITER'] = csv.customDelimiter;
  } else if (csv.delimiter !== ',') {
    opts['DELIMITER'] = csv.delimiter;
  }

  if (csv.lineEnding !== 'lf') opts['LINE_ENDING'] = csv.lineEnding;
  if (csv.nullString !== '') opts['NULL_STRING'] = csv.nullString;

  return { options: opts, error: null };
}
