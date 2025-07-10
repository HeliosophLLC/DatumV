using DatumQuery.Model;

namespace DatumQuery.Execution.Operators;

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
        AliasSchema? schema = null;

        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            schema ??= AliasSchema.Build(_alias, row);
            yield return schema.Apply(row);
        }
    }

    /// <summary>
    /// Pre-computed doubled column schema for alias expansion. Built once from
    /// the first source row and reused for all subsequent rows, allocating only
    /// a <see cref="DataValue"/> array per row.
    /// </summary>
    private sealed class AliasSchema
    {
        private readonly string[] _names;
        private readonly Dictionary<string, int> _nameIndex;
        private readonly int _sourceFieldCount;

        private AliasSchema(
            string[] names,
            Dictionary<string, int> nameIndex,
            int sourceFieldCount)
        {
            _names = names;
            _nameIndex = nameIndex;
            _sourceFieldCount = sourceFieldCount;
        }

        /// <summary>
        /// Builds the alias schema from the alias prefix and the first source row.
        /// </summary>
        internal static AliasSchema Build(string alias, Row firstRow)
        {
            int fieldCount = firstRow.FieldCount;
            string[] names = new string[fieldCount * 2];

            for (int index = 0; index < fieldCount; index++)
            {
                string originalName = firstRow.ColumnNames[index];
                names[index] = $"{alias}.{originalName}";
                names[fieldCount + index] = originalName;
            }

            Dictionary<string, int> nameIndex =
                new(names.Length, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < names.Length; index++)
            {
                nameIndex[names[index]] = index;
            }

            return new AliasSchema(names, nameIndex, fieldCount);
        }

        /// <summary>
        /// Applies the alias schema to a source row. Only a <see cref="DataValue"/>
        /// array is allocated per call.
        /// </summary>
        internal Row Apply(Row sourceRow)
        {
            DataValue[] values = new DataValue[_names.Length];

            for (int index = 0; index < _sourceFieldCount; index++)
            {
                DataValue value = sourceRow[index];
                values[index] = value;
                values[_sourceFieldCount + index] = value;
            }

            return new Row(_names, values, _nameIndex);
        }
    }
}
