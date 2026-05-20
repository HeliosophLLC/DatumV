using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators;

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
public sealed class SkipOperator : QueryOperator
{
    private readonly QueryOperator _child;
    private readonly long _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkipOperator"/> class.
    /// </summary>
    /// <param name="child">The child operator whose output to skip over.</param>
    /// <param name="count">The number of rows to skip.</param>
    public SkipOperator(QueryOperator child, long count)
    {
        _child = child;
        _count = count;
    }

    /// <inheritdoc />
    protected override OperatorPlanDescription DescribeForExplainImpl()
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
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        long skipped = 0;
        ColumnLookup? columnLookup = null;
        RowCopyOutputWriter writer = new(context);

        try
        {
            await foreach (RowBatch inputBatch in _child.ExecuteAsync(context).ConfigureAwait(false))
            {
                for (int i = 0; i < inputBatch.Count; i++)
                {
                    if (skipped < _count)
                    {
                        skipped++;
                        continue;
                    }

                    // Pin the output shape to the first post-skip batch's lookup so any
                    // mid-stream lookup-ref churn from the source doesn't trip the writer's
                    // shape-stability assertion (matches pre-migration semantics).
                    columnLookup ??= inputBatch.ColumnLookup;

                    RowBatch? full = writer.Add(columnLookup, inputBatch, i);
                    if (full is not null) yield return full;
                }

                context.ReturnRowBatch(inputBatch);
            }

            RowBatch? trailing = writer.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
        }
    }
}
