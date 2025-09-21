using System.Linq;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Computes fold/prefix-scan results over ordered partitions. Each row's
/// output feeds back as the accumulator for the next row:
/// <c>output[i] = f(output[i-1], input[i])</c>.
/// This is a blocking operator: all input rows are materialized before
/// any output is emitted, because the fold is sequential within each partition.
/// </summary>
public sealed class FoldScanOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<FoldScanColumn> _scanColumns;

    /// <summary>
    /// Creates a new fold/scan operator that computes the specified scan
    /// columns over rows from the source operator.
    /// </summary>
    /// <param name="source">The upstream operator providing input rows.</param>
    /// <param name="scanColumns">The fold/scan columns to compute.</param>
    public FoldScanOperator(
        IQueryOperator source,
        IReadOnlyList<FoldScanColumn> scanColumns)
    {
        _source = source;
        _scanColumns = scanColumns;
    }

    /// <summary>The upstream source operator.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The fold/scan columns being computed.</summary>
    public IReadOnlyList<FoldScanColumn> ScanColumns => _scanColumns;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        List<string> descriptions = [];
        foreach (FoldScanColumn column in _scanColumns)
        {
            descriptions.Add(string.Join(", ", column.OutputNames));
        }

        return new OperatorPlanDescription("FoldScan")
        {
            Properties = new Dictionary<string, string>
            {
                ["columns"] = string.Join("; ", descriptions),
            },
            Children = [(Source, null)],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        // Step 1: Materialize all input rows.
        List<Row> allRows = [];
        await foreach (RowBatch batch in _source.ExecuteAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                allRows.Add(batch[i]);
            }
            batch.Return();
        }

        if (allRows.Count == 0) yield break;

        // Count total output columns across all scan clauses.
        int totalOutputColumns = 0;
        foreach (FoldScanColumn column in _scanColumns)
        {
            totalOutputColumns += column.OutputNames.Count;
        }

        int inputFieldCount = allRows[0].FieldCount;
        int totalFieldCount = inputFieldCount + totalOutputColumns;

        // scanResults[rowIndex][flatOutputIndex]
        DataValue[][] scanResults = new DataValue[allRows.Count][];
        for (int i = 0; i < scanResults.Length; i++)
        {
            scanResults[i] = new DataValue[totalOutputColumns];
        }

        // Step 2: Group scan columns by window specification to share
        // partitioning and sorting work.
        Dictionary<WindowSpecificationKey, List<int>> specGroups = new();
        for (int columnIndex = 0; columnIndex < _scanColumns.Count; columnIndex++)
        {
            WindowSpecificationKey key = new(_scanColumns[columnIndex].WindowSpecification);
            if (!specGroups.TryGetValue(key, out List<int>? indices))
            {
                indices = [];
                specGroups[key] = indices;
            }
            indices.Add(columnIndex);
        }

        // Step 3: For each unique spec, partition + sort + fold.
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter);
        foreach (KeyValuePair<WindowSpecificationKey, List<int>> specGroup in specGroups)
        {
            WindowSpecification spec = _scanColumns[specGroup.Value[0]].WindowSpecification;
            ComputeForSpecification(spec, specGroup.Value, allRows, evaluator, scanResults, context.QueryMeter);
        }

        // Step 4: Emit all rows in original order with scan output columns appended.
        string[]? outputNames = null;
        Dictionary<string, int>? outputNameIndex = null;
        LocalBufferPool pool = context.LocalBufferPool;
        RowBatch? outputBatch = null;

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
                int outputOffset = 0;
                foreach (FoldScanColumn column in _scanColumns)
                {
                    for (int j = 0; j < column.OutputNames.Count; j++)
                    {
                        outputNames[inputFieldCount + outputOffset + j] = column.OutputNames[j];
                    }
                    outputOffset += column.OutputNames.Count;
                }
                outputNameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < outputNames.Length; index++)
                {
                    outputNameIndex[outputNames[index]] = index;
                }
            }

            DataValue[] values = pool.RentOwned(totalFieldCount);
            for (int field = 0; field < inputFieldCount; field++)
            {
                values[field] = sourceRow[field];
            }
            for (int j = 0; j < totalOutputColumns; j++)
            {
                values[inputFieldCount + j] = scanResults[rowIndex][j];
            }

            outputBatch ??= RowBatch.Rent(context.BatchSize);
            outputBatch.Add(new Row(outputNames, values, outputNameIndex!));

            if (outputBatch.IsFull)
            {
                yield return outputBatch;
                outputBatch = null;
            }
        }

        if (outputBatch is not null)
        {
            yield return outputBatch;
        }
    }

    /// <summary>
    /// Partitions, sorts, and runs the fold for all scan columns that share
    /// the given window specification.
    /// </summary>
    private void ComputeForSpecification(
        WindowSpecification spec,
        List<int> columnIndices,
        List<Row> allRows,
        ExpressionEvaluator evaluator,
        DataValue[][] scanResults,
        QueryMeter? meter)
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
            partitionRanges = [(0, allRows.Count)];
        }
        else
        {
            partitionRanges = BuildPartitions(allRows, originalIndices, spec.PartitionBy, evaluator);
        }

        // For each partition, sort by ORDER BY and fold.
        foreach ((int startIndex, int count) in partitionRanges)
        {
            if (spec.OrderBy is { Count: > 0 })
            {
                SortPartition(allRows, originalIndices, startIndex, count, spec.OrderBy, evaluator);
            }

            // Fold each scan column group within this partition.
            foreach (int scanColumnIndex in columnIndices)
            {
                FoldScanColumn column = _scanColumns[scanColumnIndex];
                int outputOffset = GetOutputOffset(scanColumnIndex);

                FoldPartition(
                    column, allRows, originalIndices, startIndex, count,
                    outputOffset, evaluator, scanResults, meter);
            }
        }
    }

    /// <summary>
    /// Runs the fold for a single <see cref="FoldScanColumn"/> over a sorted partition.
    /// </summary>
    private static void FoldPartition(
        FoldScanColumn column,
        List<Row> allRows,
        int[] originalIndices,
        int startIndex,
        int count,
        int outputOffset,
        ExpressionEvaluator evaluator,
        DataValue[][] scanResults,
        QueryMeter? meter)
    {
        int accCount = column.AccumulatorNames.Count;

        // Build augmented row schema: [source_cols..., acc1, acc2, ..., __prev_col1, __prev_col2, ...]
        Row firstRow = allRows[originalIndices[startIndex]];
        int sourceFieldCount = firstRow.FieldCount;

        // Collect PREV column names from the column's PrevColumnNames.
        IReadOnlyList<string> prevColumnNames = column.PrevColumnNames;
        int augmentedFieldCount = sourceFieldCount + accCount + prevColumnNames.Count;

        string[] augmentedNames = new string[augmentedFieldCount];
        for (int i = 0; i < sourceFieldCount; i++)
        {
            augmentedNames[i] = firstRow.ColumnNames[i];
        }
        for (int i = 0; i < accCount; i++)
        {
            augmentedNames[sourceFieldCount + i] = column.AccumulatorNames[i];
        }
        for (int i = 0; i < prevColumnNames.Count; i++)
        {
            augmentedNames[sourceFieldCount + accCount + i] = prevColumnNames[i];
        }

        Dictionary<string, int> augmentedNameIndex = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < augmentedNames.Length; i++)
        {
            augmentedNameIndex[augmentedNames[i]] = i;
        }

        // Initialize accumulators from INIT expressions (evaluated against the first row).
        DataValue[] accumulatorValues = new DataValue[accCount];
        for (int i = 0; i < accCount; i++)
        {
            accumulatorValues[i] = evaluator.Evaluate(column.InitExpressions[i], firstRow);
        }

        DataValue[] augmentedValues = new DataValue[augmentedFieldCount];
        Row? previousSourceRow = null;

        for (int rowOffset = 0; rowOffset < count; rowOffset++)
        {
            int originalRowIndex = originalIndices[startIndex + rowOffset];
            Row sourceRow = allRows[originalRowIndex];

            // Build augmented row values.
            for (int f = 0; f < sourceFieldCount; f++)
            {
                augmentedValues[f] = sourceRow[f];
            }

            // Bind accumulators.
            for (int a = 0; a < accCount; a++)
            {
                augmentedValues[sourceFieldCount + a] = accumulatorValues[a];
            }

            // Bind PREV columns (NULL on first row).
            for (int p = 0; p < prevColumnNames.Count; p++)
            {
                if (previousSourceRow is not null)
                {
                    // Strip the "__prev_" prefix to get the original column name.
                    string originalColumnName = prevColumnNames[p]["__prev_".Length..];
                    augmentedValues[sourceFieldCount + accCount + p] =
                        previousSourceRow.Value.TryGetValue(originalColumnName, out DataValue prevVal)
                            ? prevVal
                            : DataValue.UnknownNull();
                }
                else
                {
                    augmentedValues[sourceFieldCount + accCount + p] = DataValue.UnknownNull();
                }
            }

            Row augmentedRow = new(augmentedNames, augmentedValues, augmentedNameIndex);

            // Evaluate body expressions → new accumulator values.
            for (int b = 0; b < accCount; b++)
            {
                accumulatorValues[b] = evaluator.Evaluate(column.BodyExpressions[b], augmentedRow);
            }

            // Store results at the original row position.
            for (int o = 0; o < accCount; o++)
            {
                scanResults[originalRowIndex][outputOffset + o] = accumulatorValues[o];
            }

            previousSourceRow = sourceRow;
        }

        meter?.Add((long)count * accCount);
    }

    /// <summary>
    /// Returns the flat output column offset for the scan column at the given index.
    /// </summary>
    private int GetOutputOffset(int scanColumnIndex)
    {
        int offset = 0;
        for (int i = 0; i < scanColumnIndex; i++)
        {
            offset += _scanColumns[i].OutputNames.Count;
        }
        return offset;
    }

    /// <summary>
    /// Groups rows into partitions based on PARTITION BY key expressions.
    /// </summary>
    private static List<(int StartIndex, int Count)> BuildPartitions(
        List<Row> rows,
        int[] indices,
        IReadOnlyList<Expression> partitionByExpressions,
        ExpressionEvaluator evaluator)
    {
        if (partitionByExpressions.Count == 1)
        {
            Dictionary<DataValue, List<int>> groups = new();
            for (int i = 0; i < indices.Length; i++)
            {
                DataValue key = evaluator.Evaluate(partitionByExpressions[0], rows[indices[i]]);
                if (!groups.TryGetValue(key, out List<int>? list))
                {
                    list = [];
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
                    list = [];
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
    /// Sorts a contiguous partition range by the given ORDER BY items.
    /// </summary>
    private static void SortPartition(
        List<Row> rows,
        int[] indices,
        int startIndex,
        int count,
        IReadOnlyList<OrderByItem> orderByItems,
        ExpressionEvaluator evaluator)
    {
        try
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
        catch (InvalidOperationException ex)
        {
            // Array.Sort wraps comparison exceptions as InvalidOperationException.
            // Unwrap and re-throw with diagnostic context about the SCAN ORDER BY.
            string orderByDesc = string.Join(", ", orderByItems.Select(o => o.Expression.ToString()));
            throw new InvalidOperationException(
                $"SCAN ORDER BY sort failed ({count} rows, ordering: {orderByDesc}). " +
                $"Inner cause: {ex.InnerException?.Message ?? ex.Message}",
                ex.InnerException ?? ex);
        }
    }

    private static int CompareDataValues(DataValue left, DataValue right)
    {
        if (left.IsNull && right.IsNull) return 0;
        if (left.IsNull) return 1;
        if (right.IsNull) return -1;

        return DataValueComparer.Compare(left, right);
    }

    /// <summary>
    /// Structural equality key for <see cref="WindowSpecification"/> to group
    /// scan columns that share identical partitioning and ordering.
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
            return hash.ToHashCode();
        }

        private static bool SpecsEqual(WindowSpecification a, WindowSpecification b)
        {
            return Equals(a, b);
        }
    }
}

/// <summary>
/// Describes a fold/scan column to compute: the accumulator names, body expressions,
/// init expressions, window specification, and output column names.
/// </summary>
/// <param name="AccumulatorNames">The accumulator variable names bound during fold evaluation.</param>
/// <param name="BodyExpressions">The fold body expressions (one per accumulator).</param>
/// <param name="InitExpressions">The seed values for each accumulator at partition start.</param>
/// <param name="WindowSpecification">The OVER clause specification (PARTITION BY, ORDER BY).</param>
/// <param name="OutputNames">The output column names (one per accumulator).</param>
/// <param name="PrevColumnNames">
/// The <c>__prev_</c>-prefixed column names referenced by PREV() calls in the body expressions,
/// populated during the planner rewrite phase.
/// </param>
public sealed record FoldScanColumn(
    IReadOnlyList<string> AccumulatorNames,
    IReadOnlyList<Expression> BodyExpressions,
    IReadOnlyList<Expression> InitExpressions,
    WindowSpecification WindowSpecification,
    IReadOnlyList<string> OutputNames,
    IReadOnlyList<string> PrevColumnNames);
