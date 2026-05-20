using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Uuid;

/// <summary>
/// Returns the canonical <c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c> string
/// representation of a UUID. A null input yields a null result.
/// </summary>
public sealed class UuidStrFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "uuid_str";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Uuid;

    /// <inheritdoc />
    public static string Description =>
        "Returns the canonical xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx string representation of a UUID.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("value", DataKindMatcher.Exact(DataKind.Uuid))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<UuidStrFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef input = args[0];
        if (input.IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        return new ValueTask<ValueRef>(ValueRef.FromString(input.AsUuid().ToString("D")));
    }
}
