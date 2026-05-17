using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Idx;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>open_idx_labels(path) → table</c>. Opens an MNIST-style IDX label file
/// (rank-0 uint8) and yields one row per item with <c>(index, label)</c>
/// columns. Companion to <see cref="OpenIdxImagesFunction"/> — JOIN both on
/// <c>index</c> to materialise (image, label) per row.
/// </summary>
/// <remarks>
/// Accepts uint8 IDX files with item rank 0 (one scalar per item). Image
/// files (rank ≥ 2) or float files trip a runtime error. Transparent <c>.gz</c>
/// support via <see cref="FileFormatDescriptor"/>.
/// </remarks>
public sealed class OpenIdxLabelsFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup OutputColumnLookup = new(["idx", "label"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_idx_labels";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens an MNIST-style IDX label file (uint8, rank 0) and yields one row per item: " +
        "open_idx_labels(path). Columns: (index INT64, label UInt8). Companion to " +
        "open_idx_images — recipes JOIN both by index.";

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
                new ColumnInfo("label", DataKind.UInt8, nullable: false),
            ])),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new FunctionArgumentException(Name,
                "requires 1 argument: open_idx_labels(path).");
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
                "open_idx_labels requires 1 argument: (path).");
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

        if (!header.IsUInt8 || header.ItemDimensionCount != 0)
        {
            throw new InvalidDataException(
                $"open_idx_labels: '{descriptor.FilePath}' is not an IDX label file " +
                $"(type code 0x{header.TypeCode:X2}, item rank {header.ItemDimensionCount}). " +
                "Expected uint8 with item rank 0 (scalar). For image files use open_idx_images.");
        }

        byte[] itemBuffer = new byte[1];
        RowBatch? batch = null;

        for (int rowIndex = 0; rowIndex < header.ItemCount; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IdxHeader.ReadExactly(stream, itemBuffer);

            batch ??= context.RentRowBatch(OutputColumnLookup);
            DataValue[] values = context.Pool.RentDataValues(2);
            values[0] = DataValue.FromInt64(rowIndex);
            values[1] = DataValue.FromUInt8(itemBuffer[0]);
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
