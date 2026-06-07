using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Parquet;
using Parquet;
using Parquet.Schema;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_parquet_meta(path) → table</c>. Opens a Parquet file and
/// yields one row per leaf column with its parsed type metadata, row
/// group count, and total row count. The interrogation TVF for
/// Parquet: read this first to see what's inside an unfamiliar file
/// before pulling rows with <c>open_parquet</c>.
/// </summary>
/// <remarks>
/// <para>
/// HuggingFace dataset shards, Spark output, dlt pipelines, pandas
/// `.to_parquet()` files all share the same overall shape: a tree
/// schema with primitive leaf columns plus optional nested groupings
/// (LIST&lt;T&gt; for token sequences / embeddings, STRUCT&lt;…&gt;
/// for image bytes + path bundles, etc.). v1 surfaces the leaf
/// columns with their typed element kind plus the <c>is_supported</c>
/// flag, and exposes the row-group count + total rows so callers can
/// plan around file size before issuing a SELECT.
/// </para>
/// </remarks>
public sealed class OpenParquetMetaFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup OutputColumnLookup = new(
    [
        "column_path", "element_kind", "is_array", "is_nullable",
        "is_supported", "logical_type", "row_group_count", "total_rows",
        "datumv_kind", "datumv_format", "datumv_version",
    ]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_parquet_meta";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens a Parquet file and yields one row per leaf column: open_parquet_meta(path). " +
        "Columns: (column_path STRING, element_kind STRING, is_array BOOLEAN, is_nullable BOOLEAN, " +
        "is_supported BOOLEAN, logical_type STRING, row_group_count INT32, total_rows INT64, " +
        "datumv_kind STRING, datumv_format STRING, datumv_version STRING). The trailing " +
        "datumv_* columns expose the Heliosoph.DatumV typed-kind metadata when the file was produced by " +
        "the engine; NULL otherwise. Read this first to see what's inside an unfamiliar Parquet file.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters: [new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String))],
            FixedOutputSchema: new Schema(
            [
                new ColumnInfo("column_path", DataKind.String, nullable: false),
                new ColumnInfo("element_kind", DataKind.String, nullable: false),
                new ColumnInfo("is_array", DataKind.Boolean, nullable: false),
                new ColumnInfo("is_nullable", DataKind.Boolean, nullable: false),
                new ColumnInfo("is_supported", DataKind.Boolean, nullable: false),
                new ColumnInfo("logical_type", DataKind.String, nullable: false),
                new ColumnInfo("row_group_count", DataKind.Int32, nullable: false),
                new ColumnInfo("total_rows", DataKind.Int64, nullable: false),
                // datumv_* columns are nullable — they're only populated for
                // columns the engine tagged on export, NULL for third-party
                // Parquet (every external producer).
                new ColumnInfo("datumv_kind", DataKind.String, nullable: true),
                new ColumnInfo("datumv_format", DataKind.String, nullable: true),
                new ColumnInfo("datumv_version", DataKind.String, nullable: true),
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
                "requires 1 argument: open_parquet_meta(path).");
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
            throw new ArgumentException("open_parquet_meta requires 1 argument: (path).");
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
        using ParquetReader reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        DataField[] fields = reader.Schema.GetDataFields();
        int rowGroupCount = reader.RowGroupCount;
        long totalRows = 0;
        for (int rg = 0; rg < rowGroupCount; rg++)
        {
            using ParquetRowGroupReader rgReader = reader.OpenRowGroupReader(rg);
            totalRows += rgReader.RowCount;
        }

        // Read per-column datumv.* metadata from the first row group. The
        // writer emits the same map on every row group, so probing the first
        // one is sufficient — same convention open_parquet relies on. Files
        // with zero row groups simply have no datumv metadata, which the
        // null-check below handles.
        Dictionary<string, Dictionary<string, string>?> perColumnMeta = new(StringComparer.Ordinal);
        if (rowGroupCount > 0)
        {
            using ParquetRowGroupReader probe = reader.OpenRowGroupReader(0);
            foreach (DataField field in fields)
            {
                perColumnMeta[field.Name] = probe.GetCustomMetadata(field);
            }
        }

        RowBatch? batch = null;
        foreach (DataField field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ParquetColumnType type = ParquetColumnType.From(field);
            perColumnMeta.TryGetValue(field.Name, out Dictionary<string, string>? meta);
            batch ??= context.RentRowBatch(OutputColumnLookup);
            DataValue[] row = BuildRow(field, type, rowGroupCount, totalRows, meta, batch.Arena, context);
            batch.Add(row);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static DataValue[] BuildRow(
        DataField field,
        ParquetColumnType type,
        int rowGroupCount,
        long totalRows,
        IReadOnlyDictionary<string, string>? datumvMetadata,
        IValueStore arena,
        ExecutionContext context)
    {
        DataValue[] row = context.Pool.RentDataValues(OutputColumnLookup.Count);

        // column_path is the dotted in-file path for nested fields (Parquet.Net
        // exposes the leaf chain via Path); the top-level name for primitives.
        string columnPath = field.Path is { } p ? p.ToString() : field.Name;

        row[0] = DataValue.FromString(columnPath, arena);
        row[1] = DataValue.FromString(
            type.IsSupported ? type.ElementKind.ToString() : "Unknown", arena);
        row[2] = DataValue.FromBoolean(type.IsArray);
        row[3] = DataValue.FromBoolean(type.IsNullable);
        row[4] = DataValue.FromBoolean(type.IsSupported);
        row[5] = DataValue.FromString(type.LogicalTypeName ?? string.Empty, arena);
        row[6] = DataValue.FromInt32(rowGroupCount);
        row[7] = DataValue.FromInt64(totalRows);
        row[8] = MetaStringOrNull(datumvMetadata, ParquetDatumvMetadata.KindKey, arena);
        row[9] = MetaStringOrNull(datumvMetadata, ParquetDatumvMetadata.FormatKey, arena);
        row[10] = MetaStringOrNull(datumvMetadata, ParquetDatumvMetadata.VersionKey, arena);
        return row;
    }

    /// <summary>
    /// Returns the column-metadata value for <paramref name="key"/> as a
    /// <see cref="DataKind.String"/> <see cref="DataValue"/>, or a typed
    /// SQL NULL when the key isn't present. Used for the three trailing
    /// datumv_* output columns so files without Heliosoph.DatumV tagging surface as
    /// <c>NULL</c> rather than empty strings.
    /// </summary>
    private static DataValue MetaStringOrNull(
        IReadOnlyDictionary<string, string>? metadata, string key, IValueStore arena)
    {
        if (metadata is not null && metadata.TryGetValue(key, out string? value))
        {
            return DataValue.FromString(value, arena);
        }
        return DataValue.Null(DataKind.String);
    }
}
