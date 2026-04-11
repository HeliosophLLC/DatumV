using System.Diagnostics.CodeAnalysis;
using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators.Scans;

/// <summary>
/// Walks a <see cref="ScanOperator"/> filter expression and answers two
/// kinds of question:
/// <list type="bullet">
/// <item><description><b>Per-chunk pruning</b> — <see cref="CanPruneSorted"/>
/// and <see cref="CanPruneBitmap"/> traverse the expression tree and probe
/// the relevant index, returning <see langword="true"/> when the chunk can
/// be safely skipped.</description></item>
/// <item><description><b>Top-level predicate extraction</b> —
/// <see cref="ExtractEqualities"/>, <see cref="ExtractBetweens"/>, and
/// <see cref="ExtractIns"/> descend through top-level AND chains
/// (OR branches halt the descent) and collect each
/// indexable predicate so the seek planner can probe the right index
/// directly.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Replaces the eight near-mirrored static helpers that used to live on
/// <see cref="ScanOperator"/> (<c>CheckExpressionForPruning</c>,
/// <c>CheckExpressionForBitmapPruning</c>, and their per-predicate
/// equivalents) plus the three <c>ExtractTopLevel...</c> walkers. Sharing
/// the column-literal orientation helper here ensures sorted-index and
/// bitmap-index pruning agree on what counts as a recognised predicate.
/// </remarks>
internal static class PredicatePruningAnalyzer
{
    // ─────────────────────── Sorted-index pruning ───────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="filter"/> proves
    /// the chunk cannot contain any matching rows according to the
    /// sorted-index data on <paramref name="provider"/>.
    /// </summary>
    public static bool CanPruneSorted(
        Expression filter, ITableProvider provider, int chunkIndex, Arena arena)
    {
        if (filter is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                // AND: prune if either side says we can prune.
                return CanPruneSorted(binary.Left, provider, chunkIndex, arena)
                    || CanPruneSorted(binary.Right, provider, chunkIndex, arena);
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                return CheckSortedEquality(binary.Left, binary.Right, provider, chunkIndex, arena);
            }

            if (binary.Operator is BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
                or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual)
            {
                return CheckSortedComparison(
                    binary.Left, binary.Right, binary.Operator, provider, chunkIndex, arena);
            }
        }

        if (filter is BetweenExpression between && !between.Negated)
        {
            return CheckSortedBetween(between, provider, chunkIndex, arena);
        }

        if (filter is InExpression inExpression && !inExpression.Negated)
        {
            return CheckSortedIn(inExpression, provider, chunkIndex, arena);
        }

