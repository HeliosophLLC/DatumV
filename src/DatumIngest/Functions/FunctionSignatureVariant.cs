namespace DatumIngest.Functions;

/// <summary>
/// One accepted argument shape for a function. Functions that accept
/// multiple shapes (e.g. unary <c>negate(x)</c> + binary <c>negate(x, y)</c>)
/// list one variant per shape; <see cref="FunctionMetadata.Validate{T}"/>
/// picks the first matching variant.
/// </summary>
/// <param name="Parameters">Fixed positional parameters (may be empty).</param>
/// <param name="VariadicTrailing">
/// Optional trailing variadic — accepts zero or more arguments matching
/// <see cref="VariadicSpec.Kind"/> after the fixed parameters.
/// </param>
/// <param name="ReturnType">Rule that resolves the result kind.</param>
public sealed record FunctionSignatureVariant(
    IReadOnlyList<ParameterSpec> Parameters,
    VariadicSpec? VariadicTrailing,
    ReturnTypeRule ReturnType);

/// <summary>
/// One fixed positional parameter slot.
/// </summary>
/// <param name="Name">Parameter name (used in error messages and docs).</param>
/// <param name="Kind">Matcher describing the accepted kinds.</param>
/// <param name="IsOptional">
/// When <c>true</c>, the call is valid with this parameter omitted. Optional
/// parameters must come after all required parameters.
/// </param>
public sealed record ParameterSpec(
    string Name,
    DataKindMatcher Kind,
    bool IsOptional = false);

/// <summary>
/// Trailing variadic specification.
/// </summary>
/// <param name="Name">Variadic group name (used in error messages and docs).</param>
/// <param name="Kind">Matcher applied to each variadic argument.</param>
/// <param name="MinOccurrences">Minimum count of variadic arguments.</param>
/// <param name="RequireSameKindAcrossArgs">
/// When <c>true</c>, every variadic argument must have the same kind as the
/// first variadic argument. Used for things like <c>concat</c> where mixed
/// kinds wouldn't compose.
/// </param>
public sealed record VariadicSpec(
    string Name,
    DataKindMatcher Kind,
    int MinOccurrences = 0,
    bool RequireSameKindAcrossArgs = false);
