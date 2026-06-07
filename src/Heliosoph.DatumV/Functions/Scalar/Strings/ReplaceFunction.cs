using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>replace(string, from, to) → text</c>. Replaces every
/// occurrence of <c>from</c> in <c>value</c> with <c>to</c>. Comparison is
/// ordinal. Null in any argument propagates to null. An empty <c>from</c>
/// returns <c>value</c> unchanged (PG semantics).
/// </summary>
public sealed class ReplaceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "replace";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Replaces every occurrence of `from` in `value` with `to` (ordinal comparison).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("from",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("to",    DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ReplaceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        string source = args[0].AsString();
        string from = args[1].AsString();
        // string.Replace throws on empty `from`; PG returns the source unchanged.
        if (from.Length == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromString(source));
        }
        string to = args[2].AsString();
        return new ValueTask<ValueRef>(ValueRef.FromString(source.Replace(from, to, StringComparison.Ordinal)));
    }
}
