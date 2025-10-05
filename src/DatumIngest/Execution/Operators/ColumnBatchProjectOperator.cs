using System.Buffers;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Evaluates SELECT column expressions against each incoming <see cref="ColumnBatch"/>,
/// producing output batches with the projected column set.  Expressions are evaluated
/// column-at-a-time via <see cref="ColumnBatchEvaluator"/> for better cache locality
/// and fewer per-row dispatch calls.
/// </summary>
public sealed class ColumnBatchProjectOperator : IColumnBatchOperator
{
    private readonly IColumnBatchOperator _source;
    private readonly IReadOnlyList<SelectColumn> _columns;

    /// <summary>
    /// Creates a columnar project operator.
    /// </summary>
    /// <param name="source">The child columnar operator producing batches.</param>
    /// <param name="columns">The SELECT columns to project.</param>
    public ColumnBatchProjectOperator(IColumnBatchOperator source, IReadOnlyList<SelectColumn> columns)
    {
        _source = source;
        _columns = columns;
    }

    /// <summary>The child columnar operator.</summary>
    public IColumnBatchOperator Source => _source;

    /// <summary>The projected SELECT columns.</summary>
    public IReadOnlyList<SelectColumn> Columns => _columns;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        List<string> columnDescriptions = [];

        foreach (SelectColumn column in _columns)
        {
            string formatted = QueryExplainer.FormatExpression(column.Expression);
            columnDescriptions.Add(column.Alias is not null
                ? $"{formatted} AS {column.Alias}"
                : formatted);
        }

