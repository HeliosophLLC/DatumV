using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Wraps a planned source with <see cref="PivotOperator"/> or
/// <see cref="UnpivotOperator"/> when the SELECT statement carries a
/// <c>PIVOT</c> or <c>UNPIVOT</c> clause. PIVOT / UNPIVOT are mutually
/// exclusive in valid SQL — the AST guarantees at most one is set.
/// </summary>
/// <remarks>
/// PIVOT is currently in-memory only — spill against
/// <c>SpillReaderWriter</c> is a follow-up.
/// </remarks>
internal static class PivotApplier
{
    /// <summary>
    /// Returns a <see cref="PivotOperator"/> wrapping <paramref name="source"/>
    /// when <paramref name="statement"/> has a <c>PIVOT</c> clause, an
    /// <see cref="UnpivotOperator"/> when it has <c>UNPIVOT</c>, or
    /// <paramref name="source"/> unchanged when it has neither. Each PIVOT
    /// aggregate is resolved against <paramref name="functionRegistry"/>;
    /// unknown aggregates produce a <see cref="QueryPlanException"/>.
    /// </summary>
    public static QueryOperator ApplyPivotOrUnpivot(
        QueryOperator source,
        SelectStatement statement,
        FunctionRegistry functionRegistry)
    {
        if (statement.Pivot is not null)
        {
            PivotClause pivot = statement.Pivot;
            List<AggregateColumn> pivotAggregates = new(pivot.Aggregates.Count);
            foreach (FunctionCallExpression call in pivot.Aggregates)
            {
                IAggregateFunction? aggregateFunction = functionRegistry.TryGetAggregate(call.CallName);
                if (aggregateFunction is null)
                {
                    throw new QueryPlanException(
                        $"PIVOT aggregate '{call.CallName}' is not a registered aggregate function.");
                }

                bool isCountStar = AggregateRewriter.IsCountStarCall(call);
                IReadOnlyList<Expression> arguments = isCountStar
                    ? Array.Empty<Expression>()
                    : call.Arguments;
                string outputName = QueryExplainer.FormatExpression(call);

                pivotAggregates.Add(new AggregateColumn(
                    aggregateFunction, arguments, outputName, isCountStar, call.Distinct));
            }

            source = new PivotOperator(
                source,
                pivotAggregates,
                pivot.PivotColumn,
                pivot.ValueList);
        }

        if (statement.Unpivot is not null)
        {
            UnpivotClause unpivot = statement.Unpivot;
            string[] sourceColumnNames = new string[unpivot.SourceColumns.Count];
            for (int i = 0; i < unpivot.SourceColumns.Count; i++)
            {
                sourceColumnNames[i] = unpivot.SourceColumns[i].ColumnName;
            }

            source = new UnpivotOperator(
                source,
                unpivot.ValueColumnName,
                unpivot.NameColumnName,
                sourceColumnNames,
                unpivot.IncludeNulls);
        }

        return source;
    }
}
