using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Takes a limited number of rows from a child operator, optionally
/// skipping an offset. Propagates cancellation upstream once the
/// limit is reached.
/// </summary>
public sealed class LimitOperator : IQueryOperator
{
    private readonly IQueryOperator _source;

    /// <summary>
    /// Creates a limit operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="limit">Maximum number of rows to emit.</param>
    /// <param name="offset">Number of rows to skip before emitting.</param>
    public LimitOperator(IQueryOperator source, int limit, int offset = 0)
    {
        _source = source;
        Limit = limit;
        Offset = offset;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>Maximum number of rows to emit.</summary>
    public int Limit { get; }

    /// <summary>Number of rows to skip before emitting.</summary>
    public int Offset { get; }

    /// <inheritdoc/>
    public IQueryOperator RewriteExpressions(Func<Expression, Expression> rewriter) =>
        new LimitOperator(_source.RewriteExpressions(rewriter), Limit, Offset);

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["limit"] = Limit.ToString(),
        };

        if (Offset > 0)
        {
            properties["offset"] = Offset.ToString();
        }

        return new OperatorPlanDescription("Limit")
        {
            Properties = properties,
            Children = [(Source, null)],
            EstimatedRows = Limit,
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        // Propagate the row limit hint so downstream operators (e.g. join,
        // model invocation) can choose cheaper strategies when only a small
        // result set is needed. Build the limited context FIRST, then alias
        // to it for the source dispatch — the previous shape captured the
        // alias from the unmodified context, so the new RowLimit never
        // reached upstream operators.
        ExecutionContext limitedContext = context;

        if (context.RowLimit is null || Limit + Offset < context.RowLimit)
        {
            limitedContext = new ExecutionContext(context)
            {
                OuterRow = context.OuterRow,
                MaxRecursionDepth = context.MaxRecursionDepth,
                RowLimit = Limit + Offset,
                DegreeOfParallelism = context.DegreeOfParallelism,
                ParallelismBudget = context.ParallelismBudget,
            };
        }

        int skipped = 0;
        int emitted = 0;
        // Invariant: outputBatch != null ⟺ producer still owns it. Yielding transfers
        // ownership, so we null the local *before* yield. The outer finally cleans up
        // only the not-yet-yielded leftover, closing the leak window for mid-fill
        // exceptions and upstream throws during the next MoveNextAsync.
        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(limitedContext).ConfigureAwait(false))
            {
                // Fast path 1: the entire batch lies inside the skip region — drop it.
                if (skipped + inputBatch.Count <= Offset)
                {
                    skipped += inputBatch.Count;
                    context.Pool.ReturnRowBatch(inputBatch);
                    continue;
                }

                // The skip region is behind us (fully or partially consumed by this batch).
                int startIndex = Offset - skipped;
                skipped = Offset;
                int take = Math.Min(inputBatch.Count - startIndex, Limit - emitted);

                // Fast path 2: no partial start, whole batch fits inside the remaining limit,
                // and nothing pending in outputBatch that we'd have to merge with — pass the
                // input batch straight through to the consumer without touching the arena.
                // The branch guard guarantees outputBatch is null, so no leak window here.
                if (outputBatch is null && startIndex == 0 && take == inputBatch.Count)
                {
                    emitted += take;
                    yield return inputBatch;

                    if (emitted >= Limit) yield break;
                    continue;
                }

                // Mixed path: copy the [startIndex, startIndex + take) slice into outputBatch,
                // stabilising each row into the output arena.
                try
                {
                    int end = startIndex + take;
                    for (int index = startIndex; index < end; index++)
                    {
                        outputBatch ??= context.RentRowBatch(inputBatch.ColumnLookup, context.BatchSize);

                        context.Pool.RentAndCopyToOutput(inputBatch, index, outputBatch);

                        emitted++;

                        if (outputBatch.IsFull)
                        {
                            RowBatch toYield = outputBatch;
                            outputBatch = null;
                            yield return toYield;
                        }
                    }
                }
                finally
                {
                    context.Pool.ReturnRowBatch(inputBatch);
                }

                if (emitted >= Limit)
                {
                    if (outputBatch is not null)
                    {
                        RowBatch toYield = outputBatch;
                        outputBatch = null;
                        yield return toYield;
                    }
                    yield break;
                }
            }

            if (outputBatch is not null)
            {
                RowBatch toYield = outputBatch;
                outputBatch = null;
                yield return toYield;
            }
        }
        finally
        {
            if (outputBatch is not null)
            {
                context.Pool.ReturnRowBatch(outputBatch);
            }
        }
    }
}
