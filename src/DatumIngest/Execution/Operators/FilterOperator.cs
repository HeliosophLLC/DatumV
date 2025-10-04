using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Filters rows from a child operator by evaluating a WHERE expression.
/// Only rows where the expression evaluates to true are emitted.
/// </summary>
public sealed class FilterOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly Expression _predicate;

    /// <summary>
    /// Creates a filter operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="predicate">The WHERE predicate expression.</param>
    public FilterOperator(IQueryOperator source, Expression predicate)
    {
        _source = source;
        _predicate = predicate;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The filter predicate expression.</summary>
    public Expression Predicate => _predicate;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        List<string> warnings = [];

        if (QueryExplainer.ContainsPatternMatch(_predicate))
        {
            warnings.Add("LIKE/ILIKE pattern match — may scan all rows");
        }

        return new OperatorPlanDescription("Filter")
        {
            Properties = new Dictionary<string, string>
            {
                ["predicate"] = QueryExplainer.FormatExpression(_predicate),
            },
            Children = [(Source, null)],
            Warnings = warnings,
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);
        LocalBufferPool pool = context.LocalBufferPool;
        RowBatch? outputBatch = null;

        await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            for (int index = 0; index < inputBatch.Count; index++)
            {
                Row row = inputBatch[index];

                if (evaluator.EvaluateAsBoolean(_predicate, row))
                {
                    outputBatch ??= pool.RentBatch(context.BatchSize);
                    outputBatch.Add(row);

                    if (outputBatch.IsFull)
                    {
                        yield return outputBatch;
                        outputBatch = null;
                    }
                }
            }

            inputBatch.Return();
        }

        if (outputBatch is not null)
        {
            yield return outputBatch;
        }
    }
}
