using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Evaluates whether a predicate expression is provably unsatisfiable given
/// the column statistics (min/max bounds, null counts) of a data partition.
/// Used by filterable providers to skip entire partitions (e.g. Parquet row groups)
/// that cannot contain any matching rows.
/// </summary>
/// <remarks>
/// This evaluator is <b>conservative</b>: it returns <c>true</c> (skip) only when
/// the statistics conclusively prove no rows can match. It returns <c>false</c>
/// (do not skip) for any expression shape it does not understand.
/// </remarks>
public static class StatisticsPredicateEvaluator
{
    /// <summary>
    /// Determines whether a partition can be skipped based on its column statistics.
    /// </summary>
    /// <param name="predicate">The WHERE predicate to evaluate against statistics.</param>
    /// <param name="statistics">Per-column statistics for the partition, keyed by column name (case-insensitive).</param>
    /// <returns>
    /// <c>true</c> if the predicate is provably unsatisfiable for every row in this partition;
    /// <c>false</c> if the partition might contain matching rows (or if the predicate cannot
    /// be evaluated against statistics).
    /// </returns>
    public static bool CanSkipPartition(
        Expression predicate,
        IReadOnlyDictionary<string, ColumnStatisticsRange> statistics)
    {
        return CanSkip(predicate, statistics);
    }

    private static bool CanSkip(
        Expression expression,
        IReadOnlyDictionary<string, ColumnStatisticsRange> statistics)
    {
        return expression switch
        {
            BinaryExpression binary => CanSkipBinary(binary, statistics),
            InExpression inExpression => CanSkipIn(inExpression, statistics),
            BetweenExpression between => CanSkipBetween(between, statistics),
            IsNullExpression isNull => CanSkipIsNull(isNull, statistics),
            LikeExpression => false, // Cannot prune partitions for LIKE with ESCAPE.
            _ => false, // Conservative: unknown expression → do not skip.
        };
    }

    // ──────────────────── Binary expressions ────────────────────

    private static bool CanSkipBinary(
        BinaryExpression binary,
        IReadOnlyDictionary<string, ColumnStatisticsRange> statistics)
    {
        // AND: skip if either side can be skipped (any false conjunct kills the whole AND).
        if (binary.Operator == BinaryOperator.And)
        {
            return CanSkip(binary.Left, statistics) || CanSkip(binary.Right, statistics);
        }

        // OR: skip only if both sides can be skipped (all disjuncts must be false).
        if (binary.Operator == BinaryOperator.Or)
        {
            return CanSkip(binary.Left, statistics) && CanSkip(binary.Right, statistics);
        }

        // Comparison operators: col op literal or literal op col.
        if (!IsComparisonOperator(binary.Operator))
        {
            return false;
        }

        // Try to extract (column, literal, needsFlip) from the binary expression.
        if (!TryExtractColumnAndLiteral(binary, out string? columnName, out DataValue literalValue, out bool flipped))
        {
            return false;
        }

        if (!statistics.TryGetValue(columnName!, out ColumnStatisticsRange? range))
        {
            return false;
        }

        if (range.Minimum is null || range.Maximum is null)
        {
            return false; // No statistics available — cannot prune.
        }

        // Flip the operator if the literal was on the left: "5 > col" becomes "col < 5".
        BinaryOperator effectiveOperator = flipped ? FlipOperator(binary.Operator) : binary.Operator;

        return CanSkipComparison(effectiveOperator, range.Minimum.Value, range.Maximum.Value, literalValue);
    }

    /// <summary>
    /// Determines whether a comparison predicate is unsatisfiable for a partition
    /// whose column has the given [min, max] bounds.
    /// </summary>
    private static bool CanSkipComparison(
        BinaryOperator op,
        DataValue minimum,
        DataValue maximum,
        DataValue literal)
    {
        return op switch
        {
            // col = literal → skip if literal < min or literal > max
            BinaryOperator.Equal => CompareValues(literal, minimum) < 0
                                 || CompareValues(literal, maximum) > 0,

            // col != literal → skip if min = max = literal (all rows have the same value)
            BinaryOperator.NotEqual => CompareValues(minimum, maximum) == 0
                                    && CompareValues(minimum, literal) == 0,

            // col < literal → skip if min >= literal
            BinaryOperator.LessThan => CompareValues(minimum, literal) >= 0,

            // col <= literal → skip if min > literal
            BinaryOperator.LessThanOrEqual => CompareValues(minimum, literal) > 0,

            // col > literal → skip if max <= literal
            BinaryOperator.GreaterThan => CompareValues(maximum, literal) <= 0,

            // col >= literal → skip if max < literal
            BinaryOperator.GreaterThanOrEqual => CompareValues(maximum, literal) < 0,

            _ => false,
        };
    }

