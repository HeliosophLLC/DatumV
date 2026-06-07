using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// <c>contains(string, substring) → boolean</c>. Returns true when
/// <c>value</c> contains <c>substring</c>. Comparison is ordinal. Null in any
/// argument propagates to null.
/// </summary>
/// <remarks>
/// Not a PostgreSQL built-in scalar (PG reserves <c>@&gt;</c>/<c>&lt;@</c> for
/// array and range containment); kept here as a convenience over
/// <c>position(value, substring) &gt; 0</c>.
/// </remarks>
public sealed class ContainsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "contains";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when value contains substring (ordinal comparison).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("substring", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ContainsFunction>(argumentKinds);

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
        bool result = args[0].AsString().Contains(args[1].AsString(), StringComparison.Ordinal);
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(result));
    }
}
