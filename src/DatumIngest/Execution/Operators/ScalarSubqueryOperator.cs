using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Executes a correlated scalar subquery per outer row and augments each row
/// with the scalar result in a synthetic column. The inner query sees the
/// outer row via <see cref="ExecutionContext.OuterRow"/>, enabling correlated
/// column references (e.g. <c>WHERE t2.id = t1.id</c>) to resolve.
/// </summary>
internal sealed class ScalarSubqueryOperator : QueryOperator
{
    private readonly QueryOperator _source;
    private readonly QueryOperator _innerPlan;
    private readonly string _syntheticColumnName;

    /// <summary>
    /// Creates a scalar subquery operator.
    /// </summary>
    /// <param name="source">The outer operator producing rows.</param>
    /// <param name="innerPlan">The pre-planned inner subquery operator tree.</param>
    /// <param name="syntheticColumnName">The column name to inject the scalar result as.</param>
    public ScalarSubqueryOperator(
        QueryOperator source,
        QueryOperator innerPlan,
        string syntheticColumnName)
    {
        _source = source;
        _innerPlan = innerPlan;
        _syntheticColumnName = syntheticColumnName;
    }

    /// <summary>The outer operator producing rows.</summary>
    public QueryOperator Source => _source;

    /// <summary>The correlated inner subquery operator tree.</summary>
    public QueryOperator InnerPlan => _innerPlan;

    /// <summary>The synthetic column name for the scalar result.</summary>
    public string SyntheticColumnName => _syntheticColumnName;

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        return new OperatorPlanDescription("Scalar Subquery")
        {
            Properties = new Dictionary<string, string>
            {
                ["column"] = _syntheticColumnName,
            },
            Children = [(Source, "outer"), (InnerPlan, "inner")],
            Warnings = ["executes inner subquery per outer row"],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        Pool pool = context.Pool;
        ColumnLookup? outputLookup = null;
        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        Row outerRow = inputBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        // Execute the inner subquery with the current outer row as correlation context.
                        ExecutionContext innerContext = context.WithOuterRow(outerRow);

                        // Extract scalar value: zero rows → NULL, one row → first column value.
                        // The DataValue is copied out as a struct before the inner batch is
                        // returned to the pool — the array backing the row would otherwise be
                        // recycled and the slot overwritten. Arena-backed payloads (string
                        // offsets, etc.) remain valid because the inner context shares
                        // context.Store with the outer.
                        DataValue scalarResult = DataValue.UnknownNull();
                        bool foundRow = false;
                        bool hasMultipleRows = false;

                        await foreach (RowBatch innerBatch in _innerPlan.ExecuteAsync(innerContext).ConfigureAwait(false))
                        {
                            try
                            {
                                for (int j = 0; j < innerBatch.Count; j++)
                                {
                                    Row innerRow = innerBatch[j];
                                    if (foundRow)
                                    {
                                        hasMultipleRows = true;
                                        break;
                                    }

                                    if (innerRow.FieldCount != 1)
                                    {
                                        throw new InvalidOperationException(
                                            $"Scalar subquery must return exactly one column, but returned {innerRow.FieldCount}.");
                                    }

                                    scalarResult = innerRow[0];
                                    foundRow = true;
                                }
                            }
                            finally
                            {
                                context.ReturnRowBatch(innerBatch);
                            }

                            if (hasMultipleRows)
                            {
                                break;
                            }
                        }

                        if (hasMultipleRows)
                        {
                            throw new InvalidOperationException("Correlated scalar subquery returned more than one row.");
                        }

                        // Augment the outer row with the synthetic column.
                        int outerFieldCount = outerRow.FieldCount;

                        if (outputLookup is null)
                        {
                            string[] outputNames = new string[outerFieldCount + 1];
                            for (int index = 0; index < outerFieldCount; index++)
                            {
                                outputNames[index] = outerRow.ColumnNames[index];
                            }
                            outputNames[outerFieldCount] = _syntheticColumnName;
                            outputLookup = new ColumnLookup(outputNames);
                        }

                        DataValue[] values = pool.RentDataValues(outerFieldCount + 1);
                        for (int index = 0; index < outerFieldCount; index++)
                        {
                            values[index] = outerRow[index];
                        }
                        values[outerFieldCount] = scalarResult;

                        outputBatch ??= context.RentRowBatch(outputLookup);
                        outputBatch.Add(values);

                        if (outputBatch.IsFull)
                        {
                            RowBatch toYield = outputBatch;
                            outputBatch = null;
                            yield return toYield;
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            if (outputBatch is not null)
            {
                RowBatch toYield = outputBatch;
                outputBatch = null;
                yield return toYield;
            }
        }
        finally
        {
            if (outputBatch is not null)
            {
                context.ReturnRowBatch(outputBatch);
            }
        }
    }
}
