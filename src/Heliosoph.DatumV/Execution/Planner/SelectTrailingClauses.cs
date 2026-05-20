using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Applies the trailing clauses of a single SELECT (<c>DISTINCT</c>,
/// <c>ORDER BY</c>, <c>LIMIT</c> / <c>OFFSET</c>) and trims any
/// ORDER BY-only aggregate passthroughs that were appended to the projection.
/// Mirrors <see cref="CompoundQueryClauses.ApplyCompoundTrailingClauses"/>
/// but with the extra concerns specific to a single SELECT: DISTINCT-vs-
/// ORDER BY validation, streaming-sort elision, sorted-index substitution,
/// and the final hidden-column trim.
/// </summary>
internal static class SelectTrailingClauses
{
    /// <summary>
    /// Wraps <paramref name="source"/> with <see cref="DistinctOperator"/>
    /// (when <c>SELECT DISTINCT</c>), <see cref="OrderByOperator"/> (when
    /// ORDER BY is not already satisfied by the upstream ordering),
    /// <see cref="LimitOperator"/> (when LIMIT is present), and a trim
    /// <see cref="ProjectOperator"/> (when ORDER BY pulled hidden aggregate
    /// columns into the projection that the user's SELECT list does not
    /// emit). Returns the final operator.
    /// </summary>
    /// <param name="source">Source after projection / Pivot / FoldScan.</param>
    /// <param name="statement">The SELECT statement being planned.</param>
    /// <param name="rewrittenOrderByClause">
    /// Aggregate-rewritten ORDER BY clause when GROUP BY / aggregates were
    /// present (bare aggregate calls in ORDER BY have been lifted into
    /// GroupBy's aggregate columns and rewritten as column references).
    /// <see langword="null"/> falls back to <c>statement.OrderBy</c>.
    /// </param>
    /// <param name="orderByAggregatePassthroughs">
    /// Names of aggregate columns appended to the projection because
    /// ORDER BY referenced them but SELECT did not. After ORDER BY runs,
    /// a final <c>SELECT * EXCEPT (…)</c> drops them so the user-visible
    /// output matches their SELECT list.
    /// </param>
    public static QueryOperator ApplyTrailingClauses(
        QueryOperator source,
        SelectStatement statement,
        OrderByClause? rewrittenOrderByClause,
        IReadOnlyList<string>? orderByAggregatePassthroughs)
    {
        // 5. Apply DISTINCT — streaming deduplication on projected output.
        if (statement.Distinct)
        {
            // When SELECT DISTINCT is combined with ORDER BY, every ORDER BY expression
            // must appear in the SELECT list, otherwise the result is ambiguous.
            if (statement.OrderBy is not null)
            {
                OrderByAnalyzer.ValidateDistinctOrderBy(statement.Columns, statement.OrderBy);
            }

            source = new DistinctOperator(source);
        }

        // 6. Apply ORDER BY — use index scan when a sorted index covers the sort column,
        //    or elide entirely when a streaming GROUP BY already produces sorted output.
        if (statement.OrderBy is not null)
        {
            // When the query had GROUP BY / aggregates, ORDER BY items have
            // been rewritten so bare aggregate calls reference the GroupBy's
            // output columns. Otherwise fall back to the original parsed clause.
            OrderByClause effectiveOrderBy = rewrittenOrderByClause ?? statement.OrderBy;

            if (SortAggregateAnalyzer.OutputOrderingSatisfiesOrderBy(source, effectiveOrderBy))
            {
                DatumActivity.Operators.Trace("ORDER BY elided — output already sorted by streaming GROUP BY");
            }
            else if (!SortAggregateAnalyzer.TryReplaceWithIndexScan(ref source, effectiveOrderBy))
            {
                int? topNRows = LiteralFolding.TryComputeTopN(statement.Limit, statement.Offset);
                source = new OrderByOperator(source, effectiveOrderBy.Items, topNRows);
            }
        }

        // 7. Apply LIMIT/OFFSET.
        if (statement.Limit is not null)
        {
            source = new LimitOperator(source, statement.Limit, statement.Offset);
        }

        // 8. Trim hidden ORDER BY-aggregate passthroughs that were appended to
        //    the projection so ORDER BY could see them. After the sort has run
        //    they're surplus to the user's SELECT list — drop them with a final
        //    SELECT * EXCEPT (...) projection so the output schema matches what
        //    the user asked for.
        if (orderByAggregatePassthroughs is { Count: > 0 })
        {
            source = new ProjectOperator(
                source,
                [new SelectAllColumns(ExcludedColumns: orderByAggregatePassthroughs)],
                letBindings: null,
                assertions: null);
        }

        return source;
    }
}
