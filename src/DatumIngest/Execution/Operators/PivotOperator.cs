using System.Runtime.CompilerServices;
using DatumIngest.Diagnostics;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// A blocking operator that reshapes a tabular result by rotating distinct values of
/// a pivot column into new output columns, computing one aggregate per cell.
/// <para>
/// The operator consumes all input rows, then emits one output row per distinct
/// combination of non-pivot non-aggregate key columns. Each output row contains
/// the key column values followed by one column per (aggregate, pivot value) pair.
/// </para>
/// <para>
/// When the value list is omitted (auto-discover mode), distinct pivot values are
/// discovered at runtime. To prevent accidental schema explosion, the number of
/// distinct values is capped by <see cref="CardinalityCap"/>. Exceeding the cap
/// raises <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// When <see cref="ExecutionContext.MemoryBudgetBytes"/> is set, buffered input rows
/// spill to a temporary file once estimated memory usage exceeds the budget. Spilled
/// rows are read back sequentially during the aggregation pass.
/// </para>
/// </summary>
public sealed class PivotOperator : IQueryOperator, IDisposable
{
    /// <summary>
    /// Maximum number of distinct pivot values allowed when auto-discovering values.
    /// Queries that would produce more columns than this limit must use an explicit
    /// <c>IN (value1, value2, …)</c> list in the PIVOT clause.
    /// </summary>
    public const int CardinalityCap = 1000;

    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<AggregateColumn> _aggregateColumns;
    private readonly Expression _pivotColumnExpression;
    private readonly IReadOnlyList<DataValue>? _explicitValues;
    private string? _spillDirectory;

    /// <summary>Creates a PIVOT operator.</summary>
    /// <param name="source">The child operator producing pre-filtered rows.</param>
    /// <param name="aggregateColumns">
    /// The resolved aggregate functions, one per aggregate listed in the PIVOT clause.
    /// </param>
    /// <param name="pivotColumnExpression">
    /// Expression that identifies the pivot column whose distinct values become output columns.
    /// </param>
    /// <param name="explicitValues">
    /// The explicit value list from the <c>IN (…)</c> clause, or <see langword="null"/>
    /// to auto-discover distinct values (subject to <see cref="CardinalityCap"/>).
    /// </param>
    public PivotOperator(
        IQueryOperator source,
        IReadOnlyList<AggregateColumn> aggregateColumns,
        Expression pivotColumnExpression,
        IReadOnlyList<DataValue>? explicitValues)
    {
        _source = source;
        _aggregateColumns = aggregateColumns;
        _pivotColumnExpression = pivotColumnExpression;
        _explicitValues = explicitValues;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The resolved aggregate functions.</summary>
    public IReadOnlyList<AggregateColumn> AggregateColumns => _aggregateColumns;

    /// <summary>The expression that identifies the column being pivoted.</summary>
    public Expression PivotColumnExpression => _pivotColumnExpression;

    /// <summary>
    /// The explicit pivot value list, or <see langword="null"/> for auto-discover mode.
    /// </summary>
    public IReadOnlyList<DataValue>? ExplicitValues => _explicitValues;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["pivot column"] = QueryExplainer.FormatExpression(_pivotColumnExpression),
            ["aggregates"] = string.Join(", ",
                _aggregateColumns.Select(aggregate => $"{aggregate.Function.Name}() AS {aggregate.OutputName}")),
        };

        if (_explicitValues is not null)
        {
            properties["values"] = string.Join(", ",
                _explicitValues.Select(value => value.ToString()));
        }
        else
        {
            properties["values"] = "(auto-discover)";
        }

        return new OperatorPlanDescription("Pivot")
        {
            Properties = properties,
            Children = [(Source, null)],
            Warnings = ["materializes all rows to collect pivot values"],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);

        // Pass 1: buffer all input rows and collect distinct pivot values (if auto-discover).
        List<Row> bufferedRows = new();
        List<DataValue>? discoveredValues = _explicitValues is null ? new() : null;
        HashSet<DataValue>? discoveredValueSet = _explicitValues is null ? new() : null;

