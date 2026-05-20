import type {
  BetweenCheckDto,
  CustomCheckDto,
  GreaterThanCheckDto,
  InCheckDto,
  LessThanCheckDto,
  ParameterCheckDto,
  RangeCheckDto,
  RegexCheckDto,
} from '@/api/generated/openapi-client';

// Client-side helpers for the typed parameter-constraint discriminator the
// server emits at `parameter.check`. NSwag flattens the polymorphic
// hierarchy into a base interface plus extended subclass interfaces, all
// sharing a `kind: string` discriminator — so we type-narrow with the
// `kind` field rather than using `instanceof`.
//
// Three jobs:
//   1. `describeCheck` — render a one-line constraint label for tooltips
//      / hints next to the input.
//   2. `validateCheck` — run the check against a coerced inline value
//      before the request goes out. Failures surface as per-field errors.
//   3. Type guards for each shape so callers can branch on `kind` with
//      proper TS narrowing.

// ───────────────────────── Type guards ─────────────────────────

export function isBetweenCheck(c: ParameterCheckDto): c is BetweenCheckDto {
  return c.kind === 'between';
}
export function isRangeCheck(c: ParameterCheckDto): c is RangeCheckDto {
  return c.kind === 'range';
}
export function isGreaterThanCheck(c: ParameterCheckDto): c is GreaterThanCheckDto {
  return c.kind === 'greaterThan';
}
export function isLessThanCheck(c: ParameterCheckDto): c is LessThanCheckDto {
  return c.kind === 'lessThan';
}
export function isInCheck(c: ParameterCheckDto): c is InCheckDto {
  return c.kind === 'in';
}
export function isRegexCheck(c: ParameterCheckDto): c is RegexCheckDto {
  return c.kind === 'regex';
}
export function isCustomCheck(c: ParameterCheckDto): c is CustomCheckDto {
  return c.kind === 'custom';
}

// ───────────────────────── Description ─────────────────────────

/**
 * One-line, human-readable summary of the constraint. Used as the hint
 * text under a parameter field and as tooltip content. Compact intentionally
 * so it fits next to a 200 px input.
 */
export function describeCheck(check: ParameterCheckDto): string {
  if (isBetweenCheck(check)) {
    return `${formatNum(check.min)} – ${formatNum(check.max)}`;
  }
  if (isRangeCheck(check)) {
    const lo =
      check.min === undefined || check.min === null
        ? '−∞'
        : `${check.minInclusive ? '[' : '('}${formatNum(check.min)}`;
    const hi =
      check.max === undefined || check.max === null
        ? '∞'
        : `${formatNum(check.max)}${check.maxInclusive ? ']' : ')'}`;
    return `${lo}, ${hi}`;
  }
  if (isGreaterThanCheck(check)) {
    return `${check.inclusive ? '≥' : '>'} ${formatNum(check.min)}`;
  }
  if (isLessThanCheck(check)) {
    return `${check.inclusive ? '≤' : '<'} ${formatNum(check.max)}`;
  }
  if (isInCheck(check)) {
    const values = check.values ?? [];
    if (values.length <= 4) return `one of ${values.join(', ')}`;
    return `one of ${values.slice(0, 3).join(', ')}, … (${values.length})`;
  }
  if (isRegexCheck(check)) {
    return `matches /${check.pattern ?? ''}/`;
  }
  if (isCustomCheck(check)) {
    return check.sourceText ?? 'custom';
  }
  return '';
}

function formatNum(n: number | undefined | null): string {
  if (n === undefined || n === null) return '';
  // Trim trailing zeros in float-ish output but keep integers intact.
  // `Number.toString()` already does the right thing for both cases.
  return n.toString();
}

// ───────────────────────── Validation ─────────────────────────

/**
 * Returns an error message when `value` violates `check`, or null when it
 * passes. `value` is the already-coerced runtime value (number / string /
 * boolean) — coercion is the caller's job; this helper does range / set /
 * pattern checks only.
 */
