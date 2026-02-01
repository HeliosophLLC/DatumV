using DatumIngest.Execution;
using DatumIngest.Indexing.Fts;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Backs the <c>@@</c> operator: <c>haystack @@ needle</c> returns
/// <see langword="true"/> when every surviving term from the needle
/// (after tokenization) is also present in the haystack's tokens.
/// Implements the AND-of-terms semantics of v1's <c>plainto_tsquery</c>.
/// </summary>
/// <remarks>
/// <para>Both arguments are tokenized with the v1 default analyzer
/// (<c>simple_en</c>). When the planner rewrites a <c>col @@ q</c>
/// predicate to a <c>FullTextSearchOperator</c> (PR-FTS-A4), this
/// function is bypassed for indexed columns — the operator uses the
/// actual index's analyzer. The function path is the fallback for
/// non-indexed columns and for ad-hoc evaluation outside a query plan.</para>
///
/// <para>The function is null-propagating: a null haystack or needle
/// yields a null result, matching PG semantics for <c>NULL @@ NULL</c>.</para>
/// </remarks>
public sealed class TsqueryMatchFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "tsquery_match";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.FullText;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when every tokenized term of the right argument is " +
        "present among the tokens of the left argument. Backs the @@ operator.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("haystack", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("needle", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TsqueryMatchFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef haystack = args[0];
        ValueRef needle = args[1];

        if (haystack.IsNull || needle.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Boolean));
        }

        IFullTextAnalyzer analyzer = FtsAnalyzerRegistry.Default.Get(DefaultAnalyzerName);

        HashSet<string> haystackTokens = new(StringComparer.Ordinal);
        foreach (Token t in analyzer.Tokenize(haystack.AsString()))
        {
            haystackTokens.Add(t.Term);
        }

        foreach (Token t in analyzer.Tokenize(needle.AsString()))
        {
            if (!haystackTokens.Contains(t.Term))
            {
                return new ValueTask<ValueRef>(ValueRef.FromBoolean(false));
            }
        }

        // Empty query (no surviving terms after tokenization, e.g. all stop
        // words) matches everything — same as `plainto_tsquery('')` in PG.
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(true));
    }

    /// <summary>
    /// The single analyzer used by the function path. Lifting this to be
    /// per-call analyzer-aware (e.g. via a third argument or context lookup)
    /// is deferred until PR-FTS-A4 wires the operator path.
    /// </summary>
    internal const string DefaultAnalyzerName = "simple_en";
}
