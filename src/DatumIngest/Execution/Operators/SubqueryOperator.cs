using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Wraps an inner <see cref="SelectStatement"/>'s operator tree, applying
/// the subquery alias to produce a derived table result.
/// </summary>
public sealed class SubqueryOperator : IQueryOperator
{
    private readonly IQueryOperator _innerOperator;
    private readonly string _alias;

    /// <summary>
    /// Creates a subquery operator that wraps the inner operator tree.
    /// </summary>
    /// <param name="innerOperator">The operator tree for the inner SELECT.</param>
    /// <param name="alias">The alias for this derived table.</param>
    public SubqueryOperator(IQueryOperator innerOperator, string alias)
    {
        _innerOperator = innerOperator;
        _alias = alias;
    }

    /// <summary>The inner operator tree.</summary>
    public IQueryOperator InnerOperator => _innerOperator;

    /// <summary>The derived table alias.</summary>
    public string Alias => _alias;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        return new OperatorPlanDescription("Subquery")
        {
            Properties = new Dictionary<string, string>
            {
                ["alias"] = _alias,
            },
            Children = [(InnerOperator, null)],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        // The inner operator already produces correctly-named rows.
        // Pass them through without copying.
        await foreach (RowBatch batch in _innerOperator.ExecuteAsync(context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }
}
