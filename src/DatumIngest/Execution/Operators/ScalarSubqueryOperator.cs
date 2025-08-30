using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Executes a correlated scalar subquery per outer row and augments each row
/// with the scalar result in a synthetic column. The inner query sees the
/// outer row via <see cref="ExecutionContext.OuterRow"/>, enabling correlated
/// column references (e.g. <c>WHERE t2.id = t1.id</c>) to resolve.
/// </summary>
internal sealed class ScalarSubqueryOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly IQueryOperator _innerPlan;
    private readonly string _syntheticColumnName;

    /// <summary>
    /// Creates a scalar subquery operator.
    /// </summary>
    /// <param name="source">The outer operator producing rows.</param>
    /// <param name="innerPlan">The pre-planned inner subquery operator tree.</param>
    /// <param name="syntheticColumnName">The column name to inject the scalar result as.</param>
    public ScalarSubqueryOperator(
        IQueryOperator source,
        IQueryOperator innerPlan,
        string syntheticColumnName)
    {
        _source = source;
        _innerPlan = innerPlan;
        _syntheticColumnName = syntheticColumnName;
    }

    /// <summary>The outer operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The correlated inner subquery operator tree.</summary>
    public IQueryOperator InnerPlan => _innerPlan;

    /// <summary>The synthetic column name for the scalar result.</summary>
    public string SyntheticColumnName => _syntheticColumnName;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
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
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        LocalBufferPool pool = context.LocalBufferPool;
        string[]? outputNames = null;
        Dictionary<string, int>? outputNameIndex = null;

        await foreach (Row outerRow in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // Execute the inner subquery with the current outer row as correlation context.
            ExecutionContext innerContext = context.WithOuterRow(outerRow);

            Row? firstRow = null;
            bool hasMultipleRows = false;

            await foreach (Row innerRow in _innerPlan.ExecuteAsync(innerContext).ConfigureAwait(false))
            {
                if (firstRow is not null)
                {
                    hasMultipleRows = true;
                    break;
                }

                firstRow = innerRow;
            }

            if (hasMultipleRows)
            {
                throw new InvalidOperationException("Correlated scalar subquery returned more than one row.");
            }

            // Extract scalar value: zero rows → NULL, one row → first column value.
            DataValue scalarResult;
            if (firstRow is null)
            {
                scalarResult = DataValue.Null(DataKind.Float32);
            }
            else
            {
                if (firstRow.FieldCount != 1)
                {
                    throw new InvalidOperationException(
                        $"Scalar subquery must return exactly one column, but returned {firstRow.FieldCount}.");
                }

                scalarResult = firstRow[0];
            }

            // Augment the outer row with the synthetic column.
            int outerFieldCount = outerRow.FieldCount;

            if (outputNames is null)
            {
                outputNames = new string[outerFieldCount + 1];
                for (int index = 0; index < outerFieldCount; index++)
                {
                    outputNames[index] = outerRow.ColumnNames[index];
                }

                outputNames[outerFieldCount] = _syntheticColumnName;

                outputNameIndex = new Dictionary<string, int>(
                    outputNames.Length, StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < outputNames.Length; index++)
                {
                    outputNameIndex[outputNames[index]] = index;
                }
            }

            DataValue[] values = pool.RentOwned(outerFieldCount + 1);

            for (int index = 0; index < outerFieldCount; index++)
            {
                values[index] = outerRow[index];
            }

            values[outerFieldCount] = scalarResult;

            yield return new Row(outputNames, values, outputNameIndex!);
        }
    }
}
