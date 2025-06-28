using Axon.QueryEngine.Model;
using Axon.QueryEngine.Parsing.Ast;

namespace Axon.QueryEngine.Execution.Operators;

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
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        await foreach (Row row in _innerOperator.ExecuteAsync(context).ConfigureAwait(false))
        {
            // Prefix column names with the alias for qualified access.
            string[] names = new string[row.FieldCount];
            DataValue[] values = new DataValue[row.FieldCount];

            for (int index = 0; index < row.FieldCount; index++)
            {
                names[index] = row.ColumnNames[index];
                values[index] = row[index];
            }

            yield return new Row(names, values);
        }
    }
}
