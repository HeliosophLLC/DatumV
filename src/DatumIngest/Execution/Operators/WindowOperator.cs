using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Execution.Operators.Windows;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Computes window function results over partitions of input rows.
/// This is a blocking operator: all input rows must be materialized
/// before any output can be emitted, because window functions require
/// visibility of the entire partition.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="GroupByOperator"/>, which collapses each group
/// into a single row, the window operator preserves every input row
/// and augments each one with the computed window function columns.
/// Rows are emitted in their original arrival order.
/// </para>
/// <para>
/// When multiple window columns share the same <see cref="WindowSpecification"/>
/// (same PARTITION BY, ORDER BY, and frame), they are computed in the same
/// partitioning/sorting pass to avoid redundant work.
/// </para>
/// </remarks>
public sealed class WindowOperator : QueryOperator
{
    private readonly QueryOperator _source;
    private readonly IReadOnlyList<WindowColumn> _windowColumns;

    /// <summary>
    /// Creates a new window operator that computes the specified window
    /// function columns over rows from the source operator.
    /// </summary>
    /// <param name="source">The upstream operator providing input rows.</param>
    /// <param name="windowColumns">The window function columns to compute.</param>
    public WindowOperator(
        QueryOperator source,
        IReadOnlyList<WindowColumn> windowColumns)
    {
        _source = source;
        _windowColumns = windowColumns;
    }

    /// <summary>The upstream source operator.</summary>
    public QueryOperator Source => _source;

    /// <summary>The window function columns being computed.</summary>
    public IReadOnlyList<WindowColumn> WindowColumns => _windowColumns;

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        List<string> functionDescriptions = [];
        foreach (WindowColumn column in _windowColumns)
        {
            functionDescriptions.Add($"{column.Function.Name}() AS {column.OutputName}");
        }

