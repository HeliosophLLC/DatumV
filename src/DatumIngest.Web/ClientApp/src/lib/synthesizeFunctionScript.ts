import type {
  ScalarFunctionDto,
  ScalarFunctionParameterDto,
  ScalarFunctionSignatureDto,
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
 * True when every parameter of `variant` can be rendered in the v1
 * function form — i.e. is binary (file upload) OR matches at least one
 * formable inline kind. Variants with Struct / Array<Struct> / other
 * un-form-able kinds get greyed out in the overload picker.
 */
export function isFormableVariant(variant: ScalarFunctionSignatureDto): boolean {
  // Variadics aren't form-able in v1 — the variable-arity input control
  // is its own design problem. Variant is rejected if it has one.
  if (variant.variadic) return false;
  const params = variant.parameters ?? [];
  for (const p of params) {
    if (!isFormableParameter(p)) return false;
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

// Preference order for inline kinds when a slot accepts more than one.
// Float64 first so a user can type negative or fractional values into a
// slot that nominally accepts {UInt8, Int8, …, Float32, Float64} without
// the form rejecting them at coercion time. Signed widths come before
// unsigned so negative input doesn't fall through to a UInt slot. Wider
// types come before narrower so the user can type a value outside the
// narrow type's range and still have it land somewhere valid.
//
// `abs(x)` is the motivating case: the function accepts every numeric
// kind, AcceptedKinds is in DataKind enum order (UInt8 first), and
// without a preference list the form would declare a UInt8 slot and
// reject `-23243434` with "UInt8 must be a non-negative integer."
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
 * Picks the kind to declare a parameter as. For a slot that accepts a
 * single inline kind, that kind is returned unchanged; for multi-kind
 * slots, the most permissive entry from <see cref="INLINE_KIND_PREFERENCE"/>
 * that the slot actually accepts wins. Binary slots return the first
 * accepted binary kind (most accept exactly one).
 */
export function declaredKindFor(p: ScalarFunctionParameterDto): string {
  const kinds = p.acceptedKinds ?? [];
  if (isBinaryParameter(p)) {
    // Binary slots: declare as the first accepted binary kind. Most
    // binary slots accept exactly one; the rare ones that accept e.g.
    // "Image or Video" fall back to the first.
    return kinds[0] ?? 'Image';
  }
  const accepted = new Set(kinds);
  for (const k of INLINE_KIND_PREFERENCE) {
    if (accepted.has(k)) return k;
  }
  // Defensive fallback: the slot accepts a formable kind that's not in
  // INLINE_KIND_PREFERENCE. `isFormableVariant` already filters out
  // anything outside FORMABLE_INLINE_KINDS, so this is unreachable —
  // but if a new kind lands on the server before this list is updated,
  // returning the first accepted entry keeps us deterministic.
  return kinds[0] ?? 'String';
}

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
    const kind = declaredKindFor(p);
    lines.push(`DECLARE ${name} ${kind} = $${name};`);

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

  const callArgs = params
    .map((p) => p.name ?? '')
    .filter((n) => n.length > 0)
    .join(', ');
  const fnSchema = fn.schema ?? 'system';
  const fnName = fn.name ?? '';
  lines.push(`SELECT ${fnSchema}.${fnName}(${callArgs});`);

  return {
    script: lines.join('\n'),
    fields,
    ready: fields.every((f) => !f.missing),
  };
}
