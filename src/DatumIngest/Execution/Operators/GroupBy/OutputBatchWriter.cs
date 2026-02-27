using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.GroupBy;

/// <summary>
/// Owns the output-side state of a GROUP BY pipeline: the lazily-built
/// <see cref="ColumnLookup"/> for output columns, the current in-progress
/// <see cref="RowBatch"/>, and the per-row emit logic that pulls aggregate
/// results from a <see cref="GroupState"/>. Consolidates the
/// <c>rent → add → IsFull → yield</c> cycle that the operator repeats at
/// each emit site.
/// </summary>
/// <remarks>
/// One writer per pipeline. The arena passed at construction is used both for
/// renting the output batch and must match the <see cref="InvocationFrame.Target"/>
/// supplied to <see cref="AddAsync"/> — accumulator results that need a
/// long-lived store land there.
/// </remarks>
internal sealed class OutputBatchWriter
{
    private readonly IReadOnlyList<Expression> _groupByExpressions;
    private readonly IReadOnlyList<AggregateColumn> _aggregateColumns;
    private readonly Pool _pool;
    private readonly int _batchSize;
    private readonly Arena _outputArena;

    private ColumnLookup? _outputLookup;
    private RowBatch? _current;

    public OutputBatchWriter(
        IReadOnlyList<Expression> groupByExpressions,
        IReadOnlyList<AggregateColumn> aggregateColumns,
        Pool pool,
        int batchSize,
        Arena outputArena)
    {
        _groupByExpressions = groupByExpressions;
        _aggregateColumns = aggregateColumns;
        _pool = pool;
        _batchSize = batchSize;
        _outputArena = outputArena;
    }

    /// <summary>
    /// Emits one group into the current output batch. When the batch fills,
    /// returns it (detached from the writer) so the caller can <c>yield return</c>
    /// it; otherwise returns <see langword="null"/>.
    /// </summary>
    public async ValueTask<RowBatch?> AddAsync(GroupState group, bool isGlobalAggregation, InvocationFrame frame)
    {
        Row emitted = await EmitGroupRowAsync(group, isGlobalAggregation, frame).ConfigureAwait(false);
        _current ??= _pool.RentRowBatch(_outputLookup!, _batchSize, _outputArena);
        _current.Add(emitted.RawValues);
        if (_current.IsFull)
        {
            RowBatch ready = _current;
            _current = null;
            return ready;
        }
        return null;
    }

    /// <summary>
    /// Detaches and returns the in-progress batch (if any). Call once after the
    /// last <see cref="AddAsync"/> to retrieve the trailing batch for yielding.
    /// Safe to call from a <c>finally</c> block to recover ownership on
    /// exception paths — returns <see langword="null"/> if already flushed.
    /// </summary>
    public RowBatch? Flush()
    {
        RowBatch? trailing = _current;
        _current = null;
        return trailing;
    }

    private async ValueTask<Row> EmitGroupRowAsync(GroupState group, bool isGlobalAggregation, InvocationFrame frame)
    {
        int outputFieldCount = _groupByExpressions.Count + _aggregateColumns.Count;

        if (_outputLookup is null)
        {
            string[] outputNames = new string[outputFieldCount];

            for (int index = 0; index < _groupByExpressions.Count; index++)
            {
                outputNames[index] = QueryExplainer.FormatExpression(_groupByExpressions[index]);
            }

            for (int index = 0; index < _aggregateColumns.Count; index++)
            {
                outputNames[_groupByExpressions.Count + index] = _aggregateColumns[index].OutputName;
            }

            _outputLookup = new ColumnLookup(outputNames);
        }

        DataValue[] values = _pool.RentDataValues(outputFieldCount);

        if (!isGlobalAggregation)
        {
            for (int index = 0; index < _groupByExpressions.Count; index++)
            {
                values[index] = group.KeyValues![index];
            }
        }

        for (int index = 0; index < _aggregateColumns.Count; index++)
        {
            values[_groupByExpressions.Count + index] = await group.Accumulators[index].ResultAsync(frame).ConfigureAwait(false);
        }

        return new Row(_outputLookup, values);
    }
}
