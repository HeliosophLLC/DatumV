using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Plan-time literal folding helpers. Used by <see cref="QueryPlanner"/> to extract
/// constant values from expression trees before execution — for LIMIT/OFFSET row
/// counts, TABLESAMPLE percentages and seeds, and full-text-search query strings.
/// </summary>
/// <remarks>
/// Folding is conservative: only <see cref="LiteralExpression"/> nodes (and a small
/// number of identity-wrapping calls like <c>plainto_tsquery</c>) are recognised.
/// Computed expressions, parameters, and column references return <see langword="null"/>
/// rather than triggering a planner-time evaluator — semantics matching whatever
/// runtime path consumes the value.
/// </remarks>
internal static class LiteralFolding
{
    /// <summary>
    /// Folds a LIMIT + OFFSET pair into the equivalent top-N row count for bounded
    /// sort planning. Returns <see langword="null"/> when either expression isn't
    /// a constant integer literal, or when the offset is negative.
    /// </summary>
    public static int? TryComputeTopN(Expression? limit, Expression? offset)
    {
        if (limit is null) return null;
        int? limitN = TryFoldInt(limit);
        if (limitN is null) return null;
        int offsetN = offset is null ? 0 : TryFoldInt(offset) ?? -1;
        if (offsetN < 0) return null;
        return limitN + offsetN;
    }

    /// <summary>
    /// Folds <paramref name="expression"/> to a 32-bit integer if it is a numeric literal.
    /// The parser narrows literals to the smallest fitting integer type (LIMIT 10 →
    /// sbyte, LIMIT 1000 → short, etc.), so every integer kind is accepted.
    /// Returns <see langword="null"/> for non-literals or out-of-range values.
    /// </summary>
    public static int? TryFoldInt(Expression expression)
    {
        if (expression is LiteralExpression lit)
        {
            return lit.Value switch
            {
                sbyte sb => sb,
                byte b => b,
                short sh => sh,
                ushort us => us,
                int i => i,
                uint u when u <= int.MaxValue => (int)u,
                long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
                _ => null,
            };
        }
        return null;
    }

    /// <summary>
    /// Extracts a constant <see cref="string"/> from a FTS query expression. Recognises a
    /// bare <see cref="LiteralExpression"/> wrapping a <see cref="string"/>, or a call to
    /// <c>plainto_tsquery</c> wrapping one. Returns <see langword="false"/> for column
    /// refs, parameters, or other computed expressions.
    /// </summary>
    public static bool TryExtractConstantString(Expression expr, out string? text)
    {
        text = null;
        switch (expr)
        {
            case LiteralExpression { Value: string s }:
                text = s;
                return true;
            case FunctionCallExpression call
                when string.Equals(call.FunctionName, "plainto_tsquery", StringComparison.OrdinalIgnoreCase)
                  && call.Arguments.Count == 1:
                return TryExtractConstantString(call.Arguments[0], out text);
            default:
                return false;
        }
    }

    /// <summary>
    /// Evaluates a constant expression to a <see cref="double"/> at plan time.
    /// Used for TABLESAMPLE percentage and REPEATABLE seed values. Throws when the
    /// expression isn't a numeric literal — those clauses can't bind at execute time.
    /// </summary>
    public static double EvaluateConstantDouble(Expression expression)
    {
        return expression switch
        {
            LiteralExpression { Value: sbyte int8Value } => int8Value,
            LiteralExpression { Value: short int16Value } => int16Value,
            LiteralExpression { Value: int intValue } => intValue,
            LiteralExpression { Value: long longValue } => longValue,
            LiteralExpression { Value: float floatValue } => floatValue,
            LiteralExpression { Value: double doubleValue } => doubleValue,
            _ => throw new InvalidOperationException(
                "TABLESAMPLE percentage and REPEATABLE seed must be constant numeric values."),
        };
    }
}