        // Spill state for row buffering.
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;
        BinaryWriter? spillWriter = null;
        string? spillPath = null;
        bool schemaWritten = false;
        bool spilling = false;

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                for (int batchIndex = 0; batchIndex < inputBatch.Count; batchIndex++)
                {
                Row row = inputBatch[batchIndex];
                context.CancellationToken.ThrowIfCancellationRequested();
                context.QueryMeter?.ThrowIfExceeded();

                if (discoveredValueSet is not null)
                {
                    DataValue pivotValue = evaluator.Evaluate(_pivotColumnExpression, row);

                    if (discoveredValueSet.Add(pivotValue))
                    {
                        if (discoveredValueSet.Count > CardinalityCap)
                        {
                            throw new InvalidOperationException(
                                $"PIVOT auto-discover exceeded the cardinality cap of {CardinalityCap} distinct values. " +
                                "Use an explicit IN (value1, value2, …) list to select the values to pivot.");
                        }

                        discoveredValues!.Add(pivotValue);
                    }
                }

                if (spilling)
                {
                    if (!schemaWritten)
                    {
                        RowSerializer.WriteSchema(spillWriter!, row);
                        schemaWritten = true;
                    }

                    RowSerializer.WriteRow(spillWriter!, row);
                }
                else
                {
                    bufferedRows.Add(row);

                    if (estimator is not null)
                    {
                        if (estimator.ShouldSample())
                        {
                            estimator.RecordSample(row);
                        }

                        estimator.IncrementRowCount();
                        long estimatedMemory = estimator.EstimateBytesForRowCount(bufferedRows.Count);

                        if (estimatedMemory > memoryBudget!.Value)
                        {
                            spilling = true;
                            _spillDirectory = Path.Combine(
                                Path.GetTempPath(), $"datum-pivot-{Guid.NewGuid():N}");
                            Directory.CreateDirectory(_spillDirectory);
                            spillPath = Path.Combine(_spillDirectory, "pivot_buffer.spill");
                            FileStream fileStream = new(spillPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                            spillWriter = new BinaryWriter(fileStream);

                            if (ExecutionTracer.IsEnabled)
                            {
                                ExecutionTracer.Write(
                                    $"PIVOT spill start  budget={ExecutionTracer.FormatBytes(memoryBudget.Value)}  estimated={ExecutionTracer.FormatBytes(estimatedMemory)}  rows={bufferedRows.Count}");
                            }
                        }
                        else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                        {
                            estimator.EscalateToEveryRow();
                        }
                    }
                }
            }

            inputBatch.Return();
            }

            // Close the spill writer before reading.
            spillWriter?.Flush();
            spillWriter?.Dispose();
            spillWriter = null;

            // Determine the ordered list of pivot values for output column layout.
            IReadOnlyList<DataValue> pivotValues = _explicitValues
                ?? (IReadOnlyList<DataValue>)discoveredValues!;

            if (pivotValues.Count == 0)
            {
                yield break;
            }

            // Build a stable index from pivot value to ordinal position.
            Dictionary<DataValue, int> pivotValueIndex = new(_explicitValues is not null
                ? _explicitValues.Count
                : discoveredValues!.Count);

            for (int index = 0; index < pivotValues.Count; index++)
            {
                pivotValueIndex.TryAdd(pivotValues[index], index);
            }

            // Determine the key columns from the first buffered row.
            if (bufferedRows.Count == 0 && spillPath is null)
            {
                yield break;
            }

            Row firstRow = bufferedRows[0];
            string pivotColumnName = GetPivotColumnName();
            List<string> keyColumnNames = new(firstRow.FieldCount);

