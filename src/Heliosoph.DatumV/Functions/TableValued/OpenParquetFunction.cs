using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Parquet;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_parquet(path) → table</c>. Opens a Parquet file and yields
/// its rows with one column per leaf field. The output schema is the
/// file's real schema — the validator peeks at plan time so column
/// projections type-check against the actual file's typed columns:
/// </summary>
/// <remarks>
/// <para>
/// <strong>Plan-time schema peek.</strong> The <c>path</c> argument must
/// be a constant at plan time (literal in source or a
/// <c>$parameter</c> reference that the parameter binder has substituted).
/// The validator opens the file, reads
/// <see cref="ParquetSchema"/>, and produces a real
/// <see cref="Schema"/> per leaf column — so
/// <c>SELECT text FROM open_parquet('foo.parquet')</c> type-checks
/// the projection before any data is read. Non-constant
/// arguments throw <see cref="FunctionArgumentException"/>.
/// </para>
/// <para>
/// <strong>Supported types (v1).</strong> Booleans, signed and unsigned
/// 8/16/32/64-bit integers, IEEE Float32 / Float64, UTF-8 strings, and
/// byte arrays (raw <c>BYTE_ARRAY</c>). Plus 1-D arrays of any
/// supported primitive (<c>LIST&lt;T&gt;</c>). Nested
/// <c>STRUCT&lt;…&gt;</c>, <c>LIST&lt;LIST&lt;T&gt;&gt;</c>,
/// <c>MAP&lt;K,V&gt;</c>, and rarer logical types (Decimal, Timestamp
/// variants, UUID, JSON) come in a follow-up. Columns with
/// unsupported types throw at validation; recipes can use
/// <c>open_parquet_meta</c> to detect them ahead of time.
/// </para>
/// <para>
/// <strong>Memory.</strong> v1 reads each row group fully into memory
/// before emitting rows. Fine for typical HuggingFace dataset shards
/// (tens to hundreds of MB per row group); larger-than-RAM files will
/// land via the chunked-streaming follow-up that's already on the
/// roadmap.
/// </para>
/// </remarks>
public sealed class OpenParquetFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_parquet";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens a Parquet file and yields its rows with one column per leaf field: " +
        "open_parquet(path). The output schema is the file's real schema — the validator " +
        "peeks at plan time so projections type-check against the actual columns. " +
        "Supports primitive scalar columns and 1-D arrays in v1.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters: [new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String))],
            FixedOutputSchema: null),
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
                "requires 1 argument: open_parquet(path).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be STRING.");
        }
        if (constantArguments[0] is not DataValue pathValue || pathValue.Kind != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be a constant STRING. " +
                "Inline the file path or pass it via a bound $parameter.");
        }

        string path = pathValue.AsString(constantStore);
        if (!File.Exists(path))
        {
            throw new FunctionArgumentException(Name, $"Parquet file not found: '{path}'.");
        }

        DataField[] fields = OpenAndReadFields(path);
        return BuildSchema(fields);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException("open_parquet requires 1 argument: (path).");
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
        ParquetColumnType[] columnTypes = new ParquetColumnType[fields.Length];
        string[] columnNames = new string[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            columnTypes[i] = ParquetColumnType.From(fields[i]);
            columnNames[i] = fields[i].Name;
            if (!columnTypes[i].IsSupported)
            {
                throw new InvalidDataException(
                    $"Parquet column '{fields[i].Name}' has unsupported type " +
                    $"(CLR={fields[i].ClrType.Name}). Use open_parquet_meta to inspect first.");
            }
        }
        ColumnLookup outputLookup = new(columnNames);

        // Iterate row groups; for each, materialise every column and assemble
        // per-row DataValues. v1 reads the whole row group into memory at
        // once; the chunked-streaming refactor is the planned follow-up.
        RowBatch? batch = null;
        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using ParquetRowGroupReader rgReader = reader.OpenRowGroupReader(rg);
            int rowCount = checked((int)rgReader.RowCount);

            batch ??= context.RentRowBatch(outputLookup);

            // Per-column materialisation — DataValue[] per column.
            DataValue[][] perColumn = new DataValue[fields.Length][];
            for (int c = 0; c < fields.Length; c++)
            {
                DataColumn col = await rgReader.ReadColumnAsync(fields[c], cancellationToken).ConfigureAwait(false);
                perColumn[c] = ParquetColumnReader.ReadAsRows(col, columnTypes[c], rowCount, batch.Arena);
            }

            // Row assembly.
            for (int r = 0; r < rowCount; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DataValue[] row = context.Pool.RentDataValues(fields.Length);
                for (int c = 0; c < fields.Length; c++)
                {
                    row[c] = perColumn[c][r];
                }
                batch.Add(row);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                    batch ??= context.RentRowBatch(outputLookup);
                }
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static DataField[] OpenAndReadFields(string path)
    {
        using Stream stream = File.OpenRead(path);
        using ParquetReader reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();
        return reader.Schema.GetDataFields();
    }

    private static Schema BuildSchema(DataField[] fields)
    {
        ColumnInfo[] columns = new ColumnInfo[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            ParquetColumnType type = ParquetColumnType.From(fields[i]);
            if (!type.IsSupported)
            {
                throw new FunctionArgumentException(Name,
                    $"Parquet column '{fields[i].Name}' has unsupported type " +
                    $"(CLR={fields[i].ClrType.Name}). " +
                    "Use open_parquet_meta to inspect the file's column types.");
            }

            columns[i] = new ColumnInfo(fields[i].Name, type.ElementKind, nullable: type.IsNullable)
            {
                IsArray = type.IsArray,
            };
        }
        return new Schema(columns);
    }
}
