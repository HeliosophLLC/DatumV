using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Idx;

/// <summary>
/// Deserializes IDX binary files (MNIST, Fashion-MNIST, etc.) into
/// <see cref="RowBatch"/> streams. Yields one row per item with an
/// auto-generated <c>index</c> column and a data column whose type
/// depends on the IDX header (scalar, vector, matrix, tensor, or image).
/// </summary>
public sealed class IdxDeserializer : IFormatDeserializer
{
    private const int DefaultBatchSize = 1024;

    private readonly FileFormatDescriptor _descriptor;

    /// <summary>Creates a deserializer for the given file descriptor.</summary>
    public IdxDeserializer(FileFormatDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        DeserializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);
        IdxHeader header = IdxHeader.Read(stream);

        IReadOnlyList<string> names = ["index", header.DataColumnName];
        Dictionary<string, int> nameIndex = new(2, StringComparer.OrdinalIgnoreCase)
        {
            ["index"] = 0,
            [header.DataColumnName] = 1,
        };

        IValueStore store = context.Arena;
        int itemByteSize = header.ItemByteSize;
        byte[] itemBuffer = new byte[itemByteSize];

        RowBatch? batch = null;

        for (int rowIndex = 0; rowIndex < header.ItemCount; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IdxHeader.ReadExactly(stream, itemBuffer);

            DataValue[] values = context.Pool.RentDataValues(2);
            values[0] = DataValue.FromFloat32(rowIndex);
            values[1] = IdxValueReader.CreateDataValue(header, itemBuffer, store);

            batch ??= context.Pool.RentBatch(DefaultBatchSize);
            batch.Add(new Row(names, values, nameIndex));

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }
    }
}