            for (int fieldIndex = 0; fieldIndex < firstRow.FieldCount; fieldIndex++)
            {
                string name = firstRow.ColumnNames[fieldIndex];
                if (!string.Equals(name, pivotColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    keyColumnNames.Add(name);
                }
            }

            // Build the output column name schema.
            bool singleAggregate = _aggregateColumns.Count == 1;
            string[] outputNames = BuildOutputNames(pivotValues, singleAggregate, keyColumnNames);
            Dictionary<string, int> outputNameIndex = new(outputNames.Length, StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < outputNames.Length; index++)
            {
                outputNameIndex[outputNames[index]] = index;
            }

            int keyCount = keyColumnNames.Count;
            int pivotCellCount = pivotValues.Count * _aggregateColumns.Count;
            int totalOutputFields = keyCount + pivotCellCount;

            // Pass 2: group by key columns, accumulate aggregates per (pivot value, aggregate) cell.
            Dictionary<DataValue, PivotGroupState> singleKeyGroups = new();
            Dictionary<CompositeKey, PivotGroupState> compositeKeyGroups = new();
            PivotGroupState? globalGroup = keyColumnNames.Count == 0
                ? CreateGroupState(pivotValues.Count)
                : null;

            // Process in-memory rows.
            foreach (Row row in bufferedRows)
            {
                AccumulatePivotRow(row, evaluator, pivotValueIndex, keyColumnNames,
                    singleKeyGroups, compositeKeyGroups, globalGroup, pivotValues.Count);
            }

            // Process spilled rows.
            if (spillPath is not null)
            {
                using FileStream readStream = new(spillPath, FileMode.Open, FileAccess.Read, FileShare.None, 65536);
                using BinaryReader reader = new(readStream);

                RowSerializer.ReadSchema(reader, out string[] schemaNames, out Dictionary<string, int> schemaNameIndex);

                while (readStream.Position < readStream.Length)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    Row row = RowSerializer.ReadRow(reader, schemaNames, schemaNameIndex);

                    AccumulatePivotRow(row, evaluator, pivotValueIndex, keyColumnNames,
                        singleKeyGroups, compositeKeyGroups, globalGroup, pivotValues.Count);
                }
            }

            // Pass 3: emit one row per group.
            IEnumerable<PivotGroupState> allGroups = keyColumnNames.Count == 0
                ? globalGroup is null ? [] : [globalGroup]
                : keyColumnNames.Count == 1
                    ? singleKeyGroups.Values
                    : compositeKeyGroups.Values;

            LocalBufferPool pool = context.LocalBufferPool;
            RowBatch? outputBatch = null;

            foreach (PivotGroupState group in allGroups)
            {
                DataValue[] values = pool.RentOwned(totalOutputFields);

                if (group.KeyValues is not null)
                {
                    for (int keyIndex = 0; keyIndex < keyCount; keyIndex++)
                    {
                        values[keyIndex] = group.KeyValues[keyIndex];
                    }
                }

                for (int cellIndex = 0; cellIndex < pivotCellCount; cellIndex++)
                {
                    values[keyCount + cellIndex] = group.CellAccumulators[cellIndex].Result;
                }

                outputBatch ??= RowBatch.Rent(context.BatchSize);
                outputBatch.Add(new Row(outputNames, values, outputNameIndex));

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
        finally
        {
            spillWriter?.Dispose();
            CleanupSpillDirectory();
        }
    }

    /// <summary>
    /// Accumulates a single row's contribution to the pivot groups.
    /// </summary>
    private void AccumulatePivotRow(
        Row row,
        ExpressionEvaluator evaluator,
        Dictionary<DataValue, int> pivotValueIndex,
        List<string> keyColumnNames,
        Dictionary<DataValue, PivotGroupState> singleKeyGroups,
        Dictionary<CompositeKey, PivotGroupState> compositeKeyGroups,
        PivotGroupState? globalGroup,
        int pivotValueCount)
    {
        DataValue pivotValue = evaluator.Evaluate(_pivotColumnExpression, row);

        if (!pivotValueIndex.TryGetValue(pivotValue, out int pivotOrdinal))
        {
            return;
        }

        PivotGroupState group = ResolveGroup(
            row, keyColumnNames, evaluator, singleKeyGroups, compositeKeyGroups, globalGroup,
            pivotValueCount, out DataValue[]? keyValues);

        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];
            int cellIndex = (pivotOrdinal * _aggregateColumns.Count) + aggregateIndex;

