using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Counts how often each WITH-clause CTE name is referenced from any in-scope
/// position — the outer SELECT's FROM/JOINs, every sibling CTE's body
/// (anchor + recursive parts), and subquery sources nested at arbitrary depth.
/// <see cref="QueryPlanner"/> uses the result to decide the default
/// auto-materialisation policy: a CTE referenced more than once is materialised
/// to avoid redundant computation.
/// </summary>
/// <remarks>
/// Undercounting is a real hazard — heavy CTEs that look single-use because the
/// counter missed a reference will be inlined and re-evaluated. Particularly
/// catastrophic when the missed reference is inside a recursive CTE's iterating
/// member.
/// </remarks>
internal static class CommonTableExpressionReferenceCounter
{
    /// <summary>
    /// Counts how many times each CTE name appears as a table reference anywhere
    /// the CTE name is in scope: the outer SELECT's FROM/JOIN sources, every
    /// sibling CTE's body (anchor + recursive parts), and any subquery sources
    /// nested at arbitrary depth inside those bodies.
    /// </summary>
    public static Dictionary<string, int> CountCommonTableExpressionReferences(
        SelectStatement statement)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

        if (statement.CommonTableExpressions is null)
        {
            return counts;
        }

        // Build the set of known CTE names for fast lookup.
        HashSet<string> cteNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (CommonTableExpression commonTableExpression in statement.CommonTableExpressions)
        {
            cteNames.Add(commonTableExpression.Name);
            counts[commonTableExpression.Name] = 0;
        }

        // 1. Outer SELECT's own FROM + JOINs.
        CountReferencesInSelectStatement(statement, cteNames, counts, includeNestedCommonTableExpressions: false);

        // 2. Every sibling CTE's body — references between CTEs and self-references
        // inside the recursive member are otherwise invisible to the counter.
        foreach (CommonTableExpression commonTableExpression in statement.CommonTableExpressions)
        {
            CountReferencesInQueryExpression(commonTableExpression.Body, cteNames, counts);

            if (commonTableExpression.RecursiveQuery is not null)
            {
                CountReferencesInSelectStatement(commonTableExpression.RecursiveQuery, cteNames, counts, includeNestedCommonTableExpressions: true);
            }
        }

        return counts;
    }

    /// <summary>
    /// Counts CTE-name references reachable through a <see cref="QueryExpression"/>
    /// (the body type used for CTEs). Handles plain <see cref="SelectQueryExpression"/>
    /// wrappers and compound <see cref="CompoundQueryExpression"/> trees
    /// (UNION / INTERSECT / EXCEPT). DML query expressions are out of scope —
    /// CTE bodies are read-only.
    /// </summary>
    private static void CountReferencesInQueryExpression(
        QueryExpression query,
        IReadOnlySet<string> commonTableExpressionNames,
        Dictionary<string, int> counts)
    {
        switch (query)
        {
            case SelectQueryExpression { Statement: SelectStatement select }:
                CountReferencesInSelectStatement(select, commonTableExpressionNames, counts, includeNestedCommonTableExpressions: true);
                break;

            case CompoundQueryExpression compound:
                CountReferencesInQueryExpression(compound.Left, commonTableExpressionNames, counts);
                CountReferencesInQueryExpression(compound.Right, commonTableExpressionNames, counts);
                break;
        }
    }

    /// <summary>
    /// Counts CTE-name references in a single <see cref="SelectStatement"/> — its
    /// FROM source, every JOIN source, and (transitively) any subquery sources
    /// nested inside. When <paramref name="includeNestedCommonTableExpressions"/>
    /// is true, also recurses into a nested WITH block so subqueries that re-bind
    /// a name locally still get walked for outer-name references.
    /// </summary>
    private static void CountReferencesInSelectStatement(
        SelectStatement statement,
        IReadOnlySet<string> commonTableExpressionNames,
        Dictionary<string, int> counts,
        bool includeNestedCommonTableExpressions)
    {
        if (statement.From is not null)
        {
            CountCommonTableExpressionReferencesInSource(statement.From.Source, commonTableExpressionNames, counts);
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                CountCommonTableExpressionReferencesInSource(join.Source, commonTableExpressionNames, counts);
            }
        }

        if (includeNestedCommonTableExpressions && statement.CommonTableExpressions is { Count: > 0 })
        {
            foreach (CommonTableExpression nested in statement.CommonTableExpressions)
            {
                CountReferencesInQueryExpression(nested.Body, commonTableExpressionNames, counts);
                if (nested.RecursiveQuery is not null)
                {
                    CountReferencesInSelectStatement(nested.RecursiveQuery, commonTableExpressionNames, counts, includeNestedCommonTableExpressions: true);
                }
            }
        }
    }

    /// <summary>
    /// Recursively counts CTE name references within a single table source.
    /// </summary>
    private static void CountCommonTableExpressionReferencesInSource(
        TableSource source,
        IReadOnlySet<string> commonTableExpressionNames,
        Dictionary<string, int> counts)
    {
        switch (source)
        {
            case TableReference tableReference:
                if (commonTableExpressionNames.Contains(tableReference.Name))
                {
                    counts[tableReference.Name]++;
                }
                break;

            case SubquerySource subquery:
                // Subqueries CAN reference outer CTEs — the CTE name is in scope
                // for any subquery within the same SELECT statement.
                CountReferencesInSelectStatement(subquery.Query, commonTableExpressionNames, counts, includeNestedCommonTableExpressions: true);
                break;

            case FunctionSource:
                // Table-valued functions don't reference CTEs through their name;
                // their argument expressions could in principle (e.g. a scalar
                // subquery) but we leave that for the broader expression walker.
                break;
        }
    }
}
