using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators.GroupBy;

/// <summary>
/// Owns the reusable scratch buffers used to evaluate aggregate function
/// arguments (and optional <c>ORDER BY</c> sort keys) for each input row, and
/// to accumulate the buffered values into a <see cref="GroupState"/>.
/// One binder per aggregation pipeline — never shared across workers.
/// </summary>
internal sealed class AggregateArgumentBinder
{
    private readonly IReadOnlyList<AggregateColumn> _aggregateColumns;
    private readonly DataValue[][] _arguments;
    private readonly DataValue[]?[]? _sortKeys;

    public AggregateArgumentBinder(IReadOnlyList<AggregateColumn> aggregateColumns)
    {
        _aggregateColumns = aggregateColumns;
        _arguments = new DataValue[aggregateColumns.Count][];
        DataValue[]?[]? sortKeys = null;

        for (int aggregateIndex = 0; aggregateIndex < aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregateColumn = aggregateColumns[aggregateIndex];

            if (aggregateColumn.IsCountStar)
            {
                _arguments[aggregateIndex] = [];
            }
            else
            {
                _arguments[aggregateIndex] = new DataValue[aggregateColumn.ArgumentExpressions.Count];

                if (aggregateColumn.OrderBy is not null)
                {
                    sortKeys ??= new DataValue[]?[aggregateColumns.Count];
                    sortKeys[aggregateIndex] = new DataValue[aggregateColumn.OrderBy.Count];
                }
            }
        }

        _sortKeys = sortKeys;
    }

    /// <summary>
    /// Per-aggregate argument scratch buffers. Indexed by aggregate position.
    /// Exposed for the spill staging path (read) and drain replay path (write),
    /// which fill or consume the buffers outside the normal evaluate/accumulate cycle.
    /// </summary>
    public DataValue[][] Arguments => _arguments;

    /// <summary>
    /// Per-aggregate sort-key scratch buffers, or <see langword="null"/> when no
    /// aggregate has an <c>ORDER BY</c> clause. Entries are non-null only for
    /// ordered aggregates; the rest are <see langword="null"/>.
    /// </summary>
    public DataValue[]?[]? SortKeys => _sortKeys;

    /// <summary>
    /// Evaluates every aggregate's argument expressions (and any sort-key
    /// expressions) for a single input row into the owned scratch buffers.
    /// </summary>
    public async ValueTask EvaluateAsync(
        ExpressionEvaluator evaluator,
        Row row,
        CancellationToken cancellationToken)
    {
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];

            if (aggregateColumn.IsCountStar)
            {
                continue;
            }

            DataValue[] arguments = _arguments[aggregateIndex];
            for (int argumentIndex = 0; argumentIndex < aggregateColumn.ArgumentExpressions.Count; argumentIndex++)
            {
                arguments[argumentIndex] = await evaluator.EvaluateAsync(
                    aggregateColumn.ArgumentExpressions[argumentIndex], row, cancellationToken).ConfigureAwait(false);
            }

            if (aggregateColumn.OrderBy is not null)
            {
                DataValue[] sortKeyBuffer = _sortKeys![aggregateIndex]!;
                for (int sortIndex = 0; sortIndex < aggregateColumn.OrderBy.Count; sortIndex++)
                {
                    sortKeyBuffer[sortIndex] = await evaluator.EvaluateAsync(
                        aggregateColumn.OrderBy[sortIndex].Expression, row, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Accumulates the currently-buffered arguments and sort keys into a group.
    /// Ordered aggregates append into the group's deferred buffer (sorted later
    /// at flush time); the rest accumulate directly and charge query units.
    /// </summary>
    public void AccumulateInto(GroupState group, ExecutionContext context, in InvocationFrame frame)
    {
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];

            if (aggregateColumn.OrderBy is not null && _sortKeys?[aggregateIndex] is DataValue[] sortKeys)
            {
                group.OrderedBuffers![aggregateIndex]!.Add(_arguments[aggregateIndex], sortKeys);
            }
            else
            {
                group.Accumulators[aggregateIndex].Accumulate(_arguments[aggregateIndex], in frame);
                context.QueryMeter?.Add(aggregateColumn.Function.QueryUnitCost);
            }
        }
    }
}
