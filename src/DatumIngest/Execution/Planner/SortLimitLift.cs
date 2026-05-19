using System.Diagnostics.CodeAnalysis;
using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Lifts a chain of row-preserving column-adders (<see cref="ProjectOperator"/>
/// without ASSERT, <see cref="RowEnricherOperator"/>, <see cref="ModelInvocationOperator"/>)
/// above a <see cref="LimitOperator"/>+<see cref="OrderByOperator"/> pair when
/// the sort keys don't reference any column the chain introduces.
/// </summary>
/// <remarks>
/// <para>
/// Pattern: <c>Limit → OrderBy → [chain] → Source</c>. Rewrites to
/// <c>[chain] → Limit → OrderBy → Source</c> so expensive per-row work in the
/// chain (model invocation, image draws, etc.) only runs for the rows that
/// survive LIMIT.
/// </para>
/// <para>
/// Safety condition: every column referenced by any sort key must NOT be a
/// column introduced by the chain. Column-adder operators preserve every input
/// column and only append new ones, so a name not in the "added" set must come
/// from the source — which is exactly what Sort needs to see when it sits
/// directly above the source in the rewritten tree.
/// </para>
/// <para>
/// Complements <see cref="LimitPushdown"/>: that pass slides LIMIT through
/// wrappers when LIMIT is the root; this pass handles the harder case where
/// an ORDER BY sits between LIMIT and the chain.
/// </para>
/// </remarks>
internal static class SortLimitLift
{
    /// <summary>
    /// Returns <paramref name="root"/> with the chain lifted above LIMIT+ORDER BY
    /// when the pattern matches and the safety check passes. Leaves the tree
    /// unchanged otherwise.
    /// </summary>
    public static QueryOperator Lift(QueryOperator root)
    {
        if (root is not LimitOperator limit
            || limit.Source is not OrderByOperator orderBy)
        {
            return root;
        }

        List<QueryOperator> chain = [];
        QueryOperator current = orderBy.Source;

        while (TryUnwrap(current, out QueryOperator? inner))
        {
            chain.Add(current);
            current = inner!;
        }

        if (chain.Count == 0)
        {
            return root;
        }

        HashSet<string> sortReferencedNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (OrderByItem item in orderBy.OrderByItems)
        {
            foreach ((string? _, string columnName) in
                ColumnReferenceCollector.Collect(item.Expression))
            {
                sortReferencedNames.Add(columnName);
            }
        }

        foreach (QueryOperator chainOp in chain)
        {
            if (IntroducesAnyReferencedName(chainOp, sortReferencedNames))
            {
                return root;
            }
        }

        QueryOperator rebuilt = new LimitOperator(
            new OrderByOperator(current, orderBy.OrderByItems, orderBy.TopNRows),
            limit.LimitExpression,
            limit.OffsetExpression);

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            rebuilt = Rewrap(chain[i], rebuilt);
        }

        DatumActivity.Operators.Trace("SORT+LIMIT lifted below row-preserving column-adders");
        return rebuilt;
    }

    private static bool TryUnwrap(QueryOperator op, [NotNullWhen(true)] out QueryOperator? inner)
    {
        switch (op)
        {
            case ProjectOperator project when project.Assertions is null or { Count: 0 }:
                inner = project.Source;
                return true;
            case RowEnricherOperator enricher:
                inner = enricher.Source;
                return true;
            case ModelInvocationOperator model:
                inner = model.Source;
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
        ModelInvocationOperator m => new ModelInvocationOperator(newSource, m.Invocations),
        _ => throw new InvalidOperationException($"Unexpected chain operator: {original.GetType().Name}"),
    };

    private static bool IntroducesAnyReferencedName(QueryOperator op, HashSet<string> referencedNames)
    {
        switch (op)
        {
            case ModelInvocationOperator model:
                foreach (ModelInvocationOperator.Invocation invocation in model.Invocations)
                {
                    if (referencedNames.Contains(invocation.OutputColumnName))
                    {
                        return true;
                    }
                }
                return false;

            case RowEnricherOperator enricher:
                foreach (RowEnrichment enrichment in enricher.Enrichments)
                {
                    if (referencedNames.Contains(enrichment.ColumnName))
                    {
                        return true;
                    }
                }
                return false;

            case ProjectOperator project:
                if (project.LetBindings is not null)
                {
                    foreach (LetBinding binding in project.LetBindings)
                    {
                        if (binding.OutputAlias is not null
                            && referencedNames.Contains(binding.OutputAlias))
                        {
                            return true;
                        }
                    }
                }

                foreach (SelectColumn column in project.Columns)
                {
                    if (column is SelectAllColumns or SelectTableColumns)
                    {
                        continue;
                    }

                    string outputName = column.Alias
                        ?? ColumnNameResolver.GetRawName(column.Expression);

                    // Bare ColumnReference with no rename is a passthrough — the
                    // output name equals the source name, so Sort referencing it
                    // still resolves the same column post-lift.
                    bool isPassthrough = column.Alias is null
                        && column.Expression is ColumnReference colRef
                        && string.Equals(colRef.ColumnName, outputName,
                            StringComparison.OrdinalIgnoreCase);

                    if (!isPassthrough && referencedNames.Contains(outputName))
                    {
                        return true;
                    }
                }
                return false;

            default:
                return false;
        }
    }
}
