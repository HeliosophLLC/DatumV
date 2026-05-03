using System.Diagnostics.CodeAnalysis;
using DatumIngest.Diagnostics;
using DatumIngest.Execution.Operators;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Rewrites <c>Limit(Project(x))</c> → <c>Project(Limit(x))</c> at the root
/// of a planned operator tree so expensive per-row work in the projection
/// only evaluates for the rows that survive LIMIT/OFFSET. Walks through any
/// adjacent chain of row-preserving wrappers (<see cref="ProjectOperator"/>
/// without <c>ASSERT</c>, <see cref="RowEnricherOperator"/>) and slots the
/// <see cref="LimitOperator"/> in just below the deepest one.
/// </summary>
/// <remarks>
/// <para>
/// Applied as part of <see cref="QueryPlanner"/>'s finalize step. Subqueries
/// and CTEs are planned via separate <c>Plan()</c> calls that each finalize
/// independently, so their own LIMITs get pushed before they're embedded in
/// the surrounding plan — no recursive tree walk is needed here.
/// </para>
/// <para>
/// The walk stops at non-row-preserving operators (Filter, Distinct, OrderBy,
/// GroupBy, Join, Limit, ModelInvocation, etc.) so cardinality semantics are
/// preserved.
/// </para>
/// </remarks>
internal static class LimitPushdown
{
    /// <summary>
    /// Returns <paramref name="root"/> with the top <see cref="LimitOperator"/>
    /// (if any) pushed below its chain of row-preserving wrappers. Leaves the
    /// tree unchanged when the root is not a LIMIT or when LIMIT's immediate
    /// source cannot be pushed through.
    /// </summary>
    public static QueryOperator Push(QueryOperator root)
    {
        if (root is not LimitOperator limit)
        {
            return root;
        }

        List<QueryOperator> wrappers = [];
        QueryOperator current = limit.Source;

        while (TryUnwrap(current, out QueryOperator? inner))
        {
            wrappers.Add(current);
            current = inner;
        }

        if (wrappers.Count == 0)
        {
            return root;
        }

        QueryOperator rebuilt = new LimitOperator(current, limit.LimitExpression, limit.OffsetExpression);
        for (int i = wrappers.Count - 1; i >= 0; i--)
        {
            rebuilt = Rewrap(wrappers[i], rebuilt);
        }

        DatumActivity.Operators.Trace("LIMIT pushed below row-preserving wrappers");
        return rebuilt;
    }

    private static bool TryUnwrap(QueryOperator op, [NotNullWhen(true)] out QueryOperator? inner)
    {
        switch (op)
        {
            case ProjectOperator project when CanPushThroughProject(project):
                inner = project.Source;
                return true;
            case RowEnricherOperator enricher:
                inner = enricher.Source;
                return true;
            default:
                inner = null;
                return false;
        }
    }

    private static QueryOperator Rewrap(QueryOperator original, QueryOperator newSource) => original switch
    {
        ProjectOperator p => new ProjectOperator(newSource, p.Columns, p.LetBindings, p.Assertions),
        RowEnricherOperator e => new RowEnricherOperator(newSource, e.Enrichments),
        _ => throw new InvalidOperationException($"Unexpected wrapper type: {original.GetType().Name}"),
    };

    // ProjectOperator is row-preserving except when ASSERT … ON FAIL SKIP can
    // drop rows mid-projection. Conservative: bail on any assertions regardless
    // of failure mode.
    private static bool CanPushThroughProject(ProjectOperator project) =>
        project.Assertions is null or { Count: 0 };
}
