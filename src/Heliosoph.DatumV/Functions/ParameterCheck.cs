using System.Globalization;
using System.Text.RegularExpressions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Functions;

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
/// <para>
/// <strong>Runtime validation.</strong> Each subclass overrides
/// <see cref="Validate(ValueRef)"/> to return either <see langword="null"/>
/// (the value satisfies the constraint) or a user-facing message describing
/// the violation. The scalar-dispatch path and SQL-model parameter-binding
/// path invoke this against each bound argument; NULL values short-circuit
/// to <see langword="null"/> (matches SQL <c>CHECK</c>-constraint semantics —
/// NULL passes any check, <c>IS NOT NULL</c> is a separate enforcement).
/// </para>
/// </remarks>
public abstract record ParameterCheck
{
    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="value"/> satisfies the
    /// constraint, otherwise a user-facing message describing the violation
    /// (without the parameter name — callers prefix it).
    /// </summary>
    /// <remarks>
    /// NULL values pass any check (mirroring SQL semantics); the per-subclass
    /// override may assume <c>value.IsNull == false</c>.
    /// </remarks>
    public abstract string? Validate(ValueRef value);

    /// <summary>
    /// Best-effort numeric coercion from a <see cref="ValueRef"/> to
    /// <see cref="decimal"/>. Returns <see langword="false"/> for non-numeric
    /// kinds (string, blob, etc.) — the caller treats this as "constraint
    /// doesn't apply to this kind" and passes the value through. Centralised
    /// here so every numeric subclass coerces consistently.
    /// </summary>
    protected static bool TryGetDecimal(ValueRef value, out decimal result)
    {
        switch (value.Kind)
        {
            case DataKind.Int8: result = value.AsInt8(); return true;
            case DataKind.Int16: result = value.AsInt16(); return true;
            case DataKind.Int32: result = value.AsInt32(); return true;
            case DataKind.Int64: result = value.AsInt64(); return true;
            case DataKind.UInt8: result = value.AsUInt8(); return true;
            case DataKind.UInt16: result = value.AsUInt16(); return true;
            case DataKind.UInt32: result = value.AsUInt32(); return true;
            case DataKind.UInt64: result = value.AsUInt64(); return true;
            case DataKind.Float16: result = (decimal)(float)value.AsFloat16(); return true;
            case DataKind.Float32: result = (decimal)value.AsFloat32(); return true;
            case DataKind.Float64: result = (decimal)value.AsFloat64(); return true;
            case DataKind.Decimal: result = value.AsDecimal(); return true;
            default: result = 0m; return false;
        }
    }

    private protected static string FormatDecimal(decimal d) =>
        d.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Inclusive numeric range: <c>x BETWEEN Min AND Max</c>. The most common
/// constraint shape — UI renders as a range slider with both bounds.
/// </summary>
/// <param name="Min">Lower bound (inclusive).</param>
/// <param name="Max">Upper bound (inclusive).</param>
public sealed record BetweenCheck(decimal Min, decimal Max) : ParameterCheck
{
    /// <inheritdoc/>
    public override string? Validate(ValueRef value)
    {
        if (value.IsNull) return null;
        if (!TryGetDecimal(value, out decimal d)) return null;
        if (d < Min || d > Max)
        {
            return $"value {FormatDecimal(d)} is outside [{FormatDecimal(Min)}, {FormatDecimal(Max)}].";
        }
        return null;
    }
}

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
    bool MaxInclusive = true) : ParameterCheck
{
    /// <inheritdoc/>
    public override string? Validate(ValueRef value)
    {
        if (value.IsNull) return null;
        if (!TryGetDecimal(value, out decimal d)) return null;
        if (Min is decimal lo)
        {
            bool ok = MinInclusive ? d >= lo : d > lo;
            if (!ok)
            {
                string op = MinInclusive ? ">=" : ">";
                return $"value {FormatDecimal(d)} must be {op} {FormatDecimal(lo)}.";
            }
        }
        if (Max is decimal hi)
        {
            bool ok = MaxInclusive ? d <= hi : d < hi;
            if (!ok)
            {
                string op = MaxInclusive ? "<=" : "<";
                return $"value {FormatDecimal(d)} must be {op} {FormatDecimal(hi)}.";
            }
        }
        return null;
    }
}

/// <summary>
/// One-sided lower bound: <c>x &gt; Min</c> or <c>x &gt;= Min</c>.
/// UI renders as a spinbox with a minimum, unbounded above.
/// </summary>
/// <param name="Min">Lower bound.</param>
/// <param name="Inclusive">
/// When <see langword="true"/>, the bound is <c>&gt;=</c>; otherwise <c>&gt;</c>.
/// Defaults to <see langword="false"/> (strict <c>&gt;</c>).
/// </param>
public sealed record GreaterThanCheck(decimal Min, bool Inclusive = false) : ParameterCheck
{
    /// <inheritdoc/>
    public override string? Validate(ValueRef value)
    {
        if (value.IsNull) return null;
        if (!TryGetDecimal(value, out decimal d)) return null;
        bool ok = Inclusive ? d >= Min : d > Min;
        if (!ok)
        {
            string op = Inclusive ? ">=" : ">";
            return $"value {FormatDecimal(d)} must be {op} {FormatDecimal(Min)}.";
        }
        return null;
    }
}

