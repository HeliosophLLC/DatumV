using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Resolves column-reference identifiers against the active SELECT list aliases
/// and LET bindings, substituting matches with the underlying expression.
/// </summary>
/// <remarks>
/// QUALIFY and ASSERT can reference SELECT-list aliases (e.g. <c>rn</c> from
/// <c>ROW_NUMBER() OVER (...) AS rn</c>) and LET-bound names even though those
/// names don't exist as row columns at the QUALIFY / ASSERT pipeline stage.
/// Resolving them at plan time substitutes the underlying expression so the
/// runtime sees a self-contained tree.
/// </remarks>
internal static class AliasResolver
{
    /// <summary>
    /// Resolves column references in <paramref name="expression"/> that match
    /// SELECT-list aliases by substituting them with the underlying (rewritten)
    /// expression. Allows QUALIFY to reference aliases like <c>rn</c> even though
    /// projection has not yet been applied at that pipeline stage.
    /// </summary>
    public static Expression ResolveSelectAliases(
        Expression expression, IReadOnlyList<SelectColumn> projectionColumns)
    {
        if (expression is ColumnReference column && column.TableName is null)
        {
            foreach (SelectColumn selectColumn in projectionColumns)
            {
                if (selectColumn.Alias is not null &&
                    string.Equals(selectColumn.Alias, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    return selectColumn.Expression;
                }
            }
        }

        if (expression is BinaryExpression binary)
        {
            Expression left = ResolveSelectAliases(binary.Left, projectionColumns);
            Expression right = ResolveSelectAliases(binary.Right, projectionColumns);
            if (ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right))
            {
                return expression;
            }

            return new BinaryExpression(left, binary.Operator, right);
        }

        if (expression is UnaryExpression unary)
        {
            Expression operand = ResolveSelectAliases(unary.Operand, projectionColumns);
            return ReferenceEquals(operand, unary.Operand) ? expression : new UnaryExpression(unary.Operator, operand);
        }

        return expression;
    }

    /// <summary>
    /// Resolves column references in <paramref name="expression"/> that match
    /// LET binding names by substituting them with the binding's expression. This
    /// allows QUALIFY to reference LET-bound names as expression substitution (not
    /// memoised, since QUALIFY runs before projection).
    /// </summary>
    public static Expression ResolveLetBindingReferences(
        Expression expression, IReadOnlyList<LetBinding>? letBindings)
    {
        if (letBindings is null || letBindings.Count == 0)
        {
            return expression;
        }

        if (expression is ColumnReference column && column.TableName is null)
        {
            foreach (LetBinding binding in letBindings)
            {
                if (string.Equals(binding.Name, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    return binding.Expression;
                }
            }
        }

        if (expression is BinaryExpression binary)
        {
            Expression left = ResolveLetBindingReferences(binary.Left, letBindings);
            Expression right = ResolveLetBindingReferences(binary.Right, letBindings);
            if (ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right))
            {
                return expression;
            }

            return new BinaryExpression(left, binary.Operator, right);
        }

        if (expression is UnaryExpression unary)
        {
            Expression operand = ResolveLetBindingReferences(unary.Operand, letBindings);
            return ReferenceEquals(operand, unary.Operand) ? expression : new UnaryExpression(unary.Operator, operand);
        }

        return expression;
    }
}
