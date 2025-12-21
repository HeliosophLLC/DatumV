using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Window;

/// <summary>
/// Shared utilities for window function computations.
/// </summary>
internal static class WindowFunctionHelper
{
    /// <summary>
    /// Determines whether two rows are equal on all ORDER BY expressions.
    /// Used by ranking functions (RANK, DENSE_RANK) to detect ties.
    /// </summary>
    internal static async ValueTask<bool> AreOrderByValuesEqualAsync(
        Row rowA,
        Row rowB,
        IReadOnlyList<OrderByItem>? orderByItems,
        ExpressionEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        if (orderByItems is null || orderByItems.Count == 0)
        {
            return true;
        }

        for (int i = 0; i < orderByItems.Count; i++)
        {
            DataValue valueA = await evaluator.EvaluateAsync(orderByItems[i].Expression, rowA, cancellationToken).ConfigureAwait(false);
            DataValue valueB = await evaluator.EvaluateAsync(orderByItems[i].Expression, rowB, cancellationToken).ConfigureAwait(false);

            if (!Equals(valueA, valueB))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the start and end row indices for a window frame relative
    /// to the current row within a partition.
    /// </summary>
    /// <param name="frame">The window frame specification.</param>
    /// <param name="currentIndex">The index of the current row within the partition.</param>
    /// <param name="partitionSize">The total number of rows in the partition.</param>
    /// <returns>Clamped (inclusive start, inclusive end) indices.</returns>
    internal static (int Start, int End) ResolveFrameBounds(
        WindowFrame? frame,
        int currentIndex,
        int partitionSize)
    {
        if (frame is null)
        {
            return (0, partitionSize - 1);
        }

        int start = ResolveBound(frame.Start, currentIndex, partitionSize, isStart: true);
        int end = ResolveBound(frame.End, currentIndex, partitionSize, isStart: false);

        // Clamp to valid range.
        start = System.Math.Max(0, start);
        end = System.Math.Min(partitionSize - 1, end);

        return (start, end);
    }

    private static int ResolveBound(FrameBound bound, int currentIndex, int partitionSize, bool isStart)
    {
        return bound switch
        {
            UnboundedPrecedingBound => 0,
            UnboundedFollowingBound => partitionSize - 1,
            CurrentRowBound => currentIndex,
            PrecedingBound preceding => currentIndex - preceding.Offset,
            FollowingBound following => currentIndex + following.Offset,
            _ => isStart ? 0 : partitionSize - 1,
        };
    }

    /// <summary>
    /// Converts any numeric <see cref="DataValue"/> to an <see langword="int"/>.
    /// Used by window functions that accept integer offset or bucket count arguments.
    /// </summary>
    internal static int ToInt(DataValue value) => value.ToInt32();
}
