using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Result of attempting to extract equi-join keys from an ON condition.
/// Contains the list of expression key pairs to use for hash join
/// and an optional residual condition for non-equi parts.
/// </summary>
/// <param name="KeyPairs">
/// Pairs of (left, right) expressions whose evaluated values form the hash key.
/// Both sides are evaluated at runtime via <see cref="ExpressionEvaluator"/>.
/// </param>
/// <param name="Residual">
/// An optional expression that must be evaluated after the hash match
/// to check non-equi predicates. Null when all conjuncts are equalities.
/// </param>
public sealed record JoinKeyExtractionResult(
    IReadOnlyList<(Expression Left, Expression Right)> KeyPairs,
    Expression? Residual);

/// <summary>
/// Analyzes a JOIN ON condition to extract equi-join key expressions,
/// enabling hash join for conditions beyond simple column = column
/// (e.g. <c>GET_FILENAME(a.path) = b.name</c> or compound keys).
/// Non-equality conjuncts are returned as a residual filter.
/// </summary>
public static class JoinKeyExtractor
{
    /// <summary>
    /// Attempts to extract equi-join key pairs from the given ON condition.
    /// Returns null if no equality conjuncts are found.
    /// </summary>
    /// <param name="condition">The ON condition expression, or null for a CROSS join.</param>
    /// <returns>
    /// A <see cref="JoinKeyExtractionResult"/> with at least one key pair,
    /// or null if no equalities can be extracted.
    /// </returns>
    public static JoinKeyExtractionResult? TryExtract(Expression? condition)
    {
        if (condition is null)
        {
            return null;
        }

        List<Expression> conjuncts = new();
        FlattenAnd(condition, conjuncts);

        List<(Expression Left, Expression Right)> keyPairs = new();
        List<Expression> residuals = new();

        foreach (Expression conjunct in conjuncts)
        {
            if (conjunct is BinaryExpression binary && binary.Operator == BinaryOperator.Equal)
            {
                keyPairs.Add((binary.Left, binary.Right));
            }
            else
            {
                residuals.Add(conjunct);
            }
        }

        if (keyPairs.Count == 0)
        {
            return null;
        }

        Expression? residual = CombineWithAnd(residuals);

        return new JoinKeyExtractionResult(keyPairs, residual);
    }

    /// <summary>
    /// Recursively flattens AND-connected expressions into a list of conjuncts.
    /// </summary>
    private static void FlattenAnd(Expression expression, List<Expression> conjuncts)
    {
        if (expression is BinaryExpression binary && binary.Operator == BinaryOperator.And)
        {
            FlattenAnd(binary.Left, conjuncts);
            FlattenAnd(binary.Right, conjuncts);
        }
        else
        {
            conjuncts.Add(expression);
        }
    }

    /// <summary>
    /// Combines a list of expressions with AND. Returns null if the list is empty,
    /// or a single expression if only one element, or a left-associative AND chain.
    /// </summary>
    private static Expression? CombineWithAnd(List<Expression> expressions)
    {
        if (expressions.Count == 0)
        {
            return null;
        }

        Expression result = expressions[0];
        for (int index = 1; index < expressions.Count; index++)
        {
            result = new BinaryExpression(result, BinaryOperator.And, expressions[index]);
        }

        return result;
    }

    /// <summary>
    /// Normalizes equality expressions in the ON condition so that probe-side
    /// (left operator) column references appear on the left and build-side
    /// (right operator) column references appear on the right. This ensures
    /// the <see cref="JoinKeyExtractionResult.KeyPairs"/> convention is
    /// maintained regardless of the order in which the user wrote the condition
    /// or whether join reordering changed which table is on which side.
    /// </summary>
    /// <param name="onCondition">The ON condition expression to normalize.</param>
    /// <param name="leftAliases">Table aliases available on the probe (left) side of the join.</param>
    /// <returns>The normalized ON condition.</returns>
    public static Expression NormalizeKeyOrder(Expression onCondition, HashSet<string> leftAliases)
    {
        if (onCondition is BinaryExpression binary && binary.Operator == BinaryOperator.And)
        {
            Expression normalizedLeft = NormalizeKeyOrder(binary.Left, leftAliases);
            Expression normalizedRight = NormalizeKeyOrder(binary.Right, leftAliases);
            if (ReferenceEquals(normalizedLeft, binary.Left) && ReferenceEquals(normalizedRight, binary.Right))
                return onCondition;
            return new BinaryExpression(normalizedLeft, BinaryOperator.And, normalizedRight);
        }

        if (onCondition is BinaryExpression equality && equality.Operator == BinaryOperator.Equal)
        {
            HashSet<string> leftRefAliases = ColumnReferenceCollector.CollectTableAliases(equality.Left);
            HashSet<string> rightRefAliases = ColumnReferenceCollector.CollectTableAliases(equality.Right);

            bool leftRefsProbe = leftRefAliases.Overlaps(leftAliases);
            bool rightRefsProbe = rightRefAliases.Overlaps(leftAliases);

            // Swap when the right side references the probe and the left does not.
            if (rightRefsProbe && !leftRefsProbe)
            {
                return new BinaryExpression(equality.Right, BinaryOperator.Equal, equality.Left);
            }
        }

        return onCondition;
    }
}
