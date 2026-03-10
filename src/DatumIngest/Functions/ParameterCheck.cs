using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions;

/// <summary>
/// Discriminated union of well-known parameter constraint shapes. Used as
/// the structured payload of <see cref="ParameterMetadata.Check"/>; one
/// canonical shape per UI rendering pattern (slider, dropdown, etc.).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Closed taxonomy with an open escape hatch.</strong> The set of
/// concrete subclasses is intentionally small — every realistic UI
/// constraint maps to one of them. Anything that doesn't fit a canonical
/// shape lands in <see cref="CustomCheck"/>, which carries the original
/// SQL <see cref="Expression"/> for server-side validation but signals
/// to the front-end that a generic text-input + server-validate fallback
/// is required.
/// </para>
/// <para>
/// <strong>Why decimal for numeric bounds.</strong> Bounds are
/// human-authored (e.g. <c>0.5</c>, <c>1.0</c>) and round-trip through
/// catalog persistence + SQL pretty-printing. Float / double would
/// introduce silent precision drift (<c>0.1f</c> renders as
/// <c>0.10000000149011612</c>). Decimal is exact for the values
/// constraint authors actually write. The metadata footprint is dozens
/// of records, not row-scale, so the 16-byte cost is irrelevant.
/// </para>
/// <para>
/// <strong>Authoring.</strong> Built-in function authors construct these
/// directly in C# (<c>new BetweenCheck(0.0m, 1.0m)</c>). SQL-defined
/// model authors write <c>CHECK (expr)</c> after their parameter
/// declaration; a parser-side walker canonicalizes recognized shapes
/// into the same hierarchy and falls back to <see cref="CustomCheck"/>
/// for unrecognized expressions.
/// </para>
/// </remarks>
public abstract record ParameterCheck;

/// <summary>
/// Inclusive numeric range: <c>x BETWEEN Min AND Max</c>. The most common
/// constraint shape — UI renders as a range slider with both bounds.
/// </summary>
/// <param name="Min">Lower bound (inclusive).</param>
/// <param name="Max">Upper bound (inclusive).</param>
public sealed record BetweenCheck(decimal Min, decimal Max) : ParameterCheck;

/// <summary>
/// Generalised numeric range with explicit inclusivity on each side and
/// optional bounds (null = unbounded). Use this when you need an
/// open-interval bound, asymmetric inclusivity, or one-sided bounds.
/// For the common closed-interval case, prefer <see cref="BetweenCheck"/>.
/// </summary>
/// <param name="Min">Lower bound. <c>null</c> means unbounded below.</param>
/// <param name="Max">Upper bound. <c>null</c> means unbounded above.</param>
/// <param name="MinInclusive">Whether <see cref="Min"/> is inclusive.</param>
/// <param name="MaxInclusive">Whether <see cref="Max"/> is inclusive.</param>
public sealed record RangeCheck(
    decimal? Min,
    decimal? Max,
    bool MinInclusive = true,
    bool MaxInclusive = true) : ParameterCheck;

/// <summary>
/// One-sided lower bound: <c>x &gt; Min</c> or <c>x &gt;= Min</c>.
/// UI renders as a spinbox with a minimum, unbounded above.
/// </summary>
/// <param name="Min">Lower bound.</param>
/// <param name="Inclusive">
/// When <see langword="true"/>, the bound is <c>&gt;=</c>; otherwise <c>&gt;</c>.
/// Defaults to <see langword="false"/> (strict <c>&gt;</c>).
/// </param>
public sealed record GreaterThanCheck(decimal Min, bool Inclusive = false) : ParameterCheck;

/// <summary>
/// One-sided upper bound: <c>x &lt; Max</c> or <c>x &lt;= Max</c>.
/// UI renders as a spinbox with a maximum, unbounded below.
/// </summary>
/// <param name="Max">Upper bound.</param>
/// <param name="Inclusive">
/// When <see langword="true"/>, the bound is <c>&lt;=</c>; otherwise <c>&lt;</c>.
/// </param>
public sealed record LessThanCheck(decimal Max, bool Inclusive = false) : ParameterCheck;

/// <summary>
/// Enumerated discrete-value set: <c>x IN ('a', 'b', 'c')</c>. UI renders
/// as a dropdown or radio group. Values are strings; integer enums can
/// be expressed by stringifying the values, or use a separate
/// <c>IntEnumCheck</c> if integer-specific behaviour becomes important.
/// </summary>
/// <param name="Values">Allowed values; ordering is preserved for UI presentation.</param>
public sealed record InCheck(IReadOnlyList<string> Values) : ParameterCheck;

/// <summary>
/// String must match a regular expression (.NET regex syntax). UI
/// renders as a text input with client-side regex validation; server
/// validates definitively.
/// </summary>
/// <param name="Pattern">Regular expression pattern (.NET syntax).</param>
public sealed record RegexCheck(string Pattern) : ParameterCheck;

/// <summary>
/// Escape hatch for constraints that don't fit a canonical shape — carries
/// the original SQL <see cref="Expression"/> AST. The UI falls back to a
/// generic text input plus server-side validation; the server evaluates
/// the expression directly via the SQL evaluator at validation time.
/// </summary>
/// <param name="Expr">Parsed SQL boolean expression. Free variable is the parameter name.</param>
public sealed record CustomCheck(Expression Expr) : ParameterCheck;