        return new OperatorPlanDescription("Window")
        {
            Properties = new Dictionary<string, string>
            {
                ["functions"] = string.Join(", ", functionDescriptions),
            },
            Children = [(Source, null)],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        Pool pool = context.Pool;
        ExpressionEvaluator evaluator = new(context);
        using MaterializedInput input = new(context, "WINDOW");
        RowBatch? outputBatch = null;

        try
        {
            await input.CollectAsync(_source.ExecuteAsync(context)).ConfigureAwait(false);

            if (input.Rows.Count == 0) yield break;

            int inputFieldCount = input.Rows[0].FieldCount;
            int totalFieldCount = inputFieldCount + _windowColumns.Count;

            // Result slot for each row × each window column.
            DataValue[][] windowResults = new DataValue[input.Rows.Count][];
            for (int i = 0; i < windowResults.Length; i++)
            {
                windowResults[i] = new DataValue[_windowColumns.Count];
            }

            // Group window columns by spec — columns sharing the same
            // PARTITION BY / ORDER BY / Frame compute together.
            Dictionary<WindowSpecificationKey, List<int>> specGroups = new();
            for (int columnIndex = 0; columnIndex < _windowColumns.Count; columnIndex++)
            {
                WindowSpecificationKey key = new(_windowColumns[columnIndex].WindowSpecification);
                if (!specGroups.TryGetValue(key, out List<int>? indices))
                {
                    indices = new List<int>();
                    specGroups[key] = indices;
                }
                indices.Add(columnIndex);
            }

            foreach (KeyValuePair<WindowSpecificationKey, List<int>> specGroup in specGroups)
            {
                WindowSpecification spec = _windowColumns[specGroup.Value[0]].WindowSpecification;
                await ComputeForSpecificationAsync(
                    spec, specGroup.Value, input.Rows, evaluator, windowResults,
                    context.Store, context.SidecarRegistry, context.CancellationToken).ConfigureAwait(false);
            }

            // Build the output ColumnLookup once — source columns followed by
            // each window column's OutputName. Reused across every output batch.
            string[] outputNames = new string[totalFieldCount];
            for (int field = 0; field < inputFieldCount; field++)
            {
                outputNames[field] = input.SourceLookup!.ColumnNames[field];
            }
            for (int windowColumnIndex = 0; windowColumnIndex < _windowColumns.Count; windowColumnIndex++)
            {
                outputNames[inputFieldCount + windowColumnIndex] =
                    _windowColumns[windowColumnIndex].OutputName;
            }
            ColumnLookup outputLookup = new(outputNames);

            // Emit rows in original input order, augmented with window columns.
            for (int rowIndex = 0; rowIndex < input.Rows.Count; rowIndex++)
            {
                Row sourceRow = input.Rows[rowIndex];

                DataValue[] values = pool.RentDataValues(totalFieldCount);
                for (int field = 0; field < inputFieldCount; field++)
                {
                    values[field] = sourceRow[field];
                }
                for (int windowColumnIndex = 0; windowColumnIndex < _windowColumns.Count; windowColumnIndex++)
                {
                    values[inputFieldCount + windowColumnIndex] = windowResults[rowIndex][windowColumnIndex];
                }

                outputBatch ??= context.RentRowBatch(outputLookup);
                outputBatch.Add(values);

                if (outputBatch.IsFull)
                {
                    RowBatch toYield = outputBatch;
                    outputBatch = null;
                    yield return toYield;
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
            // MaterializedInput's using-Dispose releases source-row rentals + accountant.
        }
    }

    /// <summary>
    /// Partitions, sorts, and computes window function results for all
    /// columns that share the given window specification.
    /// </summary>
    private async ValueTask ComputeForSpecificationAsync(
        WindowSpecification spec,
        List<int> columnIndices,
        List<Row> allRows,
        ExpressionEvaluator evaluator,
        DataValue[][] windowResults,
        IValueStore store,
        SidecarRegistry? sidecarRegistry,
        CancellationToken cancellationToken)
    {
        // Build an index array to track original positions through partitioning.
        int[] originalIndices = new int[allRows.Count];
        for (int i = 0; i < originalIndices.Length; i++)
        {
            originalIndices[i] = i;
        }

        List<(int StartIndex, int Count)> partitionRanges = spec.PartitionBy is null or { Count: 0 }
            ? [(0, allRows.Count)]
            : await PartitionBuilder.BuildPartitionsAsync(
                allRows, originalIndices, spec.PartitionBy, evaluator, cancellationToken).ConfigureAwait(false);

        foreach ((int startIndex, int count) in partitionRanges)
        {
            if (spec.OrderBy is { Count: > 0 })
            {
                await PartitionSorter.SortPartitionAsync(
                    allRows, originalIndices, startIndex, count, spec.OrderBy, evaluator,
                    store, sidecarRegistry, "WINDOW", cancellationToken).ConfigureAwait(false);
            }

            // Build partition row list for the computation interface.
            List<Row> partitionRows = new(count);
            for (int i = startIndex; i < startIndex + count; i++)
            {
                partitionRows.Add(allRows[originalIndices[i]]);
            }

            // Compute each window column for this partition.
            DataValue[] partitionResults = new DataValue[count];
            for (int colIdx = 0; colIdx < columnIndices.Count; colIdx++)
            {
                int windowColumnIndex = columnIndices[colIdx];
                WindowColumn column = _windowColumns[windowColumnIndex];

                IWindowComputation computation = column.Function.CreateComputation();
                Array.Clear(partitionResults);
                await computation.ComputeAsync(
                    partitionRows,
                    column.ArgumentExpressions,
                    evaluator,
                    spec.OrderBy,
                    spec.Frame,
                    partitionResults,
                    column.NullHandling,
                    column.FromLast,
                    cancellationToken).ConfigureAwait(false);

                // Write results back to the correct original row positions.
                for (int i = 0; i < count; i++)
                {
                    int originalRowIndex = originalIndices[startIndex + i];
                    windowResults[originalRowIndex][windowColumnIndex] = partitionResults[i];
                }
            }
        }
    }
}

/// <summary>
/// Describes a window function column to compute: the function, its arguments,
/// the window specification (OVER clause), and the output column name.
/// </summary>
/// <param name="Function">The window function implementation.</param>
/// <param name="ArgumentExpressions">The argument expressions to evaluate per row.</param>
/// <param name="WindowSpecification">The OVER clause specification (PARTITION BY, ORDER BY, frame).</param>
/// <param name="OutputName">The name of the output column.</param>
/// <param name="NullHandling">RESPECT NULLS (default) or IGNORE NULLS modifier for value window functions.</param>
/// <param name="FromLast">When true, NTH_VALUE counts from the end of the frame instead of the beginning.</param>
public sealed record WindowColumn(
    IWindowFunction Function,
    IReadOnlyList<Expression> ArgumentExpressions,
    WindowSpecification WindowSpecification,
    string OutputName,
    NullHandling NullHandling = NullHandling.RespectNulls,
    bool FromLast = false);
