using System.Linq;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators.Windows;

/// <summary>
/// Sorts a contiguous range of a row-index array by ORDER BY items, used by
/// the blocking window-shaped operators (<see cref="WindowOperator"/> and
/// <see cref="FoldScanOperator"/>) to order rows within each partition before
/// the per-spec compute pass.
/// </summary>
/// <remarks>
/// Pre-evaluates the sort keys once per row so the inner comparator stays
/// synchronous (<see cref="Array.Sort{T}(T[], IComparer{T})"/> can't await).
/// Comparator exceptions thrown by <see cref="Array.Sort{T}(T[], IComparer{T})"/>
/// wrap into <see cref="InvalidOperationException"/>; we unwrap and rethrow with
/// a diagnostic mentioning the <c>diagnosticLabel</c> argument and the ORDER BY
/// shape so debug output points at the actual operator.
/// </remarks>
internal static class PartitionSorter
{
    /// <summary>
    /// Sorts <c>indices[startIndex..startIndex+count)</c> by the pre-evaluated
    /// ORDER BY values of the rows they reference. The <c>diagnosticLabel</c>
    /// argument (e.g. <c>"WINDOW"</c>, <c>"SCAN"</c>) is used in error messages
    /// so a sort comparator failure points at the SQL construct that triggered it.
    /// </summary>
    public static async ValueTask SortPartitionAsync(
        List<Row> rows,
        int[] indices,
        int startIndex,
        int count,
        IReadOnlyList<OrderByItem> orderByItems,
        ExpressionEvaluator evaluator,
        IValueStore store,
        SidecarRegistry? sidecarRegistry,
        string diagnosticLabel,
        CancellationToken cancellationToken)
    {
        // Pre-evaluate sort keys once per (row, item) so the comparator can stay sync.
        DataValue[][] sortKeys = new DataValue[count][];
        for (int i = 0; i < count; i++)
        {
            int rowIndex = indices[startIndex + i];
            Row row = rows[rowIndex];
            DataValue[] keys = new DataValue[orderByItems.Count];
            for (int j = 0; j < orderByItems.Count; j++)
            {
                keys[j] = await evaluator
                    .EvaluateAsync(orderByItems[j].Expression, row, cancellationToken)
                    .ConfigureAwait(false);
            }
            sortKeys[i] = keys;
        }

        // Build a parallel index array (0..count-1) we sort, swapping sortKeys in lockstep.
        int[] localIndices = new int[count];
        for (int i = 0; i < count; i++) localIndices[i] = i;

        try
        {
            Array.Sort(localIndices, Comparer<int>.Create((a, b) =>
            {
                for (int k = 0; k < orderByItems.Count; k++)
                {
                    int comparison = CompareDataValues(
                        sortKeys[a][k], store, sidecarRegistry,
                        sortKeys[b][k], store, sidecarRegistry);
                    if (orderByItems[k].Direction == SortDirection.Descending)
                    {
                        comparison = -comparison;
                    }
                    if (comparison != 0)
                    {
                        return comparison;
                    }
                }
                return 0;
            }));
        }
        catch (InvalidOperationException ex)
        {
            // Array.Sort wraps comparator exceptions as InvalidOperationException.
            // Unwrap and re-throw with diagnostic context so the message points at
            // the operator and its ORDER BY shape, not the generic sort machinery.
            string orderByDesc = string.Join(", ", orderByItems.Select(o => o.Expression.ToString()));
            throw new InvalidOperationException(
                $"{diagnosticLabel} ORDER BY sort failed ({count} rows, ordering: {orderByDesc}). " +
                $"Inner cause: {ex.InnerException?.Message ?? ex.Message}",
                ex.InnerException ?? ex);
        }

        // Apply the sorted order back to indices[startIndex..startIndex+count).
        int[] reordered = new int[count];
        for (int i = 0; i < count; i++)
        {
            reordered[i] = indices[startIndex + localIndices[i]];
        }
        Array.Copy(reordered, 0, indices, startIndex, count);
    }

    private static int CompareDataValues(
        DataValue left, IValueStore leftStore, SidecarRegistry? leftRegistry,
        DataValue right, IValueStore rightStore, SidecarRegistry? rightRegistry)
    {
        if (left.IsNull && right.IsNull) return 0;
        if (left.IsNull) return 1;
        if (right.IsNull) return -1;

        return DataValueComparer.Compare(
            left, leftStore, leftRegistry,
            right, rightStore, rightRegistry);
    }
}