        return new OperatorPlanDescription("Project")
        {
            Properties = new Dictionary<string, string>
            {
                ["columns"] = string.Join(", ", columnDescriptions),
                ["mode"] = "columnar",
            },
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ColumnBatch> ExecuteColumnBatchAsync(ExecutionContext context)
    {
        // Schema is built lazily from the first batch to resolve star expansions
        // and column name resolution.
        ColumnarProjectionSchema? schema = null;

        await foreach (ColumnBatch inputBatch in _source.ExecuteColumnBatchAsync(context).ConfigureAwait(false))
        {
            schema ??= ColumnarProjectionSchema.Build(_columns, inputBatch);

            ColumnBatch outputBatch = ColumnBatch.Create(schema.OutputNames, inputBatch.RowCount);

            using (ColumnBatchEvaluator evaluator = new(context.FunctionRegistry))
            {
                for (int slot = 0; slot < schema.Slots.Length; slot++)
                {
                    ColumnarProjectionSlot projectionSlot = schema.Slots[slot];
                    DataValue[] sourceColumn;

                    if (projectionSlot.SourceOrdinal >= 0)
                    {
                        // Direct column copy — no expression evaluation needed.
                        sourceColumn = inputBatch.GetColumnBuffer(projectionSlot.SourceOrdinal);
                    }
                    else
                    {
                        // Evaluate expression column-at-a-time.
                        sourceColumn = evaluator.EvaluateColumn(projectionSlot.Expression!, inputBatch);
                    }

                    DataValue[] destColumn = outputBatch.GetColumnBuffer(slot);

                    // Copy values, transferring arena-backed strings to the output arena.
                    CopyColumnWithArenaTransfer(
                        sourceColumn, destColumn, inputBatch, outputBatch, inputBatch.RowCount);
                }
            }

            outputBatch.SetRowCount(inputBatch.RowCount);
            inputBatch.Dispose();
            yield return outputBatch;
        }
    }

    /// <summary>
    /// Copies column values from source to destination, transferring arena-backed
    /// string data into the destination batch's <see cref="Arena"/>.
    /// </summary>
    private static void CopyColumnWithArenaTransfer(
        DataValue[] source,
        DataValue[] destination,
        ColumnBatch sourceBatch,
        ColumnBatch destinationBatch,
        int rowCount)
    {
        // Fast path: if the source and destination batches share the same arena
        // (e.g. both are the same batch), or the source has no arena data, bulk copy.
        bool hasArenaBacked = false;

        for (int row = 0; row < rowCount; row++)
        {
            if (source[row].IsArenaBacked)
            {
                hasArenaBacked = true;
                break;
            }
        }

        if (!hasArenaBacked)
        {
            Array.Copy(source, destination, rowCount);
            return;
        }

        Arena sourceArena = sourceBatch.Arena;
        Arena destinationArena = destinationBatch.Arena;

        for (int row = 0; row < rowCount; row++)
        {
            DataValue value = source[row];

            if (value.IsArenaBacked)
            {
                ReadOnlySpan<byte> utf8Bytes = value.GetArenaStringSpan(sourceArena);
                (int newOffset, int length) = destinationArena.AppendUtf8(utf8Bytes);
                destination[row] = DataValue.FromStringSlice(newOffset, length);
            }
            else
            {
                destination[row] = value;
            }
        }
    }

    /// <summary>
    /// Pre-computed projection layout built once from the first source batch.
    /// </summary>
    private sealed class ColumnarProjectionSchema
    {
        internal string[] OutputNames { get; }
        internal ColumnarProjectionSlot[] Slots { get; }

        private ColumnarProjectionSchema(string[] outputNames, ColumnarProjectionSlot[] slots)
        {
            OutputNames = outputNames;
            Slots = slots;
        }

        internal static ColumnarProjectionSchema Build(
            IReadOnlyList<SelectColumn> columns, ColumnBatch firstBatch)
        {
            List<string> names = [];
            List<ColumnarProjectionSlot> slots = [];

            foreach (SelectColumn column in columns)
            {
                switch (column)
                {
                    case SelectAllColumns allColumns:
                        for (int index = 0; index < firstBatch.ColumnCount; index++)
                        {
                            if (ProjectOperator.IsExcluded(firstBatch.GetColumnName(index), allColumns.ExcludedColumns))
                                continue;
                            Expression? replacement = ProjectOperator.FindReplacement(
                                firstBatch.GetColumnName(index), allColumns.ReplacedColumns);
                            names.Add(firstBatch.GetColumnName(index));
                            slots.Add(replacement is not null
                                ? ColumnarProjectionSlot.Evaluate(replacement)
                                : ColumnarProjectionSlot.CopyOrdinal(index));
                        }
                        break;

                    case SelectTableColumns tableColumns:
                        string prefix = tableColumns.TableName + ".";
                        for (int index = 0; index < firstBatch.ColumnCount; index++)
                        {
                            string columnName = firstBatch.GetColumnName(index);
                            if (columnName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                if (ProjectOperator.IsExcluded(columnName, tableColumns.ExcludedColumns, prefix))
                                    continue;
                                Expression? replacement = ProjectOperator.FindReplacement(
                                    columnName, tableColumns.ReplacedColumns, prefix);
                                string outputName = !tableColumns.QualifyOutput
                                    ? columnName[prefix.Length..]
                                    : columnName;
                                names.Add(outputName);
                                slots.Add(replacement is not null
                                    ? ColumnarProjectionSlot.Evaluate(replacement)
                                    : ColumnarProjectionSlot.CopyOrdinal(index));
                            }
                        }
                        break;

                    default:
                        string name = column.Alias
                            ?? ColumnNameResolver.GetRawName(column.Expression);

                        // Try to resolve as a direct column reference for zero-copy.
                        if (column.Expression is ColumnReference columnReference
                            && firstBatch.TryGetColumnOrdinal(columnReference.ColumnName, out int ordinal))
                        {
                            names.Add(name);
                            slots.Add(ColumnarProjectionSlot.CopyOrdinal(ordinal));
                        }
                        else if (column.Expression is ColumnReference qualifiedReference
                            && qualifiedReference.TableName is not null
                            && firstBatch.TryGetColumnOrdinal(
                                $"{qualifiedReference.TableName}.{qualifiedReference.ColumnName}", out int qualifiedOrdinal))
                        {
                            names.Add(name);
                            slots.Add(ColumnarProjectionSlot.CopyOrdinal(qualifiedOrdinal));
                        }
                        else
                        {
                            names.Add(name);
                            slots.Add(ColumnarProjectionSlot.Evaluate(column.Expression));
                        }
                        break;
                }
            }

            return new ColumnarProjectionSchema(names.ToArray(), slots.ToArray());
        }
    }

    /// <summary>
    /// Describes how to populate a single output column.
    /// </summary>
    private readonly struct ColumnarProjectionSlot
    {
        internal int SourceOrdinal { get; private init; }
        internal Expression? Expression { get; private init; }

        internal static ColumnarProjectionSlot CopyOrdinal(int ordinal) =>
            new() { SourceOrdinal = ordinal };

        internal static ColumnarProjectionSlot Evaluate(Expression expression) =>
            new() { SourceOrdinal = -1, Expression = expression };
    }
}
