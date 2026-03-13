import type {
  ScalarFunctionDto,
  ScalarFunctionParameterDto,
  ScalarFunctionSignatureDto,
  ScalarFunctionVariadicDto,
} from '@/api/generated/openapi-client';
import type { FunctionFormState } from '@/state/functionForm';

// Synthesizes the DECLARE+SELECT script a function tab will run. The
// server's `/api/query/stream` endpoint already accepts the resulting
// shape — see ParameterBinder + the multipart envelope on the Web side —
// so the same string is the user-visible preview AND the source the
// runner sends in PR 5.
//
// Shape:
//   DECLARE <param> <Kind> = $<param>;
//   ...
//   SELECT schema.fn(param1, param2, ...);
//
// DECLARE-for-everything (not inline literals) is the convention agreed
// in design: predictable readout, stable $name ↔ form-field mapping,
// and no need to escape inline values the user typed.

export interface FormFieldStatus {
  paramName: string;
  /** True when the parameter still needs a value to make the script runnable. */
  missing: boolean;
  /** True when this is a binary kind (file parameter). */
  isBinary: boolean;
}

export interface ScriptSynthesisResult {
  /** The synthesized SQL text, always emitted even when fields are missing. */
  script: string;
  /** Per-parameter completion state — drives the Run button's enabled flag in PR 5. */
  fields: FormFieldStatus[];
  /** True when every required parameter has a value. */
  ready: boolean;
}

/** Kinds that ride as multipart binary parts in the parameter envelope. */
const BINARY_KINDS = new Set(['Image', 'Audio', 'Video']);

/**
 * True when the parameter binds via a multipart `ref` (file upload)
 * rather than an inline JSON `value`. Mirrors the server-side
 * `IsBinaryKind` check in QueryRequestBinding.
 */
export function isBinaryParameter(p: ScalarFunctionParameterDto): boolean {
  const kinds = p.acceptedKinds ?? [];
  // A parameter with no enumerated kinds either matches any kind (the
  // `acceptsAnyKind` sentinel) or matches nothing — neither maps cleanly
  // to a single binary-vs-inline input control. v1 treats both as
  // non-binary (text input) and leaves the actual upload story to
  // explicitly typed slots.
  if (kinds.length === 0) return false;
  // A parameter is "binary" when EVERY accepted kind is binary —
  // otherwise the user might want to pass an inline value (e.g. a
  // String). Pure multi-kind slots that mix binary + inline live in
  // the "not formable in v1" set and are filtered out upstream
  // (`isFormableVariant`), so they shouldn't reach this check.
  return kinds.every((k) => BINARY_KINDS.has(k));
}

/**
 * Kinds with a meaningful inline input control in v1. Anything outside
 * this set forces the overload into the "use a SQL tab" bucket in the
 * picker.
 */
const FORMABLE_INLINE_KINDS = new Set([
  'Boolean',
  'Int8',
  'Int16',
  'Int32',
  'Int64',
  'UInt8',
  'UInt16',
  'UInt32',
  'UInt64',
  'Float32',
  'Float64',
  'String',
]);

/**
 * True when every parameter of `variant` (fixed + trailing variadic)
 * can be rendered in the v2 function form — i.e. is binary (file
 * upload) OR matches at least one formable inline kind, AND any
 * trailing variadic is itself formable. Variants with Struct /
 * Array&lt;Struct&gt; / other un-formable kinds get greyed out in the
 * overload picker.
 */
export function isFormableVariant(variant: ScalarFunctionSignatureDto): boolean {
  const params = variant.parameters ?? [];
  for (const p of params) {
    if (!isFormableParameter(p)) return false;
  }
  if (variant.variadic && !isFormableVariadic(variant.variadic)) {
    return false;
  }
  return true;
}

function isFormableParameter(p: ScalarFunctionParameterDto): boolean {
  // Array-of-X slots are deferred to v2 along with struct slots.
  if (p.arrayMatch === 'Array') return false;
  if (isBinaryParameter(p)) return true;
  const kinds = p.acceptedKinds ?? [];
  if (kinds.length === 0) return false; // Any-kind or empty matcher — see isBinaryParameter.
  return kinds.some((k) => FORMABLE_INLINE_KINDS.has(k));
}