export function validateCheck(
  check: ParameterCheckDto,
  value: unknown,
): string | null {
  if (isBetweenCheck(check)) {
    if (typeof value !== 'number') return null;
    const min = check.min ?? Number.NEGATIVE_INFINITY;
    const max = check.max ?? Number.POSITIVE_INFINITY;
    if (value < min || value > max) {
      return `Must be between ${formatNum(min)} and ${formatNum(max)}`;
    }
    return null;
  }
  if (isRangeCheck(check)) {
    if (typeof value !== 'number') return null;
    if (check.min !== undefined && check.min !== null) {
      const ok = check.minInclusive ? value >= check.min : value > check.min;
      if (!ok) {
        return `Must be ${check.minInclusive ? '≥' : '>'} ${formatNum(check.min)}`;
      }
    }
    if (check.max !== undefined && check.max !== null) {
      const ok = check.maxInclusive ? value <= check.max : value < check.max;
      if (!ok) {
        return `Must be ${check.maxInclusive ? '≤' : '<'} ${formatNum(check.max)}`;
      }
    }
    return null;
  }
  if (isGreaterThanCheck(check)) {
    if (typeof value !== 'number') return null;
    // `min` is required engine-side but the generated DTO marks it
    // optional; bail rather than throw if it ever arrives undefined.
    if (check.min === undefined) return null;
    const ok = check.inclusive ? value >= check.min : value > check.min;
    return ok ? null : `Must be ${check.inclusive ? '≥' : '>'} ${formatNum(check.min)}`;
  }
  if (isLessThanCheck(check)) {
    if (typeof value !== 'number') return null;
    if (check.max === undefined) return null;
    const ok = check.inclusive ? value <= check.max : value < check.max;
    return ok ? null : `Must be ${check.inclusive ? '≤' : '<'} ${formatNum(check.max)}`;
  }
  if (isInCheck(check)) {
    // InCheck.values are stringified canonical forms. Compare by the
    // user's raw text-form value AND by the coerced number/boolean
    // toString, so both `"640"` from a dropdown and `640` from a numeric
    // coercion pass. Server validates definitively at execution time.
    const values = check.values ?? [];
    const asString = String(value);
    if (!values.includes(asString)) {
      return `Must be one of ${values.join(', ')}`;
    }
    return null;
  }
  if (isRegexCheck(check)) {
    if (typeof value !== 'string') return null;
    const pattern = check.pattern ?? '';
    try {
      const re = new RegExp(pattern);
      return re.test(value) ? null : `Must match /${pattern}/`;
    } catch {
      // Server-only regex syntax (e.g. .NET-specific groups) — skip the
      // client-side check and let the server validate.
      return null;
    }
  }
  // CustomCheck: defer to the server. Returning null means "looks fine
  // from the client's vantage point" — the server-side evaluator runs
  // the SQL expression definitively at planning time.
  return null;
}

// ───────────────────────── Numeric attr extraction ─────────────────────────

/** Bounds extracted from a check for use as HTML `min`/`max` attributes on a numeric input. */
export interface CheckNumericBounds {
  min?: number;
  max?: number;
}

/**
 * Pulls min/max bounds out of a check when they apply to a numeric input.
 * Lets the browser surface basic out-of-range UI before the user even
 * hits Run.
 */
export function numericBoundsFor(check: ParameterCheckDto): CheckNumericBounds {
  if (isBetweenCheck(check)) {
    return { min: check.min, max: check.max };
  }
  if (isRangeCheck(check)) {
    // Browser min/max are inclusive — for strict-inequality bounds we drop
    // them rather than nudge by epsilon, since the runtime validator does
    // the right thing definitively.
    return {
      min: check.minInclusive ? check.min ?? undefined : undefined,
      max: check.maxInclusive ? check.max ?? undefined : undefined,
    };
  }
  if (isGreaterThanCheck(check)) {
    return check.inclusive ? { min: check.min } : {};
  }
  if (isLessThanCheck(check)) {
    return check.inclusive ? { max: check.max } : {};
  }
  return {};
}
