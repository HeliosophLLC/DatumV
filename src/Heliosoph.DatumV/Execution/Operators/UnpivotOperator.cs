using Heliosoph.DatumV.Execution.Operators.Pivot;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// Streaming operator that rotates a set of source columns into <c>(name, value)</c> pairs.
/// For each input row, one output row is emitted per column listed in
/// <see cref="SourceColumnNames"/> — carrying every non-source column unchanged (the "keys"),
/// the source column's value in <see cref="ValueColumnName"/>, and the column's name as a
/// string in <see cref="NameColumnName"/>.
/// </summary>
/// <remarks>
/// NULL cells are skipped by default; set <see cref="IncludeNulls"/> to <see langword="true"/>
/// to emit them. The output schema is derived from the first observed row.
/// </remarks>
public sealed class UnpivotOperator : QueryOperator
{
    private readonly QueryOperator _source;
    private readonly string _valueColumnName;
    private readonly string _nameColumnName;
    private readonly IReadOnlyList<string> _sourceColumnNames;
    private readonly bool _includeNulls;

    /// <summary>Creates an UNPIVOT operator.</summary>
    /// <param name="source">The child operator producing wide rows.</param>
    /// <param name="valueColumnName">Name of the output column that receives the cell value.</param>
    /// <param name="nameColumnName">Name of the output column that receives the source column name.</param>
    /// <param name="sourceColumnNames">The ordered list of source column names to unpivot.</param>
    /// <param name="includeNulls">When <see langword="true"/>, NULL-valued cells are emitted; otherwise they are skipped.</param>
    public UnpivotOperator(
        QueryOperator source,
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
    public QueryOperator Source => _source;

    /// <summary>Name of the output column receiving cell values.</summary>
    public string ValueColumnName => _valueColumnName;

    /// <summary>Name of the output column receiving source column names.</summary>
    public string NameColumnName => _nameColumnName;

    /// <summary>The source columns being unpivoted.</summary>
    public IReadOnlyList<string> SourceColumnNames => _sourceColumnNames;

    /// <summary>Whether NULL-valued cells are emitted to the output.</summary>
    public bool IncludeNulls => _includeNulls;

    /// <inheritdoc/>
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter)
    {
        return new UnpivotOperator(
            _source.RewriteExpressions(rewriter),
            _valueColumnName,
            _nameColumnName,
            _sourceColumnNames,
            _includeNulls);
    }

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
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
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        ColumnLookup? outputLookup = null;
        int[]? keyOrdinals = null;
        int[]? sourceOrdinals = null;
        UnpivotOutputWriter writer = new(context);

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    // Values from the input batch may reference its arena (strings, blobs).
                    // The writer stabilises through to the output batch's arena before
                    // yielding so downstream consumers resolve them through `batch.Arena`.
                    IValueStore sourceArena = inputBatch.Arena;

                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        Row row = inputBatch[i];

                        if (outputLookup is null)
                        {
                            (outputLookup, keyOrdinals, sourceOrdinals) = BuildSchema(row);
                        }

                        for (int s = 0; s < _sourceColumnNames.Count; s++)
                        {
                            int sourceOrdinal = sourceOrdinals![s];
                            // -1 = column not present in the input schema. Treat as NULL.
                            // Float32 is an arbitrary kind placeholder for the typeless NULL.
                            DataValue cellValue = sourceOrdinal >= 0
                                ? row[sourceOrdinal]
                                : DataValue.Null(DataKind.Float32);

                            if (cellValue.IsNull && !_includeNulls)
                            {
                                continue;
                            }

                            if (writer.Emit(outputLookup, row, sourceArena, keyOrdinals!, cellValue, _sourceColumnNames[s]) is RowBatch full)
                            {
                                yield return full;
                            }
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            if (writer.Flush() is RowBatch trailing)
            {
                yield return trailing;
            }
        }
        finally
        {
            if (writer.Flush() is RowBatch leftover)
            {
                context.ReturnRowBatch(leftover);
            }
        }
    }

    private (ColumnLookup OutputLookup, int[] KeyOrdinals, int[] SourceOrdinals) BuildSchema(Row firstRow)
    {
        HashSet<string> sourceSet = new(_sourceColumnNames, StringComparer.OrdinalIgnoreCase);
        (int[] keyOrdinals, string[] keyNames) = KeyColumnResolver.Resolve(firstRow, sourceSet);

        string[] outputNames = new string[keyNames.Length + 2];
        Array.Copy(keyNames, outputNames, keyNames.Length);
        outputNames[keyNames.Length] = _valueColumnName;
        outputNames[keyNames.Length + 1] = _nameColumnName;

        int[] sourceOrdinals = new int[_sourceColumnNames.Count];
        IReadOnlyDictionary<string, int> nameIndex = firstRow.ColumnLookup.NameIndex;
        for (int s = 0; s < _sourceColumnNames.Count; s++)
        {
            sourceOrdinals[s] = nameIndex.TryGetValue(_sourceColumnNames[s], out int ord) ? ord : -1;
        }

        return (new ColumnLookup(outputNames), keyOrdinals, sourceOrdinals);
    }
}
