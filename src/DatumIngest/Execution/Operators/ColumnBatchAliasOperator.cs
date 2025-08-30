using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Prefixes all column names in incoming <see cref="ColumnBatch"/> objects with a
/// table alias, enabling qualified column references (e.g. <c>t.column_name</c>).
/// The batch data is unchanged — only the column name mapping is replaced.
/// </summary>
public sealed class ColumnBatchAliasOperator : IColumnBatchOperator
{
    private readonly IColumnBatchOperator _source;
    private readonly string _alias;

    /// <summary>
    /// Creates a columnar alias operator.
    /// </summary>
    /// <param name="source">The child columnar operator producing batches.</param>
    /// <param name="alias">The table alias to prefix column names with.</param>
    public ColumnBatchAliasOperator(IColumnBatchOperator source, string alias)
    {
        _source = source;
        _alias = alias;
    }

    /// <summary>The child columnar operator.</summary>
    public IColumnBatchOperator Source => _source;

    /// <summary>The table alias.</summary>
    public string Alias => _alias;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        return new OperatorPlanDescription("Alias")
        {
            Properties = new Dictionary<string, string>
            {
                ["alias"] = _alias,
                ["mode"] = "columnar",
            },
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ColumnBatch> ExecuteColumnBatchAsync(ExecutionContext context)
    {
        ColumnBatchAliasSchema? schema = null;

        await foreach (ColumnBatch batch in _source.ExecuteColumnBatchAsync(context).ConfigureAwait(false))
        {
            schema ??= ColumnBatchAliasSchema.Build(_alias, batch);

            // Create a new ColumnBatch that shares the source column arrays
            // but with aliased names.  The original batch is NOT disposed here —
            // the output batch borrows its column arrays and arenas.
            ColumnBatch aliasedBatch = batch.WithColumnNames(schema.Names, schema.NameIndex);
            yield return aliasedBatch;
        }
    }

    /// <summary>
    /// Pre-computed alias schema built once from the first batch.
    /// </summary>
    private sealed class ColumnBatchAliasSchema
    {
        internal string[] Names { get; }
        internal Dictionary<string, int> NameIndex { get; }

        private ColumnBatchAliasSchema(string[] names, Dictionary<string, int> nameIndex)
        {
            Names = names;
            NameIndex = nameIndex;
        }

        internal static ColumnBatchAliasSchema Build(string alias, ColumnBatch batch)
        {
            int columnCount = batch.ColumnCount;
            string[] names = new string[columnCount];

            for (int index = 0; index < columnCount; index++)
            {
                names[index] = $"{alias}.{batch.GetColumnName(index)}";
            }

            // Map both qualified and unqualified names to the same slot.
            Dictionary<string, int> nameIndex =
                new(columnCount * 2, StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < columnCount; index++)
            {
                nameIndex[names[index]] = index;
                nameIndex[batch.GetColumnName(index)] = index;
            }

            return new ColumnBatchAliasSchema(names, nameIndex);
        }
    }
}
