using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>to_hex(int) → text</c>. Returns the lowercase hexadecimal
/// representation of an integer. Negative values use two's-complement
/// (matching PG behaviour: <c>to_hex(-1)</c> on a 64-bit input is
/// <c>'ffffffffffffffff'</c>). Null input propagates to null.
/// </summary>
public sealed class ToHexFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "to_hex";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the hexadecimal representation of an integer.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.IntegerFamily))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ToHexFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(BaseConversion.Run(arguments.Span[0], 16, Name));
}
