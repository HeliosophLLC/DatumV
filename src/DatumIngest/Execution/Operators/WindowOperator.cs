using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Computes window function results over partitions of input rows.
/// This is a blocking operator: all input rows must be materialized
/// before any output can be emitted, because window functions require
/// visibility of the entire partition.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="GroupByOperator"/>, which collapses each group
/// into a single row, the window operator preserves every input row
/// and augments each one with the computed window function columns.
/// Rows are emitted in their original arrival order.
/// </para>
/// <para>
/// When multiple window columns share the same <see cref="WindowSpecification"/>
/// (same PARTITION BY, ORDER BY, and frame), they are computed in the same
/// partitioning/sorting pass to avoid redundant work.
/// </para>
/// </remarks>
public sealed class WindowOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<WindowColumn> _windowColumns;

    /// <summary>
    /// Creates a new window operator that computes the specified window
    /// function columns over rows from the source operator.
    /// </summary>
    /// <param name="source">The upstream operator providing input rows.</param>
    /// <param name="windowColumns">The window function columns to compute.</param>
    public WindowOperator(
        IQueryOperator source,
        IReadOnlyList<WindowColumn> windowColumns)
    {
        _source = source;
        _windowColumns = windowColumns;
    }

    /// <summary>The upstream source operator.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The window function columns being computed.</summary>
    public IReadOnlyList<WindowColumn> WindowColumns => _windowColumns;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        List<string> functionDescriptions = [];
        foreach (WindowColumn column in _windowColumns)
        {
            functionDescriptions.Add($"{column.Function.Name}() AS {column.OutputName}");
        }

        return new OperatorPlanDescription("Window")
        {
            Properties = new Dictionary<string, string>
            {
                ["functions"] = string.Join(", ", functionDescriptions),
            },
            Children = [(Source, null)],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        LocalBufferPool bufferPool = context.LocalBufferPool;
        ExpressionEvaluator evaluator = new(context);

        // Window is a blocking operator: every input row must be materialised
        // before any output emits, since window functions need partition-wide
        // visibility. Source rows are stabilised into operator-owned
        // DataValue[] rentals so input batches return to the pool immediately
        // (same-arena fast path under one-arena-per-query — just a fresh
        // DataValue[] rental, no payload copy). Released in the finally below.
        List<Row> allRows = new();
        ColumnLookup? sourceLookup = null;
        ColumnLookup? outputLookup = null;
        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (sourceLookup is null && inputBatch.Count > 0)
                    {
                        sourceLookup = inputBatch.ColumnLookup;
                    }

                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        context.QueryMeter?.ThrowIfExceeded();

                        Row sourceRow = inputBatch[i];
                        DataValue[] copy = pool.RentAndCopyDataValues(
                            sourceRow, inputBatch.Arena, context.Store);
                        allRows.Add(new Row(sourceRow.ColumnLookup, copy));
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            if (allRows.Count == 0)
            {
                yield break;
            }

            int inputFieldCount = allRows[0].FieldCount;
            int totalFieldCount = inputFieldCount + _windowColumns.Count;

            // Result slot for each row × each window column.
            DataValue[][] windowResults = new DataValue[allRows.Count][];
            for (int i = 0; i < windowResults.Length; i++)
            {
                windowResults[i] = new DataValue[_windowColumns.Count];
            }

            // Group window columns by spec — columns sharing the same
            // PARTITION BY / ORDER BY / Frame compute together.
            Dictionary<WindowSpecificationKey, List<int>> specGroups = new();
            for (int columnIndex = 0; columnIndex < _windowColumns.Count; columnIndex++)
            {
                WindowSpecificationKey key = new(_windowColumns[columnIndex].WindowSpecification);
                if (!specGroups.TryGetValue(key, out List<int>? indices))
                {
                    indices = new List<int>();
                    specGroups[key] = indices;
                }
                indices.Add(columnIndex);
            }

            foreach (KeyValuePair<WindowSpecificationKey, List<int>> specGroup in specGroups)
            {
                WindowSpecification spec = _windowColumns[specGroup.Value[0]].WindowSpecification;
                await ComputeForSpecificationAsync(
                    spec, specGroup.Value, allRows, evaluator, windowResults, context.QueryMeter,
                    context.Store, context.SidecarRegistry, context.CancellationToken).ConfigureAwait(false);
            }

            // Build the output ColumnLookup once — source columns followed by
            // each window column's OutputName. Reused across every output batch.
            string[] outputNames = new string[totalFieldCount];
            for (int field = 0; field < inputFieldCount; field++)
            {
                outputNames[field] = sourceLookup!.ColumnNames[field];
            }
            for (int windowColumnIndex = 0; windowColumnIndex < _windowColumns.Count; windowColumnIndex++)
            {
                outputNames[inputFieldCount + windowColumnIndex] =
                    _windowColumns[windowColumnIndex].OutputName;
            }
            outputLookup = new ColumnLookup(outputNames);

            // Emit rows in original input order, augmented with window columns.
            for (int rowIndex = 0; rowIndex < allRows.Count; rowIndex++)
            {
                Row sourceRow = allRows[rowIndex];

                DataValue[] values = bufferPool.Rent(totalFieldCount);
                for (int field = 0; field < inputFieldCount; field++)
                {
                    values[field] = sourceRow[field];
                }
                for (int windowColumnIndex = 0; windowColumnIndex < _windowColumns.Count; windowColumnIndex++)
                {
                    values[inputFieldCount + windowColumnIndex] = windowResults[rowIndex][windowColumnIndex];
                }

                outputBatch ??= context.RentRowBatch(outputLookup);
                outputBatch.Add(values);

                if (outputBatch.IsFull)
                {
                    RowBatch toYield = outputBatch;
                    outputBatch = null;
                    yield return toYield;
                }
            }

            if (outputBatch is not null)
            {
                RowBatch toYield = outputBatch;
                outputBatch = null;
                yield return toYield;
            }
        }
        finally
        {
            if (outputBatch is not null)
            {
                context.ReturnRowBatch(outputBatch);
            }

            // Release the stabilised source-row rentals.
            foreach (Row row in allRows)
            {
                pool.ReturnRow(row);
            }
        }
    }

    /// <summary>
    /// Partitions, sorts, and computes window function results for all
    /// columns that share the given window specification.
    /// </summary>
    private async ValueTask ComputeForSpecificationAsync(
        WindowSpecification spec,
        List<int> columnIndices,
        List<Row> allRows,
        ExpressionEvaluator evaluator,
        DataValue[][] windowResults,
        QueryMeter? meter,
        IValueStore store,
        SidecarRegistry? sidecarRegistry,
        CancellationToken cancellationToken)
    {
        // Build an index array to track original positions through partitioning.
        int[] originalIndices = new int[allRows.Count];
        for (int i = 0; i < originalIndices.Length; i++)
        {
            originalIndices[i] = i;
        }

        // Partition the rows by PARTITION BY expressions.
        List<(int StartIndex, int Count)> partitionRanges;
        if (spec.PartitionBy is null or { Count: 0 })
        {
            // No PARTITION BY: entire result set is one partition.
            partitionRanges = [(0, allRows.Count)];
        }
        else
        {
            // Hash-partition rows by PARTITION BY key values.
            partitionRanges = await BuildPartitionsAsync(
                allRows, originalIndices, spec.PartitionBy, evaluator, cancellationToken).ConfigureAwait(false);
        }

        // For each partition, sort by ORDER BY and compute all window columns.
        foreach ((int startIndex, int count) in partitionRanges)
        {
            // Sort partition rows by ORDER BY expressions (stable sort via index).
            if (spec.OrderBy is { Count: > 0 })
            {
                await SortPartitionAsync(
                    allRows, originalIndices, startIndex, count, spec.OrderBy, evaluator,
                    store, sidecarRegistry, cancellationToken).ConfigureAwait(false);
            }

            // Build partition row list for the computation interface.
            List<Row> partitionRows = new(count);
            for (int i = startIndex; i < startIndex + count; i++)
            {
                partitionRows.Add(allRows[originalIndices[i]]);
            }

            // Compute each window column for this partition.
            DataValue[] partitionResults = new DataValue[count];
            for (int colIdx = 0; colIdx < columnIndices.Count; colIdx++)
            {
                int windowColumnIndex = columnIndices[colIdx];
                WindowColumn column = _windowColumns[windowColumnIndex];

                IWindowComputation computation = column.Function.CreateComputation();
                Array.Clear(partitionResults);
                await computation.ComputeAsync(
                    partitionRows,
                    column.ArgumentExpressions,
                    evaluator,
                    spec.OrderBy,
                    spec.Frame,
                    partitionResults,
                    column.NullHandling,
                    column.FromLast,
                    cancellationToken).ConfigureAwait(false);
                meter?.Add((long)column.Function.QueryUnitCost * count);

                // Write results back to the correct original row positions.
                for (int i = 0; i < count; i++)
                {
                    int originalRowIndex = originalIndices[startIndex + i];
                    windowResults[originalRowIndex][windowColumnIndex] = partitionResults[i];
                }
            }
        }
    }

    /// <summary>
    /// Groups rows into partitions based on PARTITION BY key expressions.
    /// Returns contiguous ranges in the reordered <paramref name="indices"/> array.
    /// </summary>
    private static async ValueTask<List<(int StartIndex, int Count)>> BuildPartitionsAsync(
        List<Row> rows,
        int[] indices,
        IReadOnlyList<Expression> partitionByExpressions,
        ExpressionEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        bool useSingleKey = partitionByExpressions.Count == 1;

        if (useSingleKey)
        {
            Dictionary<DataValue, List<int>> groups = new();
            for (int i = 0; i < indices.Length; i++)
            {
                DataValue key = await evaluator.EvaluateAsync(partitionByExpressions[0], rows[indices[i]], cancellationToken).ConfigureAwait(false);
                if (!groups.TryGetValue(key, out List<int>? list))
                {
                    list = new List<int>();
                    groups[key] = list;
                }
                list.Add(indices[i]);
            }

            List<(int, int)> ranges = new(groups.Count);
            int position = 0;
            foreach (List<int> group in groups.Values)
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
        else
        {
            Dictionary<CompositeKey, List<int>> groups = new();
            for (int i = 0; i < indices.Length; i++)
            {
                DataValue[] parts = new DataValue[partitionByExpressions.Count];
                for (int j = 0; j < partitionByExpressions.Count; j++)
                {
                    parts[j] = await evaluator.EvaluateAsync(partitionByExpressions[j], rows[indices[i]], cancellationToken).ConfigureAwait(false);
                }
                CompositeKey key = new(parts);
                if (!groups.TryGetValue(key, out List<int>? list))
                {
                    list = new List<int>();
                    groups[key] = list;
                }
                list.Add(indices[i]);
            }

            List<(int, int)> ranges = new(groups.Count);
            int position = 0;
            foreach (List<int> group in groups.Values)
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

    /// <summary>
    /// Sorts a contiguous partition range within the <paramref name="indices"/> array
    /// by the given ORDER BY items using the same comparison logic as <see cref="OrderByOperator"/>.
    /// Pre-evaluates sort keys per row so the inner comparator stays synchronous
    /// (Array.Sort can't await).
    /// </summary>
    private static async ValueTask SortPartitionAsync(
        List<Row> rows,
        int[] indices,
        int startIndex,
        int count,
        IReadOnlyList<OrderByItem> orderByItems,
        ExpressionEvaluator evaluator,
        IValueStore store,
        SidecarRegistry? sidecarRegistry,
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
                keys[j] = await evaluator.EvaluateAsync(orderByItems[j].Expression, row, cancellationToken).ConfigureAwait(false);
            }
            sortKeys[i] = keys;
        }

        // Build a parallel index array (0..count-1) we sort, swapping sortKeys in lockstep.
        int[] localIndices = new int[count];
        for (int i = 0; i < count; i++) localIndices[i] = i;

        Array.Sort(localIndices, Comparer<int>.Create((a, b) =>
        {
            for (int k = 0; k < orderByItems.Count; k++)
            {
                DataValue leftValue = sortKeys[a][k];
                DataValue rightValue = sortKeys[b][k];
                int comparison = CompareDataValues(
                    leftValue, store, sidecarRegistry,
                    rightValue, store, sidecarRegistry);
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

        // Apply the sorted order back to `indices[startIndex .. startIndex+count]`.
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

    /// <summary>
    /// Structural equality key for <see cref="WindowSpecification"/> to group
    /// window columns that share identical partitioning, ordering, and framing.
    /// </summary>
    private sealed class WindowSpecificationKey : IEquatable<WindowSpecificationKey>
    {
        private readonly WindowSpecification _spec;
        private readonly int _hashCode;

        public WindowSpecificationKey(WindowSpecification spec)
        {
            _spec = spec;
            _hashCode = ComputeHash(spec);
        }

        public bool Equals(WindowSpecificationKey? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return SpecsEqual(_spec, other._spec);
        }

        public override bool Equals(object? other) => other is WindowSpecificationKey key && Equals(key);
        public override int GetHashCode() => _hashCode;

        private static int ComputeHash(WindowSpecification spec)
        {
            HashCode hash = new();
            if (spec.PartitionBy is not null)
            {
                foreach (Expression expression in spec.PartitionBy)
                {
                    hash.Add(expression);
                }
            }
            if (spec.OrderBy is not null)
            {
                foreach (OrderByItem item in spec.OrderBy)
                {
                    hash.Add(item);
                }
            }
            if (spec.Frame is not null)
            {
                hash.Add(spec.Frame);
            }
            return hash.ToHashCode();
        }

        private static bool SpecsEqual(WindowSpecification a, WindowSpecification b)
        {
            return Equals(a, b);
        }
    }
}

/// <summary>
/// Describes a window function column to compute: the function, its arguments,
/// the window specification (OVER clause), and the output column name.
/// </summary>
/// <param name="Function">The window function implementation.</param>
/// <param name="ArgumentExpressions">The argument expressions to evaluate per row.</param>
/// <param name="WindowSpecification">The OVER clause specification (PARTITION BY, ORDER BY, frame).</param>
/// <param name="OutputName">The name of the output column.</param>
/// <param name="NullHandling">RESPECT NULLS (default) or IGNORE NULLS modifier for value window functions.</param>
/// <param name="FromLast">When true, NTH_VALUE counts from the end of the frame instead of the beginning.</param>
public sealed record WindowColumn(
    IWindowFunction Function,
    IReadOnlyList<Expression> ArgumentExpressions,
    WindowSpecification WindowSpecification,
    string OutputName,
    NullHandling NullHandling = NullHandling.RespectNulls,
    bool FromLast = false);