            if (aggregateColumn.IsCountStar)
            {
                group.CellAccumulators[cellIndex].Accumulate(ReadOnlySpan<DataValue>.Empty);
            }
            else
            {
                DataValue[] arguments = new DataValue[aggregateColumn.ArgumentExpressions.Count];
                for (int argIndex = 0; argIndex < aggregateColumn.ArgumentExpressions.Count; argIndex++)
                {
                    arguments[argIndex] = evaluator.Evaluate(aggregateColumn.ArgumentExpressions[argIndex], row);
                }

                group.CellAccumulators[cellIndex].Accumulate(arguments);
            }
        }

        if (group.KeyValues is null && keyValues is not null)
        {
            group.KeyValues = keyValues;
        }
    }

    private string GetPivotColumnName()
    {
        if (_pivotColumnExpression is ColumnReference columnRef)
        {
            return columnRef.ColumnName;
        }

        // Fallback: use expression text for non-simple-column expressions.
        return QueryExplainer.FormatExpression(_pivotColumnExpression);
    }

    private string[] BuildOutputNames(
        IReadOnlyList<DataValue> pivotValues,
        bool singleAggregate,
        List<string> keyColumnNames)
    {
        int keyCount = keyColumnNames.Count;
        int pivotCellCount = pivotValues.Count * _aggregateColumns.Count;
        string[] names = new string[keyCount + pivotCellCount];

        // Key column names occupy the first slots.
        for (int keyIndex = 0; keyIndex < keyCount; keyIndex++)
        {
            names[keyIndex] = keyColumnNames[keyIndex];
        }

        for (int pivotIndex = 0; pivotIndex < pivotValues.Count; pivotIndex++)
        {
            string valueLabel = pivotValues[pivotIndex].IsNull
                ? "null"
                : pivotValues[pivotIndex].ToString();

            for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
            {
                int cellIndex = (pivotIndex * _aggregateColumns.Count) + aggregateIndex;
                names[keyCount + cellIndex] = singleAggregate
                    ? valueLabel
                    : $"{valueLabel}_{_aggregateColumns[aggregateIndex].OutputName}";
            }
        }

        return names;
    }

    private PivotGroupState ResolveGroup(
        Row row,
        List<string> keyColumnNames,
        ExpressionEvaluator evaluator,
        Dictionary<DataValue, PivotGroupState> singleKeyGroups,
        Dictionary<CompositeKey, PivotGroupState> compositeKeyGroups,
        PivotGroupState? globalGroup,
        int pivotValueCount,
        out DataValue[]? keyValues)
    {
        if (keyColumnNames.Count == 0)
        {
            keyValues = null;
            return globalGroup!;
        }

        if (keyColumnNames.Count == 1)
        {
            DataValue key = row[keyColumnNames[0]];

            if (!singleKeyGroups.TryGetValue(key, out PivotGroupState? group))
            {
                group = CreateGroupState(pivotValueCount);
                singleKeyGroups[key] = group;
                keyValues = [key];
                return group;
            }

            keyValues = null;
            return group;
        }

        DataValue[] keyParts = new DataValue[keyColumnNames.Count];

        for (int index = 0; index < keyColumnNames.Count; index++)
        {
            keyParts[index] = row[keyColumnNames[index]];
        }

        CompositeKey compositeKey = new(keyParts);

        if (!compositeKeyGroups.TryGetValue(compositeKey, out PivotGroupState? compositeGroup))
        {
            compositeGroup = CreateGroupState(pivotValueCount);
            compositeKeyGroups[compositeKey] = compositeGroup;
            keyValues = keyParts;
            return compositeGroup;
        }

        keyValues = null;
        return compositeGroup;
    }

    private PivotGroupState CreateGroupState(int pivotValueCount)
    {
        int cellCount = pivotValueCount * _aggregateColumns.Count;
        IAggregateAccumulator[] accumulators = new IAggregateAccumulator[cellCount];

        for (int pivotIndex = 0; pivotIndex < pivotValueCount; pivotIndex++)
        {
            for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
            {
                int cellIndex = (pivotIndex * _aggregateColumns.Count) + aggregateIndex;
                accumulators[cellIndex] = _aggregateColumns[aggregateIndex].Function.CreateAccumulator();
            }
        }

        return new PivotGroupState { CellAccumulators = accumulators };
    }

    private void CleanupSpillDirectory()
    {
        if (_spillDirectory is not null && Directory.Exists(_spillDirectory))
        {
            try
            {
                Directory.Delete(_spillDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }

            _spillDirectory = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CleanupSpillDirectory();
    }

    /// <summary>
    /// Mutable state for a single row group during pivot accumulation.
    /// One <see cref="CellAccumulators"/> entry exists per (pivot value, aggregate) pair.
    /// </summary>
    private sealed class PivotGroupState
    {
        /// <summary>
        /// The key column values captured from the first row that maps to this group.
        /// Null for global (no-key) pivot groups.
        /// </summary>
        public DataValue[]? KeyValues;

        /// <summary>
        /// Flat array of accumulators indexed by
        /// <c>(pivotValueOrdinal * aggregateCount) + aggregateIndex</c>.
        /// </summary>
        public IAggregateAccumulator[] CellAccumulators = [];
    }
}
