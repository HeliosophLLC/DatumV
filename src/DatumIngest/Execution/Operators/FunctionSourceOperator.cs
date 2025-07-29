using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Executes a table-valued function and streams the resulting rows.
/// Arguments are evaluated once at execution start, then passed to the function.
/// </summary>
public sealed class FunctionSourceOperator : IQueryOperator
{
    private readonly ITableValuedFunction _function;
    private readonly IReadOnlyList<Expression> _arguments;

    /// <summary>
    /// Creates a function source operator.
    /// </summary>
    /// <param name="function">The table-valued function to invoke.</param>
    /// <param name="arguments">The argument expressions to evaluate.</param>
    public FunctionSourceOperator(ITableValuedFunction function, IReadOnlyList<Expression> arguments)
    {
        _function = function;
        _arguments = arguments;
    }

    /// <summary>The table-valued function this operator invokes.</summary>
    public ITableValuedFunction Function => _function;

    /// <summary>The argument expressions.</summary>
    public IReadOnlyList<Expression> Arguments => _arguments;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);
        Row emptyRow = new([], []);

        DataValue[] evaluatedArguments = new DataValue[_arguments.Count];
        for (int index = 0; index < _arguments.Count; index++)
        {
            evaluatedArguments[index] = evaluator.Evaluate(_arguments[index], emptyRow);
        }

        await foreach (Row row in _function.ExecuteAsync(
            evaluatedArguments, context.CancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }
}
