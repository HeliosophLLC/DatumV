using DatumQuery.Model;
using DatumQuery.Parsing.Ast;

namespace DatumQuery.Execution.Operators;

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
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry);

        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            if (evaluator.EvaluateAsBoolean(_predicate, row))
            {
                yield return row;
            }
        }
    }
}
