using Heliosoph.DatumV.Execution.Operators.Pivot;
using Heliosoph.DatumV.Execution.Operators.Sets;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// Blocking operator that rotates a pivot column's distinct values into output
/// columns, computing one aggregate per (pivot value, aggregate) cell per group.
/// </summary>
/// <remarks>
/// <para>
/// One output row per distinct combination of non-pivot non-aggregate-arg
/// "key" columns. Each output row contains the key values followed by
/// <c>pivotValueCount × aggregateCount</c> cells in
/// <c>(pivotOrdinal * aggregateCount) + aggregateIndex</c> order.
/// </para>
/// <para>
/// Two modes:
/// <list type="bullet">
///   <item><description><b>Explicit IN list</b> — pivot values known at plan time;
///     output schema is fixed before any row is read.</description></item>
///   <item><description><b>Auto-discover</b> (<see cref="ExplicitValues"/> = <see langword="null"/>) —
///     distinct pivot values are discovered as rows stream in. The output schema is built
///     once the input is fully consumed. The number of distinct values is capped at
///     <see cref="CardinalityCap"/> to prevent schema explosion.</description></item>
/// </list>
/// </para>
/// <para>
/// When <see cref="ExecutionContext.MemoryBudgetBytes"/> is set, in-memory group
/// growth is bounded via hash-partitioned spill: once adding a new group would push
/// residency past the budget, subsequent new-key rows are routed to a
/// <see cref="PartitionedRowSpiller"/> (64 partitions) and drained one partition at a
/// time at emit. Pre-spill keys keep receiving updates from later rows; the drain
/// phase skips replayed rows whose key is already present in the main table so they
/// are not double-counted.
/// </para>
/// <para>
/// Pivot values and key values are stabilised into <see cref="ExecutionContext.Store"/>
/// so they outlive the input batches they were read from.
/// </para>
/// </remarks>
public sealed class PivotOperator : QueryOperator
{
    /// <summary>
    /// Maximum number of distinct pivot values allowed in auto-discover mode.
    /// Queries that would produce more columns than this must supply an explicit
    /// <c>IN (value1, value2, …)</c> list.
    /// </summary>
    public const int CardinalityCap = 1000;

    /// <summary>
    /// Number of hash partitions used when spill activates. Matches the convention
    /// used by GroupBy / set-operation spillers.
    /// </summary>
    private const int SpillPartitionCount = 64;

    /// <summary>
    /// Heuristic bytes-per-new-group used to drive the spill-trigger budget check.
    /// Covers a dictionary entry, the cell accumulator array header, and a few
    /// accumulator-slot bytes. Conservative enough that we activate spill before
    /// the actual cost is realised.
    /// </summary>
    private const long PerGroupBytesEstimate = 256;

    private readonly QueryOperator _source;
    private readonly IReadOnlyList<AggregateColumn> _aggregateColumns;
    private readonly Expression _pivotColumnExpression;
    private readonly IReadOnlyList<Expression>? _explicitValueExpressions;

    /// <summary>Creates a PIVOT operator.</summary>
    /// <param name="source">The child operator producing pre-filtered rows.</param>
    /// <param name="aggregateColumns">
    /// The resolved aggregate functions, one per aggregate listed in the PIVOT clause.
    /// Must contain at least one aggregate.
    /// </param>
    /// <param name="pivotColumnExpression">
    /// Expression that identifies the pivot column whose distinct values become output columns.
    /// </param>
    /// <param name="explicitValueExpressions">
    /// The expressions from the <c>IN (…)</c> clause, or <see langword="null"/> for auto-discover.
    /// Each expression must be constant-foldable (typically a literal) and is evaluated once
    /// against a synthetic row to materialise the corresponding <see cref="DataValue"/>.
    /// </param>
    public PivotOperator(
        QueryOperator source,
        IReadOnlyList<AggregateColumn> aggregateColumns,
        Expression pivotColumnExpression,
        IReadOnlyList<Expression>? explicitValueExpressions)
    {
        _source = source;
        _aggregateColumns = aggregateColumns;
        _pivotColumnExpression = pivotColumnExpression;
        _explicitValueExpressions = explicitValueExpressions;
    }

    /// <summary>The child operator producing rows.</summary>
    public QueryOperator Source => _source;

    /// <summary>The resolved aggregate functions.</summary>
    public IReadOnlyList<AggregateColumn> AggregateColumns => _aggregateColumns;

