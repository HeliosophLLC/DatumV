using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Idx;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>open_idx_images(path) → table</c>. Opens an MNIST-style IDX image file
/// and yields one row per item with an auto-generated <c>index</c> column and
/// a PNG-encoded <c>image</c> column. Companion to
/// <see cref="OpenIdxLabelsFunction"/>: SQL recipes for MNIST / Fashion-MNIST
/// call both and join by <c>index</c> to land one combined table per split.
/// </summary>
/// <remarks>
/// <para>
/// Accepts uint8 IDX files with item rank ≥ 2 (height × width [× channels]).
/// Label files (rank-0 ubyte) or float IDX files trip a runtime error — pick
/// the matching TVF or fix the recipe wiring.
/// </para>
/// <para>
/// Transparent <c>.gz</c> support: the path is wrapped in a
/// <see cref="FileFormatDescriptor"/>, so callers pass the raw archive
/// filename (e.g. <c>train-images-idx3-ubyte.gz</c>) and the descriptor
/// decompresses to a temp file on first open. The temp file is cleaned up
/// when the function exits.
/// </para>
/// <para>
/// Streaming: items are decoded into the per-batch arena. Each PNG-encoded
/// image lands as an <see cref="DataKind.Image"/> value via the same
/// <see cref="IdxValueReader"/> path the ingest-time deserializer uses, so
/// the on-disk pixel encoding is identical across TVF and direct ingest.
/// </para>
/// </remarks>
public sealed class OpenIdxImagesFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup OutputColumnLookup = new(["idx", "image"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_idx_images";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens an MNIST-style IDX image file (uint8, rank ≥ 2) and yields one row per " +
        "item: open_idx_images(path). Columns: (index INT64, image Image). Companion to " +
        "open_idx_labels — recipes JOIN the two by index to land (image, label) per row.";

    // Output column is `idx` (not `index`) because `index` is a reserved
    // keyword in the SQL dialect — naming the column `index` would force
    // every recipe to quote it on every reference.
    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
            ],
            FixedOutputSchema: new Schema(
            [
                new ColumnInfo("idx", DataKind.Int64, nullable: false),
                new ColumnInfo("image", DataKind.Image, nullable: false),
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
                "requires 1 argument: open_idx_images(path).");
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
            throw new ArgumentException(
                "open_idx_images requires 1 argument: (path).");
        }

        string path = arguments[0].AsString();

        using FileFormatDescriptor descriptor = new(path);
        await foreach (RowBatch batch in StreamRowsAsync(descriptor, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        FileFormatDescriptor descriptor,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken = context.CancellationToken;
        await using Stream stream = await descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);
        IdxHeader header = IdxHeader.Read(stream);

        if (!header.IsUInt8 || header.ItemDimensionCount < 2)
        {
            throw new InvalidDataException(
                $"open_idx_images: '{descriptor.FilePath}' is not an IDX image file " +
                $"(type code 0x{header.TypeCode:X2}, item rank {header.ItemDimensionCount}). " +
                "Expected uint8 with item rank ≥ 2. For scalar label files use open_idx_labels.");
        }

        int itemByteSize = header.ItemByteSize;
        byte[] itemBuffer = new byte[itemByteSize];
        RowBatch? batch = null;

        for (int rowIndex = 0; rowIndex < header.ItemCount; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IdxHeader.ReadExactly(stream, itemBuffer);

            batch ??= context.RentRowBatch(OutputColumnLookup);
            DataValue image = IdxValueReader.CreateDataValue(header, itemBuffer, batch.Arena);

            DataValue[] values = context.Pool.RentDataValues(2);
            values[0] = DataValue.FromInt64(rowIndex);
            values[1] = image;
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
    }
}
