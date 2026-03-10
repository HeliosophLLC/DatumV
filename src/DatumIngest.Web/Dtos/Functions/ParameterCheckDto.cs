using System.Text.Json.Serialization;

namespace DatumIngest.Web.Dtos.Functions;

/// <summary>
/// Wire-format constraint shape for a function/model parameter. One concrete
/// subclass per canonical UI rendering pattern; the JSON discriminator
/// (<c>"kind"</c>) tells clients which subclass they're handling.
/// </summary>
/// <remarks>
/// <para>
/// Parallel hierarchy to the engine-side <c>ParameterCheck</c> tagged union.
/// The controller projects engine records to DTOs at the boundary; this
/// separation keeps engine AST types (<c>Expression</c>) off the wire.
/// </para>
/// <para>
/// <strong>Tagged union via System.Text.Json polymorphism.</strong> Each
/// subclass declares its discriminator value through
/// <see cref="JsonDerivedTypeAttribute"/>; ASP.NET emits the <c>kind</c>
/// property first in the JSON output and reads it first on deserialization.
/// </para>
/// <para>
/// <strong>Decimal numeric bounds.</strong> Serialize as JSON numbers via
/// the default <c>System.Text.Json</c> handling. JavaScript clients
/// lose the decimal precision on deserialization (JS <c>Number</c> is
/// always double), but the server-side source of truth — catalog file,
/// SQL pretty-printing, validation — keeps the exact authored value.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BetweenCheckDto), "between")]
[JsonDerivedType(typeof(RangeCheckDto), "range")]
[JsonDerivedType(typeof(GreaterThanCheckDto), "greaterThan")]
[JsonDerivedType(typeof(LessThanCheckDto), "lessThan")]
[JsonDerivedType(typeof(InCheckDto), "in")]
[JsonDerivedType(typeof(RegexCheckDto), "regex")]
[JsonDerivedType(typeof(CustomCheckDto), "custom")]
public abstract record ParameterCheckDto;

/// <summary>Inclusive numeric range: <c>x BETWEEN Min AND Max</c>. UI renders as a closed-interval slider.</summary>
public sealed record BetweenCheckDto(decimal Min, decimal Max) : ParameterCheckDto;

/// <summary>
/// Generalised numeric range with explicit inclusivity. Bounds may be
/// <c>null</c> to indicate unbounded sides.
/// </summary>
public sealed record RangeCheckDto(
    decimal? Min,
    decimal? Max,
    bool MinInclusive = true,
    bool MaxInclusive = true) : ParameterCheckDto;

/// <summary>One-sided lower bound: <c>x &gt; Min</c> (or <c>&gt;=</c>).</summary>
public sealed record GreaterThanCheckDto(decimal Min, bool Inclusive = false) : ParameterCheckDto;

/// <summary>One-sided upper bound: <c>x &lt; Max</c> (or <c>&lt;=</c>).</summary>
public sealed record LessThanCheckDto(decimal Max, bool Inclusive = false) : ParameterCheckDto;

/// <summary>Discrete-value set: <c>x IN (...)</c>. UI renders as a dropdown / radio group.</summary>
public sealed record InCheckDto(IReadOnlyList<string> Values) : ParameterCheckDto;

/// <summary>String must match a regular expression. UI renders as a text input with client-side regex validation.</summary>
public sealed record RegexCheckDto(string Pattern) : ParameterCheckDto;

/// <summary>
/// Escape hatch for arbitrary SQL boolean constraints — carries the
/// verbatim SQL source text (pretty-printed from the engine's AST). UI
/// falls back to a generic text input + server-side validation.
/// </summary>
public sealed record CustomCheckDto(string SourceText) : ParameterCheckDto;
