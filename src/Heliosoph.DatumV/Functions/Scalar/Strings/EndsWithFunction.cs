using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// <c>ends_with(string, suffix) → boolean</c>. Returns true when <c>value</c>
/// ends with <c>suffix</c>. Comparison is ordinal. Null in any argument
/// propagates to null.
/// </summary>
/// <remarks>
/// Companion to <see cref="StartsWithFunction"/>; not a PostgreSQL built-in
/// (PG users typically use <c>LIKE</c> or <c>right(value, length(suffix))</c>).
/// </remarks>
public sealed class EndsWithFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "ends_with";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when value ends with suffix (ordinal comparison).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("suffix", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<EndsWithFunction>(argumentKinds);

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
        bool result = args[0].AsString().EndsWith(args[1].AsString(), StringComparison.Ordinal);
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(result));
    }
}
