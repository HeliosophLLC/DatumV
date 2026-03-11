import type {
  ScalarFunctionDto,
  ScalarFunctionParameterDto,
  ScalarFunctionSignatureDto,
} from '@/api/generated/openapi-client';
import { getFunctionFormFile } from '@/state/functionForm';
import type { FunctionFormState } from '@/state/functionForm';
import type {
  ParameterBinding,
  RunMultipartOpts,
} from '@/state/execution';
import {
  declaredKindFor,
  isBinaryParameter,
  synthesizeFunctionScript,
} from './synthesizeFunctionScript';
import { validateCheck } from './parameterCheck';

// Assembles the wire payload a function tab will send to /api/query/stream.
// Splits the form into:
//   - sql: the synthesized DECLARE+SELECT script (re-used from
//     synthesizeFunctionScript, so the user's preview pane and the
//     runner see the exact same string).
//   - parameters + files: the multipart envelope. Inline scalars are
//     coerced from the form's raw text to JSON values matching the
//     server's expected types (numbers for numeric kinds, booleans for
//     Boolean, strings for String). Binary parameters resolve to a
//     `ref` pointing at a sibling multipart part of the same name.
//
// Errors here surface in the UI as a per-field validation message
// before any request goes out; the server still re-validates and
// rejects malformed values, but a client-side gate avoids round-trips
// for obvious mistakes.

export interface BuildFunctionRequestSuccess {
  ok: true;
  sql: string;
  opts: RunMultipartOpts;
}

export interface BuildFunctionRequestError {
  ok: false;
  /** Per-field validation messages, keyed by parameter name. */
  fieldErrors: Record<string, string>;
}

export type BuildFunctionRequestResult =
  | BuildFunctionRequestSuccess
  | BuildFunctionRequestError;

export function buildFunctionRequest(
  tabId: string,
  fn: ScalarFunctionDto,
  variant: ScalarFunctionSignatureDto,
  form: FunctionFormState,
): BuildFunctionRequestResult {
  const synth = synthesizeFunctionScript(fn, variant, form);
  const parameters: Record<string, ParameterBinding> = {};
  const files: Record<string, File> = {};
  const fieldErrors: Record<string, string> = {};

  for (const param of variant.parameters ?? []) {
    const name = param.name ?? '';
    if (!name) continue;
    // Pull the typed text + override into kind selection so the wire
    // payload's kind matches whatever the synthesized DECLARE shows in
    // the preview. Binary params don't have text or overrides;
    // declaredKindFor short-circuits before reading either.
    const text = form.textValues[name];
    const override = form.kindOverrides[name];
    const kind = declaredKindFor(param, text, override);

    if (isBinaryParameter(param)) {
      const file = getFunctionFormFile(tabId, name);
      if (!file) {
        if (param.isOptional !== true) {
          fieldErrors[name] = 'Required';
        }
        continue;
      }
      parameters[name] = { kind, ref: name };
      files[name] = file;
      continue;
    }

    const raw = (form.textValues[name] ?? '').trim();
    if (raw.length === 0) {
      if (param.isOptional !== true) {
        fieldErrors[name] = 'Required';
        continue;
      }
      // Optional + empty: bind a typed null inline. Server's
      // JsonScalarToDataValue treats a null `value` as UnknownNull.
      parameters[name] = { kind, value: null };
      continue;
    }

    const coerced = coerceInlineValue(param, kind, raw);
    if ('error' in coerced) {
      fieldErrors[name] = coerced.error;
      continue;
    }
    // Structured-constraint check. The server re-validates definitively
    // at execution time; this is a UX shortcut so the user gets a
    // per-field message before the request goes out.
    if (param.check) {
      const checkError = validateCheck(param.check, coerced.value);
      if (checkError !== null) {
        fieldErrors[name] = checkError;
        continue;
      }
    }
    parameters[name] = { kind, value: coerced.value };
  }

  if (Object.keys(fieldErrors).length > 0) {
    return { ok: false, fieldErrors };
  }
  return {
    ok: true,
    sql: synth.script,
    opts: { parameters, files },
  };
}

interface Coerced {
  value: unknown;
}
interface CoerceError {
  error: string;
}

function coerceInlineValue(
  _param: ScalarFunctionParameterDto,
  kind: string,
  raw: string,
): Coerced | CoerceError {
  if (kind === 'Boolean') {
    if (raw === 'true') return { value: true };
    if (raw === 'false') return { value: false };
    return { error: 'Must be true or false' };
  }
  if (kind === 'String') {
    return { value: raw };
  }
  if (kind.startsWith('Int')) {
    const n = Number(raw);
    if (!Number.isFinite(n) || !Number.isInteger(n)) {
      return { error: `${kind} must be an integer` };
    }
    return { value: n };
  }
  if (kind.startsWith('UInt')) {
    const n = Number(raw);
    if (!Number.isFinite(n) || !Number.isInteger(n) || n < 0) {
      return { error: `${kind} must be a non-negative integer` };
    }
    return { value: n };
  }
  if (kind.startsWith('Float')) {
    const n = Number(raw);
    if (!Number.isFinite(n)) {
      return { error: `${kind} must be a number` };
    }
    return { value: n };
  }
  // Unknown kind — pass through as a string and let the server reject if it
  // doesn't fit. Reachable only if a new kind is added on the server without
  // updating this client; the form's `isFormableVariant` gate already
  // filters out everything else.
  return { value: raw };
}