    /// <summary>The expression that identifies the column being pivoted.</summary>
    public Expression PivotColumnExpression => _pivotColumnExpression;

    /// <summary>
    /// The explicit pivot value expressions, or <see langword="null"/> for auto-discover.
    /// </summary>
    public IReadOnlyList<Expression>? ExplicitValues => _explicitValueExpressions;

    /// <inheritdoc/>
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter)
    {
        IReadOnlyList<AggregateColumn> rewrittenAggregates = _aggregateColumns
            .Select(ac => ac with { ArgumentExpressions = ac.ArgumentExpressions.Select(rewriter).ToList() })
            .ToList();
        IReadOnlyList<Expression>? rewrittenValues = _explicitValueExpressions?
            .Select(rewriter).ToList();

        return new PivotOperator(
            _source.RewriteExpressions(rewriter),
            rewrittenAggregates,
            rewriter(_pivotColumnExpression),
            rewrittenValues);
    }

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        Dictionary<string, string> properties = new()
        {
            ["pivot column"] = QueryExplainer.FormatExpression(_pivotColumnExpression),
            ["aggregates"] = string.Join(", ",
                _aggregateColumns.Select(a => $"{a.Function.Name}() AS {a.OutputName}")),
            ["values"] = _explicitValueExpressions is null
                ? "(auto-discover)"
                : string.Join(", ", _explicitValueExpressions.Select(QueryExplainer.FormatExpression)),
        };

        return new OperatorPlanDescription("Pivot")
        {
            Properties = properties,
            Children = [(Source, null)],
            Warnings = ["materialises all input groups before emitting"],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = context.CreateEvaluator();
        InvocationFrame frame = InvocationFrame.Symmetric(context.Store, context.SidecarRegistry, context.Types);

        // Pivot column name (used to identify which input columns are "keys").
        string pivotColumnName = _pivotColumnExpression is ColumnReference colRef
            ? colRef.ColumnName
            : QueryExplainer.FormatExpression(_pivotColumnExpression);

        // Aggregate argument column names — also excluded from keys, since they're consumed
        // by the aggregate rather than carried through as identity.
        HashSet<string> aggregateArgumentColumns = CollectAggregateArgumentColumns();

        // Pivot value index (built upfront for explicit-IN, grown as we see rows for auto-discover).
        List<DataValue> pivotValues = new();
        Dictionary<DataValue, int> pivotValueOrdinal = new();
        bool autoDiscover = _explicitValueExpressions is null;

        if (!autoDiscover)
        {
            // PIVOT value-list expressions are constant-foldable (literals in practice).
            // Evaluating against an empty synthetic row materialises the DataValue
            // without depending on any row-bound column reference.
            Row literalRow = new(new ColumnLookup(Array.Empty<string>()), Array.Empty<DataValue>());
            foreach (Expression valueExpression in _explicitValueExpressions!)
            {
                DataValue value = await evaluator.EvaluateAsync(
                    valueExpression, literalRow, context.CancellationToken).ConfigureAwait(false);
                DataValue stable = DataValueRetention.Stabilize(value, context.Store, context.Store);
                if (pivotValueOrdinal.TryAdd(stable, pivotValues.Count))
                {
                    pivotValues.Add(stable);
                }
            }
        }

        // Group state. Two dictionaries handle the common single-key and multi-key cases;
        // the global (no-key) case uses a single state.
        Dictionary<DataValue, PivotGroupState> singleKeyGroups = new();
        Dictionary<CompositeKey, PivotGroupState> compositeKeyGroups = new();
        PivotGroupState? globalGroup = null;

        // Key schema (resolved from the first row).
        int[]? keyOrdinals = null;
        string[]? keyColumnNames = null;

        // Spill machinery. Only enabled for explicit-IN (auto-discover with spill needs
        // a two-pass schema-discovery flow that isn't worth v1's complexity). Global
        // aggregation has only one group and never benefits from spill either.
        PartitionedRowSpiller? spiller = (context.MemoryBudgetBytes.HasValue && !autoDiscover)
            ? new PartitionedRowSpiller(context, SpillPartitionCount)
            : null;
        ColumnLookup? sourceSchema = null;
        long residentBytesNotified = 0;

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    sourceSchema ??= inputBatch.ColumnLookup;

                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        Row row = inputBatch[i];

                        if (keyOrdinals is null)
                        {
                            HashSet<string> exclusion = new(aggregateArgumentColumns, StringComparer.OrdinalIgnoreCase) { pivotColumnName };
                            (keyOrdinals, keyColumnNames) = KeyColumnResolver.Resolve(row, exclusion);

                            if (keyColumnNames.Length == 0)
                            {
                                globalGroup = new PivotGroupState();
                            }
                        }

                        // Resolve the row's group key. Pre-spill we may insert a new entry;
                        // post-spill we never insert new keys (those go to the spiller).
                        PivotGroupState? existing = TryGetExistingGroup(
                            row, keyOrdinals, inputBatch.Arena, context.Store,
                            singleKeyGroups, compositeKeyGroups, globalGroup,
                            out int keyHash, out DataValue[]? stabilisedKey);

                        if (existing is null && spiller is { IsActive: true })
                        {
                            spiller.Route(inputBatch, i, spiller.AssignPartition(keyHash));
                            continue;
                        }

                        // Pivot ordinal — for explicit-IN, returns false to skip rows whose
                        // pivot value isn't in the value list.
                        DataValue pivotRaw = await evaluator.EvaluateAsync(
                            _pivotColumnExpression, row, context.CancellationToken).ConfigureAwait(false);
                        DataValue pivotValue = DataValueRetention.Stabilize(pivotRaw, inputBatch.Arena, context.Store);

                        if (!TryResolvePivotOrdinal(pivotValue, autoDiscover, pivotValues, pivotValueOrdinal, out int pivotOrdinal))
                        {
                            continue;
                        }

                        PivotGroupState group;
                        if (existing is not null)
                        {
                            group = existing;
                        }
                        else
                        {
                            // Inserting a new group. If a budget is configured and this insert
                            // would push residency past it, switch to spill before the insert.
                            // Global aggregation (no keys) never spills — only one group exists.
                            if (spiller is not null
                                && !spiller.IsActive
                                && globalGroup is null
                                && context.Accountant.WouldExceedBudget(PerGroupBytesEstimate))
                            {
                                spiller.Activate(sourceSchema!);
                                spiller.Route(inputBatch, i, spiller.AssignPartition(keyHash));
                                continue;
                            }

                            group = InsertNewGroup(
                                stabilisedKey, keyOrdinals.Length,
                                singleKeyGroups, compositeKeyGroups, globalGroup);

                            if (spiller is not null && globalGroup is null)
                            {
                                context.Accountant.NotifyMaterialized(PerGroupBytesEstimate);
                                residentBytesNotified += PerGroupBytesEstimate;
                            }
                        }

                        AccumulateRow(group, pivotOrdinal, row, evaluator, in frame, context.CancellationToken);
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            // Nothing to emit if no rows arrived or no pivot values fired.
            if (pivotValues.Count == 0 || keyOrdinals is null)
            {
                yield break;
            }

            // Output schema is known once pivotValues is final. For explicit-IN it's final
            // before drain; for auto-discover spill is disabled so it's also final.
            ColumnLookup outputLookup = BuildOutputLookup(keyColumnNames!, pivotValues);
            int keyCount = keyColumnNames!.Length;
            int cellCount = pivotValues.Count * _aggregateColumns.Count;
            PivotOutputWriter writer = new(context, outputLookup, keyCount, cellCount);

            try
            {
                // Drain spill partitions FIRST so writer state is consistent across the run.
                if (spiller is { IsActive: true })
                {
                    spiller.FlushAllBuffers();

                    for (int partition = 0; partition < SpillPartitionCount; partition++)
                    {
                        if (spiller.RowsWrittenInPartition(partition) == 0) continue;

                        Dictionary<DataValue, PivotGroupState> partSingle = new();
                        Dictionary<CompositeKey, PivotGroupState> partComposite = new();

                        await foreach (RowBatch spillBatch in spiller.ReplayPartitionAsync(partition).ConfigureAwait(false))
                        {
                            try
                            {
                                for (int i = 0; i < spillBatch.Count; i++)
                                {
                                    context.CancellationToken.ThrowIfCancellationRequested();
                                    Row row = spillBatch[i];

                                    // Skip rows whose key is already in main — those rows finished
                                    // their accumulation on the in-memory path and would otherwise
                                    // be double-counted.
                                    PivotGroupState? mainHit = TryGetExistingGroup(
                                        row, keyOrdinals, spillBatch.Arena, context.Store,
                                        singleKeyGroups, compositeKeyGroups, globalGroup,
                                        out int _, out DataValue[]? partKey);
                                    if (mainHit is not null) continue;

                                    DataValue pivotRaw = await evaluator.EvaluateAsync(
                                        _pivotColumnExpression, row, context.CancellationToken).ConfigureAwait(false);
                                    DataValue pivotValue = DataValueRetention.Stabilize(
                                        pivotRaw, spillBatch.Arena, context.Store);

                                    if (!TryResolvePivotOrdinal(pivotValue, autoDiscover, pivotValues, pivotValueOrdinal, out int pivotOrdinal))
                                    {
                                        continue;
                                    }

                                    PivotGroupState partGroup = ResolveOrInsertPartitionGroup(
                                        partKey, keyOrdinals.Length, partSingle, partComposite);

                                    AccumulateRow(partGroup, pivotOrdinal, row, evaluator, in frame, context.CancellationToken);
                                }
                            }
                            finally
                            {
                                context.ReturnRowBatch(spillBatch);
                            }
                        }

                        IEnumerable<PivotGroupState> partGroups = keyOrdinals.Length == 1
                            ? partSingle.Values
                            : partComposite.Values;
                        foreach (PivotGroupState group in partGroups)
                        {
                            IAggregateAccumulator[] cells = group.GetAccumulatorsForEmit(
                                pivotValues.Count, _aggregateColumns, cellCount);
                            if (await writer.EmitAsync(group.KeyValues, cells, frame).ConfigureAwait(false) is RowBatch full)
                            {
                                yield return full;
                            }
                        }
                    }
                }

                // Main groups (always emitted, with or without spill).
                IEnumerable<PivotGroupState> mainGroups = keyCount == 0
                    ? globalGroup is null ? [] : [globalGroup]
                    : keyCount == 1
                        ? singleKeyGroups.Values
                        : compositeKeyGroups.Values;

                foreach (PivotGroupState group in mainGroups)
                {
                    IAggregateAccumulator[] cells = group.GetAccumulatorsForEmit(
                        pivotValues.Count, _aggregateColumns, cellCount);
                    if (await writer.EmitAsync(group.KeyValues, cells, frame).ConfigureAwait(false) is RowBatch full)
                    {
                        yield return full;
                    }
                }

                if (writer.Flush() is RowBatch trailing)
                {
                    yield return trailing;
                }
            }
            finally
            {
                if (writer.Flush() is RowBatch leftover)
                {
                    context.ReturnRowBatch(leftover);
                }
            }
        }
        finally
        {
            if (residentBytesNotified > 0)
            {
                context.Accountant.NotifyReleased(residentBytesNotified);
            }
            spiller?.Dispose();
        }
    }

    private HashSet<string> CollectAggregateArgumentColumns()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (AggregateColumn aggregate in _aggregateColumns)
        {
            foreach (Expression arg in aggregate.ArgumentExpressions)
            {
                if (arg is ColumnReference colRef)
                {
                    names.Add(colRef.ColumnName);
                }
            }
        }
        return names;
    }

    /// <summary>
    /// Returns the existing group for <paramref name="row"/>'s key or
    /// <see langword="null"/> when no group has been registered yet. Always emits
    /// the stabilised key (<paramref name="stabilisedKey"/>) and the partition-routing
    /// hash so the caller can either insert (in the in-memory phase) or route to spill.
    /// </summary>
    private static PivotGroupState? TryGetExistingGroup(
        Row row,
        int[] keyOrdinals,
        IValueStore sourceArena,
        IValueStore retentionStore,
        Dictionary<DataValue, PivotGroupState> singleKeyGroups,
        Dictionary<CompositeKey, PivotGroupState> compositeKeyGroups,
        PivotGroupState? globalGroup,
        out int keyHash,
        out DataValue[]? stabilisedKey)
    {
        if (keyOrdinals.Length == 0)
        {
            keyHash = 0;
            stabilisedKey = null;
            return globalGroup;
        }

        if (keyOrdinals.Length == 1)
        {
            DataValue key = DataValueRetention.Stabilize(
                row[keyOrdinals[0]], sourceArena, retentionStore);
            stabilisedKey = [key];
            keyHash = key.GetHashCode();
            return singleKeyGroups.TryGetValue(key, out PivotGroupState? group) ? group : null;
        }

        DataValue[] parts = new DataValue[keyOrdinals.Length];
        for (int i = 0; i < keyOrdinals.Length; i++)
        {
            parts[i] = DataValueRetention.Stabilize(
                row[keyOrdinals[i]], sourceArena, retentionStore);
        }
        stabilisedKey = parts;
        CompositeKey composite = new(parts);
        keyHash = composite.GetHashCode();
        return compositeKeyGroups.TryGetValue(composite, out PivotGroupState? compositeGroup) ? compositeGroup : null;
    }

    /// <summary>
    /// Inserts a fresh group keyed by <paramref name="stabilisedKey"/>. Caller has
    /// already established the key isn't present.
    /// </summary>
    private static PivotGroupState InsertNewGroup(
        DataValue[]? stabilisedKey,
        int keyCount,
        Dictionary<DataValue, PivotGroupState> singleKeyGroups,
        Dictionary<CompositeKey, PivotGroupState> compositeKeyGroups,
        PivotGroupState? globalGroup)
    {
        if (keyCount == 0)
        {
            return globalGroup!;
        }

        if (keyCount == 1)
        {
            PivotGroupState group = new() { KeyValues = stabilisedKey };
            singleKeyGroups[stabilisedKey![0]] = group;
            return group;
        }

        PivotGroupState compositeGroup = new() { KeyValues = stabilisedKey };
        compositeKeyGroups[new CompositeKey(stabilisedKey!)] = compositeGroup;
        return compositeGroup;
    }

    /// <summary>
    /// Drain-time variant: never falls back to a global group, always inserts
    /// fresh state. Returns the (possibly newly created) partition-local group.
    /// </summary>
    private static PivotGroupState ResolveOrInsertPartitionGroup(
        DataValue[]? stabilisedKey,
        int keyCount,
        Dictionary<DataValue, PivotGroupState> partSingle,
        Dictionary<CompositeKey, PivotGroupState> partComposite)
    {
        if (keyCount == 1)
        {
            DataValue key = stabilisedKey![0];
            if (!partSingle.TryGetValue(key, out PivotGroupState? group))
            {
                group = new PivotGroupState { KeyValues = stabilisedKey };
                partSingle[key] = group;
            }
            return group;
        }

        CompositeKey composite = new(stabilisedKey!);
        if (!partComposite.TryGetValue(composite, out PivotGroupState? compositeGroup))
        {
            compositeGroup = new PivotGroupState { KeyValues = stabilisedKey };
            partComposite[composite] = compositeGroup;
        }
        return compositeGroup;
    }

    /// <summary>
    /// Resolves a pivot value to its ordinal in the running value index. Returns
    /// <see langword="false"/> for explicit-IN rows whose value is not in the list
    /// (caller skips). For auto-discover, grows the value index and throws when
    /// <see cref="CardinalityCap"/> is exceeded.
    /// </summary>
    private static bool TryResolvePivotOrdinal(
        DataValue pivotValue,
        bool autoDiscover,
        List<DataValue> pivotValues,
        Dictionary<DataValue, int> pivotValueOrdinal,
        out int pivotOrdinal)
    {
        if (pivotValueOrdinal.TryGetValue(pivotValue, out pivotOrdinal))
        {
            return true;
        }

        if (!autoDiscover)
        {
            return false;
        }

        if (pivotValues.Count >= CardinalityCap)
        {
            throw new InvalidOperationException(
                $"PIVOT auto-discover exceeded the cardinality cap of {CardinalityCap} distinct values. "
                + "Use an explicit IN (value1, value2, …) list to select the values to pivot.");
        }

        pivotOrdinal = pivotValues.Count;
        pivotValueOrdinal[pivotValue] = pivotOrdinal;
        pivotValues.Add(pivotValue);
        return true;
    }

    private void AccumulateRow(
        PivotGroupState group,
        int pivotOrdinal,
        Row row,
        ExpressionEvaluator evaluator,
        in InvocationFrame frame,
        CancellationToken cancellationToken)
    {
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregate = _aggregateColumns[aggregateIndex];
            IAggregateAccumulator accumulator = group.GetOrCreateAccumulator(
                pivotOrdinal, aggregateIndex, _aggregateColumns);

            if (aggregate.IsCountStar)
            {
                accumulator.Accumulate(ReadOnlySpan<DataValue>.Empty, in frame);
                continue;
            }

            DataValue[] arguments = new DataValue[aggregate.ArgumentExpressions.Count];
            for (int argIndex = 0; argIndex < aggregate.ArgumentExpressions.Count; argIndex++)
            {
                // ExpressionEvaluator.EvaluateAsync is awaited synchronously here:
                // arg expressions in aggregates are scalar and the evaluator completes
                // synchronously for the column/literal/arithmetic shapes used in PIVOT.
                ValueTask<DataValue> task = evaluator.EvaluateAsync(
                    aggregate.ArgumentExpressions[argIndex], row, cancellationToken);
                arguments[argIndex] = task.IsCompletedSuccessfully
                    ? task.Result
                    : task.AsTask().GetAwaiter().GetResult();
            }
            accumulator.Accumulate(arguments, in frame);
        }
    }

    private ColumnLookup BuildOutputLookup(string[] keyColumnNames, List<DataValue> pivotValues)
    {
        bool singleAggregate = _aggregateColumns.Count == 1;
        int cellCount = pivotValues.Count * _aggregateColumns.Count;
        string[] names = new string[keyColumnNames.Length + cellCount];

        for (int k = 0; k < keyColumnNames.Length; k++)
        {
            names[k] = keyColumnNames[k];
        }

        for (int p = 0; p < pivotValues.Count; p++)
        {
            string label = pivotValues[p].IsNull ? "null" : pivotValues[p].ToString();
            for (int a = 0; a < _aggregateColumns.Count; a++)
            {
                int cellIndex = (p * _aggregateColumns.Count) + a;
                names[keyColumnNames.Length + cellIndex] = singleAggregate
                    ? label
                    : $"{label}_{_aggregateColumns[a].OutputName}";
            }
        }

        return new ColumnLookup(names);
    }

    /// <summary>
    /// Mutable state for a single output group. Cell accumulators are stored
    /// in a flat array indexed by <c>(pivotOrdinal * aggregateCount) + aggregateIndex</c>.
    /// Lazily expanded as new pivot values are discovered (auto-discover mode).
    /// </summary>
    private sealed class PivotGroupState
    {
        public DataValue[]? KeyValues;
        public IAggregateAccumulator[] CellAccumulators = [];

        /// <summary>
        /// Returns the accumulator for the (pivotOrdinal, aggregateIndex) cell,
        /// growing the backing array as new pivot ordinals appear.
        /// </summary>
        public IAggregateAccumulator GetOrCreateAccumulator(
            int pivotOrdinal,
            int aggregateIndex,
            IReadOnlyList<AggregateColumn> aggregateColumns)
        {
            int cellIndex = (pivotOrdinal * aggregateColumns.Count) + aggregateIndex;
            if (cellIndex >= CellAccumulators.Length)
            {
                int newSize = (pivotOrdinal + 1) * aggregateColumns.Count;
                IAggregateAccumulator[] grown = new IAggregateAccumulator[newSize];
                Array.Copy(CellAccumulators, grown, CellAccumulators.Length);
                CellAccumulators = grown;
            }

            return CellAccumulators[cellIndex]
                ??= aggregateColumns[aggregateIndex].Function.CreateAccumulator();
        }

        /// <summary>
        /// Returns the accumulator array sized to the final cell count. Empty cells
        /// (groups that never saw a particular pivot value) get a fresh empty
        /// accumulator whose <see cref="IAggregateAccumulator.ResultAsync"/> yields
        /// the aggregate's empty-group result (typically NULL).
        /// </summary>
        public IAggregateAccumulator[] GetAccumulatorsForEmit(
            int pivotValueCount,
            IReadOnlyList<AggregateColumn> aggregateColumns,
            int cellCount)
        {
            if (CellAccumulators.Length < cellCount)
            {
                IAggregateAccumulator[] grown = new IAggregateAccumulator[cellCount];
                Array.Copy(CellAccumulators, grown, CellAccumulators.Length);
                CellAccumulators = grown;
            }

            for (int c = 0; c < cellCount; c++)
            {
                CellAccumulators[c] ??= aggregateColumns[c % aggregateColumns.Count].Function.CreateAccumulator();
            }

            return CellAccumulators;
        }
    }
}
