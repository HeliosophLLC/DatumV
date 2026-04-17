using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Filters rows from a child operator by evaluating a WHERE expression.
/// Only rows where the expression evaluates to true are emitted.
/// </summary>
public sealed class FilterOperator : QueryOperator
{

    /// <summary>
    /// Creates a filter operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="predicate">The WHERE predicate expression.</param>
    public FilterOperator(QueryOperator source, Expression predicate)
    {
        Source = source;
        Predicate = predicate;
    }

    /// <summary>The child operator producing rows.</summary>
    public QueryOperator Source { get; }

    /// <summary>The filter predicate expression.</summary>
    public Expression Predicate { get; }

    /// <inheritdoc/>
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter) =>
        new FilterOperator(Source.RewriteExpressions(rewriter), rewriter(Predicate));

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
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
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = context.CreateEvaluator();
        RowCopyOutputWriter writer = new(context);

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
                        EvaluationFrame frame = new(row, sourceArena, targetArena, context, context.OuterRow);

                        if (!await evaluator.EvaluateAsBooleanAsync(Predicate, frame, context.CancellationToken).ConfigureAwait(false)) continue;

                        RowBatch? full = writer.Add(inputBatch, index);
                        if (full is not null) yield return full;
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
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
