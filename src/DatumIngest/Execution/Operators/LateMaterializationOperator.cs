using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Defers fetching expensive columns (e.g. <c>file_bytes</c> from ZIP) until after
/// all joins and filters have run, then fetches only for surviving rows via
/// <see cref="IKeyedTableProvider.FetchByKeysAsync"/>. This avoids materializing
/// expensive data for rows that would be discarded.
/// </summary>
/// <remarks>
/// The operator buffers all rows from its child, collects the key values,
/// fetches the deferred columns in a single batch, and re-emits rows with the
/// additional columns merged in. When an alias is present, physical column names
/// use the qualified form and unqualified names are added to the lookup index
/// only, matching <see cref="AliasOperator"/> behavior.
/// </remarks>
public sealed class LateMaterializationOperator : IQueryOperator
{
    private readonly IQueryOperator _child;
    private readonly TableDescriptor _descriptor;
    private readonly string _keyColumn;
    private readonly IReadOnlySet<string> _deferredColumns;
    private readonly string? _alias;

    /// <summary>
    /// Creates a late materialization operator.
    /// </summary>
    /// <param name="child">The child operator whose rows will be enriched.</param>
    /// <param name="descriptor">Table descriptor for the keyed provider.</param>
    /// <param name="keyColumn">
    /// Unqualified column name used to look up rows in the child output
    /// and in the keyed provider (e.g. <c>file_name</c>).
    /// </param>
    /// <param name="deferredColumns">
    /// Unqualified names of the expensive columns to fetch (e.g. <c>file_bytes</c>).
    /// </param>
    /// <param name="alias">
    /// Optional table alias. When set, both unqualified and qualified
    /// (<c>alias.column</c>) names are added to merged rows.
    /// </param>
    public LateMaterializationOperator(
        IQueryOperator child,
        TableDescriptor descriptor,
        string keyColumn,
        IReadOnlySet<string> deferredColumns,
        string? alias)
    {
        _child = child;
        _descriptor = descriptor;
        _keyColumn = keyColumn;
        _deferredColumns = deferredColumns;
        _alias = alias;
    }

    /// <summary>The child operator.</summary>
    public IQueryOperator Child => _child;

    /// <summary>The table descriptor for keyed fetch.</summary>
    public TableDescriptor Descriptor => _descriptor;

    /// <summary>The key column name used for lookup.</summary>
    public string KeyColumn => _keyColumn;

    /// <summary>The deferred column names being fetched.</summary>
    public IReadOnlySet<string> DeferredColumns => _deferredColumns;

    /// <summary>The source alias (for qualified column names).</summary>
    public string? Alias => _alias;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;

        // Use the alias-qualified key name when reading from child rows (post-JOIN)
        // to avoid ambiguity when multiple tables share the same column name.
        string childKeyColumn = _alias is not null ? $"{_alias}.{_keyColumn}" : _keyColumn;

        // 1. Buffer all rows from child and collect distinct key values.
        List<Row> bufferedRows = new();
        HashSet<DataValue> keyValues = new();

        await foreach (Row row in _child.ExecuteAsync(context).ConfigureAwait(false))
        {
            bufferedRows.Add(row);

            if (row.TryGetValue(childKeyColumn, out DataValue? keyValue) && keyValue is not null && !keyValue.IsNull)
            {
                keyValues.Add(keyValue);
            }
        }

        if (bufferedRows.Count == 0)
        {
            yield break;
        }

        // 2. Fetch deferred columns via keyed provider.
        ITableProvider rawProvider = context.Catalog.CreateProvider(_descriptor);

        if (rawProvider is not IKeyedTableProvider keyedProvider)
        {
            throw new InvalidOperationException(
                $"Provider '{_descriptor.Provider}' does not support keyed access.");
        }

        // Include key column in fetch so we can build the lookup.
        HashSet<string> fetchColumns = new(_deferredColumns, StringComparer.OrdinalIgnoreCase);
        fetchColumns.Add(_keyColumn);

        Dictionary<DataValue, Row> fetchedByKey = new();

