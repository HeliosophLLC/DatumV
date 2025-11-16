using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Wraps a child operator to skip the first N rows, then yields the rest.
/// Used for checkpoint-based resume to fast-forward through already-processed rows.
/// </summary>
/// <remarks>
/// This operator assumes the underlying query produces rows in a deterministic,
/// stable order between runs. Non-deterministic queries (e.g. ORDER BY RANDOM()
/// or sources with undefined row ordering) will produce incorrect results on resume.
/// Ensuring deterministic ordering is the user's responsibility.
/// </remarks>
public sealed class SkipOperator : IQueryOperator
{
    private readonly IQueryOperator _child;
    private readonly long _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkipOperator"/> class.
    /// </summary>
    /// <param name="child">The child operator whose output to skip over.</param>
    /// <param name="count">The number of rows to skip.</param>
    public SkipOperator(IQueryOperator child, long count)
    {
        _child = child;
        _count = count;
    }

    /// <inheritdoc />
    public OperatorPlanDescription DescribeForExplain()
    {
        return new OperatorPlanDescription("Skip")
        {
            Properties = new Dictionary<string, string>
            {
                ["count"] = _count.ToString(),
            },
            Children = [(_child, null)],
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        long skipped = 0;
        RowBatch? outputBatch = null;

        await foreach (RowBatch inputBatch in _child.ExecuteAsync(context).ConfigureAwait(false))
        {
            for (int i = 0; i < inputBatch.Count; i++)
            {
                Row row = inputBatch[i];

                if (skipped < _count)
                {
                    skipped++;
                    continue;
                }

                outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                outputBatch.Add(row);

                if (outputBatch.IsFull)
                {
                    yield return outputBatch;
                    outputBatch = null;
                }
            }

            context.Pool.ReturnRowBatch(inputBatch);
        }

        if (outputBatch is not null)
        {
            yield return outputBatch;
        }
    }
}
