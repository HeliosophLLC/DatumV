using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

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
        /// The physical column names use the qualified form (<c>alias.column</c>).
        /// The unqualified original names are added to the lookup index so that
        /// expressions can resolve either form, without doubling the column count.
        /// </summary>
        internal static AliasSchema Build(string alias, Row firstRow)
        {
            int fieldCount = firstRow.FieldCount;
            string[] names = new string[fieldCount];

            for (int index = 0; index < fieldCount; index++)
            {
                names[index] = $"{alias}.{firstRow.ColumnNames[index]}";
            }

            // Map both qualified and unqualified names to the same slot.
            Dictionary<string, int> nameIndex =
                new(fieldCount * 2, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < fieldCount; index++)
            {
                nameIndex[names[index]] = index;
                nameIndex[firstRow.ColumnNames[index]] = index;
            }

            return new AliasSchema(names, nameIndex, fieldCount);
        }

        /// <summary>
        /// Applies the alias schema to a source row. Only a <see cref="DataValue"/>
        /// array is allocated per call.
        /// </summary>
        internal Row Apply(Row sourceRow)
        {
            DataValue[] values = new DataValue[_sourceFieldCount];

            for (int index = 0; index < _sourceFieldCount; index++)
            {
                values[index] = sourceRow[index];
            }

            return new Row(_names, values, _nameIndex);
        }
    }
}