/**
 * Mirror of `isFormableParameter` for the trailing variadic slot:
 * binary kinds always pass; inline kinds must include at least one
 * formable kind; `Array`-only variadics are out.
 */
function isFormableVariadic(v: ScalarFunctionVariadicDto): boolean {
  if (v.arrayMatch === 'Array') return false;
  const kinds = v.acceptedKinds ?? [];
  if (kinds.length === 0) {
    // Any-kind variadic — defer in v2 along with polymorphic fixed
    // slots. (`coalesce` lands here; we'd need a kind picker that
    // applies across all occurrences.)
    return false;
  }
  if (kinds.every((k) => BINARY_KINDS.has(k))) return true;
  return kinds.some((k) => FORMABLE_INLINE_KINDS.has(k));
}

// ───────────────────────── Kind inference ─────────────────────────
//
// Two ordered tables walked narrowest-first to pick the smallest kind
// that (a) the slot accepts and (b) the typed value fits in. Integer
// rows are paired signed-before-unsigned at each width so the common
// case (`100` → Int8) lands on the signed type; the unsigned variant
// only wins when the value exceeds the signed half-range (`200` →
// UInt8 because Int8 maxes at 127).
//
// Float32 carries a precision check, not just a range check: a slot
// that accepts both Float32 and Float64 picks Float32 only when the
// value round-trips exactly through `Math.fround` — otherwise typing
// `0.1` into a polymorphic slot would silently lose precision.

interface KindRange {
  kind: string;
  min: number;
  max: number;
  /** Extra precision check beyond the range. Float32 uses this. */
  fits?: (n: number) => boolean;
}

const INTEGER_TABLE: readonly KindRange[] = [
  { kind: 'Int8',   min: -128,                    max: 127 },
  { kind: 'UInt8',  min: 0,                       max: 255 },
  { kind: 'Int16',  min: -32768,                  max: 32767 },
  { kind: 'UInt16', min: 0,                       max: 65535 },
  { kind: 'Int32',  min: -2147483648,             max: 2147483647 },
  { kind: 'UInt32', min: 0,                       max: 4294967295 },
  { kind: 'Int64',  min: Number.MIN_SAFE_INTEGER, max: Number.MAX_SAFE_INTEGER },
  { kind: 'UInt64', min: 0,                       max: Number.MAX_SAFE_INTEGER },
];

const FLOAT_TABLE: readonly KindRange[] = [
  {
    kind: 'Float32',
    min: -3.4028235e38,
    max: 3.4028235e38,
    // `Math.fround` rounds to the nearest Float32; comparing equal back
    // to the source means no precision was lost.
    fits: (n) => Math.fround(n) === n,
  },
  {
    kind: 'Float64',
    min: -Number.MAX_VALUE,
    max: Number.MAX_VALUE,
  },
];

/**
 * Picks the narrowest kind in <paramref name="accepted"/> that fits the
 * typed value. Returns null when the text isn't a finite number, or when
 * no row in the relevant table is acceptable + fitting. The caller falls
 * back to the static preference list in that case.
 *
 * The integer-vs-float branch is driven by the TEXT, not the parsed JS
 * Number — `1e3` and `100.` go to the float table even though their
 * parsed values are integers, because the user's input shape signals
 * "I want a float."
 */
function inferKindFromText(
  text: string,
  accepted: ReadonlySet<string>,
): string | null {
  const trimmed = text.trim();
  if (!trimmed) return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n)) return null;
  const looksFloat = /[.eE]/.test(trimmed);
  const table = looksFloat ? FLOAT_TABLE : INTEGER_TABLE;
  for (const row of table) {
    if (!accepted.has(row.kind)) continue;
    if (n < row.min || n > row.max) continue;
    if (row.fits && !row.fits(n)) continue;
    return row.kind;
  }
  return null;
}

// Fallback preference for empty fields (no user input yet) or values that
// don't fit any row in the inference tables. Float64 first so a slot that
// accepts the full numeric family lands on the most permissive type when
// the user hasn't yet typed anything. The previous static-only behaviour
// stays as the safety net.
const INLINE_KIND_PREFERENCE = [
  'Float64',
  'Float32',
  'Int64',
  'Int32',
  'Int16',
  'Int8',
  'UInt64',
  'UInt32',
  'UInt16',
  'UInt8',
  'Boolean',
  'String',
];