        return false;
    }

    private static bool CheckSortedEquality(
        Expression left, Expression right, ITableProvider provider, int chunkIndex, Arena arena)
    {
        if (!TryMatchColumnLiteral(left, right, out string? columnName, out object? rawLiteral))
        {
            return false;
        }

        if (!provider.TryGetColumnIndex(columnName, out IColumnIndex? index))
        {
            return false;
        }

        DataValue literalValue = DataValue.FromLiteral(rawLiteral, arena);
        if (!literalValue.IsInline)
        {
            return false;
        }

        IReadOnlySet<int> matchingChunks = index.FindChunksContaining(literalValue);
        return !matchingChunks.Contains(chunkIndex);
    }

    private static bool CheckSortedComparison(
        Expression left, Expression right, BinaryOperator op,
        ITableProvider provider, int chunkIndex, Arena arena)
    {
        string? columnName = null;
        object? rawLiteral = null;
        BinaryOperator effectiveOperator = op;

        if (left is ColumnReference columnRef && right is LiteralExpression literal)
        {
            columnName = columnRef.ColumnName;
            rawLiteral = literal.Value;
        }
        else if (left is LiteralExpression literalLeft && right is ColumnReference columnRight)
        {
            columnName = columnRight.ColumnName;
            rawLiteral = literalLeft.Value;
            effectiveOperator = FlipComparisonOperator(op);
        }

        if (columnName is null || rawLiteral is null)
        {
            return false;
        }

        if (!provider.TryGetColumnIndex(columnName, out IColumnIndex? index))
        {
            return false;
        }

        DataValue literalValue = DataValue.FromLiteral(rawLiteral, arena);

        if (!literalValue.IsInline)
        {
            return false;
        }

        IReadOnlySet<int> matchingChunks = effectiveOperator switch
        {
            BinaryOperator.LessThan => index.FindChunksLessThan(literalValue),
            BinaryOperator.LessThanOrEqual => index.FindChunksLessThanOrEqual(literalValue),
            BinaryOperator.GreaterThan => index.FindChunksGreaterThan(literalValue),
            BinaryOperator.GreaterThanOrEqual => index.FindChunksGreaterThanOrEqual(literalValue),
            _ => throw new InvalidOperationException($"Unexpected operator: {effectiveOperator}"),
        };

        return !matchingChunks.Contains(chunkIndex);
    }

    private static bool CheckSortedBetween(
        BetweenExpression between, ITableProvider provider, int chunkIndex, Arena arena)
    {
        if (between.Expression is not ColumnReference columnRef)
        {
            return false;
        }

        if (between.Low is not LiteralExpression { Value: not null } lowLiteral
            || between.High is not LiteralExpression { Value: not null } highLiteral)
        {
            return false;
        }

        if (!provider.TryGetColumnIndex(columnRef.ColumnName, out IColumnIndex? index))
        {
            return false;
        }

        DataValue low = DataValue.FromLiteral(lowLiteral.Value, arena);
        DataValue high = DataValue.FromLiteral(highLiteral.Value, arena);

        if (!low.IsInline || !high.IsInline)
        {
            return false;
        }

        IReadOnlySet<int> matchingChunks = index.FindChunksInRange(low, high);
        return !matchingChunks.Contains(chunkIndex);
    }

    private static bool CheckSortedIn(
        InExpression inExpression, ITableProvider provider, int chunkIndex, Arena arena)
    {
        if (inExpression.Expression is not ColumnReference columnRef)
        {
            return false;
        }

        if (!provider.TryGetColumnIndex(columnRef.ColumnName, out IColumnIndex? index))
        {
            return false;
        }

        // If any IN value exists in this chunk, the chunk cannot be pruned.
        foreach (Expression valueExpression in inExpression.Values)
        {
            if (valueExpression is not LiteralExpression { Value: not null } literal)
            {
                return false;
            }

            DataValue value = DataValue.FromLiteral(literal.Value, arena);
            if (!value.IsInline)
            {
                return false;
            }

            IReadOnlySet<int> matchingChunks = index.FindChunksContaining(value);

            if (matchingChunks.Contains(chunkIndex))
            {
                return false;
            }
        }

        return true;
    }

    // ─────────────────────── Bitmap-index pruning ───────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="filter"/> proves
    /// the chunk cannot contain any matching rows according to the
    /// per-column bitmap indexes in <paramref name="bitmapIndexes"/>.
    /// </summary>
    public static bool CanPruneBitmap(
        Expression filter, BitmapIndexSet bitmapIndexes, int chunkIndex, Arena arena)
    {
        if (filter is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                // AND: prune if either side proves the chunk empty.
                return CanPruneBitmap(binary.Left, bitmapIndexes, chunkIndex, arena)
                    || CanPruneBitmap(binary.Right, bitmapIndexes, chunkIndex, arena);
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                return CheckBitmapEquality(binary.Left, binary.Right, bitmapIndexes, chunkIndex, arena);
            }
        }

        if (filter is InExpression inExpression && !inExpression.Negated)
        {
            return CheckBitmapIn(inExpression, bitmapIndexes, chunkIndex, arena);
        }

        return false;
    }

    private static bool CheckBitmapEquality(
        Expression left, Expression right, BitmapIndexSet bitmapIndexes, int chunkIndex, Arena arena)
    {
        if (!TryMatchColumnLiteral(left, right, out string? columnName, out object? rawLiteral))
        {
            return false;
        }

        if (!bitmapIndexes.TryGetIndex(columnName, out BitmapColumnIndex? bitmapIndex))
        {
            return false;
        }

        DataValue literalValue = DataValue.FromLiteral(rawLiteral, arena);
        return literalValue.IsInline
            && !bitmapIndex.ChunkContainsValue(literalValue, chunkIndex);
    }

    private static bool CheckBitmapIn(
        InExpression inExpression, BitmapIndexSet bitmapIndexes, int chunkIndex, Arena arena)
    {
        if (inExpression.Expression is not ColumnReference columnRef)
        {
            return false;
        }

        if (!bitmapIndexes.TryGetIndex(columnRef.ColumnName, out BitmapColumnIndex? bitmapIndex))
        {
            return false;
        }

        // If any IN value exists in this chunk, the chunk cannot be pruned.
        foreach (Expression valueExpression in inExpression.Values)
        {
            if (valueExpression is not LiteralExpression { Value: not null } literal)
            {
                return false;
            }

            DataValue value = DataValue.FromLiteral(literal.Value, arena);

            if (value.IsInline && bitmapIndex.ChunkContainsValue(value, chunkIndex))
            {
                return false;
            }
        }

        return true;
    }

    // ─────────────────────── Top-level extraction ───────────────────────

    /// <summary>
    /// Descends through the top-level AND chain of <paramref name="filter"/>
    /// and collects each <c>column = literal</c> equality predicate into
    /// <paramref name="results"/>. OR branches halt the descent — the seek
    /// planner cannot guarantee that all matching rows would be in an
    /// index result set derived from a single OR branch.
    /// </summary>
    public static void ExtractEqualities(
        Expression filter,
        List<(string Column, DataValue Value)> results,
        Arena arena)
    {
        if (filter is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                ExtractEqualities(binary.Left, results, arena);
                ExtractEqualities(binary.Right, results, arena);
                return;
            }

            if (binary.Operator == BinaryOperator.Equal
                && TryMatchColumnLiteral(binary.Left, binary.Right, out string? columnName, out object? rawLiteral))
            {
                DataValue literalValue = DataValue.FromLiteral(rawLiteral, arena);
                if (!literalValue.IsInline) return;
                results.Add((columnName, literalValue));
            }
        }
    }

    /// <summary>
    /// Descends through the top-level AND chain of <paramref name="filter"/>
    /// and collects each non-negated <c>column BETWEEN low AND high</c>
    /// predicate with literal bounds into <paramref name="results"/>.
    /// </summary>
    public static void ExtractBetweens(
        Expression filter,
        List<(string Column, DataValue Low, DataValue High)> results,
        Arena arena)
    {
        if (filter is BinaryExpression binary && binary.Operator == BinaryOperator.And)
        {
            ExtractBetweens(binary.Left, results, arena);
            ExtractBetweens(binary.Right, results, arena);
            return;
        }

        if (filter is BetweenExpression between && !between.Negated
            && between.Expression is ColumnReference columnRef
            && between.Low is LiteralExpression { Value: not null } lowLiteral
            && between.High is LiteralExpression { Value: not null } highLiteral)
        {
            DataValue low = DataValue.FromLiteral(lowLiteral.Value, arena);
            DataValue high = DataValue.FromLiteral(highLiteral.Value, arena);

            if (!low.IsInline || !high.IsInline) return;

            results.Add((columnRef.ColumnName, low, high));
        }
    }

    /// <summary>
    /// Descends through the top-level AND chain of <paramref name="filter"/>
    /// and collects each non-negated <c>column IN (v1, v2, ...)</c>
    /// predicate with all-literal values into <paramref name="results"/>.
    /// </summary>
    public static void ExtractIns(
        Expression filter,
        List<(string Column, List<DataValue> Values)> results,
        Arena arena)
    {
        if (filter is BinaryExpression binary && binary.Operator == BinaryOperator.And)
        {
            ExtractIns(binary.Left, results, arena);
            ExtractIns(binary.Right, results, arena);
            return;
        }

        if (filter is InExpression inExpression && !inExpression.Negated
            && inExpression.Expression is ColumnReference columnRef)
        {
            List<DataValue> values = new(inExpression.Values.Count);

            foreach (Expression valueExpression in inExpression.Values)
            {
                if (valueExpression is not LiteralExpression { Value: not null } literal)
                {
                    return;
                }

                DataValue dv = DataValue.FromLiteral(literal.Value, arena);

                if (!dv.IsInline) return;

                values.Add(dv);
            }

            results.Add((columnRef.ColumnName, values));
        }
    }

    // ─────────────────────── Shared helpers ───────────────────────

    /// <summary>
    /// Matches <c>column = literal</c> OR <c>literal = column</c> orientation
    /// for the workhorse equality / comparison predicates. Returns
    /// <see langword="false"/> when the binary's operands don't match the
    /// column-versus-literal shape on either side.
    /// </summary>
    private static bool TryMatchColumnLiteral(
        Expression left, Expression right,
        [NotNullWhen(true)] out string? columnName,
        [NotNullWhen(true)] out object? rawLiteral)
    {
        if (left is ColumnReference columnRef && right is LiteralExpression literal && literal.Value is not null)
        {
            columnName = columnRef.ColumnName;
            rawLiteral = literal.Value;
            return true;
        }

        if (left is LiteralExpression literalLeft && literalLeft.Value is not null && right is ColumnReference columnRight)
        {
            columnName = columnRight.ColumnName;
            rawLiteral = literalLeft.Value;
            return true;
        }

        columnName = null;
        rawLiteral = null;
        return false;
    }

    /// <summary>
    /// Flips a comparison operator to account for reversed operand order
    /// (e.g. <c>5 &lt; col</c> becomes <c>col &gt; 5</c>).
    /// </summary>
    private static BinaryOperator FlipComparisonOperator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.LessThan => BinaryOperator.GreaterThan,
            BinaryOperator.LessThanOrEqual => BinaryOperator.GreaterThanOrEqual,
            BinaryOperator.GreaterThan => BinaryOperator.LessThan,
            BinaryOperator.GreaterThanOrEqual => BinaryOperator.LessThanOrEqual,
            _ => op,
        };
    }
}
