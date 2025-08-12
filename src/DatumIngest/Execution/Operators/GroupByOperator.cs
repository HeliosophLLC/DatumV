using System.Runtime.CompilerServices;
using DatumIngest.Diagnostics;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Hash-based aggregation operator that groups input rows by one or more
/// key expressions and computes aggregate function results per group.
/// This is a blocking operator: all input rows must be consumed before
/// any output rows are emitted.
/// <para>
/// When <see cref="ExecutionContext.MemoryBudgetBytes"/> is set, the operator
/// spills raw input rows to hash-partitioned disk files when estimated memory
/// usage exceeds the budget. Spilled partitions are re-aggregated independently
/// during the drain phase.
/// </para>
/// </summary>
public sealed class GroupByOperator : IQueryOperator, IDisposable
{
    /// <summary>Number of hash partitions used when spilling to disk.</summary>
    private const int SpillPartitionCount = 64;

    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<Expression> _groupByExpressions;
    private readonly IReadOnlyList<AggregateColumn> _aggregateColumns;
    private string? _spillDirectory;

    /// <summary>
    /// Creates a GROUP BY operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="groupByExpressions">
    /// The GROUP BY key expressions. May be empty for global aggregation
    /// (e.g. <c>SELECT COUNT(*) FROM t</c>).
    /// </param>
    /// <param name="aggregateColumns">
    /// The aggregate function calls with their output column names.
    /// </param>
    public GroupByOperator(
        IQueryOperator source,
        IReadOnlyList<Expression> groupByExpressions,
        IReadOnlyList<AggregateColumn> aggregateColumns)
    {
        _source = source;
        _groupByExpressions = groupByExpressions;
        _aggregateColumns = aggregateColumns;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The GROUP BY key expressions.</summary>
    public IReadOnlyList<Expression> GroupByExpressions => _groupByExpressions;

    /// <summary>The aggregate columns being computed.</summary>
    public IReadOnlyList<AggregateColumn> AggregateColumns => _aggregateColumns;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new();

        if (_groupByExpressions.Count > 0)
        {
            properties["keys"] = string.Join(", ",
                _groupByExpressions.Select(QueryExplainer.FormatExpression));
        }
        else
        {
            properties["keys"] = "(global)";
        }

        properties["aggregates"] = string.Join(", ",
            _aggregateColumns.Select(aggregate => $"{aggregate.Function.Name}() AS {aggregate.OutputName}"));

        return new OperatorPlanDescription("Group By")
        {
            Properties = properties,
            Children = [(Source, null)],
            Warnings = ["materializes all rows per group"],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);

        bool useSingleKey = _groupByExpressions.Count == 1;
        bool isGlobalAggregation = _groupByExpressions.Count == 0;

        // Each group maps to accumulators for all aggregate functions.
        Dictionary<DataValue, GroupState>? singleKeyGroups =
            useSingleKey ? new() : null;
        Dictionary<CompositeKey, GroupState>? compositeKeyGroups =
            !useSingleKey && !isGlobalAggregation ? new() : null;

        // For global aggregation (no GROUP BY), use a single group.
        GroupState? globalGroup = isGlobalAggregation ? CreateGroupState() : null;

        // Shared column schema for output rows (built on first output).
        string[]? outputNames = null;
        Dictionary<string, int>? outputNameIndex = null;

        // Spill state — lazily initialised when the budget is exceeded.
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue && !isGlobalAggregation
            ? new MemoryEstimator() : null;
        BinaryWriter?[]? spillWriters = null;
        bool[]? spillSchemaWritten = null;
        string[]? spillPaths = null;
        bool spilling = false;

        // The spill row schema: [group_key_0, ..., group_key_N, arg_0_0, ..., arg_M_K].
        // Built on the first row that triggers spill.
        int spillColumnCount = 0;
        string[]? spillSchemaNames = null;

        try
        {
            await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.QueryMeter?.ThrowIfExceeded();

                // Evaluate group keys.
                DataValue[]? keyValues = null;
                DataValue? singleKey = null;

                if (isGlobalAggregation)
                {
                    // No keys to evaluate.
                }
                else if (useSingleKey)
                {
                    singleKey = evaluator.Evaluate(_groupByExpressions[0], row);
                }
                else
                {
                    keyValues = new DataValue[_groupByExpressions.Count];
                    for (int index = 0; index < _groupByExpressions.Count; index++)
                    {
                        keyValues[index] = evaluator.Evaluate(_groupByExpressions[index], row);
                    }
                }

                // Evaluate aggregate arguments.
                DataValue[][] allArguments = new DataValue[_aggregateColumns.Count][];
                DataValue[]?[]? allSortKeys = null;

                for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
                {
                    AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];

                    if (aggregateColumn.IsCountStar)
                    {
                        allArguments[aggregateIndex] = [];
                    }
                    else
                    {
                        DataValue[] arguments = new DataValue[aggregateColumn.ArgumentExpressions.Count];
                        for (int argumentIndex = 0; argumentIndex < aggregateColumn.ArgumentExpressions.Count; argumentIndex++)
                        {
                            arguments[argumentIndex] = evaluator.Evaluate(
                                aggregateColumn.ArgumentExpressions[argumentIndex], row);
                        }

                        allArguments[aggregateIndex] = arguments;

                        if (aggregateColumn.OrderBy is not null)
                        {
                            allSortKeys ??= new DataValue[]?[_aggregateColumns.Count];
                            DataValue[] sortKeys = new DataValue[aggregateColumn.OrderBy.Count];
                            for (int sortIndex = 0; sortIndex < aggregateColumn.OrderBy.Count; sortIndex++)
                            {
                                sortKeys[sortIndex] = evaluator.Evaluate(
                                    aggregateColumn.OrderBy[sortIndex].Expression, row);
                            }

                            allSortKeys[aggregateIndex] = sortKeys;
                        }
                    }
                }

                if (spilling)
                {
                    // Already spilling — write to a partition file based on the group key hash.
                    int hashCode = useSingleKey
                        ? singleKey!.GetHashCode()
                        : new CompositeKey(keyValues!).GetHashCode();

                    WriteSpillRow(hashCode, useSingleKey ? [singleKey!] : keyValues!,
                        allArguments, allSortKeys, spillWriters!, spillSchemaWritten!, spillPaths!,
                        ref spillSchemaNames, ref spillColumnCount);

                    // Continue accumulating in-memory for groups that existed before
                    // spilling started. Their in-memory accumulators already hold
                    // pre-spill data, and this ensures they also see post-spill rows.
                    // During re-aggregation, spill rows for these keys are skipped.
                    GroupState? existingGroup = null;

                    if (useSingleKey)
                    {
                        singleKeyGroups!.TryGetValue(singleKey!, out existingGroup);
                    }
                    else
                    {
                        compositeKeyGroups!.TryGetValue(new CompositeKey(keyValues!), out existingGroup);
                    }

                    if (existingGroup is not null)
                    {
                        AccumulateRow(existingGroup, allArguments, allSortKeys, context);
                    }
                }
                else
                {
                    // Accumulate in memory.
                    GroupState group;

                    if (isGlobalAggregation)
                    {
                        group = globalGroup!;
                    }
                    else if (useSingleKey)
                    {
                        if (!singleKeyGroups!.TryGetValue(singleKey!, out group!))
                        {
                            group = CreateGroupState();
                            group.KeyValues = [singleKey!];
                            singleKeyGroups[singleKey!] = group;
                        }
                    }
                    else
                    {
                        CompositeKey compositeKey = new(keyValues!);

                        if (!compositeKeyGroups!.TryGetValue(compositeKey, out group!))
                        {
                            group = CreateGroupState();
                            group.KeyValues = keyValues;
                            compositeKeyGroups[compositeKey] = group;
                        }
                    }

                    AccumulateRow(group, allArguments, allSortKeys, context);

                    // Memory estimation.
                    if (estimator is not null)
                    {
                        if (estimator.ShouldSample())
                        {
                            estimator.RecordSample(row);
                        }

                        estimator.IncrementRowCount();
                        long groupCount = useSingleKey
                            ? singleKeyGroups!.Count
                            : compositeKeyGroups!.Count;
                        long estimatedMemory = estimator.EstimateBytesForRowCount(groupCount);

                        if (estimatedMemory > memoryBudget!.Value)
                        {
                            // Transition to spill mode.
                            spilling = true;
                            _spillDirectory = Path.Combine(
                                Path.GetTempPath(), $"datum-groupby-{Guid.NewGuid():N}");
                            Directory.CreateDirectory(_spillDirectory);
                            spillWriters = new BinaryWriter[SpillPartitionCount];
                            spillSchemaWritten = new bool[SpillPartitionCount];
                            spillPaths = new string[SpillPartitionCount];

                            if (ExecutionTracer.IsEnabled)
                            {
                                ExecutionTracer.Write(
                                    $"GROUP BY spill start  budget={ExecutionTracer.FormatBytes(memoryBudget.Value)}  estimated={ExecutionTracer.FormatBytes(estimatedMemory)}  groups={groupCount}");
                            }
                        }
                        else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                        {
                            estimator.EscalateToEveryRow();
                        }
                    }
                }
            }

