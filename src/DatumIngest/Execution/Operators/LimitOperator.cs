using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Takes a limited number of rows from a child operator, optionally
/// skipping an offset. Propagates cancellation upstream once the
/// limit is reached.
/// </summary>
public sealed class LimitOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly int _limit;
    private readonly int _offset;

    /// <summary>
    /// Creates a limit operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="limit">Maximum number of rows to emit.</param>
    /// <param name="offset">Number of rows to skip before emitting.</param>
    public LimitOperator(IQueryOperator source, int limit, int offset = 0)
    {
        _source = source;
        _limit = limit;
        _offset = offset;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>Maximum number of rows to emit.</summary>
    public int Limit => _limit;

    /// <summary>Number of rows to skip before emitting.</summary>
    public int Offset => _offset;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["limit"] = _limit.ToString(),
        };

        if (_offset > 0)
        {
            properties["offset"] = _offset.ToString();
        }

        return new OperatorPlanDescription("Limit")
        {
            Properties = properties,
            Children = [(Source, null)],
            EstimatedRows = _limit,
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        // Propagate the row limit hint so downstream operators (e.g. join) can
        // choose cheaper strategies when only a small result set is needed.
        ExecutionContext limitedContext = context.RowLimit is null || _limit + _offset < context.RowLimit
            ? new ExecutionContext(context)
              {
                  OuterRow = context.OuterRow,
                  MaxRecursionDepth = context.MaxRecursionDepth,
                  RowLimit = _limit + _offset,
                  DegreeOfParallelism = context.DegreeOfParallelism,
                  ParallelismBudget = context.ParallelismBudget,
              }
            : context;

        int skipped = 0;
        int emitted = 0;

        await foreach (Row row in _source.ExecuteAsync(limitedContext).ConfigureAwait(false))
        {
            if (skipped < _offset)
            {
                skipped++;
                continue;
            }

            if (emitted >= _limit)
            {
                yield break;
            }

            yield return row;
            emitted++;
        }
    }
}
