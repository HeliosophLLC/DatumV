using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Detects and rewrites <see cref="ScanExpression"/> nodes (SQL <c>SCAN</c> /
/// prefix-scan / FOLD form) in an expression tree, hoisting them into
/// <see cref="FoldScanColumn"/> descriptors that the <see cref="FoldScanOperator"/>
/// evaluates. Also rewrites the inner <c>PREV(col)</c> calls into the
/// <c>__prev_</c>-prefixed column references the operator binds at execution time.
/// </summary>
/// <remarks>
/// Detection (<see cref="HasScanExpression"/>, <see cref="HasLetScanExpression"/>,
/// <see cref="ExpressionContainsScanExpression"/>) drives the planner's gate for the
/// scan pipeline rung. Rewriting (<see cref="RewriteScanExpression"/>,
/// <see cref="RewriteCaseScanExpression"/>) is then applied to every SELECT column
/// and LET binding that contains one. Each registered SCAN exposes its first output
/// alias as the rewritten expression's <see cref="ColumnReference"/>; tuple-form
/// scans additionally surface their other aliases as columns from the operator.
/// </remarks>
internal static class ScanExpressionRewriter
{
    /// <summary>
    /// Returns <see langword="true"/> if any <see cref="SelectColumn"/> expression
    /// contains a <see cref="ScanExpression"/>. Wildcard columns are skipped.
    /// </summary>
    public static bool HasScanExpression(IReadOnlyList<SelectColumn> columns)
    {
        foreach (SelectColumn column in columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                continue;
            }

            if (ExpressionContainsScanExpression(column.Expression))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any <see cref="LetBinding"/> expression
    /// contains a <see cref="ScanExpression"/>.
    /// </summary>
    public static bool HasLetScanExpression(IReadOnlyList<LetBinding>? letBindings)
    {
        if (letBindings is null)
        {
            return false;
        }

        foreach (LetBinding binding in letBindings)
        {
            if (ExpressionContainsScanExpression(binding.Expression))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recursively checks whether an expression tree contains a
    /// <see cref="ScanExpression"/>. Descends through the standard composite shapes
    /// (binary / unary / cast / CASE / function call) so a scan nested in any of
    /// them surfaces.
    /// </summary>
    public static bool ExpressionContainsScanExpression(Expression expression)
    {
        return expression switch
        {
            ScanExpression => true,
            BinaryExpression bin => ExpressionContainsScanExpression(bin.Left)
                || ExpressionContainsScanExpression(bin.Right),
            UnaryExpression unary => ExpressionContainsScanExpression(unary.Operand),
            CastExpression cast => ExpressionContainsScanExpression(cast.Expression),
            CaseExpression caseExpr =>
                (caseExpr.Operand is not null && ExpressionContainsScanExpression(caseExpr.Operand))
                || caseExpr.WhenClauses.Any(w =>
                    ExpressionContainsScanExpression(w.Condition) || ExpressionContainsScanExpression(w.Result))
                || (caseExpr.ElseResult is not null && ExpressionContainsScanExpression(caseExpr.ElseResult)),
            FunctionCallExpression func => func.Arguments.Any(ExpressionContainsScanExpression),
            _ => false,
        };
    }

    /// <summary>
    /// Rewrites an expression by replacing <see cref="ScanExpression"/> nodes with
    /// <see cref="ColumnReference"/> nodes that reference the output columns of the
    /// <see cref="FoldScanOperator"/>. Each SCAN expression is converted to a
    /// <see cref="FoldScanColumn"/> descriptor. PREV() calls inside body expressions
    /// are rewritten to <c>__prev_</c>-prefixed column references.
    /// </summary>
    public static Expression RewriteScanExpression(
        Expression expression,
        List<FoldScanColumn> scanColumns)
    {
        if (expression is ScanExpression scan)
        {
            // Validate counts match.
            if (scan.AccumulatorNames.Count != scan.BodyExpressions.Count
                || scan.AccumulatorNames.Count != scan.InitExpressions.Count
                || scan.AccumulatorNames.Count != scan.OutputAliases.Count)
            {
                throw new InvalidOperationException(
                    "SCAN expression has mismatched accumulator, body, init, and alias counts.");
            }

            if (scan.Window.OrderBy is null or { Count: 0 })
            {
                throw new InvalidOperationException(
                    "SCAN expression requires ORDER BY in the OVER clause.");
            }

            // Collect PREV() column references from body expressions.
            HashSet<string> prevColumnNames = new(StringComparer.OrdinalIgnoreCase);

            // Rewrite PREV(col) calls in body expressions to __prev_col column references.
            List<Expression> rewrittenBodies = new(scan.BodyExpressions.Count);
            foreach (Expression body in scan.BodyExpressions)
            {
                rewrittenBodies.Add(RewritePrevCalls(body, prevColumnNames));
            }

            scanColumns.Add(new FoldScanColumn(
                scan.AccumulatorNames,
                rewrittenBodies,
                scan.InitExpressions,
                scan.Window,
                scan.OutputAliases,
                prevColumnNames.ToList()));

            // Replace the SCAN expression with a column reference to the first output alias.
            // For tuple form, the other aliases are also available as columns from the operator.
            return new ColumnReference(null, scan.OutputAliases[0]);
        }

        // Recurse into sub-expressions.
        return expression switch
        {
            BinaryExpression bin => new BinaryExpression(
                RewriteScanExpression(bin.Left, scanColumns),
                bin.Operator,
                RewriteScanExpression(bin.Right, scanColumns)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                RewriteScanExpression(unary.Operand, scanColumns)),
            CastExpression cast => new CastExpression(
                RewriteScanExpression(cast.Expression, scanColumns),
                cast.TargetType),
            CaseExpression caseExpr => RewriteCaseScanExpression(caseExpr, scanColumns),
            _ => expression,
        };
    }

    /// <summary>
    /// Rewrites SCAN expression references inside a CASE expression by descending
    /// into operand, WHEN conditions, THEN results, and the ELSE branch.
    /// </summary>
    public static CaseExpression RewriteCaseScanExpression(
        CaseExpression caseExpression,
        List<FoldScanColumn> scanColumns)
    {
        Expression? rewrittenOperand = caseExpression.Operand is not null
            ? RewriteScanExpression(caseExpression.Operand, scanColumns)
            : null;

        List<WhenClause> rewrittenClauses = new(caseExpression.WhenClauses.Count);
        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            rewrittenClauses.Add(new WhenClause(
                RewriteScanExpression(whenClause.Condition, scanColumns),
                RewriteScanExpression(whenClause.Result, scanColumns)));
        }

        Expression? rewrittenElse = caseExpression.ElseResult is not null
            ? RewriteScanExpression(caseExpression.ElseResult, scanColumns)
            : null;

        return new CaseExpression(rewrittenOperand, rewrittenClauses, rewrittenElse, caseExpression.Span);
    }

    /// <summary>
    /// Rewrites <c>PREV(col)</c> function calls into <c>__prev_col</c> column references
    /// and collects the <c>__prev_</c>-prefixed column names into
    /// <paramref name="prevColumnNames"/>. The <see cref="FoldScanOperator"/> binds
    /// these synthetic columns to the previous row's values during fold evaluation.
    /// </summary>
    public static Expression RewritePrevCalls(
        Expression expression,
        HashSet<string> prevColumnNames)
    {
        if (expression is FunctionCallExpression func
            && string.Equals(func.FunctionName, "PREV", StringComparison.OrdinalIgnoreCase))
        {
            if (func.Arguments.Count != 1 || func.Arguments[0] is not ColumnReference colRef)
            {
                throw new InvalidOperationException(
                    "PREV() requires exactly one column reference argument.");
            }

            string prevName = "__prev_" + colRef.ColumnName;
            prevColumnNames.Add(prevName);
            return new ColumnReference(null, prevName);
        }

        return expression switch
        {
            BinaryExpression bin => new BinaryExpression(
                RewritePrevCalls(bin.Left, prevColumnNames),
                bin.Operator,
                RewritePrevCalls(bin.Right, prevColumnNames)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                RewritePrevCalls(unary.Operand, prevColumnNames)),
            CastExpression cast => new CastExpression(
                RewritePrevCalls(cast.Expression, prevColumnNames),
                cast.TargetType),
            FunctionCallExpression funcExpr => new FunctionCallExpression(
                funcExpr.FunctionName,
                funcExpr.Arguments.Select(a => RewritePrevCalls(a, prevColumnNames)).ToList(),
                funcExpr.OrderBy,
                funcExpr.Distinct,
                funcExpr.Span,
                funcExpr.WithinGroupOrderBy),
            IsNullExpression isNull => new IsNullExpression(
                RewritePrevCalls(isNull.Expression, prevColumnNames),
                isNull.Negated),
            BetweenExpression between => new BetweenExpression(
                RewritePrevCalls(between.Expression, prevColumnNames),
                RewritePrevCalls(between.Low, prevColumnNames),
                RewritePrevCalls(between.High, prevColumnNames),
                between.Negated),
            InExpression inExpr => new InExpression(
                RewritePrevCalls(inExpr.Expression, prevColumnNames),
                inExpr.Values.Select(v => RewritePrevCalls(v, prevColumnNames)).ToList(),
                inExpr.Negated),
            CaseExpression caseExpr => new CaseExpression(
                caseExpr.Operand is not null ? RewritePrevCalls(caseExpr.Operand, prevColumnNames) : null,
                caseExpr.WhenClauses.Select(w => new WhenClause(
                    RewritePrevCalls(w.Condition, prevColumnNames),
                    RewritePrevCalls(w.Result, prevColumnNames))).ToList(),
                caseExpr.ElseResult is not null ? RewritePrevCalls(caseExpr.ElseResult, prevColumnNames) : null,
                caseExpr.Span),
            IndexAccessExpression idx => new IndexAccessExpression(
                RewritePrevCalls(idx.Source, prevColumnNames),
                idx.Indices.Select(i => RewritePrevCalls(i, prevColumnNames)).ToArray(),
                idx.Span),
            _ => expression,
        };
    }
}
