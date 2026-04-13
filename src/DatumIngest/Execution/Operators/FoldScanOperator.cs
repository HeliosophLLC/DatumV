using System.Linq;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Execution.Operators.Windows;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Computes fold/prefix-scan results over ordered partitions. Each row's
/// output feeds back as the accumulator for the next row:
/// <c>output[i] = f(output[i-1], input[i])</c>.
/// This is a blocking operator: all input rows are materialized before
/// any output is emitted, because the fold is sequential within each partition.
/// </summary>
public sealed class FoldScanOperator : QueryOperator
{
    private readonly QueryOperator _source;
    private readonly IReadOnlyList<FoldScanColumn> _scanColumns;

    /// <summary>
    /// Creates a new fold/scan operator that computes the specified scan
    /// columns over rows from the source operator.
    /// </summary>
    /// <param name="source">The upstream operator providing input rows.</param>
    /// <param name="scanColumns">The fold/scan columns to compute.</param>
    public FoldScanOperator(
        QueryOperator source,
        IReadOnlyList<FoldScanColumn> scanColumns)
    {
        _source = source;
        _scanColumns = scanColumns;
    }

    /// <summary>The upstream source operator.</summary>
    public QueryOperator Source => _source;

    /// <summary>The fold/scan columns being computed.</summary>
    public IReadOnlyList<FoldScanColumn> ScanColumns => _scanColumns;

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        List<string> descriptions = [];
        foreach (FoldScanColumn column in _scanColumns)
        {
            descriptions.Add(string.Join(", ", column.OutputNames));
        }

