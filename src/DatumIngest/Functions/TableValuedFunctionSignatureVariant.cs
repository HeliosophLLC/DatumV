using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// One accepted argument shape for a table-valued function. Mirrors
/// <see cref="FunctionSignatureVariant"/> for scalars but replaces the
/// scalar <see cref="ReturnTypeRule"/> with a fixed-schema slot — a TVF
/// produces a <see cref="Schema"/> of rows, not a single
/// <see cref="DataKind"/>.
/// </summary>
/// <param name="Parameters">Fixed positional parameters (may be empty).</param>
/// <param name="VariadicTrailing">
/// Optional trailing variadic — accepts zero or more arguments matching
/// <see cref="VariadicSpec.Kind"/> after the fixed parameters.
/// </param>
/// <param name="FixedOutputSchema">
/// The schema of the rows the function produces, when it is the same for
/// every call. <see langword="null"/> when the schema depends on the
/// argument values or kinds (e.g. <c>range(0, 10)</c> vs <c>range(0.0, 1.0)</c>
/// — the column kind follows the widest argument). Surfaced in hover /
/// signature help when present; the runtime check still lives in
/// <see cref="ITableValuedFunction.ValidateArguments"/>.
/// </param>
public sealed record TableValuedFunctionSignatureVariant(
    IReadOnlyList<ParameterSpec> Parameters,
    VariadicSpec? VariadicTrailing = null,
    Schema? FixedOutputSchema = null);
