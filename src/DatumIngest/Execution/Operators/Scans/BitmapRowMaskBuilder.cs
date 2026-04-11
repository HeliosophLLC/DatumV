using System.Runtime.CompilerServices;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators.Scans;

/// <summary>
/// Builds a row-inclusion bitmap mask for a single chunk by composing
/// per-value bitmaps from <see cref="BitmapIndexSet"/> with AND/OR/NOT/IN
/// over a filter expression tree. The resulting <c>byte[]</c> mask is
/// indexed bit-by-bit (LSB-first within each byte) to decide whether each
/// row in the chunk should pass downstream.
/// </summary>
/// <remarks>
/// Returns <see langword="null"/> when the filter has no bitmap-eligible
/// sub-expressions — the caller's interpretation is "no constraint, pass
/// every row through" (the bitmap row filter is a may-prune layer, not the
/// authoritative filter).
/// </remarks>
internal static class BitmapRowMaskBuilder
{
    /// <summary>
    /// Builds a row-inclusion mask for one chunk. Returns <see langword="null"/>
    /// when no bitmap-eligible predicates exist (all rows should pass through).
    /// </summary>
    public static byte[]? Build(
        Expression? filterHint, BitmapIndexSet? bitmapIndexes,
        int chunkIndex, int rowCount, Arena arena)
    {
        if (filterHint is null || bitmapIndexes is null || bitmapIndexes.Count == 0)
        {
            return null;
        }

        return EvaluateExpression(filterHint, bitmapIndexes, chunkIndex, rowCount, arena);
    }

    /// <summary>
    /// Returns whether the bit at row offset <paramref name="rowOffset"/> is
    /// set in <paramref name="mask"/>. LSB-first within each byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsBitSet(byte[] mask, int rowOffset)
    {
        int byteIndex = rowOffset >> 3;
        int bitIndex = rowOffset & 7;
        return (mask[byteIndex] & (1 << bitIndex)) != 0;
    }

    /// <summary>
    /// Recursively evaluates a filter expression against bitmap indexes,
    /// composing per-value bitmaps with AND/OR/NOT to produce a row-inclusion
    /// bitset. Returns <see langword="null"/> when the sub-expression has no
    /// bitmap-eligible predicates.
    /// </summary>
    private static byte[]? EvaluateExpression(
        Expression expression, BitmapIndexSet bitmapIndexes,
        int chunkIndex, int rowCount, Arena arena)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                byte[]? leftBits = EvaluateExpression(binary.Left, bitmapIndexes, chunkIndex, rowCount, arena);
                byte[]? rightBits = EvaluateExpression(binary.Right, bitmapIndexes, chunkIndex, rowCount, arena);

                if (leftBits is not null && rightBits is not null)
                {
                    return BitmapComposer.And(leftBits, rightBits);
                }

                // Return whichever side produced a bitmap (AND with unknown = keep the known constraint).
                return leftBits ?? rightBits;
            }

            if (binary.Operator == BinaryOperator.Or)
            {
                byte[]? leftBits = EvaluateExpression(binary.Left, bitmapIndexes, chunkIndex, rowCount, arena);
                byte[]? rightBits = EvaluateExpression(binary.Right, bitmapIndexes, chunkIndex, rowCount, arena);

                if (leftBits is not null && rightBits is not null)
                {
                    return BitmapComposer.Or(leftBits, rightBits);
                }

                // OR with an unknown side: cannot constrain (either side might match any row).
                return null;
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                return EvaluateEquality(binary.Left, binary.Right, bitmapIndexes, chunkIndex, arena);
            }

            if (binary.Operator == BinaryOperator.NotEqual)
            {
                byte[]? equalBits = EvaluateEquality(
                    binary.Left, binary.Right, bitmapIndexes, chunkIndex, arena);

                if (equalBits is not null)
                {
                    return BitmapComposer.Not(equalBits, rowCount);
                }

                return null;
            }
        }

        if (expression is InExpression inExpression && !inExpression.Negated)
        {
            return EvaluateIn(inExpression, bitmapIndexes, chunkIndex, arena);
        }

        return null;
    }

    private static byte[]? EvaluateEquality(
        Expression left, Expression right, BitmapIndexSet bitmapIndexes,
        int chunkIndex, Arena arena)
    {
        string? columnName = null;
        object? rawLiteral = null;

        if (left is ColumnReference columnRef && right is LiteralExpression literal)
        {
            columnName = columnRef.ColumnName;
            rawLiteral = literal.Value;
        }
        else if (left is LiteralExpression literalLeft && right is ColumnReference columnRight)
        {
            columnName = columnRight.ColumnName;
            rawLiteral = literalLeft.Value;
        }

        if (columnName is null || rawLiteral is null)
        {
            return null;
        }

        if (!bitmapIndexes.TryGetIndex(columnName, out BitmapColumnIndex? bitmapIndex))
        {
            return null;
        }

        DataValue literalValue = DataValue.FromLiteral(rawLiteral, arena);
        if (!literalValue.IsInline)
        {
            return null;
        }

        ChunkBitmap bitmap = bitmapIndex.GetChunkBitmap(literalValue, chunkIndex);
        return bitmap.Bits.ToArray();
    }

    private static byte[]? EvaluateIn(
        InExpression inExpression, BitmapIndexSet bitmapIndexes,
        int chunkIndex, Arena arena)
    {
        if (inExpression.Expression is not ColumnReference columnRef)
        {
            return null;
        }

        if (!bitmapIndexes.TryGetIndex(columnRef.ColumnName, out BitmapColumnIndex? bitmapIndex))
        {
            return null;
        }

        byte[]? result = null;

        foreach (Expression valueExpression in inExpression.Values)
        {
            if (valueExpression is not LiteralExpression { Value: not null } literal)
            {
                return null;
            }

            DataValue value = DataValue.FromLiteral(literal.Value, arena);
            if (!value.IsInline)
            {
                return null;
            }

            ChunkBitmap bitmap = bitmapIndex.GetChunkBitmap(value, chunkIndex);

            if (result is null)
            {
                result = bitmap.Bits.ToArray();
            }
            else
            {
                BitmapComposer.Or(bitmap.Bits, result, result);
            }
        }

        return result;
    }
}
