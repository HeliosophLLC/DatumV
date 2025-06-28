using Axon.QueryEngine.Model;
using Axon.QueryEngine.Parsing.Ast;

namespace Axon.QueryEngine.Execution.Operators;

/// <summary>
/// Evaluates SELECT column expressions against each incoming row,
/// producing output rows with the projected column set.
/// Expression results are wrapped in <see cref="LazyDataValue"/> thunks
/// so computation chains through nested SELECTs without eagerly materializing.
/// </summary>
public sealed class ProjectOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<SelectColumn> _columns;
    private readonly Schema? _sourceSchema;

    /// <summary>
    /// Creates a project operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="columns">The SELECT columns to project.</param>
    /// <param name="sourceSchema">Optional source schema for star expansion.</param>
    public ProjectOperator(
        IQueryOperator source,
        IReadOnlyList<SelectColumn> columns,
        Schema? sourceSchema = null)
    {
        _source = source;
        _columns = columns;
        _sourceSchema = sourceSchema;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The projected SELECT columns.</summary>
    public IReadOnlyList<SelectColumn> Columns => _columns;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry);

        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            yield return ProjectRow(row, evaluator);
        }
    }

    private Row ProjectRow(Row sourceRow, ExpressionEvaluator evaluator)
    {
        // Expand star columns and evaluate expressions.
        List<string> names = new();
        List<DataValue> values = new();

        foreach (SelectColumn column in _columns)
        {
            switch (column)
            {
                case SelectAllColumns:
                    // SELECT * -- pass through all columns from the source row.
                    for (int index = 0; index < sourceRow.FieldCount; index++)
                    {
                        names.Add(sourceRow.ColumnNames[index]);
                        values.Add(sourceRow[index]);
                    }
                    break;

                case SelectTableColumns tableColumns:
                    // SELECT table.* -- pass through columns matching the table prefix.
                    string prefix = tableColumns.TableName + ".";
                    for (int index = 0; index < sourceRow.FieldCount; index++)
                    {
                        string columnName = sourceRow.ColumnNames[index];
                        if (columnName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            || columnName.Equals(tableColumns.TableName, StringComparison.OrdinalIgnoreCase))
                        {
                            names.Add(columnName);
                            values.Add(sourceRow[index]);
                        }
                    }
                    break;

                default:
                    // Named expression -- evaluate.
                    string outputName = ResolveColumnName(column);
                    DataValue evaluatedValue = evaluator.Evaluate(column.Expression, sourceRow);
                    names.Add(outputName);
                    values.Add(evaluatedValue);
                    break;
            }
        }

        return new Row(names.ToArray(), values.ToArray());
    }

    private static string ResolveColumnName(SelectColumn column)
    {
        if (column.Alias is not null)
        {
            return column.Alias;
        }

        return column.Expression switch
        {
            ColumnReference colRef => colRef.ColumnName,
            FunctionCallExpression funcCall => funcCall.FunctionName,
            _ => "expr",
        };
    }
}
