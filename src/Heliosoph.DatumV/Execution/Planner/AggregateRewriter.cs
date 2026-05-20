using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Replaces aggregate <see cref="FunctionCallExpression"/> nodes in an expression
/// tree with <see cref="ColumnReference"/> nodes pointing at the
/// <see cref="GroupByOperator"/>'s output. Walks the standard composite shapes
/// (binary / unary / cast / scalar call / CASE) so an aggregate nested inside any of
/// them is hoisted out into a registered <see cref="AggregateColumn"/>.
/// </summary>
/// <remarks>
/// <para>
/// Aggregates are deduplicated by output name — repeating <c>COUNT(*)</c> in a
/// SELECT list only registers one column. WITHIN GROUP semantics fold into the
/// <see cref="AggregateColumn"/>'s arguments / OrderBy fields per the aggregate's
/// <see cref="WithinGroupSemantics"/> declaration; unsupported combinations raise a
/// clear plan-time error.
/// </para>
/// <para>
/// Also exposes the <see cref="AppendOrderByAggregatePassthroughs"/> helper that
/// surfaces aggregate columns ORDER BY references but the projection drops — adds
/// synthetic passthrough columns that a follow-up trim
/// <see cref="ProjectOperator"/> removes after sorting.
/// </para>
/// </remarks>
internal static class AggregateRewriter
{
    /// <summary>
    /// Rewrites an expression by replacing aggregate <see cref="FunctionCallExpression"/>
    /// nodes with <see cref="ColumnReference"/> nodes that reference the output columns
    /// of the <see cref="GroupByOperator"/>. Each unique aggregate is added to
    /// <paramref name="aggregateColumns"/> only once.
    /// </summary>
    public static Expression RewriteAggregateExpression(
        Expression expression,
        FunctionRegistry functionRegistry,
        List<AggregateColumn> aggregateColumns)
    {
        if (expression is FunctionCallExpression func)
        {
            IAggregateFunction? aggregateFunction =
                functionRegistry.TryGetAggregate(func.CallName);

            if (aggregateFunction is not null)
            {
                bool isCountStar = IsCountStarCall(func);

                if (func.Distinct && isCountStar)
                {
                    throw new InvalidOperationException(
                        "COUNT(DISTINCT *) is not supported. Use COUNT(DISTINCT column) instead.");
                }
                string outputName = QueryExplainer.FormatExpression(func);

                // Deduplicate: reuse existing AggregateColumn if the same aggregate
                // expression already appears (e.g. SELECT COUNT(*), COUNT(*) FROM t).
                bool alreadyRegistered = false;
                foreach (AggregateColumn existing in aggregateColumns)
                {
                    if (string.Equals(existing.OutputName, outputName, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyRegistered = true;
                        break;
                    }
                }

                if (!alreadyRegistered)
                {
                    IReadOnlyList<Expression> arguments = isCountStar
                        ? Array.Empty<Expression>()
                        : func.Arguments;

                    // Apply WITHIN GROUP semantics declared by the aggregate.
                    // Ordered-set aggregates take the WITHIN GROUP expressions as
                    // their data — prepend them to args so the accumulator sees the
                    // data column at arguments[0]. Sort-modifier aggregates leave
                    // args alone; the row sort is applied by the GroupByOperator
                    // using the OrderBy field. NotSupported aggregates with WITHIN
                    // GROUP raise a clear plan-time error.
                    IReadOnlyList<OrderByItem>? orderBy = func.OrderBy;
                    if (func.WithinGroupOrderBy is not null)
                    {
                        switch (aggregateFunction.WithinGroupSemantics)
                        {
                            case WithinGroupSemantics.OrderedSet:
                                arguments = [
                                    .. func.WithinGroupOrderBy.Select(o => o.Expression),
                                    .. arguments,
                                ];
                                orderBy = func.WithinGroupOrderBy;
                                break;
                            case WithinGroupSemantics.SortModifier:
                                orderBy = func.WithinGroupOrderBy;
                                break;
                            case WithinGroupSemantics.NotSupported:
                            default:
                                throw new InvalidOperationException(
                                    $"Aggregate '{aggregateFunction.Name}' does not accept " +
                                    "WITHIN GROUP (ORDER BY …). Use the inline ORDER BY form " +
                                    "inside the parens, or remove the clause.");
                        }
                    }

                    aggregateColumns.Add(new AggregateColumn(
                        aggregateFunction, arguments, outputName, isCountStar, func.Distinct, orderBy));
                }

                return new ColumnReference(null, outputName);
            }
        }

        // Recurse into sub-expressions.
        return expression switch
        {
            FunctionCallExpression scalarFunc => RewriteScalarFunctionArguments(
                scalarFunc, functionRegistry, aggregateColumns),
            BinaryExpression bin => new BinaryExpression(
                RewriteAggregateExpression(bin.Left, functionRegistry, aggregateColumns),
                bin.Operator,
                RewriteAggregateExpression(bin.Right, functionRegistry, aggregateColumns)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                RewriteAggregateExpression(unary.Operand, functionRegistry, aggregateColumns)),
            CastExpression cast => new CastExpression(
                RewriteAggregateExpression(cast.Expression, functionRegistry, aggregateColumns),
                cast.TargetType),
            CaseExpression caseExpr => RewriteCaseAggregateExpression(caseExpr, functionRegistry, aggregateColumns),
            _ => expression,
        };
    }

    /// <summary>
    /// Rewrites the arguments of a non-aggregate <see cref="FunctionCallExpression"/>
    /// so that nested aggregate calls (e.g. <c>DATE_DIFF('day', MIN(x), MAX(x))</c>)
    /// are replaced with column references to the <see cref="GroupByOperator"/> output.
    /// </summary>
    public static Expression RewriteScalarFunctionArguments(
        FunctionCallExpression func,
        FunctionRegistry functionRegistry,
        List<AggregateColumn> aggregateColumns)
    {
        bool changed = false;
        Expression[] rewrittenArgs = new Expression[func.Arguments.Count];
        for (int i = 0; i < func.Arguments.Count; i++)
        {
            rewrittenArgs[i] = RewriteAggregateExpression(
                func.Arguments[i], functionRegistry, aggregateColumns);
            if (!ReferenceEquals(rewrittenArgs[i], func.Arguments[i]))
            {
                changed = true;
            }
        }

        return changed
            ? new FunctionCallExpression(func.FunctionName, rewrittenArgs, func.OrderBy, func.Distinct, func.Span, func.WithinGroupOrderBy)
            : func;
    }

    /// <summary>
    /// Rewrites aggregate references inside a CASE expression by descending
    /// into operand, WHEN conditions, THEN results, and the ELSE branch.
    /// </summary>
    public static CaseExpression RewriteCaseAggregateExpression(
        CaseExpression caseExpression,
        FunctionRegistry functionRegistry,
        List<AggregateColumn> aggregateColumns)
    {
        Expression? rewrittenOperand = caseExpression.Operand is not null
            ? RewriteAggregateExpression(caseExpression.Operand, functionRegistry, aggregateColumns)
            : null;

        List<WhenClause> rewrittenClauses = new(caseExpression.WhenClauses.Count);
        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            rewrittenClauses.Add(new WhenClause(
                RewriteAggregateExpression(whenClause.Condition, functionRegistry, aggregateColumns),
                RewriteAggregateExpression(whenClause.Result, functionRegistry, aggregateColumns)));
        }

        Expression? rewrittenElse = caseExpression.ElseResult is not null
            ? RewriteAggregateExpression(caseExpression.ElseResult, functionRegistry, aggregateColumns)
            : null;

        return new CaseExpression(rewrittenOperand, rewrittenClauses, rewrittenElse, caseExpression.Span);
    }

