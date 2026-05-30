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
/// <c>open_arrow(path) → table</c>. Opens an Apache Arrow IPC file
/// (also Feather v2) and yields its rows with one column per top-level
/// field. The output schema is the file's real schema — the validator
/// peeks at plan time so projections type-check against the file's
/// actual columns.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Plan-time schema peek.</strong> The <c>path</c> argument
/// must be a constant at plan time (literal in source or a
/// <c>$parameter</c> reference that the parameter binder has
/// substituted). The validator opens the file, reads its
/// <see cref="ArrowSchema"/>, and produces a real
/// <see cref="Schema"/> per top-level column — so
/// <c>SELECT label FROM open_arrow('foo.arrow')</c> type-checks the
/// projection before any data is read.
/// </para>
/// <para>
/// <strong>Supported types (v1).</strong> Booleans, signed and unsigned
/// 8/16/32/64-bit integers, IEEE Float32 / Float64, UTF-8 strings, and
/// 1-D arrays (<see cref="Apache.Arrow.ListArray"/>,
/// <see cref="Apache.Arrow.FixedSizeListArray"/>) of any supported
/// primitive. Dictionary-encoded columns are transparently decoded.
/// Nested <see cref="Apache.Arrow.Types.StructType"/>, multi-level
/// list nesting, decimal/timestamp/date row values, and the rarer
/// Map / Union / Decimal256 types come in a follow-up; columns whose
/// element kind isn't yet wired throw at validation. Use
/// <c>open_arrow_meta</c> to detect them ahead of time.
/// </para>
/// </remarks>
public sealed class OpenArrowFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_arrow";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens an Apache Arrow IPC (.arrow / Feather v2) file and yields its rows with one " +
        "column per top-level field: open_arrow(path). The output schema is the file's real " +
        "schema — the validator peeks at plan time so projections type-check against the actual " +
        "columns. Supports primitive scalar columns and 1-D arrays in v1.";

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
                "requires 1 argument: open_arrow(path).");
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
            throw new FunctionArgumentException(Name, $"Arrow file not found: '{path}'.");
        }

        ArrowSchema arrowSchema = OpenAndReadSchema(path);
        return BuildSchema(arrowSchema);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException("open_arrow requires 1 argument: (path).");
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
        int fieldCount = arrowSchema.FieldsList.Count;

        ArrowColumnType[] columnTypes = new ArrowColumnType[fieldCount];
        string[] columnNames = new string[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            ArrowField field = arrowSchema.FieldsList[i];
            columnTypes[i] = ArrowColumnType.From(field);
            columnNames[i] = field.Name;
            if (!columnTypes[i].IsSupported)
            {
                throw new InvalidDataException(
                    $"Arrow column '{field.Name}' has unsupported type " +
                    $"({columnTypes[i].LogicalTypeName}). Use open_arrow_meta to inspect first.");
            }
        }
        ColumnLookup outputLookup = new(columnNames);

        // Iterate record batches; for each, materialise every column and
        // assemble per-row DataValues. Each record batch is fully decoded
        // before its rows are emitted — typical Arrow IPC files have a
        // small number of batches per shard.
        RowBatch? batch = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using RecordBatch? arrowBatch = await reader.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (arrowBatch is null) break;

            int rowCount = arrowBatch.Length;
            batch ??= context.RentRowBatch(outputLookup);

            DataValue[][] perColumn = new DataValue[fieldCount][];
            for (int c = 0; c < fieldCount; c++)
            {
                perColumn[c] = ArrowColumnReader.ReadAsRows(
                    arrowBatch.Column(c), columnTypes[c], rowCount, batch.Arena);
            }

            for (int r = 0; r < rowCount; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DataValue[] row = context.Pool.RentDataValues(fieldCount);
                for (int c = 0; c < fieldCount; c++)
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

    private static ArrowSchema OpenAndReadSchema(string path)
    {
        using Stream stream = File.OpenRead(path);
        using ArrowFileReader reader = new(stream);
        return reader.Schema;
    }

    private static Schema BuildSchema(ArrowSchema arrowSchema)
    {
        int fieldCount = arrowSchema.FieldsList.Count;
        ColumnInfo[] columns = new ColumnInfo[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            ArrowField field = arrowSchema.FieldsList[i];
            ArrowColumnType type = ArrowColumnType.From(field);
            if (!type.IsSupported)
            {
                throw new FunctionArgumentException(Name,
                    $"Arrow column '{field.Name}' has unsupported type " +
                    $"({type.LogicalTypeName}). " +
                    "Use open_arrow_meta to inspect the file's column types.");
            }

            columns[i] = new ColumnInfo(field.Name, type.ElementKind, nullable: type.IsNullable)
            {
                IsArray = type.IsArray,
            };
        }
        return new Schema(columns);
    }
}