        await foreach (Row fetchedRow in keyedProvider.FetchByKeysAsync(
            _descriptor, _keyColumn, keyValues, fetchColumns, cancellationToken)
            .ConfigureAwait(false))
        {
            if (fetchedRow.TryGetValue(_keyColumn, out DataValue? key) && key is not null && !key.IsNull)
            {
                fetchedByKey[key] = fetchedRow;
            }
        }

        // 3. Re-emit rows with deferred columns merged in.
        MergedRowSchema? schema = null;

        foreach (Row row in bufferedRows)
        {
            Row? deferredRow = null;

            if (row.TryGetValue(childKeyColumn, out DataValue? keyValue) && keyValue is not null && !keyValue.IsNull)
            {
                fetchedByKey.TryGetValue(keyValue, out deferredRow);
            }

            schema ??= MergedRowSchema.Build(row, _deferredColumns, _alias);
            yield return schema.Merge(row, deferredRow);
        }
    }

    /// <summary>
    /// Pre-computed schema for rows enriched with deferred columns. Built once from
    /// the first row and reused for all subsequent rows, allocating only a
    /// <see cref="DataValue"/> array per row.
    /// </summary>
    private sealed class MergedRowSchema
    {
        private readonly string[] _names;
        private readonly Dictionary<string, int> _nameIndex;
        private readonly int _originalFieldCount;
        private readonly string[] _deferredColumnNames;

        private MergedRowSchema(
            string[] names,
            Dictionary<string, int> nameIndex,
            int originalFieldCount,
            string[] deferredColumnNames)
        {
            _names = names;
            _nameIndex = nameIndex;
            _originalFieldCount = originalFieldCount;
            _deferredColumnNames = deferredColumnNames;
        }

        /// <summary>
        /// Builds a merged schema by appending deferred columns to the original
        /// row columns. When an alias is set, the physical column names use the
        /// qualified form (<c>alias.column</c>) and the unqualified names are added
        /// to the lookup index only, matching <see cref="AliasOperator"/> behavior.
        /// </summary>
        internal static MergedRowSchema Build(
            Row originalRow,
            IReadOnlySet<string> deferredColumns,
            string? alias)
        {
            int totalFields = originalRow.FieldCount + deferredColumns.Count;

            string[] names = new string[totalFields];
            string[] deferredColumnNames = new string[deferredColumns.Count];

            // Copy original columns.
            for (int index = 0; index < originalRow.FieldCount; index++)
            {
                names[index] = originalRow.ColumnNames[index];
            }

            // Append deferred columns.
            int offset = originalRow.FieldCount;
            int deferredIndex = 0;

            foreach (string columnName in deferredColumns)
            {
                deferredColumnNames[deferredIndex] = columnName;
                deferredIndex++;

                // Physical name is qualified when alias is present.
                names[offset] = alias is not null ? $"{alias}.{columnName}" : columnName;
                offset++;
            }

            Dictionary<string, int> nameIndex = new(totalFields + deferredColumns.Count, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < totalFields; index++)
            {
                nameIndex[names[index]] = index;
            }

            // Add unqualified shortcuts for aliased deferred columns.
            if (alias is not null)
            {
                offset = originalRow.FieldCount;
                foreach (string columnName in deferredColumns)
                {
                    nameIndex[columnName] = offset;
                    offset++;
                }
            }

            return new MergedRowSchema(names, nameIndex, originalRow.FieldCount, deferredColumnNames);
        }

        /// <summary>
        /// Creates a merged row by copying the original values and appending
        /// the deferred column values (null if not fetched).
        /// </summary>
        internal Row Merge(Row originalRow, Row? deferredRow)
        {
            DataValue[] values = new DataValue[_names.Length];

            // Copy original values.
            for (int index = 0; index < _originalFieldCount; index++)
            {
                values[index] = originalRow[index];
            }

            // Fill deferred columns.
            int offset = _originalFieldCount;

            for (int index = 0; index < _deferredColumnNames.Length; index++)
            {
                DataValue value;

                if (deferredRow is not null &&
                    deferredRow.TryGetValue(_deferredColumnNames[index], out DataValue? fetched) &&
                    fetched is not null)
                {
                    value = fetched;
                }
                else
                {
                    value = DataValue.Null(DataKind.UInt8Array);
                }

                values[offset] = value;
                offset++;
            }

            return new Row(_names, values, _nameIndex);
        }
    }
}