    /// <summary>
    /// After ORDER BY items have been aggregate-rewritten, finds any aggregate
    /// column names ORDER BY references that aren't in the projection's output,
    /// and appends synthetic <see cref="SelectColumn"/> entries that pass them
    /// through. Their names are recorded in <paramref name="passthroughs"/> so a
    /// follow-up trim <see cref="ProjectOperator"/> can drop them after the sort.
    /// </summary>
    /// <remarks>
    /// Skipped when the projection contains a wildcard — the wildcard already
    /// passes through every column the GroupBy emits, so the aggregate is in scope
    /// at sort time. The user sees the extra column in their output, matching the
    /// wildcard contract.
    /// </remarks>
    public static void AppendOrderByAggregatePassthroughs(
        IReadOnlyList<OrderByItem> rewrittenOrderByItems,
        IReadOnlyList<AggregateColumn> aggregateColumns,
        List<SelectColumn> rewrittenColumns,
        ref List<string>? passthroughs)
    {
        if (aggregateColumns.Count == 0) return;

        // Wildcard projection already includes every GroupBy output column, so no
        // passthrough is needed (and trimming would be incorrect — the user asked
        // for everything). Bail out early.
        foreach (SelectColumn column in rewrittenColumns)
        {
            if (column is SelectAllColumns or SelectTableColumns) return;
        }

        HashSet<string> aggregateOutputNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (AggregateColumn aggregate in aggregateColumns)
        {
            aggregateOutputNames.Add(aggregate.OutputName);
        }

        HashSet<string> projectionOutputNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectColumn column in rewrittenColumns)
        {
            string outputName = column.Alias
                ?? ColumnNameResolver.GetRawName(column.Expression);
            projectionOutputNames.Add(outputName);
        }

        foreach (OrderByItem item in rewrittenOrderByItems)
        {
            foreach ((string? _, string columnName) in
                ColumnReferenceCollector.Collect(item.Expression))
            {
                if (!aggregateOutputNames.Contains(columnName)) continue;
                if (projectionOutputNames.Contains(columnName)) continue;

                passthroughs ??= new List<string>();
                if (passthroughs.Contains(columnName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                passthroughs.Add(columnName);
                projectionOutputNames.Add(columnName);

                // Synthetic entry: alias matches the aggregate's output name so the
                // projected schema exposes it under the same name the rewritten
                // ORDER BY items reference.
                rewrittenColumns.Add(new SelectColumn(
                    new ColumnReference(null, columnName),
                    columnName));
            }
        }
    }

    /// <summary>
    /// Detects the <c>COUNT(*)</c> sentinel pattern: a function call to COUNT with a
    /// single <see cref="LiteralExpression"/> argument whose value is the string
    /// <c>"*"</c>. The parser emits this shape so the planner can distinguish
    /// row-count COUNT(*) from value-count COUNT(col).
    /// </summary>
    public static bool IsCountStarCall(FunctionCallExpression function)
    {
        return string.Equals(function.FunctionName, "COUNT", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 1
            && function.Arguments[0] is LiteralExpression literal
            && literal.Value is string value
            && value == "*";
    }
}
