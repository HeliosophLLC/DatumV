using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>starts_with(string, prefix) → boolean</c>. Returns true when
/// <c>value</c> begins with <c>prefix</c>. Comparison is ordinal. Null in any
/// argument propagates to null.
/// </summary>
public sealed class StartsWithFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "starts_with";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when value begins with prefix (ordinal comparison).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("prefix", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<StartsWithFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Boolean));
        }
        bool result = args[0].AsString().StartsWith(args[1].AsString(), StringComparison.Ordinal);
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(result));
    }
}