/**
 * Picks the kind to declare a parameter as. Resolution order:
 *
 *  1. <paramref name="override"/> — when the user clicked a specific
 *     kind pill, that choice wins as long as the slot still accepts it.
 *  2. Value-driven inference from <paramref name="currentText"/> — walk
 *     the INTEGER/FLOAT table narrowest-first (signed before unsigned
 *     at each width) and pick the first row the slot accepts and the
 *     value fits.
 *  3. Static preference fallback (INLINE_KIND_PREFERENCE) — used when
 *     the field is empty or the text isn't a finite number; lands on
 *     the most permissive kind the slot accepts.
 *
 * Binary slots return the first accepted binary kind (most accept exactly
 * one).
 */
export function declaredKindFor(
  p: ScalarFunctionParameterDto,
  currentText?: string,
  override?: string,
): string {
  const kinds = p.acceptedKinds ?? [];
  if (isBinaryParameter(p)) {
    return kinds[0] ?? 'Image';
  }
  const accepted = new Set(kinds);

  // Manual override — only honoured when the slot still accepts the
  // pinned kind. A pin can become stale if the user changes the variant
  // to one whose accepted set no longer includes the previous choice;
  // we fall through to inference rather than serve an invalid kind.
  if (override !== undefined && accepted.has(override)) {
    return override;
  }

  // Value-driven inference: a typed number narrows to the smallest
  // fitting kind so `abs(100)` declares Int8, not Float64. Falls through
  // when there's no usable text (empty / non-numeric) — the static
  // preference still picks a permissive default.
  if (currentText !== undefined) {
    const inferred = inferKindFromText(currentText, accepted);
    if (inferred !== null) return inferred;
  }

  for (const k of INLINE_KIND_PREFERENCE) {
    if (accepted.has(k)) return k;
  }
  // Defensive fallback: the slot accepts a formable kind not in
  // INLINE_KIND_PREFERENCE. `isFormableVariant` already filters out
  // anything outside FORMABLE_INLINE_KINDS, so this is unreachable —
  // but if a new kind lands on the server before this list is updated,
  // returning the first accepted entry keeps us deterministic.
  return kinds[0] ?? 'String';
}

/**
 * Prefix applied to the DECLARE variable name so it can't collide with a
 * built-in type name in expression position. SQL identifiers are
 * case-insensitive, so a parameter named `image` would shadow / be
 * shadowed by the `Image` type when later referenced in a SELECT,
 * producing argument-kind errors like
 * <c>"no matching signature for argument kinds [Type, Float64, …]"</c>.
 * Prefixing dodges every type-name collision without restricting which
 * parameter names the form accepts.
 */
const VARIABLE_PREFIX = 'arg_';

