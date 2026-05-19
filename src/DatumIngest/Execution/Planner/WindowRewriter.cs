using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Detects and rewrites <see cref="WindowFunctionCallExpression"/> nodes in an
/// expression tree, hoisting them into <see cref="WindowColumn"/> entries that the
/// <see cref="WindowOperator"/> computes once. References in SELECT / QUALIFY /
/// ASSERT clauses become <see cref="ColumnReference"/> nodes pointing at the
/// hoisted output names.
/// </summary>
/// <remarks>
/// <para>
/// Detection (<see cref="HasWindowFunction"/>, <see cref="ExpressionContainsWindowFunction"/>,
/// <see cref="CaseExpressionContainsWindowFunction"/>, <see cref="HasAssertWindowFunction"/>)
/// drives the planner's "do we need a <see cref="WindowOperator"/>?" gate. Rewriting
/// (<see cref="RewriteWindowExpression"/>, <see cref="RewriteCaseWindowExpression"/>)
/// happens after the gate fires.
/// </para>
/// <para>
/// Window calls are deduplicated by output name — repeating
/// <c>ROW_NUMBER() OVER (ORDER BY x)</c> in a SELECT registers one column.
/// <c>DISTINCT</c> inside a window call is rejected at plan time.
/// </para>
/// </remarks>
internal static class WindowRewriter
{
    /// <summary>
    /// Returns <see langword="true"/> if any SELECT column contains a window function
    /// call. Wildcard columns are skipped.
    /// </summary>
    public static bool HasWindowFunction(
        IReadOnlyList<SelectColumn> columns,
        FunctionRegistry functionRegistry)
    {
        foreach (SelectColumn column in columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                continue;
            }

            if (ExpressionContainsWindowFunction(column.Expression))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recursively checks whether an expression tree contains a
    /// <see cref="WindowFunctionCallExpression"/>. Descends through the standard
    /// composite shapes so a window call nested in any of them surfaces.
    /// </summary>
    public static bool ExpressionContainsWindowFunction(Expression expression)
    {
        return expression switch
        {
            WindowFunctionCallExpression => true,
            BinaryExpression bin => ExpressionContainsWindowFunction(bin.Left)
                || ExpressionContainsWindowFunction(bin.Right),
            UnaryExpression unary => ExpressionContainsWindowFunction(unary.Operand),
            CastExpression cast => ExpressionContainsWindowFunction(cast.Expression),
            CaseExpression caseExpr => CaseExpressionContainsWindowFunction(caseExpr),
            FunctionCallExpression func => func.Arguments.Any(ExpressionContainsWindowFunction),
            _ => false,
        };
    }

    /// <summary>
    /// Checks whether a CASE expression contains any window function calls in its
    /// operand, WHEN conditions, THEN results, or ELSE result.
    /// </summary>
    public static bool CaseExpressionContainsWindowFunction(CaseExpression caseExpression)
    {
        if (caseExpression.Operand is not null && ExpressionContainsWindowFunction(caseExpression.Operand))
        {
            return true;
        }

        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            if (ExpressionContainsWindowFunction(whenClause.Condition)
                || ExpressionContainsWindowFunction(whenClause.Result))
            {
                return true;
            }
        }

        return caseExpression.ElseResult is not null
            && ExpressionContainsWindowFunction(caseExpression.ElseResult);
    }

    /// <summary>
    /// Returns <see langword="true"/> if any ASSERT clause's predicate or message
    /// contains a window function call. Drives ASSERT-aware window operator placement.
    /// </summary>
    public static bool HasAssertWindowFunction(IReadOnlyList<AssertClause>? assertions)
    {
        if (assertions is null)
        {
            return false;
        }

        foreach (AssertClause assertClause in assertions)
        {
            if (ExpressionContainsWindowFunction(assertClause.Predicate))
            {
                return true;
            }

            if (assertClause.Message is not null
                && ExpressionContainsWindowFunction(assertClause.Message))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites an expression by replacing <see cref="WindowFunctionCallExpression"/>
    /// nodes with <see cref="ColumnReference"/> nodes that reference the output
    /// columns of the <see cref="WindowOperator"/>. Each unique window call is
    /// added to <paramref name="windowColumns"/> only once. <c>DISTINCT</c> in a
    /// window call is rejected with a clear plan-time error.
    /// </summary>
    public static Expression RewriteWindowExpression(
        Expression expression,
        FunctionRegistry functionRegistry,
        List<WindowColumn> windowColumns)
    {
        if (expression is WindowFunctionCallExpression windowCall)
        {
            if (windowCall.Distinct)
            {
                throw new InvalidOperationException(
                    $"DISTINCT is not supported in window functions: " +
                    $"'{QueryExplainer.FormatExpression(windowCall)}'.");
            }

            string outputName = QueryExplainer.FormatExpression(windowCall);

            // Deduplicate: reuse existing WindowColumn if the same expression already appears.
            bool alreadyRegistered = false;
            foreach (WindowColumn existing in windowColumns)
            {
                if (string.Equals(existing.OutputName, outputName, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyRegistered = true;
                    break;
                }
            }

            if (!alreadyRegistered)
            {
                IWindowFunction? windowFunction =
                    functionRegistry.TryGetWindowOrAggregate(windowCall.CallName);

                if (windowFunction is null)
                {
                    throw new InvalidOperationException(
                        $"Unknown window function: '{windowCall.CallName}'.");
                }

                windowColumns.Add(new WindowColumn(
                    windowFunction,
                    windowCall.Arguments,
                    windowCall.Window,
                    outputName,
                    windowCall.NullHandling,
                    windowCall.FromLast));
            }

            return new ColumnReference(null, outputName);
        }

        // Recurse into sub-expressions.
        return expression switch
        {
            BinaryExpression bin => new BinaryExpression(
                RewriteWindowExpression(bin.Left, functionRegistry, windowColumns),
                bin.Operator,
                RewriteWindowExpression(bin.Right, functionRegistry, windowColumns)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                RewriteWindowExpression(unary.Operand, functionRegistry, windowColumns)),
            CastExpression cast => new CastExpression(
                RewriteWindowExpression(cast.Expression, functionRegistry, windowColumns),
                cast.TargetType),
            CaseExpression caseExpr => RewriteCaseWindowExpression(caseExpr, functionRegistry, windowColumns),
            _ => expression,
        };
    }

    /// <summary>
    /// Rewrites window function references inside a CASE expression by descending
    /// into operand, WHEN conditions, THEN results, and the ELSE branch.
    /// </summary>
    public static CaseExpression RewriteCaseWindowExpression(
        CaseExpression caseExpression,
        FunctionRegistry functionRegistry,
        List<WindowColumn> windowColumns)
    {
        Expression? rewrittenOperand = caseExpression.Operand is not null
            ? RewriteWindowExpression(caseExpression.Operand, functionRegistry, windowColumns)
            : null;

        List<WhenClause> rewrittenClauses = new(caseExpression.WhenClauses.Count);
        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            rewrittenClauses.Add(new WhenClause(
                RewriteWindowExpression(whenClause.Condition, functionRegistry, windowColumns),
                RewriteWindowExpression(whenClause.Result, functionRegistry, windowColumns)));
        }

        Expression? rewrittenElse = caseExpression.ElseResult is not null
            ? RewriteWindowExpression(caseExpression.ElseResult, functionRegistry, windowColumns)
            : null;

        return new CaseExpression(rewrittenOperand, rewrittenClauses, rewrittenElse, caseExpression.Span);
    }
}
