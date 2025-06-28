using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Execution.Operators;

/// <summary>
/// Prefixes all column names in the incoming rows with a table alias,
/// enabling qualified column references (e.g. <c>t.column_name</c>).
/// Retains the original unqualified names as well for unqualified access.
/// </summary>
public sealed class AliasOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly string _alias;

    /// <summary>
    /// Creates an alias operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="alias">The table alias to prefix column names with.</param>
    public AliasOperator(IQueryOperator source, string alias)
    {
        _source = source;
        _alias = alias;
    }

    /// <summary>The child operator.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The table alias.</summary>
    public string Alias => _alias;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            // Expose both aliased (alias.col) and unaliased (col) names.
            string[] names = new string[row.FieldCount * 2];
            DataValue[] values = new DataValue[row.FieldCount * 2];

            for (int index = 0; index < row.FieldCount; index++)
            {
                string originalName = row.ColumnNames[index];
                names[index] = $"{_alias}.{originalName}";
                values[index] = row[index];
                names[row.FieldCount + index] = originalName;
                values[row.FieldCount + index] = row[index];
            }

            yield return new Row(names, values);
        }
    }
}
