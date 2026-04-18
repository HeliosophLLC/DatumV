using DatumIngest.Execution.Operators;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Applies the trailing clauses (ORDER BY, LIMIT, OFFSET) of a compound query
/// expression (<c>UNION</c> / <c>INTERSECT</c> / <c>EXCEPT</c>) to the operator
/// emitted by the compound's set-operation pipeline. Mirrors the trailing-clause
/// logic inside <see cref="QueryPlanner"/>'s single-SELECT path so a compound
/// query produces the same output shape as an equivalent SELECT wrapped in a
/// derived table.
/// </summary>
internal static class CompoundQueryClauses
{
    /// <summary>
    /// Wraps <paramref name="source"/> with an <see cref="OrderByOperator"/>
    /// (when <paramref name="compound"/> has an ORDER BY) and a
    /// <see cref="LimitOperator"/> (when it has a LIMIT), folding LIMIT + OFFSET
    /// into a plan-time top-N row count when both are literal so the OrderBy
    /// operator can use its bounded-heap path.
    /// </summary>
    public static QueryOperator ApplyCompoundTrailingClauses(
        QueryOperator source, CompoundQueryExpression compound)
    {
        if (compound.OrderBy is not null)
        {
            // topN-pushdown only fires when LIMIT (and any OFFSET) fold to a plan-time
            // integer. Variable / function-call / random-driven limits force a full
            // sort followed by LIMIT — correct, just not push-down-optimised.
            int? topNRows = LiteralFolding.TryComputeTopN(compound.Limit, compound.Offset);
            source = new OrderByOperator(source, compound.OrderBy.Items, topNRows);
        }

        if (compound.Limit is not null)
        {
            source = new LimitOperator(source, compound.Limit, compound.Offset);
        }

        return source;
    }
}