/// <summary>
/// One-sided upper bound: <c>x &lt; Max</c> or <c>x &lt;= Max</c>.
/// UI renders as a spinbox with a maximum, unbounded below.
/// </summary>
/// <param name="Max">Upper bound.</param>
/// <param name="Inclusive">
/// When <see langword="true"/>, the bound is <c>&lt;=</c>; otherwise <c>&lt;</c>.
/// </param>
public sealed record LessThanCheck(decimal Max, bool Inclusive = false) : ParameterCheck
{
    /// <inheritdoc/>
    public override string? Validate(ValueRef value)
    {
        if (value.IsNull) return null;
        if (!TryGetDecimal(value, out decimal d)) return null;
        bool ok = Inclusive ? d <= Max : d < Max;
        if (!ok)
        {
            string op = Inclusive ? "<=" : "<";
            return $"value {FormatDecimal(d)} must be {op} {FormatDecimal(Max)}.";
        }
        return null;
    }
}

/// <summary>
/// Enumerated discrete-value set: <c>x IN ('a', 'b', 'c')</c>. UI renders
/// as a dropdown or radio group. Values are strings; integer enums can
/// be expressed by stringifying the values, or use a separate
/// <c>IntEnumCheck</c> if integer-specific behaviour becomes important.
/// </summary>
/// <param name="Values">Allowed values; ordering is preserved for UI presentation.</param>
public sealed record InCheck(IReadOnlyList<string> Values) : ParameterCheck
{
    /// <inheritdoc/>
    public override string? Validate(ValueRef value)
    {
        if (value.IsNull) return null;
        // Stringify the value through the same path as the wire-format
        // accepted-values list so comparison is symmetric. String columns
        // pass through their value; numeric columns stringify via invariant
        // culture so "416" matches both Int32 416 and String "416".
        string actual;
        switch (value.Kind)
        {
            case DataKind.String:
                actual = value.AsString();
                break;
            default:
                if (TryGetDecimal(value, out decimal d))
                {
                    actual = FormatDecimal(d);
                }
                else
                {
                    // Unsupported kind for IN — pass through; this would
                    // surface earlier as a signature-validation error if
                    // genuinely ill-typed.
                    return null;
                }
                break;
        }
        for (int i = 0; i < Values.Count; i++)
        {
            if (string.Equals(Values[i], actual, StringComparison.Ordinal))
            {
                return null;
            }
        }
        string allowed = string.Join(", ", Values);
        return $"value '{actual}' is not one of ({allowed}).";
    }
}

/// <summary>
/// String must match a regular expression (.NET regex syntax). UI
/// renders as a text input with client-side regex validation; server
/// validates definitively.
/// </summary>
/// <param name="Pattern">Regular expression pattern (.NET syntax).</param>
public sealed record RegexCheck(string Pattern) : ParameterCheck
{
    /// <inheritdoc/>
    public override string? Validate(ValueRef value)
    {
        if (value.IsNull) return null;
        if (value.Kind != DataKind.String) return null;
        string s = value.AsString();
        // Compile-and-discard per call is acceptable for metadata-scale use;
        // the catalog has dozens of regex checks at most, each invoked once
        // per row. Cache only when a real hotspot emerges.
        if (!Regex.IsMatch(s, Pattern))
        {
            return $"value '{s}' does not match pattern /{Pattern}/.";
        }
        return null;
    }
}

/// <summary>
/// Escape hatch for constraints that don't fit a canonical shape — carries
/// the original SQL <see cref="Expression"/> AST. The UI falls back to a
/// generic text input plus server-side validation; the server evaluates
/// the expression directly via the SQL evaluator at validation time.
/// </summary>
/// <param name="Expr">Parsed SQL boolean expression. Free variable is the parameter name.</param>
public sealed record CustomCheck(Expression Expr) : ParameterCheck
{
    /// <inheritdoc/>
    /// <remarks>
    /// Custom checks need the SQL expression evaluator with the parameter
    /// bound as a variable, which lives outside the <see cref="ParameterCheck"/>
    /// type. The dispatch sites that have an evaluator + frame in hand
    /// (scalar dispatch, model parameter binding) detect <see cref="CustomCheck"/>
    /// specifically and evaluate the boolean expression there. This method
    /// returns <see langword="null"/> so callers that don't carry an evaluator
    /// (unit tests, the wire-DTO projection) treat <see cref="CustomCheck"/>
    /// as "validated elsewhere" rather than failing closed.
    /// </remarks>
    public override string? Validate(ValueRef value) => null;
}
