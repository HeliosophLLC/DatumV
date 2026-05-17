using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Serialization.Cifar;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>open_cifar10(bytes Array&lt;UInt8&gt;) → table</c>. Parses a CIFAR-10
/// binary batch (concatenation of (1-byte label + 3072-byte planar RGB
/// image) records) and yields one row per item with an auto-generated
/// <c>idx</c> column, the PNG-encoded <c>image</c>, and the integer
/// <c>label</c>. Designed to compose with <see cref="OpenArchiveFunction"/>
/// so recipes can read the per-batch <c>.bin</c> files directly out of the
/// upstream tar.gz without an explicit extraction step.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a bytes overload and not a path.</strong> CIFAR ships as a
/// tar.gz containing multiple per-batch <c>.bin</c> files. Path-based TVFs
/// would force the install pipeline to first extract those files to the raw
/// cache; the bytes overload lets a recipe pull them straight out of
/// <c>open_archive($archive)</c> via an (implicit-LATERAL) function source
/// in the <c>FROM</c> clause.
/// </para>
/// <para>
/// <strong>Idx column.</strong> Named <c>idx</c> (not <c>index</c>) because
/// <c>index</c> is a reserved keyword in the SQL dialect. The value is
/// relative to the bytes payload — each call to the TVF restarts the
/// counter at 0. Recipes that UNION ALL across batches keep the per-batch
/// idx by joining on the source-batch label as well.
/// </para>
/// </remarks>
public sealed class OpenCifar10Function : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private const int LabelBytes = 1;

    private static readonly ColumnLookup OutputColumnLookup = new(["idx", "image", "label"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_cifar10";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Parses a CIFAR-10 binary batch (sequence of 1-byte label + 3072-byte planar " +
        "RGB image records) and yields one row per item: open_cifar10(bytes). Columns: " +
        "(idx INT64, image Image, label UInt8). Composes with open_archive to read the " +
        "per-batch .bin files directly from the upstream tar.gz.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("bytes", DataKindMatcher.Exact(DataKind.UInt8), IsArray: ArrayMatch.Array),
            ],
            FixedOutputSchema: new Schema(
            [
                new ColumnInfo("idx", DataKind.Int64, nullable: false),
                new ColumnInfo("image", DataKind.Image, nullable: false),
                new ColumnInfo("label", DataKind.UInt8, nullable: false),
            ])),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        CancellationToken cancellationToken)
    {
        if (argumentKinds.Length != 1)
        {
            throw new FunctionArgumentException(Name,
                "requires 1 argument: open_cifar10(bytes).");
        }
        if (argumentKinds[0] != DataKind.UInt8)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (bytes) must be Array<UInt8> — typically the bytes column of open_archive().");
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
            throw new ArgumentException("open_cifar10 requires 1 argument: (bytes).");
        }
        if (arguments[0].IsNull)
        {
            yield break;
        }

        ReadOnlyMemory<byte> payload = arguments[0].AsBytes();
        await foreach (RowBatch batch in StreamRowsAsync(payload, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        ReadOnlyMemory<byte> payload,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken = context.CancellationToken;
        int recordSize = CifarRecordReader.RecordSize(LabelBytes);
        if (payload.Length % recordSize != 0)
        {
            throw new InvalidDataException(
                $"open_cifar10: payload length {payload.Length} is not a multiple of " +
                $"the CIFAR-10 record size ({recordSize}). Likely not a CIFAR-10 batch file.");
        }

        int recordCount = payload.Length / recordSize;
        RowBatch? batch = null;

        for (int rowIndex = 0; rowIndex < recordCount; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadOnlySpan<byte> record = payload.Span.Slice(rowIndex * recordSize, recordSize);
            byte label = record[0];
            ReadOnlySpan<byte> pixels = record.Slice(LabelBytes);

            batch ??= context.RentRowBatch(OutputColumnLookup);
            DataValue image = CifarRecordReader.EncodeImage(pixels, batch.Arena);

            DataValue[] values = context.Pool.RentDataValues(3);
            values[0] = DataValue.FromInt64(rowIndex);
            values[1] = image;
            values[2] = DataValue.FromUInt8(label);
            batch.Add(values);

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

        await Task.CompletedTask;
    }
}