            // Flush ordered buffers for in-memory groups.
            bool hasOrderedAggregates = _aggregateColumns.Any(c => c.OrderBy is not null);

            if (hasOrderedAggregates)
            {
                IEnumerable<GroupState> groupsToFlush = isGlobalAggregation
                    ? [globalGroup!]
                    : useSingleKey
                        ? singleKeyGroups!.Values
                        : compositeKeyGroups!.Values;

                FlushOrderedBuffers(groupsToFlush, context);
            }

            // Emit in-memory groups.
            IEnumerable<GroupState> allGroups = isGlobalAggregation
                ? [globalGroup!]
                : useSingleKey
                    ? singleKeyGroups!.Values
                    : compositeKeyGroups!.Values;

            foreach (GroupState group in allGroups)
            {
                yield return EmitGroupRow(group, isGlobalAggregation, ref outputNames, ref outputNameIndex);
            }

            // Drain phase: process spilled partitions.
            if (spilling)
            {
                FlushSpillWriters(spillWriters!);

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (spillPaths![partition] is null)
                    {
                        continue;
                    }

                    // Re-aggregate this partition's rows.
                    foreach (Row groupRow in ReaggregatePartition(
                        spillPaths[partition], useSingleKey,
                        singleKeyGroups, compositeKeyGroups, hasOrderedAggregates, context,
                        ref outputNames, ref outputNameIndex))
                    {
                        yield return groupRow;
                    }
                }
            }
        }
        finally
        {
            CleanupSpillFiles(spillWriters);
        }
    }

    private GroupState CreateGroupState()
    {
        IAggregateAccumulator[] accumulators = new IAggregateAccumulator[_aggregateColumns.Count];
        List<(DataValue[] Arguments, DataValue[] SortKeys)>?[]? orderedBuffers = null;

        for (int index = 0; index < _aggregateColumns.Count; index++)
        {
            AggregateColumn column = _aggregateColumns[index];
            IAggregateAccumulator accumulator = column.Function.CreateAccumulator();

            if (column.Distinct)
            {
                accumulator = new DistinctAccumulatorDecorator(
                    accumulator, column.ArgumentExpressions.Count);
            }

            accumulators[index] = accumulator;

            if (column.OrderBy is not null)
            {
                orderedBuffers ??= new List<(DataValue[], DataValue[])>?[_aggregateColumns.Count];
                orderedBuffers[index] = [];
            }
        }
        return new GroupState { Accumulators = accumulators, OrderedBuffers = orderedBuffers };
    }

    /// <summary>
    /// Accumulates one input row's aggregate arguments into the given group state.
    /// </summary>
    private void AccumulateRow(
        GroupState group,
        DataValue[][] allArguments,
        DataValue[]?[]? allSortKeys,
        ExecutionContext context)
    {
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];

            if (aggregateColumn.OrderBy is not null && allSortKeys?[aggregateIndex] is DataValue[] sortKeys)
            {
                group.OrderedBuffers![aggregateIndex]!.Add((allArguments[aggregateIndex], sortKeys));
            }
            else
            {
                group.Accumulators[aggregateIndex].Accumulate(allArguments[aggregateIndex]);
                context.QueryMeter?.Add(aggregateColumn.Function.QueryUnitCost);
            }
        }
    }

    /// <summary>
    /// Flushes ordered aggregate buffers by sorting and accumulating deferred rows.
    /// </summary>
    private void FlushOrderedBuffers(IEnumerable<GroupState> groups, ExecutionContext context)
    {
        foreach (GroupState groupState in groups)
        {
            for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
            {
                AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];
                if (aggregateColumn.OrderBy is null) continue;

                List<(DataValue[] Arguments, DataValue[] SortKeys)> buffer =
                    groupState.OrderedBuffers![aggregateIndex]!;

                IReadOnlyList<OrderByItem> orderByItems = aggregateColumn.OrderBy;

                buffer.Sort((a, b) =>
                {
                    for (int sortIndex = 0; sortIndex < orderByItems.Count; sortIndex++)
                    {
                        int comparison = OrderByOperator.CompareDataValues(
                            a.SortKeys[sortIndex], b.SortKeys[sortIndex]);

                        if (orderByItems[sortIndex].Direction == SortDirection.Descending)
                        {
                            comparison = -comparison;
                        }

                        if (comparison != 0) return comparison;
                    }
                    return 0;
                });

                foreach ((DataValue[] arguments, _) in buffer)
                {
                    groupState.Accumulators[aggregateIndex].Accumulate(arguments);
                    context.QueryMeter?.Add(aggregateColumn.Function.QueryUnitCost);
                }
            }
        }
    }

    /// <summary>
    /// Emits a single output row from a completed group state.
    /// </summary>
    private Row EmitGroupRow(
        GroupState group,
        bool isGlobalAggregation,
        ref string[]? outputNames,
        ref Dictionary<string, int>? outputNameIndex)
    {
        int outputFieldCount = _groupByExpressions.Count + _aggregateColumns.Count;
        DataValue[] values = new DataValue[outputFieldCount];

        if (outputNames is null)
        {
            outputNames = new string[outputFieldCount];

            for (int index = 0; index < _groupByExpressions.Count; index++)
            {
                outputNames[index] = QueryExplainer.FormatExpression(_groupByExpressions[index]);
            }

            for (int index = 0; index < _aggregateColumns.Count; index++)
            {
                outputNames[_groupByExpressions.Count + index] = _aggregateColumns[index].OutputName;
            }

            outputNameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < outputNames.Length; index++)
            {
                outputNameIndex[outputNames[index]] = index;
            }
        }

        if (!isGlobalAggregation)
        {
            for (int index = 0; index < _groupByExpressions.Count; index++)
            {
                values[index] = group.KeyValues![index];
            }
        }

        for (int index = 0; index < _aggregateColumns.Count; index++)
        {
            values[_groupByExpressions.Count + index] = group.Accumulators[index].Result;
        }

        return new Row(outputNames, values, outputNameIndex!);
    }

    // ---------------------------------------------------------------
    //  Spill-to-disk infrastructure
    // ---------------------------------------------------------------

    /// <summary>
    /// Writes a spill row containing group keys and all aggregate arguments.
    /// The schema is: key_0, ..., key_N, arg_0_0, ..., arg_M_K, [sort_0_0, ...].
    /// </summary>
    private void WriteSpillRow(
        int hashCode,
        DataValue[] keyValues,
        DataValue[][] allArguments,
        DataValue[]?[]? allSortKeys,
        BinaryWriter?[] writers,
        bool[] schemaWritten,
        string[] paths,
        ref string[]? spillSchemaNames,
        ref int spillColumnCount)
    {
        int partition = AssignPartition(hashCode);

        if (writers[partition] is null)
        {
            paths[partition] = Path.Combine(_spillDirectory!, $"groupby_{partition}.spill");
            FileStream fileStream = new(paths[partition], FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            writers[partition] = new BinaryWriter(fileStream);
        }

        // Build the schema once (all spill rows share the same layout).
        if (spillSchemaNames is null)
        {
            List<string> names = new();

            for (int index = 0; index < keyValues.Length; index++)
            {
                names.Add($"__key_{index}");
            }

            for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
            {
                for (int argIndex = 0; argIndex < allArguments[aggregateIndex].Length; argIndex++)
                {
                    names.Add($"__arg_{aggregateIndex}_{argIndex}");
                }

                if (allSortKeys?[aggregateIndex] is DataValue[] sortKeys)
                {
                    for (int sortIndex = 0; sortIndex < sortKeys.Length; sortIndex++)
                    {
                        names.Add($"__sort_{aggregateIndex}_{sortIndex}");
                    }
                }
            }

            spillSchemaNames = names.ToArray();
            spillColumnCount = names.Count;
        }

        // Build the flat values array.
        DataValue[] flatValues = new DataValue[spillColumnCount];
        int offset = 0;

        for (int index = 0; index < keyValues.Length; index++)
        {
            flatValues[offset++] = keyValues[index];
        }

        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            for (int argIndex = 0; argIndex < allArguments[aggregateIndex].Length; argIndex++)
            {
                flatValues[offset++] = allArguments[aggregateIndex][argIndex];
            }

            if (allSortKeys?[aggregateIndex] is DataValue[] sortKeys)
            {
                for (int sortIndex = 0; sortIndex < sortKeys.Length; sortIndex++)
                {
                    flatValues[offset++] = sortKeys[sortIndex];
                }
            }
        }

        Row spillRow = new(spillSchemaNames, flatValues);

        if (!schemaWritten[partition])
        {
            RowSerializer.WriteSchema(writers[partition]!, spillRow);
            schemaWritten[partition] = true;
        }

        RowSerializer.WriteRow(writers[partition]!, spillRow);
    }

    /// <summary>
    /// Reads back a spill partition and re-aggregates its rows, returning one output
    /// row per group. Groups that were already aggregated in memory are skipped.
    /// </summary>
    private List<Row> ReaggregatePartition(
        string path,
        bool useSingleKey,
        Dictionary<DataValue, GroupState>? inMemorySingleKeyGroups,
        Dictionary<CompositeKey, GroupState>? inMemoryCompositeKeyGroups,
        bool hasOrderedAggregates,
        ExecutionContext context,
        ref string[]? outputNames,
        ref Dictionary<string, int>? outputNameIndex)
    {
        int keyCount = _groupByExpressions.Count;
        Dictionary<DataValue, GroupState>? partitionSingleGroups =
            useSingleKey ? new() : null;
        Dictionary<CompositeKey, GroupState>? partitionCompositeGroups =
            !useSingleKey ? new() : null;

        using FileStream fileStream = new(path, FileMode.Open, FileAccess.Read, FileShare.None, 65536);
        using BinaryReader reader = new(fileStream);

        RowSerializer.ReadSchema(reader, out string[] schemaNames, out Dictionary<string, int> schemaNameIndex);

        while (fileStream.Position < fileStream.Length)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            Row spillRow = RowSerializer.ReadRow(reader, schemaNames, schemaNameIndex);

            // Extract group keys from the spill row.
            DataValue[] keyValues = new DataValue[keyCount];
            for (int index = 0; index < keyCount; index++)
            {
                keyValues[index] = spillRow[index];
            }

            // Skip rows whose group was already aggregated in memory.
            if (useSingleKey)
            {
                if (inMemorySingleKeyGroups!.ContainsKey(keyValues[0]))
                {
                    continue;
                }
            }
            else
            {
                CompositeKey compositeKey = new(keyValues);
                if (inMemoryCompositeKeyGroups!.ContainsKey(compositeKey))
                {
                    continue;
                }
            }

            // Resolve or create the group for this partition.
            GroupState group;

            if (useSingleKey)
            {
                if (!partitionSingleGroups!.TryGetValue(keyValues[0], out group!))
                {
                    group = CreateGroupState();
                    group.KeyValues = keyValues;
                    partitionSingleGroups[keyValues[0]] = group;
                }
            }
            else
            {
                CompositeKey compositeKey = new(keyValues);

                if (!partitionCompositeGroups!.TryGetValue(compositeKey, out group!))
                {
                    group = CreateGroupState();
                    group.KeyValues = keyValues;
                    partitionCompositeGroups[compositeKey] = group;
                }
            }

            // Extract aggregate arguments from the spill row and accumulate.
            int offset = keyCount;

            for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
            {
                AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];

                if (aggregateColumn.IsCountStar)
                {
                    group.Accumulators[aggregateIndex].Accumulate(ReadOnlySpan<DataValue>.Empty);
                }
                else
                {
                    int argCount = aggregateColumn.ArgumentExpressions.Count;
                    DataValue[] arguments = new DataValue[argCount];
                    for (int argIndex = 0; argIndex < argCount; argIndex++)
                    {
                        arguments[argIndex] = spillRow[offset++];
                    }

                    if (aggregateColumn.OrderBy is not null)
                    {
                        int sortCount = aggregateColumn.OrderBy.Count;
                        DataValue[] sortKeys = new DataValue[sortCount];
                        for (int sortIndex = 0; sortIndex < sortCount; sortIndex++)
                        {
                            sortKeys[sortIndex] = spillRow[offset++];
                        }

                        group.OrderedBuffers![aggregateIndex]!.Add((arguments, sortKeys));
                    }
                    else
                    {
                        group.Accumulators[aggregateIndex].Accumulate(arguments);
                    }
                }
            }
        }

        // Flush ordered buffers for this partition.
        if (hasOrderedAggregates)
        {
            IEnumerable<GroupState> partitionGroups = useSingleKey
                ? partitionSingleGroups!.Values
                : partitionCompositeGroups!.Values;

            FlushOrderedBuffers(partitionGroups, context);
        }

        // Emit one row per group in this partition.
        IEnumerable<GroupState> allPartitionGroups = useSingleKey
            ? partitionSingleGroups!.Values
            : partitionCompositeGroups!.Values;

        List<Row> results = new();
        foreach (GroupState group in allPartitionGroups)
        {
            results.Add(EmitGroupRow(group, isGlobalAggregation: false, ref outputNames, ref outputNameIndex));
        }

        return results;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AssignPartition(int hashCode)
    {
        return (int)((uint)hashCode % SpillPartitionCount);
    }

    private static void FlushSpillWriters(BinaryWriter?[] writers)
    {
        for (int index = 0; index < writers.Length; index++)
        {
            if (writers[index] is not null)
            {
                writers[index]!.Flush();
                writers[index]!.Dispose();
                writers[index] = null;
            }
        }
    }

    private void CleanupSpillFiles(BinaryWriter?[]? writers)
    {
        if (writers is not null)
        {
            for (int index = 0; index < writers.Length; index++)
            {
                try
                {
                    writers[index]?.Dispose();
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }

        CleanupSpillDirectory();
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
    /// Mutable state for a single group during hash aggregation.
    /// </summary>
    private sealed class GroupState
    {
        /// <summary>The GROUP BY key values for this group (null for global aggregation).</summary>
        public DataValue[]? KeyValues;

        /// <summary>One accumulator per aggregate column.</summary>
        public IAggregateAccumulator[] Accumulators = [];

        /// <summary>
        /// Buffered rows for aggregates with intra-aggregate ORDER BY. Null when
        /// no aggregates in the query use ORDER BY. Each element is either null
        /// (aggregate has no ORDER BY) or a list of (arguments, sort-keys) tuples.
        /// </summary>
        public List<(DataValue[] Arguments, DataValue[] SortKeys)>?[]? OrderedBuffers;
    }
}

/// <summary>
/// Describes a single aggregate function call in a GROUP BY query,
/// including the function, its argument expressions, and the output column name.
/// </summary>
/// <param name="Function">The aggregate function implementation.</param>
/// <param name="ArgumentExpressions">
/// The expressions to evaluate per row as arguments to the aggregate.
/// Empty for <c>COUNT(*)</c>.
/// </param>
/// <param name="OutputName">The output column name (e.g. <c>COUNT(*)</c>, <c>SUM(price)</c>).</param>
/// <param name="IsCountStar">Whether this is a <c>COUNT(*)</c> invocation with no arguments.</param>
/// <param name="Distinct">Whether the aggregate uses <c>DISTINCT</c> to deduplicate values before accumulation.</param>
/// <param name="OrderBy">
/// Optional intra-aggregate ORDER BY items for functions like
/// <c>STRING_AGG(expr, separator ORDER BY expr ASC)</c>. When non-null,
/// accumulated rows are sorted before being fed to the accumulator.
/// </param>
public sealed record AggregateColumn(
    IAggregateFunction Function,
    IReadOnlyList<Expression> ArgumentExpressions,
    string OutputName,
    bool IsCountStar = false,
    bool Distinct = false,
    IReadOnlyList<OrderByItem>? OrderBy = null);
