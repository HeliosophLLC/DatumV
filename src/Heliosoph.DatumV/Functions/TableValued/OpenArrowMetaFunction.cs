using System.Runtime.CompilerServices;
using Apache.Arrow.Ipc;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Arrow;
using ArrowField = Apache.Arrow.Field;
using ArrowSchema = Apache.Arrow.Schema;
using RecordBatch = Apache.Arrow.RecordBatch;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_arrow_meta(path) → table</c>. Opens an Apache Arrow IPC file
/// (also Feather v2) and yields one row per top-level column with its
/// parsed type metadata. The interrogation TVF for Arrow — read this
/// first to see what's inside an unfamiliar <c>.arrow</c> /
/// <c>.feather</c> file before pulling rows with <c>open_arrow</c>.
/// </summary>
/// <remarks>
/// <para>
/// Arrow IPC is the native interchange format for the
/// <c>datasets</c> library (the Python <c>HF Datasets</c> backend), as
/// well as the on-disk shape for Polars cache, DuckDB exports, and
/// pandas <c>.to_feather()</c> writes. The schema is flat at the
/// top level (Arrow does have nested <see cref="Apache.Arrow.Types.StructType"/>
/// columns, but the dominant HF-dataset shape — text, label, embedding
/// — is flat); v1 surfaces every top-level column with its mapped
/// element kind.
/// </para>
/// </remarks>
public sealed class OpenArrowMetaFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup OutputColumnLookup = new(
    [
        "column_name", "element_kind", "is_array", "is_nullable",
        "is_supported", "logical_type", "batch_count", "total_rows",
    ]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_arrow_meta";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens an Apache Arrow IPC (.arrow / Feather v2) file and yields one row per top-level " +
        "column: open_arrow_meta(path). Columns: (column_name STRING, element_kind STRING, " +
        "is_array BOOLEAN, is_nullable BOOLEAN, is_supported BOOLEAN, logical_type STRING, " +
        "batch_count INT32, total_rows INT64). Read this first to see what's inside an Arrow file.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters: [new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String))],
            FixedOutputSchema: new Schema(
            [
                new ColumnInfo("column_name", DataKind.String, nullable: false),
                new ColumnInfo("element_kind", DataKind.String, nullable: false),
                new ColumnInfo("is_array", DataKind.Boolean, nullable: false),
                new ColumnInfo("is_nullable", DataKind.Boolean, nullable: false),
                new ColumnInfo("is_supported", DataKind.Boolean, nullable: false),
                new ColumnInfo("logical_type", DataKind.String, nullable: false),
                new ColumnInfo("batch_count", DataKind.Int32, nullable: false),
                new ColumnInfo("total_rows", DataKind.Int64, nullable: false),
            ])),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
    {
        if (argumentKinds.Length != 1)
        {
            throw new FunctionArgumentException(Name,
                "requires 1 argument: open_arrow_meta(path).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be STRING.");
        }
        return Signatures[0].FixedOutputSchema!;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException("open_arrow_meta requires 1 argument: (path).");
        }

        string path = arguments[0].AsString();
        await foreach (RowBatch batch in StreamRowsAsync(path, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string path,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken = context.CancellationToken;

        await using Stream stream = File.OpenRead(path);
        using ArrowFileReader reader = new(stream);

        ArrowSchema arrowSchema = reader.Schema;

        // Walk all record batches to compute total rows + batch count.
        // Arrow IPC files typically have a small number of batches —
        // the iteration is cheap. ReadNextRecordBatchAsync materialises
        // each batch, but we dispose immediately after counting; the
        // row-emitting open_arrow TVF re-iterates from a fresh reader.
        int batchCount = 0;
        long totalRows = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using RecordBatch? batch = await reader.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (batch is null) break;
            batchCount++;
            totalRows += batch.Length;
        }

        RowBatch? outputBatch = null;
        foreach (ArrowField field in arrowSchema.FieldsList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ArrowColumnType type = ArrowColumnType.From(field);
            outputBatch ??= context.RentRowBatch(OutputColumnLookup);
            DataValue[] row = BuildRow(field, type, batchCount, totalRows, outputBatch.Arena, context);
            outputBatch.Add(row);

            if (outputBatch.IsFull)
            {
                yield return outputBatch;
                outputBatch = null;
            }
        }

        if (outputBatch is not null && outputBatch.Count > 0)
        {
            yield return outputBatch;
        }
    }

    private static DataValue[] BuildRow(
        ArrowField field,
        ArrowColumnType type,
        int batchCount,
        long totalRows,
        IValueStore arena,
        ExecutionContext context)
    {
        DataValue[] row = context.Pool.RentDataValues(OutputColumnLookup.Count);
        row[0] = DataValue.FromString(field.Name, arena);
        row[1] = DataValue.FromString(
            type.IsSupported ? type.ElementKind.ToString() : "Unknown", arena);
        row[2] = DataValue.FromBoolean(type.IsArray);
        row[3] = DataValue.FromBoolean(type.IsNullable);
        row[4] = DataValue.FromBoolean(type.IsSupported);
        row[5] = DataValue.FromString(type.LogicalTypeName, arena);
        row[6] = DataValue.FromInt32(batchCount);
        row[7] = DataValue.FromInt64(totalRows);
        return row;
    }
}
