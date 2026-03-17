using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Executes a table-valued function and streams the resulting rows.
/// Arguments are evaluated once at execution start, then passed to the function.
/// </summary>
public sealed class FunctionSourceOperator : QueryOperator
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
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        Dictionary<string, string> properties = new()
        {
            ["function"] = _function.Name,
        };

        if (_arguments.Count > 0)
        {
            properties["arguments"] = string.Join(", ",
                _arguments.Select(QueryExplainer.FormatExpression));
        }

        return new OperatorPlanDescription("Function Source")
        {
            Properties = properties,
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context);
        EvaluationFrame frame = new(Row.Empty, context.Store, context.Store, context.Accountant, context.OuterRow, context.SidecarRegistry, context.Types, context.TypeIdTranslations);

        ValueRef[] evaluatedArguments = new ValueRef[_arguments.Count];
        for (int index = 0; index < _arguments.Count; index++)
        {
            evaluatedArguments[index] = await evaluator.EvaluateAsValueRefAsync(_arguments[index], frame, context.CancellationToken).ConfigureAwait(false);
        }

        await foreach (RowBatch batch in _function.ExecuteAsync(evaluatedArguments, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }
}
