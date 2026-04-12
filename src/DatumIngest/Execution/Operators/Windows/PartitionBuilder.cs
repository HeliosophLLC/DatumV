using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators.Windows;

/// <summary>
/// Groups a row index array into contiguous partitions by evaluating the
/// PARTITION BY key expressions per row and reordering the indices in-place
/// so rows belonging to the same partition occupy a contiguous range.
/// </summary>
/// <remarks>
/// <para>
/// Used by the blocking window-shaped operators (<see cref="WindowOperator"/>
/// and <see cref="FoldScanOperator"/>) — both feed their fully-materialised
/// row list through this routine to get the partition boundaries before
/// per-partition sort and compute.
/// </para>
/// <para>
/// Single-column PARTITION BY uses a <see cref="DataValue"/>-keyed dictionary
/// (struct-copy keys, identical to <c>GroupByOperator</c>'s single-key path);
/// multi-column uses <see cref="CompositeKey"/> with a heap-allocated parts
/// array per first-seen key. Hash semantics match the rest of the engine —
/// arena-backed value content is captured by <see cref="DataValue.RawContentHash"/>.
/// </para>
/// </remarks>
internal static class PartitionBuilder
{
    /// <summary>
    /// Partitions <paramref name="indices"/> in-place by the values of
    /// <paramref name="partitionByExpressions"/> evaluated against
    /// <paramref name="rows"/>. Returns the partition ranges as
    /// <c>(StartIndex, Count)</c> pairs into the reordered <paramref name="indices"/>.
    /// </summary>
    public static async ValueTask<List<(int StartIndex, int Count)>> BuildPartitionsAsync(
        List<Row> rows,
        int[] indices,
        IReadOnlyList<Expression> partitionByExpressions,
        ExpressionEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        if (partitionByExpressions.Count == 1)
        {
            return await BuildSingleKeyAsync(rows, indices, partitionByExpressions[0], evaluator, cancellationToken)
                .ConfigureAwait(false);
        }

        return await BuildCompositeKeyAsync(rows, indices, partitionByExpressions, evaluator, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<List<(int StartIndex, int Count)>> BuildSingleKeyAsync(
        List<Row> rows,
        int[] indices,
        Expression partitionByExpression,
        ExpressionEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        Dictionary<DataValue, List<int>> groups = new();
        for (int i = 0; i < indices.Length; i++)
        {
            DataValue key = await evaluator
                .EvaluateAsync(partitionByExpression, rows[indices[i]], cancellationToken)
                .ConfigureAwait(false);
            if (!groups.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>();
                groups[key] = list;
            }
            list.Add(indices[i]);
        }

        return Flatten(indices, groups.Values);
    }

    private static async ValueTask<List<(int StartIndex, int Count)>> BuildCompositeKeyAsync(
        List<Row> rows,
        int[] indices,
        IReadOnlyList<Expression> partitionByExpressions,
        ExpressionEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        Dictionary<CompositeKey, List<int>> groups = new();
        for (int i = 0; i < indices.Length; i++)
        {
            DataValue[] parts = new DataValue[partitionByExpressions.Count];
            for (int j = 0; j < partitionByExpressions.Count; j++)
            {
                parts[j] = await evaluator
                    .EvaluateAsync(partitionByExpressions[j], rows[indices[i]], cancellationToken)
                    .ConfigureAwait(false);
            }
            CompositeKey key = new(parts);
            if (!groups.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>();
                groups[key] = list;
            }
            list.Add(indices[i]);
        }

        return Flatten(indices, groups.Values);
    }

    /// <summary>
    /// Writes group members back into <paramref name="indices"/> in group order,
    /// returning the (start, count) ranges. Group order matches dictionary
    /// enumeration order — first-seen-first-written, which is stable across runs
    /// for the same key sequence.
    /// </summary>
    private static List<(int StartIndex, int Count)> Flatten(
        int[] indices,
        IEnumerable<List<int>> groups)
    {
        List<(int, int)> ranges = new();
        int position = 0;
        foreach (List<int> group in groups)
        {
            for (int i = 0; i < group.Count; i++)
            {
                indices[position + i] = group[i];
            }
            ranges.Add((position, group.Count));
            position += group.Count;
        }
        return ranges;
    }
}
