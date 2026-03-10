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
/// Three-state filter on a parameter's array-ness, used by array-aware
/// signature matching. <see cref="Either"/> means the slot accepts both
/// scalar and array values (kind-only matching, matching legacy behaviour
/// before per-signature array discrimination); <see cref="Scalar"/> rejects
/// arrays; <see cref="Array"/> requires arrays.
/// </summary>
public enum ArrayMatch
{
    /// <summary>Accepts both scalar and array. Default — preserves the
    /// kind-only matching behaviour for signatures that don't care.</summary>
    Either = 0,
    /// <summary>The argument must NOT be a typed array.</summary>
    Scalar = 1,
    /// <summary>The argument must be a typed array.</summary>
    Array = 2,
}

/// <summary>
/// One fixed positional parameter slot.
/// </summary>
/// <param name="Name">Parameter name (used in error messages and docs).</param>
/// <param name="Kind">Matcher describing the accepted kinds.</param>
/// <param name="IsOptional">
/// When <c>true</c>, the call is valid with this parameter omitted. Optional
/// parameters must come after all required parameters.
/// </param>
/// <param name="IsArray">
/// Discriminates scalar vs array inputs. <see cref="ArrayMatch.Either"/>
/// (default) preserves legacy kind-only matching. Set to
/// <see cref="ArrayMatch.Array"/> on a signature that requires a typed-array
/// input, or <see cref="ArrayMatch.Scalar"/> on a sibling signature that
/// must reject arrays — used together to dispatch between scalar/array
/// shapes (see <c>image_crop</c>'s rect/rect-array variants).
/// </param>
/// <param name="Metadata">
/// Optional UI-facing metadata (range constraint, step, unit, description).
/// Defaults to <c>null</c> for parameters with no per-parameter hints. See
/// <see cref="ParameterMetadata"/> for the field semantics.
/// </param>
public sealed record ParameterSpec(
    string Name,
    DataKindMatcher Kind,
    bool IsOptional = false,
    ArrayMatch IsArray = ArrayMatch.Either,
    ParameterMetadata? Metadata = null);

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
/// <param name="IsArray">
/// Per-argument array-ness filter, mirroring <see cref="ParameterSpec.IsArray"/>.
/// </param>
public sealed record VariadicSpec(
    string Name,
    DataKindMatcher Kind,
    int MinOccurrences = 0,
    bool RequireSameKindAcrossArgs = false,
    ArrayMatch IsArray = ArrayMatch.Either);
