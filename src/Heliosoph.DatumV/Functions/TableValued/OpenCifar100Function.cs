using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Cifar;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_cifar100(bytes Array&lt;UInt8&gt;) → table</c>. Parses a CIFAR-100
/// binary file (sequence of (1-byte coarse label + 1-byte fine label +
/// 3072-byte planar RGB image) records) and yields one row per item with
/// <c>idx</c>, <c>image</c>, <c>coarse_label</c> (one of 20 superclasses),
/// and <c>fine_label</c> (one of 100 classes). Composes with
/// <see cref="OpenArchiveFunction"/> the same way as
/// <see cref="OpenCifar10Function"/>.
/// </summary>
public sealed class OpenCifar100Function : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private const int LabelBytes = 2;

    private static readonly ColumnLookup OutputColumnLookup =
        new(["idx", "image", "coarse_label", "fine_label"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_cifar100";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Parses a CIFAR-100 binary file (sequence of 1-byte coarse + 1-byte fine + " +
        "3072-byte planar RGB image records) and yields one row per item: " +
        "open_cifar100(bytes). Columns: (idx INT64, image Image, coarse_label UInt8, " +
        "fine_label UInt8). Coarse labels are 20 superclasses; fine labels are 100 classes.";

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
                new ColumnInfo("coarse_label", DataKind.UInt8, nullable: false),
                new ColumnInfo("fine_label", DataKind.UInt8, nullable: false),
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
                "requires 1 argument: open_cifar100(bytes).");
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
            throw new ArgumentException("open_cifar100 requires 1 argument: (bytes).");
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
                $"open_cifar100: payload length {payload.Length} is not a multiple of " +
                $"the CIFAR-100 record size ({recordSize}). Likely not a CIFAR-100 batch file.");
        }

        int recordCount = payload.Length / recordSize;
        RowBatch? batch = null;

        for (int rowIndex = 0; rowIndex < recordCount; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadOnlySpan<byte> record = payload.Span.Slice(rowIndex * recordSize, recordSize);
            byte coarseLabel = record[0];
            byte fineLabel = record[1];
            ReadOnlySpan<byte> pixels = record.Slice(LabelBytes);

            batch ??= context.RentRowBatch(OutputColumnLookup);
            DataValue image = CifarRecordReader.EncodeImage(pixels, batch.Arena);

            DataValue[] values = context.Pool.RentDataValues(4);
            values[0] = DataValue.FromInt64(rowIndex);
            values[1] = image;
            values[2] = DataValue.FromUInt8(coarseLabel);
            values[3] = DataValue.FromUInt8(fineLabel);
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
