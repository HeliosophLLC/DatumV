using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// A streaming operator that unpivots a wide row into multiple narrow rows by rotating
/// a set of source columns into (name, value) pairs.
/// <para>
/// For each input row the operator emits one output row per source column in
/// <see cref="SourceColumnNames"/>. Each output row carries all non-source columns from
/// the input (key columns), the column name as a string in <see cref="NameColumnName"/>,
/// and the column value in <see cref="ValueColumnName"/>.
/// </para>
/// <para>
/// By default rows whose source column value is NULL are omitted. Set
/// <see cref="IncludeNulls"/> to <see langword="true"/> to retain them.
/// </para>
/// </summary>
public sealed class UnpivotOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly string _valueColumnName;
    private readonly string _nameColumnName;
    private readonly IReadOnlyList<string> _sourceColumnNames;
    private readonly bool _includeNulls;

    /// <summary>Creates an UNPIVOT operator.</summary>
    /// <param name="source">The child operator producing wide rows.</param>
    /// <param name="valueColumnName">
    /// Name of the output column that receives the cell value.
    /// </param>
    /// <param name="nameColumnName">
    /// Name of the output column that receives the source column name.
    /// </param>
    /// <param name="sourceColumnNames">
    /// The ordered list of source column names to unpivot.
    /// </param>
    /// <param name="includeNulls">
    /// When <see langword="true"/>, rows whose source column value is NULL are included in
    /// the output. When <see langword="false"/> (the default) they are silently skipped.
    /// </param>
    public UnpivotOperator(
        IQueryOperator source,
        string valueColumnName,
        string nameColumnName,
        IReadOnlyList<string> sourceColumnNames,
        bool includeNulls = false)
    {
        _source = source;
        _valueColumnName = valueColumnName;
        _nameColumnName = nameColumnName;
        _sourceColumnNames = sourceColumnNames;
        _includeNulls = includeNulls;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>Name of the output column receiving cell values.</summary>
    public string ValueColumnName => _valueColumnName;

    /// <summary>Name of the output column receiving source column names.</summary>
    public string NameColumnName => _nameColumnName;

    /// <summary>The source columns being unpivoted.</summary>
    public IReadOnlyList<string> SourceColumnNames => _sourceColumnNames;

    /// <summary>Whether NULL-valued cells are included in the output.</summary>
    public bool IncludeNulls => _includeNulls;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        return new OperatorPlanDescription("Unpivot")
        {
            Properties = new Dictionary<string, string>
            {
                ["value column"] = _valueColumnName,
                ["name column"] = _nameColumnName,
                ["source columns"] = string.Join(", ", _sourceColumnNames),
                ["include nulls"] = _includeNulls.ToString(),
            },
            Children = [(Source, null)],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        // Output schema is derived from the first row: key columns + value + name.
        // The schema is built lazily on the first row.
        string[]? outputNames = null;
        Dictionary<string, int>? outputNameIndex = null;
        HashSet<string>? sourceColumnSet = null;
        int[]? keyFieldOrdinals = null;
        string[]? keyFieldNames = null;
        LocalBufferPool pool = context.LocalBufferPool;
        RowBatch? outputBatch = null;

        await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            for (int i = 0; i < inputBatch.Count; i++)
            {
            Row row = inputBatch[i];
            context.CancellationToken.ThrowIfCancellationRequested();
            context.QueryMeter?.ThrowIfExceeded();

            // Build output schema from the first row.
            if (outputNames is null)
            {
                sourceColumnSet = new HashSet<string>(_sourceColumnNames, StringComparer.OrdinalIgnoreCase);

                List<int> keyOrdinals = new(row.FieldCount);
                List<string> keyNames = new(row.FieldCount);

                for (int fieldIndex = 0; fieldIndex < row.FieldCount; fieldIndex++)
                {
                    string fieldName = row.ColumnNames[fieldIndex];
                    if (!sourceColumnSet.Contains(fieldName))
                    {
                        keyOrdinals.Add(fieldIndex);
                        keyNames.Add(fieldName);
                    }
                }

                keyFieldOrdinals = keyOrdinals.ToArray();
                keyFieldNames = keyNames.ToArray();

                // Output columns: key columns, then value column, then name column.
                outputNames = new string[keyFieldNames.Length + 2];

                for (int keyIndex = 0; keyIndex < keyFieldNames.Length; keyIndex++)
                {
                    outputNames[keyIndex] = keyFieldNames[keyIndex];
                }

                outputNames[keyFieldNames.Length] = _valueColumnName;
                outputNames[keyFieldNames.Length + 1] = _nameColumnName;

                outputNameIndex = new Dictionary<string, int>(outputNames.Length, StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < outputNames.Length; index++)
                {
                    outputNameIndex[outputNames[index]] = index;
                }
            }

            // Emit one output row per source column.
            for (int sourceIndex = 0; sourceIndex < _sourceColumnNames.Count; sourceIndex++)
            {
                string sourceColumnName = _sourceColumnNames[sourceIndex];
                DataValue cellValue;

                if (row.TryGetValue(sourceColumnName, out DataValue found))
                {
                    cellValue = found;
                }
                else
                {
                    // Column not present in this row — treat as NULL.
                    cellValue = DataValue.Null(DataKind.Float32);
                }

                if (cellValue.IsNull && !_includeNulls)
                {
                    continue;
                }

                DataValue[] values = pool.RentOwned(outputNames!.Length);

                // Copy key field values.
                for (int keyIndex = 0; keyIndex < keyFieldOrdinals!.Length; keyIndex++)
                {
                    values[keyIndex] = row[keyFieldOrdinals[keyIndex]];
                }

                // Value column.
                values[keyFieldOrdinals.Length] = cellValue;

                // Name column — the source column name as a string.
                values[keyFieldOrdinals.Length + 1] = DataValue.FromString(sourceColumnName);

                outputBatch ??= RowBatch.Rent(context.BatchSize);
                outputBatch.Add(new Row(outputNames, values, outputNameIndex!));

                if (outputBatch.IsFull)
                {
                    yield return outputBatch;
                    outputBatch = null;
                }
            }
            }

            inputBatch.Return();
        }

        if (outputBatch is not null)
        {
            yield return outputBatch;
        }
    }
}
