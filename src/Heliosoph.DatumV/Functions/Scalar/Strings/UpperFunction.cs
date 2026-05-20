using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// Returns the input string with all characters converted to upper case
/// using the invariant culture. Null input propagates to null output.
/// </summary>
public sealed class UpperFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "upper";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the input string with all characters converted to upper case (invariant culture).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<UpperFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef input = args[0];
        if (input.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(input.AsString().ToUpperInvariant()));
    }
}