    // ──────────────────── IN expression ────────────────────

    private static bool CanSkipIn(
        InExpression inExpression,
        IReadOnlyDictionary<string, ColumnStatisticsRange> statistics)
    {
        // Only handle: col IN (literal1, literal2, ...)
        if (inExpression.Expression is not ColumnReference column)
        {
            return false;
        }

        string columnName = column.ColumnName;
        if (!statistics.TryGetValue(columnName, out ColumnStatisticsRange? range))
        {
            return false;
        }

        if (range.Minimum is null || range.Maximum is null)
        {
            return false;
        }

        if (inExpression.Negated)
        {
            // NOT IN — skip only if min = max and that single value is in the exclusion list.
            if (CompareValues(range.Minimum.Value, range.Maximum.Value) != 0)
            {
                return false;
            }

            foreach (Expression valueExpression in inExpression.Values)
            {
                if (valueExpression is LiteralExpression literal)
                {
                    DataValue? literalValue = LiteralToDataValue(literal.Value, range.Minimum.Value.Kind);
                    if (literalValue is not null && CompareValues(range.Minimum.Value, literalValue.Value) == 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // col IN (v1, v2, ...) — skip if every literal is outside [min, max].
        foreach (Expression valueExpression in inExpression.Values)
        {
            if (valueExpression is not LiteralExpression literal)
            {
                return false; // Non-literal value — cannot evaluate.
            }

            DataValue? literalValue = LiteralToDataValue(literal.Value, range.Minimum.Value.Kind);
            if (literalValue is null)
            {
                return false; // Cannot convert — be conservative.
            }

            // If any value falls within [min, max], we cannot skip.
            if (CompareValues(literalValue.Value, range.Minimum.Value) >= 0
                && CompareValues(literalValue.Value, range.Maximum.Value) <= 0)
            {
                return false;
            }
        }

        return true;
    }

    // ──────────────────── BETWEEN expression ────────────────────

    private static bool CanSkipBetween(
        BetweenExpression between,
        IReadOnlyDictionary<string, ColumnStatisticsRange> statistics)
    {
        if (between.Expression is not ColumnReference column)
        {
            return false;
        }

        string columnName = column.ColumnName;
        if (!statistics.TryGetValue(columnName, out ColumnStatisticsRange? range))
        {
            return false;
        }

        if (range.Minimum is null || range.Maximum is null)
        {
            return false;
        }

        // Both bounds must be literals.
        if (between.Low is not LiteralExpression lowLiteral ||
            between.High is not LiteralExpression highLiteral)
        {
            return false;
        }

        DataValue? lowValue = LiteralToDataValue(lowLiteral.Value, range.Minimum.Value.Kind);
        DataValue? highValue = LiteralToDataValue(highLiteral.Value, range.Minimum.Value.Kind);

        if (lowValue is null || highValue is null)
        {
            return false;
        }

        if (between.Negated)
        {
            // NOT BETWEEN low AND high → col < low OR col > high
            // Skip if all values are within [low, high]: min >= low AND max <= high.
            return CompareValues(range.Minimum.Value, lowValue.Value) >= 0
                && CompareValues(range.Maximum.Value, highValue.Value) <= 0;
        }

        // BETWEEN low AND high → col >= low AND col <= high
        // Skip if the partition range doesn't overlap [low, high]: max < low OR min > high.
        return CompareValues(range.Maximum.Value, lowValue.Value) < 0
            || CompareValues(range.Minimum.Value, highValue.Value) > 0;
    }

    // ──────────────────── IS NULL / IS NOT NULL ────────────────────

    private static bool CanSkipIsNull(
        IsNullExpression isNull,
        IReadOnlyDictionary<string, ColumnStatisticsRange> statistics)
    {
        if (isNull.Expression is not ColumnReference column)
        {
            return false;
        }

        string columnName = column.ColumnName;
        if (!statistics.TryGetValue(columnName, out ColumnStatisticsRange? range))
        {
            return false;
        }

        if (range.NullCount is null)
        {
            return false; // No null count available — cannot prune.
        }

        if (isNull.Negated)
        {
            // IS NOT NULL — skip if all rows are null.
            return range.NullCount == range.RowCount;
        }

        // IS NULL — skip if no rows are null.
        return range.NullCount == 0;
    }

    // ──────────────────── Helpers ────────────────────

    /// <summary>
    /// Extracts a (columnName, literalValue, flipped) triple from a binary comparison.
    /// Returns <c>false</c> if the expression is not a column-vs-literal comparison.
    /// </summary>
    private static bool TryExtractColumnAndLiteral(
        BinaryExpression binary,
        out string? columnName,
        out DataValue literalValue,
        out bool flipped)
    {
        columnName = null;
        literalValue = default;
        flipped = false;

        // col op literal
        if (binary.Left is ColumnReference leftColumn && binary.Right is LiteralExpression rightLiteral)
        {
            columnName = leftColumn.ColumnName;
            DataValue? temp = LiteralToDataValue(rightLiteral.Value, targetKind: null);
            if (temp is null) return false;
            literalValue = temp.Value;
            return true;
        }

        // literal op col → flip
        if (binary.Left is LiteralExpression leftLiteral && binary.Right is ColumnReference rightColumn)
        {
            columnName = rightColumn.ColumnName;
            DataValue? temp = LiteralToDataValue(leftLiteral.Value, targetKind: null);
            if (temp is null) return false;
            literalValue = temp.Value;
            flipped = true;
            return true;
        }

        return false;
    }

    private static bool IsComparisonOperator(BinaryOperator op)
    {
        return op is BinaryOperator.Equal
            or BinaryOperator.NotEqual
            or BinaryOperator.LessThan
            or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThan
            or BinaryOperator.GreaterThanOrEqual;
    }

    /// <summary>
    /// Flips a comparison operator for "literal op col" → "col flipped-op literal".
    /// </summary>
    private static BinaryOperator FlipOperator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.LessThan => BinaryOperator.GreaterThan,
            BinaryOperator.LessThanOrEqual => BinaryOperator.GreaterThanOrEqual,
            BinaryOperator.GreaterThan => BinaryOperator.LessThan,
            BinaryOperator.GreaterThanOrEqual => BinaryOperator.LessThanOrEqual,
            _ => op, // Equal and NotEqual are symmetric.
        };
    }

    /// <summary>
    /// Converts an AST literal value to a <see cref="DataValue"/>.
    /// </summary>
    internal static DataValue? LiteralToDataValue(object? value, DataKind? targetKind)
    {
        if (value is null)
        {
            return null; // NULL literals don't participate in statistics comparisons.
        }

        return value switch
        {
            int intValue => DataValue.FromFloat32(intValue),
            long longValue => DataValue.FromFloat32(longValue),
            float floatValue => DataValue.FromFloat32(floatValue),
            double doubleValue => DataValue.FromFloat32((float)doubleValue),
            decimal decimalValue => DataValue.FromFloat32((float)decimalValue),
            string stringValue => DataValue.FromString(stringValue),
            bool boolValue => DataValue.FromFloat32(boolValue ? 1f : 0f),
            _ => null,
        };
    }

    /// <summary>
    /// Compares two <see cref="DataValue"/> instances using the same semantics
    /// as <see cref="ExpressionEvaluator"/>: ordinal for strings, CompareTo for
    /// dates, float coercion for numerics.
    /// </summary>
    internal static int CompareValues(DataValue left, DataValue right)
    {
        if (left.Kind == DataKind.String && right.Kind == DataKind.String)
        {
            return string.Compare(left.AsString(), right.AsString(), StringComparison.Ordinal);
        }

        if (left.Kind == DataKind.Date && right.Kind == DataKind.Date)
        {
            return left.AsDate().CompareTo(right.AsDate());
        }

        if (left.Kind == DataKind.DateTime && right.Kind == DataKind.DateTime)
        {
            return left.AsDateTime().CompareTo(right.AsDateTime());
        }

        // Numeric comparison via double coercion.
        double leftDouble = ToDouble(left);
        double rightDouble = ToDouble(right);
        return leftDouble.CompareTo(rightDouble);
    }

    private static double ToDouble(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.Float32 => value.AsFloat32(),
            DataKind.Float64 => value.AsFloat64(),
            DataKind.UInt8 => value.AsUInt8(),
            DataKind.Int8 => value.AsInt8(),
            DataKind.Int16 => value.AsInt16(),
            DataKind.UInt16 => value.AsUInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.UInt32 => value.AsUInt32(),
            DataKind.Int64 => value.AsInt64(),
            DataKind.UInt64 => value.AsUInt64(),
            _ => 0d,
        };
    }
}