export function synthesizeFunctionScript(
  fn: ScalarFunctionDto,
  variant: ScalarFunctionSignatureDto,
  form: FunctionFormState,
): ScriptSynthesisResult {
  const params = variant.parameters ?? [];
  const lines: string[] = [];
  const fields: FormFieldStatus[] = [];

  for (const p of params) {
    const name = p.name ?? '';
    if (!name) continue;
    // Pass the typed text + any manual override so the DECLARE matches
    // whatever the user has pinned or, failing that, narrows to the
    // smallest fitting kind as they type. Binary params don't have a
    // text value or override; declaredKindFor short-circuits on the
    // binary branch before reading either.
    const text = form.textValues[name];
    const override = form.kindOverrides[name];
    const kind = declaredKindFor(p, text, override);
    lines.push(`DECLARE ${VARIABLE_PREFIX}${name} ${kind} = $${name};`);

    if (isBinaryParameter(p)) {
      fields.push({
        paramName: name,
        missing: !form.fileNames[name],
        isBinary: true,
      });
    } else {
      const v = form.textValues[name] ?? '';
      const isOptional = p.isOptional === true;
      // Optional parameters with no value are still emitted as a DECLARE
      // — DECLARE @x KIND = NULL is the safer default than dropping the
      // parameter from the call (which would shift positional args).
      fields.push({
        paramName: name,
        missing: !isOptional && v.trim().length === 0,
        isBinary: false,
      });
    }
  }

  // Variadic expansion. Each occurrence becomes its own DECLARE with a
  // synthetic `${variadicName}_${i}` $-binding name; the same key is
  // used as the field id throughout the form so text/file/override
  // lookups stay direct. Slot count defaults to `max(1, minOccurrences)`
  // — there's always at least one row to type into.
  const variadicCallArgs: string[] = [];
  const variadicSpec = variant.variadic ?? null;
  if (variadicSpec) {
    const variadicName = variadicSpec.name ?? '';
    const minOccurrences = variadicSpec.minOccurrences ?? 0;
    const count = explicitVariadicCount(form, variadicName, minOccurrences);
    const isBinary = isBinaryVariadic(variadicSpec);
    for (let i = 0; i < count; i++) {
      const slotKey = `${variadicName}_${i}`;
      const text = form.textValues[slotKey];
      const override = form.kindOverrides[slotKey];
      const slotKind = declaredKindForVariadicSlot(variadicSpec, text, override);
      lines.push(`DECLARE ${VARIABLE_PREFIX}${slotKey} ${slotKind} = $${slotKey};`);
      variadicCallArgs.push(`${VARIABLE_PREFIX}${slotKey}`);
      if (isBinary) {
        fields.push({
          paramName: slotKey,
          missing: !form.fileNames[slotKey],
          isBinary: true,
        });
      } else {
        const v = text ?? '';
        fields.push({
          paramName: slotKey,
          // Variadic slots are required up to minOccurrences; trailing
          // user-added slots can be left blank without blocking Run,
          // but the user-visible expectation is "if you added a row,
          // fill it." Mark each row required so an unfilled add gets
          // surfaced rather than silently dropped at the call site.
          missing: v.trim().length === 0,
          isBinary: false,
        });
      }
    }
  }

  const fixedCallArgs = params
    .map((p) => p.name ?? '')
    .filter((n) => n.length > 0)
    .map((n) => `${VARIABLE_PREFIX}${n}`);
  const callArgs = [...fixedCallArgs, ...variadicCallArgs].join(', ');
  const fnSchema = fn.schema ?? 'system';
  const fnName = fn.name ?? '';
  lines.push(`SELECT ${fnSchema}.${fnName}(${callArgs});`);

  return {
    script: lines.join('\n'),
    fields,
    ready: fields.every((f) => !f.missing),
  };
}

/**
 * Internal mirror of `variadicSlotCount` in state/functionForm; reading
 * from the proxy directly would create a state ↔ lib circular import,
 * so the synth helper inlines the same default rule.
 */
function explicitVariadicCount(
  form: FunctionFormState,
  variadicName: string,
  minOccurrences: number,
): number {
  const explicit = form.variadicCounts[variadicName];
  if (typeof explicit === 'number') return explicit;
  return Math.max(1, minOccurrences);
}

/**
 * Picks the declared kind for a single variadic occurrence — same shape
 * as `declaredKindFor` for a fixed parameter, but reads the matcher off
 * a <see cref="ScalarFunctionVariadicDto"/> instead of a
 * <see cref="ScalarFunctionParameterDto"/>. The override and value-driven
 * inference paths reuse the same helpers.
 */
export function declaredKindForVariadicSlot(
  v: ScalarFunctionVariadicDto,
  currentText?: string,
  override?: string,
): string {
  const kinds = v.acceptedKinds ?? [];
  if (isBinaryVariadic(v)) {
    return kinds[0] ?? 'Image';
  }
  const accepted = new Set(kinds);
  if (override !== undefined && accepted.has(override)) {
    return override;
  }
  if (currentText !== undefined) {
    const inferred = inferKindFromText(currentText, accepted);
    if (inferred !== null) return inferred;
  }
  for (const k of INLINE_KIND_PREFERENCE) {
    if (accepted.has(k)) return k;
  }
  return kinds[0] ?? 'String';
}

/** True when every kind a variadic accepts is a multipart-binary kind. */
export function isBinaryVariadic(v: ScalarFunctionVariadicDto): boolean {
  const kinds = v.acceptedKinds ?? [];
  if (kinds.length === 0) return false;
  return kinds.every((k) => BINARY_KINDS.has(k));
}
