using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

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
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter);

        // Step 1: Materialize all source rows and track their original order.
        List<Row> allRows = new();
        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.QueryMeter?.ThrowIfExceeded();

            allRows.Add(row);
        }

        if (allRows.Count == 0)
        {
            yield break;
        }

        int inputFieldCount = allRows[0].FieldCount;
        int totalFieldCount = inputFieldCount + _windowColumns.Count;

        // Allocate result storage for each row — one DataValue per window column.
        // windowResults[rowIndex][windowColumnIndex]
        DataValue[][] windowResults = new DataValue[allRows.Count][];
        for (int i = 0; i < windowResults.Length; i++)
        {
            windowResults[i] = new DataValue[_windowColumns.Count];
        }

        // Step 2: Group window columns by their window specification to avoid
        // redundant partitioning and sorting for columns sharing the same spec.
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

        // Step 3: For each unique spec, partition + sort + compute.
        foreach (KeyValuePair<WindowSpecificationKey, List<int>> specGroup in specGroups)
        {
            WindowSpecification spec = _windowColumns[specGroup.Value[0]].WindowSpecification;
            ComputeForSpecification(spec, specGroup.Value, allRows, evaluator, windowResults);
        }

        // Step 4: Emit all rows in original order, augmented with window columns.
        string[]? outputNames = null;
        Dictionary<string, int>? outputNameIndex = null;

        for (int rowIndex = 0; rowIndex < allRows.Count; rowIndex++)
        {
            Row sourceRow = allRows[rowIndex];

            if (outputNames is null)
            {
                outputNames = new string[totalFieldCount];
                for (int field = 0; field < inputFieldCount; field++)
                {
                    outputNames[field] = sourceRow.ColumnNames[field];
                }
                for (int windowColumnIndex = 0; windowColumnIndex < _windowColumns.Count; windowColumnIndex++)
                {
                    outputNames[inputFieldCount + windowColumnIndex] = _windowColumns[windowColumnIndex].OutputName;
                }
                outputNameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < outputNames.Length; index++)
                {
                    outputNameIndex[outputNames[index]] = index;
                }
            }

            DataValue[] values = new DataValue[totalFieldCount];
            for (int field = 0; field < inputFieldCount; field++)
            {
                values[field] = sourceRow[field];
            }
            for (int windowColumnIndex = 0; windowColumnIndex < _windowColumns.Count; windowColumnIndex++)
            {
                values[inputFieldCount + windowColumnIndex] = windowResults[rowIndex][windowColumnIndex];
            }

            yield return new Row(outputNames, values, outputNameIndex!);
        }
    }

    /// <summary>
    /// Partitions, sorts, and computes window function results for all
    /// columns that share the given window specification.
    /// </summary>
    private void ComputeForSpecification(
        WindowSpecification spec,
        List<int> columnIndices,
        List<Row> allRows,
        ExpressionEvaluator evaluator,
        DataValue[][] windowResults)
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
            partitionRanges = BuildPartitions(allRows, originalIndices, spec.PartitionBy, evaluator);
        }

        // For each partition, sort by ORDER BY and compute all window columns.
        foreach ((int startIndex, int count) in partitionRanges)
        {
            // Sort partition rows by ORDER BY expressions (stable sort via index).
            if (spec.OrderBy is { Count: > 0 })
            {
                SortPartition(allRows, originalIndices, startIndex, count, spec.OrderBy, evaluator);
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
                computation.Compute(
                    partitionRows,
                    column.ArgumentExpressions,
                    evaluator,
                    spec.OrderBy,
                    spec.Frame,
                    partitionResults);

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
    private static List<(int StartIndex, int Count)> BuildPartitions(
        List<Row> rows,
        int[] indices,
        IReadOnlyList<Expression> partitionByExpressions,
        ExpressionEvaluator evaluator)
    {
        bool useSingleKey = partitionByExpressions.Count == 1;

        if (useSingleKey)
        {
            Dictionary<DataValue, List<int>> groups = new();
            for (int i = 0; i < indices.Length; i++)
            {
                DataValue key = evaluator.Evaluate(partitionByExpressions[0], rows[indices[i]]);
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
                    parts[j] = evaluator.Evaluate(partitionByExpressions[j], rows[indices[i]]);
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
    /// </summary>
    private static void SortPartition(
        List<Row> rows,
        int[] indices,
        int startIndex,
        int count,
        IReadOnlyList<OrderByItem> orderByItems,
        ExpressionEvaluator evaluator)
    {
        Array.Sort(indices, startIndex, count, Comparer<int>.Create((a, b) =>
        {
            Row rowA = rows[a];
            Row rowB = rows[b];
            foreach (OrderByItem item in orderByItems)
            {
                DataValue leftValue = evaluator.Evaluate(item.Expression, rowA);
                DataValue rightValue = evaluator.Evaluate(item.Expression, rowB);
                int comparison = CompareDataValues(leftValue, rightValue);
                if (item.Direction == SortDirection.Descending)
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

    private static int CompareDataValues(DataValue left, DataValue right)
    {
        if (left.IsNull && right.IsNull) return 0;
        if (left.IsNull) return 1;
        if (right.IsNull) return -1;

        return left.Kind switch
        {
            DataKind.Scalar => left.AsScalar().CompareTo(right.AsScalar()),
            DataKind.UInt8 => left.AsUInt8().CompareTo(right.AsUInt8()),
            DataKind.String => string.Compare(
                left.AsString(), right.AsString(), StringComparison.Ordinal),
            DataKind.Date => left.AsDate().CompareTo(right.AsDate()),
            DataKind.DateTime => left.AsDateTime().CompareTo(right.AsDateTime()),
            _ => 0,
        };
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
public sealed record WindowColumn(
    IWindowFunction Function,
    IReadOnlyList<Expression> ArgumentExpressions,
    WindowSpecification WindowSpecification,
    string OutputName);
