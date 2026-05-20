using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators.GroupBy;

/// <summary>
/// Owns the output-side state of a GROUP BY pipeline: the lazily-built
/// <see cref="ColumnLookup"/> for output columns, the current in-progress
/// <see cref="RowBatch"/>, and the per-row emit logic that pulls aggregate
/// results from a <see cref="GroupState"/>. Consolidates the
/// <c>rent → add → IsFull → yield</c> cycle that the operator repeats at
/// each emit site.
/// </summary>
/// <remarks>
/// One writer per pipeline. Output batches are rented via
/// <see cref="ExecutionContext.RentRowBatch(ColumnLookup)"/>, which threads
/// <c>Types</c> + <c>TypeIdTranslations</c> onto the batch and backs it with
/// <see cref="ExecutionContext.Store"/> — the canonical per-query arena.
/// Caller-supplied <see cref="InvocationFrame.Target"/> in <see cref="AddAsync"/>
/// must match <see cref="ExecutionContext.Store"/> so emitted accumulator
/// values resolve correctly against the output batch's arena.
/// </remarks>
internal sealed class OutputBatchWriter : OutputBatchAccumulator
{
    private readonly IReadOnlyList<Expression> _groupByExpressions;
    private readonly IReadOnlyList<AggregateColumn> _aggregateColumns;

    private ColumnLookup? _outputLookup;

    public OutputBatchWriter(
        IReadOnlyList<Expression> groupByExpressions,
        IReadOnlyList<AggregateColumn> aggregateColumns,
        ExecutionContext context) : base(context)
    {
        _groupByExpressions = groupByExpressions;
        _aggregateColumns = aggregateColumns;
    }

    /// <summary>
    /// Emits one group into the current output batch. When the batch fills,
    /// returns it (detached from the writer) so the caller can <c>yield return</c>
    /// it; otherwise returns <see langword="null"/>.
    /// </summary>
    public async ValueTask<RowBatch?> AddAsync(GroupState group, bool isGlobalAggregation, InvocationFrame frame)
    {
        Row emitted = await EmitGroupRowAsync(group, isGlobalAggregation, frame).ConfigureAwait(false);
        RowBatch current = EnsureRentedAndGetCurrent(_outputLookup!);
        current.Add(emitted.RawValues);
        return TakeIfFull();
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

        DataValue[] values = Pool.RentDataValues(outputFieldCount);

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
