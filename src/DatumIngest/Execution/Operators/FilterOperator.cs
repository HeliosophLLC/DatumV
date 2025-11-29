using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Filters rows from a child operator by evaluating a WHERE expression.
/// Only rows where the expression evaluates to true are emitted.
/// </summary>
public sealed class FilterOperator : IQueryOperator
{

    /// <summary>
    /// Creates a filter operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="predicate">The WHERE predicate expression.</param>
    public FilterOperator(IQueryOperator source, Expression predicate)
    {
        Source = source;
        Predicate = predicate;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source { get; }

    /// <summary>The filter predicate expression.</summary>
    public Expression Predicate { get; }

    /// <inheritdoc/>
    public IQueryOperator RewriteExpressions(Func<Expression, Expression> rewriter) =>
        new FilterOperator(Source.RewriteExpressions(rewriter), rewriter(Predicate));

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        List<string> warnings = [];

        if (QueryExplainer.ContainsPatternMatch(Predicate))
        {
            warnings.Add("LIKE/ILIKE pattern match — may scan all rows");
        }

        return new OperatorPlanDescription("Filter")
        {
            Properties = new Dictionary<string, string>
            {
                ["predicate"] = QueryExplainer.FormatExpression(Predicate),
            },
            Children = [(Source, null)],
            Warnings = warnings,
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context);
        Pool pool = context.Pool;
        // Invariant: outputBatch != null ⟺ producer still owns it. Yielding transfers
        // ownership, so we null the local *before* yield. The outer finally cleans up
        // only the not-yet-yielded leftover, closing the leak window for mid-fill
        // exceptions and upstream throws during the next MoveNextAsync. Post-yield
        // assignment alone wouldn't help — that statement runs on resumption, not on
        // iterator disposal.
        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch inputBatch in Source.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    // Source arena: where the row's non-inline values live (the input batch).
                    // Target arena: the long-lived context store, so literal materialisations
                    // and cache entries from predicate evaluation outlive this batch.
                    Arena sourceArena = inputBatch.Arena;
                    Arena targetArena = context.Store;

                    for (int index = 0, count = inputBatch.Count; index < count; index++)
                    {
                        Row row = inputBatch[index];
                        EvaluationFrame frame = new(row, sourceArena, targetArena, context.OuterRow, context.SidecarRegistry);

                        if (!evaluator.EvaluateAsBoolean(Predicate, frame)) continue;

                        outputBatch ??= context.RentRowBatch(inputBatch.ColumnLookup);

                        pool.RentAndCopyToOutput(inputBatch, index, outputBatch);

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
                    context.ReturnRowBatch(inputBatch);
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
                context.ReturnRowBatch(outputBatch);
            }
        }
    }
}
