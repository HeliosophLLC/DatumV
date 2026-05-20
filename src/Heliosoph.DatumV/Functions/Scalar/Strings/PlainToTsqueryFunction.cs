using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Fulltext;

/// <summary>
/// Constructs an AND-of-terms query from plain user text. In v1 the engine
/// has no <c>tsquery</c> type — the query is just the raw string, and
/// tokenization happens inside <see cref="TsqueryMatchFunction"/> (the
/// <c>@@</c> operator's implementation) using the FTS column's analyzer.
/// PR-FTS-B replaces this no-op with parser work (websearch syntax,
/// AND/OR/NOT) and lifts <c>tsquery</c> closer to a real type.
/// </summary>
/// <remarks>
/// <para>v1 behaviour: returns the input string unchanged (null in →
/// null out). The function exists so user-written SQL matches the
/// PostgreSQL surface — <c>body @@ plainto_tsquery('foo bar')</c> works
/// today, and the same statement will keep working when PR-FTS-B lights
/// up real query parsing.</para>
/// </remarks>
public sealed class PlainToTsqueryFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "plainto_tsquery";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.FullText;

    /// <inheritdoc />
    public static string Description =>
        "Builds a full-text query from plain text. v1 treats the input as " +
        "an implicit AND of every surviving term after tokenization — done " +
        "at @@ evaluation time using the FTS index's analyzer.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PlainToTsqueryFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        // v1: passthrough. The tokenization + AND-fold lives in
        // TsqueryMatchFunction so the analyzer choice can vary per index.
        return new ValueTask<ValueRef>(ValueRef.FromString(input.AsString()));
    }
}
