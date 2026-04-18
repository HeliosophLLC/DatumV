using DatumIngest.Catalog;
using DatumIngest.Execution.Operators;
using DatumIngest.Indexing.Fts;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Plan-time rewrite that replaces a <see cref="ScanOperator"/> + filter pair with
/// a <see cref="FullTextSearchOperator"/> when the predicate is a constant-query
/// <c>tsquery_match</c> call against a column that exposes a
/// <see cref="ITextSearchIndex"/>. Lets the planner skip a full table scan in
/// favour of an index-driven posting-list intersection.
/// </summary>
/// <remarks>
/// <para>
/// v1 scope: single-table queries (no JOINs), one FTS predicate per query, RHS
/// must be a literal or <c>plainto_tsquery(literal)</c>. Multi-predicate FTS
/// combinations (one column with two queries, or two FTS-indexed columns ANDed)
/// are deferred — they require either an intersection step or planner
/// cost-comparison.
/// </para>
/// <para>
/// Semantic-preservation guard: an empty post-analyzer query means "no rows" via
/// the FTS operator but "match everything" via the runtime <c>tsquery_match</c>
/// function. The rewrite is skipped in that case so the function-path filter
/// still runs and returns all rows.
/// </para>
/// </remarks>
internal static class FullTextSearchRewriter
{
    /// <summary>
    /// If <paramref name="source"/> is a single <see cref="ScanOperator"/>
    /// (optionally wrapped in an <see cref="AliasOperator"/>) and one of
    /// <paramref name="pendingPredicates"/> is a <c>tsquery_match(col, &lt;const&gt;)</c>
    /// against a column with an FTS index, replaces the scan with a
    /// <see cref="FullTextSearchOperator"/> and removes the predicate from the
    /// pending list. Otherwise returns <paramref name="source"/> unchanged.
    /// </summary>
    public static QueryOperator MaybeRewriteForFullTextSearch(
        QueryOperator source,
        List<Expression> pendingPredicates)
    {
        ScanOperator? scan;
        string? wrappingAlias;
        switch (source)
        {
            case AliasOperator alias when alias.Source is ScanOperator innerScan:
                scan = innerScan;
                wrappingAlias = alias.Alias;
                break;
            case ScanOperator topScan:
                scan = topScan;
                wrappingAlias = null;
                break;
            default:
                return source;
        }

        ITableProvider provider = scan.TableProvider;

        for (int i = 0; i < pendingPredicates.Count; i++)
        {
            if (!TryMatchFullTextPredicate(pendingPredicates[i], out string? columnName, out string? queryText))
            {
                continue;
            }

            if (!provider.TryGetTextSearchIndex(columnName!, out ITextSearchIndex? index))
            {
                continue;
            }

            // Empty post-analyzer query would mean "no rows" via the FTS operator but
            // "match everything" via the tsquery_match function. To preserve semantic
            // equivalence, skip the rewrite — the function-path filter still runs and
            // returns all rows.
            if (!HasAnySurvivingToken(index.Analyzer, queryText!))
            {
                continue;
            }

            FullTextSearchOperator ftsOp = new(
                provider,
                columnName!,
                queryText!,
                scan.RequiredColumns);

            pendingPredicates.RemoveAt(i);

            return wrappingAlias is null
                ? ftsOp
                : new AliasOperator(ftsOp, wrappingAlias);
        }

        return source;
    }

    /// <summary>
    /// Matches a predicate of shape <c>tsquery_match(&lt;column-ref&gt;, &lt;const-string&gt;)</c>.
    /// The RHS may be a bare string literal or a call to <c>plainto_tsquery</c>
    /// wrapping one. Other shapes (parameterised queries, expression-derived query
    /// strings) fall through to the scan + filter path for v1.
    /// </summary>
    private static bool TryMatchFullTextPredicate(
        Expression predicate,
        out string? columnName,
        out string? queryText)
    {
        columnName = null;
        queryText = null;

        if (predicate is not FunctionCallExpression call) return false;
        if (!string.Equals(call.FunctionName, "tsquery_match", StringComparison.OrdinalIgnoreCase)) return false;
        if (call.Arguments.Count != 2) return false;

        if (call.Arguments[0] is not ColumnReference colRef) return false;
        if (!LiteralFolding.TryExtractConstantString(call.Arguments[1], out string? text)) return false;

        columnName = colRef.ColumnName;
        queryText = text;
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="analyzer"/>'s tokenizer
    /// yields at least one token from <paramref name="queryText"/>. Tokenisation
    /// can drop every token (all stop words, all too-short, etc.); the rewrite
    /// uses this to detect the "match-everything in v1" semantic edge case
    /// described in the class remarks.
    /// </summary>
    private static bool HasAnySurvivingToken(IFullTextAnalyzer analyzer, string queryText)
    {
        foreach (Token _ in analyzer.Tokenize(queryText))
        {
            return true;
        }
        return false;
    }
}