        return new OperatorPlanDescription("FoldScan")
        {
            Properties = new Dictionary<string, string>
            {
                ["columns"] = string.Join("; ", descriptions),
            },
            Children = [(Source, null)],
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// FOLD/SCAN is a blocking operator: every input row must be materialised before
    /// any output is emitted, because both the partition assignment and the per-
    /// partition sort depend on seeing all rows. The materialised rows are stabilised
    /// into an operator-owned <see cref="Arena"/> so the input batches can be returned
    /// to the pool immediately instead of being pinned for the operator's lifetime.
    /// </para>
    /// <para>
    /// <strong>Memory bound (Tier 2).</strong> Reports each materialised row's
    /// structural residency to the plan-wide <see cref="MemoryAccountant"/>;
    /// when <see cref="MemoryAccountant.WouldExceedBudget"/> returns true, an
    /// <see cref="ExecutionException"/> is thrown rather than silently OOMing.
    /// Spill-to-disk for the materialised rows + per-partition external merge
    /// sort is on the roadmap (Tier 3 / Tier 4).
    /// </para>
    /// </remarks>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        Pool pool = context.Pool;
        using MaterializedInput input = new(context, "FOLD/SCAN");
        RowBatch? outputBatch = null;

        try
        {
            // ───── Step 1: materialise input into context.Store ─────
            await input.CollectAsync(_source.ExecuteAsync(context)).ConfigureAwait(false);

            if (input.Rows.Count == 0) yield break;

            // Count total output columns across all scan clauses.
            int totalOutputColumns = 0;
            foreach (FoldScanColumn column in _scanColumns)
            {
                totalOutputColumns += column.OutputNames.Count;
            }

            int inputFieldCount = input.Rows[0].FieldCount;
            int totalFieldCount = inputFieldCount + totalOutputColumns;

            // scanResults[rowIndex][flatOutputIndex] — accumulator results indexed by the
            // row's *original* materialised position (not its sorted position within a
            // partition). This is what lets Step 4 emit in original input order with each
            // row joined to its computed scan values.
            DataValue[][] scanResults = new DataValue[input.Rows.Count][];
            for (int i = 0; i < scanResults.Length; i++)
            {
                scanResults[i] = new DataValue[totalOutputColumns];
            }

            // ───── Step 2: group scan columns by window specification ─────
            Dictionary<WindowSpecificationKey, List<int>> specGroups = new();
            for (int columnIndex = 0; columnIndex < _scanColumns.Count; columnIndex++)
            {
                WindowSpecificationKey key = new(_scanColumns[columnIndex].WindowSpecification);
                if (!specGroups.TryGetValue(key, out List<int>? indices))
                {
                    indices = [];
                    specGroups[key] = indices;
                }
                indices.Add(columnIndex);
            }

            // ───── Step 3: per spec, partition + sort + fold ─────
            ExpressionEvaluator evaluator = context.CreateEvaluator();
            foreach (KeyValuePair<WindowSpecificationKey, List<int>> specGroup in specGroups)
            {
                WindowSpecification spec = _scanColumns[specGroup.Value[0]].WindowSpecification;
                await ComputeForSpecificationAsync(
                    spec, specGroup.Value, input.Rows, evaluator, scanResults,
                    context.Store, context.SidecarRegistry, context.CancellationToken).ConfigureAwait(false);
            }

            // ───── Step 4: emit in original input order ─────
            string[] outputNames = new string[totalFieldCount];
            for (int field = 0; field < inputFieldCount; field++)
            {
                outputNames[field] = input.SourceLookup!.ColumnNames[field];
            }
            int outputOffset = 0;
            foreach (FoldScanColumn column in _scanColumns)
            {
                for (int j = 0; j < column.OutputNames.Count; j++)
                {
                    outputNames[inputFieldCount + outputOffset + j] = column.OutputNames[j];
                }
                outputOffset += column.OutputNames.Count;
            }
            ColumnLookup outputLookup = new(outputNames);

            for (int rowIndex = 0; rowIndex < input.Rows.Count; rowIndex++)
            {
                Row sourceRow = input.Rows[rowIndex];

                DataValue[] values = pool.RentDataValues(totalFieldCount);
                for (int field = 0; field < inputFieldCount; field++)
                {
                    values[field] = sourceRow[field];
                }
                for (int j = 0; j < totalOutputColumns; j++)
                {
                    values[inputFieldCount + j] = scanResults[rowIndex][j];
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
            if (outputBatch is not null) context.ReturnRowBatch(outputBatch);
            // MaterializedInput's using-Dispose releases source-row rentals + accountant.
        }
    }

    /// <summary>
    /// Partitions, sorts, and runs the fold for all scan columns that share
    /// the given window specification.
    /// </summary>
    private async ValueTask ComputeForSpecificationAsync(
        WindowSpecification spec,
        List<int> columnIndices,
        List<Row> allRows,
        ExpressionEvaluator evaluator,
        DataValue[][] scanResults,
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
                    store, sidecarRegistry, "SCAN", cancellationToken).ConfigureAwait(false);
            }

            // Fold each scan column group within this partition.
            foreach (int scanColumnIndex in columnIndices)
            {
                FoldScanColumn column = _scanColumns[scanColumnIndex];
                int outputOffset = GetOutputOffset(scanColumnIndex);

                await FoldPartitionAsync(
                    column, allRows, originalIndices, startIndex, count,
                    outputOffset, evaluator, scanResults, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs the fold for a single <see cref="FoldScanColumn"/> over a sorted partition.
    /// </summary>
    private static async ValueTask FoldPartitionAsync(
        FoldScanColumn column,
        List<Row> allRows,
        int[] originalIndices,
        int startIndex,
        int count,
        int outputOffset,
        ExpressionEvaluator evaluator,
        DataValue[][] scanResults,
        CancellationToken cancellationToken)
    {
        int accCount = column.AccumulatorNames.Count;

        // Build augmented row schema: [source_cols..., acc1, acc2, ..., __prev_col1, __prev_col2, ...]
        Row firstRow = allRows[originalIndices[startIndex]];
        int sourceFieldCount = firstRow.FieldCount;

        // Collect PREV column names from the column's PrevColumnNames.
        IReadOnlyList<string> prevColumnNames = column.PrevColumnNames;
        int augmentedFieldCount = sourceFieldCount + accCount + prevColumnNames.Count;

        string[] augmentedNames = new string[augmentedFieldCount];
        for (int i = 0; i < sourceFieldCount; i++)
        {
            augmentedNames[i] = firstRow.ColumnNames[i];
        }
        for (int i = 0; i < accCount; i++)
        {
            augmentedNames[sourceFieldCount + i] = column.AccumulatorNames[i];
        }
        for (int i = 0; i < prevColumnNames.Count; i++)
        {
            augmentedNames[sourceFieldCount + accCount + i] = prevColumnNames[i];
        }

        // Single ColumnLookup for every augmented row in this partition fold —
        // names don't change between rows, only the values do.
        ColumnLookup augmentedLookup = new(augmentedNames);

        // Initialize accumulators from INIT expressions (evaluated against the first row).
        DataValue[] accumulatorValues = new DataValue[accCount];
        for (int i = 0; i < accCount; i++)
        {
            accumulatorValues[i] = await evaluator.EvaluateAsync(column.InitExpressions[i], firstRow, cancellationToken).ConfigureAwait(false);
        }

        DataValue[] augmentedValues = new DataValue[augmentedFieldCount];
        Row? previousSourceRow = null;

        for (int rowOffset = 0; rowOffset < count; rowOffset++)
        {
            int originalRowIndex = originalIndices[startIndex + rowOffset];
            Row sourceRow = allRows[originalRowIndex];

            // Build augmented row values.
            for (int f = 0; f < sourceFieldCount; f++)
            {
                augmentedValues[f] = sourceRow[f];
            }

            // Bind accumulators.
            for (int a = 0; a < accCount; a++)
            {
                augmentedValues[sourceFieldCount + a] = accumulatorValues[a];
            }

            // Bind PREV columns (NULL on first row).
            for (int p = 0; p < prevColumnNames.Count; p++)
            {
                if (previousSourceRow is not null)
                {
                    // Strip the "__prev_" prefix to get the original column name.
                    string originalColumnName = prevColumnNames[p]["__prev_".Length..];
                    augmentedValues[sourceFieldCount + accCount + p] =
                        previousSourceRow.Value.TryGetValue(originalColumnName, out DataValue prevVal)
                            ? prevVal
                            : DataValue.UnknownNull();
                }
                else
                {
                    augmentedValues[sourceFieldCount + accCount + p] = DataValue.UnknownNull();
                }
            }

            Row augmentedRow = new(augmentedLookup, augmentedValues);

            // Evaluate body expressions → new accumulator values.
            for (int b = 0; b < accCount; b++)
            {
                accumulatorValues[b] = await evaluator.EvaluateAsync(column.BodyExpressions[b], augmentedRow, cancellationToken).ConfigureAwait(false);
            }

            // Store results at the original row position.
            for (int o = 0; o < accCount; o++)
            {
                scanResults[originalRowIndex][outputOffset + o] = accumulatorValues[o];
            }

            previousSourceRow = sourceRow;
        }
    }

    /// <summary>
    /// Returns the flat output column offset for the scan column at the given index.
    /// </summary>
    private int GetOutputOffset(int scanColumnIndex)
    {
        int offset = 0;
        for (int i = 0; i < scanColumnIndex; i++)
        {
            offset += _scanColumns[i].OutputNames.Count;
        }
        return offset;
    }

}

/// <summary>
/// Describes a fold/scan column to compute: the accumulator names, body expressions,
/// init expressions, window specification, and output column names.
/// </summary>
/// <param name="AccumulatorNames">The accumulator variable names bound during fold evaluation.</param>
/// <param name="BodyExpressions">The fold body expressions (one per accumulator).</param>
/// <param name="InitExpressions">The seed values for each accumulator at partition start.</param>
/// <param name="WindowSpecification">The OVER clause specification (PARTITION BY, ORDER BY).</param>
/// <param name="OutputNames">The output column names (one per accumulator).</param>
/// <param name="PrevColumnNames">
/// The <c>__prev_</c>-prefixed column names referenced by PREV() calls in the body expressions,
/// populated during the planner rewrite phase.
/// </param>
public sealed record FoldScanColumn(
    IReadOnlyList<string> AccumulatorNames,
    IReadOnlyList<Expression> BodyExpressions,
    IReadOnlyList<Expression> InitExpressions,
    WindowSpecification WindowSpecification,
    IReadOnlyList<string> OutputNames,
    IReadOnlyList<string> PrevColumnNames);
